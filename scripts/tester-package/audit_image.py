"""Audit a task-built offline image before it can enter a tester distribution."""
import argparse
import hashlib
import json
from pathlib import Path, PurePosixPath
import tarfile
import posixpath
import re

def image_lock(support):
    """The reviewed base image and OS/PostgreSQL/Python identity, single-sourced in release.json."""
    lock=json.loads((support/'release/release.json').read_text(encoding='utf-8'))['serverImage']
    if lock.get('schema')!='reign-tester-image-v2':raise ValueError('Unsupported server image lock')
    return lock

def check_provenance(provenance, lock):
    base=lock['base']
    expected={'ociIndex':base['indexDigest'],'ociManifest':base['manifestDigest'],
              'rootfsLayerSha256':base['rootfsLayerDigest'].removeprefix('sha256:'),'rootfsLayerBytes':base['rootfsLayerBytes'],
              'reference':base['reference'],'platform':base['platform']}
    if any(provenance.get(key)!=value for key,value in expected.items()):
        raise ValueError('Unreviewed Debian base provenance')

def check_marker(marker, lock, req_sha, model_sha):
    if not marker or marker.get('schema')!=lock['schema'] or marker.get('personalDataIncluded') is not False:
        raise ValueError('Clean image marker missing or from an older image')
    if (marker.get('debian'),marker.get('codename'),marker.get('postgresMajor'),marker.get('icuPackage'),marker.get('baseManifestDigest'))!=(
        lock['os']['versionId'],lock['os']['codename'],lock['postgresMajor'],lock['icuPackage'],lock['base']['manifestDigest']) \
        or not str(marker.get('python','')).startswith(lock['python']+'.'):
        raise ValueError('Image OS/PostgreSQL/Python identity differs from release.json serverImage')
    if marker.get('requirementsSha256')!=req_sha or marker.get('modelLockSha256')!=model_sha:
        raise ValueError('Dependency image content/lock mismatch')
    packages=dict(row.split('\t',1) for row in marker.get('packages',[]) if '\t' in row)
    major=str(lock['postgresMajor'])
    for name in ('postgresql-'+major,'postgresql-client-'+major,lock['icuPackage'],'python3','python3-venv'):
        if name not in packages:raise ValueError('Pinned image package missing: '+name)
    others=[n for n in packages if n.startswith(('postgresql-1','postgresql-client-1','postgresql-9')) and n.rsplit('-',1)[-1]!=major]
    if others:raise ValueError('Unexpected PostgreSQL major in image: '+', '.join(sorted(others)))

def check_venv(config, abis, lock):
    # The venv path is keyed only by the requirements hash; bind it to the image's Python ABI too.
    cfg=dict(line.split(' = ',1) for line in (config or '').splitlines() if ' = ' in line)
    if not cfg.get('version','').startswith(lock['python']+'.') or abis!={'python'+lock['python']}:
        raise ValueError('Dependency venv Python ABI differs from the image Python')

