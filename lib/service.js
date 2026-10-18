'use strict';

const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const { PNG } = require('pngjs');
const { loadRuntime } = require('./runtime');
const vision = require('./offline-vision');

const ROOT = path.join(__dirname, '..');
const TOOL_NAMES = {
  revenge: '复仇者之匣', muzzle: '狂兽的剧痛口枷', saw: '锈锯', keys: '遗落的钥匙串',
  rope: '绞首绳', fire: '灼痛的烙铁', ironbox: '蠕动铁盒：硕鼠', skewer: '鼠头串：硕鼠',
  hook: '挑拣钩', pestle: '铁杵', crown: '狱王的铁冠：狂狮', maul: '象足重锤：巨象',
  coin: '手足的命运硬币', scapegoat: '替罪人的断头台', maiden: '切开的人形棺',
  shackles: '长链脚镣', blacklist: '执行官的黑名册', ravenous: '凶兽的饥饿绞链',
  collar: '疯兽的倒刺项圈', stake: '火刑柱', guillotine: '狼牙铡刀：头狼',
  dogtag: '重刑铭牌', whip: '棘刺铁鞭', tongs: '恶欲铁钳：巨象', cage: '拥挤兽笼',
  scales: '典狱官的天平', shortchain: '短链脚镣', axe: '狱卒的行刑巨斧', forged: '伪造钥匙',
};
const EVENT_NAMES = { chain: '连锁反应', execution: '处刑效率', trickster: '暗箱操作', appetite: '贪婪食欲', convict: '高危囚犯', crow: '鸦语', freedom: '自由的滋味', usurp1: '僭越·其一', usurp2: '僭越·其二', usurp3: '僭越·其三', usurp4: '僭越·其四', ratway: '暗渠鼠道', hunt: '寻“宝”' };
const GIFT_NAMES = { ring: '三首扳指', horn: '斗争铜号', wine: '赤色佳酿', brand: '原罪烙印' };
const PHASES = ['patrol', 'manage', 'office', 'event', 'tool', 'gift', 'chain', 'ratway'];
const clone = x => JSON.parse(JSON.stringify(x));
const integer = (v, lo, hi, name) => {
  if (!Number.isInteger(v) || v < lo || v > hi) throw new Error(`${name}须为${lo}至${hi}的整数。`);
  return v;
};

function policy() {
  const file = path.join(ROOT, 'models', 'trained-policy.json');
  if (!fs.existsSync(file)) return null;
  const model = JSON.parse(fs.readFileSync(file, 'utf8'));
  const r = loadRuntime();
  const hash = crypto.createHash('sha256').update(fs.readFileSync(path.join(__dirname, 'offline-engine.js'))).update(fs.readFileSync(path.join(__dirname, 'offline-solver.js'))).digest('hex');
  if (model.version !== 2 || model.engineVersion !== r.revision || model.engineSourceHash !== hash || model.ruleVersion !== r.ruleNotes.version || model.training?.configuration?.muzzleTiming !== 'after') throw new Error('策略与本地规则版本或口枷假设不一致，请重新训练，不能混用旧模型。');
  return model;
}

function bootstrap() {
  const { engine: T, revision, ruleNotes } = loadRuntime();
  const model = policy();
  return {
    tools: T.TOOL_IDS.map(id => ({ id, name: TOOL_NAMES[id] || T.toolName(id) })).concat(ruleNotes.unsupported.filter(x => Object.hasOwn(TOOL_NAMES, x.id)).map(x => ({ id: x.id, name: `${TOOL_NAMES[x.id]}（待支持）` })), [{ id: 'lion-trap', name: '染红的捕兽夹：狂狮（待支持）' }]),
    events: T.EVENT_IDS.map(id => ({ id, name: EVENT_NAMES[id] || T.eventName(id) })).concat(ruleNotes.unsupported.filter(x => ['shared-fortune', 'nemesis', 'shared-transfer'].includes(x.id)).map(x => ({ id: x.id, name: `${x.names[0]}（待支持）` }))),
    gifts: T.GIFT_IDS.map(id => ({ id, name: GIFT_NAMES[id] || id })),
    policy: model ? { ready: true, version: model.version, training: model.training, engineVersion: revision } : { ready: false, label: '本地初始策略，尚无本版本训练结果' },
    limitations: ruleNotes.limitations,
    ruleVersion: ruleNotes.version,
    offline: true,
    vision: visionProfile(),
  };
}

