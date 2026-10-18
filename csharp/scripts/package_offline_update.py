#!/usr/bin/env python3
"""Wrap an existing signed payload for the application's offline import flow."""
import argparse
import base64
import hashlib
import json
from pathlib import Path
import shutil

from build_preview import zip_files


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--directory', required=True, type=Path)
    args = parser.parse_args()
    out = args.directory.resolve()
    envelope = json.loads((out/'latest.json').read_text('utf8'))
    manifest = json.loads(base64.b64decode(envelope['payload']))
    version = manifest['version']
    payload = out/f'TalosHelper-CSharp-{version}-update.zip'
    if payload.stat().st_size != manifest['package']['bytes'] or sha(payload) != manifest['package']['sha256']:
        raise RuntimeError('Signed package identity differs from the local ZIP')
    folder = out/'offline-package'
    folder.mkdir(exist_ok=True)
    if any(p.name not in ('latest.json', payload.name, '使用说明.txt', 'SHA256SUMS.txt') for p in folder.iterdir()):
        raise RuntimeError('Unexpected files in the offline distribution folder')
    shutil.copyfile(out/'latest.json', folder/'latest.json')
    shutil.copyfile(payload, folder/payload.name)
    (folder/'使用说明.txt').write_text(
        f'典狱助手 {version} 离线更新\n\n'
        '适用于已能正常启动的 0.4.2 便携版。无需删除 data，也无需替换启动器。\n\n'
        '1. 完整解压本ZIP。不要再解压里面的 update.zip。\n'
        '2. 在现有助手打开“检查更新”，点击“离线导入签名文件与ZIP”。\n'
        '3. 第一个对话框选择本目录的 latest.json。\n'
        f'4. 第二个对话框选择本目录的 {payload.name}。\n'
        '5. 校验和安装完成后，点击“重启到当前启用版本”。\n\n'
        '导入、核对、截图和建议均不需要联网。旧版本继续保留，可在程序中回退。\n'
        '签名与0.4.2启动器兼容，导入不需要先联网检查更新。\n'
        '旧0.4.0/0.4.1启动器不支持此便携更新，请改用完整便携包。\n'
        '\n本次更新：\n' + ''.join('- ' + note + '\n' for note in manifest['notes']), encoding='utf-8-sig')
    (folder/'SHA256SUMS.txt').write_text(''.join(f'{sha(folder/name)}  {name}\n' for name in ['latest.json', payload.name]), encoding='utf8')
    archive = out/f'TalosHelper-CSharp-{version}-Offline-Update.zip'
    zip_files(folder, archive)
    result = {'version': version, 'file': archive.name, 'bytes': archive.stat().st_size,
              'sha256': sha(archive), 'payloadSha256': sha(payload),
              'feedSha256': sha(out/'latest.json'), 'minimumExistingInstallation': '0.4.2',
              'published': False, 'requiresNetworkForImport': False}
    (out/'offline-package-report.json').write_text(json.dumps(result, ensure_ascii=False, indent=2)+'\n', encoding='utf8')
    print(json.dumps(result, ensure_ascii=False, indent=2))


if __name__ == '__main__':
    main()
