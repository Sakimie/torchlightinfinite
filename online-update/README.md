# TalosHelper 更新通道

游戏截图、识别、状态核对和策略建议始终在本机运行。只有点击“检查更新”或“下载并安装”时才联网。朋友不需要 Git、Python、Node 安装器或 GitHub 账号。

## 第一次升级

将 `TalosHelper-Online-Update-0.3.3.zip` 的全部文件解压到原来完整的 TalosHelper 文件夹，与 `TalosHelper.exe`、`_internal`、`runtime` 同级。以后用 `TalosHelper-启动器.exe` 打开助手。首次所需版本已放在包内，可以离线安装和启动。

这个首次升级包需要发送一次；原来的 v0.3 不包含更新器。原来的程序和运行库仍需保留。

已有0.3.2的用户也需这次更换启动器以迁移下载地址；旧本地版本保留，随后手动点击“检查更新”获取0.3.3。

## 测试从0.3.2升级

用户需要旧版本测试时，使用 `build_update_test.py` 生成的独立 `TalosHelper-0.3.2-Update-Test.zip`：新版启动器/core/UI/config逐字来自正式0.3.3，bootstrap两个文件逐字来自原签名0.3.2。它不修改/重签旧正式包，也不更改在线最新feed。先复制完整原目录作为测试副本，副本若有 `talos-update-state.json` 则改名备份，再解压测试包，首次显示0.3.2；点击检查后可升级到0.3.3。构建、实际HTTPS升级/回退验证和公开下载记录位于 `dist/update-test/`。不要把此测试包冒充原始未改地址的0.3.2发行包。

用户进一步要求GitHub也提供旧版后，已公开 [0.3.2历史版本页](https://github.com/Sakimie/torchlightinfinite/releases/tag/v0.3.2)，分别提供可在线升级的测试包与原始未改留档包，以及两者SHA256清单。发布设置 `make_latest:false`，最新版本仍为0.3.3，在线feed字节未改。历史原包位于更新分支的 `archive/0.3.2/`，公开记录 `dist/archive-0.3.2/publication-report.json`。

## 以后更新

在启动器点击“检查更新”，查看版本说明，再下载并安装。更新包只包含该版本的程序和策略组件，不重复下载整套运行环境。下载中断或包校验失败时，继续保留当前版本；安装成功后，下次启动使用新版本。也可以在启动器回退到保留的前版。

已运行的助手不会在游戏途中被替换或重启。只有更新操作才会联网，不上传游戏截图。

## 发布渠道

2026-10-17 用户亲自公开当前仓库 `Sakimie/torchlightinfinite` 并授权发布。**0.3.3已公开发布**：[版本页](https://github.com/Sakimie/torchlightinfinite/releases/tag/v0.3.3)。正式feed为 `https://raw.githubusercontent.com/Sakimie/torchlightinfinite/talos-updates/latest.json`，版本包固定到完整commit SHA。Release正文提供首次包公开下载链接。

当前连接可创建Release，但访问附件上传域返回401。工作发布工具为 `publish_git_channel.py`：通过正常Git Data API将审核过的ZIP/签名feed/说明放到独立 `talos-updates` 分支，完整提交保持父子相连；正常 `main` 分支不变。不读取令牌，不上传私钥，不改变仓库可见性。实际公开文件已下载复核，Release设置为latest。

未来发布先构建版本payload，再运行 `publish_git_channel.py stage-package` 获取不可变package URL；重新调用 `package_release.py --feed-url <stage.feed_url> --package-url <stage.package_url>` 签名及生成首次包，通过该版本QA后准备Release草稿并运行 `publish_git_channel.py publish`。不能改变已公开版本的内容或force重建下载分支。`publish_release.py`保留作常规附件发布工具，当前环境不能用其附件上传路径。

`verify_public_channel.py`仅供开发人员显式运行，使用真实HTTPS完成旧0.3.2→0.3.3检查/验签/下载/安装，再禁止联网验证回退；结果见 `public-channel-validation.json`。本地最终ZIP的71项Tk/Node检查见 `integration-validation-0.3.3.json`。

客户端固定发布公钥，使用原运行环境中的 Node 验证 Ed25519 签名，再校验 ZIP 和所有文件的 SHA256。私钥保存在源码仓库外，不随客户端、版本包或公开仓库分发。不要把私钥上传到 GitHub。

## 开发与验证

更新协议与目录布局见 [CONTRACT.md](CONTRACT.md)。`legacy-update/` 的 v0.3.1 产物保留不变。本次只添加更新能力，沿用已复验的 baseline 策略和修正规则，不宣称新增训练效果。

`sign-release.cjs` 使用 Node 内置密码学模块签名精确的 manifest 字节，要求显式提供仓库外的私钥和与客户端一致的公钥。`node online-update/test-sign-release.cjs` 使用临时密钥完成签名和篡改检查。

本环境不能运行 Windows PE，最终仍需 Windows 电脑验证启动器、游戏截图和 DPI 显示。Linux 上的结构审计、Tk/Node 测试不能替代这一步。
