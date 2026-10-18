'use strict';
const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const crypto = require('node:crypto');
const { spawnSync } = require('node:child_process');
const A = require('./game-runtime.cjs');
const BASE = process.env.TALOS_AUDIT_BASE || '/workspace/legacy-talos-v03';
const r = A.loadRuntime({ baseDir: BASE });
const original = A.loadRuntime({ baseDir: BASE, corrected: false });
const plain = x => JSON.parse(JSON.stringify(x));
function observed(edits = {}) {
  const b = {
    day: 8, totalDays: 20, position: 1, phase: 'manage', tools: [], vault: 0,
    objective: 'other', objectiveProgress: 0, milestone: false, usedAction: false,
    chainArmed: false, usurpStage: 1, startEvent: null, confirmed: true,
    cells: Array.from({ length: 16 }, (_, n) => ({ n, kind: n ? 'locked' : 'office' })),
    ...edits,
  };
  b.cells[1] = { n: 1, kind: 'inmate', beast: 'lion', tier: 1 };
  b.cells[2] = { n: 2, kind: 'inmate', beast: 'wolf', tier: 1 };
  return b;
}
function rulesBoard(runtime, population = 4) {
  const T = runtime.T;
  const st = T.newGame({ mode: 'drill', scenario: 'empty', seed: 'RULE-BOUNDARIES', params: { rewards: false, startTools: 0, eventChance: 0 } });
  st.phase = 'manage'; st.position = 1;
  for (let n = 1; n <= population; n++) assert.equal(T.editCell(st, n, { kind: 'open', beast: n === 1 ? 'lion' : n === 2 ? 'wolf' : 'rat', tier: n <= 2 ? 2 : 0 }).ok, true);
  st.tools = ['muzzle'];
  return st;
}
function fixtureApp(t) {
  const base = fs.mkdtempSync(path.join(os.tmpdir(), 'talos-adapter-test-'));
  t.after(() => fs.rmSync(base, { recursive: true, force: true }));
  fs.mkdirSync(path.join(base, 'training'), { recursive: true });
  fs.symlinkSync(path.join(BASE, 'english-source'), path.join(base, 'english-source'), 'dir');
  fs.mkdirSync(path.join(base, 'training', 'english'));
  fs.copyFileSync(path.join(BASE, 'training', 'english', 'policy.json'), path.join(base, 'training', 'english', 'policy.json'));
  for (const file of ['game-runtime.cjs', 'game-advice.cjs', 'game-metadata.json']) fs.copyFileSync(path.join(__dirname, file), path.join(base, 'training', file));
  return base;
}

test('pinned files match expected hashes, runtime is isolated, and original source stays unchanged', () => {
  for (const [file, hash] of Object.entries(A.EXPECTED)) {
    assert.equal(crypto.createHash('sha256').update(fs.readFileSync(path.join(BASE, 'english-source', 'js', file))).digest('hex'), hash);
  }
  assert.equal(r.T.INSTIGATE_EVENTS, 1); assert.equal(original.T.INSTIGATE_EVENTS, 2);
  assert.deepEqual(plain(r.T.USURP.usurp2), { count: 1, by: 2, next: 'usurp3' });
  assert.deepEqual(plain(original.T.USURP.usurp2), { count: 2, by: 1, next: 'usurp3' });
  assert.equal(globalThis.Tartarus, undefined);
  assert.equal(r.effectiveHistoricalParams.toolSets, false);
  assert.equal(r.baselineParams.toolSets, false);
  assert.throws(() => A.replaceExactly('x x', 'x', 'y', 'test'), /预期1处/);
});

test('Usurpation II upgrades one inmate by two and properly advances its pool', () => {
  const st = rulesBoard(r, 3); st.tools = [];
  st.cells[1].inmate.tier = 0; st.cells[2].inmate.tier = 0;
  const before = new Map(r.T.boardInmates(st).map(i => [i.id, i.tier]));
  st.phase = 'event'; st.event = { kind: 'usurp2', cell: 1 }; st.eventPool = ['chain', 'usurp2'];
  assert.equal(r.T.resolveEvent(st, true).ok, true);
  assert.deepEqual(plain(r.T.boardInmates(st).map(i => i.tier - before.get(i.id)).sort()), [0, 0, 2]);
  assert.ok(st.eventPool.includes('usurp3')); assert.ok(!st.eventPool.includes('usurp2'));
});

