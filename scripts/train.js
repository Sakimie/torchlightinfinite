#!/usr/bin/env node
'use strict';

const fs = require('node:fs');
const path = require('node:path');
const os = require('node:os');
const crypto = require('node:crypto');
const { Worker, isMainThread, workerData, parentPort } = require('node:worker_threads');
const { loadRuntime, RULE_VERSION } = require('../lib/runtime');

function playBatch(job) {
  const { engine: T, solver: S } = loadRuntime();
  const values = job.policies.map(() => []);
  const started = performance.now();
  for (let i = job.from; i < job.to; i++) {
    for (let p = 0; p < job.policies.length; p++) {
      const state = T.newGame({ mode: 'drill', scenario: 'empty', seed: `${job.seed}-${i}`, params: { rewards: true, startTools: 1, drillDays: 20 } });
      let actions = 0;
      S.playout(state, job.policies[p].params, S.rng32((Math.imul(i + 1, 2654435761) ^ 71267) >>> 0), (action, result) => {
        actions++;
        if (!result.ok) throw new Error(`Illegal ${action.type}: ${result.reason}; seed=${job.seed}-${i}`);
      });
      if (state.phase !== 'ended' || actions >= 2000) throw new Error(`Unfinished simulation ${job.seed}-${i}`);
      values[p].push(state.vault.length);
    }
  }
  return { from: job.from, values, elapsedMs: performance.now() - started };
}

if (!isMainThread) {
  try { parentPort.postMessage({ ok: true, result: playBatch(workerData) }); }
  catch (error) { parentPort.postMessage({ ok: false, error: error.stack }); }
} else {
  main().catch((error) => { console.error(error.stack); process.exitCode = 1; });
}

function stats(values) {
  const n = values.length;
  const mean = values.reduce((sum, value) => sum + value, 0) / n;
  const variance = n > 1 ? values.reduce((sum, value) => sum + (value - mean) ** 2, 0) / (n - 1) : 0;
  const se = Math.sqrt(variance / n);
  const histogram = {};
  for (const value of values) histogram[value] = (histogram[value] || 0) + 1;
  return { n, mean, standardDeviation: Math.sqrt(variance), standardError: se, confidence95: [mean - 1.96 * se, mean + 1.96 * se], zeroRate: values.filter((v) => v === 0).length / n, histogram };
}

function paired(candidate, baseline) {
  return stats(candidate.map((value, i) => value - baseline[i]));
}

function argument(name, fallback, min, max) {
  const i = process.argv.indexOf(`--${name}`);
  const n = i < 0 ? fallback : Number(process.argv[i + 1]);
  if (!Number.isInteger(n) || n < min || n > max) throw new Error(`--${name} must be an integer between ${min} and ${max}`);
  return n;
}

async function evaluate(policies, games, seed, threads) {
  const size = Math.ceil(games / threads);
  const results = await Promise.all(Array.from({ length: threads }, (_, k) => new Promise((resolve, reject) => {
    const from = k * size;
    const to = Math.min(games, from + size);
    if (from >= to) return resolve({ from, values: policies.map(() => []) });
    const worker = new Worker(__filename, { workerData: { policies, from, to, seed } });
    worker.once('message', (message) => message.ok ? resolve(message.result) : reject(new Error(message.error)));
    worker.once('error', reject);
    worker.once('exit', (code) => { if (code !== 0) reject(new Error(`Training worker exited ${code}`)); });
  })));
  results.sort((a, b) => a.from - b.from);
  return policies.map((policy, i) => ({ id: policy.id, values: results.flatMap((result) => result.values[i]) }));
}

