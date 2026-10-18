# 0.4.3 界面更新验证

本地交付已完成，GitHub 尚未发布。当前执行环境的网络 socket 被禁止，初始只读 GitHub 连接即返回 `socket: operation not permitted`；没有请求提权或尝试绕过限制。

WPF Release 编译成功，零警告、零错误。使用离线已恢复依赖和无构建服务器模式，避免在受限环境中依赖网络或编译服务器 IPC。新增主题确认为实际 `Talos.App.dll` 的 `Talos.App.g.resources` 中的 `themes/dark.baml`（16,818 字节）。

独立界面源码审查 22 项通过，涵盖控件模板、资源键、焦点与三态、显示/编辑样式、滚动兜底、截图空态和原有窗口生命周期。Core、Host、Updates、Storage 的 15 个源文件/项目及 NativeCapture、UiModels 保持原样；版本包的 159 个文件中只有 Talos.App.dll 变化。

现有 63 项检查以真实 0.4.3 签名包全部通过；另外使用真实 0.4.2/0.4.3 包验证离线导入、逐文件哈希、旧引导包不降级、校准保留及离线回退，全程 HTTP handler 请求数为 0。线上旧版本的下载按钮被禁用，同版只在损坏时可修复，主动回退仍有独立入口。

完整 EXE 的 380 个条目均无需 .NET bundle 自解压，5 个旁置运行库与官方文件匹配。内嵌公钥、签名 feed 和初始包的哈希与已测材料完全相同。完整包、离线外包和内包的校验值在 `dist/csharp-0.4.3/SHA256SUMS.txt`。

没有运行 Windows EXE、WPF、DPI 或真实截图。这里的模板和布局检查是源码/交叉编译证据，不能替代 Windows 实际观感、键盘及缩放验证。
