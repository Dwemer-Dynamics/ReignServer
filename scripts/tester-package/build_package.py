"""Assemble one ZIP64 tester distribution from canonical artifacts; never build or deploy."""
from __future__ import annotations
import argparse
import hashlib
import json
from pathlib import Path, PurePosixPath
import re
import shutil
import subprocess
import zipfile
import unicodedata
import os
from html.parser import HTMLParser

SCHEMA = 'reign-tester-package-v1'
CONTENT = ('GUI/', 'ModuleData/', 'EventArt/', 'TavernArt/', 'Videos/',
           'PortraitCache/_shared/', 'TavernHousePortraits/')
SUPPORT = ('reignctl', 'reign_versions.py', 'reignserver.service', 'install-reign-control.sh',
           'prepare-systemd-sessions.sh', 'reign-control.py', 'reign-recover.py',
           'Start-ReignServer-Wsl.ps1', 'Prepare-LinuxRuntime.py', 'Prepare-MemorySelection.py')
CLIENT_DLLS={'ArenaOverhaul.1.4.6.dll','JpegLibrary.dll','Reign.Core.Contracts.dll','ReignBeta.dll',
             'System.Buffers.dll','System.Memory.dll','System.Runtime.CompilerServices.Unsafe.dll'}
HELPER_FILES={'Bannerlord.NativeCharacterImageGenerator.App.exe','Bannerlord.NativeCharacterImageGenerator.runtimeconfig.json',
              'Bannerlord.NativePortraitRenderHost.exe','Bannerlord.NativePortraitRenderHost.exe.config'}

def sha(path):
    with Path(path).open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()

def read(path):
    return json.loads(Path(path).read_text(encoding='utf-8-sig'))

def safe_name(name):
    p = PurePosixPath(name)
    if not name or p.is_absolute() or re.search(r'[\\:<>"|?*\x00-\x1f]',name) or len(name)>220 or unicodedata.normalize('NFC',name)!=name or any(x in ('', '.', '..') for x in name.split('/')):
        raise ValueError('Unsafe package path: ' + name)
    if any(x.rstrip(' .') != x or re.fullmatch(r'(?i)(con|prn|aux|nul|com[1-9]|lpt[1-9])(?:\..*)?', x) for x in p.parts):
        raise ValueError('Unsupported Windows path: ' + name)
    return name

def check_entries(entries):
    seen = set()
    for entry in entries:
        name = safe_name(entry['path'])
        if name.casefold() in seen:
            raise ValueError('Case-colliding package entries')
        seen.add(name.casefold())
        if entry['bytes'] < 0 or not re.fullmatch('[a-f0-9]{64}', entry['sha256']):
            raise ValueError('Invalid file evidence')

def verify_tree(root, manifest):
    root = Path(root)
    if manifest.get('schema') != SCHEMA or not manifest.get('files'):
        raise ValueError('Unsupported or empty tester package')
    check_entries(manifest['files'])
    expected = {e['path']: e for e in manifest['files']}
    actual = {p.relative_to(root).as_posix() for p in root.rglob('*') if p.is_file()}
    if actual != set(expected) | {'package-manifest.json'}:
        raise ValueError('Package inventory mismatch')
    for p in root.rglob('*'):
        if p.is_symlink():
            raise ValueError('Package links are not permitted')
    for name, entry in expected.items():
        p = root / name
        if p.stat().st_size != entry['bytes'] or sha(p) != entry['sha256']:
            raise ValueError('Package file changed: ' + name)
    return True

def inventory_check(root, inventory):
    expected = {safe_name(e['path']): e for e in inventory['files']}
    actual = {p.relative_to(root).as_posix() for p in root.rglob('*') if p.is_file()}
    if actual != set(expected):
        raise ValueError('Shipped content inventory differs')
    for name, e in expected.items():
        p = root / name
        if p.is_symlink() or p.stat().st_size != e['bytes'] or sha(p) != e['sha256']:
            raise ValueError('Unhydrated/changed shipped content: ' + name)

