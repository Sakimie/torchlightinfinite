'use strict';
const { test } = require('node:test');
const assert = require('node:assert/strict');
const T = require('../lib/offline-engine');
const S = require('../lib/offline-solver');
function board(entries = [], tools = []) {
  const st = T.newGame({ seed: 'BOUNDARY', params: { rewards: false, startTools: 0, initialEvents: 0, eventChance: 0 } });
  for (const [cell, beast, tier] of entries) assert.equal(T.editCell(st, cell, { kind: 'open', beast, tier }).ok, true);
  st.tools = tools; st.phase = 'manage'; st.position = entries[0]?.[0] || 1;
  return st;
}
function finish(job) { let r; do { r = job.step(1000); } while (!r.done); return r; }

test('local factories are deterministic without shared RNG or network runtime', () => {
  const a = T.createEngine(), b = T.createEngine();
  assert.deepEqual(a.newGame({ seed: 'same' }), b.newGame({ seed: 'same' }));
  assert.equal(a.VERSION, 'offline-guide-v2');
  assert.equal(T.TOOL_IDS.length, new Set(T.TOOL_IDS).size);
  assert.equal(T.INSTIGATE_EVENTS, 1);
});

test('food ring, equal rarity, high-tier extra prey and rat skewer boundaries', () => {
  const st = board();
  for (const [eater, meal] of [['mammoth', 'lion'], ['lion', 'wolf'], ['wolf', 'rat'], ['rat', 'mammoth']]) {
    assert.equal(T.canEat(st, { id: 1, beast: eater, tier: 0 }, { id: 2, beast: meal, tier: 0 }), true);
    assert.equal(T.canEat(st, { id: 1, beast: eater, tier: 0 }, { id: 2, beast: meal, tier: 1 }), false);
  }
  const predator = { id: 1, beast: 'mammoth', tier: 2 }, prey = { id: 2, beast: 'wolf', tier: 2 };
  assert.equal(T.canEat(st, predator, prey), false);
  predator.tier = prey.tier = 3;
  assert.equal(T.canEat(st, predator, prey), true);
  const rat = { id: 1, beast: 'rat', tier: 0 }, rat2 = { id: 2, beast: 'rat', tier: 0 };
  assert.equal(T.canEat(st, rat, rat2), false);
  st.tools.push('skewer');
  assert.equal(T.canEat(st, rat, rat2), true);
  assert.equal(T.canEat(st, rat, rat), false);
});

test('devour and replace share a daily allowance, and invalid actions are atomic', () => {
  const st = board([[1, 'lion', 1], [2, 'wolf', 1]]);
  const before = JSON.stringify(st);
  assert.equal(T.devour(st, 1, 1).ok, false);
  assert.equal(JSON.stringify(st), before);
  assert.equal(T.devour(st, 1, 2).ok, true);
  assert.equal(T.swap(st).ok, false);
  assert.equal(T.devour(st, 1, 2).ok, false);
  assert.equal(T.endDay(st).ok, true);
  assert.equal(st.used.devour, 0);
  assert.equal(st.phase, 'patrol');
});

test('Muzzle default checks population after consuming the food; alternate timing is isolated', () => {
  for (const [population, gain] of [[3, 3], [4, 3], [5, 1]]) {
    const st = board([[1, 'lion', 2], [2, 'wolf', 2], ...Array.from({ length: population - 2 }, (_, n) => [n + 3, 'rat', 0])], ['muzzle']);
    assert.equal(T.gainFor(st, 1, { cell: 2 }).gain, gain);
    assert.equal(T.devour(st, 1, 2).ok, true);
    assert.equal(st.vault.length ? T.RAINBOW : st.cells[1].inmate.tier, 2 + gain);
  }
  const before = T.createEngine({ muzzleTiming: 'before' });
  const st = before.newGame({ params: { rewards: false, startTools: 0, initialEvents: 0 } });
  st.phase = 'manage'; st.position = 1; st.tools = ['muzzle'];
  for (const [cell, beast, tier] of [[1, 'lion', 2], [2, 'wolf', 2], [3, 'rat', 0], [4, 'rat', 0]]) T.editCell(st, cell, { kind: 'open', beast, tier });
  assert.equal(before.gainFor(st, 1, { cell: 2 }).gain, 1);
  assert.throws(() => T.createEngine({ muzzleTiming: 'bogus' }), /before or after/);
});

test('Rainbow capture immediately reseals the cell and a reward overlay preserves patrol', () => {
  const st = board([[1, 'lion', 4], [2, 'wolf', 4]]);
  st.params.rewards = true; st.milestone = true;
  assert.equal(T.devour(st, 1, 2).ok, true);
  assert.equal(st.vault.length, 1);
  assert.equal(st.vault[0].tier, 5);
  assert.equal(st.cells[1].sealed, true);
  assert.equal(st.cells[1].inmate, null);
  assert.equal(st.pending[0].kind, 'gift');
  st.phase = 'patrol'; st.destinations = [3, 4, 5];
  assert.equal(T.choose(st, 'brand').ok, true);
  assert.equal(st.phase, 'patrol');
  assert.equal(S.instant(st).action.type, 'patrol');
});

