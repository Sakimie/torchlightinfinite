'use strict';

// Original visible-state policy and a bounded Monte Carlo search over legal next actions.
// The parameter search tunes these heuristics; this module does not load another project's policy.
const localEngine = require('./offline-engine');
const DEFAULTS = Object.freeze({
  wOpen: 1.5, wNow: 1, wWalk: 0.65, wStep: 0.25, wInstigate: 0.6,
  wCrow: 1, wChain: 1, wStair: 0.2, swapMaxTier: 2,
  minDevour: 0, stopMargin: 0, rainbowMult: 2, tierBase: 2,
});
const PROFILES = Object.freeze({ rewards: DEFAULTS });
const UNUSED_PARAMETERS = Object.freeze(['toolSets']);
function clone(value) { return JSON.parse(JSON.stringify(value)); }
function rng32(seed) {
  let value = localEngine.hashSeed(seed);
  return () => {
    value ^= value << 13; value ^= value >>> 17; value ^= value << 5;
    value = value >>> 0 || 0x9e3779b9;
    return value / 4294967296;
  };
}
function createSolver(T = localEngine) {
  const has = (st, tool) => st.tools.includes(tool);
  const inmates = st => st.cells.slice(1).filter(c => c.inmate);
  const unknownDoors = st => st.cells.slice(1).filter(c => c.sealed && !c.nest);
  const parameters = p => ({ ...DEFAULTS, ...(p || {}) });
  function timeLeft(st) { return Math.max(0, st.maxDays - st.day + 1); }
  function tierValue(st, tier, p) {
    if (tier >= T.RAINBOW) return p.rainbowMult;
    const base = Math.max(1.2, Number(p.tierBase) || 2);
    const value = p.rainbowMult / Math.pow(base, T.RAINBOW - tier + 0.3);
    // A low-tier inmate at the end has little remaining opportunity to produce a Rainbow.
    return value * Math.min(1, (timeLeft(st) + 1) / Math.max(2, (T.RAINBOW - tier) * 1.3));
  }
  function gainValue(st, tier, by, p) { return tierValue(st, tier + by, p) - tierValue(st, tier, p); }
  function expectedTierValue(st, minimum, p, replacement = false) {
    const weights = replacement ? st.params.replacementWeights : st.params.tierWeights;
    const total = weights.slice(minimum).reduce((a, b) => a + b, 0);
    if (!total) return tierValue(st, minimum, p);
    return weights.slice(minimum).reduce((sum, weight, k) => sum + weight * tierValue(st, k + minimum, p), 0) / total;
  }
  function visibleMealCells(st, eater) { return inmates(st).filter(c => T.canEat(st, eater, c.inmate)); }
  function opportunityCount(st, cell) { return visibleMealCells(st, cell.inmate).length; }
  function eventExpected(st, id, p) {
    const list = inmates(st), count = list.length;
    const averageUpgrade = by => count ? list.reduce((sum, c) => sum + gainValue(st, c.inmate.tier, by, p), 0) / count : 0;
    if (T.USURP[id]) {
      const { count: n, by } = T.USURP[id];
      const stair = id === 'usurp4' ? 0 : Math.min(0.65, timeLeft(st) / 30) * Number(p.wStair);
      return Math.min(n, count) * averageUpgrade(by) + stair;
    }
    if (id === 'freedom') return Math.min(3, unknownDoors(st).length) * expectedTierValue(st, has(st, 'hook') ? 1 : 0, p) * p.wOpen;
    if (id === 'convict') return unknownDoors(st).length ? expectedTierValue(st, 3, p) * p.wOpen : 0;
    if (id === 'chain') return Math.min(unknownDoors(st).length, 3) * expectedTierValue(st, 0, p) * Math.min(2, p.wChain);
    if (id === 'trickster') {
      const current = st.cells[st.position]?.inmate;
      return current ? gainValue(st, current.tier, Math.max(0, T.SUPREME - current.tier), p) : 0;
    }
    if (id === 'appetite') return averageUpgrade(1) * 2.5;
    if (id === 'execution') {
      const here = st.cells[st.position]?.inmate;
      if (!here) return 0;
      const targets = list.filter(c => c.inmate.id !== here.id && c.inmate.tier <= here.tier && T.canEat(st, { ...here, tier: c.inmate.tier }, c.inmate));
      return gainValue(st, here.tier, targets.length, p) - targets.reduce((s, c) => s + tierValue(st, c.inmate.tier, p), 0);
    }
    if (id === 'crow') return Math.min(4, unknownDoors(st).length) * 0.02 * p.wCrow;
    if (id === 'hunt') return unknownDoors(st).length * 0.006;
    if (id === 'ratway') return 0.04 + Math.max(0, ...list.map(c => opportunityCount(st, c))) * 0.01;
    return 0;
  }
  function eventPrior(st, p) {
    const pool = st.eventPool.length ? st.eventPool : T.EVENT_POOL;
    return pool.reduce((sum, id) => sum + Math.max(0, eventExpected(st, id, p)), 0) / pool.length;
  }
  function devourScore(st, eaterCell, mealCell, p, chain = false) {
    const eater = eaterCell.inmate, meal = mealCell.inmate;
    let by = T.gainFor(st, eaterCell.n, { cell: mealCell.n }).gain;
    let gain = gainValue(st, eater.tier, by, p);
    if (has(st, 'collar') && chain) gain += 0.8 * (gainValue(st, eater.tier, by + 1, p) - gainValue(st, eater.tier, by, p));
    if (has(st, 'saw')) gain += 0.25 * (gainValue(st, eater.tier, by + 1, p) - gainValue(st, eater.tier, by, p));
    let score = gain - tierValue(st, meal.tier, p);
    if (has(st, 'cage')) score += 0.4 * expectedTierValue(st, has(st, 'hook') ? 1 : 0, p) * p.wOpen;
    if (eater.tier + by < T.RAINBOW) {
      // The eater's post-meal species is random. Count future meals using all four possibilities.
      const follow = T.BEASTS.reduce((s, beast) => s + inmates(st).filter(c => c.n !== eaterCell.n && c.n !== mealCell.n && T.canEat(st, { id: eater.id, beast, tier: eater.tier + by }, c.inmate)).length, 0) / 4;
      score += Math.min(1, follow) * gainValue(st, eater.tier + by, 1, p) * 0.55;
      if (has(st, 'revenge') && unknownDoors(st).length) score += 0.3 * gainValue(st, eater.tier + by, 1, p) * 0.7;
    }
    if (has(st, 'coin')) {
      const same = inmates(st).filter(c => c.inmate.beast === meal.beast && c.inmate.tier === meal.tier).length;
      if (same === 2) score -= Math.min(0.15, timeLeft(st) * 0.015);
    }
    if (mealCell.ivory && has(st, 'tongs')) score -= 0.06;
    return score * p.wNow;
  }
  function swapScore(st, c, p) {
    if (c.inmate.tier > p.swapMaxTier && !(c.ivory && has(st, 'tongs')) && !has(st, 'scapegoat') && !has(st, 'maiden')) return -100;
    const previous = c.inmate, others = inmates(st).filter(x => x.n !== c.n);
    let minimum = has(st, 'rope') ? 2 : 0;
    let cost = tierValue(st, previous.tier, p);
    if (has(st, 'scapegoat') && others.length >= 2) {
      minimum = Math.min(T.RAINBOW, Math.max(minimum, previous.tier + 1));
      cost += 2 * others.reduce((sum, x) => sum + tierValue(st, x.inmate.tier, p), 0) / others.length;
    }
    let gain = minimum >= T.RAINBOW ? tierValue(st, T.RAINBOW, p) : expectedTierValue(st, minimum, p, true);
    if (has(st, 'stake') && previous.tier < 3) gain = 0.6 * gain + 0.4 * Math.max(gain, tierValue(st, 3, p));
    if (has(st, 'maiden') && minimum < T.RAINBOW) {
      const weights = st.params.replacementWeights, total = weights.slice(minimum).reduce((s, v) => s + v, 0);
      for (let tier = minimum; tier <= T.SUPREME; tier++) {
        const high = others.filter(x => x.inmate.tier > tier).length, low = others.filter(x => x.inmate.tier < tier).length;
        if (high === low) gain += weights[tier] / total * gainValue(st, tier, 3, p);
      }
    }
    if (c.ivory && has(st, 'tongs')) {
      const meanGain = inmates(st).reduce((sum, x) => sum + gainValue(st, x.inmate.tier, 1, p), 0) / Math.max(1, inmates(st).length);
      gain += Math.min(4, inmates(st).length) * meanGain;
    }
    return (gain - cost) * p.wNow;
  }
  function toolScore(st, id, p) {
    const list = inmates(st), days = timeLeft(st), pop = list.length;
    const meanGain = list.reduce((sum, c) => sum + gainValue(st, c.inmate.tier, 1, p), 0) / Math.max(1, pop);
    const pairs = new Map();
    for (const c of list) { const key = c.inmate.beast + c.inmate.tier; pairs.set(key, (pairs.get(key) || 0) + 1); }
    const paired = [...pairs.values()].filter(n => n > 1).length;
    // Each score is an independent expected mechanic yield, not a lookup of tool combinations.
    let value;
    switch (id) {
      case 'coin': value = 0.6 * (0.12 + paired * 0.05); break;
      case 'shackles': value = 0.4 * Math.max(meanGain, 0.075) * 0.75; break;
      case 'blacklist': value = Math.max(meanGain, 0.08) * 2 * (st.cells.slice(1).filter(c => c.sealed).length <= 6 ? 0.8 : 0.3); break;
      case 'scapegoat': value = 0.11 + (has(st, 'maiden') ? 0.03 : 0); break;
      case 'maiden': value = 0.10 + 0.08 / Math.max(1, pop); break;
      case 'rope': value = 0.065; break;
      case 'collar': value = 0.8 * Math.max(meanGain, 0.13) * 0.45; break;
      case 'revenge': value = 0.3 * Math.max(meanGain, 0.2); break;
      case 'cage': value = 0.4 * expectedTierValue(st, 0, p) * p.wOpen; break;
      case 'saw': value = 0.25 * Math.max(meanGain, 0.17); break;
      case 'whip': value = 0.35 * Math.max(meanGain, 0.12) - 0.008; break;
      case 'skewer': value = 0.04 + list.filter(c => c.inmate.beast === 'rat').length * 0.015; break;
      case 'muzzle': value = pop <= 4 ? 0.18 : 0.055; break;
      case 'tongs': value = 0.08 + list.filter(c => c.inmate.beast === 'mammoth').length * 0.02; break;
      case 'ironbox': value = expectedTierValue(st, 2, p) * 0.45; break;
      case 'keys': value = 0.8 * 0.9 * expectedTierValue(st, 0, p) * p.wOpen; break;
      case 'fire': value = 0.35 * expectedTierValue(st, 0, p) * p.wOpen; break;
      case 'hook': value = expectedTierValue(st, 1, p) - expectedTierValue(st, 0, p); break;
      case 'dogtag': value = 0.25 * Math.max(meanGain, 0.12); break;
      case 'stake': value = 0.4 * (tierValue(st, 3, p) - expectedTierValue(st, 0, p, true)); break;
      default: value = -100;
    }
    return value * days;
  }
  function giftScore(st, id, p) {
    const list = inmates(st);
    if (id === 'ring') {
      let value = Math.min(3, unknownDoors(st).length) * expectedTierValue(st, has(st, 'hook') ? 1 : 0, p) * p.wOpen;
      return value;
    }
    if (id === 'wine') return list.filter(c => c.inmate.tier < 3).reduce((s, c) => s + gainValue(st, c.inmate.tier, 1, p), 0);
    if (id === 'horn') return list.reduce((sum, c) => sum + Math.max(0, ...visibleMealCells(st, c.inmate).map(meal => devourScore(st, c, meal, p))), 0) * 0.7;
    if (id === 'brand') return Math.min(4, list.filter(c => !opportunityCount(st, c)).length) * 0.025;
    return -100;
  }
  function patrolScore(st, destination, p) {
    if (destination === 0) {
      const ups = inmates(st).reduce((sum, c) => sum + gainValue(st, c.inmate.tier, 1, p), 0) / Math.max(1, inmates(st).length);
      return Math.max(ups, eventPrior(st, p) * p.wInstigate * Math.min(1, timeLeft(st) / 5));
    }
    const c = st.cells[destination];
    let value = 0;
    if (c.sealed && !c.nest) {
      const tier = c.known ? c.hidden?.tier : undefined;
      value += p.wOpen * (Number.isInteger(tier) ? tierValue(st, tier, p) : expectedTierValue(st, has(st, 'hook') ? 1 : 0, p));
      if (has(st, 'keys')) {
        const adjacent = [(destination + 15) % 16, (destination + 1) % 16].filter(n => n && st.cells[n].sealed && !st.cells[n].nest).length;
        value += 0.8 * adjacent * expectedTierValue(st, 0, p) * p.wOpen;
      }
      if (has(st, 'fire')) value *= 1.35;
      if (has(st, 'dogtag')) value += 0.25 * 0.05;
      if (has(st, 'whip')) value += 0.35 * 0.05;
      if (has(st, 'blacklist') && st.cells.slice(1).filter(c => c.sealed).length <= 6) value += 2 * 0.12;
    }
    if (c.inmate) {
      const meals = visibleMealCells(st, c.inmate);
      value += Math.max(0, swapScore(st, c, p), ...meals.map(meal => devourScore(st, c, meal, p)));
      if (c.ivory && has(st, 'tongs')) value += 0.02;
    }
    // Only revealed markers may influence the policy. An unseen event is indistinguishable from none.
    if (c.event && c.seen) value += c.eventKnown ? Math.max(0, eventExpected(st, c.event, p)) : eventPrior(st, p);
    if (st.chain) {
      const count = st.cells.slice(1).filter(x => x.sealed && !x.nest && T.stepsBetween(st.position, x.n) <= T.stepsBetween(st.position, destination)).length;
      value += count * expectedTierValue(st, 0, p) * 0.6;
    }
    const steps = T.stepsBetween(st.position, destination);
    if (has(st, 'shackles') && steps >= 4) value += 0.4 * 0.13 * p.wWalk;
    value += steps * 0.003 * p.wStep;
    if (st.objective.id === 'walk' && !st.milestone) value += steps * 0.008;
    return value;
  }
  function actions(st) {
    if (st.phase === 'ended') return [];
    if (st.pending.length) return st.pending[0].options.map(id => ({ type: 'choose', id }));
    if (st.chaining) return [...T.chainOptions(st).map(o => ({ type: 'chain', to: o.cell })), { type: 'stopchain' }];
    if (st.phase === 'patrol') return st.destinations.map(cell => ({ type: 'patrol', cell }));
    if (st.phase === 'event') return [{ type: 'event', accept: true }, { type: 'event', accept: false }];
    if (st.phase === 'office') return [{ type: 'office', choice: 'instigate' }, { type: 'office', choice: 'reward' }];
    if (st.phase !== 'manage') return [];
    const result = [{ type: 'end' }], c = st.cells[st.position];
    if (c?.inmate && !st.used.devour && !st.used.swap) {
      if (!st.used.noDevour) for (const meal of visibleMealCells(st, c.inmate)) result.push({ type: 'devour', to: meal.n });
      if (!st.used.noSwap) result.push({ type: 'swap' });
    }
    return result;
  }
  function score(st, action, p) {
    if (action.type === 'choose') return st.pending[0].kind === 'tool' ? toolScore(st, action.id, p) : giftScore(st, action.id, p);
    if (action.type === 'patrol') return patrolScore(st, action.cell, p);
    if (action.type === 'devour' || action.type === 'chain') {
      const eater = action.type === 'chain' ? inmates(st).find(c => c.inmate.id === st.chaining.eater) : st.cells[st.position];
      return devourScore(st, eater, st.cells[action.to], p, action.type === 'chain') - Number(p.minDevour || 0) * 0.001;
    }
    if (action.type === 'stopchain') return Number(p.stopMargin || 0) * 0.005;
    if (action.type === 'swap') return swapScore(st, st.cells[st.position], p);
    if (action.type === 'event') return action.accept ? eventExpected(st, st.event.kind, p) + 0.001 : 0;
    if (action.type === 'office') {
      if (action.choice === 'instigate') return unknownDoors(st).filter(c => !c.event || !c.seen).length ? eventPrior(st, p) * p.wInstigate * Math.min(1, timeLeft(st) / 5) : 0;
      return inmates(st).reduce((sum, c) => sum + gainValue(st, c.inmate.tier, 1, p), 0) / Math.max(1, inmates(st).length);
    }
    return 0;
  }
  function label(action) {
    if (action.type === 'choose') return T.TOOLS[action.id]?.name || T.GIFTS[action.id]?.name || action.id;
    if (action.type === 'patrol') return action.cell === 0 ? '前往审讯室' : `前往${action.cell + 1}号牢房`;
    if (action.type === 'devour' || action.type === 'chain') return `吞噬${action.to + 1}号牢房`;
    if (action.type === 'swap') return '替换当前囚犯';
    if (action.type === 'office') return action.choice === 'instigate' ? '煽动' : '赏赐';
    if (action.type === 'event') return action.accept ? '接受事件' : '跳过事件';
    if (action.type === 'stopchain') return '停止连吃';
    return '结束本日';
  }
  function decide(st, params, random = () => 0.5) {
    const p = parameters(params), choices = actions(st);
    let best = null, value = -Infinity;
    for (const action of choices) {
      const current = score(st, action, p);
      // Independent policy RNG affects ties only and cannot inspect the game's hidden RNG.
      if (current > value + 1e-10 || Math.abs(current - value) <= 1e-10 && random() < 0.5) { value = current; best = action; }
    }
    return best;
  }
  function instant(st, params) {
    const action = decide(st, params);
    return action ? { action, label: label(action), why: '根据已确认的种族、稀有度、剩余天数及刑具效果比较下一步。' } : null;
  }
  function apply(st, action) {
    if (!action) return { ok: false, reason: '缺少操作。' };
    switch (action.type) {
      case 'choose': return T.choose(st, action.id);
      case 'patrol': return T.patrol(st, action.cell);
      case 'devour': return T.devour(st, st.position, action.to);
      case 'chain': return T.chainBite(st, action.to);
      case 'stopchain': return T.stopChain(st);
      case 'swap': return T.swap(st);
      case 'end': return T.endDay(st);
      case 'event': return T.resolveEvent(st, action.accept);
      case 'office': return T.officeAct(st, action.choice);
      default: return { ok: false, reason: '未知操作。' };
    }
  }
  function playout(st, params, random = rng32(1), onAction) {
    const p = parameters(params);
    for (let count = 0; count < 1800 && st.phase !== 'ended'; count++) {
      const action = decide(st, p, random);
      if (!action) throw new Error(`No legal action in phase ${st.phase}`);
      const result = apply(st, action);
      if (onAction) onAction(action, result);
      if (!result.ok) throw new Error(`Illegal ${action.type}: ${result.reason}`);
    }
    if (st.phase !== 'ended') throw new Error('Playout exceeded the action limit.');
    return st.vault.length;
  }
  function hideFuture(st, seed) {
    st.rng = T.hashSeed(seed);
    const random = rng32(seed), pool = st.eventPool.length ? st.eventPool : T.EVENT_POOL;
    for (const c of st.cells.slice(1)) {
      if (c.sealed && !c.known) c.hidden = null;
      if (!c.seen) {
        c.event = random() < st.params.eventChance ? pool[Math.floor(random() * pool.length)] : null;
        c.eventKnown = false;
      } else if (c.event && !c.eventKnown) c.event = pool[Math.floor(random() * pool.length)];
    }
    // The next ID counter can expose hidden generation history even if inmate values are removed.
    st.nextId = Math.max(0, ...T.boardInmates(st).map(i => i.id), ...st.vault.map(i => i.id)) + 1;
    st.log = [];
    return st;
  }
  function solve(st, options = {}) {
    const p = parameters(options.params), choices = actions(st), random = rng32(options.seed ?? 1);
    const rollouts = Math.min(10000, Math.max(1, Math.floor(options.rollouts || 64)));
    const rows = choices.map(action => ({ action, values: [], sum: 0, sumSquares: 0 }));
    let rounds = 0;
    const snapshot = clone(st);
    function result() {
      const summary = rows.map((r, index) => {
        const n = r.values.length, mean = n ? r.sum / n : 0;
        const variance = n > 1 ? Math.max(0, (r.sumSquares - r.sum * r.sum / n) / (n - 1)) : 0;
        return { index, action: r.action, label: label(r.action), mean, se: n ? Math.sqrt(variance / n) : 0, n };
      }).sort((a, b) => b.mean - a.mean || score(snapshot, b.action, p) - score(snapshot, a.action, p));
      if (summary.length) {
        const best = rows[summary[0].index];
        for (const row of summary) {
          const other = rows[row.index], n = Math.min(best.values.length, other.values.length);
          let sum = 0, squares = 0;
          for (let i = 0; i < n; i++) { const d = best.values[i] - other.values[i]; sum += d; squares += d * d; }
          row.gapSe = n > 1 ? Math.sqrt(Math.max(0, (squares - sum * sum / n) / (n - 1)) / n) : 0;
          delete row.index;
        }
      }
      return { done: rounds >= rollouts || !rows.length, rounds, options: summary, goal: 'rainbows' };
    }
    return {
      step(ms = 40) {
        const deadline = Date.now() + Math.max(1, Number(ms) || 1);
        do {
          if (rounds >= rollouts || !rows.length) break;
          const gameSeed = Math.floor(random() * 4294967296), policySeed = Math.floor(random() * 4294967296);
          // Common sampled futures reduce variance while keeping every candidate's branch separate.
          for (const row of rows) {
            const state = hideFuture(clone(snapshot), gameSeed);
            const first = apply(state, row.action);
            if (!first.ok) throw new Error(`Candidate ${row.action.type} rejected: ${first.reason}`);
            const value = playout(state, p, rng32(policySeed));
            row.values.push(value); row.sum += value; row.sumSquares += value * value;
          }
          rounds++;
        } while (Date.now() < deadline);
        return result();
      },
    };
  }
  return { DEFAULTS, PROFILES, UNUSED_PARAMETERS, rng32, clone, actions, instant, decide, apply, playout, hideFuture, solve };
}
module.exports = { ...createSolver(), createSolver };
