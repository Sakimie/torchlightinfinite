#!/usr/bin/env python3.12
"""Real Tk/Node update integration QA without executing Windows PE files.

Requires Python 3.12, Tkinter, Pillow, NumPy, Node and DISPLAY (Xvfb works).
Only the five pinned base dependencies are copied from --original-dir. The
engine/vision .pyc files in --extracted-dir must be the audited Python 3.12
extraction from that original EXE. Temporary Ed25519 keys remain inside Node;
this test never reads a product private key or connects to a real update host.

Example:
  DISPLAY=:0 python3.12 online-update/test_integration.py \
    --version 0.3.3 \
    --bundle dist/online-0.3.3/TalosHelper-Online-Update-0.3.3.zip \
    --original-dir /workspace/legacy-talos-v03 \
    --extracted-dir /workspace/scratch/talos-v03-extracted

The transport is simulated; real Node crypto verifies every signed manifest.
Windows launch requests are observed with Popen, never executed. Game UI tests
use constructed manual fields, not a real screenshot or an OCR accuracy test.
"""
from __future__ import annotations

import argparse
import base64
import copy
import hashlib
import importlib.util
import io
import json
import marshal
import os
from pathlib import Path
import re
import shlex
import shutil
import socket
import subprocess
import sys
import tempfile
import threading
import time
import traceback
import types
import zipfile

sys.dont_write_bytecode = True


def digest(data):
    return hashlib.sha256(data).hexdigest()


def load_module(name, filename):
    spec = importlib.util.spec_from_file_location(name, filename)
    module = importlib.util.module_from_spec(spec)
    sys.modules[name] = module
    if str(filename).endswith('.pyc'):
        spec.loader.exec_module(module)
    else:
        exec(compile(Path(filename).read_bytes(), str(filename), 'exec'), module.__dict__)
    return module


class ValidationReport:
    def __init__(self):
        self.data = {
            'schema': 'talos-online-integration-v1', 'passed': False,
            'platform': sys.platform, 'python': sys.version.split()[0],
            'windowsExeExecuted': False, 'productPrivateKeyRead': False,
            'realNetworkTransportUsed': False, 'realGameOcrAccuracyClaimed': False,
            'scope': 'Actual signed bootstrap and pinned launcher modules; real Tk buttons and Node crypto; simulated online transport; Windows process launches observed only.',
            'limitations': [
                'Windows PE launch and native Windows rendering were not tested on this Linux host.',
                'HTTPS delivery was simulated through the transport API; no public update host was contacted.',
                'Manual game fields and controlled recognition failure are not a real-game OCR accuracy benchmark.',
            ],
            'checks': {}, 'errors': [],
        }

    def check(self, name, condition, detail=None):
        if not condition:
            raise AssertionError(name + ': ' + str(detail))
        self.data['checks'][name] = {'passed': True}
        if detail is not None:
            self.data['checks'][name]['detail'] = detail

    def rejected(self, name, action, error_type=Exception):
        try:
            action()
        except error_type as error:
            self.check(name, True, {'type': type(error).__name__, 'message': str(error)[:500]})
            return
        raise AssertionError(name + ': operation unexpectedly succeeded')


class Transport:
    def __init__(self, mapping):
        self.mapping = mapping
        self.calls = []
        self.fail = set()
        self.entered = threading.Event()
        self.release = threading.Event()
        self.release.set()
        self.hold = False

    def __call__(self, url, maximum):
        self.calls.append({'url': url, 'limit': maximum, 'thread': threading.get_ident()})
        if self.hold:
            self.entered.set()
            if not self.release.wait(15):
                raise TimeoutError('QA transport hold timed out')
        if url in self.fail:
            raise OSError('QA simulated transport failure')
        return self.mapping[url]


def zip_bytes(files, extra=None):
    stream = io.BytesIO()
    with zipfile.ZipFile(stream, 'w', zipfile.ZIP_DEFLATED) as archive:
        for name, data in files.items():
            archive.writestr(name, data)
        for name, data in (extra or {}).items():
            archive.writestr(name, data)
    return stream.getvalue()


def fixtures(node, guard, work):
    manifests, archives = [], {}
    for patch in range(2, 8):
        version = f'0.3.{patch}'
        entry = f'TalosHelper-App-{version}.exe'
        files = {
            entry: b'MZ\0QA inert Windows executable fixture; never executed\n' + version.encode(),
            f'releases/{version}/talos_game_update.py': b'# QA component fixture; not executed\n',
        }
        extra = {'../must-not-exist.txt': b'unsafe'} if patch == 5 else None
        archive = zip_bytes(files, extra)
        url = f'https://updates.example.test/{version}.zip'
        manifest = {
            'schema': 'talos-release-v1', 'app': 'TalosHelper', 'version': version,
            'launcher_min': '9.0.0' if patch == 7 else '1.0.0', 'base_version': '0.3',
            'published_at': '2026-10-17T00:00:00Z',
            'notes': [f'QA 临时签名版本 {version}', '截图和建议保持离线'],
            'entry': entry, 'package': {'url': url, 'bytes': len(archive), 'sha256': digest(archive)},
            'files': [{'path': name, 'bytes': len(data), 'sha256': digest(data)} for name, data in files.items()],
        }
        manifests.append(manifest)
        archives[version] = archive
    # The private key is generated and consumed inside this one Node invocation.
    # Only its public key and signed envelopes cross back into the QA process.
    signer = """const fs=require('node:fs'),c=require('node:crypto');const pair=c.generateKeyPairSync('ed25519');const inputs=JSON.parse(fs.readFileSync(0,'utf8'));process.stdout.write(JSON.stringify({publicKey:pair.publicKey.export({type:'spki',format:'pem'}),feeds:inputs.map(m=>{const b=Buffer.from(JSON.stringify(m));return {schema:'talos-signed-feed-v1',payload:b.toString('base64'),signature:c.sign(null,b,pair.privateKey).toString('base64')}})}));"""
    completed = subprocess.run([node, '--require', str(guard), '--eval', signer], input=json.dumps(manifests).encode(), stdout=subprocess.PIPE, stderr=subprocess.PIPE, check=True)
    signed = json.loads(completed.stdout)
    result = {}
    for manifest, feed in zip(manifests, signed['feeds']):
        result[manifest['version']] = {'manifest': manifest, 'feed': json.dumps(feed).encode(), 'archive': archives[manifest['version']]}
    return signed['publicKey'], result


