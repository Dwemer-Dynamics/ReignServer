#!/bin/sh
# Install reviewed control scripts into the dedicated ReignServer distro.
# Standalone setup: install-reign-control.sh <control> <versions> <service>.
# Deploy-LocalWsl.ps1 uses the recoverable form, under one recovery handle:
#   --begin <id> <control> <versions> <service>  record the prior controls, Apache
#       configuration and PostgreSQL/Apache states, install, configure the proxy and
#       start the support services; any failure restores exactly what was recorded
#   --restore <id>  return every changed path and support service to its prior state
#   --commit <id>   release the handle once the whole deployment verified
set -eu
mode=install id=
case "${1:-}" in
    --begin|--restore|--commit)
        [ "$#" -ge 2 ] || exit 64
        mode=${1#--} id=$2
        shift 2
        case "$id" in ''|*[!A-Za-z0-9-]*) exit 64 ;; esac
        [ "${#id}" -ge 8 ] && [ "${#id}" -le 96 ] || exit 64
        ;;
esac
handle=/var/lib/reign-control/$id
case "$mode" in
    install|begin) [ "$#" -eq 3 ] || { echo 'Expected control, rollback, and service paths.' >&2; exit 64; } ;;
    *) [ "$#" -eq 0 ] || exit 64 ;;
esac

# The fixed staging names and every recorded path are written, checked and
# swapped only while this installer holds the runtime lease (the same lock file
# reignctl uses). reignctl configure-web borrows this descriptor, so it neither
# waits on nor interleaves with the installation.
lease() {
    exec 9>/run/lock/reignserver-runtime.lock
    flock -w 30 9 || { echo 'Another Reign runtime operation is running; service controls were not changed.' >&2; exit 75; }
}