function demo() {
  const cells = Array.from({ length: 16 }, (_, n) => ({ n, kind: n ? 'locked' : 'office', event: false, ivory: false, scratch: false }));
  for (const [n, beast, tier] of [[2, 'lion', 3], [4, 'wolf', 3], [7, 'rat', 4], [9, 'mammoth', 2], [12, 'lion', 2]]) cells[n] = { ...cells[n], kind: 'inmate', beast, tier };
  return { day: 12, totalDays: 20, phase: 'manage', position: 2, destinations: [], tools: ['collar', 'revenge'], vault: 1, objective: 'king', objectiveProgress: 0, milestone: true, startEvent: null, usedAction: false, eventKind: 'usurp2', offerIds: [], cells, confirmed: true, needsReview: [], source: 'demo', chainArmed: false, usurpStage: 1, resumePhase: null };
}

function newBoard() {
  return { ...demo(), day: 1, position: 0, phase: 'patrol', tools: [], vault: 0, objective: 'other', milestone: false, eventKind: null, cells: Array.from({ length: 16 }, (_, n) => ({ n, kind: n ? 'locked' : 'office' })), source: 'manual', confirmed: false, needsReview: ['请逐格录入当前截图，再确认天数、位置、目的地和刑具。'], usurpStage: 'unknown' };
}

function unsupported(input, T, ruleNotes) {
  const all = [...(input.tools || []), ...(input.offerIds || []), input.eventKind, ...(input.cells || []).map(c => c.eventKind)].filter(Boolean);
  for (const name of all) {
    const missing = ruleNotes.unsupported.find(x => x.id === name || x.names.includes(name));
    if (missing) throw new Error(`暂不支持${missing.names[0]}：${missing.reason}请保留截图用于校准。`);
  }
  for (const id of input.tools || []) if (!Object.hasOwn(T.TOOLS, id)) throw new Error(`未识别的刑具：${String(id).slice(0, 50)}。不能忽略其效果后给出建议。`);
  for (const cell of input.cells || []) if (cell.eventKind && !Object.hasOwn(T.EVENTS, cell.eventKind)) throw new Error('存在未支持的狱中事变，请先核对事件。');
}

function normalizeBoard(input) {
  if (!input || typeof input !== 'object' || Array.isArray(input)) throw new Error('缺少棋盘数据。');
  const { engine: T } = loadRuntime();
  const board = clone(input);
  board.totalDays = integer(board.totalDays ?? 20, 20, 20, '普通模式天数');
  board.day = integer(board.day, 1, board.totalDays, '当前天数');
  board.position = integer(board.position, 0, 15, '典狱长位置');
  if (!PHASES.includes(board.phase)) throw new Error('请确认当前决策阶段。');
  if (!Array.isArray(board.cells) || board.cells.length !== 16) throw new Error('棋盘必须包含审讯室及15间牢房。');
  board.cells = board.cells.map((c, n) => {
    if (!c || c.n !== n) throw new Error('牢房编号必须连续且不能重复。');
    if (n === 0) return { n, kind: 'office' };
    if (!['locked', 'inmate', 'empty', 'nest'].includes(c.kind)) throw new Error(`请确认${n + 1}号牢房状态。`);
    const out = { n, kind: c.kind, event: !!c.event, ivory: !!c.ivory, scratch: !!c.scratch, seen: !!c.seen };
    if (c.kind === 'inmate') {
      if (!T.BEASTS.includes(c.beast)) throw new Error(`请确认${n + 1}号囚犯种族。`);
      if (c.tier === 5) throw new Error('绮彩囚犯会被捕获入库，请核对已捕获数量，并将离场后的牢房设置为未开启。');
      out.beast = c.beast;
      out.tier = integer(c.tier, 0, 4, '囚犯稀有度');
    }
    if (c.kind === 'nest') out.due = integer(c.due ?? Math.min(board.day + 2, 22), board.day + 1, 30, '鼠巢孵化天数');
    if (c.eventKind) out.eventKind = c.eventKind;
    if (c.prey) {
      out.prey = { cell: integer(c.prey.cell, 1, 15, '猎物位置'), rounds: integer(c.prey.rounds, 1, 9, '标记剩余天数') };
    }
    return out;
  });
  if (!Array.isArray(board.destinations)) throw new Error('请设置今日可选目的地。');
  board.destinations = [...new Set(board.destinations.map(n => integer(n, 0, 15, '目的地')))];
  if (!Array.isArray(board.tools) || board.tools.length > 10) throw new Error('请确认刑具列表。');
  board.tools = [...new Set(board.tools)];
  board.vault = integer(board.vault ?? 0, 0, 200, '已捕获绮彩数量');
  const objectiveId = board.objective === 'incidents' ? 'incident' : board.objective;
  if (!Object.hasOwn(T.OBJECTIVES, objectiveId)) throw new Error('请确认左上角任务。');
  board.objective = objectiveId;
  board.objectiveProgress = integer(board.objectiveProgress ?? 0, 0, 99, '任务进度');
  board.startEvent = board.startEvent || null;
  if (board.startEvent) throw new Error('本轮训练未覆盖特殊开局，请先使用普通20天开局。');
  board.offerIds = Array.isArray(board.offerIds) ? [...new Set(board.offerIds)] : [];
  board.usurpStage = board.usurpStage ?? 'unknown';
  if (![1, 2, 3, 4, 'done', 'unknown'].includes(board.usurpStage)) throw new Error('请确认僭越事件进度。');
  if (['tool', 'gift'].includes(board.phase) && !['patrol', 'manage', 'chain', 'office'].includes(board.resumePhase)) throw new Error('请确认选完奖励后返回的阶段，不能默认跳过巡逻或连吃。');
  return board;
}

