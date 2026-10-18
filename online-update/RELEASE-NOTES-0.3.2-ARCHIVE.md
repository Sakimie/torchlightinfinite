0.3.2 历史版本与在线升级测试。当前最新版本仍为 [0.3.3](https://github.com/Sakimie/torchlightinfinite/releases/tag/v0.3.3)。

## 测试从 0.3.2 升级到 0.3.3（推荐）

**[下载 0.3.2 更新测试包（约 10 MB）](https://raw.githubusercontent.com/Sakimie/torchlightinfinite/3fed4153ead036aa415e4dcda4fe38567da6528c/tests/TalosHelper-0.3.2-Update-Test.zip)**

测试包保留原 0.3.2 程序和原签名 bootstrap，搭配已接入当前公开下载地址的启动器，便于测试“检查更新 → 下载并安装 → 0.3.3”。

1. 退出助手，复制一份原完整 TalosHelper 目录作为测试副本。
2. 副本若有 `talos-update-state.json`，先改名备份；没有则跳过。
3. 将测试包全部解压到副本，与 `TalosHelper.exe`、`_internal`、`runtime` 同级。
4. 运行 `TalosHelper-启动器.exe`，应显示 0.3.2；点击“检查更新”，再下载并安装 0.3.3。

## 原始 0.3.2 留档包

[下载原始 0.3.2 首次升级包（约 10 MB）](https://raw.githubusercontent.com/Sakimie/torchlightinfinite/3f1482f3db09c989a461a05921f32ecebdf5becb/archive/0.3.2/TalosHelper-Online-Update-0.3.2.zip)

原始留档包字节与此前交付版本完全一致，保留当时未启用的旧下载地址。它可用于离线留档；若要测试在线升级，请使用上方更新测试包。

[下载 SHA256 校验清单](https://raw.githubusercontent.com/Sakimie/torchlightinfinite/3f1482f3db09c989a461a05921f32ecebdf5becb/archive/0.3.2/SHA256SUMS.txt)

两者都是升级组件，依赖原完整 v0.3 目录，不是独立安装包。游戏截图与建议仍离线；只有手动检查和下载更新时联网。在线升级与回退流程已验证，Windows 原生启动和实际游戏截图仍待实机确认。
