"""Provider-free contracts. Service calls and health probes are always injected fakes."""
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import tempfile
import time
import unittest
from unittest.mock import patch
from types import SimpleNamespace

SOURCE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location("reign_control", SOURCE / "reign-control.py")
control = importlib.util.module_from_spec(spec)
spec.loader.exec_module(control)
recovery_spec = importlib.util.spec_from_file_location("reign_recovery", SOURCE / "reign-recover.py")
recovery = importlib.util.module_from_spec(recovery_spec)
recovery_spec.loader.exec_module(recovery)


class StartupPrerequisiteTests(unittest.TestCase):
    """Execute production shell functions in disposable Linux paths only."""

    @staticmethod
    def lifecycle_functions():
        return (SOURCE / 'reignctl').read_text().split('case "$command" in\n    configure-web)', 1)[0]

    def run_lifecycle(self, directory, body):
        fixture = Path(directory)
        prefix = self.lifecycle_functions()
        script = prefix + '\nroot="$1/runtime"; data="$1/data"; pidfile="$1/pids/server.pid"\n' + body
        return subprocess.run(['bash', '-c', script, 'fixture', str(fixture)], capture_output=True, text=True, timeout=10)

    def test_root_owned_stale_pid_can_be_replaced_while_server_stays_unprivileged(self):
        self.assertEqual(os.geteuid(), 0, 'PID access contract requires the dedicated root test adapter.')
        with tempfile.TemporaryDirectory(prefix='reign-startup-pid-') as directory:
            fixture = Path(directory)
            fixture.chmod(0o755)
            pids = fixture / 'pids'
            pids.mkdir()
            pidfile = pids / 'server.pid'
            pidfile.write_text('2147483647\n')
            pidfile.chmod(0o644)
            legacy = subprocess.run(['runuser', '-u', 'reign', '--', 'sh', '-c', 'echo $$ > "$1"', 'fixture', str(pidfile)],
                                    capture_output=True, text=True, timeout=5)
            self.assertNotEqual(legacy.returncode, 0)
            self.assertIn('Permission denied', legacy.stderr)
            data = fixture / 'data'
            logs = data / 'logs'
            logs.mkdir(parents=True)
            uid = int(subprocess.check_output(['id', '-u', 'reign'], text=True))
            gid = int(subprocess.check_output(['id', '-g', 'reign'], text=True))
            for path in (data, logs):
                os.chown(path, uid, gid)
                path.chmod(0o700)
            executable = fixture / 'fixture-server'
            executable.write_text('#!/bin/sh\nid -u > "$REIGN_DATA_ROOT/logs/uid"\nprintf done > "$REIGN_DATA_ROOT/logs/complete"\n')
            executable.chmod(0o755)
            result = self.run_lifecycle(directory, 'start_server_process "$1/fixture-server" "$1/python" "$1/models" ""\n')
            self.assertEqual(result.returncode, 0, result.stderr)
            deadline = time.monotonic() + 3
            while not (logs / 'complete').exists() and time.monotonic() < deadline:
                time.sleep(.02)
            self.assertEqual((logs / 'uid').read_text().strip(), str(uid))
            self.assertTrue(pidfile.read_text().strip().isdigit())
            self.assertNotEqual(pidfile.read_text().strip(), '2147483647')
            self.assertEqual(pidfile.stat().st_uid, 0)
            self.assertEqual(pidfile.stat().st_mode & 0o777, 0o644)
            self.assertEqual(pids.stat().st_uid, 0)
            self.assertEqual(pids.stat().st_mode & 0o777, 0o755)
            access = subprocess.run(['runuser', '-u', 'reign', '--', 'test', '-w', str(pidfile)])
            self.assertNotEqual(access.returncode, 0)

    def test_redirected_pid_paths_are_rejected_without_writing_the_target(self):
        with tempfile.TemporaryDirectory(prefix='reign-startup-redirect-') as directory:
            fixture = Path(directory)
            pids = fixture / 'pids'
            pids.mkdir()
            target = fixture / 'preserve'
            target.write_text('preserve')
            (pids / 'server.pid').symlink_to(target)
            result = self.run_lifecycle(directory, 'start_server_process /unused /unused /unused ""\n')
            self.assertNotEqual(result.returncode, 0)
            self.assertEqual(target.read_text(), 'preserve')
            (pids / 'server.pid').unlink()
            pids.rmdir()
            pids.symlink_to(fixture, target_is_directory=True)
            result = self.run_lifecycle(directory, 'start_server_process /unused /unused /unused ""\n')
            self.assertNotEqual(result.returncode, 0)
            self.assertFalse((fixture / 'server.pid').exists())

    def test_failed_child_launch_does_not_leave_a_false_pid_record(self):
        with tempfile.TemporaryDirectory(prefix='reign-startup-failure-') as directory:
            result = self.run_lifecycle(directory, 'runuser() { return 12; }\nstart_server_process /unused /unused /unused ""\n')
            self.assertNotEqual(result.returncode, 0)
            self.assertFalse((Path(directory) / 'pids/server.pid').exists())

    def test_healthy_reuse_preserves_the_pid_and_does_not_launch(self):
        with tempfile.TemporaryDirectory(prefix='reign-startup-reuse-') as directory:
            pidfile = Path(directory) / 'pids/server.pid'
            pidfile.parent.mkdir()
            pidfile.write_text('123\n')
            result = self.run_lifecycle(directory, 'repair_permissions() { return 0; }\nrunning() { return 0; }\nstart_server_process() { exit 99; }\nstart\n')
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertEqual(pidfile.read_text(), '123\n')

    def test_foreign_listener_blocks_launch_and_preserves_stale_record(self):
        with tempfile.TemporaryDirectory(prefix='reign-startup-listener-') as directory:
            fixture = Path(directory)
            executable = fixture / 'runtime/current/ReignServer'
            executable.parent.mkdir(parents=True)
            executable.write_text('#!/bin/sh\nexit 99\n')
            executable.chmod(0o755)
            pidfile = fixture / 'pids/server.pid'
            pidfile.parent.mkdir()
            pidfile.write_text('2147483647\n')
            result = self.run_lifecycle(directory, 'repair_permissions() { return 0; }\nrunning() { return 1; }\nss() { echo LISTEN; }\nstart\n')
            self.assertNotEqual(result.returncode, 0)
            self.assertIn('5101 is already in use', result.stderr)
            self.assertEqual(pidfile.read_text(), '2147483647\n')

    def run_session_preparation(self, installed=True, pam=True, apt_fails=False, remains_missing=False):
        with tempfile.TemporaryDirectory(prefix='reign-session-packages-') as directory:
            fixture = Path(directory)
            if installed:
                (fixture / 'installed').touch()
            (fixture / 'common-session').write_text('session optional pam_systemd.so\n' if pam else '# session optional pam_systemd.so\n')
            stubs = '''
id() { echo 0; }
dpkg-query() { [ -f "$FIXTURE/installed" ] && printf 'install ok installed'; }
apt-get() {
    echo "$*" >> "$FIXTURE/calls"
    [ "$APT_FAILS" = 0 ] || return 23
    if [ "$1" = install ] && [ "$REMAINS_MISSING" = 0 ]; then touch "$FIXTURE/installed"; fi
}
grep() { command grep "$1" "$2" "$FIXTURE/common-session"; }
'''
            env = dict(os.environ, FIXTURE=directory, APT_FAILS=str(int(apt_fails)), REMAINS_MISSING=str(int(remains_missing)))
            result = subprocess.run(['bash', '-c', stubs + (SOURCE / 'prepare-systemd-sessions.sh').read_text()],
                                    env=env, capture_output=True, text=True, timeout=5)
            calls = (fixture / 'calls').read_text().splitlines() if (fixture / 'calls').exists() else []
            return result, calls

    def test_installed_session_prerequisites_do_not_invoke_package_manager(self):
        result, calls = self.run_session_preparation()
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(calls, [])

    def test_missing_session_prerequisites_install_both_explicitly(self):
        result, calls = self.run_session_preparation(installed=False)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(calls, ['update -qq', 'install -y --no-install-recommends libpam-systemd dbus-user-session'])

    def test_package_failure_and_incomplete_installation_are_not_suppressed(self):
        for options in ({'apt_fails': True}, {'remains_missing': True}):
            result, _ = self.run_session_preparation(installed=False, **options)
            self.assertNotEqual(result.returncode, 0)

    def test_disabled_pam_registration_fails_without_rewriting_or_installing(self):
        result, calls = self.run_session_preparation(pam=False)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('active pam_systemd.so', result.stderr)
        self.assertEqual(calls, [])


