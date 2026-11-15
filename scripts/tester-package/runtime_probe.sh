#!/bin/bash
# Explicit opt-in acceptance in a task-owned clean installer fixture only.
set -euo pipefail
test "$#" = 4 || { echo 'Expected owner ID, isolated save directory, server DLL hash and report path.' >&2; exit 64; }
owner=$1
saves=$2
expected_dll=$3
report=$4
[[ "${WSL_DISTRO_NAME:-}" = ReignTester* ]] || { echo 'A task-named tester distro is required.' >&2; exit 1; }
[[ "$owner" =~ ^[a-f0-9]{32}$ && "$expected_dll" =~ ^[a-f0-9]{64}$ ]] || exit 64
[[ "$report" = "/var/tmp/reign-tester-proof-$owner.json" ]] || exit 64
test ! -e "$report" && test ! -L "$report" || { echo 'Retain the prior proof and choose a fresh fixture.' >&2; exit 1; }
root=/var/www/html/ReignServer
test "$(sha256sum "$root/runtime/current/ReignServer.dll" | cut -d ' ' -f 1)" = "$expected_dll"
python3 - "$root" "$owner" "$saves" <<'PY'
import json,pathlib,sys
root=pathlib.Path(sys.argv[1]);owner=json.loads((root/'tester-owner.json').read_text())
if owner.get('ownerId')!=sys.argv[2]:raise ValueError('Wrong test installation owner')
saved=pathlib.Path(sys.argv[3])
if saved!=saved.resolve() or any(p.is_symlink() for p in (saved,*saved.parents)) or '/.codex-build/tester-distribution/' not in str(saved) or saved.name!='native-saves':
    raise ValueError('A run-owned isolated native-save directory is required')
saved.mkdir(parents=True,exist_ok=True)
if any(saved.iterdir()) or any((root/'data/campaigns').iterdir()):raise ValueError('Clean fixture must have no saves or campaigns')
p=root/'data/windows-bridge.json';bridge=json.loads(p.read_text());bridge['nativeSaveRoot']=str(saved)
next=p.with_suffix('.probe-next');next.write_text(json.dumps(bridge));next.chmod(0o600);next.replace(p)
PY
for unit in reignserver apache2 postgresql; do
    if systemctl is-active --quiet "$unit"; then echo 'Fixture services must start stopped.' >&2; exit 1; fi
done
namespace="reign-tester-${owner:0:12}"
for unit in reignserver apache2 postgresql@15-main; do
    test ! -e "/run/systemd/system/$unit.service.d/tester-isolation.conf"
done
ip netns add "$namespace"
cleanup() {
    result=$?
    systemctl stop reignserver.service apache2 postgresql || true
    for unit in reignserver apache2 postgresql@15-main; do
        rm -f -- "/run/systemd/system/$unit.service.d/tester-isolation.conf"
    done
    systemctl daemon-reload || true
    ip netns del "$namespace" || true
    exit "$result"
}
trap cleanup EXIT
ip -n "$namespace" link set lo up
for unit in reignserver apache2 postgresql@15-main; do
    install -d -m 0755 "/run/systemd/system/$unit.service.d"
    printf '[Service]\nNetworkNamespacePath=/run/netns/%s\n' "$namespace" > "/run/systemd/system/$unit.service.d/tester-isolation.conf"
done
systemctl daemon-reload
test "$(pg_conftool 15 main show listen_addresses)" = "listen_addresses = ''"
test "$(pg_conftool 15 main show port)" = 'port = 5432'
for unit in reignserver apache2 postgresql@15-main; do
    test "$(systemctl show "$unit" -p NetworkNamespacePath --value)" = "/run/netns/$namespace"
done
systemctl start postgresql
test "$(runuser -u postgres -- psql -XAt -d reign -c 'SHOW listen_addresses;')" = ''
requirements=$(sha256sum "$root/services/vector-worker/requirements-linux.txt" | cut -d ' ' -f 1)
"$root/runtime/venvs/$requirements/bin/python" -I -B - "$root/services/vector-worker/requirements-linux.txt" > /var/tmp/reign-tester-dependency-proof.json <<'PY'
import base64,hashlib,importlib.metadata,json,pathlib,re,sys
expected={}
for line in pathlib.Path(sys.argv[1]).read_text().splitlines():
    match=re.fullmatch(r'([A-Za-z0-9_.-]+)==([^\s]+)',line.strip())
    if match:expected[match[1]]=match[2]
    elif line.strip() and not line.lstrip().startswith('#'):raise ValueError('Unparsed dependency lock line')