function toState(input) {
  const runtime = loadRuntime();
  unsupported(input, runtime.engine, runtime.ruleNotes);
  const board = normalizeBoard(input);
  const { engine: T } = loadRuntime();
  const st = T.newGame({ seed: 'COACH-OBSERVED', mode: 'drill', params: { rewards: true, startTools: 0, drillDays: 20 } });
  st.day = board.day;
  st.nextId = 1;
  st.log = [];
  st.pending = [];
  st.event = null;
  st.destinations = [];
  st.cells = board.cells.map(c => {
    const out = { n: c.n, sealed: c.n !== 0, inmate: null, hidden: null, known: 0, scratch: false, nest: null, event: null, ivory: false, seen: !!c.seen };
    if (c.n === 0) return out;
    if (c.kind === 'inmate') { out.sealed = false; out.inmate = { id: st.nextId++, beast: c.beast, tier: c.tier }; out.ivory = c.ivory; }
    if (c.kind === 'empty') out.sealed = false;
    if (c.kind === 'nest') out.nest = { due: c.due };
    if (c.scratch) {
      throw new Error('划痕标记的隐藏稀有度尚未录入，请先确认标记详情；不能当作普通锁门计算。');
    }
    if (c.event) { out.event = c.eventKind || 'chain'; out.seen = true; out.eventKnown = !!c.eventKind; }
    return out;
  });
  st.tools = [...board.tools];
  st.vault = Array.from({ length: board.vault }, () => ({ id: st.nextId++, beast: 'mammoth', day: 1 }));
  st.cache = st.vault.length;
  st.position = board.position;
  st.objective = { id: board.objective, progress: board.objectiveProgress };
  const need = T.OBJECTIVES[board.objective].need;
  st.milestone = !!board.milestone || !!(need && board.objectiveProgress >= need);
  st.stats.best = Math.max(0, ...st.cells.filter(c => c.inmate).map(c => c.inmate.tier), st.vault.length ? 5 : 0);
  st.used = { swap: board.usedAction ? 1 : 0, devour: board.usedAction ? 1 : 0, noSwap: !!board.noSwap, noDevour: !!board.noDevour };
  st.chain = !!board.chainArmed;
  let usurpStage = board.usurpStage;
  if (board.phase === 'event' && /^usurp[1-4]$/.test(board.eventKind || '')) usurpStage = Number(board.eventKind.slice(-1));
  if (usurpStage === 'unknown') throw new Error('请在更多信息中确认僭越进度，避免重新模拟已经发生过的事件。');
  st.eventPool = T.EVENT_POOL.filter(id => !/^usurp[1-4]$/.test(id));
  if (usurpStage !== 'done') st.eventPool.push(`usurp${usurpStage}`);
  st.prey = [];
  for (const c of board.cells) {
    if (!c.prey) continue;
    const lion = st.cells[c.n].inmate;
    const meal = st.cells[c.prey.cell].inmate;
    if (!lion || lion.beast !== 'lion' || !meal || !T.canEat(st, lion, meal)) throw new Error('猎物标记必须指向同阶、可被该狂狮吞噬的囚犯。');
    st.prey.push({ lion: lion.id, prey: meal.id, preyBeast: meal.beast, rounds: c.prey.rounds, next: st.day + 1 });
  }
  st.phase = 'manage';
  const pendingKind = ['tool', 'gift'].includes(board.phase) ? board.phase : null;
  const phase = pendingKind ? board.resumePhase : board.phase;
  if (phase === 'patrol') {
    if (!board.destinations.length) throw new Error('请先标记今日可去的牢房。');
    for (const n of board.destinations) if (n === st.position || T.stepsBetween(st.position, n) > st.params.patrolRange) throw new Error('目的地不在典狱长前方六格内，请核对位置和目的地。');
    st.phase = 'patrol'; st.destinations = [...board.destinations];
  } else if (phase === 'ratway') {
    st.phase = 'patrol'; st.ratway = true; st.destinations = Array.from({ length: 15 }, (_, n) => n + 1);
  } else if (phase === 'office') {
    if (st.position !== 0) throw new Error('煽动或赏赐需要典狱长在审讯室。');
    st.phase = 'office';
  } else if (phase === 'event') {
    if (!Object.hasOwn(T.EVENTS, board.eventKind)) throw new Error('请先选择当前事件类型。');
    if (!st.cells[st.position].inmate) throw new Error('请录入事件房间的当前囚犯。');
    st.phase = 'event'; st.event = { kind: board.eventKind, cell: st.position };
  } else if (phase === 'chain') {
    const here = st.cells[st.position].inmate;
    if (!here) throw new Error('请录入当前正在连吃的囚犯。');
    st.used.devour = 1;
    st.chaining = { eater: here.id, at: 0, bites: [] };
    if (!T.chainOptions(st).length) throw new Error('当前局面没有连吃目标，请核对囚犯和决策阶段。');
  }
  if (pendingKind) {
    const ids = pendingKind === 'gift' ? T.GIFT_IDS : board.offerIds;
    if (!ids.length || ids.some(id => !Object.hasOwn(pendingKind === 'gift' ? T.GIFTS : T.TOOLS, id))) throw new Error('请录入实际可选的刑具或珍藏。');
    if (pendingKind === 'tool' && ids.some(id => st.tools.includes(id))) throw new Error('待选刑具不能包含已持有刑具。');
    st.pending = [{ kind: pendingKind, options: [...ids], rerolls: 0 }];
  }
  return { state: st, board };
}

