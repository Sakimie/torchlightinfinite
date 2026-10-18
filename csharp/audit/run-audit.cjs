#!/usr/bin/env node
'use strict';

// Development-only independent audit. Never included in the Windows product.
// The original JavaScript policy is an oracle only where its assumptions agree
// with the C# contract. Known card corrections are explicitly separate checks.
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const Module = require('node:module');
const { spawnSync } = require('node:child_process');
const root = path.resolve(__dirname, '../..');
const enginePath = path.join(root, 'lib/offline-engine.js');
const solverPath = path.join(root, 'lib/offline-solver.js');
const T = require(enginePath);
const solverSource = fs.readFileSync(solverPath, 'utf8');
const exportMarker = 'return { DEFAULTS, PROFILES, UNUSED_PARAMETERS, rng32';
if (solverSource.split(exportMarker).length !== 2) throw new Error('JS audit hook no longer matches the source; review the oracle.');
const oracleModule = new Module(solverPath, module);
oracleModule.filename = solverPath;
oracleModule.paths = Module._nodeModulePaths(path.dirname(solverPath));
oracleModule._compile(solverSource.replace(exportMarker, 'return { auditScore: score, DEFAULTS, PROFILES, UNUSED_PARAMETERS, rng32'), solverPath);
const S = oracleModule.exports;
const executionMarker = 'c.inmate.tier <= here.tier && T.canEat(st, { ...here, tier: c.inmate.tier }, c.inmate)';
if (solverSource.split(executionMarker).length !== 2) throw new Error('Review the documented lower-tier Execution correction before rerunning.');
const guideModule = new Module(solverPath, module);
guideModule.filename = solverPath;
guideModule.paths = oracleModule.paths;
guideModule._compile(solverSource.replace(exportMarker, 'return { auditScore: score, DEFAULTS, PROFILES, UNUSED_PARAMETERS, rng32')
  .replace(executionMarker, "c.inmate.tier < here.tier && ({ mammoth: 'lion', lion: 'wolf', wolf: 'rat', rat: 'mammoth' }[here.beast] === c.inmate.beast)"), solverPath);
const guideOracle = guideModule.exports;
const clone = value => JSON.parse(JSON.stringify(value));
const hash = filename => crypto.createHash('sha256').update(fs.readFileSync(filename)).digest('hex');
const fixtures = [];
function base() {
  return {
    day: 8, position: 1, phase: 'manage',
    rooms: Array.from({ length: 16 }, (_, index) => ({ index, kind: index === 0 ? 'office' : 'locked' })),
    destinations: [], tools: [], vault: 0, objective: 'other', objectiveProgress: 0,
    milestone: false, usedAction: false, noDevour: false, noSwap: false,
    chainArmed: false, chainConsumed: 0, usurpStage: 1, offerIds: [],
    confirmed: true, unsupportedPresent: false, source: 'audit-constructed-manual',
  };
}
function put(st, index, species, tier, extra = {}) { st.rooms[index] = { index, kind: 'inmate', species, tier, ...extra }; return st; }
function populated() {
  const st = base();
  put(st, 1, 'mammoth', 2); put(st, 3, 'lion', 2);
  put(st, 6, 'wolf', 3); put(st, 10, 'rat', 1);
  return st;
}
function add(id, state, mode = 'parity') { const row = { id, mode, state: clone(state) }; fixtures.push(row); return row; }
function reject(id, edit) { const st = populated(); edit(st); add('reject/' + id, st, 'reject'); }

// All species and both matched/mismatched tier pairs, including the legendary
// extra-prey rules. These are legal-action tests as well as numerical checks.
for (const eater of T.BEASTS) for (const meal of T.BEASTS)
  for (let tier = 0; tier < 5; tier++) for (let mealTier = 0; mealTier < 5; mealTier++) {
    const st = base(); put(st, 1, eater, tier); put(st, 2, meal, mealTier);
    add(`pair/${eater}/${meal}/${tier}/${mealTier}`, st);
  }
