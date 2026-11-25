"""Provider-free distribution contracts; called by canonical MCP tests."""
print('tester-contracts: isolated Python started',flush=True)
import hashlib
import importlib.util
import io
import json
from pathlib import Path
import subprocess
import tarfile
import tempfile
import unittest
import os

HERE=Path(__file__).resolve().parent
spec=importlib.util.spec_from_file_location('tester_package',HERE/'build_package.py')
module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module)

class PackageContracts(unittest.TestCase):
    def test_unsafe_members(self):
        for name in ('../a','/a','a/../b','C:/x','a\\b','a//b','a/.','a/NUL.txt','a/b.'):
            with self.subTest(name=name),self.assertRaises(ValueError):module.safe_name(name)
        self.assertEqual(module.safe_name('safe/(portrait)/portrait.png'),'safe/(portrait)/portrait.png')

    def test_duplicate_and_malformed_evidence(self):
        entry={'path':'a.txt','bytes':1,'sha256':'a'*64}
        module.check_entries([entry])
        for entries in ([entry,dict(entry,path='A.TXT')],[dict(entry,bytes=-1)],[dict(entry,sha256='not-a-hash')]):
            with self.assertRaises(ValueError):module.check_entries(entries)

    def test_exact_tree_and_tamper(self):
        with tempfile.TemporaryDirectory(prefix='reign-package-contract-') as raw:
            root=Path(raw);(root/'a.txt').write_bytes(b'first')
            manifest={'schema':module.SCHEMA,'files':[{'path':'a.txt','bytes':5,'sha256':module.sha(root/'a.txt')}]}
            (root/'package-manifest.json').write_text(json.dumps(manifest))
            self.assertTrue(module.verify_tree(root,manifest))
            (root/'a.txt').write_bytes(b'other')
            with self.assertRaises(ValueError):module.verify_tree(root,manifest)
            (root/'a.txt').write_bytes(b'first');(root/'extra.txt').touch()
            with self.assertRaises(ValueError):module.verify_tree(root,manifest)

    def test_prohibited_game_or_private_payload(self):
        with tempfile.TemporaryDirectory(prefix='reign-package-contract-') as raw:
            root=Path(raw);a=module.Assembler(root/'output')
            for name in ('TaleWorlds.Core.dll','private.pem','campaign.sav'):
                source=root/name;source.write_bytes(b'fixture')
                with self.assertRaises(ValueError):a.copy(source,'payload/'+name)

    def test_client_dll_selection(self):
        lock={'schema':'reign-external-dependencies-v1','artifacts':[{'id':'Bannerlord.ButterLib','role':'bannerlord-module'}]}
        required=sorted(module.CLIENT_DLLS)
        self.assertEqual(module.select_client_dlls(required,lock),module.CLIENT_DLLS)
        self.assertEqual(module.select_client_dlls(required+['Bannerlord.ButterLib.dll'],lock),module.CLIENT_DLLS)
        for extra in ('bannerlord.butterlib.dll','reignbeta.dll','Bannerlord.UIExtenderEx.dll','Unknown.dll','TaleWorlds.Core.dll'):
            with self.subTest(extra=extra),self.assertRaises(ValueError):module.select_client_dlls(required+[extra],lock)
        with self.assertRaises(ValueError):module.select_client_dlls(required+['Bannerlord.ButterLib.dll'],dict(lock,artifacts=[]))
        with self.assertRaises(ValueError):module.select_client_dlls(required[1:]+['Bannerlord.ButterLib.dll'],lock)
        self.assertIn('ReignBeta.Core.dll',module.CLIENT_DLLS)
        with self.assertRaises(ValueError):module.select_client_dlls([n for n in required if n!='ReignBeta.Core.dll'],lock)
        for extra in ('reignbeta.core.dll','ReignBeta.Core2.dll'):
            with self.subTest(extra=extra),self.assertRaises(ValueError):module.select_client_dlls(required+[extra],lock)
        with_testing=module.CLIENT_DLLS|{'ReignBeta.Testing.dll'}
        self.assertEqual(module.select_client_dlls(required+['ReignBeta.Testing.dll'],lock),with_testing)
        with self.assertRaises(ValueError):module.select_client_dlls(required+['reignbeta.testing.dll'],lock)
        with self.assertRaises(ValueError):module.select_client_dlls([n for n in required if n!='ReignBeta.dll']+['ReignBeta.Testing.dll'],lock)

    def test_inventory_detects_missing_extra_and_pointer(self):
        with tempfile.TemporaryDirectory(prefix='reign-package-contract-') as raw:
            root=Path(raw);p=root/'portrait.png';p.write_bytes(b'actual image fixture')
            inventory={'files':[{'path':'portrait.png','bytes':p.stat().st_size,'sha256':module.sha(p)}]}
            module.inventory_check(root,inventory)
            p.write_bytes(b'version https://git-lfs.github.com/spec/v1')
            with self.assertRaises(ValueError):module.inventory_check(root,inventory)

    def test_no_global_runtime_or_destructive_distro_controls(self):
        for name in ('Install-Reign.ps1','Start-Reign.ps1','initialize-server.sh','prepare-image.sh'):
            source=(HERE/name).read_text()
            for forbidden in ('--shutdown','--unregister','.wslconfig'):
                self.assertNotIn(forbidden,source,name)
        source=(HERE/'prepare-image.sh').read_text()
        self.assertLess(source.index('policy-rc.d'),source.index('apt-get install'))
        self.assertIn('create_main_cluster = false',source)
        self.assertIn('--leave-stopped',(HERE/'initialize-server.sh').read_text())
        initialize=(HERE/'initialize-server.sh').read_text()
        self.assertIn('pg_createcluster "$pg_major" main --port=5432',initialize)
        # The image/legacy-cluster guard must refuse before ownership, scripts, services or database change.
        guard=initialize.index("raise SystemExit('This distro holds a PostgreSQL")
        for mutation in ("p.write_text(json.dumps({'schema':'reign-tester-owner-v1'",'cp -r --no-preserve=all','systemctl unmask','pg_createcluster'):
            self.assertLess(guard,initialize.index(mutation),mutation)
        # Same-major updates compare runtime identity; only fresh/resume pins this package's base digest.
        self.assertIn("(sys.argv[2]!='update' and marker.get('baseManifestDigest')!=lock['base']['manifestDigest'])",initialize)
        self.assertIn('"$package/server-support/release/release.json" "$mode" <<',initialize)
        for name in ('initialize-server.sh','prepare-image.sh','runtime_probe.sh'):
            self.assertNotRegex((HERE/name).read_text(),r'postgresql[-@/]1[0-6]\b|pg_[a-z]+ 1[0-6] |libicu72|python3\.11',name)

    def test_server_image_identity(self):
        spec=importlib.util.spec_from_file_location('audit_image',HERE/'audit_image.py')
        audit=importlib.util.module_from_spec(spec);spec.loader.exec_module(audit)
        lock=audit.image_lock(HERE.parent.parent)
        self.assertEqual((lock['os']['versionId'],lock['postgresMajor'],lock['python'],lock['icuPackage']),('13',17,'3.13','libicu76'))
        base=lock['base']
        provenance={'ociIndex':base['indexDigest'],'ociManifest':base['manifestDigest'],'rootfsLayerSha256':base['rootfsLayerDigest'][7:],
                    'rootfsLayerBytes':base['rootfsLayerBytes'],'reference':base['reference'],'platform':base['platform']}
        audit.check_provenance(provenance,lock)
        old={'rootfsLayerSha256':'69245854ad5855991d16224f6abf55855d2d73922785206adea9c5c5988434a0',
             'ociManifest':'sha256:366a6da5efcceffbaa19c560a101b97b87a1a9664eeeac8bc3d24395ddbf14f3'}
        for changed in (old,dict(provenance,ociIndex=None),dict(provenance,rootfsLayerBytes=1)):
            with self.assertRaises(ValueError):audit.check_provenance(changed,lock)
        packages=['postgresql-17\t17.6-1','postgresql-client-17\t17.6-1','postgresql-common\t278','libicu76\t76.1-4','python3\t3.13.5-1','python3-venv\t3.13.5-1']
        marker={'schema':'reign-tester-image-v2','debian':'13','codename':'trixie','postgresMajor':17,'python':'3.13.5','icuPackage':'libicu76',
                'baseManifestDigest':base['manifestDigest'],'requirementsSha256':'r','modelLockSha256':'m','personalDataIncluded':False,'packages':packages}
        audit.check_marker(marker,lock,'r','m')
        legacy={'schema':'reign-tester-image-v1','debian':'12','postgresMajor':15,'requirementsSha256':'r','modelLockSha256':'m','personalDataIncluded':False,'packages':[]}
        for changed in (legacy,dict(marker,postgresMajor=15),dict(marker,python='3.11.2'),dict(marker,baseManifestDigest=old['ociManifest']),
                        dict(marker,packages=packages+['postgresql-15\t15.14-0']),dict(marker,packages=packages[1:]),dict(marker,requirementsSha256='x')):
            with self.assertRaises(ValueError):audit.check_marker(changed,lock,'r','m')
        audit.check_venv('home = /usr/bin\nversion = 3.13.5\n',{'python3.13'},lock)
        for config,abis in (('version = 3.11.2\n',{'python3.11'}),('version = 3.13.5\n',{'python3.13','python3.11'}),('',{'python3.13'})):
            with self.assertRaises(ValueError):audit.check_venv(config,abis,lock)

    def test_image_sanitizer_rewrites_gnu_sparse_members(self):
        spec=importlib.util.spec_from_file_location('sanitize_image',HERE/'sanitize_image.py')
        sanitizer=importlib.util.module_from_spec(spec);spec.loader.exec_module(sanitizer)
        def sparse(name):
            # GNU sparse 1.0 PAX member: extent map block, then stored extents [(0,4),(1024,4)] of a 1028-byte file.
            stored=b'2\n0\n4\n1024\n4\n'.ljust(512,b'\0')+b'headtail'
            info=tarfile.TarInfo('./GNUSparseFile.0/'+name.rsplit('/',1)[-1]);info.size=len(stored)
            info.pax_headers={'GNU.sparse.major':'1','GNU.sparse.minor':'0','GNU.sparse.name':name,'GNU.sparse.realsize':'1028'}
            return info,io.BytesIO(stored)
        with tempfile.TemporaryDirectory(prefix='reign-package-sparse-') as raw:
            source=Path(raw)/'raw.tar';output=Path(raw)/'clean.tar'
            with tarfile.open(source,'w',format=tarfile.PAX_FORMAT) as tar:
                tar.addfile(*sparse('./var/log/lastlog'));tar.addfile(*sparse('./opt/kept.bin'))
                stale=tarfile.TarInfo('./var/log/app.log');stale.size=5;stale.pax_headers={'size':'5'}
                tar.addfile(stale,io.BytesIO(b'stale'))
                sentinel=tarfile.TarInfo('./etc/sentinel');sentinel.size=8
                tar.addfile(sentinel,io.BytesIO(b'sentinel'))
            with tarfile.open(source) as tar:self.assertEqual(tar.getmember('./var/log/lastlog').sparse,[(0,4),(1024,4)])
            self.assertEqual(sanitizer.sanitize(source,output),['var/log/lastlog','var/log/app.log'])
            self.assertNotIn(b'GNU.sparse',output.read_bytes())
            with tarfile.open(output) as tar:
                members={m.name:m for m in tar}
                self.assertEqual(list(members),['./var/log/lastlog','./opt/kept.bin','./var/log/app.log','./etc/sentinel'])
                for m in members.values():
                    self.assertTrue(m.isreg());self.assertIsNone(m.sparse)
                    self.assertFalse([k for k in m.pax_headers if k.startswith('GNU.sparse.')])
                read=lambda name:tar.extractfile(members[name]).read()
                self.assertEqual((read('./var/log/lastlog'),read('./var/log/app.log')),(b'',b''))
                self.assertEqual(read('./opt/kept.bin'),b'head'+b'\0'*1020+b'tail')
                self.assertEqual(read('./etc/sentinel'),b'sentinel')

    def test_windows_installer_contracts(self):
        with tempfile.TemporaryDirectory(prefix='reign-package-ps-') as raw:
            powershell=Path(os.environ['SystemRoot'])/'System32/WindowsPowerShell/v1.0/powershell.exe'
            environment={k:v for k,v in os.environ.items() if k.casefold()!='psmodulepath'}
            environment['PSModulePath']=str(powershell.parent/'Modules')+';'+str(Path(os.environ['ProgramFiles'])/'WindowsPowerShell/Modules')
            run=subprocess.run([str(powershell),'-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',str(HERE/'test_installer.ps1'),
                '-FixtureRoot',str(Path(raw)/'new-fixture')],capture_output=True,text=True,stdin=subprocess.DEVNULL,env=environment,timeout=90)
            self.assertEqual(run.returncode,0,run.stdout+'\n'+run.stderr)
            result=json.loads(run.stdout.strip().splitlines()[-1])
            self.assertTrue(result['ok']);self.assertGreaterEqual(result['cases'],24)
            self.assertEqual(result['nativeCalls'],0)

if __name__=='__main__':unittest.main(verbosity=2)
