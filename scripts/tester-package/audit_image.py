"""Audit a task-built offline image before it can enter a tester distribution."""
import argparse
import hashlib
import json
from pathlib import Path, PurePosixPath
import tarfile
import posixpath
import re

def audit(image, provenance, support):
    if provenance.get('rootfsLayerSha256')!='69245854ad5855991d16224f6abf55855d2d73922785206adea9c5c5988434a0' or provenance.get('ociManifest')!='sha256:366a6da5efcceffbaa19c560a101b97b87a1a9664eeeac8bc3d24395ddbf14f3':
        raise ValueError('Unreviewed Debian base provenance')
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
    seen_models=set();seen_preparation=set();complete=False;marker=None;count=0;size=0
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
            if name=='var/www/html/ReignServer/runtime/venvs/'+req_sha+'/.complete':complete=True
            if name=='etc/reign-tester-image.json':
                marker=json.load(archive.extractfile(item))
    if not marker or marker.get('schema')!='reign-tester-image-v1' or marker.get('personalDataIncluded') is not False:
        raise ValueError('Clean image marker missing')
    if not complete or seen_models!=set(expected_models) or seen_preparation!=set(expected_preparation) or marker['requirementsSha256']!=req_sha or marker['modelLockSha256']!=model_sha:
        raise ValueError('Dependency image content/lock mismatch')
    with image.open('rb') as stream:
        digest=hashlib.file_digest(stream,'sha256').hexdigest()
    return dict(schema='reign-tester-image-receipt-v1',auditPassed=True,sha256=digest,
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
