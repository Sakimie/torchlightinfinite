#!/usr/bin/env python3
"""Combine original signed v0.3.2 with the current public-channel launcher."""
from __future__ import annotations
import hashlib
import json
from pathlib import Path
import tempfile
import zipfile

from package_release import make_zip

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent
OLD_HASH = 'ee30df0b0aee7922b871ed9ca1ca6ef1dfcb296da217dfdad32dcd8ee653d145'
NEW_HASH = '4f85ea98ff28a60b3f30c2e730bdc8e5219b0ad36dbe580c4774a2cb50a9ffbe'


def build():
    old = ROOT / 'dist/online-0.3.2/TalosHelper-Online-Update-0.3.2.zip'
    new = ROOT / 'dist/online-0.3.3/TalosHelper-Online-Update-0.3.3.zip'
    assert hashlib.sha256(old.read_bytes()).hexdigest() == OLD_HASH
    assert hashlib.sha256(new.read_bytes()).hexdigest() == NEW_HASH
    destination = ROOT / 'dist/update-test'
    destination.mkdir(parents=True, exist_ok=True)
    output = destination / 'TalosHelper-0.3.2-Update-Test.zip'
    with tempfile.TemporaryDirectory(prefix='talos-032-test-build-') as tmp:
        staging = Path(tmp)
        with zipfile.ZipFile(new) as current, zipfile.ZipFile(old) as previous:
            names = ['TalosHelper-启动器.exe', 'talos-updater/talos_update_core.py',
                     'talos-updater/launcher_ui.py', 'talos-updater/config.json',
                     'talos-updater/bootstrap/latest.json', 'talos-updater/bootstrap/package.zip']
            for name in names:
                archive = previous if name.startswith('talos-updater/bootstrap/') else current
                target = staging / name; target.parent.mkdir(parents=True, exist_ok=True)
                target.write_bytes(archive.read(name))
        instructions = '''0.3.2 → 0.3.3 更新测试

此测试包保留原0.3.2程序和签名，配合已接入当前公开下载地址的启动器。
原0.3.2首次包的旧下载地址没有启用，因此请用本测试包测试在线升级。
不会重发/修改原0.3.2的签名版本，也没有改变游戏策略。

1. 退出助手，复制一份原完整TalosHelper文件夹作为测试目录。
   必须保留_internal、runtime、assets、english-source和原training等文件。
2. 若测试副本里有talos-update-state.json，把它改名为talos-update-state.测试前备份.json。
   只处理副本，不改你的原使用目录。没有该文件可跳过。
3. 把本ZIP全部文件解压到测试副本，与原TalosHelper.exe、_internal、runtime同级。
4. 双击TalosHelper-启动器.exe。首次本地安装后，当前启用版本应为0.3.2。
5. 点击“检查更新”，应发现0.3.3；点击“下载并安装此更新”。
6. 安装完成显示0.3.3，再点“启动助手”。已运行的旧助手不会被自动重启。
7. 可点“回退上一版”验证返回0.3.2。

只有检查/下载更新联网；本地启动、截图、建议和回退仍离线。
本测试包是升级组件，不是完整独立安装包。
已有本地Node/Tk集成及生产HTTPS升级验证；Windows原生启动、游戏截图仍需实机试运行。
'''
        (staging / '0.3.2更新测试说明.txt').write_text(instructions, encoding='utf-8-sig')
        make_zip(staging, output)
    report = {'schema': 'talos-update-test-bundle-v1', 'applicationVersion': '0.3.2',
              'launcherChannelVersion': '0.3.3', 'file': output.name,
              'bytes': output.stat().st_size, 'sha256': hashlib.sha256(output.read_bytes()).hexdigest(),
              'sourceOldSha256': OLD_HASH, 'sourceLauncherSha256': NEW_HASH,
              'feedUrl': 'https://raw.githubusercontent.com/Sakimie/torchlightinfinite/talos-updates/latest.json',
              'requiresOriginalRuntime': True, 'useCopiedDirectory': True}
    (destination / 'test-bundle-report.json').write_text(json.dumps(report, ensure_ascii=False, indent=2) + '\n', encoding='utf8')
    return report


if __name__ == '__main__':
    print(json.dumps(build(), ensure_ascii=False, indent=2))
