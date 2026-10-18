#!/usr/bin/env python3
"""Publish the reviewed, authorized C# preview through normal GitHub APIs."""
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import urllib.request

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'online-update'))
from publish_release import api, digest
from publish_git_channel import commit_files, download_check, RAW

CS = ROOT / 'csharp'
OUT = ROOT / 'dist/csharp-0.4.0'
REPO = 'Sakimie/torchlightinfinite'
TAG = 'v0.4.0-csharp-preview'
OLD_FEED = RAW + 'talos-updates/latest.json'
OLD_SHA = '7e11930bb99b09421f3b9d6e958ab42a9dc35ed97ceaf700a31be96784564406'
FEED = RAW + 'talos-updates/csharp/latest.json'


def old_channel_unchanged():
    with urllib.request.urlopen(OLD_FEED, timeout=40) as response:
        if hashlib.sha256(response.read(1024*1024)).hexdigest() != OLD_SHA:
            raise RuntimeError('Python stable feed has changed unexpectedly')
    if api('repos/' + REPO + '/releases/latest')['tag_name'] != 'v0.3.3':
        raise RuntimeError('Python stable release is no longer latest')


def save(path, value):
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + '\n', encoding='utf8')


def commit_public_files(files, message):
    if all(file.stat().st_size < 30 * 1024 * 1024 for file in files.values()):
        return commit_files(files, message)
    # GitHub's JSON blob API rejects the full runtime archive. Use ordinary Git
    # with existing platform authentication, without reading credentials.
    remote = subprocess.check_output(['git', 'remote', 'get-url', 'origin'], cwd=ROOT, text=True).strip()
    checkout = Path(tempfile.mkdtemp(prefix='talos-csharp-channel-')) / 'repo'
    subprocess.run(['git', 'clone', '--depth', '1', '--single-branch', '--branch', 'talos-updates',
                    remote, str(checkout)], check=True, stdout=subprocess.DEVNULL)
    for name, source in files.items():
        destination = checkout / name
        destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(source, destination)
    subprocess.run(['git', 'add', '--', *files], cwd=checkout, check=True)
    subprocess.run(['git', '-c', 'user.name=Codex', '-c', 'user.email=codex@openai.com',
                    'commit', '-m', message], cwd=checkout, check=True)
    subprocess.run(['git', 'push', 'origin', 'HEAD:refs/heads/talos-updates'], cwd=checkout, check=True)
    return subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=checkout, text=True).strip()


