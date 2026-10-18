#!/usr/bin/env node
'use strict';

// This is an independent evaluation of frozen historical parameters under
// corrected rules, not another search or a rerun of the million-episode study.
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const zlib = require('node:zlib');
const os = require('node:os');
const { Worker, isMainThread, workerData, parentPort } = require('node:worker_threads');

const SCHEMA = 'talos-game-validation-v1';
const NAMESPACE = 'TALOS-GAME-CORRECTED-HOLDOUT-V1';
const CALIBRATION_NAMESPACE = 'TALOS-GAME-CORRECTED-CALIBRATE-V1';
const POLICY_RANDOM_SALT = 0x64a7b19;
const sha256 = bytes => crypto.createHash('sha256').update(bytes).digest('hex');

function statistics(values) {
  const n = values.length;
  if (!n || values.some(value => !Number.isFinite(value))) throw new Error('Statistics require nonempty finite observations.');
  const sum = values.reduce((total, value) => total + value, 0);
  const sumSquares = values.reduce((total, value) => total + value * value, 0);
  const mean = sum / n;
  const variance = n > 1 ? Math.max(0, (sumSquares - sum * sum / n) / (n - 1)) : 0;
  const standardDeviation = Math.sqrt(variance);
  const standardError = standardDeviation / Math.sqrt(n);
  const histogram = {};
  for (const value of values) histogram[value] = (histogram[value] || 0) + 1;
  return { n, games: n, sum, sumSquares, mean, standardDeviation, standardError, SEx: standardError, confidence95: [mean - 1.96 * standardError, mean + 1.96 * standardError], histogram };
}

function pairedStatistics(historical, baseline) {
  if (historical.length !== baseline.length) throw new Error('Paired series lengths differ.');
  return statistics(historical.map((value, i) => value - baseline[i]));
}

function successStatistics(historical, baseline) {
  const pairs = { bothAtLeastOne: 0, historicalOnly: 0, baselineOnly: 0, bothZero: 0 };
  for (let i = 0; i < baseline.length; i++) {
    const a = baseline[i] > 0;
    const b = historical[i] > 0;
    pairs[a && b ? 'bothAtLeastOne' : b ? 'historicalOnly' : a ? 'baselineOnly' : 'bothZero']++;
  }
  return { pairs, difference: pairedStatistics(historical.map(value => Number(value > 0)), baseline.map(value => Number(value > 0))) };
}

function runtimeFor(baseDir, expected) {
  const runtimeFile = path.join(__dirname, 'game-runtime.cjs');
  const beforeModuleHash = sha256(fs.readFileSync(runtimeFile));
  const runtime = require('./game-runtime.cjs').loadRuntime({ baseDir, corrected: true });
  for (const key of ['T', 'S', 'publicView', 'effectiveHistoricalParams', 'baselineParams', 'correctedSourceHash', 'oldPolicyHash']) {
    if (runtime[key] === undefined || runtime[key] === null) throw new Error(`Runtime is missing ${key}.`);
  }
  if (!/^[a-f0-9]{64}$/i.test(runtime.correctedSourceHash) || !/^[a-f0-9]{64}$/i.test(runtime.oldPolicyHash)) throw new Error('Runtime fingerprints must be SHA256 digests.');
  if (runtime.effectiveHistoricalParams.toolSets !== false || runtime.baselineParams.toolSets !== false) throw new Error('Both policies must disable the historical tool-combination table.');
  const afterModuleHash = sha256(fs.readFileSync(runtimeFile));
  if (beforeModuleHash !== afterModuleHash) throw new Error('Runtime adapter changed during loading.');
  const actual = bindingsFor(runtime);
  if (expected && Object.keys(expected).some(key => actual[key] !== expected[key])) throw new Error('Runtime, observation adapter or effective parameters changed during evaluation; results must not be combined.');
  return runtime;
}

function bindingsFor(runtime) {
  return {
    correctedSourceHash: runtime.correctedSourceHash,
    oldPolicyHash: runtime.oldPolicyHash,
    runtimeModuleHash: sha256(fs.readFileSync(path.join(__dirname, 'game-runtime.cjs'))),
    baselineParamsHash: sha256(JSON.stringify(runtime.baselineParams)),
    historicalParamsHash: sha256(JSON.stringify(runtime.effectiveHistoricalParams)),
  };
}