function candidatePolicies(base) {
  const candidates = [
    ['baseline', {}],
    ['event-flow', { wInstigate: 1, wCrow: 1.5, wChain: 12, wStair: 0.5 }],
    ['movement-flow', { wStep: 1, wWalk: 1 }],
    ['open-flow', { wOpen: 2.25, wNow: 0.625 }],
    ['preserve-high-tiers', { swapMaxTier: 1, stopMargin: 2, rainbowMult: 2.5 }],
    ['feeding-flow', { wNow: 1.25, wOpen: 1, swapMaxTier: 1 }],
    ['open-low', { wOpen: 1 }], ['open-high', { wOpen: 2 }],
    ['activate-low', { wNow: 0.55 }], ['activate-high', { wNow: 1.25 }],
    ['walk-low', { wWalk: 0.5 }], ['walk-high', { wWalk: 1.1 }],
    ['event-low', { wInstigate: 0.25 }], ['event-high', { wInstigate: 0.9 }],
    ['spread-off', { wStair: 0 }], ['spread-high', { wStair: 0.5 }],
    ['steps-off', { wStep: 0 }], ['rainbow-high', { rainbowMult: 3 }],
    ['replace-supreme', { swapMaxTier: 4 }],
  ];
  return candidates.map(([id, edits]) => ({ id, params: { ...base, ...edits } }));
}

