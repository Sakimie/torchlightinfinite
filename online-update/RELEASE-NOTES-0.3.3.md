启用当前公开仓库的在线更新通道。朋友首次换上本版启动器后，可以自行检查和下载后续小版本包。

**[下载 0.3.3 首次更新器包（约 10 MB）](https://raw.githubusercontent.com/Sakimie/torchlightinfinite/talos-updates/TalosHelper-Online-Update-0.3.3.zip)** · [SHA256 校验清单](https://raw.githubusercontent.com/Sakimie/torchlightinfinite/talos-updates/SHA256SUMS.txt)

**第一次使用，或已经装过 0.3.2：** 下载 `TalosHelper-Online-Update-0.3.3.zip`，退出助手后全部解压到原完整 TalosHelper 文件夹，与 `TalosHelper.exe`、`_internal`、`runtime` 同级，按提示覆盖启动器组件。以后运行 `TalosHelper-启动器.exe`。

已有 0.3.2 本地版本会继续保留；更换启动器后，点击“检查更新”即可获取 0.3.3。原 0.3.2 的旧下载地址没有公开发布，需要完成这次启动器迁移。

- 只有手动检查、下载更新时联网；游戏截图、识别、状态核对和建议始终离线，不上传截图。
- 后续只下载约 5 MB 的版本程序和策略组件，不重复下载运行库。下载失败保留当前版本，支持回退。
- 安装前验证 Ed25519 签名及 SHA256。朋友不需要 Git、Python 安装器或 GitHub 账号。
- 策略继续使用 0.3.1 已复验的 baseline 与修正规则；截图后仍需人工核对。本次没有新增训练效果。

这是升级包，需保留原完整 v0.3 的 `_internal`、`runtime`、`assets`、`english-source` 等文件，不能单独放进空文件夹使用。`latest.json` 和 `TalosHelper-0.3.3-update.zip` 由更新器使用。

更新文件通过本仓库的公开下载分支分发；启动器固定校验发布公钥，版本包链接固定到不可变提交。下载不需要登录 GitHub。

已做本地 Tk/Node 集成和包结构验证；Windows 原生启动、游戏实际截图与 DPI 显示仍待实机确认。
