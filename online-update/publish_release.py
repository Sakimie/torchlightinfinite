#!/usr/bin/env python3
"""Publish reviewed assets through normal gh authentication; never read tokens.

The public repository must exist and the connected GitHub app must have access.
Run only after explicit authorization to publish the public update channel.
"""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import tempfile

HERE = Path(__file__).resolve().parent
REPO = 'Sakimie/torchlightinfinite'


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def command(args, stdout=None):
    result = subprocess.run(args, stdout=stdout or subprocess.PIPE,
                            stderr=subprocess.PIPE, check=False)
    if result.returncode:
        raise RuntimeError(result.stderr.decode('utf-8', 'replace').strip()[-1800:])
    return result.stdout


def api(endpoint, *, method='GET', value=None, binary_to=None):
    args = ['gh', 'api', endpoint, '--method', method,
            '-H', 'X-GitHub-Api-Version: 2022-11-28']
    if binary_to:
        args += ['-H', 'Accept: application/octet-stream']
        with binary_to.open('wb') as output:
            command(args, stdout=output)
        return
    if value is not None:
        with tempfile.TemporaryDirectory(prefix='talos-publish-api-') as tmp:
            request = Path(tmp) / 'request.json'
            request.write_text(json.dumps(value, ensure_ascii=False), encoding='utf-8')
            return json.loads(command(args + ['--input', str(request)]))
    return json.loads(command(args))


def publish(directory, make_public):
    report = json.loads((directory / 'release-build-report.json').read_text('utf-8'))
    version = report['version']; tag = 'v' + version
    paths = [directory / item['file'] for item in report['artifacts']]
    paths.append(directory / 'SHA256SUMS.txt')
    expected = {item['file']: item for item in report['artifacts']}
    for path in paths:
        if path.name in expected:
            item = expected[path.name]
            if path.stat().st_size != item['bytes'] or digest(path) != item['sha256']:
                raise RuntimeError('Local reviewed artifact changed: ' + path.name)
    lines = (directory / 'SHA256SUMS.txt').read_text('utf-8').splitlines()
    if {line.split('  ', 1)[1]: line.split('  ', 1)[0] for line in lines} != {
            item['file']: item['sha256'] for item in report['artifacts']}:
        raise RuntimeError('Checksum list does not match reviewed assets')
    # Visibility was changed by the user. The publisher never changes it.
    repository = api('repos/' + REPO)
    if repository['full_name'].casefold() != REPO.casefold() or repository['private']:
        raise RuntimeError('Expected the authorized public update repository')
    releases = api('repos/' + REPO + '/releases?per_page=100')
    matching = [release for release in releases if release['tag_name'] == tag]
    if len(matching) > 1:
        raise RuntimeError('Ambiguous release tag')
    if matching:
        release = matching[0]
    else:
        release = api('repos/' + REPO + '/releases', method='POST', value={
            'tag_name': tag, 'target_commitish': repository['default_branch'],
            'name': 'TalosHelper ' + version + '（试用版）',
            'body': (HERE / ('RELEASE-NOTES-' + version + '.md')).read_text('utf-8'),
            'draft': True, 'prerelease': False,
        })
    remote = {asset['name']: asset for asset in release['assets']}
    for path in paths:
        asset = remote.get(path.name)
        same = False
        if asset:
            with tempfile.TemporaryDirectory(prefix='talos-asset-check-') as tmp:
                downloaded = Path(tmp) / 'asset'
                api('repos/' + REPO + '/releases/assets/' + str(asset['id']), binary_to=downloaded)
                same = digest(downloaded) == digest(path)
        if same:
            continue
        if asset and not release['draft']:
            raise RuntimeError('Never replace a different asset in a published release: ' + path.name)
        args = ['gh', 'release', 'upload', tag, str(path), '--repo', REPO]
        if asset:
            args.append('--clobber')
        command(args)
    release = api('repos/' + REPO + '/releases/' + str(release['id']))
    remote = {asset['name']: asset for asset in release['assets']}
    for path in paths:
        if path.name not in remote:
            raise RuntimeError('Missing uploaded asset: ' + path.name)
        with tempfile.TemporaryDirectory(prefix='talos-upload-verify-') as tmp:
            downloaded = Path(tmp) / 'asset'
            api('repos/' + REPO + '/releases/assets/' + str(remote[path.name]['id']), binary_to=downloaded)
            if digest(downloaded) != digest(path):
                raise RuntimeError('Uploaded asset hash mismatch: ' + path.name)
    if make_public:
        release = api('repos/' + REPO + '/releases/' + str(release['id']), method='PATCH',
                      value={'draft': False, 'prerelease': False, 'make_latest': 'true'})
    result = {'schema': 'talos-publication-v1', 'repository': REPO, 'version': version,
              'release_url': release['html_url'], 'draft': release['draft'],
              'repository_public': True, 'uploaded_assets_verified': [p.name for p in paths],
              'public_download_verified': False}
    (directory / 'publication-report.json').write_text(
        json.dumps(result, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    return result


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--directory', type=Path, default=HERE.parent / 'dist' / 'online-0.3.3')
    parser.add_argument('--publish', action='store_true', help='Publish verified draft assets as latest')
    args = parser.parse_args()
    print(json.dumps(publish(args.directory.resolve(), args.publish), ensure_ascii=False, indent=2))
