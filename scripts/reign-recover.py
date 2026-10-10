"""Bounded recovery of this installation's processes; never kill an arbitrary port owner."""
import fcntl
import json
import os
from pathlib import Path
import pwd
import re
import signal
import time

RUNTIME = Path('/var/www/html/ReignServer/runtime')


def owned_executable(executable, uid, expected_uid, pid, sid, pgid, runtime=RUNTIME):
    path = Path(str(executable).removesuffix(' (deleted)'))
    if uid != expected_uid or pid != sid or pid != pgid:
        return False
    if path.name not in ('ReignServer', 'ReignBetaServer'):
        return False
    if path.parent == runtime / 'current':
        return True
    return (path.parent.parent == runtime / 'versions'
            and re.fullmatch(r'\d{14}-\d+', path.parent.name) is not None)


def process_stamp(proc):
    # Everything after the final ')' starts at field 3; starttime is field 22.
    return proc.joinpath('stat').read_text().rsplit(')', 1)[1].split()[19]


def is_server_launch(argv):
    args = [value.decode(errors='replace') for value in argv if value]
    if any(value.startswith('--run-') or value.startswith('--list-') for value in args[1:]):
        return False # A non-listening verifier is not an unhealthy installed server.
    ports = [index for index, value in enumerate(args) if value == '--port']
    if not ports:
        return all(value == '--no-browser' for value in args[1:]) # Production default is 5101.
    return len(ports) == 1 and ports[0] + 1 < len(args) and args[ports[0] + 1] == '5101'


def same_live_process(identity, expected_uid, proc_root=Path('/proc')):
    pid, stamp = identity
    proc = proc_root / str(pid)
    try:
        fields = proc.joinpath('stat').read_text().rsplit(')', 1)[1].split()
        return proc.stat().st_uid == expected_uid and fields[19] == stamp and fields[0] not in ('Z', 'X', 'x')
    except (OSError, ValueError, IndexError):
        return False


def send_owned(identity, expected_uid, signum, alive=same_live_process,
               open_pid=os.pidfd_open, send_fd=signal.pidfd_send_signal, close_fd=os.close):
    # Open first, then verify. The descriptor binds the process across PID reuse.
    try:
        descriptor = open_pid(identity[0])
    except ProcessLookupError:
        return
    try:
        if alive(identity, expected_uid):
            try:
                send_fd(descriptor, signum)
            except ProcessLookupError:
                pass
    finally:
        close_fd(descriptor)


def process_snapshot(expected_uid, proc_root=Path('/proc'), runtime=RUNTIME):
    sessions = set()
    rows = []
    workers = set()
    worker_paths = {str(runtime / 'current/services/vector-worker/worker.py'),
                    str(runtime / 'current/VectorWorker/worker.py')}
    for proc in proc_root.iterdir():
        if not proc.name.isdigit():
            continue
        try:
            pid = int(proc.name)
            uid = proc.stat().st_uid
            sid, pgid = os.getsid(pid), os.getpgid(pid)
            stamp = process_stamp(proc)
            executable = os.readlink(proc / 'exe')
            argv = proc.joinpath('cmdline').read_bytes().split(b'\0')
            if (owned_executable(executable, uid, expected_uid, pid, sid, pgid, runtime)
                    and is_server_launch(argv)):
                sessions.add(sid)
            if (uid == expected_uid and len(argv) > 1
                    and argv[1].decode(errors='replace') in worker_paths
                    and Path(executable).name.startswith('python')):
                workers.add(pid)
            rows.append((pid, stamp, uid, sid))
        except (OSError, ValueError, IndexError):
            continue # A process can exit while /proc is being observed.
    return [(pid, stamp) for pid, stamp, uid, sid in rows
            if uid == expected_uid and (sid in sessions or pid in workers)]


def terminate_owned(identities, alive, send, pause=time.sleep, clock=time.monotonic):
    # Identity includes Linux starttime, so PID reuse cannot redirect a signal.
    remaining = lambda: [identity for identity in identities if alive(identity)]
    for identity in remaining():
        send(identity, signal.SIGTERM)
    deadline = clock() + 5
    while remaining() and clock() < deadline:
        pause(.1)
    forced = remaining()
    for identity in forced:
        send(identity, signal.SIGKILL)
    deadline = clock() + 3
    while remaining() and clock() < deadline:
        pause(.1)
    if remaining():
        raise RuntimeError('An owned Reign process did not exit after forced recovery.')
    return {'ownedProcesses': len(identities), 'forcedProcesses': len(forced)}


def recover():
    if os.geteuid() != 0:
        raise PermissionError('Reign recovery must run through the dedicated root lifecycle route.')
    if RUNTIME.is_symlink() or (RUNTIME / 'current').is_symlink():
        raise RuntimeError('Redirected Reign runtime roots cannot be force-recovered.')
    expected_uid = pwd.getpwnam('reign').pw_uid
    # Share the canonical runtime lease. Never remove, replace or bypass its file.
    with open('/run/lock/reignserver-runtime.lock', 'a') as lease:
        deadline = time.monotonic() + 30
        while True:
            try:
                fcntl.flock(lease, fcntl.LOCK_EX | fcntl.LOCK_NB)
                break
            except BlockingIOError:
                if time.monotonic() >= deadline:
                    raise RuntimeError('Another Reign runtime operation still owns the lease.')
                time.sleep(.1)
        identities = process_snapshot(expected_uid)

        alive = lambda identity: same_live_process(identity, expected_uid)
        send = lambda identity, signum: send_owned(identity, expected_uid, signum)
        result = terminate_owned(identities, alive, send)
        result['schema'] = 'reign-owned-recovery-v1'
        print(json.dumps(result))


if __name__ == '__main__':
    recover()