async function main() {
  const runtime = loadRuntime();
  const sourceFingerprint = () => crypto.createHash('sha256').update(fs.readFileSync(path.join(__dirname, '..', 'lib', 'offline-engine.js'))).update(fs.readFileSync(path.join(__dirname, '..', 'lib', 'offline-solver.js'))).digest('hex');
  const engineSourceHash = sourceFingerprint();
  const threads = argument('threads', Math.min(4, Math.max(1, os.availableParallelism() - 1)), 1, 4);
  const trainGames = argument('train-games', 2000, 20, 100000);
  const validationGames = argument('validation-games', 6000, 20, 100000);
  const holdoutGames = argument('holdout-games', 20000, 20, 1000000);
  const trainSeed = 'OFFLINE20-TRAIN-V3';
  const validationSeed = 'OFFLINE20-VALID-V3';
  const holdoutSeed = 'OFFLINE20-HOLDOUT-V3';
  const started = Date.now();
  const baseline = { id: 'baseline', params: { ...runtime.solver.DEFAULTS, ...runtime.solver.PROFILES.rewards } };
  const calibration = playBatch({ policies: [baseline], seed: 'OFFLINE20-CALIBRATE-V3', from: 0, to: 100 });
  console.log(JSON.stringify({ stage: 'calibration', games: 100, elapsedMs: calibration.elapsedMs, threads }));
  const candidates = candidatePolicies(baseline.params);
  console.log(JSON.stringify({ stage: 'search', candidates: candidates.length, gamesPerCandidate: trainGames, seed: trainSeed }));
  const trainRows = await evaluate(candidates, trainGames, trainSeed, threads);
  const ranked = trainRows.slice(1).sort((a, b) => stats(b.values).mean - stats(a.values).mean);
  const finalists = [baseline, ...ranked.slice(0, 3).map((row) => candidates.find((p) => p.id === row.id))];
  console.log(JSON.stringify({ stage: 'validation', finalists: finalists.map((p) => p.id), gamesPerPolicy: validationGames, seed: validationSeed }));
  const validationRows = await evaluate(finalists, validationGames, validationSeed, threads);
  const validationBaseline = validationRows[0].values;
  // Only a candidate with a positive paired lower bound can advance.
  const validationRanked = validationRows.slice(1).map((row) => ({ ...row, difference: paired(row.values, validationBaseline) })).sort((a, b) => b.difference.mean - a.difference.mean);
  const bestValidation = validationRanked[0];
  const candidate = finalists.find((p) => p.id === bestValidation.id);
  const validationPassed = bestValidation.difference.confidence95[0] > 0;
  console.log(JSON.stringify({ stage: 'holdout', candidate: candidate.id, validationPassed, gamesPerPolicy: holdoutGames, seed: holdoutSeed }));
  // The holdout is evaluated exactly once after search and validation are fixed.
  const holdoutRows = await evaluate([baseline, candidate], holdoutGames, holdoutSeed, threads);
  const baselineStats = stats(holdoutRows[0].values);
  const candidateStats = stats(holdoutRows[1].values);
  const difference = paired(holdoutRows[1].values, holdoutRows[0].values);
  const improved = validationPassed && difference.confidence95[0] > 0;
  const selected = improved ? candidate : baseline;
  if (sourceFingerprint() !== engineSourceHash) throw new Error('训练期间规则或求解器文件发生变化，本次结果不得发布；请固定实现后使用新的种子集合重新训练。');
  const evaluations = candidates.length * trainGames + finalists.length * validationGames + 2 * holdoutGames + 100;
  const report = {
    version: 2, generatedAt: new Date().toISOString(), engineVersion: runtime.revision, engineSourceHash, ruleVersion: RULE_VERSION,
    objective: 'Ordinary 20-day drill: maximize vaulted Rainbows, rewards enabled, empty scenario, one random starting tool offer.',
    method: 'Finite rule-parameter search and Monte Carlo full-game evaluation; common game seeds and policy RNG within each split; disjoint search, validation and untouched holdout seed namespaces.',
    scope: 'Research simulator results, not measured real-game performance or a proof of global optimality.',
    configuration: { threads, trainGames, validationGames, holdoutGames, muzzleTiming: 'after', startTools: 1, rewards: true, days: 20 },
    seedSplits: { calibration: 'OFFLINE20-CALIBRATE-V3', search: trainSeed, validation: validationSeed, holdout: holdoutSeed },
    calibration: { games: 100, elapsedMs: calibration.elapsedMs },
    search: trainRows.map((row) => ({ id: row.id, ...stats(row.values), pairedAgainstBaseline: paired(row.values, trainRows[0].values) })),
    validation: validationRows.map((row) => ({ id: row.id, ...stats(row.values), pairedAgainstBaseline: paired(row.values, validationBaseline) })),
    holdout: { baseline: baselineStats, candidate: { id: candidate.id, ...candidateStats }, pairedCandidateMinusBaseline: difference },
    selection: { candidate: candidate.id, validationPassed, improved, selected: selected.id, rule: 'Use candidate only if paired 95% lower confidence bound exceeds zero on both validation and holdout; otherwise retain local initial policy under local offline rules.' },
    fullGameEvaluations: evaluations, elapsedSeconds: (Date.now() - started) / 1000,
    confidenceMethod: 'Normal 95% confidence intervals using sample variance (n-1); paired intervals use per-seed candidate minus baseline. Search and validation intervals are descriptive after selection; final claim depends on the holdout.',
    ruleNotes: runtime.ruleNotes,
  };
  const model = {
    version: 2, engineVersion: runtime.revision, engineSourceHash, ruleVersion: RULE_VERSION, params: selected.params,
    training: { objective: report.objective, selected: selected.id, improved, seedSplits: report.seedSplits, configuration: report.configuration, fullGameEvaluations: evaluations, holdout: report.holdout, generatedAt: report.generatedAt },
    limitations: runtime.ruleNotes.limitations,
  };
  const root = path.join(__dirname, '..');
  fs.mkdirSync(path.join(root, 'models'), { recursive: true });
  fs.mkdirSync(path.join(root, 'reports'), { recursive: true });
  fs.writeFileSync(path.join(root, 'models/trained-policy.json'), `${JSON.stringify(model, null, 2)}\n`);
  fs.writeFileSync(path.join(root, 'reports/training-report.json'), `${JSON.stringify(report, null, 2)}\n`);
  fs.writeFileSync(path.join(root, 'reports/training-report.md'), markdownReport(report));
  console.log(JSON.stringify({ stage: 'complete', selected: selected.id, improved, fullGameEvaluations: evaluations, baseline: baselineStats.mean, candidate: candidateStats.mean, pairedDifference: difference.mean, confidence95: difference.confidence95, elapsedSeconds: report.elapsedSeconds }));
}