def main():
    report = json.loads((OUT / 'build-report.json').read_text())
    for label in ('complete', 'payload'):
        item = report[label]; file = OUT / item['file']
        if file.stat().st_size != item['bytes'] or digest(file) != item['sha256']:
            raise RuntimeError('Reviewed artifact changed: ' + file.name)
    if digest(OUT / 'latest.json') != report['feedSha256']:
        raise RuntimeError('Reviewed signed feed changed')
    if not json.loads((CS / 'BUNDLE-REPORT.json').read_text()).get('passed'):
        raise RuntimeError('Bundle inspection has not passed')
    if json.loads((CS / 'TEST-REPORT.json').read_text())['summary']['failed']:
        raise RuntimeError('Console verification failed')
    if api('repos/' + REPO)['private']:
        raise RuntimeError('Expected authorized public repository')
    old_channel_unchanged()
    complete_name = report['complete']['file']
    (OUT / 'SHA256SUMS.txt').write_text(''.join(
        f"{digest(OUT / name)}  {name}\n" for name in
        (complete_name, report['payload']['file'], 'latest.json')), encoding='utf8')
    receipt_path = OUT / 'public-files-receipt.json'
    if receipt_path.exists():
        receipt = json.loads(receipt_path.read_text())
        if receipt['completeSha256'] != report['complete']['sha256']:
            raise RuntimeError('Already staged a different complete archive')
        commit = receipt['commit']
    else:
        files = {'csharp/' + complete_name: OUT / complete_name,
                 'csharp/latest.json': OUT / 'latest.json',
                 'csharp/SHA256SUMS.txt': OUT / 'SHA256SUMS.txt',
                 'csharp/README.md': CS / 'README.md',
                 'csharp/TEST-REPORT.json': CS / 'TEST-REPORT.json',
                 'csharp/BUNDLE-REPORT.json': CS / 'BUNDLE-REPORT.json'}
        commit = commit_public_files(files, 'Publish complete C# 0.4.0 preview and separate signed channel')
        save(receipt_path, {'commit': commit, 'completeSha256': report['complete']['sha256']})
    url = RAW + commit + '/csharp/' + complete_name
    download_check(url, OUT / complete_name)
    download_check(FEED, OUT / 'latest.json')
    stage = json.loads((OUT / 'stage-receipt.json').read_text())
    download_check(stage['packageUrl'], OUT / report['payload']['file'])
    live = subprocess.run(['/workspace/.dotnet/dotnet', 'run', '--project',
                           'scripts/VerifyChannel/VerifyChannel.csproj', '-c', 'Release', '--',
                           FEED, str(CS / 'update-public-key.pem'), '0.4.0'],
                          cwd=CS, capture_output=True, text=True)
    if live.returncode:
        raise RuntimeError('Native live-channel verification failed: ' + live.stdout + live.stderr)
    channel_report = json.loads(live.stdout[live.stdout.index('{'):])
    save(CS / 'LIVE-CHANNEL-REPORT.json', channel_report)
    verification_commit = commit_files({'csharp/LIVE-CHANNEL-REPORT.json': CS / 'LIVE-CHANNEL-REPORT.json'},
                                      'Record native C# public channel verification')
    body = (f'TalosHelper C# 测试版 0.4.0，适用于 Windows 10/11 x64。程序、截图识别、规则与更新器均为 C#。\n\n'
            f'**[下载完整 Windows 包（约 60 MB）]({url})**\n\n'
            '完整解压到一个新文件夹，双击 `TalosHelper.exe`。自带运行环境，无需 Python、Node、Git、.NET 或原版目录。\n\n'
            '支持 F8 截图、导入/粘贴、棋盘校准、人工核对、离线建议和悬浮窗。截图及建议在本机进行；只有点击检查和下载更新时联网。\n\n'
            'C# 预览采用独立更新通道，Python 0.3.x 通道继续保留。以后应用组件可在预览内更新；启动器或运行环境变更仍需完整包。\n\n'
            '此版为新的规则移植及单步启发式预览，尚未新训练，不能保证实机最优解。每次建议前必须人工核对状态。\n\n'
            '验证：WPF Windows x64 编译通过，47/47 核心/识图/更新检查及 757/757 构造局面审查通过；单文件内嵌运行环境、签名初始包和真实 HTTPS 更新安装已核对。'
            '云端没有运行 Windows EXE；窗口启动、快捷键、DPI 和游戏截图仍需 Windows 实机验证。\n\n'
            f"SHA256：`{report['complete']['sha256']}`\n\n"
            f'[使用说明]({RAW + commit}/csharp/README.md) · '
            f'[校验值]({RAW + commit}/csharp/SHA256SUMS.txt) · '
            f'[更新通道验证]({RAW + verification_commit}/csharp/LIVE-CHANNEL-REPORT.json)\n')
    releases = api('repos/' + REPO + '/releases?per_page=100')
    existing = [item for item in releases if item['tag_name'] == TAG]
    if len(existing) > 1:
        raise RuntimeError('Ambiguous release tag')
    value = {'name': 'TalosHelper 0.4.0 C# 测试版', 'body': body,
             'draft': False, 'prerelease': True, 'make_latest': 'false'}
    if existing:
        release = api('repos/' + REPO + '/releases/' + str(existing[0]['id']), method='PATCH', value=value)
    else:
        value.update({'tag_name': TAG, 'target_commitish': commit})
        release = api('repos/' + REPO + '/releases', method='POST', value=value)
    old_channel_unchanged()
    result = {'passed': True, 'releaseUrl': release['html_url'], 'downloadUrl': url,
              'feedUrl': FEED, 'commit': commit, 'verificationCommit': verification_commit,
              'complete': report['complete'], 'prerelease': release['prerelease'],
              'publicDownloadVerified': True, 'nativeChannelVerified': channel_report['passed'],
              'pythonStableUnchanged': True, 'windowsExeExecuted': False}
    save(OUT / 'publication-report.json', result)
    print(json.dumps(result, ensure_ascii=False, indent=2))


if __name__ == '__main__':
    main()
