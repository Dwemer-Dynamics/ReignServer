#!/bin/bash
# Initialize only the fresh/owned distro selected by the Windows installer.
set -euo pipefail
test "$(id -u)" = 0
test "$#" = 6 || { echo 'Expected package directory, owner ID, windows bridge, mode and input/script hashes.' >&2; exit 64; }
package=$(realpath "$1")
owner=$2
bridge=$(realpath "$3")
mode=$4
inputs_sha=$5
script_sha=$6
test "$(sha256sum "$0" | cut -d ' ' -f 1)" = "$script_sha" || { echo 'Installer script changed before execution.' >&2; exit 1; }
[[ "$mode" = fresh || "$mode" = resume || "$mode" = update ]] || exit 64
[[ "$owner" =~ ^[a-f0-9]{32}$ ]] || exit 64
root=/var/www/html/ReignServer
test -f /etc/reign-tester-image.json
test ! -L "$root" && test ! -L "$root/data" && test ! -L "$root/runtime"
private=$(mktemp -d /var/tmp/reign-tester-inputs.XXXXXXXX)
trap 'case "$private" in /var/tmp/reign-tester-inputs.*) rm -rf -- "$private";; esac' EXIT
python3 - "$package" "$private" "$inputs_sha" <<'PY'
import hashlib,json,pathlib,shutil,sys
source=pathlib.Path(sys.argv[1]);target=pathlib.Path(sys.argv[2]);raw=(source/'install-inputs.json').read_bytes()
if hashlib.sha256(raw).hexdigest()!=sys.argv[3]:raise ValueError('Installer inputs changed')
j=json.loads(raw)
if j.get('schema')!='reign-tester-inputs-v1':raise ValueError('Invalid installation inputs')
expected=set()
for e in j['files']:
    name=e['path'];p=pathlib.PurePosixPath(name)
    if p.is_absolute() or '..' in p.parts or not (name.startswith(('server/','server-support/')) or name=='initialize-server.sh'):
        raise ValueError('Unexpected installation input')
    f=source/name;out=target/name
    if any(parent.is_symlink() for parent in [f,*f.parents] if parent!=source.parent):raise ValueError('Linked installation input')
    if not f.is_file() or f.stat().st_size!=e['bytes']:raise ValueError('Missing installation input')
    out.parent.mkdir(parents=True,exist_ok=True);shutil.copyfile(f,out);out.chmod(0o644)
    if hashlib.file_digest(out.open('rb'),'sha256').hexdigest()!=e['sha256']:raise ValueError('Altered installation input')
    expected.add(name)
actual={p.relative_to(source).as_posix() for p in source.rglob('*') if p.is_file()}
if actual!=expected|{'install-inputs.json'}:raise ValueError('Unexpected staged installation files')
PY
package=$private
python3 - "$root" "$owner" "$mode" <<'PY'
import json,pathlib,sys
root=pathlib.Path(sys.argv[1]); p=root/'tester-owner.json'
if sys.argv[3]=='update' and (not p.exists() or not (root/'data').is_dir()):
    raise ValueError('Owned existing data is required for an update')
if sys.argv[3]=='fresh' and p.exists():
    raise ValueError('Fresh installation cannot adopt an existing owner')
if p.exists() and json.loads(p.read_text()).get('ownerId')!=sys.argv[2]:
    raise ValueError('Distro belongs to another installation')
if not p.exists():
    if (root/'data').exists(): raise ValueError('Unowned existing player data')
    p.write_text(json.dumps({'schema':'reign-tester-owner-v1','ownerId':sys.argv[2]}))
PY
requirements=$(sha256sum "$package/server-support/services/vector-worker/requirements-linux.txt" | cut -d ' ' -f 1)
models=$(sha256sum "$package/server-support/release/embedding-model.lock.json" | cut -d ' ' -f 1)
python3 - "$requirements" "$models" <<'PY'
import json,pathlib,sys
j=json.loads(pathlib.Path('/etc/reign-tester-image.json').read_text())
if j['requirementsSha256']!=sys.argv[1] or j['modelLockSha256']!=sys.argv[2]:
    raise ValueError('Offline image does not match this server dependency lock')
PY
cp -r --no-preserve=all "$package/server-support/scripts/." "$root/scripts/"
cp -r --no-preserve=all "$package/server-support/release/." "$root/release/"
install -d -m 0755 "$root/services/vector-worker"
cp "$package/server-support/services/vector-worker/requirements-linux.txt" "$root/services/vector-worker/"
chown -R root:root "$root/scripts" "$root/release" "$root/services"
chmod -R go-w "$root/scripts" "$root/release" "$root/services"
find "$root/scripts" -maxdepth 1 -type f \( -name '*.sh' -o -name reignctl -o -name '*.service' \) -exec sed -i 's/\r$//' {} +
systemctl unmask postgresql.service apache2.service reignserver.service
if ! test -f /etc/postgresql/15/main/postgresql.conf; then
    test "$mode" != update || { echo 'Existing database cluster is missing; update refused.' >&2; exit 1; }
    # The image intentionally ships no database cluster or user data.
    pg_createcluster 15 main --port=5432 --encoding=UTF8 --locale=C.UTF-8 --start-conf=auto
fi
pg_conftool 15 main set listen_addresses ''
pg_conftool 15 main set port 5432
printf 'auto\n' > /etc/postgresql/15/main/start.conf
if ! test -f /etc/ssl/private/ssl-cert-snakeoil.key; then make-ssl-cert generate-default-snakeoil; fi
pg_ctlcluster 15 main start || pg_ctlcluster 15 main status
if test "$mode" = update; then
    test "$(runuser -u postgres -- psql -XAt -v ON_ERROR_STOP=1 -d postgres -c "SELECT COUNT(*) FROM pg_database WHERE datname='reign';")" = 1 || { echo 'Existing player database is missing; update refused.' >&2; exit 1; }
fi
runuser -u postgres -- psql -X -v ON_ERROR_STOP=1 -d postgres <<'SQL'
SELECT 'CREATE ROLE reign LOGIN' WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname='reign')\gexec
SELECT 'CREATE DATABASE reign OWNER reign ENCODING ''UTF8'' TEMPLATE template0'
WHERE NOT EXISTS (SELECT 1 FROM pg_database WHERE datname='reign')\gexec
SQL
install -d -o reign -g reign -m 0700 "$root/data" "$root/data/logs" "$root/data/campaigns" "$root/data/PortraitCache"
install -o reign -g reign -m 0600 "$bridge" "$root/data/windows-bridge.json"
# This dedicated proxy needs only the Reign port, not Apache's unrelated port80.
a2dissite 000-default default-ssl >/dev/null 2>&1 || true
sed -i '/^[[:space:]]*Listen[[:space:]]\+80[[:space:]]*$/d' /etc/apache2/ports.conf
bash "$root/scripts/install-reign-control.sh" "$root/scripts/reignctl" "$root/scripts/reign_versions.py" "$root/scripts/reignserver.service"
/usr/local/bin/reignctl repair-permissions
systemctl stop reignserver.service
/usr/local/bin/reignctl activate "$package/server" --leave-stopped
pg_ctlcluster 15 main stop
systemctl disable postgresql.service apache2.service reignserver.service >/dev/null
rm -f /usr/sbin/policy-rc.d
echo 'Qualified server installed; player database initialized with local peer authentication; services left stopped.'
