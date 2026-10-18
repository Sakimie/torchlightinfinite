'use strict';

// Independently implemented from the documented game mechanics. No upstream runtime or assets.
// Unknown probabilities and ordering are a practice model, not measurements of the live game.
const BEASTS = Object.freeze(['mammoth', 'lion', 'wolf', 'rat']);
const RAINBOW = 5, SUPREME = 4, INSTIGATE_EVENTS = 1;
const toolNames = {
  coin: '手足的命运硬币', shackles: '长链脚镣', blacklist: '执行官的黑名册',
  scapegoat: '替罪人的断头台', maiden: '切开的人形棺', rope: '绞首绳',
  collar: '疯兽的倒刺项圈', revenge: '复仇者之匣', cage: '拥挤兽笼',
  saw: '锈锯', whip: '棘刺铁鞭', skewer: '鼠头串：硕鼠', muzzle: '狂兽的剧痛口枷',
  tongs: '恶欲铁钳：巨象', ironbox: '蠕动铁盒：硕鼠', keys: '遗落的钥匙串',
  fire: '灼痛的烙铁', hook: '挑拣钩', dogtag: '重刑铭牌', stake: '火刑柱',
};
const TOOLS = Object.freeze(Object.fromEntries(Object.entries(toolNames).map(([id, name]) => [id, Object.freeze({ id, name })])));
const TOOL_IDS = Object.freeze(Object.keys(TOOLS));
const eventNames = {
  chain: '连锁反应', execution: '处刑效率', trickster: '暗箱操作', appetite: '贪婪食欲',
  convict: '高危囚犯', crow: '鸦语', freedom: '自由的滋味', ratway: '暗渠鼠道', hunt: '寻“宝”',
  usurp1: '僭越·其一', usurp2: '僭越·其二', usurp3: '僭越·其三', usurp4: '僭越·其四',
};
const EVENTS = Object.freeze(Object.fromEntries(Object.entries(eventNames).map(([id, name]) => [id, Object.freeze({ id, name })])));
const EVENT_IDS = Object.freeze(Object.keys(EVENTS));
const EVENT_POOL = Object.freeze(EVENT_IDS.filter(id => !/^usurp[234]$/.test(id)));
const USURP = Object.freeze({ usurp1: { count: 1, by: 1 }, usurp2: { count: 1, by: 2 }, usurp3: { count: 2, by: 2 }, usurp4: { count: 15, by: 2 } });
const GIFTS = Object.freeze({ ring: { name: '三首扳指' }, horn: { name: '斗争铜号' }, wine: { name: '赤色佳酿' }, brand: { name: '原罪烙印' } });
const GIFT_IDS = Object.freeze(Object.keys(GIFTS));
// Movement and incident thresholds come from cards; the consume threshold remains an explicit assumption.
const OBJECTIVES = Object.freeze({ other: { need: 0 }, king: { need: 1 }, walk: { need: 18 }, incident: { need: 2 }, consume: { need: 4 } });
const MODEL = Object.freeze({
  tierWeights: [40, 38, 12, 8, 2], replacementWeights: [52, 32, 9, 6, 1.5], initialEvents: 2, eventChance: 0.05,
  patrolRange: 6, destinations: 3,
});
const RULE_ASSUMPTIONS = Object.freeze([
  '口枷按吞噬后、囚犯升阶前的人数是否不超过三名判定；该结算时点未验证。',
  '练习模型开门阶级权重40:38:12:8:2，替换52:32:9:6:1.5，种族等概率，来自旧公开实测近似、并非官方规则；初始2个事件、封门重置5%生成事件、事件等概率属于练习假设。',
  '普通练习开局提供一件刑具选择、达成任务再提供一次刑具；每捕获绮彩途中提供一次珍藏；默认任务为一名至臻。',
  '硬币每天至多判定一次，成功时在所有同种同阶配对中等概率抽一对；铁盒每天生成一处鼠巢，铁钳每天随机标记一名巨象。多效果按本引擎固定顺序结算。',
  '任务阈值移动18格、事件2次来自卡面；吞噬4次仅来自玩家观察，仍待验证。稀缺种族并列、批量随机目标、珍藏烙印的选种采用本地练习规则。黑名册只在普通巡逻主开门触发，事件/珍藏开门能否触发尚未实测。',
]);
const UNSUPPORTED_NOTES = Object.freeze([
  { id: 'lion-trap', names: ['染红的捕兽夹：狂狮'], reason: '寻猎的次数消耗及标记持续时间尚未核实。' },
  { id: 'shared-fortune', names: ['有福同享'], reason: '目标选择与结算顺序尚未核实。' },
  { id: 'nemesis', names: ['宿敌'], reason: '打开食物的完整条件尚未核实。' },
  { id: 'shared-transfer', names: ['同罪移押'], reason: '复制目标和触发顺序尚未核实。' },
  ...['pestle', 'crown', 'maul', 'ravenous', 'guillotine', 'scales', 'shortchain', 'axe', 'forged'].map(id => ({ id, names: [id], reason: '尚未独立实现并验证此刑具完整规则。' })),
]);