function actionLabel(action) {
  const cell = n => `${String(n + 1).padStart(2, '0')}号牢房`;
  switch (action.type) {
    case 'patrol': return action.cell === 0 ? '前往审讯室' : `前往${cell(action.cell)}`;
    case 'devour': return `吞噬${cell(action.to)}的囚犯`;
    case 'chain': return `继续吞噬${cell(action.to)}`;
    case 'stopchain': return '停止连吃，保留囚犯';
    case 'swap': return '替换当前囚犯';
    case 'end': return '结束本日';
    case 'event': return action.accept ? '接受当前事件' : '跳过当前事件';
    case 'office': return action.choice === 'instigate' ? '煽动：增加一个事件' : '赏赐：随机提升囚犯';
    case 'choose': return `选择${TOOL_NAMES[action.id] || GIFT_NAMES[action.id] || action.id}`;
    default: return '核对当前操作';
  }
}

function recommend(input, options = {}) {
  if (input?.confirmed !== true) throw new Error('请先核对黄色标记、当前阶段及刑具，再点击“已核对棋盘，计算建议”。');
  const { state, board } = toState(input);
  const { solver: S, ruleNotes } = loadRuntime();
  const model = policy();
  const params = model?.params || S.PROFILES.rewards;
  const budget = Math.min(10000, Math.max(100, Number(options.budgetMs) || 1800));
  const rollouts = Math.min(1000, Math.max(4, Math.floor(Number(options.rollouts) || 64)));
  const seed = crypto.createHash('sha256').update(JSON.stringify(board)).digest().readUInt32LE(0);
  const started = Date.now();
  const job = S.solve(state, { rollouts, seed, params });
  let result;
  do { result = job.step(Math.min(80, Math.max(1, budget - (Date.now() - started)))); }
  while (!result.done && Date.now() - started < budget);
  const instant = S.instant(state, params);
  const rows = result.options?.length ? result.options : instant ? [{ action: instant.action, n: 0 }] : [];
  if (!rows.length) throw new Error('没有可用建议，请确认当前阶段和棋盘。');
  const mapped = rows.map(row => ({
    label: actionLabel(row.action), action: clone(row.action),
    mean: row.n && Number.isFinite(row.mean) ? row.mean : null,
    se: row.n && Number.isFinite(row.se) ? row.se : null,
    samples: row.n || 0,
    reason: row.n ? `基于当前已确认局面，随机模拟后续${row.n}次，比较本局最终绮彩数量；未知牢房在每次模拟中重新抽样。` : '当前规则下只有一个可比较操作，未产生抽样收益估计。',
  }));
  const warnings = ['这是近似规则下的期望收益建议，不保证真实游戏的绝对最优。'];
  if (!model) warnings.push('训练尚未完成，当前使用修正规则下的原始策略。');
  if (mapped.length > 1 && Number.isFinite(rows[1]?.gapSe) && (rows[0].mean - rows[1].mean) <= 2 * rows[1].gapSe) warnings.push('前两项抽样差距较小，当前样本不足以可靠区分。');
  if (board.tools.includes('muzzle')) warnings.push(ruleNotes.assumptions[0]);
  return { primary: mapped[0], alternatives: mapped.slice(1, 4), stats: { samples: result.rounds || 0, elapsedMs: Date.now() - started, complete: !!result.done }, warnings, ruleVersion: ruleNotes.version };
}

