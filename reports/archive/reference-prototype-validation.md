# 原型验证记录

日期：2026-10-17（北京时间）。版本0.1.0。

## 已执行

- Node测试24项全部通过，无跳过或禁用：规则升阶和人数边界、隐藏信息不泄漏、奖励恢复阶段、僭越进度、跨天标记、确认门槛、HTTP服务、IPC来源校验及捕获约束。
- 使用真实HTTP后台的Chromium流程：加载示例、16格布局、实际蒙特卡洛建议、手动修改后重算、读取104100局训练报告、420像素精简布局；无页面异常。界面截图在忽略目录 `artifacts/app-full.png`、`artifacts/app-overlay.png`。
- 真实Electron 44.8.0虚拟显示测试：preload → bootstrap → demo → 后台worker推荐，4次抽样返回有效操作，退出码0。最新示例显式携带僭越阶段1。
- 真实Electron CDP测试：枚举后明确选择虚拟显示器，捕获1920×1080非黑屏PNG；未枚举来源拒绝；置顶、紧凑/展开、报告读取正常；渲染页没有Node接口且外网请求被阻止。
- 新来源及新局清空、在途旧结果作废、识别失败旧建议过期、置顶/精简回调竞态，均经过浏览器流程验证。
- 从锁文件重新执行 `npm ci`、`npm run setup` 成功，参考检出保持干净。
- Windows x64 ZIP构建成功，ZIP逐项CRC通过，包含有效PE文件头的Windows可执行文件；包内模型、报告及业务源码与当前工作区逐字节一致。
- Windows包没有捆绑参考仓库源码、识图特征或其美术。包内参考加载器在用户机器初始化独立检出。

## 产物

- `dist/Torchlight-Coach-0.1.0-Windows-x64.zip`，153561286字节。
- SHA256：`63cd89a5e2c048f9b7d9f14f2fae36dde9da240163555a6cad95095691978fd6`。
- `dist/Torchlight-Coach-0.1.0-Windows-x64.zip.sha256`。
- `models/trained-policy.json` 与 `reports/training-report.json`：104100局真实评估，未证实提升并保留基线。

## 验证界限

没有真实Windows游戏或用户中文典狱截图。Windows包已构建和检查，但没有在Windows实际运行；Linux虚拟显示结果不能等同Windows实机通过。识别器可执行，尚未获得中文实机准确率。当前原型包含手动核对和不支持机制的拒绝提示。

测试环境为Linux，Xvfb采用Debian官方签名索引及哈希验证后局部解包。Electron测试临时使用 `--no-sandbox --disable-gpu`，没有写入产品默认启动命令。测试截图仅来自自行创建的虚拟显示，测试进程已清理。

环境的 `install_script` 与 `start_skill` 已保存为草稿，保存不等于发布或新任务恢复验证。用户可在环境设置审核、保存后发布；网络设置保持原有不受限状态。