class Assembler:
    def __init__(self, root):
        self.root = root
        self.names = set()
    def copy(self, source, name, digest=None):
        name = safe_name(name)
        if name.casefold() in self.names or source.is_symlink() or not source.is_file():
            raise ValueError('Duplicate, linked or missing payload: ' + name)
        if source.name.startswith('TaleWorlds.') or source.suffix.lower() in ('.sav', '.pfx', '.pem', '.key'):
            raise ValueError('Prohibited payload: ' + name)
        if digest and sha(source) != digest:
            raise ValueError('Qualified artifact mismatch: ' + name)
        destination = self.root / name
        destination.parent.mkdir(parents=True, exist_ok=True)
        with source.open('rb') as stream:
            if stream.read(100).startswith(b'version https://git-lfs.github.com/spec/v1'):
                raise ValueError('Unhydrated source payload: '+name)
        if not digest and (name.endswith(('.sh','.service')) or name.endswith('/reignctl')):
            destination.write_bytes(source.read_bytes().replace(b'\r\n',b'\n'))
        else:
            shutil.copy2(source, destination)
        if digest and sha(destination) != digest:
            raise ValueError('Copied artifact mismatch')
        self.names.add(name.casefold())

def git(root, *args):
    return subprocess.check_output(['git', '-c', 'core.quotepath=false', *args], cwd=root).decode().strip()

def verify_guide(path):
    class Guide(HTMLParser):
        def handle_starttag(self,tag,attrs):
            for key,value in attrs:
                if not value:continue
                if key=='src' and not value.startswith(('data:','blob:','#')):raise ValueError('Manual has a nonembedded dependency')
                if key=='href' and not value.startswith(('#','data:','blob:','https:','http:')):raise ValueError('Manual requires a missing companion asset')
    text=path.read_text(encoding='utf-8-sig')
    if '<html' not in text.lower():raise ValueError('HTML manual required')
    Guide().feed(text)