function mergeReading(r, previousBoard) {
  const fresh = () => ({ ...demo(), day: 1, position: 0, tools: [], vault: 0, milestone: false, phase: 'patrol', destinations: [], source: 'screenshot', usurpStage: 'unknown', resumePhase: null });
  let base = previousBoard ? normalizeBoard(previousBoard) : fresh();
  const issues = [];
  const issue = (message, cell) => issues.push(cell === undefined ? { message } : { message, cell });
  if (!r.ok) throw new Error(r.reason || '未检测到完整典狱棋盘，请先完成离线位置校准和样本学习。');
  const observedDay = r.days && Number.isInteger(r.days.days) ? Math.max(1, Math.min(20, 20 - r.days.days)) : null;
  if (previousBoard && observedDay !== null && observedDay < base.day) {
    base = fresh();
    issue('检测到天数回退，已清空上一局刑具、任务和标记；请确认新局信息。');
  }
  const board = clone(base);
  if (observedDay !== null) board.day = observedDay;
  if (!previousBoard) issue('首次识别：请确认刑具、任务和当前决策阶段。');
  board.cells = r.cells.map((c, n) => {
    if (n === 0) return { n, kind: 'office' };
    if (c.unknown) {
      issue(c.reason || '未学过或无法区分此牢房，请手动纠正并保存样本。', n);
      const was = base.cells[n];
      return was?.kind === 'inmate' ? clone(was) : { n, kind: 'locked', event: false, seen: false };
    }
    const out = { n, kind: c.kind === 'locked' ? c.nest ? 'nest' : 'locked' : c.kind, event: !!c.event, ivory: false, scratch: false, seen: c.kind === 'locked' && base.cells[n]?.kind === 'locked' ? !!base.cells[n].seen : false };
    if (out.kind === 'inmate') {
      out.beast = c.beast; out.tier = c.tier;
      const was = base.cells[n];
      if (was?.kind === 'inmate' && was.beast === c.beast) {
        if (was.tier === c.tier) out.ivory = !!was.ivory;
        if (was.ivory && (board.day !== base.day || board.position === n)) issue('请确认象牙标记是否已消耗。', n);
        if (was.prey) {
          const elapsed = Math.max(0, board.day - base.day);
          if (was.prey.rounds > elapsed) out.prey = { ...was.prey, rounds: was.prey.rounds - elapsed };
          if (elapsed || was.tier !== c.tier) issue('已按跨天减少猎物标记天数，请确认标记是否仍有效。', n);
        }
      }
    }
    if (out.kind === 'nest') { out.due = base.cells[n]?.due || board.day + 2; issue('请确认鼠巢的孵化日期。', n); }
    if (c.conf < 0.65 || c.ghost || !['locked', 'nest', 'inmate'].includes(out.kind)) issue('请确认牢房状态、种族和稀有度。', n);
    return out;
  });
  if (r.days && Number.isInteger(r.days.days)) { board.day = Math.max(1, Math.min(20, 20 - r.days.days)); if (r.days.conf < 0.65) issue('请确认当前天数。'); }
  else issue('没有可靠读取剩余天数，请确认当前天数。');
  if (r.warden) { board.position = r.warden.cell; if (r.warden.conf < 0.65) issue('请确认典狱长的位置。', board.position); }
  else issue('未找到典狱长箭头，请手动设置位置。');
  board.usedAction = false;
  board.noSwap = false; board.noDevour = false;
  board.destinations = [];
  if (r.doors?.cells?.length) { board.phase = 'patrol'; board.destinations = [...r.doors.cells]; if (r.doors.conf < 0.65) issue('请确认今日可去的目的地。'); }
  else if (r.chain) { board.phase = 'chain'; issue('检测到连吃选择，请确认当前囚犯与目标。'); }
  else if (r.decision?.office) board.phase = 'office';
  else if (r.decision?.ratway) board.phase = 'ratway';
  else if (r.decision) { board.phase = 'manage'; board.noSwap = !r.decision.replace; board.noDevour = !r.decision.consume; }
  else { board.phase = base.phase === 'patrol' ? 'manage' : base.phase; issue('当前决策按钮未可靠识别，请确认操作阶段和今日是否已操作。'); }
  if (r.offlineCalibration) {
    board.phase = base.phase;
    board.destinations = [...base.destinations];
    board.usedAction = !!base.usedAction;
    board.noSwap = !!base.noSwap;
    board.noDevour = !!base.noDevour;
    issue('本地外观识别不读取控制文字：请确认天数、位置、目的地、刑具、任务及阶段。');
  }
  if (board.day === base.day && board.position === base.position && !r.decision && board.phase === 'manage') board.usedAction = !!base.usedAction;
  const { engine: T, scanData } = loadRuntime();
  const readings = (r.tools?.tools || []).map(x => ({ ...x, sim: x.sim || (Object.hasOwn(T.TOOLS, x.id || '') ? x.id : scanData.toolNames?.[x.id]?.sim) }));
  const knownTools = readings.filter(x => x.sim && x.conf >= 0.65).map(x => x.sim);
  board.tools = [...new Set([...base.tools, ...knownTools])];
  if (knownTools.some(id => !base.tools.includes(id))) issue('识别到新增刑具，请确认实际持有项。');
  if (!readings.length || readings.some(x => !x.sim || x.conf < 0.65)) issue('请核对持有刑具，未知刑具不能直接忽略。');
  if (r.chromatic) { board.vault = r.chromatic.count; if (r.chromatic.conf < 0.65 && board.vault !== base.vault) issue('请确认已捕获绮彩数量。'); }
  if (board.objective === 'king' && (board.vault || board.cells.some(c => c.kind === 'inmate' && c.tier >= 4))) board.milestone = true;
  if (base.chainArmed && (board.day !== base.day || board.position !== base.position || (base.phase === 'patrol' && board.phase !== 'patrol'))) board.chainArmed = false;
  if (base.phase === 'event' && /^usurp[1-4]$/.test(base.eventKind || '') && board.phase !== 'event') {
    board.usurpStage = 'unknown';
    issue('刚离开僭越事件，请确认是否接受及当前僭越进度。');
  }
  if (base.phase === 'patrol' && board.phase !== 'patrol' && board.position !== base.position && board.day === base.day) {
    for (let n = (base.position + 1) % 16; ; n = (n + 1) % 16) {
      board.cells[n].seen = true;
      if (n === board.position) break;
    }
  }
  board.source = 'screenshot';
  board.needsReview = issues.map(x => x.message);
  board.confirmed = issues.length === 0 && !!previousBoard?.confirmed;
  return { board, issues, recognition: { ok: true, ms: r.ms, summary: `识别15间牢房，${issues.length}项待核对；中文界面识别尚需实机样本验证。` } };
}

