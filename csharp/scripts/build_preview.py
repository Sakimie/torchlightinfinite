#!/usr/bin/env python3
"""Build payload first, then sign/embed it in a complete offline Windows host."""
from __future__ import annotations
import argparse
import datetime
import hashlib
import json
from pathlib import Path
import re
import shutil
import subprocess
import sys
import zipfile

CSHARP = Path(__file__).resolve().parent.parent
ROOT = CSHARP.parent
DOTNET = '/workspace/.dotnet/dotnet'
VERSION = '0.4.2'
FEED = 'https://raw.githubusercontent.com/Sakimie/torchlightinfinite/talos-updates/csharp/latest.json'
OUT = ROOT / 'dist' / ('csharp-' + VERSION)
SKIP_RESTORE = False


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def write(path, value):
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + '\n', encoding='utf8')


def zip_files(folder, output):
    with zipfile.ZipFile(output, 'w', zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
        for file in sorted(p for p in folder.rglob('*') if p.is_file()):
            name = file.relative_to(folder).as_posix()
            if file.is_symlink() or any(part.startswith('.') for part in file.relative_to(folder).parts):
                raise RuntimeError('Unexpected hidden/symlink distribution file')
            item = zipfile.ZipInfo(name, (2026, 10, 17, 0, 0, 0))
            item.compress_type = zipfile.ZIP_DEFLATED
            item.external_attr = 0o100644 << 16
            archive.writestr(item, file.read_bytes(), compresslevel=9)


def payload():
    if (OUT / 'publication-report.json').exists() or (OUT / 'delivery-report.json').exists():
        raise RuntimeError('Delivered versions are immutable; select a new --version')
    OUT.mkdir(parents=True, exist_ok=True)
    subprocess.run([DOTNET, 'build', 'Talos.App/Talos.App.csproj', '-c', 'Release', '--nologo',
                    '-p:DebugType=none', '-p:DebugSymbols=false'] + (['--no-restore', '--disable-build-servers', '-m:1', '-nr:false', '-p:UseSharedCompilation=false'] if SKIP_RESTORE else []), cwd=CSHARP, check=True)
    folder = OUT / 'payload'
    if folder.exists():
        shutil.rmtree(folder)
    folder.mkdir()
    for assembly in ('Talos.App', 'Talos.Core'):
        framework = 'net10.0-windows' if assembly == 'Talos.App' else 'net10.0'
        source = CSHARP / assembly / 'bin' / 'Release' / framework / (assembly + '.dll')
        shutil.copyfile(source, folder / source.name)
    source_assets = Path('/workspace/legacy-talos-v03/assets')
    if not (source_assets / 'templates.json').is_file():
        raise RuntimeError('Original user-provided recognition crops are missing')
    shutil.copytree(source_assets, folder / 'assets')
    notes = ('C# 本地策略测试版\n\n'
             '此版使用本项目自有规则的C#移植与保守修正，不沿用旧模拟器收益统计。\n'
             '截图后必须核对棋盘、阶段、天数、刑具和任务；建议不保证实机全局最优。\n'
             '即时规则评分和整局随机模拟属于不同估计方式；具体模式、样本数量和误差以界面为准。\n'
             '概率和部分结算顺序属于近似假设，未知机制需停止建议。\n'
             '截图裁片由用户原应用提供；不是通用中文OCR或已测量准确率的识图模型。\n'
             '日志跟随只提供截图时机，不直接还原棋盘；国服事件和动画时长仍需实机核实。\n')
    (folder / 'RULE-NOTES.txt').write_text(notes, encoding='utf-8-sig')
    archive = OUT / f'TalosHelper-CSharp-{VERSION}-update.zip'
    zip_files(folder, archive)
    files = [{'path': p.relative_to(folder).as_posix(), 'bytes': p.stat().st_size, 'sha256': sha(p)}
             for p in sorted(folder.rglob('*')) if p.is_file()]
    result = {'version': VERSION, 'file': archive.name, 'bytes': archive.stat().st_size,
              'sha256': sha(archive), 'files': files}
    write(OUT / 'payload-report.json', result)
    return {key: value for key, value in result.items() if key != 'files'}


def sign(args):
    if (OUT / 'publication-report.json').exists() or (OUT / 'delivery-report.json').exists():
        raise RuntimeError('Delivered versions are immutable; select a new --version')
    report = json.loads((OUT / 'payload-report.json').read_text('utf8'))
    archive = OUT / report['file']
    if sha(archive) != report['sha256']:
        raise RuntimeError('Payload changed after staging')
    manifest = {'schema': 'talos-csharp-release-v1', 'app': 'TalosHelper', 'version': VERSION,
                'launcherMin': args.launcher_min, 'publishedAt': datetime.datetime.now(datetime.timezone.utc).isoformat().replace('+00:00', 'Z'),
                'notes': args.notes or ['C# 完整测试版：自带运行环境，不需要旧 Python 目录。',
                          '支持 F8 截图、人工核对、离线建议、悬浮窗和签名更新。',
                          'C# 策略移植属于新预览，不复用旧训练收益声明；Windows 实机待验证。'],
                'entryAssembly': 'Talos.App.dll',
                'package': {'url': args.package_url, 'bytes': report['bytes'], 'sha256': report['sha256']},
                'files': report['files']}
    write(OUT / 'manifest.json', manifest)
    subprocess.run(['node', str(CSHARP / 'scripts/sign_feed.cjs'), '--manifest', str(OUT / 'manifest.json'),
                    '--key', str(args.key), '--public', str(CSHARP / 'update-public-key.pem'),
                    '--out', str(OUT / 'latest.json')], check=True)
    return {'version': VERSION, 'feedSha256': sha(OUT / 'latest.json'), 'package': manifest['package']}


def host(args):
    sign(args)
    report = json.loads((OUT / 'payload-report.json').read_text('utf8'))
    archive = OUT / report['file']
    published = OUT / 'host-publish'
    subprocess.run([DOTNET, 'publish', 'Talos.Host/Talos.Host.csproj', '-c', 'Release', '-r', 'win-x64',
                    '--self-contained', 'true', '--nologo', '-o', str(published),
                    '-p:BootstrapFeed=' + str(OUT / 'latest.json'),
                    '-p:BootstrapPackage=' + str(archive)] + (['--no-restore', '--disable-build-servers', '-m:1', '-nr:false', '-p:UseSharedCompilation=false'] if SKIP_RESTORE else []), cwd=CSHARP, check=True)
    full = OUT / 'complete-package'
    if full.exists():
        shutil.rmtree(full)
    full.mkdir()
    for file in published.iterdir():
        if file.is_file() and file.suffix.lower() != '.pdb':
            shutil.copyfile(file, full / file.name)
    if not (full / 'TalosHelper.exe').is_file():
        raise RuntimeError('Self-contained Windows executable missing')
    (full / '开始使用.txt').write_text(
        f'TalosHelper C# 测试版 {VERSION}（Windows 10/11 x64）\n\n'
        '1. 将本ZIP全部解压到一个新文件夹。直接双击 TalosHelper.exe。\n'
        '   本包自带.NET运行环境，不需要Python、Node、Git或旧版TalosHelper目录。\n'
        '2. 校准、更新状态和版本文件保存在程序目录的 data 文件夹；整体移动程序目录即可带走数据。\n'
        '   data 首次为空时会复制旧 AppData 里的助手数据，并保留旧数据原件；请先退出旧版助手。\n'
        '   本地 data 已有数据时优先使用，不与另一台电脑的旧数据合并。\n'
        '3. 使用F8或“截图”，也可以导入/粘贴截图；按界面校准棋盘区域。\n'
        '4. 人工核对16格、当前天数、阶段、目的地、刑具和任务，确认后计算建议。\n'
        '5. 可打开悬浮窗。只有点击检查/下载更新才联网；截图与建议不上传。\n'
        '6. C#预览使用独立更新通道，旧Python版继续保留。\n\n'
        '建议采用本地规则评分或整局随机模拟比较，具体方式以界面为准；不复用旧模拟器收益统计。\n'
        '未知机制应停止建议，识图结果必须人工核对。\n'
        '已完成云端编译和核心/更新验证；Windows窗口、截图、快捷键与DPI仍需实机测试。\n'
        '请保留程序旁所有DLL文件。运行环境无需解压到系统临时目录。\n'
        '启动错误详情保存在程序旁 last-startup-error.txt；目录不可写时请整体移到可写位置。\n', encoding='utf-8-sig')
    manifest = json.loads((OUT / 'manifest.json').read_text('utf8'))
    with (full / '开始使用.txt').open('a', encoding='utf8') as usage:
        usage.write('\n本次更新：\n' + ''.join('- ' + note + '\n' for note in manifest['notes']))
    extra_usage = CSHARP / ('RELEASE-NOTES-' + VERSION + '.md')
    if extra_usage.is_file():
        shutil.copyfile(extra_usage, full / '更新说明.md')
    notices = full / 'licenses'; notices.mkdir()
    for package in ('microsoft.netcore.app.runtime.win-x64', 'microsoft.windowsdesktop.app.runtime.win-x64'):
        package_root = Path.home() / '.nuget/packages' / package
        for source in package_root.rglob('*') if package_root.exists() else []:
            if source.is_file() and source.name.lower() in ('license', 'license.txt', 'third-party-notices.txt', 'thirdpartynotices.txt'):
                shutil.copyfile(source, notices / (package + '-' + source.name))
    complete = OUT / f'TalosHelper-CSharp-{VERSION}-Windows-x64.zip'
    zip_files(full, complete)
    if complete.stat().st_size >= 100 * 1024 * 1024:
        raise RuntimeError('Complete package exceeds GitHub Git blob limit; use a supported delivery format')
    result = {'schema': 'talos-csharp-preview-build-v1', 'version': VERSION,
              'payload': {'file': archive.name, 'bytes': archive.stat().st_size, 'sha256': sha(archive)},
              'complete': {'file': complete.name, 'bytes': complete.stat().st_size, 'sha256': sha(complete)},
              'exeBytes': (full / 'TalosHelper.exe').stat().st_size,
              'feedSha256': sha(OUT / 'latest.json'), 'requiresOldInstallation': False,
              'requiresInstalledDotnet': False, 'windowsExeExecuted': False,
              'sourceHashes': {str(p.relative_to(CSHARP)): sha(p) for p in sorted(CSHARP.rglob('*'))
                               if p.is_file() and p.suffix in ('.cs', '.csproj', '.props', '.xaml') and not {'obj', 'bin'} & set(p.parts)}}
    write(OUT / 'build-report.json', result)
    return result


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('action', choices=['payload', 'sign', 'host'])
    parser.add_argument('--version', default=VERSION)
    parser.add_argument('--notes', action='append')
    parser.add_argument('--launcher-min', default='1.1.0')
    parser.add_argument('--no-restore', action='store_true', help='Use the already restored dependency graph without network access')
    parser.add_argument('--package-url', default='')
    parser.add_argument('--key', type=Path)
    args = parser.parse_args()
    SKIP_RESTORE = args.no_restore
    if not re.fullmatch(r'\d+\.\d+\.\d+', args.version):
        parser.error('version must be numeric major.minor.patch')
    VERSION = args.version
    OUT = ROOT / 'dist' / ('csharp-' + VERSION)
    if args.action in ('sign', 'host') and (not args.key or not args.package_url.startswith('https://')):
        parser.error('sign/host requires --key and immutable HTTPS --package-url')
    operation = {'payload': payload, 'sign': lambda: sign(args), 'host': lambda: host(args)}[args.action]
    print(json.dumps(operation(), ensure_ascii=False, indent=2))
