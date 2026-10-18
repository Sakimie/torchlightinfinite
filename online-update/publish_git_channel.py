#!/usr/bin/env python3
"""Publish signed public downloads through ordinary GitHub Git Data APIs.

No tokens/private keys are read. Full commit URLs keep packages immutable, and
every commit remains a parent of later versions. Existing main is untouched.
"""
from __future__ import annotations
import argparse
import base64
import hashlib
import json
from pathlib import Path
import urllib.request

from publish_release import api, digest

HERE = Path(__file__).resolve().parent
REPO = 'Sakimie/torchlightinfinite'
BRANCH = 'talos-updates'
RAW = 'https://raw.githubusercontent.com/' + REPO + '/'


def save(path, value):
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + '\n', encoding='utf8')


def checked(directory):
    report = json.loads((directory / 'release-build-report.json').read_text('utf8'))
    for item in report['artifacts']:
        path = directory / item['file']
        if path.stat().st_size != item['bytes'] or digest(path) != item['sha256']:
            raise RuntimeError('Reviewed asset changed: ' + path.name)
    if api('repos/' + REPO)['private']:
        raise RuntimeError('Authorized repository must be public')
    return report


def current():
    try:
        return api('repos/' + REPO + '/git/ref/heads/' + BRANCH)['object']['sha']
    except RuntimeError as error:
        if 'HTTP 404' not in str(error):
            raise
        return None


def commit_files(files, message):
    parent = current()
    entries = []
    for name, path in files.items():
        print('Uploading public file: ' + name, flush=True)
        blob = api('repos/' + REPO + '/git/blobs', method='POST', value={
            'encoding': 'base64', 'content': base64.b64encode(path.read_bytes()).decode('ascii')})
        entries.append({'path': name, 'mode': '100644', 'type': 'blob', 'sha': blob['sha']})
    request = {'tree': entries}
    if parent:
        request['base_tree'] = api('repos/' + REPO + '/git/commits/' + parent)['tree']['sha']
    tree = api('repos/' + REPO + '/git/trees', method='POST', value=request)
    commit = api('repos/' + REPO + '/git/commits', method='POST', value={
        'message': message, 'tree': tree['sha'], 'parents': [parent] if parent else []})
    if parent:
        api('repos/' + REPO + '/git/refs/heads/' + BRANCH, method='PATCH',
            value={'sha': commit['sha'], 'force': False})
    else:
        api('repos/' + REPO + '/git/refs', method='POST',
            value={'ref': 'refs/heads/' + BRANCH, 'sha': commit['sha']})
    return commit['sha']


def download_check(url, path):
    request = urllib.request.Request(url, headers={'User-Agent': 'TalosHelper-Public-Check'})
    with urllib.request.urlopen(request, timeout=40) as response:
        received = response.read(path.stat().st_size + 1)
    if len(received) != path.stat().st_size or hashlib.sha256(received).hexdigest() != digest(path):
        raise RuntimeError('Public bytes do not match reviewed asset: ' + path.name)


def stage_package(directory):
    report = checked(directory); version = report['version']
    name = f'TalosHelper-{version}-update.zip'
    path = f'releases/{version}/' + name
    commit = commit_files({path: directory / name}, 'Stage signed TalosHelper package ' + version)
    result = {'schema': 'talos-public-channel-stage-v1', 'version': version,
              'commit': commit, 'package_url': RAW + commit + '/' + path,
              'feed_url': RAW + BRANCH + '/latest.json'}
    download_check(result['package_url'], directory / name)
    save(directory / 'channel-stage.json', result)
    return result


def publish(directory):
    report = checked(directory); version = report['version']
    stage = json.loads((directory / 'channel-stage.json').read_text('utf8'))
    manifest = json.loads((directory / 'manifest.json').read_text('utf8'))
    if manifest['package']['url'] != stage['package_url'] or report['plannedFeedUrl'] != stage['feed_url']:
        raise RuntimeError('Rebuild and sign the exact staged immutable package URL before publishing')
    tag = 'v' + version
    releases = api('repos/' + REPO + '/releases?per_page=100')
    matches = [r for r in releases if r['tag_name'] == tag]
    if len(matches) != 1 or not matches[0]['draft']:
        raise RuntimeError('Expected exactly one unpublished release draft')
    name = f'TalosHelper-Online-Update-{version}.zip'
    notes = HERE / ('RELEASE-NOTES-' + version + '.md')
    files = {name: directory / name, 'latest.json': directory / 'latest.json',
             'SHA256SUMS.txt': directory / 'SHA256SUMS.txt',
             'README.md': HERE / 'PUBLIC-README.md',
             'RELEASE-NOTES-' + version + '.md': notes}
    commit = commit_files(files, 'Publish TalosHelper ' + version + ' update channel')
    urls = {name: RAW + commit + '/' + name,
            'latest.json': stage['feed_url'],
            'package': stage['package_url'],
            'SHA256SUMS.txt': RAW + commit + '/SHA256SUMS.txt'}
    for item in report['artifacts']:
        key = 'package' if item['file'] == f'TalosHelper-{version}-update.zip' else item['file']
        download_check(urls[key], directory / item['file'])
    body = notes.read_text('utf8').replace(RAW + BRANCH + '/' + name, urls[name]).replace(
        RAW + BRANCH + '/SHA256SUMS.txt', urls['SHA256SUMS.txt'])
    release = api('repos/' + REPO + '/releases/' + str(matches[0]['id']), method='PATCH', value={
        'body': body, 'draft': False, 'prerelease': False, 'make_latest': 'true'})
    result = {'schema': 'talos-publication-v1', 'repository': REPO, 'version': version,
              'release_url': release['html_url'], 'draft': release['draft'], 'commit': commit,
              'repository_public': True, 'delivery': 'public-git-branch',
              'public_download_verified': True, 'urls': urls,
              'artifacts': report['artifacts']}
    save(directory / 'publication-report.json', result)
    return result


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('action', choices=['stage-package', 'publish'])
    parser.add_argument('--directory', type=Path, default=HERE.parent / 'dist' / 'online-0.3.3')
    args = parser.parse_args()
    operation = stage_package if args.action == 'stage-package' else publish
    print(json.dumps(operation(args.directory.resolve()), ensure_ascii=False, indent=2))
