#!/usr/bin/env python3
"""Publish the authorized portable host after artifact, migration and compatibility checks."""
import json
from pathlib import Path
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'online-update'))
sys.path.insert(0, str(ROOT / 'csharp/scripts'))
from publish_release import api, digest
from publish_git_channel import commit_files, download_check, RAW
from publish_preview import old_channel_unchanged, save, FEED, REPO

OUT = ROOT / 'dist/csharp-0.4.2'
CS = ROOT / 'csharp'
VERSION = '0.4.2'
TAG = 'v0.4.2-csharp-preview'


def main():
    if (OUT / 'publication-report.json').exists():
        raise RuntimeError('Published versions are immutable')
    build = json.loads((OUT / 'build-report.json').read_text())
    for label in ('complete', 'payload'):
        item = build[label]; file = OUT / item['file']
        if file.stat().st_size != item['bytes'] or digest(file) != item['sha256']:
            raise RuntimeError('Reviewed artifact changed: ' + file.name)
    if digest(OUT / 'latest.json') != build['feedSha256']:
        raise RuntimeError('Signed feed changed')
    for name, expected in build['sourceHashes'].items():
        if name.split('/')[0] in ('Talos.App', 'Talos.Core', 'Talos.Storage', 'Talos.Host', 'Talos.Updates') and digest(CS / name) != expected:
            raise RuntimeError('Compiled product source changed: ' + name)
    bundle_path = OUT / 'embedded-check/bundle-inspection.json'
    bundle = json.loads(bundle_path.read_text())
    if not bundle['passed'] or not bundle['noBundleExtractionRequired'] or len(bundle['externalNative']) != 5:
        raise RuntimeError('Portable runtime verification failed')
    if bundle['resources']['Talos.BootstrapFeed'] != build['feedSha256'] or bundle['resources']['Talos.BootstrapPackage'] != build['payload']['sha256']:
        raise RuntimeError('Host embeds different bootstrap bytes')
    for file in (CS/'PORTABLE-REVIEW-0.4.2.json', OUT/'legacy-gate-report.json'):
        if not json.loads(file.read_text())['passed']:
            raise RuntimeError('Independent review failed: ' + file.name)
    tests = json.loads((OUT / 'test-results.json').read_text())
    if len(tests) < 63 or any(not test['Passed'] for test in tests):
        raise RuntimeError('Final signed-payload tests have not passed')
    stage = json.loads((OUT / 'complete-stage-receipt.json').read_text())
    if stage['completeSha256'] != build['complete']['sha256']:
        raise RuntimeError('Staged complete package changed')
    old_channel_unchanged()
    download_check(stage['downloadUrl'], OUT / build['complete']['file'])
    names = [build['complete']['file'], build['payload']['file'], 'latest.json']
    (OUT / 'SHA256SUMS.txt').write_text(''.join(f'{digest(OUT/name)}  {name}\n' for name in names), encoding='utf8')
    notes = (CS/'RELEASE-NOTES-0.4.2.md').read_text('utf8').replace('DOWNLOAD_URL', stage['downloadUrl']).replace('COMPLETE_SHA256', build['complete']['sha256'])
    (OUT/'release-notes.md').write_text(notes, encoding='utf8')
    prefix = 'csharp/releases/' + VERSION + '/'
    files = {'csharp/latest.json': OUT/'latest.json', 'csharp/README.md': CS/'README.md',
             'csharp/RELEASE-NOTES-0.4.2.md': OUT/'release-notes.md',
             prefix+'SHA256SUMS.txt': OUT/'SHA256SUMS.txt',
             prefix+'TEST-RESULTS.json': OUT/'test-results.json',
             prefix+'BUNDLE-REPORT.json': bundle_path,
             prefix+'STORAGE-TEST-REPORT.json': CS/'STORAGE-TEST-REPORT-0.4.2.json',
             prefix+'PORTABLE-REVIEW.json': CS/'PORTABLE-REVIEW-0.4.2.json',
             prefix+'LEGACY-GATE-REPORT.json': OUT/'legacy-gate-report.json'}
    commit = commit_files(files, 'Publish portable C# 0.4.2 and launcher capability requirement')
    download_check(FEED, OUT/'latest.json')
    live = subprocess.run(['/workspace/.dotnet/dotnet', 'run', '--project',
                           'scripts/VerifyChannel/VerifyChannel.csproj', '-c', 'Release', '--',
                           FEED, str(CS/'update-public-key.pem'), VERSION,
                           str(ROOT/'dist/csharp-0.4.1/latest.json'),
                           str(ROOT/'dist/csharp-0.4.1/TalosHelper-CSharp-0.4.1-update.zip')],
                          cwd=CS, capture_output=True, text=True)
    if live.returncode:
        raise RuntimeError('Native live upgrade verification failed: ' + live.stdout + live.stderr)
    live_report = json.loads(live.stdout[live.stdout.index('{'):])
    save(OUT/'live-channel-report.json', live_report)
    verified = commit_files({prefix+'LIVE-CHANNEL-REPORT.json': OUT/'live-channel-report.json'},
                            'Record portable C# 0.4.2 native public channel verification')
    if any(item['tag_name'] == TAG for item in api('repos/'+REPO+'/releases?per_page=100')):
        raise RuntimeError('Release tag already exists')
    body = notes + f'\n[真实更新通道验证]({RAW+verified}/{prefix}LIVE-CHANNEL-REPORT.json)\n'
    release = api('repos/'+REPO+'/releases', method='POST', value={
        'tag_name': TAG, 'target_commitish': commit, 'name': 'TalosHelper 0.4.2 C# 便携测试版',
        'body': body, 'draft': False, 'prerelease': True, 'make_latest': 'false'})
    old_channel_unchanged()
    result = {'passed': True, 'version': VERSION, 'releaseUrl': release['html_url'],
              'downloadUrl': stage['downloadUrl'], 'complete': build['complete'],
              'commit': commit, 'verificationCommit': verified, 'feedUrl': FEED,
              'feedSha256': build['feedSha256'], 'testCount': len(tests),
              'dataLocation': 'EXE-directory/data', 'launcherMinimum': '1.1.0',
              'nativeBundleExtractionRequired': False, 'legacyLauncherGateVerified': True,
              'publicDownloadVerified': True, 'nativeChannelVerified': live_report['passed'],
              'pythonStableUnchanged': True, 'windowsExeExecuted': False}
    save(OUT/'publication-report.json', result)
    print(json.dumps(result, ensure_ascii=False, indent=2))


if __name__ == '__main__':
    main()