for (const day of [1, 19, 20]) for (const phase of ['patrol', 'manage', 'office', 'chain', 'ratway']) {
  const st = populated(); st.day = day; st.phase = phase;
  if (phase === 'office') st.position = 0;
  if (phase === 'patrol') { st.position = 14; st.destinations = [0, 2, 3]; }
  if (phase === 'ratway') st.destinations = [0, 2, 7, 15];
  if (phase === 'chain') { st.chainConsumed = 1; st.usedAction = true; }
  add(`phase/${phase}/day${day}`, st, phase === 'ratway' ? 'ratway-correction' : 'parity');
}
for (const id of T.EVENT_IDS) for (const population of [1, 4]) {
  const st = base(); st.phase = 'event'; st.eventKind = id;
  st.usurpStage = id.startsWith('usurp') ? Number(id.slice(-1)) : 3;
  for (let n = 1; n <= population; n++) put(st, n, T.BEASTS[(n - 1) % 4], n % 5);
  add(`event/${id}/population${population}`, st);
}
for (const tool of T.TOOL_IDS) for (const phase of ['patrol', 'manage', 'tool']) {
  const st = populated(); st.phase = phase;
  if (phase === 'tool') { st.offerIds = [tool]; st.resumePhase = 'manage'; }
  else st.tools = [tool];
  if (phase === 'patrol') { st.position = 14; st.destinations = [0, 2, 3]; }
  add(`tool/${tool}/${phase}`, st, ['whip', 'skewer'].includes(tool) ? 'card-correction' : 'parity');
}
for (const id of T.GIFT_IDS) for (const tools of [[], ['hook', 'dogtag', 'keys', 'fire', 'blacklist']]) {
  const st = populated(); st.phase = 'gift'; st.offerIds = [id]; st.resumePhase = 'manage'; st.tools = tools;
  add(`gift/${id}/${tools.length ? 'open-tools' : 'plain'}`, st, id === 'ring' ? 'card-correction' : 'parity');
}
for (const population of [2, 3, 4, 5]) for (const phase of ['manage', 'chain']) {
  const st = base(); st.tools = ['muzzle']; st.phase = phase;
  for (let n = 1; n <= population; n++) put(st, n, T.BEASTS[(n - 1) % 4], 1);
  if (phase === 'chain') { st.chainConsumed = 1; st.usedAction = true; st.tools.push('collar'); }
  add(`muzzle/population${population}/${phase}`, st);
}
for (const flag of ['usedAction', 'noDevour', 'noSwap']) { const st = populated(); st[flag] = true; add('restriction/' + flag, st); }
for (const objective of Object.keys(T.OBJECTIVES)) {
  const st = populated(); st.objective = objective; st.phase = 'patrol'; st.position = 14; st.destinations = [0, 2, 3]; st.chainArmed = true;
  add('objective/' + objective, st);
}
for (const stage of [1, 2, 3, 4, 5]) {
  const st = populated(); st.phase = 'office'; st.position = 0; st.usurpStage = stage;
  add('event-prior/usurp-stage' + stage, st);
}
{
  const st = populated(); st.phase = 'ratway'; st.chainArmed = true; st.tools = ['shackles']; st.objective = 'walk';
  add('ratway/excludes-walking-effects', st, 'ratway-correction');
}
for (const permission of ['usedAction', 'noSwap', 'noDevour']) {
  const st = populated(); st.phase = 'ratway'; st[permission] = true;
  add('ratway/respects-' + permission, st, 'permission-correction');
}
for (const sameTier of [false, true]) {
  const st = base(); st.phase = 'event'; st.eventKind = 'execution';
  put(st, 1, 'mammoth', 4); put(st, 2, 'lion', sameTier ? 4 : 3); put(st, 4, 'wolf', 3);
  add('execution/strict-lower-' + sameTier, st, 'execution-correction');
}
for (const [species, mealSpecies, tools] of [['mammoth', 'wolf', []], ['lion', 'rat', []], ['rat', 'rat', ['skewer']], ['rat', 'mammoth', ['skewer']]]) {
  const st = base(); st.phase = 'event'; st.eventKind = 'execution'; st.tools = tools;
  put(st, 1, species, 4); put(st, 2, mealSpecies, 3);
  add(`execution/base-counter-only/${species}/${mealSpecies}`, st, 'execution-correction');
}
for (const withEvent of [false, true]) for (const sideOpen of [false, true]) {
  const st = base(); st.position = 14; st.phase = 'patrol'; st.destinations = [0, 2, 4]; st.chainArmed = sideOpen;
  st.tools = ['keys', 'fire', 'dogtag', 'blacklist'];
  put(st, 1, 'mammoth', 2); put(st, 10, 'rat', 1);
  for (const n of [6, 7, 8, 9, 11, 12, 13, 15]) st.rooms[n] = { index: n, kind: 'empty' };
  if (withEvent) { st.rooms[4].event = true; st.rooms[4].eventKind = 'freedom'; }
  add(`chain-open/event${withEvent}/side${sideOpen}`, st, sideOpen ? 'chain-open-correction' : 'parity');
}
const random = S.rng32('csharp-independent-audit-v1');
const pick = list => list[Math.floor(random() * list.length)];
const stableTools = T.TOOL_IDS.filter(id => !['whip', 'skewer'].includes(id));
for (let n = 0; n < 160; n++) {
  const st = base(); st.day = 1 + Math.floor(random() * 20);
  for (let cell = 1; cell < 16; cell++) {
    const draw = random();
    if (draw < .45) put(st, cell, pick(T.BEASTS), Math.floor(random() * 5));
    else if (draw < .58) st.rooms[cell] = { index: cell, kind: 'empty' };
    if (st.rooms[cell].kind === 'locked' && random() < .13) st.rooms[cell].event = true;
  }
  put(st, 1, pick(T.BEASTS), Math.floor(random() * 5));
  st.tools = stableTools.filter(() => random() < .13);
  st.phase = pick(['patrol', 'manage', 'office']);
  st.usurpStage = 1 + Math.floor(random() * 5); st.objective = pick(Object.keys(T.OBJECTIVES));
  if (st.phase === 'office') st.position = 0;
  if (st.phase === 'patrol') { st.position = 14; st.destinations = [0, 2, 3]; }
  add('visible-mixture/' + n, st);
}
for (const tier of [0, 2, 3, 4]) for (const tools of [['whip'], ['whip', 'saw'], ['whip', 'saw', 'collar'], ['skewer'], ['skewer', 'muzzle']]) {
  const st = base(); st.tools = tools;
  const rat = tools.includes('skewer');
  put(st, 1, rat ? 'rat' : 'mammoth', tier); put(st, 2, rat ? 'mammoth' : 'lion', tier);
  if (tools.includes('collar')) { st.phase = 'chain'; st.chainConsumed = 1; st.usedAction = true; }
  add(`card/devour/${tier}/${tools.join('+')}`, st, tier === 0 && tools.includes('collar') ? 'reject' : 'card-correction');
}
reject('unconfirmed', st => st.confirmed = false);
reject('unknown-room', st => st.rooms[15].kind = 'unknown');
reject('day-zero', st => st.day = 0);
reject('day-21', st => st.day = 21);
reject('position-outside', st => st.position = 16);
reject('rainbow-not-banked', st => st.rooms[1].tier = 5);
reject('unconfirmed-usurp', st => st.usurpStage = 0);
reject('unsupported-present', st => st.unsupportedPresent = true);
reject('unknown-tool', st => st.tools = ['future-tool']);
for (const item of T.UNSUPPORTED_NOTES) reject('unsupported-' + item.id, st => st.tools = [item.id]);
reject('unknown-current-event', st => { st.phase = 'event'; st.eventKind = 'future-event'; });
reject('unknown-marked-event', st => { st.rooms[3].event = true; st.rooms[3].eventKind = 'future-event'; });
reject('missing-event-kind', st => { st.phase = 'event'; st.eventKind = null; });
reject('invalid-species', st => st.rooms[1].species = 'dragon');
reject('duplicate-room', st => st.rooms[15].index = 1);
reject('missing-room', st => st.rooms.pop());
reject('empty-offer', st => { st.phase = 'tool'; st.offerIds = []; st.resumePhase = 'manage'; });
reject('unsupported-offer', st => { st.phase = 'tool'; st.offerIds = ['pestle']; st.resumePhase = 'manage'; });
reject('unknown-gift', st => { st.phase = 'gift'; st.offerIds = ['future-gift']; st.resumePhase = 'manage'; });
reject('invalid-destination', st => { st.phase = 'patrol'; st.destinations = [16]; });
reject('unknown-phase', st => st.phase = 'next-season');