test('instigation adds a single event and never exceeds free doors', () => {
  for (const free of [0, 1, 15]) {
    const st = rulesBoard(r, 0); st.phase = 'office'; st.position = 0;
    for (const c of st.cells.slice(1)) c.event = c.n <= free ? null : 'chain';
    const before = st.cells.filter(c => c.event).length;
    assert.equal(r.T.officeAct(st, 'instigate').ok, true);
    assert.equal(st.cells.filter(c => c.event).length - before, Math.min(1, free));
  }
});

test('Muzzle preview, execution, and solver share the post-meal population threshold of three', () => {
  for (const [population, gain] of [[3, 3], [4, 3], [5, 1]]) {
    const st = rulesBoard(r, population);
    assert.equal(r.T.gainFor(st, 1, { cell: 2, scratch: false }).gain, gain);
    const id = st.cells[1].inmate.id;
    assert.equal(r.T.devour(st, 1, 2).ok, true);
    assert.equal(st.vault.some(i => i.id === id) ? 5 : st.cells[1].inmate.tier, 2 + gain);
  }
  const before = rulesBoard(original, 5), after = rulesBoard(r, 5);
  const p = { ...r.baselineParams, wStair: 0 };
  const oldScore = original.S.devourScores(before, original.S.context(before, p))[0].score;
  const newScore = r.S.devourScores(after, r.S.context(after, p))[0].score;
  assert.ok(oldScore > newScore);
});

test('patched solver values Usurpation I follow-up as one double promotion and II correctly', () => {
  const b = observed({ phase: 'patrol', destinations: [3], position: 1 });
  b.cells[3].event = true; b.cells[3].eventKind = 'usurp1';
  const st = A.mapObserved(b, r).state;
  const p = { ...r.baselineParams, wInstigate: 1 };
  const oldValue = original.S.patrolScores(st, original.S.context(st, p))[0].score;
  const newValue = r.S.patrolScores(st, r.S.context(st, p))[0].score;
  assert.ok(newValue > oldValue, 'one +2 has higher convex value than two +1 at equal tiers');
});

test('adapter maps observable board correctly and never invents unknown inmates', () => {
  const b = observed(); const before = JSON.stringify(b), st = A.mapObserved(b, r).state;
  assert.equal(st.v, 3); assert.equal(st.day, 8); assert.equal(st.position, 1);
  assert.equal(st.params.startTools, 1); assert.equal(st.pending.length, 0);
  assert.ok(st.cells.filter(c => c.sealed).every(c => c.hidden === null && c.known === 0));
  assert.equal(st.cells[0].sealed, false); assert.equal(st.cells[0].inmate, null);
  assert.equal(JSON.stringify(b), before);
});

test('uncertain fields, unsupported mechanics, and impossible cells are rejected', () => {
  assert.throws(() => A.mapObserved(observed({ day: undefined }), r), /当前天数/);
  assert.throws(() => A.mapObserved(observed({ confirmed: false }), r), /核对/);
  assert.throws(() => A.mapObserved(observed({ startEvent: 'cagefight' }), r), /特殊开局/);
  assert.throws(() => A.mapObserved(observed({ tools: ['lion-trap'] }), r), /暂不支持/);
  assert.throws(() => A.mapObserved(observed({ tools: ['__proto__'] }), r), /暂不支持/);
  assert.throws(() => A.mapObserved(observed({ usedExtra: true }), r), /额外行动/);
  assert.throws(() => A.mapObserved(observed({ devourAfterSwap: true }), r), /额外行动/);
  assert.throws(() => A.mapObserved(observed({ usurpStage: 'unknown' }), r), /僭越/);
  const b = observed(); b.cells[2].tier = 5;
  assert.throws(() => A.mapObserved(b, r), /绮彩囚犯已离场/);
  b.cells[2] = { n: 2, kind: 'unknown' };
  assert.throws(() => A.mapObserved(b, r), /未知状态/);
  b.cells[2] = { n: 2, kind: 'locked', scratch: true };
  assert.throws(() => A.mapObserved(b, r), /划痕/);
  b.cells[2] = { n: 2, kind: 'nest' };
  assert.throws(() => A.mapObserved(b, r), /鼠巢/);
});

