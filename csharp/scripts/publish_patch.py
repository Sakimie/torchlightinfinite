#!/usr/bin/env python3
"""Stage and publish a reviewed application update without replacing its host."""
import argparse
import json
from pathlib import Path
import re
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'online-update'))
sys.path.insert(0, str(ROOT / 'csharp/scripts'))
from publish_release import api, digest
from publish_git_channel import commit_files, download_check, RAW
from publish_preview import old_channel_unchanged, FEED, REPO


def save(path, value):
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + '\n', encoding='utf8')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('action', choices=['stage', 'publish'])
    parser.add_argument('--version', required=True)
    args = parser.parse_args()
    version = args.version
    if not re.fullmatch(r'\d+\.\d+\.\d+', version):
        parser.error('Expected numeric major.minor.patch')
    out = ROOT / 'dist' / ('csharp-' + version)
    report = json.loads((out / 'payload-report.json').read_text())
    package = out / report['file']
    if report['version'] != version or digest(package) != report['sha256'] or package.stat().st_size != report['bytes']:
        raise RuntimeError('Reviewed package changed')
    if (out / 'publication-report.json').exists():
        raise RuntimeError('This version has already been published')
    old_channel_unchanged()
    stage_path = out / 'stage-receipt.json'
    if args.action == 'stage':
        if stage_path.exists():
            raise RuntimeError('Package already staged; never replace a version identity')
        name = 'csharp/releases/' + version + '/' + package.name
        commit = commit_files({name: package}, 'Stage C# ' + version + ' application update')
        url = RAW + commit + '/' + name
        download_check(url, package)
        result = {'version': version, 'commit': commit, 'packageUrl': url, 'payloadSha256': digest(package)}
        save(stage_path, result)
        print(json.dumps(result, ensure_ascii=False, indent=2))
        return
    stage = json.loads(stage_path.read_text())
    manifest = json.loads((out / 'manifest.json').read_text())
    if manifest['version'] != version or manifest['package']['url'] != stage['packageUrl'] or stage['payloadSha256'] != digest(package):
        raise RuntimeError('Signed manifest differs from staged package')
    tests = json.loads((out / 'test-results.json').read_text())
    if len(tests) < 47 or any(not item['Passed'] for item in tests):
        raise RuntimeError('Real payload checks have not passed')
    baseline = json.loads((ROOT / 'dist/csharp-0.4.0/payload-report.json').read_text())
    before = {item['path']: item['sha256'] for item in baseline['files']}
    after = {item['path']: item['sha256'] for item in report['files']}
    changed = [name for name in set(before) | set(after) if before.get(name) != after.get(name)]
    if sorted(changed) != ['Talos.App.dll']:
        raise RuntimeError('Window hotfix unexpectedly changed non-UI components: ' + str(changed))
    review_path = ROOT / 'csharp' / ('WINDOW-REVIEW-' + version + '.json')
    if not json.loads(review_path.read_text()).get('passed'):
        raise RuntimeError('Window lifecycle source review has not passed')
    notes_path = ROOT / 'csharp' / ('RELEASE-NOTES-' + version + '.md')
    notes = notes_path.read_text('utf8')
    (out / 'SHA256SUMS.txt').write_text(''.join(f'{digest(out/name)}  {name}\n' for name in [package.name, 'latest.json']), encoding='utf8')
    files = {'csharp/latest.json': out / 'latest.json',
             'csharp/releases/' + version + '/SHA256SUMS.txt': out / 'SHA256SUMS.txt',
             'csharp/releases/' + version + '/TEST-RESULTS.json': out / 'test-results.json',
             'csharp/releases/' + version + '/WINDOW-REVIEW.json': review_path,
             'csharp/RELEASE-NOTES-' + version + '.md': notes_path,
             'csharp/README.md': ROOT / 'csharp/README.md'}
    commit = commit_files(files, 'Publish C# ' + version + ' window fixes')
    download_check(FEED, out / 'latest.json')
    download_check(stage['packageUrl'], package)
    live = subprocess.run(['/workspace/.dotnet/dotnet', 'run', '--project',
                           'scripts/VerifyChannel/VerifyChannel.csproj', '-c', 'Release', '--',
                           FEED, str(ROOT / 'csharp/update-public-key.pem'), version,
                           str(ROOT / 'dist/csharp-0.4.0/latest.json'),
                           str(ROOT / 'dist/csharp-0.4.0/TalosHelper-CSharp-0.4.0-update.zip')],
                          cwd=ROOT / 'csharp', capture_output=True, text=True)
    if live.returncode:
        raise RuntimeError('Native upgrade verification failed: ' + live.stdout + live.stderr)
    live_report = json.loads(live.stdout[live.stdout.index('{'):])
    save(out / 'live-channel-report.json', live_report)
    verified = commit_files({'csharp/releases/' + version + '/LIVE-CHANNEL-REPORT.json': out / 'live-channel-report.json'},
                            'Record native C# ' + version + ' upgrade and rollback verification')
    body = notes + '\n\n' + f'[更新通道验证]({RAW + verified}/csharp/releases/{version}/LIVE-CHANNEL-REPORT.json)\n'
    tag = 'v' + version + '-csharp-preview'
    if any(item['tag_name'] == tag for item in api('repos/' + REPO + '/releases?per_page=100')):
        raise RuntimeError('Release tag already exists; do not replace published versions')
    release = api('repos/' + REPO + '/releases', method='POST', value={
        'tag_name': tag, 'target_commitish': commit, 'name': 'TalosHelper ' + version + ' C# 测试版',
        'body': body, 'draft': False, 'prerelease': True, 'make_latest': 'false'})
    old_channel_unchanged()
    result = {'passed': True, 'version': version, 'releaseUrl': release['html_url'], 'feedUrl': FEED,
              'packageUrl': stage['packageUrl'], 'bytes': report['bytes'], 'sha256': report['sha256'],
              'feedSha256': digest(out/'latest.json'), 'commit': commit, 'verificationCommit': verified,
              'onlyUiAssemblyChanged': True, 'baselineVersion': '0.4.0', 'testCount': len(tests),
              'nativeChannelVerified': live_report['passed'], 'pythonStableUnchanged': True,
              'windowsExeExecuted': False}
    save(out / 'publication-report.json', result)
    print(json.dumps(result, ensure_ascii=False, indent=2))


if __name__ == '__main__':
    main()
