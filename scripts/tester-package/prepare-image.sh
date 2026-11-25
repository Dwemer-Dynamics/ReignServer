#!/bin/bash
# Run only in a fresh task-owned image of the Debian base pinned by release.json
# serverImage, never an existing installation or a shared player distro.
set -euo pipefail
test "$(id -u)" = 0
test "$#" = 2 || { echo 'Expected support directory and dependency archive.' >&2; exit 64; }
support=$(realpath "$1")
dependencies=$(realpath "$2")
test -f "$support/release/embedding-model.lock.json"
test -f "$support/release/release.json"
test ! -e /var/www/html/ReignServer/data
test ! -e /etc/reign-tester-image.json
. /etc/os-release
# Must equal release.json serverImage; verified against it once python3 exists.
debian_version=13 debian_codename=trixie pg_major=17 python_minor=3.13 icu_package=libicu76
test "$ID" = debian && test "$VERSION_ID" = "$debian_version" && test "$VERSION_CODENAME" = "$debian_codename"
test ! -e /etc/postgresql && test ! -e /var/lib/postgresql
# Maintainer scripts must never start services in the shared WSL network.
printf '#!/bin/sh\nexit 101\n' > /usr/sbin/policy-rc.d
chmod 0755 /usr/sbin/policy-rc.d
mkdir -p /etc/postgresql-common /etc/systemd/system
printf 'create_main_cluster = false\n' > /etc/postgresql-common/createcluster.conf
for unit in postgresql apache2 reignserver; do ln -s /dev/null "/etc/systemd/system/$unit.service"; done
export DEBIAN_FRONTEND=noninteractive
apt-get update -qq
apt-get install -y --no-install-recommends systemd systemd-sysv dbus dbus-user-session libpam-systemd sudo \
    "postgresql-$pg_major" "postgresql-client-$pg_major" apache2 python3 python3-venv "$icu_package" libgssapi-krb5-2 \
    ca-certificates curl acl iproute2 util-linux locales
python3 -I - "$support/release/release.json" "$debian_version" "$debian_codename" "$pg_major" "$python_minor" "$icu_package" <<'PY'
import json,pathlib,subprocess,sys
lock=json.loads(pathlib.Path(sys.argv[1]).read_text())['serverImage']
version,codename,major,python,icu=sys.argv[2:]
if (lock['os']['versionId'],lock['os']['codename'],str(lock['postgresMajor']),lock['python'],lock['icuPackage'])!=(version,codename,major,python,icu):
    raise ValueError('prepare-image.sh constants differ from release.json serverImage')
if '%d.%d'%sys.version_info[:2]!=python:raise ValueError('Debian python3 is not the pinned minor version')
if not pathlib.Path('/usr/lib/postgresql/'+major+'/bin/postgres').is_file():raise ValueError('Pinned PostgreSQL server missing')
others=[p.name for p in pathlib.Path('/usr/lib/postgresql').iterdir() if p.name!=major]
if others:raise ValueError('Unexpected PostgreSQL majors installed: '+', '.join(others))
if subprocess.run(['dpkg-query','-W','-f=${Status}',icu],capture_output=True,text=True).stdout!='install ok installed':
    raise ValueError('Pinned ICU package missing')
PY
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
python3 -I - "$dependencies" "$python_minor" <<'PY'
import pathlib,sys,tarfile
with tarfile.open(sys.argv[1]) as archive:
    for item in archive:
        p=pathlib.PurePosixPath(item.name)
        if p.is_absolute() or '..' in p.parts or len(p.parts)<2 or p.parts[0] not in {'venvs','model-versions'}:
            raise ValueError('Unexpected dependency archive member')
        if item.isdev() or item.isfifo() or item.islnk():
            raise ValueError('Unsupported dependency archive entry')
        if item.issym() and item.linkname not in {'python3','/usr/bin/python3','/usr/bin/python'+sys.argv[2]}:
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
venv="$root/runtime/venvs/$requirements"
# A venv keyed only by the requirements hash could come from another Python ABI;
# its site-packages would then be invisible and pip check would pass vacuously.
python3 -I - "$venv" "$python_minor" "$root/services/vector-worker/requirements-linux.txt" <<'PY'
import pathlib,re,sys
venv=pathlib.Path(sys.argv[1]);minor=sys.argv[2]
cfg=dict(line.split(' = ',1) for line in (venv/'pyvenv.cfg').read_text().splitlines() if ' = ' in line)
if not cfg.get('version','').startswith(minor+'.') or cfg.get('home')!='/usr/bin':
    raise ValueError('Dependency venv was not built with the pinned Debian Python')
if sorted(p.name for p in (venv/'lib').iterdir())!=['python'+minor]:
    raise ValueError('Dependency venv contains another Python ABI')
if not any(re.fullmatch(r'[A-Za-z0-9_.-]+==\S+',l.strip()) for l in pathlib.Path(sys.argv[3]).read_text().splitlines()):
    raise ValueError('Empty dependency lock')
PY
"$venv/bin/python" -I - "$root/services/vector-worker/requirements-linux.txt" <<'PY'
import importlib.metadata,pathlib,re,sys
for line in pathlib.Path(sys.argv[1]).read_text().splitlines():
    m=re.fullmatch(r'([A-Za-z0-9_.-]+)==(\S+)',line.strip())
    if m and importlib.metadata.version(m[1])!=m[2]:raise ValueError('Dependency lock mismatch: '+m[1])
    if not m and line.strip() and not line.lstrip().startswith('#'):raise ValueError('Unparsed dependency lock line')
import numpy,onnxruntime,tokenizers,fastembed,qdrant_client  # native extension ABI smoke test
PY
"$venv/bin/python" -m pip check
python3 -I - "$root/runtime/model-versions/$models" "$root/release/embedding-model.lock.json" <<'PY'
import hashlib,json,pathlib,sys
root=pathlib.Path(sys.argv[1]); lock=json.loads(pathlib.Path(sys.argv[2]).read_text())
for entry in lock['files']:
    p=root/entry['file']
    if p.is_symlink() or not p.is_file() or p.stat().st_size!=entry['bytes'] or hashlib.file_digest(p.open('rb'),'sha256').hexdigest()!=entry['sha256']:
        raise ValueError('Pinned model mismatch: '+entry['file'])
PY
printf '[boot]\nsystemd=true\n[user]\ndefault=reign\n' > /etc/wsl.conf
printf 'LANG=C.UTF-8\n' > /etc/default/locale
python3 -I - "$requirements" "$models" "$root/release/release.json" <<'PY'
import json,pathlib,platform,subprocess,sys
lock=json.loads(pathlib.Path(sys.argv[3]).read_text())['serverImage']
record={'schema':lock['schema'],'debian':lock['os']['versionId'],'codename':lock['os']['codename'],
 'postgresMajor':lock['postgresMajor'],'python':platform.python_version(),'icuPackage':lock['icuPackage'],
 'baseManifestDigest':lock['base']['manifestDigest'],'requirementsSha256':sys.argv[1],
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