verified=0;skipped=[]
for name,version in expected.items():
    dist=importlib.metadata.distribution(name)
    if dist.version!=version:raise ValueError('Dependency lock mismatch: '+name)
    entries=dist.files
    if not entries:raise ValueError('Missing dependency RECORD: '+name)
    sources={str(e):e for e in entries if e.hash}
    package_verified=0
    for entry in entries:
        if not entry.hash:continue
        file=dist.locate_file(entry)
        if not file.exists() and len(entry.parts)>1 and entry.parts[-2]=='__pycache__' and entry.suffix=='.pyc':
            source=str(entry.parent.parent/(entry.name.split('.')[0]+'.py'))
            if source not in sources:raise ValueError('Missing cached module without recorded source: '+str(entry))
            skipped.append(str(entry));continue
        if not file.is_file():raise ValueError('Missing installed dependency file: '+str(entry))
        with file.open('rb') as stream:digest=hashlib.file_digest(stream,entry.hash.mode).digest()
        if base64.urlsafe_b64encode(digest).rstrip(b'=').decode()!=entry.hash.value:
            raise ValueError('Installed dependency RECORD changed: '+str(entry))
        verified+=1;package_verified+=1
    if not package_verified:raise ValueError('No hashed dependency files: '+name)
print(json.dumps({'packages':len(expected),'recordFilesVerified':verified,'missingGeneratedCaches':skipped}))
PY
systemctl start reignserver.service apache2
ip netns exec "$namespace" python3 - "$namespace" "$report" "$expected_dll" <<'PY'
import json,pathlib,subprocess,sys,time,urllib.request
namespace,report,dll=sys.argv[1:]
def command(*args):return subprocess.check_output(list(args),text=True,stdin=subprocess.DEVNULL).strip()
def get(url):
    with urllib.request.urlopen(url,timeout=3) as response:return json.load(response)
def ready(timeout=180):
    deadline=time.monotonic()+timeout
    while time.monotonic()<deadline:
        try:
            h=get('http://127.0.0.1:8089/health');w=get('http://127.0.0.1:5102/health');c=get('http://127.0.0.1:8089/reign-control/status')
            pid=int(command('systemctl','show','reignserver.service','-p','MainPID','--value'))
            if (h.get('ok') is True and h.get('databaseName')=='reign'
                and h.get('databaseSchemaUpToDate') is True and h.get('databaseSchemaVersion')==h.get('requiredDatabaseSchemaVersion')
                and h.get('processId')==pid and pid>0 and c.get('state')=='on'
                and w.get('modelLoaded') is True and w.get('dimensions')==384 and not w.get('lastError')):
                return h,w,c,pid
        except (OSError,ValueError,subprocess.CalledProcessError):pass
        time.sleep(1)
    raise TimeoutError('Isolated installed server/worker/control did not become ready')
first,worker,control,first_pid=ready()
if pathlib.Path('/proc/'+str(first_pid)+'/ns/net').stat().st_ino!=pathlib.Path('/run/netns/'+namespace).stat().st_ino:
    raise ValueError('Server escaped isolated network namespace')
links=json.loads(command('ip','-j','link'))
if [link['ifname'] for link in links]!=['lo']:raise ValueError('Unexpected external network interface')
def power(action):
    request=urllib.request.Request('http://127.0.0.1:8089/reign-control/power',
        data=json.dumps({'action':action}).encode(),method='POST',
        headers={'Content-Type':'application/json','Origin':'http://127.0.0.1:8089','X-Reign-Control':'1'})
    with urllib.request.urlopen(request,timeout=5) as response:return json.load(response)
power('stop');deadline=time.monotonic()+60
while time.monotonic()<deadline:
    listeners=command('ss','-ltnH','( sport = :5101 or sport = :5102 )')
    if not listeners and get('http://127.0.0.1:8089/reign-control/status').get('state')=='off':break
    time.sleep(1)
else:raise TimeoutError('Managed stop did not drain both server and worker')
power('start');second,worker,control,second_pid=ready()
if first_pid==second_pid:raise ValueError('Managed restart did not create a new server process')
bridge=json.loads(pathlib.Path('/var/www/html/ReignServer/data/windows-bridge.json').read_text())
if any(pathlib.Path(bridge['nativeSaveRoot']).iterdir()) or any(pathlib.Path('/var/www/html/ReignServer/data/campaigns').iterdir()):
    raise ValueError('Fixture created unexpected saves or campaigns')
result={'schema':'reign-tester-runtime-proof-v1','ok':True,'serverDllSha256':dll,
    'firstManagedPid':first_pid,'restartedManagedPid':second_pid,'databaseName':'reign',
    'databaseSchemaVersion':second['databaseSchemaVersion'],'modelLoaded':True,'dimensions':384,
    'controlState':control['state'],'bothListenersStopped':True,'networkNamespace':namespace,
    'dependencies':json.loads(pathlib.Path('/var/tmp/reign-tester-dependency-proof.json').read_text()),
    'nativeGameLaunched':False,'nativeSavesUsed':False,'providerCalls':0}
pathlib.Path(report).write_text(json.dumps(result,indent=2)+'\n')
print(json.dumps(result))
PY