test('task rewards fire once, including observed movement and incident objectives', () => {
  for (const [id, progress] of [['walk', 17], ['incident', 1]]) {
    const st = board([[1, 'rat', 0]]);
    st.params.rewards = true; st.objective = { id, progress };
    if (id === 'walk') { st.phase = 'patrol'; st.destinations = [2]; assert.equal(T.patrol(st, 2).ok, true); }
    else { st.phase = 'event'; st.event = { kind: 'chain', cell: 1 }; assert.equal(T.resolveEvent(st, true).ok, true); }
    assert.equal(st.milestone, true);
    assert.equal(st.pending.filter(p => p.kind === 'tool').length, 1);
    T.choose(st, st.pending[0].options[0]);
    assert.equal(st.pending.filter(p => p.kind === 'tool').length, 0);
  }
});

test('Usurpation II changes one inmate by exactly two and advances only the current stage', () => {
  const st = board([[1, 'mammoth', 0], [2, 'lion', 0], [3, 'rat', 2]]);
  const before = new Map(T.boardInmates(st).map(i => [i.id, i.tier]));
  st.phase = 'event'; st.event = { kind: 'usurp2', cell: 1 }; st.eventPool = ['chain', 'usurp2'];
  assert.equal(T.resolveEvent(st).ok, true);
  assert.deepEqual(T.boardInmates(st).map(i => i.tier - before.get(i.id)).sort(), [0, 0, 2]);
  assert.ok(st.eventPool.includes('usurp3'));
  assert.ok(!st.eventPool.includes('usurp1'));
  assert.ok(!st.eventPool.includes('usurp2'));
});

test('Usurpation IV captures every legendary or Supreme and leaves lower inmates advanced', () => {
  const st = board([[1, 'lion', 3], [2, 'rat', 4], [3, 'mammoth', 0]]);
  st.phase = 'event'; st.event = { kind: 'usurp4', cell: 1 }; st.eventPool = ['chain', 'usurp4'];
  assert.equal(T.resolveEvent(st, true).ok, true);
  assert.equal(st.vault.length, 2);
  assert.equal(st.cells[3].inmate.tier, 2);
  assert.equal(st.eventPool.some(id => /^usurp/.test(id)), false);
});

test('instigation adds at most one event and never overwrites an occupied cell', () => {
  for (const freeDoors of [0, 1, 15]) {
    const st = board(); st.phase = 'office';
    for (const c of st.cells.slice(1)) if (c.n > freeDoors) { c.event = 'chain'; c.seen = true; }
    const before = st.cells.filter(c => c.event).length;
    assert.equal(T.officeAct(st, 'instigate').ok, true);
    assert.equal(st.cells.filter(c => c.event).length - before, Math.min(freeDoors, 1));
  }
});

test('coin rolls globally once even when many duplicate pairs exist', () => {
  const st = board(Array.from({ length: 6 }, (_, n) => [n + 1, 'rat', 2]), ['coin']);
  st.rng = 1;
  assert.equal(T.endDay(st).ok, true);
  assert.equal(T.boardInmates(st).length, 5);
  assert.deepEqual(T.boardInmates(st).map(i => i.tier).sort(), [2, 2, 2, 2, 4]);
});

test('scapegoat removes two others and replacing a Supreme can produce a Rainbow', () => {
  const st = board([[1, 'lion', 4], [2, 'rat', 0], [3, 'mammoth', 0]], ['scapegoat']);
  assert.equal(T.swap(st).ok, true);
  assert.equal(st.vault.length, 1);
  assert.equal(T.boardInmates(st).length, 0);
});

test('chain movement and extra key opens do not recursively trigger opening bonuses', () => {
  const st = board([], ['keys', 'fire', 'dogtag', 'blacklist']);
  st.chain = true; st.phase = 'patrol'; st.position = 1; st.destinations = [5];
  st.params.tierWeights = [1, 0, 0, 0, 0];
  assert.equal(T.patrol(st, 5).ok, true);
  assert.equal(T.boardInmates(st).length, 4);
  assert.ok(T.boardInmates(st).every(i => i.tier === 0));
  assert.equal(st.chain, false);
});

test('ironbox nests resolve after two dawns and do not resolve early', () => {
  const st = board([], ['ironbox']);
  T.endDay(st);
  const c = st.cells.find(c => c.nest);
  assert.ok(c); assert.equal(c.nest.due, 4);
  st.phase = 'manage'; T.endDay(st);
  assert.equal(c.inmate, null);
  st.phase = 'manage'; T.endDay(st);
  assert.equal(c.inmate.beast, 'rat'); assert.equal(c.inmate.tier, 2);
});

test('a gift does not consume the daily devour or swap allowance', () => {
  const st = board([[1, 'lion', 1], [2, 'wolf', 1]]);
  st.pending = [{ kind: 'gift', options: ['ring'] }];
  assert.equal(T.choose(st, 'ring').ok, true);
  assert.equal(T.boardInmates(st).length, 5);
  assert.equal(st.used.devour, 0); assert.equal(st.used.swap, 0);
});