function simulate(runtime, seed, index, params) {
  const { T, S, publicView } = runtime;
  const state = T.newGame({ mode: 'drill', scenario: 'empty', seed, params: { rewards: true, startTools: 1, drillDays: 20 } });
  // A different namespace must not be truncated away by the reference engine.
  if (state.seed !== seed.toUpperCase()) throw new Error(`Engine truncated the evaluation seed: ${seed}`);
  if (state.maxDays !== 20) throw new Error('Evaluation must use the ordinary 20-day mode.');
  const random = S.rng32((Math.imul(index + 1, 2654435761) ^ POLICY_RANDOM_SALT) >>> 0);
  let steps = 0;
  while (state.phase !== 'ended') {
    if (++steps > 2000) throw new Error(`Unfinished simulation: ${seed}`);
    // Decisions never receive the true simulator seed/RNG or concealed inmates.
    const action = S.decide(publicView(state), params, random);
    if (!action) throw new Error(`Missing action: ${seed}`);
    const applied = S.apply(state, action);
    if (!applied.ok) throw new Error(`Illegal ${action.type}: ${seed}: ${applied.reason}`);
    if (state.cells[0].inmate) throw new Error(`Office occupied: ${seed}`);
  }
  const rainbows = state.vault.length;
  if (!Number.isInteger(rainbows) || rainbows < 0) throw new Error(`Invalid terminal reward: ${seed}`);
  return { rainbows, steps };
}

function evaluateRange(job, onProgress) {
  if (sha256(fs.readFileSync(__filename)) !== job.validatorHash) throw new Error('Validator changed during evaluation.');
  const runtime = runtimeFor(job.baseDir, job.expected);
  const baseline = [];
  const historical = [];
  let steps = 0;
  const started = performance.now();
  for (let i = job.from; i < job.to; i++) {
    const seed = `${job.namespace}-${i}`;
    const a = simulate(runtime, seed, i, runtime.baselineParams);
    const b = simulate(runtime, seed, i, runtime.effectiveHistoricalParams);
    baseline.push(a.rainbows);
    historical.push(b.rainbows);
    steps += a.steps + b.steps;
    if (onProgress && (i - job.from + 1) % 1000 === 0) onProgress(i - job.from + 1);
  }
  return { from: job.from, to: job.to, baseline, historical, steps, elapsedMs: performance.now() - started, correctedSourceHash: runtime.correctedSourceHash, oldPolicyHash: runtime.oldPolicyHash };
}

function option(name, fallback) {
  const i = process.argv.indexOf(`--${name}`);
  if (i < 0) return fallback;
  if (!process.argv[i + 1] || process.argv[i + 1].startsWith('--')) throw new Error(`--${name} requires a value.`);
  return process.argv[i + 1];
}

async function runWorkers(job, threads) {
  const live = new Set();
  const completed = new Map();
  const size = Math.ceil(job.pairs / threads);
  try {
    const chunks = await Promise.all(Array.from({ length: threads }, (_, k) => new Promise((resolve, reject) => {
      const from = k * size;
      const to = Math.min(job.pairs, from + size);
      const worker = new Worker(__filename, { workerData: { ...job, from, to } });
      live.add(worker);
      worker.on('message', message => {
        if (message.type === 'progress') {
          completed.set(k, message.pairs);
          console.log(JSON.stringify({ stage: 'holdout-progress', completePairs: [...completed.values()].reduce((total, count) => total + count, 0), totalPairs: job.pairs }));
        } else if (message.type === 'result') {
          completed.set(k, to - from);
          resolve(message.result);
        } else if (message.type === 'error') reject(new Error(message.error));
      });
      worker.once('error', reject);
      worker.once('exit', code => {
        live.delete(worker);
        if (code !== 0) reject(new Error(`Evaluation worker exited ${code}.`));
      });
    })));
    chunks.sort((a, b) => a.from - b.from);
    return { baseline: chunks.flatMap(chunk => chunk.baseline), historical: chunks.flatMap(chunk => chunk.historical), steps: chunks.reduce((total, chunk) => total + chunk.steps, 0) };
  } catch (error) {
    await Promise.allSettled([...live].map(worker => worker.terminate()));
    throw error;
  }
}

