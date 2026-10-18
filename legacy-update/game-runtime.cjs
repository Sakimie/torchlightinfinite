'use strict';

// A small adapter over the user's existing, pinned simulator files. This file does not
// redistribute those files, execute an external entry point, or change the old installation.
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const vm = require('node:vm');

const SOURCE_COMMIT = '49bcc6d7b5a52293adf0d225f349dfbb9504ad55';
const RUNTIME_VERSION = 'talos-game-corrected-49bcc6d7-v1';
const EXPECTED_POLICY_HASH = '471ad7862020d1bc444114a811c087f1c5db2872e350590ab6383b9fadf7e78f';
const EXPECTED = Object.freeze({
  'engine.js': 'bb6a73e4cbc24cfd9c020273c18699fe43ab5bc3f2a87e1a209b038a6f5cf139',
  'solver.js': '72f0d01aeee7b9d897a7e742ab2b1b739b6d9e3f5f4104372d2188a8ae3954d5',
  'tool-combos.js': '3cc44fbbbc301832ba93292248741879ae9b7a96b21d8b7c53b0fdf25932a51e',
});
const sha256 = bytes => crypto.createHash('sha256').update(bytes).digest('hex');
const copy = x => JSON.parse(JSON.stringify(x));
const instances = new Map();
function replaceExactly(source, before, after, label, expected = 1) {
  const count = source.split(before).length - 1;
  if (count !== expected) throw new Error(`本地规则补丁无法安全应用（${label}：预期${expected}处，实际${count}处）。请保留原文件并检查版本。`);
  return source.split(before).join(after);
}
function correctedSources(engine, solver) {
  const patches = [
    ['engine', "usurp2: { count: 2, by: 1, next: 'usurp3' }", "usurp2: { count: 1, by: 2, next: 'usurp3' }", '僭越II人数和阶级'],
    ['engine', 'const INSTIGATE_EVENTS = 2;', 'const INSTIGATE_EVENTS = 1;', '煽动事件数量'],
    ['engine', "has(state, 'muzzle') && boardCells(state).length - (option.scratch ? 0 : 1) <= 4", "has(state, 'muzzle') && boardCells(state).length - (option.scratch ? 0 : 1) <= 3", '口枷预览阈值'],
    ['engine', "has(state, 'muzzle') && crowd <= 4", "has(state, 'muzzle') && crowd <= 3", '口枷执行阈值'],
    ['engine', 'When 4 or fewer inmates are on the block after the meal', 'When 3 or fewer inmates are on the block after the meal', '口枷说明'],
    ['engine', 'const small = board.length <= 5;', 'const small = board.length <= 4;', '简易顾问口枷阈值'],
    ['engine', "const keepSmall = has(state, 'muzzle') && board.length <= 5;", "const keepSmall = has(state, 'muzzle') && board.length <= 4;", '简易事件顾问口枷阈值'],
    ['engine', "has(state, 'muzzle') && board.length <= 5 ? 8 : 2", "has(state, 'muzzle') && board.length <= 4 ? 8 : 2", '简易巡逻顾问口枷阈值'],
    ['solver', 'ctx.muzzle && crowd - 1 <= 4', 'ctx.muzzle && crowd - 1 <= 3', '策略口枷估值阈值'],
    ['solver', 'usurp1: () => ctx.lift + 2 * ctx.lift * P.wInstigate,', 'usurp1: () => ctx.lift + ctx.lift2 * P.wInstigate,', '僭越I后续估值'],
    ['solver', 'usurp2: () => 2 * ctx.lift + 2 * ctx.lift2 * P.wInstigate,', 'usurp2: () => ctx.lift2 + 2 * ctx.lift2 * P.wInstigate,', '僭越II估值'],
    ['solver', 'Usurpation II waits there: 2 random inmates rise a tier', 'Usurpation II waits there: 1 random inmate rises 2 tiers', '僭越II落点说明'],
    ['solver', 'Two random inmates rise a tier, and Usurpation III joins the events.', 'One random inmate rises 2 tiers, and Usurpation III joins the events.', '僭越II操作说明'],
    ['solver', 'which beats a couple of new event doors.', 'which beats one new event door.', '审讯室说明'],
  ];
  const source = { engine, solver };
  for (const [file, before, after, label] of patches) source[file] = replaceExactly(source[file], before, after, label);
  return { ...source, patchCount: patches.length };
}
function evaluate(source, context, filename) {
  context.module = { exports: {} };
  new vm.Script(source, { filename }).runInContext(context, { timeout: 1000 });
  return context.module.exports;
}
function publicView(state) {
  const st = copy(state);
  st.seed = 'PUBLIC-OBSERVATION'; st.rng = 314159;
  for (const c of st.cells) {
    if (!c.sealed) continue;
    if (!c.known) c.hidden = null;
    else if (c.known === 1 && c.hidden) c.hidden.beast = 'mammoth';
    if (c.seen === false) c.event = null;
    else if (c.event && c.eventKnown === false) c.event = 'chain';
  }
  // Do not let an unseen occupant's generated ID counter leak into future draws.
  st.nextId = Math.max(0, ...st.cells.flatMap(c => [c.inmate?.id || 0, c.known ? c.hidden?.id || 0 : 0]), ...st.vault.map(i => i.id || 0)) + 1;
  st.log = [];
  if (st.chaining) st.chaining.at = 0;
  return st;
}
function loadRuntime(options = {}) {
  const baseDir = path.resolve(options.baseDir || path.join(__dirname, '..'));
  const corrected = options.corrected !== false;
  const key = `${baseDir}|${corrected}`;
  if (instances.has(key)) return instances.get(key);
  const folder = path.join(baseDir, 'english-source', 'js'), source = {}, sourceHashes = {};
  for (const [file, expected] of Object.entries(EXPECTED)) {
    const bytes = fs.readFileSync(path.join(folder, file));
    if (bytes.length > 2 * 1024 * 1024 || sha256(bytes) !== expected) throw new Error(`原${file}与已核对的49bcc6d7版本不一致，已停止加载；不会覆盖原文件。`);
    source[file] = bytes.toString('utf8'); sourceHashes[file] = expected;
  }
  const manifest = JSON.parse(fs.readFileSync(path.join(baseDir, 'english-source', 'manifest.json'), 'utf8'));
  if (manifest.commit !== SOURCE_COMMIT) throw new Error('原源码清单版本不匹配，请使用原v0.3目录。');
  const policyBytes = fs.readFileSync(path.join(baseDir, 'training', 'english', 'policy.json'));
  if (policyBytes.length > 1024 * 1024 || sha256(policyBytes) !== EXPECTED_POLICY_HASH) throw new Error('历史策略文件与原v0.3参数不一致，已停止加载；不会把修改过的参数称作原训练策略。');
  const model = JSON.parse(policyBytes.toString('utf8'));
  if (model.schema !== 'talos-english-policy-v1' || model.sourceCommit !== SOURCE_COMMIT || !model.params || typeof model.params !== 'object' || Array.isArray(model.params)) throw new Error('历史策略与原v0.3源码不匹配。');
  const patched = corrected ? correctedSources(source['engine.js'], source['solver.js']) : { engine: source['engine.js'], solver: source['solver.js'], patchCount: 0 };
  // No require/process/fs/network is exposed to the evaluated simulator scripts.
  const context = vm.createContext(Object.create(null), { codeGeneration: { strings: false, wasm: false } });
  const T = evaluate(patched.engine, context, 'pinned-engine.js');
  evaluate(source['tool-combos.js'], context, 'pinned-tool-combos.js');
  const S = evaluate(patched.solver, context, 'pinned-solver.js');
  const correctedSourceHash = sha256([RUNTIME_VERSION, patched.engine, patched.solver, source['tool-combos.js']].join('\0'));
  const runtime = {
    baseDir, T, S, model, publicView, sourceCommit: SOURCE_COMMIT, sourceHashes,
    runtimeVersion: corrected ? RUNTIME_VERSION : 'talos-game-original-49bcc6d7', corrected,
    correctedSourceHash, oldPolicyHash: sha256(policyBytes), patchCount: patched.patchCount,
    effectiveHistoricalParams: { ...copy(model.params), toolSets: corrected ? false : model.params.toolSets },
    baselineParams: { ...copy(S.DEFAULTS), ...copy(S.PROFILES.rewards), toolSets: false },
    notes: [
      '本地读取原v0.3的固定版本源码，原文件保持不变。',
      ...(corrected ? ['已在内存中修正僭越II一人升两阶、煽动一个事件、口枷人数不超过三；口枷按吞噬后人数判断仍是待实机确认的时点假设。', '旧工具组合收益表已停用；历史参数不等于在修正规则下已验证。'] : ['当前为原版规则，仅用于旧策略复现。']),
      '截图和操作阶段必须先人工核对；建议不保证真实游戏的绝对最优。',
    ],
  };
  instances.set(key, runtime);
  return runtime;
}
function int(value, lo, hi, label) {
  if (!Number.isInteger(value) || value < lo || value > hi) throw new Error(`${label}须为${lo}至${hi}的整数。`);
  return value;
}
function bool(value, fallback, label) {
  if (value === undefined) return fallback;
  if (typeof value !== 'boolean') throw new Error(`请核对${label}。`);
  return value;
}
function list(value, label) { if (!Array.isArray(value)) throw new Error(`请提供${label}。`); return value; }
const PHASES = ['patrol', 'manage', 'office', 'event', 'tool', 'gift', 'chain', 'ratway'];
const UNSUPPORTED = ['lion-trap', 'trap', 'shared-fortune', 'nemesis', 'shared-transfer', 'hunt'];
function mapObserved(input, runtime = loadRuntime()) {
  const { T } = runtime;
  if (!input || typeof input !== 'object' || Array.isArray(input)) throw new Error('缺少已核对的棋盘。');
  if (input.confirmed !== true) throw new Error('请逐格核对截图、天数、位置、刑具和决策阶段，再确认棋盘。');
  if (input.startEvent) throw new Error('当前只支持普通20天，不支持特殊开局。');
  if (input.usedExtra || input.devourAfterSwap || (Array.isArray(input.prey) ? input.prey.length : input.prey) || input.scratch) throw new Error('暂不支持额外行动、猎物或划痕；请保留截图，不能省略这些规则后继续推荐。');
  if (input.hasUnrecordedHiddenInfo) throw new Error('已探知的隐藏信息尚未录入，请先补充稀有度/种族或保留截图。');
  if (Array.isArray(input.needsReview) && input.needsReview.length) throw new Error('仍有待核对信息，请完成后再计算。');
  if (input.rerolls || input.remainingRerolls) throw new Error('暂不支持刑具刷新次数，请使用原界面处理或保留截图。');
  const board = copy(input), day = int(board.day, 1, 20, '当前天数'), position = int(board.position, 0, 15, '典狱长位置');
  if (board.totalDays !== undefined && board.totalDays !== 20) throw new Error('当前只支持普通20天。');
  if (!PHASES.includes(board.phase)) throw new Error('请确认当前决策阶段。');
  const cells = list(board.cells, '16格牢房');
  if (cells.length !== 16) throw new Error('棋盘必须包括审讯室和15间牢房。');
  const tools = list(board.tools, '已持有刑具（没有时选择空列表）');
  if (tools.length > T.TOOL_IDS.length || new Set(tools).size !== tools.length) throw new Error('刑具列表重复或过长，请核对。');
  for (const id of [...tools, ...(Array.isArray(board.offerIds) ? board.offerIds : [])]) {
    if (UNSUPPORTED.includes(id) || !Object.hasOwn(T.TOOLS, id) && !Object.hasOwn(T.GIFTS, id)) throw new Error(`暂不支持或未识别：${String(id).slice(0, 45)}。`);
  }
  if (tools.some(id => !Object.hasOwn(T.TOOLS, id))) throw new Error('持有列表中含珍藏，请把珍藏放在待选择奖励中。');
  const objective = board.objective === 'incidents' ? 'incident' : board.objective;
  if (!Object.hasOwn(T.OBJECTIVES, objective)) throw new Error('请确认本局任务。');
  if (![1, 2, 3, 4, 'done'].includes(board.usurpStage)) throw new Error('请确认僭越事件进度；不能自动重置到第一阶段。');
  const st = T.newGame({ mode: 'drill', scenario: 'empty', seed: 'PUBLIC-OBSERVATION', params: { rewards: true, startTools: 1 } });
  st.day = day; st.maxDays = 20; st.nextId = 1; st.log = []; st.pending = []; st.event = null; st.destinations = []; st.chaining = null;
  st.tools = [...tools]; st.position = position; st.startEvent = null; st.challenge = null; st.result = null;
  st.cells = cells.map((c, n) => {
    if (!c || c.n !== n) throw new Error('牢房编号必须从0到15连续且不能重复。');
    if (n === 0 && c.kind !== 'office') throw new Error('1号位置必须为审讯室。');
    if (n && !['locked', 'empty', 'nest', 'inmate'].includes(c.kind)) throw new Error(`请确认${n + 1}号牢房，未知状态不能参与计算。`);
    if (c.scratch || c.prey || c.hidden || c.known) throw new Error(`暂不支持${n + 1}号牢房的划痕、猎物或未录全的隐藏信息。`);
    const out = { n, sealed: n !== 0, inmate: null, hidden: null, known: 0, scratch: false, nest: null, event: null, eventKnown: false, ivory: false, seen: bool(c.seen, false, '已走过的门') };
    if (n === 0) return out;
    if (c.kind === 'inmate') {
      if (!T.BEASTS.includes(c.beast)) throw new Error(`请确认${n + 1}号囚犯种族。`);
      if (c.tier === 5) throw new Error('绮彩囚犯已离场入库；请增加已获绮彩数，并把该牢房改为未开启。');
      out.inmate = { id: st.nextId++, beast: c.beast, tier: int(c.tier, 0, 4, '囚犯稀有度') }; out.sealed = false;
      out.ivory = bool(c.ivory, false, '象牙标记'); out.seen = true;
    } else if (c.kind === 'empty') out.sealed = false;
    else if (c.kind === 'nest') out.nest = { due: int(c.due, day + 1, 30, '鼠巢孵化日期') };
    if (c.knownBeast !== undefined && c.knownTier === undefined) throw new Error('已知隐藏种族还需要同时确认隐藏稀有度。');
    if (c.knownTier !== undefined) {
      if (!out.sealed) throw new Error('隐藏稀有度只能用于未开门牢房。');
      const tier = int(c.knownTier, 0, 4, '已知隐藏稀有度');
      if (c.knownBeast !== undefined && !T.BEASTS.includes(c.knownBeast)) throw new Error('已知隐藏种族无效。');
      out.known = c.knownBeast ? 2 : 1;
      out.hidden = { id: st.nextId++, tier, beast: c.knownBeast || 'mammoth' };
    }
    if (c.eventKind && !Object.hasOwn(T.EVENTS, c.eventKind)) throw new Error('存在未支持的事件，请保留截图。');
    if (c.event) {
      if (!out.sealed) throw new Error('已开牢房不应保留未触发事件标记；当前事件请在决策阶段设置。');
      out.event = c.eventKind || 'chain'; out.eventKnown = !!c.eventKind; out.seen = true;
    }
    return out;
  });
  const vault = int(board.vault ?? 0, 0, 200, '已获绮彩数');
  st.vault = Array.from({ length: vault }, () => ({ id: st.nextId++, beast: 'mammoth', day: 1 })); st.cache = vault;
  st.rng = 314159; st.seed = 'PUBLIC-OBSERVATION';
  st.objective = { id: objective, progress: int(board.objectiveProgress ?? 0, 0, 99, '任务进度') };
  const best = Math.max(0, ...st.cells.filter(c => c.inmate).map(c => c.inmate.tier), vault ? 5 : 0);
  st.stats = { patrols: 0, opened: 0, devours: 0, chains: 0, swaps: 0, executions: 0, best };
  st.milestone = bool(board.milestone, false, '任务达成状态') || !!(T.OBJECTIVES[objective].need && st.objective.progress >= T.OBJECTIVES[objective].need) || objective === 'king' && best >= 4;
  const used = bool(board.usedAction, false, '当日操作次数');
  st.used = { swap: used ? 1 : 0, devour: used ? 1 : 0, noSwap: bool(board.noSwap, false, '替换权限'), noDevour: bool(board.noDevour, false, '吞噬权限'), devourAfterSwap: false };
  st.chain = bool(board.chainArmed, false, '连锁反应'); st.prey = [];
  st.eventPool = T.EVENT_POOL.filter(id => !/^usurp[1-4]$/.test(id));
  if (board.usurpStage !== 'done') st.eventPool.push(`usurp${board.usurpStage}`);
  const pendingKind = ['tool', 'gift'].includes(board.phase) ? board.phase : null;
  const phase = pendingKind ? board.resumePhase : board.phase;
  if (!['patrol', 'manage', 'office', 'event', 'chain', 'ratway'].includes(phase)) throw new Error('请确认选择奖励后返回的阶段。');
  st.phase = 'manage'; st.ratway = false;
  if (phase === 'patrol') {
    const destinations = list(board.destinations, '今日实际目的地');
    if (!destinations.length || destinations.length > 6 || new Set(destinations).size !== destinations.length) throw new Error('请核对今日可选目的地。');
    st.destinations = destinations.map(n => int(n, 0, 15, '目的地'));
    if (st.destinations.some(n => n === position || T.stepsBetween(position, n) > 6)) throw new Error('普通巡逻目的地必须在前方六格内。');
    st.phase = 'patrol'; st.destBase = st.destinations.length + ['whip', 'stake', 'forged'].filter(t => tools.includes(t)).length;
  } else if (phase === 'ratway') {
    st.phase = 'patrol'; st.ratway = true; st.destinations = Array.from({ length: 15 }, (_, n) => n + 1);
  } else if (phase === 'office') {
    if (position !== 0) throw new Error('审讯室操作需要典狱长在1号位置。');
    st.phase = 'office';
  } else if (phase === 'event') {
    if (!Object.hasOwn(T.EVENTS, board.eventKind) || !st.cells[position].inmate) throw new Error('请确认当前事件类型和事件房间囚犯。');
    st.phase = 'event'; st.event = { kind: board.eventKind, cell: position };
    if (/^usurp[1-4]$/.test(board.eventKind) && Number(board.eventKind.slice(-1)) !== board.usurpStage) throw new Error('当前僭越事件与事件进度不一致，请核对。');
  } else if (phase === 'chain') {
    const eater = st.cells[position].inmate;
    if (!eater) throw new Error('连吃阶段需要当前房间囚犯。');
    const count = int(board.chainConsumed, 1, 20, '本次连吃已吞口数');
    st.used.devour = 1; st.stats.devours = count;
    // This is an observation, not a full save. Do not fabricate historical bites or deserialize it.
    st.chaining = { eater: eater.id, at: 0, bites: [] }; st.observedChainConsumed = count;
    if (!T.chainOptions(st).length) throw new Error('当前没有连吃目标，请核对阶段和囚犯。');
  }
  if (pendingKind) {
    const ids = list(board.offerIds, pendingKind === 'gift' ? '实际可选珍藏' : '实际可选刑具');
    if (!ids.length || new Set(ids).size !== ids.length || ids.some(id => !Object.hasOwn(pendingKind === 'gift' ? T.GIFTS : T.TOOLS, id))) throw new Error('请核对当前实际可选奖励。');
    if (pendingKind === 'tool' && (ids.length > 3 || ids.some(id => tools.includes(id)))) throw new Error('待选刑具不能重复持有，且最多三项。');
    st.pending = [{ kind: pendingKind, options: [...ids], rerolls: 0 }];
  }
  return { state: st, board, notes: [] };
}
function actionLabel(action) {
  const cell = n => `${n + 1}号牢房`;
  switch (action.type) {
    case 'patrol': return action.cell === 0 ? '前往审讯室' : `前往${cell(action.cell)}`;
    case 'devour': return `吞噬${cell(action.to)}的囚犯`;
    case 'chain': return `继续吞噬${cell(action.to)}的囚犯`;
    case 'stopchain': return '停止连吃，保留当前囚犯';
    case 'swap': return '替换当前囚犯';
    case 'end': return '结束本日';
    case 'event': return action.accept ? '接受当前事件' : '跳过当前事件';
    case 'office': return action.choice === 'instigate' ? '煽动：添加一个事件' : '赏赐：随机一名囚犯升阶';
    case 'choose': { const data = require('./game-metadata.json'); return `选择${[...data.tools, ...data.gifts].find(x => x.id === action.id)?.name || action.id}`; }
    default: throw new Error('策略返回未支持操作，已停止显示建议。');
  }
}
function validationFor(runtime) {
  const file = path.join(runtime.baseDir, 'training', 'game-validation.json');
  if (!fs.existsSync(file)) return { ready: false, selected: 'historical', reason: '尚无匹配当前修正规则的独立验证；当前使用历史参数，旧百万局结论不适用于本次修正。' };
  try {
    const bytes = fs.readFileSync(file);
    if (bytes.length > 2 * 1024 * 1024) throw new Error('report too large');
    const report = JSON.parse(bytes.toString('utf8'));
    if (report.schema !== 'talos-game-validation-v1' || report.correctedSourceHash !== runtime.correctedSourceHash || report.oldPolicyHash !== runtime.oldPolicyHash || !['historical', 'baseline'].includes(report.selection?.selected)) throw new Error('report mismatch');
    return { ready: true, selected: report.selection.selected, generatedAt: report.generatedAt || null, reason: '已读取与当前源码及历史策略哈希匹配的新规则独立验证报告；结论只适用于报告的模拟范围。' };
  } catch {
    throw new Error('独立验证报告损坏或与当前规则/策略不匹配，已停止建议。请重新安装完整补丁，不能退回未验证参数。');
  }
}
function recommend(board, options = {}) {
  const runtime = options.runtime || loadRuntime();
  if (!runtime.corrected) throw new Error('实机建议只使用明确标记的修正规则；原版入口用于旧存档复现。');
  const mapped = mapObserved(board, runtime), state = runtime.publicView(mapped.state), validation = validationFor(runtime);
  const params = validation.selected === 'baseline' ? runtime.baselineParams : runtime.effectiveHistoricalParams;
  const action = runtime.S.decide(state, params, runtime.S.rng32(1));
  if (!action) throw new Error('当前没有可推荐操作，请核对游戏阶段。');
  const legal = runtime.S.apply(copy(state), copy(action));
  if (!legal.ok) throw new Error(`策略操作与当前局面不一致：${legal.reason || '请重新核对截图'}。`);
  return {
    ok: true, primary: { label: actionLabel(action), action: copy(action) },
    notes: [...runtime.notes, validation.reason, ...(['consume', 'incident'].includes(mapped.state.objective.id) ? ['当前任务可按卡面规则处理，但不属于旧训练默认抽取的king/walk任务分布；不能继承其收益结论。'] : []), '当前为单步规则策略建议，未将启发式评分当作成功率或预期彩数。'],
    validation, source: 'corrected-historical-parameters',
    runtimeVersion: runtime.runtimeVersion, sourceCommit: SOURCE_COMMIT,
    correctedSourceHash: runtime.correctedSourceHash, oldPolicyHash: runtime.oldPolicyHash,
  };
}
module.exports = { loadRuntime, mapObserved, recommend, publicView, validationFor, correctedSources, replaceExactly, EXPECTED, EXPECTED_POLICY_HASH, SOURCE_COMMIT, RUNTIME_VERSION };
