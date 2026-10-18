# 0.4.4 Windows 发布与在线更新验证

2026-10-18，从 Windows 本地发布已冻结的 0.4.4 成品。未重建、重签应用、宿主、feed 或 ZIP；新增的 Windows 发布工具只构建开发用原生更新验证器。

## 已完成的发布

- [0.4.4 C# 预发布](https://github.com/Sakimie/torchlightinfinite/releases/tag/v0.4.4-csharp-preview)，保持 `prerelease=true`、`make_latest=false`。
- 独立 C# 更新通道 `talos-updates/csharp/latest.json` 已指向 0.4.4，原 Python 稳定通道保持不变。
- 15 份已审查版本文件已从公共不可变提交地址重新下载并校验字节数及 SHA256，包括完整包、离线包和在线更新内包。
- [原生真实 HTTPS 验证报告](https://raw.githubusercontent.com/Sakimie/torchlightinfinite/b62f2350d6ae092de0d15358b9af78b1b09939e5/csharp/releases/0.4.4/LIVE-CHANNEL-REPORT.json)记录：实际安装签名 0.4.2 基线，经线上 feed 和 HTTPS 升级为 0.4.4，校验激活文件；用旧宿主初始包模拟重启仍保留 0.4.4；在拒绝一切网络访问的客户端中回退到 0.4.2。

签名 feed SHA256：`73e3a0514c43785cc74d3d23c7722dc96b10f8d8d62a63882fe0d18321fa14b7`。

在线内包为 1,335,511 字节，SHA256：`00dcf68ca36e65e27284ff5c81ef151ee6fc9ec10a23bbe3b0ea753c2097477c`。

## 本地 Windows 检查

锁定的官方 Windows .NET SDK 10.0.402 经官方 SHA512 校验后安装到独立工具目录。开发验证器与测试项目编译成功，0 警告、0 错误。53 个冻结编译输入及 156 份识别素材保持原字节。

完整测试项目在 Windows 运行 109 项：108 项通过，1 项抛出 `PlatformNotSupportedException`。该项名称是 `storage.unwritable application directory fails explicitly without external fallback on Linux`，源码明确限定 Linux 权限测试；因此它在本次 Windows 验证中不适用，未修改测试冒充通过。原交付包的 Linux 109/109 测试报告仍保留原字节，两者属于不同平台的验证结果。

本次运行了原生 C# 更新库和真实公开 HTTPS 传输，没有运行 Windows EXE 或加载 WPF 应用组件。游戏截图、F8、DPI、日志触发与实机识别收益仍需用户实际试用。现有程序目录的 `data` 与锁文件未被修改、删除或上传；发布使用独立的干净交付副本。

后续发布步骤见 [Windows 发布入口说明](scripts/COMPONENT-PUBLISHING.md#windows-本地发布)。