def assemble(args):
    workspace = args.workspace.resolve()
    server = args.server_workspace.resolve()
    report = read(args.validation_report)
    linux = read(args.linux_report)
    if not report['Ok'] or report['Plan']['Profile'] not in ('product', 'all') or report['Plan']['Configuration'] != 'Release':
        raise ValueError('Successful canonical Release product/all report required')
    if not linux['Ok'] or linux['Schema'] != 'reign-linux-build-v1':
        raise ValueError('Successful canonical Linux component required')
    if report['SourceFingerprintSha256'] != linux['SourceFingerprintSha256']:
        raise ValueError('Client/server reports qualify different source')
    # This canonical read-only engine enforces original report location, current source,
    # paired hygiene, retained verifier and complete artifact hashes.
    verifier=args.verifier.resolve()
    if sha(verifier)!=args.verifier_sha256:
        raise ValueError('Independently selected qualified verifier changed')
    result = subprocess.run(['dotnet', str(verifier), '--deployment-source-check-cli',
        '--validation-report', str(args.validation_report.resolve()), '--linux-build-report', str(args.linux_report.resolve())],
        cwd=workspace, capture_output=True, text=True, check=True,stdin=subprocess.DEVNULL,timeout=300,
        env={**os.environ,'REIGN_WORKSPACE_ROOT':str(workspace),'REIGN_MCP_ALLOW_BUILD':'false','REIGN_MCP_ALLOW_RESTORE':'false'})
    if json.loads(result.stdout).get('Ok') is not True:
        raise ValueError('Canonical source/artifact binding failed')
    for root in (workspace, server):
        if git(root, 'diff', 'HEAD', '--name-only'):
            raise ValueError('Commit reviewed tracked source before packaging')
    selected_sources=set(git(server,'ls-files','-z').split('\0'))
    for root in (workspace,server):
        flags=git(root,'ls-files','-v').splitlines()
        if any(row and (row[0].islower() or row[0]=='S') for row in flags):
            raise ValueError('Source has assume-unchanged/skip-worktree flags; review them before packaging')
    if not Path(__file__).resolve().is_relative_to(server) or Path(__file__).resolve().relative_to(server).as_posix() not in selected_sources:
        raise ValueError('Use the committed server-owned builder')
    image = read(args.image_receipt)
    if image.get('schema') != 'reign-tester-image-receipt-v1' or image.get('auditPassed') is not True or sha(args.image) != image['sha256']:
        raise ValueError('Prepared image audit/hash missing')
    if image['requirementsSha256'] != sha(server / 'services/vector-worker/requirements-linux.txt') or image['modelLockSha256'] != sha(server / 'release/embedding-model.lock.json'):
        raise ValueError('Dependency image does not match current locks')
    for name in ('Prepare-MemorySelection.py','Prepare-LinuxRuntime.py'):
        if image.get('preparationSha256',{}).get(name)!=sha(server/'scripts'/name):
            raise ValueError('Dependency image preparation source differs')
    if args.output.exists():
        raise ValueError('Output directory already exists; preserve earlier candidate')
    args.output.mkdir(parents=True)
    target = args.output / 'Reign'
    target.mkdir()
    a = Assembler(target)
    tracked = git(workspace, 'ls-files', '-z', 'ReignBeta').split('\0')
    for name in tracked:
        if not name:
            continue
        relative = name.removeprefix('ReignBeta/')
        if relative.startswith(CONTENT) or relative in ('SubModule.xml', 'PortraitCache/shared-portrait-inventory.json'):
            a.copy(workspace / name, 'ReignBeta/' + relative)
    inventory_check(target / 'ReignBeta/PortraitCache/_shared', read(target / 'ReignBeta/PortraitCache/shared-portrait-inventory.json'))
    artifact = Path(report['ArtifactRoot'])
    hashes = report['DeploymentArtifactHashes']
    for name, digest in hashes.items():
        p = PurePosixPath(name)
        if name.startswith('client/out/native-portrait-generator/'):
            if p.name not in HELPER_FILES or p.parent.as_posix()!='client/out/native-portrait-generator':
                raise ValueError('Unexpected Windows helper artifact')
            a.copy(artifact / name, 'portrait-helper/' + name.removeprefix('client/out/native-portrait-generator/'), digest)
        elif p.parent.as_posix() == 'client/out' and p.suffix.lower() == '.dll':
            if p.name not in CLIENT_DLLS: raise ValueError('Unexpected client assembly: '+p.name)
            a.copy(artifact / name, 'ReignBeta/bin/Win64_Shipping_Client/' + p.name, digest)
        elif name in ('client/out/.version.txt', 'client/out/.version_number.txt'):
            a.copy(artifact / name, 'ReignBeta/' + p.name, digest)
    publish = Path(linux['ArtifactRoot'])
    manifest = publish / 'reign-linux-artifact.json'
    if sha(manifest) != linux['ArtifactManifestSha256']:
        raise ValueError('Linux inventory changed')
    for name, digest in read(manifest)['files'].items():
        a.copy(publish / name, 'server/' + name, digest)
    a.copy(manifest, 'server/reign-linux-artifact.json', linux['ArtifactManifestSha256'])
    for name in SUPPORT:
        if 'scripts/'+name not in selected_sources: raise ValueError('Untracked support script')
        a.copy(server / 'scripts' / name, 'server-support/scripts/' + name)
    for name in ('embedding-model.lock.json', 'release.json', 'dependencies.lock.json'):
        if 'release/'+name not in selected_sources: raise ValueError('Untracked release input')
        a.copy(server / 'release' / name, 'server-support/release/' + name)
    a.copy(server / 'services/vector-worker/requirements-linux.txt', 'server-support/services/vector-worker/requirements-linux.txt')
    here = Path(__file__).resolve().parent
    for name in ('Install.cmd', 'Install-Reign.ps1', 'Start-Reign.ps1', 'initialize-server.sh', 'INSTALL.html', 'THIRD_PARTY.txt'):
        if (here/name).relative_to(server).as_posix() not in selected_sources: raise ValueError('Untracked installer input')
        a.copy(here / name, name)
    if args.guide.suffix.lower()!='.html': raise ValueError('Single-file HTML manual required')
    verify_guide(args.guide)
    a.copy(args.guide, 'Reign User Guide.html')
    a.copy(args.image, 'reign-server-image.tar', image['sha256'])
    a.copy(args.image_receipt, 'image-receipt.json')
    release = read(server / 'release/release.json')
    files = [{'path':p.relative_to(target).as_posix(),'bytes':p.stat().st_size,'sha256':sha(p)}
             for p in sorted(target.rglob('*')) if p.is_file()]
    payload = dict(schema=SCHEMA, version=release['version'], protocolVersion=release['protocolVersion'],
        contentVersion=release['contentVersion'], sourceFingerprintSha256=report['SourceFingerprintSha256'],
        clientCommit=git(workspace,'rev-parse','HEAD'), serverCommit=git(server,'rev-parse','HEAD'),
        qualification=dict(validationRun=report['RunId'], validationReportSha256=sha(args.validation_report),
            linuxRun=linux['RunId'], linuxReportSha256=sha(args.linux_report)), files=files)
    check_entries(files)
    needed={*('ReignBeta/bin/Win64_Shipping_Client/'+n for n in CLIENT_DLLS),
            *('portrait-helper/'+n for n in HELPER_FILES),'ReignBeta/.version.txt','ReignBeta/.version_number.txt','server/ReignServer'}
    if not needed.issubset({e['path'] for e in files}):raise ValueError('Incomplete compiled payload')
    (target / 'package-manifest.json').write_text(json.dumps(payload,indent=2)+'\n',encoding='utf-8')
    verify_tree(target, payload)
    archive = args.output / ('Reign-unstable-' + report['RunId'] + '.zip')
    with zipfile.ZipFile(archive,'w',compression=zipfile.ZIP_DEFLATED,compresslevel=3,allowZip64=True) as z:
        for p in sorted(target.rglob('*')):
            if p.is_file():
                info=zipfile.ZipInfo('Reign/'+p.relative_to(target).as_posix(),date_time=(2026,1,1,0,0,0))
                info.compress_type=zipfile.ZIP_DEFLATED
                with p.open('rb') as source,z.open(info,'w',force_zip64=True) as output:
                    shutil.copyfileobj(source,output,1024*1024)
    # Verify each member against the frozen source bytes, without a second giant extraction.
    expected={e['path']:e['sha256'] for e in payload['files']}
    expected['package-manifest.json']=sha(target/'package-manifest.json')
    with zipfile.ZipFile(archive) as z:
        if set(z.namelist())!={'Reign/'+name for name in expected} or len(z.infolist())!=len(expected):raise ValueError('ZIP inventory mismatch')
        for name,digest in expected.items():
            with z.open('Reign/'+name) as stream:
                if hashlib.file_digest(stream,'sha256').hexdigest()!=digest:raise ValueError('Archive roundtrip failed')
    receipt=dict(schema='reign-tester-package-receipt-v1',zip=str(archive),bytes=archive.stat().st_size,
        sha256=sha(archive),manifestSha256=sha(target/'package-manifest.json'),files=len(files),
        clientCommit=payload['clientCommit'],serverCommit=payload['serverCommit'])
    (args.output/'package-receipt.json').write_text(json.dumps(receipt,indent=2)+'\n')
    print(json.dumps(receipt,indent=2))

if __name__ == '__main__':
    p=argparse.ArgumentParser(description=__doc__)
    for name in ('workspace','server-workspace','validation-report','linux-report','image','image-receipt','guide','output'):
        p.add_argument('--'+name,required=True,type=Path)
    p.add_argument('--verifier',required=True,type=Path)
    p.add_argument('--verifier-sha256',required=True)
    assemble(p.parse_args())
