#!/usr/bin/env python3
"""Developer-only live HTTPS verification; never executed by the game helper."""
from __future__ import annotations
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import tempfile
from unittest import mock
import urllib.request
import zipfile

from talos_update_core import BASE_DEPENDENCIES, Updater

HERE = Path(__file__).resolve().parent


def verify(directory, original, legacy):
    publication = json.loads((directory / 'publication-report.json').read_text('utf8'))
    report = {'schema': 'talos-live-public-channel-v1', 'passed': False,
              'realHttpsTransport': True, 'explicitDeveloperVerification': True,
              'windowsExeExecuted': False, 'productPrivateKeyRead': False, 'checks': {}}
    checks = report['checks']
    for item in publication['artifacts']:
        name = item['file']
        key = 'package' if name == f"TalosHelper-{publication['version']}-update.zip" else name
        request = urllib.request.Request(publication['urls'][key], headers={'User-Agent': 'TalosHelper-Public-Verify'})
        with urllib.request.urlopen(request, timeout=40) as response:
            data = response.read(item['bytes'] + 1)
        assert len(data) == item['bytes'] and hashlib.sha256(data).hexdigest() == item['sha256']
        checks['public_https_bytes_' + name] = True
    with tempfile.TemporaryDirectory(prefix='talos-live-update-') as tmp:
        root = Path(tmp)
        for name in BASE_DEPENDENCIES:
            target = root / name; target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(original / name, target)
        updater = Updater(root, (HERE / 'update-public-key.pem').read_text('ascii'),
                          publication['urls']['latest.json'], node_path=shutil.which('node'))
        old_hash = None
        if legacy:
            with zipfile.ZipFile(legacy) as archive:
                feed = root / 'old-feed.json'; package = root / 'old-package.zip'
                feed.write_bytes(archive.read('talos-updater/bootstrap/latest.json'))
                package.write_bytes(archive.read('talos-updater/bootstrap/package.zip'))
            updater.install_local(feed, package)
            old = root / 'TalosHelper-App-0.3.2.exe'
            old_hash = hashlib.sha256(old.read_bytes()).hexdigest()
            assert updater.status()['active'] == '0.3.2'
            checks['old_032_signed_install'] = True
        manifest = updater.check()  # Production HTTPS transport, no injection.
        assert manifest['version'] == publication['version']
        checks['real_https_feed_and_ed25519'] = True
        installed = updater.install(manifest)
        assert installed['active'] == publication['version']
        checks['real_https_download_and_install'] = True
        with mock.patch.object(urllib.request.OpenerDirector, 'open', side_effect=AssertionError('Unexpected network during local operation')):
            assert updater.status()['active_valid'] is True
            checks['local_status_without_https'] = True
            if old_hash:
                assert hashlib.sha256(old.read_bytes()).hexdigest() == old_hash
                updater.rollback('0.3.2')
                assert updater.status()['active'] == '0.3.2'
                checks['offline_rollback_and_old_exe_unchanged'] = True
                updater.rollback(publication['version'])
                assert updater.status()['active'] == publication['version']
                checks['offline_return_to_new_version'] = True
    report['passed'] = True
    report['releaseUrl'] = publication['release_url']
    return report


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--directory', type=Path, default=HERE.parent / 'dist' / 'online-0.3.3')
    parser.add_argument('--original', type=Path, default=Path('/workspace/legacy-talos-v03'))
    parser.add_argument('--legacy', type=Path, default=HERE.parent / 'dist' / 'online-0.3.2' / 'TalosHelper-Online-Update-0.3.2.zip')
    args = parser.parse_args()
    result = verify(args.directory, args.original, args.legacy)
    (HERE / 'public-channel-validation.json').write_text(json.dumps(result, ensure_ascii=False, indent=2) + '\n', encoding='utf8')
    print(json.dumps(result, ensure_ascii=False, indent=2))
