# v0.3.2 发布候选验证

首次更新器包：`dist/online-0.3.2/TalosHelper-Online-Update-0.3.2.zip`，10,398,795 字节，SHA256 `ee30df0b0aee7922b871ed9ca1ca6ef1dfcb296da217dfdad32dcd8ee653d145`。

后续版本包：`TalosHelper-0.3.2-update.zip`，5,261,274 字节，SHA256 `c979b6884b6c564aabb51f0e393496d7d2542935ae6126e79144dd9ff6d65648`。`latest.json` 使用客户端固定公钥对应的 Ed25519 密钥签名；私钥未进入源码或 ZIP。

验证结果：

- 32 项 Python 单元测试通过：20 项核心、9 项启动器、3 项助手集成守卫（含原 18 项守卫复用）。
- 19 项独立攻击与恢复测试通过：伪签名、清单/状态篡改、Windows 路径、ZIP 链接/压缩大小、HTTPS 降级、掉电续装、锁崩溃释放、提交后异常、坏当前版本回退。
- 6 项 Node 签名工具检查通过；构建器合成代码与模块依赖自检通过。
- 最终 ZIP 的 60 项 Tk + Node 集成检查通过，详见 `integration-validation.json`：实际发布公钥、签名 bootstrap、隔离目录、启动器 hashpin、安装/回退/取消、坏版本恢复、真实本地建议及识图失败 busy 释放。
- 原 EXE、v0.3.1 helper 与已交付 v0.3.1 ZIP 哈希保持不变。原 PE 前缀、PYZ 和除 app 外的 CArchive 成员保持不变。

测试用临时安装目录和临时密钥模拟后续升级；实际签名 bootstrap 使用公开材料验签。联网操作使用注入 transport，尚未连接拟公开通道；没有创建公开仓库或上传版本。

本环境没有执行 Windows PE，也没有原完整 Windows 运行库。Windows 原生启动、文件锁分支、游戏实际截图和 DPI 显示仍需实机验证。Tk 冒烟的 fake core 报告单独存为 `ui-smoke-validation.json`，不替代最终集成报告。

本次不修改游戏引擎、训练参数或 baseline 复验报告，不重跑训练，也不新增策略收益声明。
