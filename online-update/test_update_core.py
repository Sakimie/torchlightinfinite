"""Temporary-key tests only. No user file or product signing key is read or changed."""
import base64
import copy
import hashlib
import io
import json
import os
from pathlib import Path
import shutil
import stat
import subprocess
import tempfile
import types
import unittest
from unittest.mock import patch
import zipfile
import talos_update_core as C

NODE = shutil.which('node')
KEYGEN = "const c=require('node:crypto'),k=c.generateKeyPairSync('ed25519');process.stdout.write(JSON.stringify({public:k.publicKey.export({type:'spki',format:'pem'}),private:k.privateKey.export({type:'pkcs8',format:'pem'})}));"
SIGN = "const c=require('node:crypto'),fs=require('node:fs'),q=JSON.parse(fs.readFileSync(0,'utf8'));process.stdout.write(c.sign(null,Buffer.from(q.payload,'base64'),q.key).toString('base64'));"

def digest(value):
    return hashlib.sha256(value).hexdigest()

def sign(raw, key):
    payload = base64.b64encode(raw).decode('ascii')
    result = subprocess.run([NODE, '--eval', SIGN], input=json.dumps({'payload': payload, 'key': key}).encode(), stdout=subprocess.PIPE, stderr=subprocess.PIPE, check=True)
    return {'schema': 'talos-signed-feed-v1', 'payload': payload, 'signature': result.stdout.decode()}