function jsState(input) {
  const st = T.newGame({ seed: 17, objective: input.objective, params: { rewards: false, startTools: 0, initialEvents: 0 } });
  st.day = input.day; st.position = input.position; st.tools = [...input.tools];
  st.objective.progress = input.objectiveProgress; st.milestone = input.milestone;
  st.chain = input.chainArmed; st.vault = Array.from({ length: input.vault }, (_, n) => ({ id: 100 + n, beast: 'rat', tier: 5 }));
  st.used = { devour: input.usedAction ? 1 : 0, swap: 0, noDevour: input.noDevour, noSwap: input.noSwap };
  st.phase = ['tool', 'gift'].includes(input.phase) ? input.resumePhase : input.phase;
  if (st.phase === 'chain') st.phase = 'manage';
  if (st.phase === 'ratway') { st.phase = 'patrol'; st.ratway = true; }
  st.destinations = input.phase === 'ratway' ? Array.from({ length: 15 }, (_, n) => n + 1) : [...input.destinations];
  if (['tool', 'gift'].includes(input.phase)) st.pending = [{ kind: input.phase, options: [...input.offerIds] }];
  st.eventPool = T.EVENT_POOL.filter(id => id !== 'usurp1');
  if (input.usurpStage <= 4) st.eventPool.push('usurp' + input.usurpStage);
  st.cells = input.rooms.map(room => ({
    n: room.index, sealed: ['locked', 'nest'].includes(room.kind), seen: !!room.event || !!room.seen || room.kind === 'inmate',
    inmate: room.kind === 'inmate' ? { id: room.index + 1, beast: room.species, tier: room.tier } : null,
    hidden: room.knownTier == null ? null : { id: 200 + room.index, beast: room.knownSpecies || 'rat', tier: room.knownTier },
    known: room.knownTier == null ? 0 : 1, event: room.event ? room.eventKind || 'freedom' : null,
    eventKnown: !!room.eventKind, ivory: !!room.ivory, scratch: false, nest: room.kind === 'nest' ? { due: room.nestDue } : null,
  }));
  if (input.phase === 'event') st.event = { kind: input.eventKind, cell: input.position };
  if (input.phase === 'chain') st.chaining = { eater: input.position + 1, at: input.chainConsumed, bites: [] };
  return st;
}
function jsAction(action) {
  const id = action.type === 'choose' ? 'choose:' + action.id
    : action.type === 'event' ? 'event:' + (action.accept ? 'accept' : 'skip')
    : action.type === 'office' ? 'office:' + action.choice : action.type;
  return { id, target: action.cell ?? action.to ?? null };
}
const key = action => action.id + '|' + action.target;
function tierValue(input, tier) {
  if (tier >= 5) return 2;
  return 2 / 2 ** (5 - tier + .3) * Math.min(1, (22 - input.day) / Math.max(2, (5 - tier) * 1.3));
}
function independentGuaranteedGain(input, eater) {
  return 1 + (input.tools.includes('skewer') && eater.species === 'rat' ? 1 : 0)
    + (input.tools.includes('muzzle') && input.rooms.filter(room => room.kind === 'inmate').length - 1 <= 3 ? 2 : 0);
}
function twoInmateDevour(input) {
  const eater = input.rooms[input.position], meal = input.rooms.find(room => room.kind === 'inmate' && room.index !== eater.index);
  let distribution = [[independentGuaranteedGain(input, eater), 1]];
  for (const [tool, chance] of [['whip', .35], ['saw', .25], ['collar', .8]]) {
    if (!input.tools.includes(tool) || tool === 'collar' && input.phase !== 'chain') continue;
    distribution = distribution.flatMap(([gain, probability]) => [[gain, probability * (1 - chance)], [gain + 1, probability * chance]]);
  }
  return distribution.reduce((sum, [gain, probability]) => sum + probability * tierValue(input, eater.tier + gain), 0)
    - tierValue(input, eater.tier) - tierValue(input, meal.tier);
}
const fixturesFile = path.join(__dirname, 'fixtures.json');
fs.writeFileSync(fixturesFile, JSON.stringify(fixtures, null, 2) + '\n');
if (process.argv.includes('--generate-only')) { console.log(JSON.stringify({ fixtures: fixtures.length, path: fixturesFile })); process.exit(0); }
const dotnetArgument = process.argv.indexOf('--dotnet');
const dotnet = dotnetArgument >= 0 ? process.argv[dotnetArgument + 1] : '/workspace/.dotnet/dotnet';
const resultsFile = path.join(__dirname, 'csharp-results.json');
const execution = spawnSync(dotnet, ['run', '--project', path.join(__dirname, 'Talos.StrategyAudit.csproj'), '--', fixturesFile, resultsFile], { cwd: root, encoding: 'utf8', timeout: 120000 });
if (execution.status !== 0) { process.stderr.write(execution.stdout || ''); process.stderr.write(execution.stderr || ''); throw new Error('C# adapter failed: ' + execution.status); }
const results = JSON.parse(fs.readFileSync(resultsFile, 'utf8'));
const report = {
  schema: 'talos-csharp-independent-strategy-audit-v1', passed: true,
  scope: 'Constructed visible-state single-step legal actions and heuristic scores; no full-game training or accuracy claim.',
  framework: results.framework, jsEngineSha256: hash(enginePath), jsSolverSha256: hash(solverPath),
  coreSourceHashes: Object.fromEntries(['GameState.cs', 'EngineCatalog.cs', 'GameRules.cs', 'GameAdvisor.cs'].map(name => [name, hash(path.join(root, 'csharp/Talos.Core', name))])),
  coreAssemblySha256: hash(path.join(__dirname, 'bin/Debug/net10.0/Talos.Core.dll')),
  knownIntentionalDifferences: ['whip applies to each devour, not main opening; independent Saw/Whip/chain-Collar probabilities combine before the Rainbow cap', 'skewer adds one tier to every legal rat devour', 'ring secondary openings omit main opening bonuses (the original JS score already omitted those bonuses)', 'ratway offers every non-office room and excludes ordinary walking/Chain/Shackles/walk-objective scoring', 'patrol arrival value respects UsedAction/NoDevour/NoSwap', 'Execution uses strictly lower-tier base-counter species, without legendary or skewer extra-prey relations; audit patches only that predicate in a separately labelled in-memory JS oracle. This interpretation was checked against reference behaviour, not newly measured in the live game.', 'ChainArmed ordinary patrol opens suppress main-opening Keys/Fire/Dogtag/Blacklist and destination-event bonuses'],
  windowsUiExecuted: false, networkUsedByAuditRuntime: false, rows: [], counts: {},
};
const sameSet = (left, right) => JSON.stringify([...left].sort()) === JSON.stringify([...right].sort());
report.catalog = { ...results.catalog, passed: sameSet(results.catalog.tools, T.TOOL_IDS)
  && sameSet(results.catalog.events, T.EVENT_IDS) && sameSet(results.catalog.gifts, T.GIFT_IDS)
  && T.UNSUPPORTED_NOTES.every(item => results.catalog.unsupported.includes(item.id))
  && results.catalog.totalDays === 20 && results.catalog.instigateEvents === 1 && results.catalog.muzzlePopulation === 3
  && results.catalog.usurp.every(item => item.count === T.USURP[item.id].count && item.by === T.USURP[item.id].by) };
