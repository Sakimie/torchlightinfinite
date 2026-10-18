'use strict';
const { test } = require('node:test');
const assert = require('node:assert/strict');
const { PNG } = require('pngjs');
const service = require('../lib/service');

test('observed board does not contain invented hidden inmates; monte carlo is side effect free', () => {
  assert.equal(service.newBoard().eventKind, null, 'a new board must not inherit the demo event');
  const board = service.demo();
  const before = JSON.stringify(board);
  const { state } = service.toState(board);
  assert.equal(state.pending.length, 0);
  assert.ok(state.cells.filter(c => c.sealed).every(c => c.hidden === null));
  const answer = service.recommend(board, { rollouts: 8, budgetMs: 2000 });
  assert.ok(answer.primary.label.length);
  assert.ok(Number.isFinite(answer.primary.mean));
  assert.equal(answer.stats.samples, 8);
  assert.equal(JSON.stringify(board), before);
});

test('uncertain screenshots cannot silently become confirmed recommendations', () => {
  const board = { ...service.demo(), confirmed: false, needsReview: ['未读到刑具'] };
  assert.throws(() => service.recommend(board), /核对/);
  const { state } = service.toState({ ...board, confirmed: true });
  assert.ok(state);
});

test('unsupported mechanics, special starts and impossible patrols are rejected', () => {
  assert.throws(() => service.toState({ ...service.demo(), tools: ['lion-trap'] }), /暂不支持/);
  assert.throws(() => service.toState({ ...service.demo(), tools: ['unknown'] }), /未识别的刑具/);
  assert.throws(() => service.toState({ ...service.demo(), eventKind: 'nemesis', phase: 'event' }), /暂不支持/);
  assert.throws(() => service.toState({ ...service.demo(), startEvent: 'cagefight' }), /特殊开局/);
  assert.throws(() => service.toState({ ...service.demo(), phase: 'patrol', destinations: [12] }), /六格/);
});

test('day action limits and manual choice phases are preserved', () => {
  const { state } = service.toState({ ...service.demo(), usedAction: true });
  assert.equal(state.used.swap, 1);
  assert.equal(state.used.devour, 1);
  assert.deepEqual(service.toState({ ...service.demo(), phase: 'tool', resumePhase: 'manage', offerIds: ['rope', 'coin'] }).state.pending[0].options, ['rope', 'coin']);
});

test('reward choices resume the observed patrol or chain instead of ending the day', () => {
  const runtime = require('../lib/runtime').loadRuntime();
  const patrol = service.toState({ ...service.demo(), phase: 'tool', resumePhase: 'patrol', position: 0, destinations: [1, 3, 5], offerIds: ['rope', 'coin'] }).state;
  assert.equal(patrol.phase, 'patrol');
  assert.equal(runtime.engine.choose(patrol, 'rope').ok, true);
  assert.equal(runtime.solver.instant(patrol).action.type, 'patrol');
  const chain = service.toState({ ...service.demo(), phase: 'gift', resumePhase: 'chain' }).state;
  assert.equal(runtime.engine.choose(chain, 'brand').ok, true);
  assert.ok(chain.chaining);
  assert.throws(() => service.toState({ ...service.demo(), phase: 'gift' }), /返回的阶段/);
});

test('usurpation progress excludes old stages and requires confirmation when unknown', () => {
  const { state } = service.toState({ ...service.demo(), phase: 'event', eventKind: 'usurp3' });
  assert.ok(state.eventPool.includes('usurp3'));
  assert.equal(state.eventPool.includes('usurp1'), false);
  require('../lib/runtime').loadRuntime().engine.resolveEvent(state, true);
  assert.equal(state.eventPool.includes('usurp1'), false);
  assert.ok(state.eventPool.includes('usurp4'));
  assert.throws(() => service.toState({ ...service.demo(), usurpStage: 'unknown' }), /僭越进度/);
  assert.throws(() => service.recommend({ ...service.demo(), confirmed: false, needsReview: [] }), /核对/);
});

test('new days decrement prey, consume armed chains, and tool IDs map from actual scanner output', () => {
  const base = service.demo();
  base.chainArmed = true;
  base.cells[2].prey = { cell: 4, rounds: 3 };
  const r = { ok: true, ms: 1, cells: base.cells.map(c => ({ ...c, conf: 0.95 })), warden: { cell: 4, conf: 0.95 }, days: { days: 7, conf: 0.95 }, decision: { consume: true, replace: true }, tools: { tools: [{ id: 'coin', conf: 0.95 }] }, chromatic: { count: 1, conf: 0.95 } };
  const result = service.mergeReading(r, base);
  assert.equal(result.board.cells[2].prey.rounds, 2);
  assert.equal(result.board.chainArmed, false);
  assert.equal(result.board.confirmed, false);
  assert.ok(result.board.tools.includes('coin'));
  r.days.days = 19;
  const nextRun = service.mergeReading(r, base);
  assert.equal(nextRun.board.tools.includes('collar'), false);
  assert.equal(nextRun.board.usurpStage, 'unknown');
});

test('first frame and low confidence fields require explicit confirmation', () => {
  const r = { ok: true, ms: 10, cells: service.demo().cells.map(c => ({ ...c, conf: 0.95 })), warden: { cell: 2, conf: 0.95 }, days: { days: 8, conf: 0.95 }, decision: { consume: true, replace: true }, tools: { tools: [{ sim: 'collar', conf: 0.95 }] }, chromatic: { count: 1, conf: 0.5 } };
  const result = service.mergeReading(r);
  assert.equal(result.board.confirmed, false);
  assert.equal(result.board.day, 12);
  assert.ok(result.issues.length > 0);
  r.warden = null;
  assert.match(service.mergeReading(r, service.demo()).issues.map(i => i.message).join(' '), /箭头/);
});

test('invalid and unrelated screenshots fail clearly', () => {
  assert.throws(() => service.scan('data:image/png;base64,AAAA'), /有效PNG/);
  const png = new PNG({ width: 400, height: 300 });
  png.data.fill(255);
  const dataUrl = 'data:image/png;base64,' + PNG.sync.write(png).toString('base64');
  assert.throws(() => service.scan(dataUrl), /校准|未检测到完整典狱棋盘/);
});