class ControlTests(unittest.TestCase):
    def setUp(self):
        self.env = {"REMOTE_ADDR": "127.0.0.1", "HTTP_HOST": "127.0.0.1:8089", "HTTP_ORIGIN": "http://127.0.0.1:8089",
                    "HTTP_X_REIGN_CONTROL": "1", "CONTENT_TYPE": "application/json", "REQUEST_METHOD": "POST", "PATH_INFO": "/power"}
        self.active, self.pid, self.ready, self.load, self.failure = "inactive", 0, False, "loaded", False
        self.calls = []

    def command(self, argv, **kwargs):
        self.calls.append(argv)
        if argv == ["/usr/bin/systemctl", "show", "reignserver.service", "--property=LoadState,ActiveState,SubState,MainPID"]:
            return SimpleNamespace(returncode=0, stdout=f"LoadState={self.load}\nActiveState={self.active}\nSubState=running\nMainPID={self.pid}\n")
        assert argv in [["/usr/bin/sudo", "-n", "/usr/bin/systemctl", "--no-block", action, "reignserver.service"] for action in ["start", "stop"]], argv
        return SimpleNamespace(returncode=1 if self.failure else 0, stdout="", stderr="")

    def request(self, payload=None):
        body = json.dumps(payload if payload is not None else {"action": "start"}).encode()
        status, headers, text = control.handle(self.env, body, self.command, lambda: self.ready, {"127.0.0.1"}, SOURCE.parent / "ui")
        self.assertEqual(headers["Cache-Control"], "no-store")
        return status, json.loads(text) if "application/json" in headers["Content-Type"] else text

    def test_start_then_stop_use_exact_managed_unit(self):
        self.assertEqual(self.request(), (202, {"ok": True, "state": "starting"}))
        self.active, self.pid, self.ready = "active", 123, True
        self.assertEqual(self.request({"action": "stop"}), (202, {"ok": True, "state": "stopping"}))
        self.assertEqual(len([x for x in self.calls if x[0].endswith("sudo")]), 2)

    def test_idempotent_start_and_stop_do_not_repeat_mutations(self):
        self.assertEqual(self.request({"action": "stop"})[1]["state"], "off")
        self.active, self.pid, self.ready = "active", 123, True
        self.assertEqual(self.request()[1]["state"], "on")
        self.assertFalse(any(x[0].endswith("sudo") for x in self.calls))

    def test_external_origin_host_peer_and_missing_header_are_rejected(self):
        for key, value in [("HTTP_ORIGIN", "https://outside.invalid"), ("HTTP_ORIGIN", "null"), ("HTTP_HOST", "outside.invalid:8089"),
                           ("REMOTE_ADDR", "192.0.2.1"), ("HTTP_X_REIGN_CONTROL", ""), ("CONTENT_TYPE", "text/plain")]:
            with self.subTest(key=key, value=value):
                old = self.env[key]
                self.env[key] = value
                self.assertEqual(self.request()[0], 403)
                self.env[key] = old
        self.assertEqual(self.calls, [])

    def test_arbitrary_units_commands_and_methods_are_rejected(self):
        for payload in [{"action": "restart"}, {"action": "start; rm"}, {"action": "start", "unit": "other.service"}, {}, [], {"action": []}]:
            with self.subTest(payload=payload):
                self.assertEqual(self.request(payload)[0], 400)
        self.env["REQUEST_METHOD"] = "GET"
        self.assertEqual(self.request()[0], 404)
        self.assertEqual(self.calls, [])

    def test_unmanaged_process_missing_unit_and_failed_command_are_not_success(self):
        self.ready = True
        self.assertEqual(self.request()[0], 503)
        self.ready, self.load = False, "not-found"
        self.assertEqual(self.request()[0], 503)
        self.load, self.failure = "loaded", True
        self.assertEqual(self.request()[0], 503)

    def test_transition_health_and_failure_states(self):
        self.env.update(REQUEST_METHOD="GET", PATH_INFO="/status")
        for active, pid, ready, expected in [("activating", 0, False, "starting"), ("deactivating", 123, False, "stopping"),
                                            ("active", 123, False, "starting"), ("active", 123, True, "on"), ("failed", 0, False, "off")]:
            self.active, self.pid, self.ready = active, pid, ready
            self.assertEqual(self.request()[1]["state"], expected)

    def test_offline_page_contains_same_power_code_without_service_calls(self):
        self.env.update(REQUEST_METHOD="GET", PATH_INFO="/offline")
        status, html = self.request()
        self.assertEqual(status, 200)
        self.assertIn("async function toggleServerPower()", html)
        self.assertNotIn("@SERVER_CONTROL_SCRIPT@", html)
        self.assertEqual(self.calls, [])

    def test_timeout_and_invalid_body_report_failure(self):
        def timeout(*args, **kwargs):
            raise subprocess.TimeoutExpired("fixture", 5)
        self.assertEqual(control.handle(self.env, b'{"action":"start"}', timeout, lambda: False, {"127.0.0.1"})[0], 503)
        for body in [b"", b"{broken", b"x" * 1025]:
            self.assertEqual(control.handle(self.env, body, self.command, lambda: False, {"127.0.0.1"})[0], 400)

    def test_installed_apache_accepts_generated_config_without_starting(self):
        source = (SOURCE / "reignctl").read_text()
        site = source.split("cat > /etc/apache2/sites-available/reign.conf <<'EOF'\n", 1)[1].split("\nEOF", 1)[0]
        self.assertLess(site.index("ProxyPass /reign-control !"), site.index("ProxyPass / http"))
        with tempfile.TemporaryDirectory(prefix="reign-control-config-") as directory:
            root = Path(directory)
            modules = ["mpm_event", "authz_core", "proxy", "proxy_http", "alias", "cgid"]
            config = f'ServerRoot "/etc/apache2"\nServerName localhost\nDefaultRuntimeDir "{root}"\nErrorLog "{root}/error.log"\n'
            config += "\n".join(f"LoadModule {name}_module /usr/lib/apache2/modules/mod_{name}.so" for name in modules)
            config += "\n" + site.replace("${APACHE_LOG_DIR}", str(root)) + "\n"
            target = root / "apache.conf"
            target.write_text(config)
            result = subprocess.run(["/usr/sbin/apache2", "-t", "-f", str(target)], capture_output=True, text=True, timeout=10)
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)