function decodeImage(dataUrl) {
  if (typeof dataUrl !== 'string' || !/^data:image\/png;base64,[A-Za-z0-9+/=]+$/.test(dataUrl) || dataUrl.length > 32 * 1024 * 1024) throw new Error('请提供32MB以内的PNG截图。');
  const bytes = Buffer.from(dataUrl.slice(dataUrl.indexOf(',') + 1), 'base64');
  if (bytes.length < 24 || bytes.toString('hex', 0, 8) !== '89504e470d0a1a0a') throw new Error('截图不是有效PNG。');
  const width = bytes.readUInt32BE(16), height = bytes.readUInt32BE(20);
  if (!width || !height || width * height > 24_000_000) throw new Error('截图尺寸过大，请缩小到24百万像素以内。');
  let img;
  try { img = PNG.sync.read(bytes, { checkCRC: true }); } catch { throw new Error('PNG截图损坏，无法读取。'); }
  return { width: img.width, height: img.height, data: img.data };
}

function profileFile() {
  return path.join(process.env.TORCHLIGHT_DATA_DIR || path.join(ROOT, '.cache', 'user'), 'vision-profile.json');
}

function visionProfile() {
  const file = profileFile();
  if (!fs.existsSync(file)) return { ready: false, profile: null, summary: '尚未校准：标记16个牢房中心，并学习一张人工核对的棋盘。' };
  try {
    const profile = JSON.parse(fs.readFileSync(file, 'utf8'));
    const valid = vision.validateProfile(profile);
    if (!valid.ok) return { ready: false, profile: null, summary: valid.reason };
    const count = Object.values(valid.counts.kind).reduce((sum, n) => sum + n, 0);
    return { ready: count > 0, profile, summary: `本机已保存${count}个牢房外观样本，全部离线识别。`, counts: valid.counts };
  } catch { return { ready: false, profile: null, summary: '本地校准文件损坏，请重新导入或学习样本。' }; }
}