def copy_base(source, destination, specs):
    destination.mkdir(parents=True, exist_ok=True)
    for name, (size, expected) in specs.items():
        content = (source / name).read_bytes()
        if len(content) != size or digest(content) != expected:
            raise AssertionError('Original base dependency differs: ' + name)
        target = destination / name
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(content)


def bootstrap(root, fixture):
    folder = root / 'talos-updater/bootstrap'
    folder.mkdir(parents=True, exist_ok=True)
    (folder / 'latest.json').write_bytes(fixture['feed'])
    (folder / 'package.zip').write_bytes(fixture['archive'])


def extract_bundle(bundle, destination):
    """Overlay only ZIP members, as a user unpacking the launcher update would."""
    with zipfile.ZipFile(bundle) as archive:
        if archive.testzip() is not None:
            raise AssertionError('Invalid ZIP CRC: ' + str(bundle))
        for item in archive.infolist():
            name = item.filename
            if item.is_dir():
                continue
            if name.startswith('/') or '\\' in name or '..' in Path(name).parts:
                raise AssertionError('Unsafe product bundle member: ' + name)
            target = destination / name
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(archive.read(item))


def make_node_wrapper(root, node, guard):
    wrapper = root / 'runtime/node.exe'
    wrapper.parent.mkdir(parents=True, exist_ok=True)
    wrapper.write_text('#!/bin/sh\nexec ' + shlex.quote(node) + ' --require ' + shlex.quote(str(guard)) + ' "$@"\n')
    wrapper.chmod(0o700)
    return wrapper