class RecoveryTests(unittest.TestCase):
    def test_only_exact_reign_executable_uid_and_owned_session_are_selected(self):
        root = Path('/fixture/runtime')
        owner = lambda exe, uid=1000, sid=77, pgid=77: recovery.owned_executable(exe, uid, 1000, 77, sid, pgid, root)
        self.assertTrue(owner(root / 'current/ReignServer'))
        self.assertTrue(owner(str(root / 'current/ReignServer') + ' (deleted)'))
        self.assertTrue(owner(root / 'versions/20261003120000-12/ReignServer'))
        for exe in ['/unrelated/ReignServer', root / 'current/other', root / 'versions/not-a-version/ReignServer']:
            self.assertFalse(owner(exe))
        self.assertFalse(owner(root / 'current/ReignServer', uid=0))
        self.assertFalse(owner(root / 'current/ReignServer', sid=1))
        self.assertFalse(owner(root / 'current/ReignServer', pgid=1))
        self.assertTrue(recovery.is_server_launch([b'ReignServer', b'--no-browser']))
        self.assertTrue(recovery.is_server_launch([b'ReignServer', b'--port', b'5101']))
        self.assertFalse(recovery.is_server_launch([b'ReignServer', b'--port', b'5200']))
        self.assertFalse(recovery.is_server_launch([b'ReignServer', b'--run-verification', b'--port', b'5101']))
        self.assertFalse(recovery.is_server_launch([b'ReignServer', b'--list-actions']))
        self.assertFalse(recovery.is_server_launch([b'ReignServer', b'--port']))

    def run_recovery(self, stubborn=False, never_exit=False):
        identity = (77, 'original-start')
        living, calls, clock = {identity}, [], [0.0]
        def send(target, signum):
            calls.append((target, signum))
            if not never_exit and (not stubborn or signum == recovery.signal.SIGKILL):
                living.discard(target)
        def pause(seconds):
            clock[0] += seconds
        result = recovery.terminate_owned([identity], lambda item: item in living, send, pause, lambda: clock[0])
        return result, calls

    def test_graceful_exit_does_not_force_or_touch_another_process(self):
        result, calls = self.run_recovery()
        self.assertEqual(result, {'ownedProcesses': 1, 'forcedProcesses': 0})
        self.assertEqual(calls, [((77, 'original-start'), recovery.signal.SIGTERM)])

    def test_stuck_owned_process_is_force_closed_after_bounded_grace(self):
        result, calls = self.run_recovery(stubborn=True)
        self.assertEqual(result['forcedProcesses'], 1)
        self.assertEqual([item[1] for item in calls], [recovery.signal.SIGTERM, recovery.signal.SIGKILL])

    def test_disappeared_or_reused_pid_is_not_signalled(self):
        calls = []
        result = recovery.terminate_owned([(77, 'old')], lambda item: False, lambda *args: calls.append(args))
        self.assertEqual(calls, [])
        self.assertEqual(result['forcedProcesses'], 0)

    def test_failure_to_exit_remains_failure(self):
        with self.assertRaisesRegex(RuntimeError, 'did not exit'):
            self.run_recovery(stubborn=True, never_exit=True)

    def test_pidfd_is_verified_after_open_and_always_closed(self):
        for matches in [True, False]:
            calls = []
            recovery.send_owned((77, 'original'), 1000, recovery.signal.SIGTERM,
                alive=lambda *args: matches,
                open_pid=lambda pid: calls.append(('open', pid)) or 99,
                send_fd=lambda fd, signum: calls.append(('signal', fd, signum)),
                close_fd=lambda fd: calls.append(('close', fd)))
            expected = [('open', 77)]
            if matches:
                expected.append(('signal', 99, recovery.signal.SIGTERM))
            expected.append(('close', 99))
            self.assertEqual(calls, expected)

    def test_zombies_and_changed_starttime_are_not_live(self):
        with tempfile.TemporaryDirectory(prefix='reign-recovery-proc-') as directory:
            root = Path(directory)
            proc = root / '77'
            proc.mkdir()
            def write(state):
                (proc / 'stat').write_text('77 (fixture) ' + state + ' ' + '0 ' * 18 + '123')
            write('S')
            self.assertTrue(recovery.same_live_process((77, '123'), os.getuid(), root))
            self.assertFalse(recovery.same_live_process((77, 'other'), os.getuid(), root))
            write('Z')
            self.assertFalse(recovery.same_live_process((77, '123'), os.getuid(), root))

    def test_snapshot_preserves_foreign_sessions_and_includes_owned_children_and_orphan_worker(self):
        with tempfile.TemporaryDirectory(prefix='reign-recovery-snapshot-') as directory:
            root = Path(directory)
            runtime, procs = root / 'runtime', root / 'proc'
            procs.mkdir()
            for pid, exe in [(77, str(runtime / 'current/ReignServer')), (78, '/usr/bin/python3'),
                             (88, '/unrelated/ReignServer'), (99, '/usr/bin/python3'),
                             (100, str(runtime / 'current/ReignServer')), (101, str(runtime / 'current/ReignServer'))]:
                proc = procs / str(pid)
                proc.mkdir()
                (proc / 'stat').write_text(f'{pid} (fixture) S ' + '0 ' * 18 + str(pid))
                (proc / 'exe').symlink_to(exe)
                argv = b'fixture\0'
                if pid in (77, 88):
                    argv += b'--port\0' + b'5101\0'
                if pid == 99:
                    argv += str(runtime / 'current/services/vector-worker/worker.py').encode() + b'\0'
                if pid == 100:
                    argv += b'--run-verification\0'
                if pid == 101:
                    argv += b'--port\0' + b'5200\0'
                (proc / 'cmdline').write_bytes(argv)
            with patch.object(recovery.os, 'getsid', side_effect=lambda pid: 77 if pid in (77, 78) else pid), \
                 patch.object(recovery.os, 'getpgid', side_effect=lambda pid: 77 if pid in (77, 78) else pid):
                result = recovery.process_snapshot(os.getuid(), procs, runtime)
            self.assertEqual(set(result), {(77, '77'), (78, '78'), (99, '99')})


if __name__ == "__main__":
    suite = unittest.TestSuite(unittest.defaultTestLoader.loadTestsFromTestCase(case) for case in (ControlTests, RecoveryTests, StartupPrerequisiteTests))
    result = unittest.TextTestRunner(verbosity=2).run(suite)
    print(json.dumps({"schema": "reign-control-contracts-v1", "ok": result.wasSuccessful(), "tests": result.testsRun, "isolated": True}))
    raise SystemExit(0 if result.wasSuccessful() else 1)
