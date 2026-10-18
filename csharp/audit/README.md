# C# 可见状态策略独立审计

运行：

```sh
node csharp/audit/run-audit.cjs --dotnet /workspace/.dotnet/dotnet
```

需要 .NET 10 SDK 和 Node，仅用于开发审计。Windows 产品本身不需要 Node。

脚本构造人工确认的可见状态，运行独立控制台适配器，通过 Core 的公开 API 检查合法动作集、逐候选相对评分、最佳候选、全部备选及拒绝条件。JavaScript 基准只使用本项目原创的 `lib/offline-engine.js` 和 `lib/offline-solver.js`。私有评分函数通过开发工具在内存中导出，原文件不修改，不加载第三方参考引擎。

已知规则修正单独标记：铁鞭吞噬概率、鼠串升阶、珍藏及连锁副开门、鼠道不计普通移动收益、行动权限及处刑严格低阶且只按基础克制。概率组合采用独立的伯努利枚举和绮彩封顶算式核对；处刑差异使用明确标注的内存基准修正，不将旧 JavaScript 的已知错误作为正确答案。处刑不继承传奇扩展克制或鼠串额外目标的解释已核对参考行为，仍不是新做的真实游戏测量。

输出 `fixtures.json`、`csharp-results.json` 和 `strategy-parity-report.json`。报告记录源码及实际执行 Core 程序集的哈希。`static-review.json` 只记录代码审阅及其范围。

这些检查不是训练、整局收益验证或真实中文游戏识别准确率测试。Linux 没有运行 WPF 界面、Windows EXE、热键或屏幕捕获；相关审阅只能说明代码路径，不能替代 Windows 实机测试。