def pump(root, condition, timeout=12):
    deadline = time.monotonic() + timeout
    ready = [False]
    def poll():
        if condition():
            ready[0] = True
            root.quit()
        elif time.monotonic() >= deadline:
            root.quit()
        else:
            root.after(15, poll)
    root.after(0, poll)
    root.mainloop()
    if not ready[0]:
        raise AssertionError('Timed out waiting for Tk/controller state')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--repo', type=Path, default=Path(__file__).resolve().parent.parent)
    parser.add_argument('--bundle', type=Path)
    parser.add_argument('--version', default='0.3.3', help='Expected signed product version; temporary transaction fixtures are independent.')
    parser.add_argument('--previous-bundle', type=Path, help='Original signed 0.3.2 launcher bundle for migration QA.')
    parser.add_argument('--expected-feed-url', default='https://raw.githubusercontent.com/Sakimie/torchlightinfinite/talos-updates/latest.json')
    parser.add_argument('--expected-package-url', help='Exact immutable payload URL expected in the signed product manifest.')
    parser.add_argument('--original-dir', type=Path, default=Path('/workspace/legacy-talos-v03'))
    parser.add_argument('--extracted-dir', type=Path, default=Path('/workspace/scratch/talos-v03-extracted'))
    parser.add_argument('--output', type=Path)
    parser.add_argument('--artifacts-dir', type=Path)
    args = parser.parse_args()
    if not re.fullmatch(r'\d+\.\d+\.\d+', args.version):
        parser.error('--version must contain three numeric components')
    args.bundle = args.bundle or args.repo / f'dist/online-{args.version}/TalosHelper-Online-Update-{args.version}.zip'
    if args.previous_bundle is None and args.version != '0.3.2':
        args.previous_bundle = args.repo / 'dist/online-0.3.2/TalosHelper-Online-Update-0.3.2.zip'
    args.output = args.output or args.repo / 'online-update/integration-validation.json'
    args.artifacts_dir = args.artifacts_dir or args.repo / 'artifacts'
    report = ValidationReport()
    network_attempts, process_launches, worker_errors, tk_errors, foreign_writes = [], [], [], [], []
    owner = threading.get_ident()
    original_popen = subprocess.Popen
    original_socket = {name: getattr(socket.socket, name) for name in ('connect', 'connect_ex', 'sendto')}
    roots = []
    holds = []
    original_thread_hook = threading.excepthook
    original_exe_hash = digest((args.original_dir / 'TalosHelper.exe').read_bytes())
    def forbid(*values, **options):
        network_attempts.append('Python socket attempt blocked')
        raise RuntimeError('Actual network is forbidden in integration QA')
    for name in original_socket:
        setattr(socket.socket, name, forbid)
    threading.excepthook = lambda event: worker_errors.append(''.join(traceback.format_exception(event.exc_type, event.exc_value, event.exc_traceback)))
    def observe_launch(command, *values, **options):
        executable = str(command[0]) if isinstance(command, (list, tuple)) and command else str(command)
        if executable.lower().endswith('.exe') and Path(executable).name.lower() != 'node.exe':
            process_launches.append({'command': list(map(str, command)), 'cwd': str(options.get('cwd')), 'shell': options.get('shell', False)})
            return types.SimpleNamespace(pid=700000 + len(process_launches), poll=lambda: None)
        return original_popen(command, *values, **options)
    subprocess.Popen = observe_launch
    try:
        report.check('final_bundle_exists', args.bundle.is_file(), str(args.bundle))
        report.data['bundleSha256'] = digest(args.bundle.read_bytes())
        report.data['bundleBytes'] = args.bundle.stat().st_size
        report.data['productVersion'] = args.version
        with tempfile.TemporaryDirectory(prefix='talos-online-integration-') as temporary:
            work = Path(temporary)
            guard_log = work / 'node-network-attempts.txt'
            guard = work / 'deny-network.cjs'
            guard.write_text("const fs=require('node:fs');const deny=()=>{fs.appendFileSync(" + json.dumps(str(guard_log)) + ",'blocked\\n');throw new Error('Network forbidden in QA')};for(const n of ['net','tls']){const m=require('node:'+n);m.connect=deny;m.createConnection=deny;if(m.Socket)m.Socket.prototype.connect=deny;}for(const n of ['http','https']){const m=require('node:'+n);m.get=deny;m.request=deny;}globalThis.fetch=deny;\n")
            node = shutil.which('node')
            report.check('real_node_available', bool(node))
            public_key, samples = fixtures(node, guard, work)
            report.data['temporaryTestPublicKeySha256'] = digest(public_key.encode())
            legacy = load_module('online_qa_legacy_builder', args.repo / 'legacy-update/build-update.py')
            product_root = work / 'product'
            product_root.mkdir()
            extract_bundle(args.bundle, product_root)
            report.check('bundle_crc_and_safe_member_paths', True)
            launcher_file = product_root / 'TalosHelper-启动器.exe'
            code = marshal.loads(legacy.Archive.read(launcher_file.read_bytes()).entry('app').content())
            namespace = {'__name__': 'talos_qa_stable_entry', '__file__': str(product_root / 'stable_entry.py')}
            exec(code, namespace)
            config = namespace['_config'](product_root)
            report.check('stable_default_and_config_use_expected_feed', namespace['DEFAULT_FEED_URL'] == args.expected_feed_url and config['feed_url'] == args.expected_feed_url, config['feed_url'])
            report.data['feedUrl'] = config['feed_url']
            pins = namespace['EXPECTED_MODULE_SHA256']
            report.data['launcherModulePins'] = pins
            report.check('stable_entry_pins_exact_core_and_ui', all(digest((product_root / 'talos-updater' / (name + '.py')).read_bytes()) == value for name, value in pins.items()) and set(pins) == {'talos_update_core', 'launcher_ui'})
            modules = namespace['_load_modules'](product_root)
            core, ui = modules['talos_update_core'], modules['launcher_ui']
            report.check('loaded_modules_carry_verified_hash', all(getattr(modules[name], '__talos_verified_sha256__', None) == value for name, value in pins.items()))
            for name in pins:
                filename = product_root / 'talos-updater' / (name + '.py')
                old = filename.read_bytes()
                filename.write_bytes(b"raise AssertionError('TAMPER_EXECUTED')\n")
                report.rejected('stable_entry_rejects_tampered_' + name, lambda: namespace['_load_modules'](product_root), RuntimeError)
                filename.write_bytes(old)
            run_arguments = []
            original_run = ui.run
            ui.run = lambda root, key, config=None: run_arguments.append((root, key, config))
            try:
                report.check('stable_entry_forwards_only_local_ui', namespace['main']() == 0 and len(run_arguments) == 1 and run_arguments[0][0] == product_root and run_arguments[0][1] == namespace['PUBLIC_KEY_PEM'])
            finally:
                ui.run = original_run
            copy_base(args.original_dir, product_root, core.BASE_DEPENDENCIES)
            product_node = make_node_wrapper(product_root, node, guard)
            product_network = Transport({})
            product_updater = core.Updater(product_root, namespace['PUBLIC_KEY_PEM'], node_path=product_node, transport=product_network)
            actual = product_updater.status()
            report.check('actual_signed_bootstrap_installs_offline', actual['active'] == args.version and not product_network.calls, actual)
            report.data['productPublicKeySha256'] = digest(namespace['PUBLIC_KEY_PEM'].encode())
            report.check('stable_public_key_matches_published_public_key', namespace['PUBLIC_KEY_PEM'].encode() == (args.repo / 'online-update/update-public-key.pem').read_bytes())
            report.data['bootstrapPayloadSha256'] = digest((product_root / 'talos-updater/bootstrap/package.zip').read_bytes())
            report.check('actual_product_bootstrap_files_complete', all((product_root / info['entry']).is_file() for info in actual['installed'].values()))
            migration = None
            if args.previous_bundle is not None:
                report.check('original_signed_migration_bundle_exists', args.previous_bundle.is_file(), str(args.previous_bundle))
                report.data['migrationFromBundleSha256'] = digest(args.previous_bundle.read_bytes())
                migration_root = work / 'actual-previous-installation'
                extract_bundle(args.previous_bundle, migration_root)
                old_entry = marshal.loads(legacy.Archive.read((migration_root / 'TalosHelper-启动器.exe').read_bytes()).entry('app').content())
                previous = {'__name__': 'talos_qa_previous_entry', '__file__': str(migration_root / 'previous_entry.py')}
                exec(old_entry, previous)
                report.check('migration_preserves_original_key_core_and_ui', previous['PUBLIC_KEY_PEM'] == namespace['PUBLIC_KEY_PEM'] and previous['EXPECTED_MODULE_SHA256'] == pins)
                old_config = previous['_config'](migration_root)
                copy_base(args.original_dir, migration_root, core.BASE_DEPENDENCIES)
                migration_node = make_node_wrapper(migration_root, node, guard)
                migration_transport = Transport({})
                holds.append(migration_transport)
                previous_updater = core.Updater(migration_root, previous['PUBLIC_KEY_PEM'], old_config['feed_url'], node_path=migration_node, transport=migration_transport)
                old_status = previous_updater.status()
                report.check('actual_previous_bootstrap_installs_032_offline', old_status['active'] == '0.3.2' and not migration_transport.calls)
                migration_state_file = migration_root / 'talos-update-state.json'
                old_state_bytes = migration_state_file.read_bytes()
                old_executable = migration_root / old_status['installed']['0.3.2']['entry']
                old_executable_sha = digest(old_executable.read_bytes())
                extract_bundle(args.bundle, migration_root)
                migrated_entry = marshal.loads(legacy.Archive.read((migration_root / 'TalosHelper-启动器.exe').read_bytes()).entry('app').content())
                migrated = {'__name__': 'talos_qa_migrated_entry', '__file__': str(migration_root / 'migrated_entry.py')}
                exec(migrated_entry, migrated)
                migrated_modules = migrated['_load_modules'](migration_root)
                migrated_config = migrated['_config'](migration_root)
                report.check('overlay_changes_feed_without_changing_trusted_modules', migrated['PUBLIC_KEY_PEM'] == previous['PUBLIC_KEY_PEM'] and migrated['EXPECTED_MODULE_SHA256'] == previous['EXPECTED_MODULE_SHA256'] and migrated_modules['talos_update_core'] is core and migrated_config['feed_url'] == args.expected_feed_url and migrated_config['feed_url'] != old_config['feed_url'])
                new_feed = (migration_root / 'talos-updater/bootstrap/latest.json').read_bytes()
                new_manifest = json.loads(base64.b64decode(json.loads(new_feed)['payload']))
                report.check('actual_release_manifest_has_target_version_and_payload_url', new_manifest['version'] == args.version and (args.expected_package_url is None or new_manifest['package']['url'] == args.expected_package_url), {'version': new_manifest['version'], 'packageUrl': new_manifest['package']['url']})
                migration_transport.mapping.update({migrated_config['feed_url']: new_feed, new_manifest['package']['url']: (migration_root / 'talos-updater/bootstrap/package.zip').read_bytes()})
                migration_updater = core.Updater(migration_root, migrated['PUBLIC_KEY_PEM'], migrated_config['feed_url'], node_path=migration_node, transport=migration_transport)
                report.check('new_launcher_does_not_auto_upgrade_existing_032', migration_updater.status()['active'] == '0.3.2' and migration_state_file.read_bytes() == old_state_bytes and not migration_transport.calls)
                migration = {'root': migration_root, 'node': migration_node, 'transport': migration_transport, 'config': migrated_config, 'old_executable': old_executable, 'old_executable_sha': old_executable_sha}
                report.data['migrationOldFeedUrl'] = old_config['feed_url']
                report.data['migrationPackageUrl'] = new_manifest['package']['url']
            # Use the actual pinned core with a temporary signing identity for the
            # update/rollback transaction flow; no product signing key is used.
            transaction_root = work / 'transactions'
            copy_base(args.original_dir, transaction_root, core.BASE_DEPENDENCIES)
            transaction_node = make_node_wrapper(transaction_root, node, guard)
            bootstrap(transaction_root, samples['0.3.2'])
            feed_url = 'https://updates.example.test/latest.json'
            mapping = {feed_url: samples['0.3.3']['feed']}
            mapping.update({value['manifest']['package']['url']: value['archive'] for value in samples.values()})
            transport = Transport(mapping)
            holds.append(transport)
            updater = core.Updater(transaction_root, public_key, feed_url, node_path=transaction_node, transport=transport)
            first = updater.status()
            report.check('temporary_signed_bootstrap_has_zero_transport', first['active'] == '0.3.2' and not transport.calls)
            updater.status()
            launched = updater.launch_active()
            report.check('local_status_and_launch_do_not_connect', not transport.calls and launched['version'] == '0.3.2' and process_launches[-1]['command'][0] == str(transaction_root / 'TalosHelper-App-0.3.2.exe'))
            manifest = updater.check()
            report.check('explicit_check_only_fetches_feed', len(transport.calls) == 1 and transport.calls[-1]['url'] == feed_url and manifest['version'] == '0.3.3' and updater.status()['active'] == '0.3.2')
            signed_feed = mapping[feed_url]
            invalid_feed = json.loads(signed_feed)
            signature = bytearray(base64.b64decode(invalid_feed['signature']))
            signature[0] ^= 1
            invalid_feed['signature'] = base64.b64encode(signature).decode()
            mapping[feed_url] = json.dumps(invalid_feed).encode()
            report.rejected('real_crypto_rejects_invalid_feed_signature', updater.check, core.AuthenticationError)
            mapping[feed_url] = signed_feed
            modified = copy.deepcopy(manifest)
            modified['notes'].append('not authenticated')
            before = len(transport.calls)
            report.rejected('modified_manifest_cannot_bypass_signature', lambda: updater.install(modified), core.AuthenticationError)
            report.check('unauthenticated_install_never_downloads', len(transport.calls) == before)
            installed = updater.install(manifest)
            upgraded = updater.status()
            report.check('explicit_install_downloads_then_atomically_activates', installed['active'] == '0.3.3' and upgraded['active'] == '0.3.3' and upgraded['history'][0] == '0.3.2' and len(transport.calls) == before + 1)
            report.check('per_version_files_are_isolated', (transaction_root / 'releases/0.3.2/talos_game_update.py').is_file() and (transaction_root / 'releases/0.3.3/talos_game_update.py').is_file() and not (transaction_root / 'talos_game_update.py').exists())
            for version in ('0.3.2', '0.3.3'):
                report.check('pinned_base_copied_locally_' + version, all(digest((transaction_root / 'releases' / version / name).read_bytes()) == spec[1] for name, spec in core.BASE_DEPENDENCIES.items()))
            network_before = len(transport.calls)
            rolled = updater.rollback()
            report.check('rollback_is_local_and_retains_newer_files', rolled['active'] == '0.3.2' and len(transport.calls) == network_before and (transaction_root / 'TalosHelper-App-0.3.3.exe').is_file())
            state_file = transaction_root / 'talos-update-state.json'
            committed = state_file.read_bytes()
            def select(version):
                mapping[feed_url] = samples[version]['feed']
                return updater.check()
            candidate = select('0.3.4')
            package_url = candidate['package']['url']
            transport.fail.add(package_url)
            report.rejected('download_failure_is_reported', lambda: updater.install(candidate), core.NetworkError)
            transport.fail.clear()
            report.check('download_failure_keeps_active_state_bytes', state_file.read_bytes() == committed)
            expected_archive = mapping[package_url]
            mapping[package_url] = bytes([expected_archive[0] ^ 1]) + expected_archive[1:]
            report.rejected('package_hash_failure_is_reported', lambda: updater.install(candidate), core.ValidationError)
            mapping[package_url] = expected_archive
            report.check('hash_failure_keeps_active_state_bytes', state_file.read_bytes() == committed)
            unsafe = select('0.3.5')
            report.rejected('signed_unsafe_zip_is_rejected', lambda: updater.install(unsafe), core.ValidationError)
            report.check('unsafe_zip_does_not_activate_or_escape', state_file.read_bytes() == committed and not (transaction_root.parent / 'must-not-exist.txt').exists())
            cancelled = select('0.3.6')
            stages = []
            def cancel_at_activate(progress):
                stages.append(progress['stage'])
                return progress['stage'] != 'activate'
            report.rejected('cancel_before_activation_is_reported', lambda: updater.install(cancelled, progress=cancel_at_activate), core.UpdateCancelled)
            report.check('cancelled_install_cleans_and_keeps_active', 'activate' in stages and state_file.read_bytes() == committed and not (transaction_root / 'TalosHelper-App-0.3.6.exe').exists() and not (transaction_root / 'releases/0.3.6').exists())
            select_version = '0.3.7'
            mapping[feed_url] = samples[select_version]['feed']
            report.rejected('incompatible_signed_release_is_rejected', updater.check, core.ValidationError)
            report.check('all_failed_updates_preserve_active', state_file.read_bytes() == committed and updater.status()['active'] == '0.3.2')
            # The lock inode intentionally persists; the OS lock must release.
            with updater._lock():
                report.check('transaction_lock_released_and_staging_cleaned', not any(transaction_root.glob('.talos-stage-*')))
            # A hard process loss bypasses ordinary Exception cleanup. Reuse
            # only complete matching orphan files, and never activate early.
            recovery_root = work / 'interrupted-installation'
            copy_base(args.original_dir, recovery_root, core.BASE_DEPENDENCIES)
            recovery_node = make_node_wrapper(recovery_root, node, guard)
            bootstrap(recovery_root, samples['0.3.2'])
            recovery_transport = Transport({feed_url: samples['0.3.3']['feed'], samples['0.3.3']['manifest']['package']['url']: samples['0.3.3']['archive']})
            recovery = core.Updater(recovery_root, public_key, feed_url, node_path=recovery_node, transport=recovery_transport)
            recovery.status()
            recovery_state = recovery_root / 'talos-update-state.json'
            original_state = recovery_state.read_bytes()
            recovery_manifest = recovery.check()
            publish = recovery._publish
            published = []
            class HardProcessLoss(BaseException):
                pass
            def cut_after_two(source, target, created):
                publish(source, target, created)
                published.append(target)
                if len(published) == 2:
                    raise HardProcessLoss()
            recovery._publish = cut_after_two
            try:
                report.rejected('hard_interruption_exercised_after_partial_publish', lambda: recovery.install(recovery_manifest), HardProcessLoss)
            finally:
                recovery._publish = publish
            report.check('interruption_keeps_old_active_and_state_bytes', recovery_state.read_bytes() == original_state and recovery.status()['active'] == '0.3.2' and len(published) == 2)
            report.check('matching_partial_orphan_recovered_on_retry', recovery.install(recovery_manifest)['active'] == '0.3.3' and recovery.status()['active'] == '0.3.3')
            old_helper = recovery_root / 'releases/0.3.2/talos_game_update.py'
            healthy_old_helper = old_helper.read_bytes()
            old_helper.write_bytes(b'QA intentionally damaged historical release\n')
            recovery_before = len(recovery_transport.calls)
            recovery_status = recovery.status()
            report.check('damaged_history_does_not_block_valid_active', recovery_status['active'] == '0.3.3' and '0.3.2' not in recovery_status['installed'] and recovery_status['unavailable'][0]['version'] == '0.3.2')
            report.rejected('damaged_history_cannot_be_rollback_target', lambda: recovery.rollback('0.3.2'), core.ValidationError)
            report.check('valid_active_launch_with_bad_history_is_offline', recovery.launch_active()['version'] == '0.3.3' and len(recovery_transport.calls) == recovery_before)
            old_helper.write_bytes(healthy_old_helper)
            (recovery_root / 'releases/0.3.3/talos_game_update.py').write_bytes(b'QA intentionally damaged active release\n')
            damaged_active = recovery.status()
            report.check('damaged_active_status_preserves_healthy_rollback_options', damaged_active['active'] == '0.3.3' and damaged_active['active_valid'] is False and '0.3.2' in damaged_active['installed'] and damaged_active['unavailable'][0]['version'] == '0.3.3')
            launches_before = len(process_launches)
            report.rejected('damaged_active_is_never_launched', recovery.launch_active, core.ValidationError)
            report.check('damaged_active_recovers_by_offline_rollback', recovery.rollback()['active'] == '0.3.2' and len(process_launches) == launches_before and len(recovery_transport.calls) == recovery_before)
            # Real Tk launcher with the real core and a simulated transport.
            import tkinter as tk
            from tkinter import font, messagebox
            from PIL import Image, ImageGrab
            original_set = tk.Variable.set
            def observe_set(variable, value):
                if threading.get_ident() != owner:
                    foreign_writes.append(str(value)[:100])
                return original_set(variable, value)
            tk.Variable.set = observe_set
            messagebox.showerror = lambda *values, **options: tk_errors.append('Unexpected modal: ' + str(values))
            ui_root_dir = work / 'ui-installation'
            copy_base(args.original_dir, ui_root_dir, core.BASE_DEPENDENCIES)
            ui_node = make_node_wrapper(ui_root_dir, node, guard)
            bootstrap(ui_root_dir, samples['0.3.2'])
            ui_transport = Transport({feed_url: samples['0.3.3']['feed'], samples['0.3.3']['manifest']['package']['url']: samples['0.3.3']['archive']})
            holds.append(ui_transport)
            def factory(root, key, feed_url=''):
                return core.Updater(root, key, feed_url, node_path=ui_node, transport=ui_transport)
            controller = ui.LauncherController(ui_root_dir, public_key, feed_url, updater_factory=factory)
            window = tk.Tk()
            roots.append(window)
            report.data['tkVersion'] = window.tk.call('info', 'patchlevel')
            report.data['screenshotLimitations'] = 'Linux X11/Tk with task-local Unifont fallback; layout evidence only, not Windows rendering or real-game OCR accuracy.'
            window.report_callback_exception = lambda *error: tk_errors.append(''.join(traceback.format_exception(*error)))
            launcher = ui.Launcher(window, ui_root_dir, public_key, config={'feed_url': feed_url}, controller=controller)
            pump(window, lambda: not controller.busy and launcher.local_status.get('active') == '0.3.2')
            report.check('launcher_initialization_and_refresh_are_offline', not ui_transport.calls and '0.3.2' in launcher.version_text.get())
            launcher.refresh_button.invoke()
            pump(window, lambda: not controller.busy)
            report.check('refresh_local_state_has_zero_transport', not ui_transport.calls)
            launcher.launch_button.invoke()
            pump(window, lambda: not controller.busy)
            report.check('launcher_launch_button_is_offline', not ui_transport.calls and '0.3.2' in launcher.status_text.get())
            launcher.check_button.invoke()
            pump(window, lambda: not controller.busy and launcher.manifest is not None)
            report.check('check_button_fetches_feed_and_displays_verified_notes', len(ui_transport.calls) == 1 and launcher.manifest['version'] == '0.3.3' and 'QA 临时签名版本' in launcher.notes.get('1.0', 'end') and bool(launcher.size_text.get()))
            args.artifacts_dir.mkdir(parents=True, exist_ok=True)
            window.update_idletasks()
            ImageGrab.grab(bbox=(window.winfo_rootx(), window.winfo_rooty(), window.winfo_rootx() + window.winfo_width(), window.winfo_rooty() + window.winfo_height()), xdisplay=os.environ.get('DISPLAY')).save(args.artifacts_dir / 'talos-online-launcher.png')
            report.data['launcherScreenshot'] = str(args.artifacts_dir / 'talos-online-launcher.png')
            launcher.install_button.invoke()
            pump(window, lambda: not controller.busy and launcher.local_status.get('active') == '0.3.3')
            report.check('download_button_upgrades_through_real_core', len(ui_transport.calls) == 2 and '0.3.3' in launcher.version_text.get())
            launcher.rollback_button.invoke()
            pump(window, lambda: not controller.busy and launcher.local_status.get('active') == '0.3.2')
            report.check('rollback_button_never_connects', len(ui_transport.calls) == 2)
            ui_transport.hold = True
            ui_transport.entered.clear()
            ui_transport.release.clear()
            launcher.check_button.invoke()
            pump(window, ui_transport.entered.is_set)
            launcher.cancel_button.invoke()
            ui_transport.release.set()
            pump(window, lambda: not controller.busy)
            report.check('cancelled_check_is_not_applied', '取消' in launcher.status_text.get() and launcher.local_status['active'] == '0.3.2' and len(ui_transport.calls) == 3)
            (ui_root_dir / 'releases/0.3.2/talos_game_update.py').write_bytes(b'QA damaged active for launcher recovery\n')
            launcher.refresh_button.invoke()
            pump(window, lambda: not controller.busy)
            report.check('launcher_disables_damaged_active_and_offers_healthy_rollback', launcher.local_status['active_valid'] is False and str(launcher.launch_button['state']) == 'disabled' and str(launcher.rollback_button['state']) == 'normal')
            launcher.rollback_button.invoke()
            pump(window, lambda: not controller.busy and launcher.local_status.get('active') == '0.3.3')
            report.check('launcher_recovers_damaged_active_without_network', len(ui_transport.calls) == 3 and launcher.local_status['active_valid'] is True and str(launcher.launch_button['state']) == 'normal')
            # Multiple Tk interpreters are used consecutively only by this
            # harness. Clear their timers before destroying the first root.
            for timer in window.tk.call('after', 'info'):
                window.after_cancel(timer)
            launcher.close()
            if migration is not None:
                def migrated_factory(root, key, feed_url=''):
                    return core.Updater(root, key, feed_url, node_path=migration['node'], transport=migration['transport'])
                migrated_controller = ui.LauncherController(migration['root'], namespace['PUBLIC_KEY_PEM'], migration['config']['feed_url'], updater_factory=migrated_factory)
                migrated_window = tk.Tk()
                roots.append(migrated_window)
                migrated_window.report_callback_exception = lambda *error: tk_errors.append(''.join(traceback.format_exception(*error)))
                migrated_launcher = ui.Launcher(migrated_window, migration['root'], namespace['PUBLIC_KEY_PEM'], config=migration['config'], controller=migrated_controller)
                pump(migrated_window, lambda: not migrated_controller.busy and migrated_launcher.local_status.get('active') == '0.3.2')
                report.check('migrated_real_launcher_starts_on_032_without_network', not migration['transport'].calls and '0.3.2' in migrated_launcher.version_text.get())
                migrated_launcher.check_button.invoke()
                pump(migrated_window, lambda: not migrated_controller.busy and migrated_launcher.manifest is not None)
                report.check('migrated_check_uses_new_feed_and_real_signature', len(migration['transport'].calls) == 1 and migration['transport'].calls[0]['url'] == args.expected_feed_url and migrated_launcher.manifest['version'] == args.version)
                migrated_launcher.install_button.invoke()
                pump(migrated_window, lambda: not migrated_controller.busy and migrated_launcher.local_status.get('active') == args.version)
                report.check('migrated_download_installs_real_signed_release', len(migration['transport'].calls) == 2 and migrated_launcher.local_status['history'][0] == '0.3.2' and migrated_launcher.local_status['active_valid'] is True and digest(migration['old_executable'].read_bytes()) == migration['old_executable_sha'])
                migrated_launcher.rollback_button.invoke()
                pump(migrated_window, lambda: not migrated_controller.busy and migrated_launcher.local_status.get('active') == '0.3.2')
                migrated_launcher.launch_button.invoke()
                pump(migrated_window, lambda: not migrated_controller.busy)
                report.check('migrated_rollback_and_launch_keep_032_offline', len(migration['transport'].calls) == 2 and process_launches[-1]['command'] == [str(migration['old_executable'])] and digest(migration['old_executable'].read_bytes()) == migration['old_executable_sha'])
                report.data['migrationTransportCalls'] = migration['transport'].calls
                for timer in migrated_window.tk.call('after', 'info'):
                    migrated_window.after_cancel(timer)
                migrated_launcher.close()
            # Exercise the actual version-specific app code and helper against
            # the product's signed, locally installed release.
            engine = load_module('engine', args.extracted_dir / 'engine.pyc')
            vision = load_module('vision', args.extracted_dir / 'vision.pyc')
            vision.ROOT = product_root
            app_exe = product_root / actual['installed'][actual['active']]['entry']
            app_code = marshal.loads(legacy.Archive.read(app_exe.read_bytes()).entry('app').content())
            app_module = types.ModuleType('talos_online_qa_game')
            app_module.__file__ = str(product_root / 'game_entry.py')
            sys.modules[app_module.__name__] = app_module
            exec(app_code, app_module.__dict__)
            app_module.recognize = lambda image: (_ for _ in ()).throw(ValueError('QA controlled recognition failure'))
            game_window = tk.Tk()
            roots.append(game_window)
            game_window.report_callback_exception = lambda *error: tk_errors.append(''.join(traceback.format_exception(*error)))
            app = app_module.App(game_window)
            report.check('actual_app_release_and_base_roots_are_separate', Path(app._talos_install_root) == product_root / 'releases' / args.version and Path(app._talos_base_root) == product_root)
            game_state = app._talos_game_update
            app.calculate()
            dialog = game_state['dialog']
            helper_prefix = '_talos_release_' + args.version.replace('.', '_') + '_'
            helper_name = next(name for name, value in sys.modules.items() if name.startswith(helper_prefix) and getattr(value, '__talos_verified_sha256__', None))
            helper = sys.modules[helper_name]
            for n, row in enumerate(dialog.cell_vars):
                row['kind'].set(helper.KINDS['office' if n == 0 else 'locked'])
                row['event'].set('无')
                row['destination'].set(False)
                row['ivory'].set(False)
                row['scratch'].set(False)
            for n, beast, tier in [(2, 'lion', 3), (4, 'wolf', 3), (7, 'rat', 4)]:
                row = dialog.cell_vars[n]
                row['kind'].set(helper.KINDS['inmate'])
                row['beast'].set(helper.BEASTS[beast])
                row['tier'].set(helper.TIERS[tier])
            dialog.position.set(2)
            values = {'day': '12', 'phase': helper.PHASES['manage'], 'objective': helper.OBJECTIVES['other'], 'objectiveProgress': '0', 'vault': '0', 'usurpStage': dialog.usurp_names['1'], 'eventKind': '未确认', 'chainConsumed': '0', 'resumePhase': '未确认', 'startEvent': '普通20天开局'}
            for name, variable in dialog.vars.items():
                variable.set(values[name] if name in values else False)
            dialog.tools.selection_clear(0, 'end')
            dialog.offers.selection_clear(0, 'end')
            dialog.confirmed.set(True)
            dialog.submit_button.invoke()
            pump(game_window, lambda: not game_state['busy'] and '建议：' in app.output.get('1.0', 'end'))
            game_text = app.output.get('1.0', 'end')
            report.check('actual_game_advice_uses_signed_isolated_backend_offline', '修正规则基线' in game_text and not product_network.calls and not guard_log.exists(), game_text[:300])
            app.analyze(Image.new('RGB', (640, 360), 'black'))
            pump(game_window, lambda: not app.busy and '识别失败' in app.status.get())
            report.check('game_recognition_failure_remains_offline_and_expires_advice', '建议：' not in app.output.get('1.0', 'end') and not product_network.calls and not guard_log.exists())
            game_state['update_button'].invoke()
            report.check('game_update_button_only_opens_local_launcher', process_launches[-1]['command'] == [str(product_root / 'TalosHelper-启动器.exe'), '--updates'] and not product_network.calls and not guard_log.exists())
            app.close()
            tk.Variable.set = original_set
            report.check('no_worker_tk_variable_writes', not foreign_writes, foreign_writes)
            report.check('no_thread_or_tk_callback_errors', not worker_errors and not tk_errors, {'workers': worker_errors, 'tk': tk_errors})
            report.check('no_real_network_attempts', not network_attempts and not guard_log.exists(), network_attempts)
            report.check('windows_launches_are_argument_lists_without_shell', bool(process_launches) and all(not item['shell'] and isinstance(item['command'], list) for item in process_launches))
            report.check('original_user_executable_is_unchanged', digest((args.original_dir / 'TalosHelper.exe').read_bytes()) == original_exe_hash)
            report.data['simulatedTransportCalls'] = transport.calls
            report.data['uiTransportCalls'] = ui_transport.calls
            report.data['observedWindowsLaunchRequests'] = process_launches
            report.data['gameSourceHash'] = digest((product_root / 'releases' / args.version / 'training/game-runtime.cjs').read_bytes())
            report.data['passed'] = True
    except BaseException:
        report.data['errors'].append(traceback.format_exc())
    finally:
        for transport in holds:
            transport.release.set()
        for root in roots:
            try:
                root.destroy()
            except Exception:
                pass
        subprocess.Popen = original_popen
        for name, value in original_socket.items():
            setattr(socket.socket, name, value)
        threading.excepthook = original_thread_hook
        report.data['workerErrors'] = worker_errors
        report.data['tkErrors'] = tk_errors
        report.data['networkAttempts'] = network_attempts
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(json.dumps(report.data, ensure_ascii=False, indent=2) + '\n')
    print(json.dumps({'passed': report.data['passed'], 'checks': len(report.data['checks']), 'output': str(args.output), 'errors': report.data['errors']}, ensure_ascii=False))
    return 0 if report.data['passed'] else 1


if __name__ == '__main__':
    raise SystemExit(main())
