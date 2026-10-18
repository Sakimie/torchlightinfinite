# 离线助手的在线更新契约 v1

用户已授权：仅检查、下载更新时联网；截图和建议继续离线。2026-10-17 用户在独立仓库创建被GitHub集成403拒绝后，改为亲自公开 Sakimie/torchlightinfinite，并回复“修改了”，授权在当前仓库继续发布。不需要再次请求发布授权，也不再创建 -updates 仓库。API已验证当前仓库 private:false；发布0.3.3迁移下载地址，原0.3.2发布候选和签名身份保持不变。

0.3.3已发布，Release https://github.com/Sakimie/torchlightinfinite/releases/tag/v0.3.3 。附件域认证受限，因此使用同仓库独立talos-updates公开分支：feed为 https://raw.githubusercontent.com/Sakimie/torchlightinfinite/talos-updates/latest.json ，package固定提交c44a86a4f52e2f8919cdc71e7349897cf2049ba0；公开channel提交55836c2436dfd74744cdd6e8aa0f7ad2a87d0ace以package提交为parent，未来保持历史可达。已做真实HTTPS字节核对与check/install/rollback验证，不再重试新建仓库或要求用户令牌。

## 目录与版本

- 原完整v0.3目录保留；`_internal`、`runtime`、`assets`、`english-source`为已有运行环境。
- 稳定入口：根目录 `TalosHelper-启动器.exe`，及 `talos-updater/talos_update_core.py`、`launcher_ui.py`、`config.json`。启动器字节码固定校验两个Python模块哈希，并嵌入发布公钥。
- 每版原生EXE必须在根目录，以复用相对路径 `_internal`：`TalosHelper-App-0.3.2.exe`。
- 每版自有组件隔离：`releases/0.3.2/talos_game_update.py`、`releases/0.3.2/training/game-*`，不能覆盖v0.3.1的共享helper/game-*。
- 安装时仅在本机复制原已有 `english-source/manifest.json`、`english-source/js/{engine,solver,tool-combos}.js` 和 `training/english/policy.json` 到该release，逐份校验v0.3固定SHA。它们不随下载包再分发。若缺失/不同，拒绝激活。
- 新App的calculate loader设 `app._talos_install_root=release_dir`（新后端路径），`app._talos_base_root=original_root`（启动器和原运行环境），加载该版本专属helper；旧vision/Node仍以根目录为ROOT。
- 状态文件 `talos-update-state.json` 原子写入，schema `talos-update-state-v1`，含active版本、history、已知包身份；只有所有文件校验并就位后才更改active。
- 只有启动器负责启动/切换版本。保留前版用于手动回退；不用替换正在运行的EXE。

## 签名feed和下载包

Feed是UTF-8 JSON信封：`{"schema":"talos-signed-feed-v1","payload":"base64(raw UTF-8 manifest bytes)","signature":"base64(Ed25519 signature over exact payload bytes)"}`。公钥由首次启动器固定，私钥绝不进入程序、源码仓库、ZIP或日志。

Manifest字段：

```json
{
  "schema": "talos-release-v1",
  "app": "TalosHelper",
  "version": "0.3.2",
  "launcher_min": "1.0.0",
  "base_version": "0.3",
  "published_at": "ISO-8601",
  "notes": ["用户可读更新说明"],
  "entry": "TalosHelper-App-0.3.2.exe",
  "package": {"url":"https://public-host/package.zip","bytes":123,"sha256":"..."},
  "files": [{"path":"TalosHelper-App-0.3.2.exe","bytes":123,"sha256":"..."}]
}
```

files列EXE和该版本releases/<version>下所有自有文件；包内不包含manifest/feed自身（避免循环哈希），也不含运行库、原模拟器源码或私钥。签名验证使用自带 `runtime/node.exe` 的 `node:crypto` Ed25519实现；Python不自写密码学算法。

所有更新请求必须由用户明确点击检查/下载触发。正常启动和游戏建议不访问网络。生产只用正常证书校验的HTTPS，不接受URL内凭据、file地址或不安全重定向。测试可注入本地transport，不能以测试模式发布。

## 安装流程及API

`talos_update_core.Updater(install_root, public_key_pem, feed_url="", node_path=None, transport=None)`：

- `check()`：联网下载feed（或测试transport），验签后解析与兼容检查；返回manifest。无新版本由UI比较version处理。
- `install(manifest, progress=None)`：只接受已验签/认证的manifest（防调用方绕过验签），下载包到同盘暂存；先验证字节数/SHA、ZIP成员安全/大小/重复/完整文件列表，再复制固定旧依赖，最后移动到最终位置并原子激活。返回已安装版本信息。
- `install_local(feed_path, zip_path)`：同一验签/校验路径，可用于测试及离线导入。
- `status()`：读取/校验本地状态，返回active/history、已验证installed、unavailable与active_valid；坏active不能启动，但保留健康历史供恢复。没有state时用本地已签名bootstrap进行首次激活，不联网。
- `rollback(version=None)`：仅切到已完整校验的本地已装前版；不联网。
- `launch_active()`：启动已校验active EXE，参数列表、shell=False；不能启动feed任意路径或未验证文件。

提供本地bootstrap：`talos-updater/bootstrap/latest.json`（签名feed）、`package.zip`。首次status/UI初始化使用install_local，无网络；确保初次收到包即能离线启动。bootstrap仅在初次分发包中出现；未来在线包只含一版5MB应用及轻量组件。

运行中更新使用OS锁、暂存完整文件的原子发布和原子状态提交；失败保持旧active，不自动删除旧版本。进程崩溃后锁自动释放，未登记的部分版本可在逐文件核对后续装同一签名包。拒绝zip路径穿越、符号链接、NTFS ADS/保留名、Windows大小写冲突、重复成员、未列成员、错hash、过大或不兼容包。

## 实现分工

- core代理：`talos_update_core.py`、`test_update_core.py`，主机只读原件，测试使用临时目录/临时签名key。
- UI代理：`launcher_ui.py`、`game_helper.py`（从0.3.1自有helper复制并改），UI测试；联网/Node/download均后台worker，Tk只主线程queue。
- 构建代理：`build_online.py`，复用原CArchive/原v0.3，不改原件；可按版本构建隔离App与稳定launcher，所有原PYZ成员保持不变。
- root：发布公钥/签名工具和最终包、发布说明、公开渠道确认。
- QA代理：最终ZIP/真实Tk+Node/有网络check与无网络游戏边界、安装回滚、原件完整性。

v0.3.2沿用同一已验证game-runtime（e7a9873e…）及baseline报告，不改规则/参数、不重跑4万局。现有0.3.1更新包与原件继续保留。