if (!report.catalog.passed) report.passed = false;
const rows = new Map(results.rows.map(row => [row.Id, row]));
for (const fixture of fixtures) {
  const row = rows.get(fixture.id), errors = [];
  if (!row) errors.push('Missing C# result');
  else if (fixture.mode === 'reject') {
    if (row.valid || !row.recommendRejected || !row.errors?.length) errors.push('Validate must explain rejection and Recommend must refuse invalid input');
  } else if (!row.valid) errors.push('Unexpected rejection: ' + JSON.stringify(row.errors || row.error));
  else {
    const st = jsState(fixture.state);
    const expected = S.actions(st).map(action => ({ ...jsAction(action), score: guideOracle.auditScore(st, action, S.DEFAULTS), originalScore: S.auditScore(st, action, S.DEFAULTS) }));
    if (fixture.mode === 'parity' && expected.some(action => Math.abs(action.score - action.originalScore) > 1e-9)) fixture.mode = 'execution-correction';
    const actual = row.candidates.map(action => ({ id: action.ActionId, target: action.Target ?? null, score: action.Score, room: action.Room }));
    const expectedMap = new Map(expected.map(action => [key(action), action]));
    const actualMap = new Map(actual.map(action => [key(action), action]));
    if (actualMap.size !== actual.length || JSON.stringify([...actualMap.keys()].sort()) !== JSON.stringify([...expectedMap.keys()].sort())) errors.push('Legal candidate set differs');
    if (['parity', 'ratway-correction', 'permission-correction', 'execution-correction', 'chain-open-correction'].includes(fixture.mode)) for (const action of actual) {
      const old = expectedMap.get(key(action));
      let wanted = old?.score;
      if (old && fixture.mode === 'chain-open-correction') {
        const secondary = clone(st);
        secondary.tools = secondary.tools.filter(id => !['keys', 'fire', 'dogtag', 'blacklist'].includes(id));
        for (const room of secondary.cells) { room.event = null; room.eventKnown = false; }
        wanted = guideOracle.auditScore(secondary, { type: 'patrol', cell: action.target }, S.DEFAULTS);
      }
      if (old && ['ratway-correction', 'permission-correction'].includes(fixture.mode)) {
        const steps = T.stepsBetween(st.position, action.target);
        wanted -= steps * .003 * .25;
        if (st.tools.includes('shackles') && steps >= 4) wanted -= .4 * .13 * .65;
        if (st.objective.id === 'walk' && !st.milestone) wanted -= steps * .008;
        if (st.chain) wanted -= st.cells.slice(1).filter(room => room.sealed && !room.nest && T.stepsBetween(st.position, room.n) <= steps).length
          * T.MODEL.tierWeights.reduce((sum, weight, tier) => sum + weight * tierValue(fixture.state, tier), 0) / 100 * .6;
      }
      if (old && fixture.mode === 'permission-correction' && fixture.state.rooms[action.target].kind === 'inmate') {
        const location = clone(st); location.position = action.target; location.phase = 'manage';
        const eater = location.cells[action.target];
        const all = [{ type: 'swap' }, ...location.cells.filter(room => T.canEat(location, eater.inmate, room.inmate)).map(room => ({ type: 'devour', to: room.n }))];
        const originalArrival = Math.max(0, ...all.map(candidate => S.auditScore(location, candidate, S.DEFAULTS)));
        const permitted = fixture.state.usedAction ? [] : all.filter(candidate => candidate.type !== 'swap' || !fixture.state.noSwap).filter(candidate => candidate.type !== 'devour' || !fixture.state.noDevour);
        const permittedArrival = Math.max(0, ...permitted.map(candidate => S.auditScore(location, candidate, S.DEFAULTS)));
        wanted += permittedArrival - originalArrival;
      }
      if (old && Math.abs(action.score - wanted) > 1e-9) errors.push(`Score ${key(action)}: C#=${action.score}, expected=${wanted}`);
    }
    for (const gain of row.guaranteedGains) if (gain.gain !== independentGuaranteedGain(fixture.state, fixture.state.rooms[fixture.state.position])) errors.push('Guaranteed rule gain differs from independent card arithmetic');
    const actor = fixture.state.rooms[fixture.state.position];
    const independentExecutionTargets = actor.kind === 'inmate' ? fixture.state.rooms.filter(room => room.kind === 'inmate' && room.tier < actor.tier && room.index !== actor.index
      && { mammoth: 'lion', lion: 'wolf', wolf: 'rat', rat: 'mammoth' }[actor.species] === room.species).map(room => room.index) : [];
    if (!sameSet(row.executionTargets, independentExecutionTargets)) errors.push('Execution must remove only strictly lower-tier prey');
    for (const action of actual) if (['devour', 'chain', 'swap'].includes(action.id) && action.room !== fixture.state.position) errors.push('Wrong acting room');
    const best = row.recommendation?.Best;
    if (!best || !Number.isFinite(best.Score) || Math.abs(best.Score - Math.max(...actual.map(action => action.score))) > 1e-9) errors.push('Recommendation is not a maximum-score candidate');
    if (row.recommendation?.Alternatives.length !== Math.max(0, actual.length - 1)) errors.push('Recommendation omitted alternatives');
    if (fixture.mode === 'card-correction') {
      if (fixture.id.startsWith('card/devour/')) {
        const action = actual.find(action => action.id === (fixture.state.phase === 'chain' ? 'chain' : 'devour'));
        if (!action || Math.abs(action.score - twoInmateDevour(fixture.state)) > 1e-9) errors.push('Independent capped Bernoulli devour expectation mismatch');
      } else for (const action of actual) {
        let wanted = expectedMap.get(key(action))?.score;
        if (fixture.state.tools.includes('whip') && action.id === 'patrol' && fixture.state.rooms[action.target].kind === 'locked') wanted -= .35 * .05;
        if (fixture.state.tools.includes('whip') && action.id === 'devour') {
          const eater = fixture.state.rooms[fixture.state.position];
          const by = independentGuaranteedGain(fixture.state, eater);
          wanted += .35 * (tierValue(fixture.state, eater.tier + by + 1) - tierValue(fixture.state, eater.tier + by));
        }
        if (action.id === 'choose:ring') {
          const minimum = fixture.state.tools.includes('hook') ? 1 : 0;
          const weights = T.MODEL.tierWeights.slice(minimum);
          wanted = Math.min(3, fixture.state.rooms.filter(room => room.kind === 'locked').length) * 1.5
            * weights.reduce((sum, weight, n) => sum + weight * tierValue(fixture.state, minimum + n), 0) / weights.reduce((a, b) => a + b, 0);
        }
        if (Math.abs(action.score - wanted) > 1e-9) errors.push(`Card correction ${key(action)}: C#=${action.score}, independently corrected=${wanted}`);
      }
    }
  }
  report.rows.push({ id: fixture.id, mode: fixture.mode, passed: !errors.length, errors });
  if (errors.length) report.passed = false;
}
for (const mode of ['parity', 'reject', 'card-correction', 'ratway-correction', 'execution-correction', 'permission-correction', 'chain-open-correction']) report.counts[mode] = report.rows.filter(row => row.mode === mode).length;
report.counts.total = report.rows.length; report.counts.failed = report.rows.filter(row => !row.passed).length;
fs.writeFileSync(path.join(__dirname, 'strategy-parity-report.json'), JSON.stringify(report, null, 2) + '\n');
console.log(JSON.stringify({ passed: report.passed, counts: report.counts, failures: report.rows.filter(row => !row.passed).slice(0, 10) }, null, 2));
process.exitCode = report.passed ? 0 : 1;