# Exact prior state of every control/config path an installation can change:
# bytes, owner, mode and presence of files, link targets, and the entries of
# Apache's enabled-link and a2enmod/a2enconf/a2ensite marker directories.
recovery() {
    python3 -I - "$@" <<'PY'
import hashlib, json, os, shutil, stat, subprocess, sys
action, handle = sys.argv[1], sys.argv[2]
store = os.path.dirname(handle)
WRITTEN = ('/etc/apache2/ports.conf', '/etc/apache2/conf-available/reign-source.conf', '/etc/apache2/sites-available/reign.conf')
LISTED = tuple('/etc/apache2/%s-enabled' % kind for kind in ('mods', 'conf', 'sites')) + tuple(
    '/var/lib/apache2/%s/%s_by_%s' % (kind, state, who) for kind in ('module', 'conf', 'site') for state in ('enabled', 'disabled') for who in ('admin', 'maint'))
FIXED = WRITTEN + LISTED + ('/usr/local/bin/reignctl', '/usr/local/bin/reign_versions.py', '/etc/systemd/system/reignserver.service',
    '/etc/sudoers.d/reign-control', '/usr/local/lib/reign', '/usr/local/lib/reign/reign-control.py',
    '/usr/local/lib/reign/prepare-systemd-sessions.sh', '/var/lib/apache2/module', '/var/lib/apache2/conf', '/var/lib/apache2/site')
ABSENT = {'type': 'absent'}

def write(path, raw):
    descriptor = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
    with os.fdopen(descriptor, 'wb') as target:
        target.write(raw); target.flush(); os.fsync(target.fileno())

def save(name, value):
    staged = os.path.join(handle, name + '.next')
    if os.path.lexists(staged): os.unlink(staged)
    write(staged, json.dumps(value, sort_keys=True).encode())
    os.replace(staged, os.path.join(handle, name))

def load(name):
    with open(os.path.join(handle, name)) as source: return json.load(source)

def describe(path, keep=False):
    try: info = os.lstat(path)
    except FileNotFoundError: return dict(ABSENT)
    record = {'uid': info.st_uid, 'gid': info.st_gid}
    if stat.S_ISLNK(info.st_mode):
        record.update(type='link', target=os.readlink(path)); return record
    record['mode'] = stat.S_IMODE(info.st_mode)
    if stat.S_ISDIR(info.st_mode):
        record['type'] = 'dir'; return record
    if not stat.S_ISREG(info.st_mode): raise SystemExit('Unsupported file type at ' + path + '.')
    with os.fdopen(os.open(path, os.O_RDONLY | os.O_NOFOLLOW), 'rb') as source: raw = source.read()
    record.update(type='file', sha256=hashlib.sha256(raw).hexdigest())
    blob = os.path.join(handle, 'blobs', record['sha256'])
    if keep and not os.path.exists(blob): write(blob, raw)
    return record

def capture(extra=(), keep=False):
    names = set(FIXED) | set(extra)
    for folder in LISTED:
        if os.path.isdir(folder) and not os.path.islink(folder): names.update(os.path.join(folder, name) for name in os.listdir(folder))
    return {name: describe(name, keep) for name in sorted(names)}

def systemctl(*arguments): return subprocess.run(('systemctl',) + arguments, capture_output=True, text=True)
def active(unit): return systemctl('show', unit, '--property=ActiveState', '--value').stdout.strip()
def clusters():
    rows = systemctl('list-units', '--plain', '--no-legend', '--state=active', 'postgresql@*').stdout
    return sorted(row.split()[0] for row in rows.splitlines() if row.strip())

def release():
    shutil.rmtree(handle)
    try: os.rmdir(store)
    except OSError: pass

def put(path, record):
    if record['type'] == 'dir':
        if os.path.islink(path) or not os.path.isdir(path): os.mkdir(path, 0o700)
        os.chown(path, record['uid'], record['gid'], follow_symlinks=False); os.chmod(path, record['mode'])
        return
    temporary = path + '.reign-restore-' + os.path.basename(handle)
    try:
        if record['type'] == 'link': os.symlink(record['target'], temporary)
        else:
            with open(os.path.join(handle, 'blobs', record['sha256']), 'rb') as source: raw = source.read()
            if hashlib.sha256(raw).hexdigest() != record['sha256']: raise SystemExit('Recovery evidence for ' + path + ' is damaged.')
            write(temporary, raw)
        os.chown(temporary, record['uid'], record['gid'], follow_symlinks=False)
        if record['type'] == 'file': os.chmod(temporary, record['mode'])
        os.replace(temporary, path)
    finally:
        if os.path.lexists(temporary): os.unlink(temporary)

if action == 'begin':
    # configure-web writes through these names, so a redirected one could change an unrecorded file.
    linked = [path for path in WRITTEN + LISTED if os.path.islink(path)]
    if linked: raise SystemExit('Redirected Apache configuration is not supported (' + ', '.join(linked) + '); nothing was changed.')
    if not os.path.lexists(store): os.mkdir(store, 0o700)
    info = os.lstat(store)
    if not stat.S_ISDIR(info.st_mode) or info.st_uid != 0 or info.st_mode & 0o077:
        raise SystemExit(store + ' is not a private root directory; nothing was changed.')
    if os.listdir(store): raise SystemExit('An earlier control installation left recovery evidence in ' + store + ' for review; nothing was changed.')
    services = {unit: active(unit) for unit in ('apache2.service', 'postgresql.service')}
    if any(state not in ('active', 'inactive', 'failed') for state in services.values()):
        raise SystemExit('PostgreSQL or Apache is changing state; retry after it settles. Nothing was changed.')
    os.mkdir(handle, 0o700)
    try:
        os.mkdir(os.path.join(handle, 'blobs'), 0o700)
        save('before.json', {'paths': capture(keep=True), 'services': services, 'clusters': clusters()})
    except BaseException:
        shutil.rmtree(handle); raise
elif action == 'seal':
    save('after.json', {'paths': capture(load('before.json')['paths'])})
elif action == 'restore':
    if os.path.islink(handle) or not os.path.isdir(handle): raise SystemExit('No control recovery handle ' + handle + ' exists.')
    saved = load('before.json'); before = saved['paths']
    try: after = load('after.json')['paths']
    except FileNotFoundError: raise SystemExit('The interrupted control installation recorded no final state; nothing was restored and ' + handle + ' was retained for review.')
    # Undo only this installation's own changes: a path already back at its prior
    # state is skipped (a resumed restore), and any other state belongs to someone else.
    pending, conflicts = [], []
    for path in sorted(set(before) | set(after)):
        if before.get(path, ABSENT) == after.get(path, ABSENT): continue
        now = describe(path)
        if now == after.get(path, ABSENT): pending.append(path)
        elif now != before.get(path, ABSENT): conflicts.append(path)
    if conflicts:
        raise SystemExit('Controls or proxy configuration changed after this installation (' + ', '.join(conflicts) + '); nothing was restored and ' + handle + ' was retained for review.')
    depth = lambda path: path.count('/')
    for path in sorted((path for path in pending if before.get(path, ABSENT)['type'] != 'absent'), key=depth): put(path, before[path])
    for path in sorted((path for path in pending if before.get(path, ABSENT)['type'] == 'absent'), key=depth, reverse=True):
        if os.path.isdir(path) and not os.path.islink(path): os.rmdir(path)
        else: os.unlink(path)
    # Reloads follow this installation's recorded differences, not this attempt's
    # file writes: a resumed restore must still unload the candidate configuration.
    changed = [path for path in set(before) | set(after) if before.get(path, ABSENT) != after.get(path, ABSENT)]
    problems = []
    if any(path.startswith('/etc/systemd/') for path in changed) and systemctl('daemon-reload').returncode:
        problems.append('systemd could not reload the restored service unit.')
    # Only the units themselves are stopped or reloaded; foreign processes and listeners are never signalled.
    was, apache = saved['services'], active('apache2.service')
    if was['apache2.service'] == 'active':
        if any(path.startswith('/etc/apache2/') for path in changed) or apache != 'active':
            if subprocess.run(['apache2ctl', 'configtest'], capture_output=True).returncode:
                problems.append('The restored Apache configuration fails its configuration test; Apache was left as it is.')
            elif systemctl('reload-or-restart', 'apache2.service').returncode:
                problems.append('Apache did not reload its restored configuration.')
    elif apache == 'active' and systemctl('stop', 'apache2.service').returncode:
        problems.append('Apache could not be returned to stopped.')
    added = [unit for unit in clusters() if unit not in saved['clusters']]
    wrapper = was['postgresql.service'] != 'active' and active('postgresql.service') == 'active'
    if (added or wrapper) and active('reignserver.service') == 'active':
        problems.append('PostgreSQL was left running because the managed Reign service is using it.')
    elif added or wrapper:
        for unit in added + (['postgresql.service'] if wrapper and not saved['clusters'] else []):
            if systemctl('stop', unit).returncode: problems.append(unit + ' could not be returned to stopped.')
    if problems: raise SystemExit(' '.join(problems) + ' Restored files stay in place; ' + handle + ' was retained for review.')
    release()
    print('Restored the prior Reign service controls, proxy configuration and support service states.')
elif action == 'commit':
    if os.path.islink(handle) or not os.path.isdir(handle): raise SystemExit('No control recovery handle ' + handle + ' exists.')
    release()
else: raise SystemExit('Unsupported recovery action.')
PY
}