test('actual destinations and ratway are preserved rather than rerolled', () => {
  const st = A.mapObserved(observed({ phase: 'patrol', destinations: [2, 5, 7] }), r).state;
  assert.deepEqual(st.destinations, [2, 5, 7]);
  assert.throws(() => A.mapObserved(observed({ phase: 'patrol', destinations: [12] }), r), /六格/);
  const ratway = A.mapObserved(observed({ phase: 'ratway' }), r).state;
  assert.equal(ratway.phase, 'patrol'); assert.equal(ratway.ratway, true); assert.equal(ratway.destinations.length, 15);
});

test('empty chain history survives observation entry and every next bite is chained', () => {
  const b = observed({ phase: 'chain', chainConsumed: 1, tools: ['collar'] });
  const st = A.mapObserved(b, r).state;
  assert.ok(st.chaining); assert.equal(st.phase, 'manage'); assert.equal(st.observedChainConsumed, 1);
  assert.equal(st.used.devour, 1);
  const a = r.S.decide(A.publicView(st), r.effectiveHistoricalParams, r.S.rng32(1));
  assert.ok(['chain', 'stopchain'].includes(a.type));
  assert.equal(r.S.apply(st, a).ok, true);
  assert.throws(() => A.mapObserved(observed({ phase: 'chain' }), r), /已吞口数/);
});

test('pending rewards preserve patrol, chain and event underneath the overlay', () => {
  const patrol = A.mapObserved(observed({ phase: 'tool', resumePhase: 'patrol', destinations: [2, 3], offerIds: ['rope', 'coin'] }), r).state;
  assert.equal(r.T.choose(patrol, 'rope').ok, true); assert.equal(patrol.phase, 'patrol');
  const chain = A.mapObserved(observed({ phase: 'gift', resumePhase: 'chain', chainConsumed: 1, offerIds: ['ring', 'horn', 'wine', 'brand'] }), r).state;
  assert.equal(r.T.choose(chain, 'ring').ok, true); assert.ok(chain.chaining);
  const event = A.mapObserved(observed({ phase: 'tool', resumePhase: 'event', eventKind: 'usurp2', usurpStage: 2, offerIds: ['rope', 'coin'] }), r).state;
  assert.equal(r.T.choose(event, 'coin').ok, true); assert.equal(event.phase, 'event'); assert.equal(event.event.kind, 'usurp2');
  assert.throws(() => A.mapObserved(observed({ phase: 'gift' }), r), /返回的阶段/);
  assert.throws(() => A.mapObserved(observed({ phase: 'gift', resumePhase: 'manage', offerIds: [] }), r), /核对当前实际可选奖励/);
});

test('unknown event contents and unknown occupant species cannot leak through public input', () => {
  const b = observed({ phase: 'patrol', destinations: [3, 4, 5] }); b.cells[3].event = true;
  const a = A.mapObserved(b, r).state; a.cells[4].hidden = { id: 888, tier: 4, beast: 'wolf' }; a.cells[4].known = 0;
  const c = plain(a); c.cells[4].hidden = { id: 9999, tier: 0, beast: 'rat' }; c.cells[4].event = 'usurp4'; c.cells[4].seen = false; c.rng = 777; c.nextId = 100000;
  const va = A.publicView(a), vc = A.publicView(c);
  assert.deepEqual(va, vc);
  assert.deepEqual(plain(r.S.decide(va, r.effectiveHistoricalParams, r.S.rng32(1))), plain(r.S.decide(vc, r.effectiveHistoricalParams, r.S.rng32(1))));
});

