#!/usr/bin/env python3
"""Build reviewable release files locally. This command never publishes them."""
from __future__ import annotations

import argparse
import datetime
import hashlib
import importlib.util
import json
from pathlib import Path
import re
import shutil
import subprocess
import sys
import zipfile

HERE = Path(__file__).resolve().parent
REPO = HERE.parent
CHANNEL = 'https://github.com/Sakimie/torchlightinfinite/releases'
FEED_URL = 'https://raw.githubusercontent.com/Sakimie/torchlightinfinite/talos-updates/latest.json'


def sha(data):
    return hashlib.sha256(data).hexdigest()


def write_json(path, value):
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')


def make_zip(source, destination):
    files = sorted(p for p in source.rglob('*') if p.is_file())
    if not files or len(files) > 100:
        raise ValueError('Unexpected release file count')
    with zipfile.ZipFile(destination, 'w', zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
        for path in files:
            if path.is_symlink():
                raise ValueError('Release must not contain symlinks')
            name = path.relative_to(source).as_posix()
            if any(part.startswith('.') for part in path.relative_to(source).parts):
                raise ValueError('Hidden files must not enter release')
            info = zipfile.ZipInfo(name, (2026, 10, 17, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = 0o100644 << 16
            archive.writestr(info, path.read_bytes(), compresslevel=9)
    return files


def build(args):
    version = args.version
    if not re.fullmatch(r'\d+\.\d+\.\d+', version):
        raise ValueError('Version must contain three numeric parts')
    out = args.output.resolve()
    out.mkdir(parents=True, exist_ok=True)
    spec = importlib.util.spec_from_file_location('talos_online_builder', HERE / 'build_online.py')
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    stage = out / 'staging'
    if stage.exists():
        shutil.rmtree(stage)
    public_key = HERE / 'update-public-key.pem'
    feed_url = args.feed_url
    original_hash = sha(args.original.read_bytes())
    module.build_staging(args.original, version, public_key, feed_url, stage)
    payload = stage / 'payload'
    entry = f'TalosHelper-App-{version}.exe'
    paths = sorted(p for p in payload.rglob('*') if p.is_file())
    for path in paths:
        relative = path.relative_to(payload).as_posix()
        if relative != entry and not relative.startswith(f'releases/{version}/'):
            raise ValueError('Unexpected file in signed version package: ' + relative)
        if any(part in {'runtime', '_internal', 'english-source'} for part in path.relative_to(payload).parts):
            raise ValueError('Original runtime/source must not be redistributed in this update')
    package_name = f'TalosHelper-{version}-update.zip'
    package_path = out / package_name
    make_zip(payload, package_path)
    package_bytes = package_path.read_bytes()
    notes = [
        '更新下载地址迁移到公开仓库 Sakimie/torchlightinfinite；请先解压本版首次更新器包更换启动器。',
        '新增检查更新、下载小版本包及回退功能；首次请使用 TalosHelper-启动器.exe。',
        '游戏截图、识别和策略建议继续离线运行；只有手动检查或下载更新时联网。',
        '沿用 0.3.1 已复验的 baseline 策略；截图后仍须核对状态，再计算下一步。',
    ]
    manifest = {
        'schema': 'talos-release-v1', 'app': 'TalosHelper', 'version': version,
        'launcher_min': '1.0.0', 'base_version': '0.3',
        'published_at': datetime.datetime.now(datetime.timezone.utc).isoformat().replace('+00:00', 'Z'),
        'notes': notes, 'entry': entry,
        'package': {'url': args.package_url or CHANNEL + f'/download/v{version}/' + package_name,
                    'bytes': len(package_bytes), 'sha256': sha(package_bytes)},
        'files': [{'path': p.relative_to(payload).as_posix(), 'bytes': p.stat().st_size,
                   'sha256': sha(p.read_bytes())} for p in paths],
    }
    manifest_path = out / 'manifest.json'
    write_json(manifest_path, manifest)
    feed_path = out / 'latest.json'
    subprocess.run(['node', str(HERE / 'sign-release.cjs'), '--manifest', str(manifest_path),
                    '--key', str(args.key.resolve()), '--public', str(public_key),
                    '--out', str(feed_path)], check=True, capture_output=True, text=True)
    first = out / 'first-install'
    if first.exists():
        shutil.rmtree(first)
    shutil.copytree(stage / 'launcher', first)
    bootstrap = first / 'talos-updater' / 'bootstrap'
    bootstrap.mkdir(parents=True, exist_ok=True)
    shutil.copy2(feed_path, bootstrap / 'latest.json')
    shutil.copy2(package_path, bootstrap / 'package.zip')
    instructions = (
        f'TalosHelper {version} 首次更新器包\n\n'
        '1. 退出助手。把本 ZIP 全部文件解压到原完整 TalosHelper 文件夹，'
        '与 TalosHelper.exe、_internal、runtime 同级。不要只移动 EXE。\n'
        '2. 双击 TalosHelper-启动器.exe。首次版本已包含在包内，可以离线启动。\n'
        '3. 后续点“检查更新”，查看说明后下载并安装；朋友不需要安装 Git。\n'
        '4. 游戏截图和建议始终离线。联网仅用于手动检查、下载更新。\n'
        '5. 下载失败保留旧版；启动器支持回退。原 TalosHelper.exe 也保留。\n\n'
        '更新地址需有公开发布版本；若检查更新连接失败，不影响本地使用。\n'
        '本包已做结构与本地集成验证，Windows 启动和游戏截图仍需实机确认。\n'
    )
    (first / '首次更新说明.txt').write_text(instructions, encoding='utf-8-sig')
    client_path = out / f'TalosHelper-Online-Update-{version}.zip'
    make_zip(first, client_path)
    if sha(args.original.read_bytes()) != original_hash:
        raise ValueError('Original executable changed during build')
    report = {'schema': 'talos-online-release-build-v1', 'version': version,
              'published': False, 'plannedFeedUrl': feed_url,
              'originalSha256': original_hash, 'publicKeySha256': sha(public_key.read_bytes()),
              'sourceHashes': {name: sha((HERE / name).read_bytes()) for name in
                               ('build_online.py', 'talos_update_core.py', 'launcher_ui.py',
                                'game_helper.py', 'sign-release.cjs', 'package_release.py')},
              'artifacts': [{'file': p.name, 'bytes': p.stat().st_size, 'sha256': sha(p.read_bytes())}
                            for p in (package_path, feed_path, client_path)],
              'notes': notes, 'windowsExecutableTested': False}
    write_json(out / 'release-build-report.json', report)
    (out / 'SHA256SUMS.txt').write_text(''.join(
        f"{item['sha256']}  {item['file']}\n" for item in report['artifacts']), encoding='utf-8')
    return report


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--original', type=Path, required=True)
    parser.add_argument('--key', type=Path, required=True, help='Ed25519 private key outside repository')
    parser.add_argument('--version', default='0.3.3')
    parser.add_argument('--feed-url', default=FEED_URL)
    parser.add_argument('--package-url', default='', help='Immutable public HTTPS package URL')
    parser.add_argument('--output', type=Path, default=REPO / 'dist' / 'online-0.3.3')
    print(json.dumps(build(parser.parse_args()), ensure_ascii=False, indent=2))