@unittest.skipUnless(NODE, 'Node crypto required')
class CoreTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        # These keys exist only in this test process and are never printed or saved.
        result = subprocess.run([NODE, '--eval', KEYGEN], capture_output=True, check=True)
        cls.keys = json.loads(result.stdout)
        cls.fixture_files = {name: ('fixed fixture ' + name).encode() for name in C.BASE_DEPENDENCIES}

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='talos-core-test-')
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.pins = {name: (len(data), digest(data)) for name, data in self.fixture_files.items()}
        self.replace_pins = patch.object(C, 'BASE_DEPENDENCIES', self.pins)
        self.replace_pins.start(); self.addCleanup(self.replace_pins.stop)
        for name, data in self.fixture_files.items():
            target = self.root / name; target.parent.mkdir(parents=True, exist_ok=True); target.write_bytes(data)
        self.urls = {}; self.requests = []
        self.u = C.Updater(self.root, self.keys['public'], feed_url='https://updates.example/latest.json', node_path=NODE, transport=self.transport)

    def transport(self, url, maximum):
        self.requests.append(url)
        data = self.urls[url]
        if isinstance(data, Exception):
            raise data
        return data

    def release(self, version='0.3.2', files=None, archive=None, changes=None):
        if files is None:
            files = {f'TalosHelper-App-{version}.exe': b'MZ-unit-test-' + version.encode(), f'releases/{version}/talos_game_update.py': b'# own helper\n', f'releases/{version}/training/game-runtime.cjs': b'// own adapter\n'}
        if archive is None:
            output = io.BytesIO()
            with zipfile.ZipFile(output, 'w', compression=zipfile.ZIP_DEFLATED) as z:
                for name, data in files.items():
                    z.writestr(name, data)
            archive = output.getvalue()
        manifest = {'schema': 'talos-release-v1', 'app': 'TalosHelper', 'version': version,
                    'launcher_min': '1.0.0', 'base_version': '0.3', 'published_at': '2026-10-17T16:00:00+08:00',
                    'notes': ['测试签名版本'], 'entry': f'TalosHelper-App-{version}.exe',
                    'package': {'url': f'https://updates.example/{version}.zip', 'bytes': len(archive), 'sha256': digest(archive)},
                    'files': [{'path': name, 'bytes': len(data), 'sha256': digest(data)} for name, data in files.items()]}
        if changes:
            manifest.update(changes)
        envelope = sign(json.dumps(manifest, ensure_ascii=False, separators=(',', ':')).encode(), self.keys['private'])
        self.urls[self.u.feed_url] = json.dumps(envelope).encode()
        self.urls[manifest['package']['url']] = archive
        return manifest, envelope, archive

    def install_release(self, version='0.3.2'):
        self.release(version)
        return self.u.install(self.u.check())

    def local(self, envelope, archive):
        folder = self.root / 'local-import'; folder.mkdir(exist_ok=True)
        feed = folder / 'latest.json'; package = folder / 'package.zip'
        feed.write_text(json.dumps(envelope), encoding='utf8'); package.write_bytes(archive)
        return feed, package

    def test_real_ed25519_check_install_and_immutable_original_dependencies(self):
        self.release(); manifest = self.u.check(); info = self.u.install(manifest)
        self.assertEqual(info['active'], '0.3.2')
        self.assertEqual(self.u.status()['active'], '0.3.2')
        self.assertEqual(self.requests, [self.u.feed_url, manifest['package']['url']])
        for name, data in self.fixture_files.items():
            self.assertEqual((self.root / name).read_bytes(), data)
            self.assertEqual((self.root / 'releases/0.3.2' / name).read_bytes(), data)

    def test_install_refuses_unverified_or_mutated_manifest(self):
        raw, _, _ = self.release()
        with self.assertRaises(C.AuthenticationError):
            self.u.install(raw)
        verified = self.u.check(); altered = copy.deepcopy(verified); altered['notes'] = ['changed']
        with self.assertRaises(C.AuthenticationError):
            self.u.install(altered)
        copied = json.loads(json.dumps(verified)); self.assertEqual(self.u.install(copied)['active'], '0.3.2')

    def test_signature_exact_bytes_invalid_key_and_malformed_encoding(self):
        _, envelope, _ = self.release()
        bad = dict(envelope); bad['payload'] = base64.b64encode(base64.b64decode(bad['payload']) + b' ').decode()
        self.urls[self.u.feed_url] = json.dumps(bad).encode()
        with self.assertRaises(C.AuthenticationError):
            self.u.check()
        bad = dict(envelope); bad['signature'] = '!!!'; self.urls[self.u.feed_url] = json.dumps(bad).encode()
        with self.assertRaises(C.AuthenticationError):
            self.u.check()
        malformed = sign(b'{"schema":"a","schema":"b"}', self.keys['private'])
        self.urls[self.u.feed_url] = json.dumps(malformed).encode()
        with self.assertRaises(C.ValidationError):
            self.u.check()

    def test_manifest_windows_paths_and_duplicate_case_rejected(self):
        bad_names = ['../outside', 'releases/0.3.2/../outside', 'releases/0.3.2/a:b', 'releases/0.3.2/CON.txt', 'releases/0.3.2/trailing.', 'releases/0.3.2/dir /x', 'releases\\0.3.2\\file', 'releases/0.3.2/LPT¹.log', 'releases/0.3.2/CON .txt', 'releases/0.3.2/CONIN$', 'releases/0.3.2/CONOUT$']
        for name in bad_names:
            with self.subTest(name=name):
                self.release(files={'TalosHelper-App-0.3.2.exe': b'x', name: b'y'})
                with self.assertRaises(C.ValidationError):
                    self.u.check()
        self.release(files={'TalosHelper-App-0.3.2.exe': b'x', 'releases/0.3.2/a': b'y', 'releases/0.3.2/A': b'z'})
        with self.assertRaises(C.ValidationError):
            self.u.check()

    def test_no_shared_or_redistributed_source_paths(self):
        for name in ['runtime/node.exe', '_internal/a.dll', 'releases/0.3.2/english-source/js/engine.js', 'releases/0.3.2/training/english/policy.json', 'releases/0.3.2/English-Source/js/engine.js']:
            self.release(files={'TalosHelper-App-0.3.2.exe': b'x', name: b'y'})
            with self.assertRaises(C.ValidationError):
                self.u.check()

    def test_truncated_wrong_hash_extra_zip_members_and_symlink(self):
        manifest, envelope, archive = self.release(); good = self.u.check()
        self.urls[manifest['package']['url']] = archive[:-1]
        with self.assertRaises(C.ValidationError):
            self.u.install(good)
        output = io.BytesIO()
        with zipfile.ZipFile(output, 'w') as z:
            z.writestr(manifest['entry'], b'wrong'); z.writestr('extra.py', b'x')
        self.release(archive=output.getvalue())
        with self.assertRaises(C.ValidationError):
            self.u.install(self.u.check())
        output = io.BytesIO()
        name = 'releases/0.3.2/talos_game_update.py'; link = zipfile.ZipInfo(name); link.create_system = 3; link.external_attr = (stat.S_IFLNK | 0o777) << 16
        with zipfile.ZipFile(output, 'w') as z:
            z.writestr('TalosHelper-App-0.3.2.exe', b'X'); z.writestr(link, b'../target')
        self.release(files={'TalosHelper-App-0.3.2.exe': b'X', name: b'../target'}, archive=output.getvalue())
        with self.assertRaises(C.ValidationError):
            self.u.install(self.u.check())
        self.assertFalse((self.root / 'talos-update-state.json').exists())

    def test_zip_member_hash_and_high_ratio_boundaries(self):
        files = {'TalosHelper-App-0.3.2.exe': b'X', 'releases/0.3.2/fill': b'0' * (2 * 1024 * 1024)}
        self.release(files=files)
        with self.assertRaises(C.ValidationError):
            self.u.install(self.u.check())
        output = io.BytesIO()
        with zipfile.ZipFile(output, 'w') as z:
            z.writestr('TalosHelper-App-0.3.2.exe', b'Y')
        self.release(files={'TalosHelper-App-0.3.2.exe': b'X'}, archive=output.getvalue())
        with self.assertRaises(C.ValidationError):
            self.u.install(self.u.check())

    def test_cancel_and_atomic_state_failure_leave_old_active_and_no_partial_new_files(self):
        self.install_release(); original_state = (self.root / 'talos-update-state.json').read_bytes()
        self.release('0.3.3'); signed = self.u.check()
        with self.assertRaises(C.UpdateCancelled):
            self.u.install(signed, progress=lambda event: False if event['stage'] == 'activate' else None)
        self.assertEqual((self.root / 'talos-update-state.json').read_bytes(), original_state)
        self.assertFalse((self.root / 'TalosHelper-App-0.3.3.exe').exists())
        with patch.object(self.u, '_atomic_state', side_effect=OSError('simulated full disk')):
            with self.assertRaises(C.InstallError):
                self.u.install(signed)
        self.assertEqual((self.root / 'talos-update-state.json').read_bytes(), original_state)
        self.assertFalse((self.root / 'releases/0.3.3').exists())
        self.assertTrue((self.root / '.talos-update-lock').is_file())

    def test_same_version_idempotent_but_conflicting_file_or_identity_refused(self):
        self.release(); good = self.u.check(); self.u.install(good); self.u.install(good)
        self.release(changes={'notes': ['another identity']})
        with self.assertRaises(C.ValidationError):
            self.u.install(self.u.check())
        (self.root / 'releases/0.3.2/talos_game_update.py').write_bytes(b'user edited')
        self.assertFalse(self.u.status()['active_valid'])
        with self.assertRaises(C.ValidationError):
            self.u.launch_active()
        self.assertEqual((self.root / 'releases/0.3.2/talos_game_update.py').read_bytes(), b'user edited')

    def test_explicit_downgrade_rejected_and_offline_rollback_verified(self):
        self.install_release(); self.install_release('0.3.3')
        self.release('0.3.2')
        with self.assertRaises(C.ValidationError):
            self.u.install(self.u.check())
        requests = list(self.requests)
        self.u.transport = lambda *_: (_ for _ in ()).throw(AssertionError('unexpected network'))
        self.assertEqual(self.u.rollback()['active'], '0.3.2')
        self.assertEqual(self.u.status()['active'], '0.3.2')
        self.assertEqual(self.requests, requests)
        (self.root / 'releases/0.3.3/training/game-runtime.cjs').write_bytes(b'tampered')
        with self.assertRaises(C.ValidationError):
            self.u.rollback('0.3.3')

    def test_bootstrap_status_and_launch_are_offline_and_launch_uses_fixed_verified_path(self):
        _, envelope, archive = self.release()
        folder = self.root / 'talos-updater/bootstrap'; folder.mkdir(parents=True)
        (folder / 'latest.json').write_text(json.dumps(envelope)); (folder / 'package.zip').write_bytes(archive)
        self.u.transport = lambda *_: (_ for _ in ()).throw(AssertionError('unexpected network'))
        self.assertEqual(self.u.status()['active'], '0.3.2')
        real = subprocess.Popen; launches = []
        def process(args, *a, **kw):
            if args[0] == str(Path(NODE).resolve()):
                return real(args, *a, **kw)
            launches.append((args, kw)); return types.SimpleNamespace(pid=456)
        with patch.object(C.subprocess, 'Popen', side_effect=process):
            result = self.u.launch_active()
        self.assertEqual(result['pid'], 456)
        self.assertEqual(launches[0][0], [str(self.root / 'TalosHelper-App-0.3.2.exe')])
        self.assertIs(launches[0][1]['shell'], False)
        self.assertEqual(self.requests, [])

    def test_missing_dependency_junction_and_unlisted_installed_file_are_rejected(self):
        self.release(); good = self.u.check()
        (self.root / 'english-source/js/engine.js').unlink()
        with self.assertRaises(C.ValidationError):
            self.u.install(good)
        (self.root / 'english-source/js/engine.js').write_bytes(self.fixture_files['english-source/js/engine.js'])
        outside = self.root / 'external'; outside.mkdir()
        (self.root / 'releases').symlink_to(outside, target_is_directory=True)
        with self.assertRaises(C.ValidationError):
            self.u.install(good)
        (self.root / 'releases').unlink(); self.u.install(good)
        (self.root / 'releases/0.3.2/unlisted.py').write_text('unlisted')
        with self.assertRaises(C.ValidationError):
            self.u.launch_active()

    def test_state_cannot_redirect_launch_and_lock_is_exclusive(self):
        self.install_release()
        state_file = self.root / 'talos-update-state.json'; state = json.loads(state_file.read_text()); state['installed']['0.3.2']['entry'] = '/bin/sh'; state_file.write_text(json.dumps(state))
        with self.assertRaises(C.ValidationError):
            self.u.launch_active()
        with self.u._lock():
            with self.assertRaises(C.UpdateBusy):
                self.u.rollback()

    def test_https_and_redirect_validation_never_accepts_credentials_or_http(self):
        for url in ['http://example/a', 'https://user:pass@example/a', 'file:///tmp/a', 'https://example/a#fragment', 'https://example/\r\nX:y']:
            with self.assertRaises(C.ValidationError):
                C._url(url)
        handler = C._Redirect()
        with self.assertRaises(C.ValidationError):
            handler.redirect_request(None, None, 302, '', {}, 'http://example/file')

    def test_node_preload_environment_is_removed_for_verification(self):
        self.release()
        with patch.dict(os.environ, {'NODE_OPTIONS': '--require=/nonexistent/bad.js', 'NODE_PATH': '/nonexistent'}):
            self.u.check()


    def test_exception_after_state_commit_does_not_delete_active_files(self):
        self.install_release(); self.release('0.3.3'); manifest = self.u.check()
        real = self.u._atomic_state
        def committed_then_error(state):
            real(state)
            raise OSError('simulated post-commit error')
        with patch.object(self.u, '_atomic_state', side_effect=committed_then_error):
            result = self.u.install(manifest)
        self.assertEqual(result['active'], '0.3.3')
        self.assertEqual(self.u.status()['active'], '0.3.3')
        self.assertTrue((self.root / 'TalosHelper-App-0.3.3.exe').is_file())

    def test_damaged_historical_release_does_not_disable_current_active(self):
        self.install_release(); self.install_release('0.3.3')
        (self.root / 'releases/0.3.2/talos_game_update.py').write_bytes(b'damaged old file')
        status = self.u.status()
        self.assertEqual(status['active'], '0.3.3')
        self.assertEqual(status['unavailable'][0]['version'], '0.3.2')
        with self.assertRaises(C.ValidationError):
            self.u.rollback('0.3.2')

    def test_os_lock_releases_after_crashed_process(self):
        # A killed process leaves the inode, but the kernel releases its lock.
        code = "import sys,os;sys.path.insert(0,sys.argv[1]);from talos_update_core import Updater;u=Updater(sys.argv[2],'test',node_path=sys.argv[3]);cm=u._lock();cm.__enter__();os._exit(0)"
        subprocess.run([os.sys.executable, '-c', code, str(Path(C.__file__).parent), str(self.root), NODE], check=True)
        with self.u._lock():
            pass


    def test_hard_interruption_resumes_only_matching_completed_files(self):
        self.install_release(); before = (self.root / 'talos-update-state.json').read_bytes()
        self.release('0.3.3'); manifest = self.u.check(); publish = self.u._publish; calls = []
        class HardCut(BaseException):
            pass
        def stop_after_two_files(source, target, created):
            publish(source, target, created); calls.append(target)
            if len(calls) == 2:
                raise HardCut()
        with patch.object(self.u, '_publish', side_effect=stop_after_two_files):
            with self.assertRaises(HardCut):
                self.u.install(manifest)
        self.assertEqual((self.root / 'talos-update-state.json').read_bytes(), before)
        self.assertEqual(self.u.status()['active'], '0.3.2')
        self.assertEqual((self.root / manifest['entry']).stat().st_size, manifest['files'][0]['bytes'])
        self.assertEqual(self.u.install(manifest)['active'], '0.3.3')
        self.assertEqual(self.u.status()['active'], '0.3.3')


    def test_verified_rollback_and_new_install_can_recover_a_broken_active(self):
        self.install_release(); self.install_release('0.3.3')
        broken = self.root / 'TalosHelper-App-0.3.3.exe'; broken.write_bytes(b'quarantined')
        status = self.u.status()
        self.assertFalse(status['active_valid']); self.assertIn('0.3.2', status['installed'])
        with self.assertRaises(C.ValidationError):
            self.u.launch_active()
        self.assertEqual(self.u.rollback('0.3.2')['active'], '0.3.2')
        self.assertTrue(self.u.status()['active_valid'])
        # A fresh signed version can also recover without overwriting the broken EXE.
        self.release('0.3.4')
        self.assertEqual(self.u.install(self.u.check())['active'], '0.3.4')
        self.assertEqual(broken.read_bytes(), b'quarantined')

if __name__ == '__main__':
    unittest.main(verbosity=2)
