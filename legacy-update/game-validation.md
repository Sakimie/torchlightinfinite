# 旧策略在校正规则下的独立复验

生成时间（北京时间，Asia/Shanghai）：2026-10-17 12:40:33。

这是固定旧参数的独立模拟器复验，没有重训百万局，也不是用户真实游戏中的效果验证。平均彩数配对95%区间下界未大于零，本规则版本保留社区基线。

## 冻结的比较设计

普通20天 empty 开局，奖励开启，一件随机起始刑具；双方都关闭旧刑具组合先验表。基线为旧引擎 DEFAULTS+rewards，候选为找回的百万评估策略参数。每次决策只提供 publicView，屏蔽未知囚犯、未观察事件、真实种子和RNG。

新种子前缀：`TALOS-GAME-CORRECTED-HOLDOUT-V1`，索引0至19999。20,000个配对种子、40,000次完整对局，最多4个工作线程。另100局仅用于耗时校准，种子分离，不计入统计。样本数在看留出结果前固定，无参数搜索、无删除失败局；任一非法或未结束对局均中止。

## 留出结果

| 策略 | 样本数 | 平均绮彩 | 均值95%区间 | 至少一彩 | 零彩 |
| --- | ---: | ---: | --- | ---: | ---: |
| 社区基线，禁先验表 | 20000 | 3.055400 | [3.032543, 3.078257] | 96.105% | 3.895% |
| 历史参数，禁先验表 | 20000 | 2.989450 | [2.966059, 3.012841] | 94.985% | 5.015% |

历史参数减基线：每局-0.065950彩，配对标准误0.011123，95%区间[-0.087751, -0.044149]。至少一彩率差：-1.1200个百分点，配对95%区间[-1.461394, -0.778606]个百分点。

选择只依据平均彩数配对区间下界是否大于零，不把保底指标下降掩盖为整体改善。区间使用样本方差（n-1）和正态近似。

## 可复算资料与绑定

逐种子两项绮彩数保存在 `game-validation.rows.csv.gz`，共20000行，压缩后121174字节。JSON同时保存每组及配对差的n、sum、sumSquares与成功/失败配对表，可重新计算标准误和区间。

- correctedSourceHash：`192ce164b20d25e57e488a377cdf292180e696f2cb61bd9f6083b5ff3675e94c`
- oldPolicyHash：`471ad7862020d1bc444114a811c087f1c5db2872e350590ab6383b9fadf7e78f`
- validatorHash：`57affc599bc23cc6b76594088d84faf2aa1472c737926316ba8783058b06d91a`
- CSV.gz SHA256：`8e47bbf8160fd53ece2e15d18bd2f23390885befd304a568344a9c6aa98be977`

报告只适用于上述源码、旧策略、参数和规则版本；原百万评估报告只作归档。校准100局耗时1.275002秒；总耗时210.339000秒。

## 实际适用范围

- This is a corrected approximate simulator study, not a test against the actual game or a proof of globally optimal moves.
- Only three confirmed card-rule corrections and matching solver estimates are applied; other event probabilities and ordering assumptions remain inherited from the fixed historical simulator.
- The Muzzle threshold is corrected to at most three inmates; counting after the meal is still an unverified timing assumption.
- The historical policy was tuned under different rules and an existing combination-table prior; both policies disable that prior in this independent comparison.
- The original million-episode report is historical evidence and is not reused as this validation result.
- Average Rainbow count is the selection objective; the at-least-one-Rainbow rate is reported separately and can move in the opposite direction.
- Screenshot recognition, special starts, contract challenges, unsupported instruments/events, combat, loot and currency are outside this evaluation.

## 复现

在保留固定旧包和校正规则runtime的前提下运行：`node legacy-update/validate-policy.cjs --base-dir <旧包根目录> --pairs 20000 --threads 4`。不要用同一留出集反复挑参数；更改策略或规则后应使用新命名空间。