test('readiness requires a validation report matching both code and policy hashes', t => {
  const baseDir = fixtureApp(t), runtime = A.loadRuntime({ baseDir });
  assert.equal(A.validationFor(runtime).ready, false);
  const report = { schema: 'talos-game-validation-v1', correctedSourceHash: runtime.correctedSourceHash, oldPolicyHash: 'wrong', selection: { selected: 'baseline' } };
  fs.writeFileSync(path.join(baseDir, 'training', 'game-validation.json'), JSON.stringify(report));
  assert.throws(() => A.validationFor(runtime), /停止建议/);
  report.oldPolicyHash = runtime.oldPolicyHash;
  fs.writeFileSync(path.join(baseDir, 'training', 'game-validation.json'), JSON.stringify(report));
  assert.equal(A.validationFor(runtime).ready, true); assert.equal(A.validationFor(runtime).selected, 'baseline');
  const result = A.recommend(observed(), { runtime });
  assert.equal(result.validation.selected, 'baseline'); assert.ok(result.primary.label); assert.equal(result.primary.mean, undefined);
});

test('CLI accepts only a bounded JSON observation and reports errors without stdout advice', t => {
  const baseDir = fixtureApp(t), cli = path.join(baseDir, 'training', 'game-advice.cjs');
  const good = spawnSync(process.execPath, [cli], { input: JSON.stringify({ board: observed() }), encoding: 'utf8' });
  assert.equal(good.status, 0, good.stderr); const data = JSON.parse(good.stdout); assert.equal(data.ok, true); assert.ok(data.primary.label); assert.equal(data.source, 'corrected-historical-parameters');
  const bad = spawnSync(process.execPath, [cli], { input: JSON.stringify({ board: observed(), options: { baseDir: '/tmp' } }), encoding: 'utf8' });
  assert.equal(bad.status, 1); assert.equal(bad.stdout, ''); assert.match(bad.stderr, /不接受/);
  const huge = spawnSync(process.execPath, [cli], { input: ' '.repeat(1024 * 1024 + 1), encoding: 'utf8' });
  assert.equal(huge.status, 1); assert.equal(huge.stdout, ''); assert.match(huge.stderr, /1 MiB/);
});

test('100 corrected-policy simulations finish with legal operations using public information', () => {
  for (let i = 0; i < 100; i++) {
    const st = r.T.newGame({ mode: 'drill', scenario: 'empty', seed: `ADAPTER-SMOKE-${i}`, params: { rewards: true, startTools: 1 } });
    let steps = 0;
    while (st.phase !== 'ended') {
      assert.ok(++steps < 2000);
      const a = r.S.decide(A.publicView(st), r.effectiveHistoricalParams, r.S.rng32(i + steps));
      assert.ok(a); assert.equal(r.S.apply(st, a).ok, true, JSON.stringify(a));
    }
    assert.equal(st.day, 20); assert.equal(st.cells[0].inmate, null);
  }
});


test('edited parameters cannot impersonate the pinned historical model', t => {
  const baseDir = fixtureApp(t), file = path.join(baseDir, 'training', 'english', 'policy.json');
  const changed = JSON.parse(fs.readFileSync(file, 'utf8')); changed.params.wOpen += 1;
  fs.writeFileSync(file, JSON.stringify(changed));
  assert.throws(() => A.loadRuntime({ baseDir }), /历史策略文件与原v0.3参数不一致/);
});

test('known hidden information is preserved only when explicitly supplied', () => {
  const b = observed({ phase: 'patrol', destinations: [3, 4] });
  b.cells[3].knownTier = 4;
  b.cells[4].knownTier = 3; b.cells[4].knownBeast = 'wolf';
  const st = A.mapObserved(b, r).state;
  assert.equal(st.cells[3].known, 1); assert.equal(st.cells[3].hidden.tier, 4);
  assert.equal(st.cells[4].known, 2); assert.equal(st.cells[4].hidden.beast, 'wolf');
  assert.throws(() => A.mapObserved(observed({ hasUnrecordedHiddenInfo: true }), r), /隐藏信息尚未录入/);
  assert.throws(() => A.mapObserved(observed({ needsReview: ['unknown tool'] }), r), /待核对/);
});

test('choosing a destination-reducing tool reduces the actual observed door offer', () => {
  const b = observed({ phase: 'tool', resumePhase: 'patrol', destinations: [2, 3], offerIds: ['rope', 'whip'] });
  const st = A.mapObserved(b, r).state;
  assert.equal(st.destinations.length, 2);
  assert.equal(r.T.choose(st, 'whip').ok, true);
  assert.equal(st.destinations.length, 1);
});