test('preserved chain survives a pending gift and manual chain count counts the next bite as second', () => {
  const st = board([[1, 'lion', 1], [2, 'wolf', 1]], ['collar']);
  st.chaining = { eater: st.cells[1].inmate.id, at: 0, bites: [] };
  st.pending = [{ kind: 'gift', options: ['ring'] }];
  assert.equal(T.choose(st, 'ring').ok, true);
  // Preserve the observed prey for the bite boundary check.
  st.cells[1].inmate.beast = 'lion'; st.cells[2].inmate.beast = 'wolf';
  st.rng = 1;
  assert.equal(T.chainBite(st, 2).ok, true);
  assert.equal(st.cells[1].inmate.tier, 3);
});

test('decisions and rollouts ignore concealed identities, event kinds and original game RNG', () => {
  const a = board([[1, 'lion', 0], [2, 'wolf', 0], [3, 'rat', 2]]);
  a.phase = 'patrol'; a.position = 1; a.destinations = [4, 5, 6];
  a.cells[4].hidden = { id: 999, beast: 'lion', tier: 4 }; a.cells[4].known = 0;
  a.cells[4].event = 'usurp4'; a.cells[4].eventKnown = false; a.cells[4].seen = false;
  const b = S.clone(a); b.cells[4].hidden = { id: 123, beast: 'rat', tier: 0 }; b.cells[4].event = null; b.rng = 3383; b.nextId = 99999;
  assert.deepEqual(S.decide(a, S.DEFAULTS, S.rng32(8)), S.decide(b, S.DEFAULTS, S.rng32(8)));
  assert.deepEqual(finish(S.solve(a, { rollouts: 8, seed: 8 })), finish(S.solve(b, { rollouts: 8, seed: 8 })));
  const hidden = S.hideFuture(S.clone(a), 17);
  assert.equal(hidden.cells[4].hidden, null);
});

test('solver preserves observed hidden information and input state, with paired uncertainty', () => {
  const st = board([[1, 'lion', 1], [2, 'wolf', 1], [3, 'rat', 3]]);
  st.phase = 'patrol'; st.destinations = [4, 5, 6];
  st.cells[4].hidden = { id: 99, beast: 'rat', tier: 4 }; st.cells[4].known = 1;
  assert.equal(S.hideFuture(S.clone(st), 12).cells[4].hidden.tier, 4);
  const before = JSON.stringify(st), result = finish(S.solve(st, { seed: 11, rollouts: 12 }));
  assert.equal(JSON.stringify(st), before);
  assert.equal(result.rounds, 12);
  assert.equal(result.options.length, 3);
  assert.equal(result.goal, 'rainbows');
  assert.ok(result.options.every(row => row.n === 12 && Number.isFinite(row.se) && Number.isFinite(row.gapSe)));
});

test('100 diverse local games end legally and obey board invariants', () => {
  for (let seed = 0; seed < 100; seed++) {
    const st = T.newGame({ seed: `SMOKE-${seed}`, params: { startTools: seed < T.TOOL_IDS.length ? 0 : 1 } });
    // Exercise every supported mechanic in addition to naturally offered tools.
    if (seed < T.TOOL_IDS.length) st.tools.push(T.TOOL_IDS[seed]);
    let steps = 0;
    S.playout(st, S.PROFILES.rewards, S.rng32(seed + 1), (action, result) => {
      assert.equal(result.ok, true, JSON.stringify(action)); steps++;
      assert.ok(st.cells.every(c => !c.inmate || c.n > 0 && !c.sealed && Number.isInteger(c.inmate.tier) && c.inmate.tier <= 4));
      assert.equal(new Set(T.boardInmates(st).map(i => i.id)).size, T.boardInmates(st).length);
    });
    assert.equal(st.phase, 'ended'); assert.equal(st.day, 20); assert.ok(steps < 1800);
    assert.ok(st.vault.every(i => i.tier === 5));
  }
});

test('unsupported rules are excluded from local action and offer tables', () => {
  for (const item of T.UNSUPPORTED_NOTES) {
    assert.equal(T.TOOL_IDS.includes(item.id), false);
    assert.equal(T.EVENT_IDS.includes(item.id), false);
  }
  assert.match(T.RULE_ASSUMPTIONS[0], /未验证/);
});


test('a sealed rat nest still counts toward the blacklist unopened-door threshold', () => {
  const st = board(Array.from({ length: 8 }, (_, n) => [n + 1, 'rat', 0]), ['blacklist']);
  st.cells[9].nest = { due: 30 };
  st.params.tierWeights = [1, 0, 0, 0, 0];
  st.phase = 'patrol'; st.position = 7; st.destinations = [10];
  assert.equal(T.patrol(st, 10).ok, true);
  assert.equal(st.cells.slice(1).filter(c => c.sealed).length, 6);
  assert.ok(T.boardInmates(st).every(i => i.tier === 0));
});
