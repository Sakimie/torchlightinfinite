# TalosHelper C# 便携测试版 0.4.2

Windows 10/11 x64 桌面助手，支持截图、逐格核对、本地策略建议、置顶悬浮窗，以及手动检查更新。程序、截图识别、规则和更新器均使用 C#；开发机的 Python/Node 构建工具不随产品分发。

下载入口：https://github.com/Sakimie/torchlightinfinite/releases/tag/v0.4.2-csharp-preview

## 使用

下载 `TalosHelper-CSharp-0.4.2-Windows-x64.zip`，全部解压到**可写的新文件夹**，保留所有 DLL 文件，双击 `TalosHelper.exe`。完整包内含 .NET 运行环境及初始应用组件，无需安装 Python、Node、Git、.NET，也无需原版目录。0.4.0/0.4.1 用户此次也要换一次完整启动器，仅点旧版应用更新不能改变启动器的数据路径。

程序自行管理的校准、版本文件和更新状态保存在 `TalosHelper.exe` 旁的 `data` 文件夹，启动诊断在程序旁 `last-startup-error.txt`；界面显示真实数据路径，并有“打开数据目录”按钮。完整移动或备份整个程序文件夹即可带走数据。运行组件使用旁置 DLL，不需要 .NET bundle 向系统临时目录自解压。

首次启动且本地 `data` 没有数据时，会从旧 `%LOCALAPPDATA%\TalosHelperCSharp\preview` 复制校准、签名版本状态、版本文件及日志目录，保留旧稳定数据原件。请先退出旧助手；本地已有数据时优先使用，绝不与另一台电脑的旧数据合并覆盖。目录不可写时明确报错，不会静默改回 AppData。

按 F8 或界面按钮截图，也可导入或粘贴图片。首次须校准棋盘区域及 16 个房间中心；逐格核对种族、阶级、天数、当前位置、可到达位置、阶段、刑具、任务和事件后再计算建议。局面变化后重新截图、核对、计算。0.4.1 的取消及关闭更新窗口修复继续保留。

截图、识别和建议均在本机执行，不上传图片。仅点击“检查更新”及“下载更新”时联网；本地导入和回退无需联网。C# 使用独立预览通道，不覆盖 Python 0.3.x 的更新通道。以后应用组件可通过此通道更新；启动器或内置运行环境变更仍需完整包。

这是新 C# 规则移植预览，采用单步启发式评分，**尚未进行新训练，也未验证真实游戏收益或全局最优**。识图使用用户原应用中的裁片匹配，不是通用中文 OCR，不能自动读全状态。低置信度和未知机制必须人工处理。

## 开发与验证

已在 Linux 使用锁定的官方 .NET SDK 10.0.402 编译 WPF Windows x64 完整包。运行环境为 .NET 10.0.13。SDK 下载校验官方 SHA512，版本锁定见 `toolchain.lock.json` 和 `global.json`。

```sh
python csharp/scripts/setup_sdk.py
cd csharp
/workspace/.dotnet/dotnet build Talos.App/Talos.App.csproj -c Release
/workspace/.dotnet/dotnet run --project Talos.Tests/Talos.Tests.csproj -c Release -- --json
```

验证证据：

- `STORAGE-TEST-REPORT-0.4.2.json` 和 `dist/csharp-0.4.2/test-results.json`：63/63 检查通过，含 16 项迁移/搬迁/权限/取消测试与原 47 项核心、识图、签名更新及真实初始包检查；逐项校验 159 个组件文件。
- `audit/strategy-parity-report.json`：757/757 人工构造局面的合法动作、评分及明确修正检查通过；不是完整对局训练。
- `dist/csharp-0.4.2/embedded-check/bundle-inspection.json`：380 个内嵌条目均无需解压，5 个旁置运行库匹配官方字节，初始资源签名及哈希匹配。
- `dist/csharp-0.4.2/live-channel-report.json`：原生 C# 更新器访问真实公开 HTTPS 通道的签名、下载、安装、原启动器启动保持新版与离线回退校验。

这些检查没有运行 Windows EXE。窗口启动、F8、游戏窗口截图、DPI 和实机识别率仍需 Windows 电脑验证。开始使用说明随完整包提供；启动失败详情写入本地 `last-startup-error.txt`。

## 模块

- `Talos.Core`：本项目自有规则的 C# 移植、状态校验、像素匹配与建议。
- `Talos.App`：WPF 界面、Win32 截图、核对与悬浮窗。
- `Talos.Updates`：原生 ECDSA P-256/SHA256 验签、安全解压、原子切换及本地回退。
- `Talos.Storage`：EXE 旁数据目录、旧数据的原子复制与已有数据保护。
- `Talos.Host`：自带运行环境的便携宿主，内嵌签名初始包；启动不联网。能力版本 1.1.0；便携 feed 最低要求 1.1.0，旧启动器会拒绝并提示升级启动器，防止仍在 AppData 下运行却误以为迁移完成。
- `Talos.Tests`、`audit`、`scripts`：验证、开发构建和发布工具。

私钥始终保存在仓库之外，不得提交、打包或输出。应用仅内置公钥；旧 Python 的密钥和已发布包保持原样。
