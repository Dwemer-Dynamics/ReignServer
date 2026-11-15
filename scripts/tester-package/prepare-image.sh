#!/bin/bash
# Run only in a fresh task-owned Debian 12 image, never an existing installation.
set -euo pipefail
test "$(id -u)" = 0
test "$#" = 2 || { echo 'Expected support directory and dependency archive.' >&2; exit 64; }
support=$(realpath "$1")
dependencies=$(realpath "$2")
test -f "$support/release/embedding-model.lock.json"
test ! -e /var/www/html/ReignServer/data
test ! -e /etc/reign-tester-image.json
. /etc/os-release
test "$ID" = debian && test "$VERSION_ID" = 12
# Maintainer scripts must never start services in the shared WSL network.
printf '#!/bin/sh\nexit 101\n' > /usr/sbin/policy-rc.d
chmod 0755 /usr/sbin/policy-rc.d
mkdir -p /etc/postgresql-common /etc/systemd/system
printf 'create_main_cluster = false\n' > /etc/postgresql-common/createcluster.conf
for unit in postgresql apache2 reignserver; do ln -s /dev/null "/etc/systemd/system/$unit.service"; done
export DEBIAN_FRONTEND=noninteractive
apt-get update -qq
apt-get install -y --no-install-recommends systemd systemd-sysv dbus dbus-user-session libpam-systemd sudo \
    postgresql-15 postgresql-client-15 apache2 python3 python3-venv libicu72 libgssapi-krb5-2 \
    ca-certificates curl acl iproute2 util-linux locales
id reign >/dev/null 2>&1 || useradd --create-home --shell /bin/bash reign
passwd -l reign >/dev/null
root=/var/www/html/ReignServer
install -d -m 0755 "$root/runtime" "$root/scripts" "$root/release" "$root/services/vector-worker"
cp -a "$support/scripts/." "$root/scripts/"
cp -a "$support/release/." "$root/release/"
cp "$support/services/vector-worker/requirements-linux.txt" "$root/services/vector-worker/"
chown -R root:root "$root/scripts" "$root/release" "$root/services"
chmod -R go-w "$root/scripts" "$root/release" "$root/services"
# Only immutable requirement/model roots were exported; no runtime/current or data.
python3 - "$dependencies" <<'PY'
import pathlib,sys,tarfile
with tarfile.open(sys.argv[1]) as archive:
    for item in archive:
        p=pathlib.PurePosixPath(item.name)
        if p.is_absolute() or '..' in p.parts or len(p.parts)<2 or p.parts[0] not in {'venvs','model-versions'}:
            raise ValueError('Unexpected dependency archive member')
        if item.isdev() or item.isfifo() or item.islnk():
            raise ValueError('Unsupported dependency archive entry')
        if item.issym() and item.linkname not in {'python3','/usr/bin/python3','/usr/bin/python3.11'}:
            target=pathlib.PurePosixPath(item.linkname)
            if target.is_absolute() or '..' in target.parts:
                raise ValueError('Escaping dependency link')
PY
tar -xf "$dependencies" -C "$root/runtime"
chown -R root:root "$root/runtime"
chmod -R go-w "$root/runtime"
requirements=$(sha256sum "$root/services/vector-worker/requirements-linux.txt" | cut -d ' ' -f 1)
models=$(sha256sum "$root/release/embedding-model.lock.json" | cut -d ' ' -f 1)
test -f "$root/runtime/venvs/$requirements/.complete"
"$root/runtime/venvs/$requirements/bin/python" -m pip check
python3 - "$root/runtime/model-versions/$models" "$root/release/embedding-model.lock.json" <<'PY'
import hashlib,json,pathlib,sys
root=pathlib.Path(sys.argv[1]); lock=json.loads(pathlib.Path(sys.argv[2]).read_text())
for entry in lock['files']:
    p=root/entry['file']
    if p.is_symlink() or not p.is_file() or p.stat().st_size!=entry['bytes'] or hashlib.file_digest(p.open('rb'),'sha256').hexdigest()!=entry['sha256']:
        raise ValueError('Pinned model mismatch: '+entry['file'])
PY
printf '[boot]\nsystemd=true\n[user]\ndefault=reign\n' > /etc/wsl.conf
printf 'LANG=C.UTF-8\n' > /etc/default/locale
python3 - "$requirements" "$models" <<'PY'
import json,pathlib,subprocess,sys
record={'schema':'reign-tester-image-v1','debian':'12','postgresMajor':15,'requirementsSha256':sys.argv[1],
 'modelLockSha256':sys.argv[2],'personalDataIncluded':False,
 'packages':subprocess.check_output(['dpkg-query','-W','-f=${Package}\t${Version}\n'],text=True).splitlines()}
pathlib.Path('/etc/reign-tester-image.json').write_text(json.dumps(record,indent=2)+'\n')
PY
# Retain package copyright/source notices. Remove only fresh-image transient state.
apt-get clean
find /var/log -xdev -type f -exec truncate -s 0 {} +
rm -f /root/.bash_history /root/.python_history /etc/machine-id /var/lib/dbus/machine-id
touch /etc/machine-id
test -z "$(find /var/lib/postgresql -mindepth 1 -type f -print -quit)"
test ! -e "$root/data"
echo 'Clean offline dependency image prepared; all application services remain masked.'