if [ "$mode" = restore ] || [ "$mode" = commit ]; then
    lease
    if [ "$mode" = restore ]; then
        # The installed controls own an unfinished runtime recovery; keep them for it.
        for leftover in current.next current.previous current.aborted activation-pending; do
            if [ -e "/var/www/html/ReignServer/runtime/$leftover" ] || [ -L "/var/www/html/ReignServer/runtime/$leftover" ]; then
                echo "Runtime recovery is still pending ($leftover); the installed controls that own it were kept and $handle was retained for review." >&2
                exit 75
            fi
        done
    fi
    recovery "$mode" "$handle"
    exit 0
fi

source_dir=$(dirname "$1")
# Repair minimal imports that omitted recommended login/session dependencies.
# Finish prerequisites before replacing any installed service controls. The
# reviewed helper runs from a private temporary copy, so no runtime lease is
# held while packages install; it is installed with the other controls below.
prep=$(mktemp)
trap 'rm -f "$prep"' EXIT
sed 's/\r$//' "$source_dir/prepare-systemd-sessions.sh" > "$prep"
sh "$prep"
lease
# Stage and check every control before replacing any of them, so a failed
# update cannot leave a new controller beside an older unit or privilege rule.
staged='/usr/local/bin/reignctl.next /usr/local/bin/reign_versions.py.next /etc/systemd/system/reignserver.service.next /usr/local/lib/reign/reign-control.py.next /usr/local/lib/reign/prepare-systemd-sessions.sh.next /etc/sudoers.d/reign-control.next'
recover() {
    status=$1
    trap - EXIT
    set +e
    rm -f "$prep" $staged
    echo 'Control installation failed; restoring the prior controls, proxy configuration and support services.' >&2
    if recovery seal "$handle" && recovery restore "$handle"; then exit "$status"; fi
    echo "Control recovery did not complete; its evidence was retained in $handle for review." >&2
    exit "$status"
}
if [ "$mode" = begin ]; then
    recovery begin "$handle"
    trap 'recover $?' EXIT