def audit(image, provenance, support):
    lock=image_lock(support)
    check_provenance(provenance,lock)
    requirements=(support/'services/vector-worker/requirements-linux.txt').read_bytes()
    model_raw=(support/'release/embedding-model.lock.json').read_bytes()
    req_sha=hashlib.sha256(requirements).hexdigest();model_sha=hashlib.sha256(model_raw).hexdigest()
    model_lock=json.loads(model_raw)
    expected_models={e['file']:e['sha256'] for e in model_lock['files']}
    preparation=(support/'scripts/Prepare-MemorySelection.py').read_text()
    for name,key in [('qwen-reranker-cpu.onnx','CPU_SHA256'),('qwen-reranker-binary.onnx','TARGET_SHA256')]:
        expected_models[name]=re.search(key+r' = "([a-f0-9]{64})"',preparation).group(1)
    expected_preparation={name:hashlib.sha256((support/'scripts'/name).read_bytes()).hexdigest()
                          for name in ('Prepare-MemorySelection.py','Prepare-LinuxRuntime.py')}
    seen_models=set();seen_preparation=set();complete=False;marker=None;count=0;size=0;venv_cfg=None;venv_abis=set()
    with tarfile.open(image,'r|*') as archive:
        for item in archive:
            name=item.name.removeprefix('./').rstrip('/')
            path=PurePosixPath(name)
            if path.is_absolute() or '..' in path.parts:
                raise ValueError('Escaping image member')
            forbidden=('var/www/html/ReignServer/data','etc/reignserver','etc/ssl/private/')
            if name==forbidden[0] or name==forbidden[1] or name.startswith(('var/www/html/ReignServer/data/','var/lib/postgresql/','etc/reignserver/','etc/ssl/private/','etc/ssh/ssh_host_')):
                raise ValueError('Private state directory/member in clean image: '+name)
            if item.issym() or item.islnk():
                target=posixpath.normpath(item.linkname.lstrip('/') if item.linkname.startswith('/') else posixpath.join(posixpath.dirname(name),item.linkname))
                if target==forbidden[0] or target.startswith(('var/www/html/ReignServer/data/','var/lib/postgresql/','etc/ssl/private/','etc/reignserver/')):
                    raise ValueError('Link to private state in clean image')
            if name.startswith('var/www/html/ReignServer/') and (item.uid!=0 or item.gid!=0 or (not item.issym() and item.mode&0o022)):
                raise ValueError('Service-writable supposedly immutable content')
            if not item.isfile():
                continue
            count+=1;size+=item.size
            if name.startswith(('var/www/html/ReignServer/data/','var/lib/postgresql/','etc/ssl/private/',
                'root/.ssh/','home/reign/.ssh/','etc/reignserver/')):
                raise ValueError('Private database/credential/state in clean image: '+name)
            if name in ('root/.bash_history','root/.python_history','home/reign/.bash_history','var/lib/systemd/random-seed'):
                raise ValueError('Machine/user history in clean image')
            if name.startswith('var/log/') and item.size:
                raise ValueError('Nonempty image log: '+name)
            if name in ('etc/machine-id','var/lib/dbus/machine-id') and item.size:
                raise ValueError('Machine identity in clean image')
            prefix='var/www/html/ReignServer/runtime/'
            if name.startswith(prefix) and not name.removeprefix(prefix).startswith(('venvs/','model-versions/')):
                raise ValueError('Unexpected runtime payload in dependency image')
            if name=='etc/shadow':
                rows=archive.extractfile(item).read().decode().splitlines()
                if any(row.split(':')[1] not in ('!','!!','*','!*') for row in rows):
                    raise ValueError('Unlocked/password-bearing account in clean image')
            if name=='etc/hostname' and archive.extractfile(item).read()!=b'reignserver\n':
                raise ValueError('Build-machine hostname in image')
            if name=='etc/hosts' and archive.extractfile(item).read()!=b'127.0.0.1 localhost\n::1 localhost ip6-localhost ip6-loopback\n':
                raise ValueError('Build-machine hosts in image')
            model_prefix='var/www/html/ReignServer/runtime/model-versions/'+model_sha+'/'
            if name.startswith(model_prefix) and name.removeprefix(model_prefix) in expected_models:
                short=name.removeprefix(model_prefix)
                if hashlib.file_digest(archive.extractfile(item),'sha256').hexdigest()!=expected_models[short]:raise ValueError('Model/derived graph changed: '+short)
                seen_models.add(short)
            script_prefix='var/www/html/ReignServer/scripts/'
            if name.startswith(script_prefix) and name.removeprefix(script_prefix) in expected_preparation:
                short=name.removeprefix(script_prefix)
                if hashlib.file_digest(archive.extractfile(item),'sha256').hexdigest()!=expected_preparation[short]:raise ValueError('Preparation source changed')
                seen_preparation.add(short)
            venv_prefix='var/www/html/ReignServer/runtime/venvs/'+req_sha+'/'
            if name==venv_prefix+'.complete':complete=True
            if name==venv_prefix+'pyvenv.cfg':venv_cfg=archive.extractfile(item).read().decode()
            if name.startswith(venv_prefix+'lib/'):venv_abis.add(name.removeprefix(venv_prefix+'lib/').split('/',1)[0])
            if name=='etc/reign-tester-image.json':
                marker=json.load(archive.extractfile(item))
    check_marker(marker,lock,req_sha,model_sha)
    if not complete or seen_models!=set(expected_models) or seen_preparation!=set(expected_preparation):
        raise ValueError('Dependency image content/lock mismatch')
    check_venv(venv_cfg,venv_abis,lock)
    with image.open('rb') as stream:
        digest=hashlib.file_digest(stream,'sha256').hexdigest()
    return dict(schema='reign-tester-image-receipt-v2',auditPassed=True,sha256=digest,
        bytes=image.stat().st_size,expandedFileBytes=size,fileCount=count,
        requirementsSha256=marker['requirementsSha256'],modelLockSha256=marker['modelLockSha256'],
        preparationSha256=expected_preparation,verifiedModelCount=len(seen_models),
        upstream=provenance,imageIdentity=marker,
        exclusions=['player data','PostgreSQL cluster','private TLS keys','global API credentials',
                    'shell/Python history','machine ID','nonempty logs','application runtime/current'])

if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--image',required=True,type=Path)
    p.add_argument('--base-provenance',required=True,type=Path)
    p.add_argument('--output',required=True,type=Path)
    p.add_argument('--support-root',required=True,type=Path)
    a=p.parse_args()
    result=audit(a.image,json.loads(a.base_provenance.read_text()),a.support_root)
    a.output.write_text(json.dumps(result,indent=2)+'\n',encoding='utf-8')
    print(json.dumps({k:v for k,v in result.items() if k not in ('imageIdentity','upstream')},indent=2))