async function main() {
  const args = process.argv.slice(2);
  if (args.includes('--help') || args.includes('-h')) {
    console.log('Usage: node validate-policy.cjs [--base-dir PATH] [--out-dir PATH] [--pairs 10000|20000] [--threads 1|2|3|4]\nEvaluates frozen historical parameters under corrected rules; defaults to 20000 paired seeds. --help performs no simulations.');
    return;
  }
  const allowed = new Set(['--base-dir', '--out-dir', '--pairs', '--threads']);
  const seen = new Set();
  for (let i = 0; i < args.length; i += 2) {
    if (!allowed.has(args[i])) throw new Error(`Unknown argument: ${args[i]}. Use --help.`);
    if (seen.has(args[i])) throw new Error(`Repeated argument: ${args[i]}.`);
    if (!args[i + 1] || args[i + 1].startsWith('--')) throw new Error(`${args[i]} requires a value.`);
    seen.add(args[i]);
  }
  const baseDir = path.resolve(option('base-dir', process.env.TALOS_LEGACY_BASE || path.join(__dirname, '..', '..', 'legacy-talos-v03')));
  const outDir = path.resolve(option('out-dir', __dirname));
  const pairs = Number(option('pairs', '20000'));
  const threads = Number(option('threads', String(Math.min(4, Math.max(1, os.availableParallelism() - 1)))));
  if (![10000, 20000].includes(pairs)) throw new Error('--pairs must be 10000 or 20000, fixed before examining holdout outcomes.');
  if (!Number.isInteger(threads) || threads < 1 || threads > 4) throw new Error('--threads must be between 1 and 4.');
  const runtime = runtimeFor(baseDir);
  const validatorHash = sha256(fs.readFileSync(__filename));
  const expected = bindingsFor(runtime);
  const { baselineParamsHash, historicalParamsHash, runtimeModuleHash } = expected;
  const started = Date.now();
  const scope = { mode: 'drill', scenario: 'empty', days: 20, rewards: true, startTools: 1, goal: 'terminal-rainbow-count', publicInformationOnly: true, toolSets: false, muzzleTiming: 'after-unverified-assumption', realGameValidation: false };
  console.log(JSON.stringify({ stage: 'precommitted-plan', namespace: NAMESPACE, pairs, fullGameEvaluations: pairs * 2, threads, ...expected, baselineParamsHash, historicalParamsHash }));
  // Fifty paired calibration worlds = 100 games, disjoint from the holdout.
  // Their outcomes are neither inspected nor used for candidate/sample selection.
  const calibration = evaluateRange({ baseDir, validatorHash, expected, namespace: CALIBRATION_NAMESPACE, from: 0, to: 50 });
  const estimatedSeconds = calibration.elapsedMs / 100 / 1000 * (pairs * 2) / threads;
  console.log(JSON.stringify({ stage: 'calibration', fullGameEvaluations: 100, elapsedMs: calibration.elapsedMs, estimatedHoldoutSeconds: estimatedSeconds, pairs, threads }));
  const results = await runWorkers({ baseDir, validatorHash, expected, namespace: NAMESPACE, pairs }, threads);
  if (results.baseline.length !== pairs || results.historical.length !== pairs) throw new Error('Missing holdout observations; no partial report will be promoted.');
  const baseline = statistics(results.baseline);
  const historical = statistics(results.historical);
  for (const [summary, values] of [[baseline, results.baseline], [historical, results.historical]]) {
    summary.meanRainbows = summary.mean;
    summary.rainbow95CI = [...summary.confidence95];
    summary.atLeastOneCount = values.filter(value => value > 0).length;
    summary.atLeastOneRainbow = summary.atLeastOneCount / pairs;
    summary.zeroRate = 1 - summary.atLeastOneRainbow;
  }
  const difference = pairedStatistics(results.historical, results.baseline);
  const success = successStatistics(results.historical, results.baseline);
  const selected = difference.confidence95[0] > 0 ? 'historical' : 'baseline';
  const generatedAt = new Date().toISOString();
  const csv = ['index,seed,baseline_rainbows,historical_rainbows', ...results.baseline.map((value, i) => `${i},${NAMESPACE}-${i},${value},${results.historical[i]}`)].join('\n') + '\n';
  const compressed = zlib.gzipSync(Buffer.from(csv), { level: 9 });
  const recordsName = 'game-validation.rows.csv.gz';
  const report = {
    schema: SCHEMA, generatedAt, runtimeVersion: runtime.runtimeVersion,
    correctedSourceHash: runtime.correctedSourceHash, oldPolicyHash: runtime.oldPolicyHash, sourceHashes: runtime.sourceHashes,
    validatorHash, runtimeModuleHash, baselineParamsHash, historicalParamsHash,
    purpose: 'Independent evaluation of frozen historical heuristic parameters under corrected simulator rules; not retraining, not the archived million-episode study, not live-game validation.',
    scope, namespace: NAMESPACE,
    seedPlan: { namespace: NAMESPACE, firstIndex: 0, lastIndex: pairs - 1, pairs, policyRng: `mulberry32(imul(index+1,2654435761)^${POLICY_RANDOM_SALT})`, commonGameSeeds: true, commonPolicyRng: true, candidateFrozenBeforeHoldout: true, sampleCountChosenBeforeOutcomes: true },
    configuration: { pairs, threads, fullGameEvaluations: pairs * 2, calibrationGames: 100, totalGamesIncludingCalibration: pairs * 2 + 100, engineParams: { ...runtime.T.DEFAULT_PARAMS, rewards: true, startTools: 1, drillDays: 20 }, toolSets: false },
    calibration: { namespace: CALIBRATION_NAMESPACE, pairs: 50, games: 100, elapsedMs: calibration.elapsedMs, estimatedHoldoutSeconds: estimatedSeconds, excludedFromStatistics: true },
    holdout: { pairs, baseline, historical, pairedHistoricalMinusBaseline: difference, atLeastOne: success },
    comparison: { meanRainbowDifference: difference.mean, paired95CI: [...difference.confidence95], pairedStandardError: difference.standardError, atLeastOneDifference: success.difference.mean, atLeastOnePaired95CI: [...success.difference.confidence95] },
    selection: { selected, criterion: 'historical only if the paired historical-minus-baseline mean Rainbow 95% confidence interval lower bound is greater than zero; otherwise baseline', historicalPromoted: selected === 'historical' },
    parameters: { baseline: runtime.baselineParams, historical: runtime.effectiveHistoricalParams },
    records: { file: recordsName, format: 'gzip CSV; index,seed,baseline_rainbows,historical_rainbows', rows: pairs, bytes: compressed.length, sha256: sha256(compressed), uncompressedBytes: Buffer.byteLength(csv), uncompressedSha256: sha256(csv) },
    statistics: { confidenceLevel: 0.95, method: 'Normal approximation with sample variance (n-1); paired intervals from per-seed differences. No invalid/unfinished games are omitted; any failure aborts the run.', enoughAggregatesForRecalculation: true, noParameterSearch: true },
    runtimeNotes: runtime.notes,
    limitations: [
      'This is a corrected approximate simulator study, not a test against the actual game or a proof of globally optimal moves.',
      'Only three confirmed card-rule corrections and matching solver estimates are applied; other event probabilities and ordering assumptions remain inherited from the fixed historical simulator.',
      'The Muzzle threshold is corrected to at most three inmates; counting after the meal is still an unverified timing assumption.',
      'The historical policy was tuned under different rules and an existing combination-table prior; both policies disable that prior in this independent comparison.',
      'The original million-episode report is historical evidence and is not reused as this validation result.',
      'Average Rainbow count is the selection objective; the at-least-one-Rainbow rate is reported separately and can move in the opposite direction.',
      'Screenshot recognition, special starts, contract challenges, unsupported instruments/events, combat, loot and currency are outside this evaluation.',
    ],
    elapsedSeconds: (Date.now() - started) / 1000, decisionSteps: results.steps,
  };
  // Check every binding again before publishing a loadable report.
  runtimeFor(baseDir, expected);
  if (sha256(fs.readFileSync(__filename)) !== validatorHash) throw new Error('Validator changed before report publication.');
  fs.mkdirSync(outDir, { recursive: true });
  fs.writeFileSync(path.join(outDir, recordsName), compressed);
  fs.writeFileSync(path.join(outDir, 'game-validation.md'), markdownReport(report));
  fs.writeFileSync(path.join(outDir, 'game-validation.json'), `${JSON.stringify(report, null, 2)}\n`);
  console.log(JSON.stringify({ stage: 'complete', pairs, fullGameEvaluations: pairs * 2, selected, baseline: baseline.mean, historical: historical.mean, pairedDifference: difference.mean, paired95CI: difference.confidence95, atLeastOneDifference: success.difference.mean, elapsedSeconds: report.elapsedSeconds, recordsBytes: compressed.length }));
}

