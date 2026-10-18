# C# 组件发布

入口为 `publish_component_release.py`。它发布已完成交付审查的版本，不重建、重签或改写完整包、更新包和签名 feed，也不需要私钥。

从仓库根目录执行离线检查：

```sh
python csharp/scripts/publish_component_release.py --version 0.4.4 --check
```

默认模式也是 `--check`。检查交付报告、源码指纹、109 项测试、审查报告、实际单文件 Host 的嵌入资源、各 ZIP 成员与签名，以及原版 0.4.2 升级基线。输出列出版本目录中 15 个待发布文件及其校验值，不访问网络、不写远程。

网络可用且已获得本次发布授权时才执行：

```sh
python csharp/scripts/publish_component_release.py --version 0.4.4 --publish
```

发布依次执行：

1. 本地预编译原生 `VerifyChannel` 验证工具，禁止还原依赖；发布期间运行时使用 `--no-build`。
2. 确认授权的公开仓库、Python 稳定通道及 Git fetch/push 地址。使用独立 scratch 克隆，普通 Git 提交和非强制推送保留 `talos-updates` 历史。
3. 追加 `csharp/releases/0.4.4/` 下的完整包、离线外包、签名内包、原样 `latest.json`、校验清单和报告。已存在同名不同内容时拒绝发布。
4. 逐项下载公共不可变提交地址核对字节，并核对签名指定的版本包地址；全部通过后，才推进 `csharp/latest.json`。已有更高版本或同版本不同 feed 时拒绝覆盖。
5. 通过真实 HTTPS 从原版 0.4.2 升级，验证旧 Host 启动不会降级，再离线回退。公开验证报告并检查下载字节后，创建 `v0.4.4-csharp-preview` 预发布，`make_latest=false`。
6. 再次核对公共 feed、内包和 Python 稳定通道。仅全部成功后生成 `component-publication-report.json`。

进度收据位于对应 `dist/csharp-版本/component-publish-receipt.json`，绑定离线计划指纹；同一命令可在中断后重试，先前版本文件不能被替换。若在线验证失败，操作会停在对应步骤，不宣称已成功；已经推进的通道仍可通过收据恢复后续验证。

历史限制：2026-10-17 的云端环境在创建网络 socket 时被运行层拒绝（EPERM），当时只完成了离线检查和发布计划。本地检查通过不代表 GitHub 已发布。

## Windows 本地发布

Windows 使用新增的 `publish_component_release_windows.py`，复用上面的签名、文件哈希、版本、公开下载和原生升级检查。它使用 Windows 文件锁、UTF-8 命令输出和独立发布检出目录；Git 只进行普通快进推送，不丢弃已有工作区修改。

开发机需要 Python 3.11+、Git / Git Credential Manager 和 Node.js。先完成 GitHub 登录，例如 `git credential-manager github login --device`。发布 API 通过已配置的凭据提供程序认证，不输出 Token、不把 Token 写入脚本，也不需要更新签名私钥。

在源码仓库根目录安装锁定的官方 Windows SDK（默认放在上级 `.tools/dotnet`，按官方元数据校验 SHA512，不改系统 PATH）：

```powershell
python csharp/scripts/setup_sdk_windows.py
```

发布目录必须是干净的已签名交付副本。运行过的程序目录可能含 `data` 和 `.storage.lock`，不要从该目录打包或删除其中的用户数据。可以解压原迁移备份到另一处，并运行其 `prepare-local.py`，然后指定其中的项目目录：

```powershell
python csharp/scripts/publish_component_release_windows.py --version 0.4.4 --release-root D:\TalosHelper-clean\torchlightinfinite --check
python csharp/scripts/publish_component_release_windows.py --version 0.4.4 --release-root D:\TalosHelper-clean\torchlightinfinite --publish
```

SDK 在其他位置时添加 `--dotnet D:\tools\dotnet\dotnet.exe`。发布入口先锁定还原并编译开发用的 `VerifyChannel`，不重建或重签应用；其余发布顺序、幂等收据和完成报告沿用原入口。最终报告在交付目录的 `dist/csharp-0.4.4/component-publication-report.json`。只有该报告通过并核验真实线上 feed 后，才视为发布完成。