function hashSeed(value) {
  if (Number.isInteger(value)) return (value >>> 0) || 0x9e3779b9;
  let x = 2166136261;
  for (const c of String(value ?? 'offline-game')) x = Math.imul(x ^ c.charCodeAt(0), 16777619) >>> 0;
  return x || 0x9e3779b9;
}
function random(st) {
  let x = st.rng >>> 0;
  x ^= x << 13; x ^= x >>> 17; x ^= x << 5;
  st.rng = x >>> 0 || 0x9e3779b9;
  return st.rng / 4294967296;
}
function pick(st, list) { return list.length ? list[Math.floor(random(st) * list.length)] : undefined; }
function shuffled(st, list) {
  const out = list.slice();
  for (let i = out.length - 1; i > 0; i--) { const j = Math.floor(random(st) * (i + 1)); [out[i], out[j]] = [out[j], out[i]]; }
  return out;
}
function has(st, id) { return st.tools.includes(id); }
function boardInmates(st) { return st.cells.slice(1).filter(c => c.inmate).map(c => c.inmate); }
function inmateCells(st) { return st.cells.slice(1).filter(c => c.inmate); }
function sealedCells(st) { return st.cells.slice(1).filter(c => c.sealed && !c.nest); }
function cellOf(st, id) { return st.cells.find(c => c.inmate?.id === id); }
function stepsBetween(from, to) { return (to - from + 16) % 16; }
function fail(reason) { return { ok: false, reason, events: [] }; }
function done(events = []) { return { ok: true, events }; }
function note(st, events, type, detail = {}) {
  const entry = { day: st.day, type, ...detail };
  events.push(entry);
  if (Array.isArray(st.log)) { st.log.push(entry); if (st.log.length > 300) st.log.shift(); }
}
function drawTier(st, minimum = 0, replacement = false) {
  const weights = replacement ? st.params.replacementWeights : st.params.tierWeights;
  let total = 0;
  for (let i = minimum; i <= SUPREME; i++) total += Math.max(0, Number(weights[i]) || 0);
  if (!total) return minimum;
  let x = random(st) * total;
  for (let i = minimum; i <= SUPREME; i++) { x -= Math.max(0, Number(weights[i]) || 0); if (x < 0) return i; }
  return SUPREME;
}
function makeInmate(st, { beast, tier, minimum = 0, replacement = false } = {}) {
  return { id: st.nextId++, beast: beast || pick(st, BEASTS), tier: tier ?? drawTier(st, minimum, replacement) };
}
function queueTool(st) {
  if (!st.params.rewards || st.tools.length >= TOOL_IDS.length) return;
  const options = shuffled(st, TOOL_IDS.filter(id => !st.tools.includes(id))).slice(0, 3);
  if (options.length) st.pending.push({ kind: 'tool', options, rerolls: 0 });
}
function milestone(st) {
  if (st.objective.id === 'king' && st.stats.best >= SUPREME) st.objective.progress = 1;
  const need = OBJECTIVES[st.objective.id]?.need;
  if (!st.milestone && need && st.objective.progress >= need) { st.milestone = true; queueTool(st); }
}
function upgrade(st, id, amount, events) {
  const c = cellOf(st, id);
  if (!c || amount < 0) return false;
  c.inmate.tier += amount;
  st.stats.best = Math.max(st.stats.best, Math.min(RAINBOW, c.inmate.tier));
  note(st, events, 'upgrade', { cell: c.n, amount, tier: Math.min(RAINBOW, c.inmate.tier) });
  if (c.inmate.tier >= RAINBOW) {
    const inmate = { ...c.inmate, tier: RAINBOW, day: st.day };
    st.vault.push(inmate); st.cache = st.vault.length;
    resetCell(st, c);
    note(st, events, 'rainbow', { cell: c.n, id });
    if (st.params.rewards) st.pending.push({ kind: 'gift', options: [...GIFT_IDS], rerolls: 0 });
  }
  milestone(st);
  return true;
}
function randomUpgrades(st, count, amount, events) {
  const ids = shuffled(st, boardInmates(st).map(i => i.id)).slice(0, count);
  for (const id of ids) upgrade(st, id, amount, events);
}
function newEvent(st) { return pick(st, st.eventPool); }
function resetCell(st, c, eventChance = st.params.eventChance) {
  c.sealed = true; c.inmate = null; c.hidden = null; c.known = 0; c.scratch = false; c.ivory = false; c.nest = null;
  c.event = random(st) < eventChance ? newEvent(st) : null;
  c.eventKnown = false;
  // Event locations are visible; their identity stays unknown until entered or explicitly revealed.
  c.seen = !!c.event;
}
function canEat(st, eater, meal) {
  if (!eater || !meal || eater.id === meal.id || eater.tier !== meal.tier) return false;
  const ring = { mammoth: 'lion', lion: 'wolf', wolf: 'rat', rat: 'mammoth' };
  if (ring[eater.beast] === meal.beast) return true;
  if (eater.tier >= 3 && ((eater.beast === 'mammoth' && meal.beast === 'wolf') || (eater.beast === 'lion' && meal.beast === 'rat'))) return true;
  return has(st, 'skewer') && eater.beast === 'rat' && meal.beast === 'rat';
}
function gainFor(st, from, meal) {
  const eater = typeof from === 'number' ? st.cells[from]?.inmate : from;
  const target = st.cells[meal.cell ?? meal.to]?.inmate;
  let gain = 1;
  if (has(st, 'skewer') && eater?.beast === 'rat' && target?.beast === 'rat') gain++;
  const population = boardInmates(st).length - (st.params.muzzleTiming === 'before' ? 0 : target ? 1 : 0);
  if (has(st, 'muzzle') && population <= 3) gain += 2;
  return { gain };
}
function mealOptions(st, eater) {
  return inmateCells(st).filter(c => canEat(st, eater, c.inmate)).map(c => ({ cell: c.n, scratch: false }));
}
function chainOptions(st) {
  const eater = st.chaining && cellOf(st, st.chaining.eater)?.inmate;
  return eater ? mealOptions(st, eater) : [];
}
function open(st, cell, events, options = {}) {
  if (!cell || cell.n === 0 || !cell.sealed || cell.nest) return false;
  const minimum = options.minimum ?? (has(st, 'hook') ? 1 : 0);
  const inmate = options.inmate ? makeInmate(st, options.inmate) : cell.hidden ? { ...cell.hidden, id: st.nextId++ } : makeInmate(st, { minimum });
  const event = cell.event;
  cell.inmate = inmate; cell.hidden = null; cell.known = 0; cell.sealed = false; cell.seen = true;
  cell.scratch = false; cell.nest = null;
  note(st, events, 'open', { cell: cell.n, beast: inmate.beast, tier: inmate.tier });
  st.stats.opened++;
  st.stats.best = Math.max(st.stats.best, inmate.tier);
  milestone(st);
  if (options.effects === false) return true;
  // Opening bonuses are each rolled once; secondary opens explicitly suppress all opening bonuses.
  if (has(st, 'dogtag') && random(st) < 0.25) upgrade(st, inmate.id, 1, events);
  if (has(st, 'whip') && random(st) < 0.35) upgrade(st, inmate.id, 1, events);
  if (has(st, 'fire') && cellOf(st, inmate.id) && random(st) < 0.35) {
    const target = pick(st, sealedCells(st));
    if (target) open(st, target, events, { inmate: { beast: inmate.beast, tier: inmate.tier }, effects: false });
  }
  if (has(st, 'keys') && random(st) < 0.8) {
    for (const n of [(cell.n + 15) % 16, (cell.n + 1) % 16]) if (n !== 0) open(st, st.cells[n], events, { effects: false });
  }
  if (options.patrol && has(st, 'blacklist') && st.cells.slice(1).filter(c => c.sealed).length <= 5) randomUpgrades(st, 2, 1, events);
  // Preserve the event until patrol decides whether the landed cell can resolve it.
  if (event && !cell.event) cell.event = event;
  return true;
}
function addEvents(st, count) {
  const free = st.cells.slice(1).filter(c => c.sealed && !c.nest && !c.event);
  for (const c of shuffled(st, free).slice(0, count)) { c.event = newEvent(st); c.eventKnown = false; c.seen = true; }
}
function setDestinations(st) {
  if (st.ratway) { st.destinations = st.cells.slice(1).map(c => c.n); return; }
  const size = Math.max(1, Math.min(st.params.patrolRange, st.params.destinations - (has(st, 'whip') ? 1 : 0)));
  st.destinations = shuffled(st, Array.from({ length: st.params.patrolRange }, (_, n) => (st.position + n + 1) % 16)).slice(0, size).sort((a, b) => stepsBetween(st.position, a) - stepsBetween(st.position, b));
}
function dawn(st, events, initial = false) {
  st.used = { swap: 0, devour: 0, noSwap: false, noDevour: false };
  st.chaining = null;
  // Resolve due nests before generating the next one.
  for (const c of st.cells.slice(1)) if (c.nest && c.nest.due <= st.day) {
    c.nest = null; open(st, c, events, { inmate: { beast: 'rat', tier: 2 }, effects: false });
  }
  if (has(st, 'coin')) {
    const groups = new Map();
    for (const c of inmateCells(st)) {
      const key = `${c.inmate.beast}:${c.inmate.tier}`;
      if (!groups.has(key)) groups.set(key, []);
      groups.get(key).push(c.n);
    }
    const pairs = [];
    for (const cells of groups.values()) for (let i = 0; i < cells.length; i++) for (let j = i + 1; j < cells.length; j++) pairs.push([cells[i], cells[j]]);
    if (pairs.length && random(st) < 0.6) {
      const pair = pick(st, pairs);
      if (random(st) < 0.5) pair.reverse();
      const survivor = st.cells[pair[1]].inmate.id;
      resetCell(st, st.cells[pair[0]]); upgrade(st, survivor, 2, events);
    }
  }
  for (const c of st.cells) c.ivory = false;
  if (has(st, 'tongs')) { const elephant = pick(st, inmateCells(st).filter(c => c.inmate.beast === 'mammoth')); if (elephant) elephant.ivory = true; }
  if (has(st, 'ironbox')) { const c = pick(st, sealedCells(st).filter(c => !c.event)); if (c) { c.nest = { due: st.day + 2 }; c.seen = true; } }
  st.prey = st.prey.filter(p => cellOf(st, p.lion) && cellOf(st, p.prey) && p.rounds > 0);
  for (const p of st.prey) if ((p.next ?? st.day) <= st.day) { p.rounds--; p.next = st.day + 1; }
  st.prey = st.prey.filter(p => p.rounds > 0);
  st.phase = 'patrol';
  setDestinations(st);
}
function newGame(options = {}) {
  const params = { rewards: true, startTools: 1, drillDays: 20, ...MODEL, ...(options.params || {}) };
  params.patrolRange = Math.max(1, Math.min(15, Math.floor(params.patrolRange)));
  params.destinations = Math.max(1, Math.min(params.patrolRange, Math.floor(params.destinations)));
  const st = {
    version: 1, rng: hashSeed(options.seed), nextId: 1, params, day: 1, maxDays: params.drillDays,
    position: 0, phase: 'patrol', destinations: [], tools: [], vault: [], cache: 0, pending: [],
    cells: Array.from({ length: 16 }, (_, n) => ({ n, sealed: n !== 0, inmate: null, hidden: null, known: 0, seen: false, event: null, eventKnown: false, ivory: false, scratch: false, nest: null })),
    used: {}, stats: { best: 0, opened: 0, devours: 0, swaps: 0, walked: 0, incidents: 0 },
    objective: { id: options.objective || 'king', progress: 0 }, milestone: false,
    eventPool: [...EVENT_POOL], event: null, chain: false, chaining: null, ratway: false, prey: [], log: [],
  };
  if (!Object.hasOwn(OBJECTIVES, st.objective.id)) st.objective.id = 'other';
  addEvents(st, params.initialEvents);
  dawn(st, [], true);
  for (let i = 0; i < Math.max(0, Math.min(TOOL_IDS.length, params.startTools)); i++) queueTool(st);
  return st;
}
function patrol(st, destination) {
  if (st.pending.length) return fail('请先选择奖励。');
  if (st.phase !== 'patrol' || !st.destinations.includes(destination)) return fail('当前目的地不可选。');
  const events = [], wasRatway = !!st.ratway;
  const distance = stepsBetween(st.position, destination);
  const path = Array.from({ length: distance }, (_, i) => (st.position + i + 1) % 16);
  if (st.chain) {
    for (const n of path) if (n) open(st, st.cells[n], events, { effects: false });
    st.chain = false;
  }
  st.position = destination; st.destinations = []; st.ratway = false;
  st.stats.walked += distance;
  if (st.objective.id === 'walk') st.objective.progress += distance;
  milestone(st);
  // A tunnel relocation is not treated as walking four cells for shackles.
  if (!wasRatway && has(st, 'shackles') && distance >= 4 && random(st) < 0.4) randomUpgrades(st, 1, 1, events);
  if (destination === 0) st.phase = 'office';
  else {
    const c = st.cells[destination];
    open(st, c, events, { patrol: true });
    c.seen = true;
    if (c.event && c.inmate) {
      st.event = { kind: c.event, cell: destination }; c.eventKnown = true; st.phase = 'event';
    } else st.phase = 'manage';
  }
  note(st, events, 'patrol', { cell: destination, distance });
  return done(events);
}
function eat(st, from, to, biteNumber, events) {
  const eater = st.cells[from]?.inmate, meal = st.cells[to]?.inmate;
  const id = eater.id, oldBeast = eater.beast;
  let gain = gainFor(st, from, { cell: to }).gain;
  resetCell(st, st.cells[to]);
  st.stats.devours++;
  if (st.objective.id === 'consume') st.objective.progress++;
  milestone(st);
  if (has(st, 'collar') && biteNumber >= 2 && random(st) < 0.8) gain++;
  if (has(st, 'saw') && random(st) < 0.25) gain++;
  eater.beast = pick(st, BEASTS);
  upgrade(st, id, gain, events);
  note(st, events, 'devour', { from, to, gain, oldBeast, mealBeast: meal.beast });
  if (has(st, 'cage') && random(st) < 0.4) open(st, st.cells[to], events);
  const current = cellOf(st, id);
  if (current && has(st, 'revenge') && random(st) < 0.3) {
    const preyBeasts = BEASTS.filter(beast => canEat(st, current.inmate, { id: -1, beast, tier: current.inmate.tier }));
    const target = pick(st, sealedCells(st));
    if (target && preyBeasts.length) open(st, target, events, { inmate: { beast: pick(st, preyBeasts), tier: current.inmate.tier } });
  }
  return id;
}
function devour(st, from, to) {
  if (to === undefined) { to = from; from = st.position; }
  if (st.pending.length || st.phase !== 'manage' || st.chaining || from !== st.position) return fail('当前不能开始吞噬。');
  if (st.used.devour || st.used.swap || st.used.noDevour) return fail('今日吞噬或替换次数已用完。');
  if (!canEat(st, st.cells[from]?.inmate, st.cells[to]?.inmate)) return fail('囚犯不满足同阶克制关系。');
  const events = [];
  st.used.devour = 1;
  const id = eat(st, from, to, 1, events);
  st.chaining = cellOf(st, id) ? { eater: id, at: 1, bites: [to] } : null;
  if (st.chaining && !chainOptions(st).length) st.chaining = null;
  return done(events);
}
function chainBite(st, to) {
  if (st.pending.length || !st.chaining || st.phase !== 'manage') return fail('当前没有可继续的连吃。');
  const c = cellOf(st, st.chaining.eater);
  if (!c || !canEat(st, c.inmate, st.cells[to]?.inmate)) return fail('当前目标不可吞噬。');
  const events = [], count = Math.max(1, st.chaining.at) + 1, bites = [...st.chaining.bites, to];
  const id = eat(st, c.n, to, count, events);
  st.chaining = cellOf(st, id) ? { eater: id, at: count, bites } : null;
  if (st.chaining && !chainOptions(st).length) st.chaining = null;
  return done(events);
}
function stopChain(st) {
  if (st.pending.length || !st.chaining) return fail('当前没有连吃。');
  st.chaining = null; return done();
}
function swap(st, cell = st.position) {
  if (st.pending.length || st.phase !== 'manage' || st.chaining || cell !== st.position || !st.cells[cell]?.inmate) return fail('当前不能替换囚犯。');
  if (st.used.devour || st.used.swap || st.used.noSwap) return fail('今日吞噬或替换次数已用完。');
  const events = [], c = st.cells[cell], previous = c.inmate, hadIvory = c.ivory;
  st.used.swap = 1; st.stats.swaps++;
  let minimum = has(st, 'rope') ? 2 : 0;
  if (has(st, 'scapegoat') && inmateCells(st).length >= 3) {
    const others = shuffled(st, inmateCells(st).filter(x => x.n !== cell)).slice(0, 2);
    for (const other of others) resetCell(st, other);
    minimum = Math.min(RAINBOW, Math.max(minimum, previous.tier + 1));
  }
  const replacement = makeInmate(st, { tier: minimum >= RAINBOW ? RAINBOW : undefined, minimum: Math.min(SUPREME, minimum), replacement: true });
  if (has(st, 'stake') && previous.tier < 3 && random(st) < 0.4) replacement.tier = Math.max(3, replacement.tier);
  c.inmate = replacement; c.ivory = false;
  if (replacement.tier >= RAINBOW) upgrade(st, replacement.id, 0, events);
  else { st.stats.best = Math.max(st.stats.best, replacement.tier); milestone(st); }
  if (has(st, 'maiden') && cellOf(st, replacement.id)) {
    const others = boardInmates(st).filter(i => i.id !== replacement.id);
    const higher = others.filter(i => i.tier > replacement.tier).length;
    const lower = others.filter(i => i.tier < replacement.tier).length;
    if (higher === lower) upgrade(st, replacement.id, 3, events);
  }
  if (hadIvory && has(st, 'tongs')) randomUpgrades(st, 4, 1, events);
  note(st, events, 'swap', { cell, previousTier: previous.tier, tier: Math.min(RAINBOW, replacement.tier) });
  return done(events);
}
function endDay(st) {
  if (st.pending.length || st.chaining || st.phase !== 'manage') return fail('请先完成当前巡逻、事件或奖励。');
  const events = [];
  if (st.day >= st.maxDays) { st.phase = 'ended'; st.destinations = []; note(st, events, 'ended', { rainbows: st.vault.length }); }
  else { st.day++; dawn(st, events); }
  return done(events);
}
function officeAct(st, choice) {
  if (st.pending.length || st.phase !== 'office' || !['instigate', 'reward'].includes(choice)) return fail('审讯室操作不可用。');
  const events = [];
  if (choice === 'instigate') addEvents(st, INSTIGATE_EVENTS);
  else randomUpgrades(st, 1, 1, events);
  st.phase = 'manage';
  note(st, events, 'office', { choice });
  return done(events);
}
function resolveEvent(st, accept = true) {
  if (st.pending.length || st.phase !== 'event' || !st.event || !Object.hasOwn(EVENTS, st.event.kind)) return fail('当前没有可处理事件。');
  const { kind, cell } = st.event, events = [];
  st.event = null; st.cells[cell].event = null; st.cells[cell].eventKnown = false; st.phase = 'manage';
  if (!accept) return done(events);
  st.stats.incidents++;
  if (st.objective.id === 'incident') st.objective.progress++;
  milestone(st);
  if (USURP[kind]) {
    const spec = USURP[kind]; randomUpgrades(st, spec.count, spec.by, events);
    st.eventPool = st.eventPool.filter(id => !/^usurp[1-4]$/.test(id));
    if (kind !== 'usurp4') st.eventPool.push(`usurp${Number(kind.slice(-1)) + 1}`);
    // A stage already present behind an unopened door must advance too.
    for (const c of st.cells.slice(1)) if (c.event && /^usurp[1-4]$/.test(c.event)) c.event = kind === 'usurp4' ? newEvent(st) : `usurp${Number(kind.slice(-1)) + 1}`;
  } else if (kind === 'chain') st.chain = true;
  else if (kind === 'execution') {
    const here = st.cells[cell].inmate;
    if (here) {
      const prey = inmateCells(st).filter(c => c.n !== cell && c.inmate.tier <= here.tier && canEat(st, { ...here, tier: c.inmate.tier }, c.inmate));
      for (const c of prey) resetCell(st, c);
      upgrade(st, here.id, prey.length, events);
    }
  } else if (kind === 'trickster') {
    const here = st.cells[cell].inmate;
    if (here) {
      const counts = Object.fromEntries(BEASTS.map(beast => [beast, boardInmates(st).filter(i => i.beast === beast).length]));
      const minimum = Math.min(...Object.values(counts));
      here.beast = pick(st, BEASTS.filter(beast => counts[beast] === minimum));
      if (here.tier < SUPREME) upgrade(st, here.id, SUPREME - here.tier, events);
    }
  } else if (kind === 'appetite') {
    for (let n = 0; n < 5; n++) {
      const inmates = boardInmates(st), minimum = Math.min(...inmates.map(i => i.tier));
      const chosen = pick(st, inmates.filter(i => i.tier === minimum));
      if (chosen && random(st) < 0.5) upgrade(st, chosen.id, 1, events);
    }
  } else if (kind === 'convict') {
    const target = pick(st, sealedCells(st)); if (target) open(st, target, events, { minimum: 3 });
  } else if (kind === 'freedom') {
    for (const c of shuffled(st, sealedCells(st)).slice(0, 3)) open(st, c, events);
  } else if (kind === 'crow' || kind === 'hunt') {
    const count = kind === 'crow' ? 4 : 15;
    for (const c of shuffled(st, sealedCells(st)).slice(0, count)) {
      if (!c.hidden) c.hidden = makeInmate(st);
      if (kind === 'crow' || c.hidden.tier <= 1) { c.known = 1; c.seen = true; c.eventKnown = true; }
    }
  } else if (kind === 'ratway') {
    st.ratway = true; st.phase = 'patrol'; setDestinations(st);
  }
  note(st, events, 'event', { kind });
  return done(events);
}
function useGift(st, id, events) {
  if (id === 'ring') {
    for (const c of shuffled(st, sealedCells(st)).slice(0, 3)) open(st, c, events);
  } else if (id === 'wine') {
    const ids = boardInmates(st).filter(i => i.tier < 3).map(i => i.id);
    for (const inmateId of ids) upgrade(st, inmateId, 1, events);
  } else if (id === 'brand') {
    const candidates = shuffled(st, inmateCells(st).filter(c => !mealOptions(st, c.inmate).length)).slice(0, 4);
    for (const c of candidates) c.inmate.beast = pick(st, BEASTS.filter(beast => beast !== c.inmate.beast));
  } else if (id === 'horn') {
    // Each initially present inmate may eat at most once; earlier removals can cancel later turns.
    for (const eaterId of shuffled(st, boardInmates(st).map(i => i.id))) {
      const c = cellOf(st, eaterId);
      if (!c) continue;
      const target = pick(st, mealOptions(st, c.inmate));
      if (target) eat(st, c.n, target.cell, 1, events);
    }
  }
}
function choose(st, id) {
  const pending = st.pending[0];
  if (!pending || !pending.options.includes(id)) return fail('当前奖励中没有该选项。');
  if (pending.kind === 'tool' && (!Object.hasOwn(TOOLS, id) || st.tools.includes(id))) return fail('该刑具不可选择。');
  if (pending.kind === 'gift' && !Object.hasOwn(GIFTS, id)) return fail('该珍藏不可选择。');
  st.pending.shift();
  const events = [];
  if (pending.kind === 'tool') st.tools.push(id);
  else useGift(st, id, events);
  // Do not change phase; pending rewards are an overlay over patrol/manage/chain/office.
  if (st.chaining && !chainOptions(st).length) st.chaining = null;
  note(st, events, 'choose', { kind: pending.kind, id });
  return done(events);
}
function editCell(st, n, spec) {
  if (!Number.isInteger(n) || n < 1 || n > 15 || !spec) return fail('牢房无效。');
  if (spec.beast && !BEASTS.includes(spec.beast)) return fail('种族无效。');
  if (spec.tier !== undefined && (!Number.isInteger(spec.tier) || spec.tier < 0 || spec.tier > 4)) return fail('稀有度无效。');
  const c = st.cells[n];
  Object.assign(c, { sealed: true, inmate: null, hidden: null, known: 0, scratch: false, ivory: false, event: null, eventKnown: false, nest: null, seen: !!spec.seen });
  if (['open', 'inmate'].includes(spec.kind)) { c.sealed = false; c.inmate = makeInmate(st, spec); c.seen = true; }
  else if (spec.kind === 'empty') c.sealed = false;
  else if (spec.kind === 'hidden') { c.hidden = makeInmate(st, spec); c.known = spec.known ?? 0; c.scratch = !!spec.scratch; }
  else if (spec.kind === 'nest') c.nest = { due: spec.due ?? st.day + 2 };
  if (spec.event) { c.event = spec.event; c.eventKnown = spec.eventKnown ?? true; c.seen = true; }
  if (spec.ivory) c.ivory = true;
  return done();
}
const VERSION = 'offline-guide-v2';
function createEngine(options = {}) {
  if (options.muzzleTiming && !['after', 'before'].includes(options.muzzleTiming)) throw new Error('muzzleTiming must be before or after');
  return { ...module.exports, newGame: input => newGame({ ...input, params: { ...(input?.params || {}), muzzleTiming: options.muzzleTiming || 'after' } }) };
}
module.exports = {
  createEngine, VERSION,
  BEASTS, TOOL_IDS, TOOLS, EVENT_IDS, EVENTS, EVENT_POOL, OBJECTIVES, GIFTS, GIFT_IDS,
  USURP, INSTIGATE_EVENTS, RAINBOW, SUPREME, MODEL, RULE_ASSUMPTIONS, UNSUPPORTED_NOTES,
  newGame, patrol, endDay, devour, chainBite, stopChain, swap, resolveEvent, officeAct, choose,
  canEat, chainOptions, stepsBetween, boardInmates, gainFor, editCell, hashSeed,
  toolName: id => TOOLS[id]?.name || id, eventName: id => EVENTS[id]?.name || id,
};