else
    trap 'rm -f "$prep" $staged' EXIT
fi
install -d -o root -g root -m 0755 /usr/local/lib/reign
install -m 0755 "$1" /usr/local/bin/reignctl.next
sed -i 's/\r$//' /usr/local/bin/reignctl.next
bash -n /usr/local/bin/reignctl.next
install -m 0755 "$2" /usr/local/bin/reign_versions.py.next
install -m 0644 "$3" /etc/systemd/system/reignserver.service.next
sed -i 's/\r$//' /etc/systemd/system/reignserver.service.next
install -o root -g root -m 0755 "$source_dir/reign-control.py" /usr/local/lib/reign/reign-control.py.next
sed -i 's/\r$//' /usr/local/lib/reign/reign-control.py.next
python3 -I -c 'import ast,sys; [ast.parse(open(path, "rb").read(), path) for path in sys.argv[1:]]' \
    /usr/local/bin/reign_versions.py.next /usr/local/lib/reign/reign-control.py.next
install -o root -g root -m 0755 "$prep" /usr/local/lib/reign/prepare-systemd-sessions.sh.next
sh -n /usr/local/lib/reign/prepare-systemd-sessions.sh.next
# No shell, arbitrary unit, arguments or general reignctl privilege is granted.
printf '%s\n' 'www-data ALL=(root) NOPASSWD: /usr/bin/systemctl --no-block start reignserver.service, /usr/bin/systemctl --no-block stop reignserver.service' > /etc/sudoers.d/reign-control.next
chmod 0440 /etc/sudoers.d/reign-control.next
visudo -cf /etc/sudoers.d/reign-control.next
mv -f /usr/local/bin/reignctl.next /usr/local/bin/reignctl
mv -f /usr/local/bin/reign_versions.py.next /usr/local/bin/reign_versions.py
mv -f /etc/systemd/system/reignserver.service.next /etc/systemd/system/reignserver.service
mv -f /usr/local/lib/reign/reign-control.py.next /usr/local/lib/reign/reign-control.py
mv -f /usr/local/lib/reign/prepare-systemd-sessions.sh.next /usr/local/lib/reign/prepare-systemd-sessions.sh
mv -f /etc/sudoers.d/reign-control.next /etc/sudoers.d/reign-control
systemctl daemon-reload
# The newly installed configure-web runs inside this installer's lease (fd 9).
/usr/local/bin/reignctl configure-web
if [ "$mode" = begin ]; then
    systemctl start postgresql apache2
    # Record the installed state, so a later restore can tell this installation's
    # changes from anyone else's.
    recovery seal "$handle"
fi
# Standalone installs are not rolled back after this point; Deploy keeps the handle.
trap - EXIT
rm -f "$prep"
