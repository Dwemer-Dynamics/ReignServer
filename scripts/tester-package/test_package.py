"""Provider-free distribution contracts; called by canonical MCP tests."""
print('tester-contracts: isolated Python started',flush=True)
import hashlib
import importlib.util
import json
from pathlib import Path
import subprocess
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
        self.assertIn('pg_createcluster 15 main --port=5432',(HERE/'initialize-server.sh').read_text())

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