function markdownReport(report) {
  const format = (value) => value.toFixed(4);
  const interval = (values) => `[${values.map(format).join(', ')}]`;
  const holdout = report.holdout;
  const generatedAt = new Intl.DateTimeFormat('sv-SE', { timeZone: 'Asia/Shanghai', dateStyle: 'short', timeStyle: 'medium', hourCycle: 'h23' }).format(new Date(report.generatedAt));
  return `# 普通20天追彩策略训练报告\n\n生成时间（北京时间，Asia/Shanghai）：${generatedAt}。自有离线引擎版本：\`${report.engineVersion}\`；规则版本：\`${report.ruleVersion}\`。\n\n${report.selection.improved ? `已选用 ${report.selection.selected}，独立留出验证的配对提升置信区间大于零。` : '未得到同时通过验证集与独立留出集的显著提升，因此模型保留本地初始策略。'} 这是近似模拟器内的结果，不能称为真实游戏中的全局最优解。\n\n## 目标与方法\n\n普通20天，奖励开启，empty 开局，一件随机起始刑具；目标是结算绮彩囚犯数量。按攻略的事件、移动、开门、留怪和连吃思路构造有限参数候选，并进行完整对局蒙特卡洛评估。搜索、验证、留出集使用不同种子前缀，同一集合中各策略使用相同对局种子；保留逐局差值计算配对区间。\n\n共 ${report.fullGameEvaluations.toLocaleString('en-US')} 次完整模拟，最多 ${report.configuration.threads} 个工作线程，耗时 ${format(report.elapsedSeconds)} 秒。100局校准耗时 ${format(report.calibration.elapsedMs / 1000)} 秒。\n\n| 独立留出评估 | 样本数 | 平均绮彩 | 均值95%置信区间 | 零绮彩比例 |\n| --- | ---: | ---: | --- | ---: |\n| 本地初始策略 | ${holdout.baseline.n} | ${format(holdout.baseline.mean)} | ${interval(holdout.baseline.confidence95)} | ${format(holdout.baseline.zeroRate)} |\n| 候选 ${holdout.candidate.id} | ${holdout.candidate.n} | ${format(holdout.candidate.mean)} | ${interval(holdout.candidate.confidence95)} | ${format(holdout.candidate.zeroRate)} |\n\n候选减初始策略：每局 ${format(holdout.pairedCandidateMinusBaseline.mean)} 彩，配对95%置信区间 ${interval(holdout.pairedCandidateMinusBaseline.confidence95)}，配对种子数 ${holdout.pairedCandidateMinusBaseline.n}。\n\n只有验证集和留出集的配对区间下界均大于零才替换初始策略。训练集的筛选结果不当作泛化提升；留出集仅在选定候选后评估一次。区间使用样本方差与正态近似，完整候选和验证数据见 training-report.json。\n\n## 规则校正与限制\n\n僭越第二阶段修为随机一人升两阶；煽动修为一个事件；口枷人数阈值修为不超过三人，并同步修正求解器估值。口枷在吞噬后计算人数是可配置的未验证假设。\n\n${report.ruleNotes.limitations.map((text) => `- ${text}`).join('\n')}\n\n捕兽夹、有福同享、宿敌、同罪移押场景必须提示不支持，不能使用本报告结果给出确定推荐。需用真实游戏截图、逐步操作记录和最终收益进一步校准。\n\n## 复现\n\n\`npm run setup\` 后运行 \`node scripts/train.js --threads ${report.configuration.threads} --train-games ${report.configuration.trainGames} --validation-games ${report.configuration.validationGames} --holdout-games ${report.configuration.holdoutGames}\`。种子前缀固定，不能反复挑选留出结果；更换候选或规则后应使用新版本独立留出种子。\n`;
}