function markdownReport(report) {
  const f = value => value.toFixed(6);
  const ci = values => `[${values.map(f).join(', ')}]`;
  const pct = value => `${(value * 100).toFixed(3)}%`;
  const localTime = new Intl.DateTimeFormat('sv-SE', { timeZone: 'Asia/Shanghai', dateStyle: 'short', timeStyle: 'medium', hourCycle: 'h23' }).format(new Date(report.generatedAt));
  const { baseline, historical, pairedHistoricalMinusBaseline: delta, atLeastOne } = report.holdout;
  return `# 旧策略在校正规则下的独立复验\n\n生成时间（北京时间，Asia/Shanghai）：${localTime}。\n\n这是固定旧参数的独立模拟器复验，没有重训百万局，也不是用户真实游戏中的效果验证。${report.selection.selected === 'historical' ? '平均彩数配对95%区间下界大于零，本规则版本选用旧参数。' : '平均彩数配对95%区间下界未大于零，本规则版本保留社区基线。'}\n\n## 冻结的比较设计\n\n普通20天 empty 开局，奖励开启，一件随机起始刑具；双方都关闭旧刑具组合先验表。基线为旧引擎 DEFAULTS+rewards，候选为找回的百万评估策略参数。每次决策只提供 publicView，屏蔽未知囚犯、未观察事件、真实种子和RNG。\n\n新种子前缀：\`${report.namespace}\`，索引0至${report.seedPlan.lastIndex}。${report.holdout.pairs.toLocaleString('en-US')}个配对种子、${report.configuration.fullGameEvaluations.toLocaleString('en-US')}次完整对局，最多${report.configuration.threads}个工作线程。另100局仅用于耗时校准，种子分离，不计入统计。样本数在看留出结果前固定，无参数搜索、无删除失败局；任一非法或未结束对局均中止。\n\n## 留出结果\n\n| 策略 | 样本数 | 平均绮彩 | 均值95%区间 | 至少一彩 | 零彩 |\n| --- | ---: | ---: | --- | ---: | ---: |\n| 社区基线，禁先验表 | ${baseline.n} | ${f(baseline.mean)} | ${ci(baseline.confidence95)} | ${pct(baseline.atLeastOneRainbow)} | ${pct(baseline.zeroRate)} |\n| 历史参数，禁先验表 | ${historical.n} | ${f(historical.mean)} | ${ci(historical.confidence95)} | ${pct(historical.atLeastOneRainbow)} | ${pct(historical.zeroRate)} |\n\n历史参数减基线：每局${f(delta.mean)}彩，配对标准误${f(delta.standardError)}，95%区间${ci(delta.confidence95)}。至少一彩率差：${(atLeastOne.difference.mean * 100).toFixed(4)}个百分点，配对95%区间${ci(atLeastOne.difference.confidence95.map(value => value * 100))}个百分点。\n\n选择只依据平均彩数配对区间下界是否大于零，不把保底指标下降掩盖为整体改善。区间使用样本方差（n-1）和正态近似。\n\n## 可复算资料与绑定\n\n逐种子两项绮彩数保存在 \`${report.records.file}\`，共${report.records.rows}行，压缩后${report.records.bytes}字节。JSON同时保存每组及配对差的n、sum、sumSquares与成功/失败配对表，可重新计算标准误和区间。\n\n- correctedSourceHash：\`${report.correctedSourceHash}\`\n- oldPolicyHash：\`${report.oldPolicyHash}\`\n- validatorHash：\`${report.validatorHash}\`\n- CSV.gz SHA256：\`${report.records.sha256}\`\n\n报告只适用于上述源码、旧策略、参数和规则版本；原百万评估报告只作归档。校准100局耗时${f(report.calibration.elapsedMs / 1000)}秒；总耗时${f(report.elapsedSeconds)}秒。\n\n## 实际适用范围\n\n${report.limitations.map(text => `- ${text}`).join('\n')}\n\n## 复现\n\n在保留固定旧包和校正规则runtime的前提下运行：\`node legacy-update/validate-policy.cjs --base-dir <旧包根目录> --pairs ${report.holdout.pairs} --threads ${report.configuration.threads}\`。不要用同一留出集反复挑参数；更改策略或规则后应使用新命名空间。\n`;
}

module.exports = { statistics, pairedStatistics, successStatistics, markdownReport };
if (!isMainThread) {
  try {
    const result = evaluateRange(workerData, pairs => parentPort.postMessage({ type: 'progress', pairs }));
    parentPort.postMessage({ type: 'result', result });
  } catch (error) { parentPort.postMessage({ type: 'error', error: error.stack }); }
} else if (require.main === module) {
  main().catch(error => { console.error(error.stack); process.exitCode = 1; });
}