function importVision(profile) {
  if (!profile || typeof profile !== 'object' || Array.isArray(profile) || JSON.stringify(profile).length > 2 * 1024 * 1024) throw new Error('校准配置应为2MB以内的JSON对象。');
  const valid = vision.validateProfile(profile);
  if (!valid.ok) throw new Error(valid.reason);
  // Keep only the documented data schema. Imported arbitrary fields, paths and
  // image contents are never persisted or executed.
  const calibration = { width: profile.calibration.width, height: profile.calibration.height, points: profile.calibration.points.map(p => ({ x: p.x, y: p.y })) };
  if (profile.calibration.radius !== undefined) calibration.radius = profile.calibration.radius;
  const clean = { version: profile.version, calibration, samples: { kind: clone(profile.samples.kind), beast: clone(profile.samples.beast), tier: clone(profile.samples.tier) }, updatedAt: new Date().toISOString() };
  const file = profileFile();
  fs.mkdirSync(path.dirname(file), { recursive: true });
  const temporary = `${file}.${process.pid}.tmp`;
  fs.writeFileSync(temporary, JSON.stringify(clean), { mode: 0o600 });
  fs.renameSync(temporary, file);
  return visionProfile();
}

function learnVision(payload) {
  if (!payload || typeof payload !== 'object') throw new Error('缺少已核对截图和棋盘。');
  const img = decodeImage(payload.dataUrl);
  const board = payload.board;
  const old = visionProfile().profile;
  return importVision(vision.trainProfile(img, board, payload.calibration, old));
}

function resetVision() {
  const file = profileFile();
  if (fs.existsSync(file)) fs.unlinkSync(file);
  return visionProfile();
}

function scan(dataUrl, previousBoard) {
  const img = decodeImage(dataUrl);
  const state = visionProfile();
  return mergeReading(vision.scan(img, state.profile), previousBoard);
}

function report() {
  const file = path.join(ROOT, 'reports', 'training-report.json');
  return fs.existsSync(file) ? JSON.parse(fs.readFileSync(file, 'utf8')) : { status: 'pending', message: '训练尚未完成。' };
}

module.exports = { bootstrap, demo, newBoard, scan, recommend, report, normalizeBoard, toState, mergeReading, actionLabel, visionProfile, learnVision, importVision, resetVision };
