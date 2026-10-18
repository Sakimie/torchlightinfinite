'use strict';
const { test, beforeEach, afterEach } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const http = require('node:http');
const https = require('node:https');
const net = require('node:net');
const { PNG } = require('pngjs');
const service = require('../lib/service');

// Original geometric PNG fixtures; they contain no screenshots or game assets.
const points = Array.from({ length: 16 }, (_, n) => ({ x: (n % 4 + 0.5) / 4, y: (Math.floor(n / 4) + 0.5) / 4 }));
const calibration = { width: 320, height: 320, points, radius: 0.095 };
const beasts = { lion: [210, 50, 20], wolf: [20, 60, 210], rat: [40, 170, 60], mammoth: [210, 135, 30] };
const tiers = [[70, 70, 70], [20, 220, 220], [150, 25, 220], [220, 220, 220], [240, 235, 20]];
let directory, originalDataDirectory;

function fixture(overrides = {}) {
  const board = { ...service.newBoard(), day: 4, position: 3, phase: 'patrol', destinations: [4, 6], objective: 'other', tools: ['coin'], usurpStage: 1, confirmed: true, needsReview: [], usedAction: true };
  board.cells = Array.from({ length: 16 }, (_, n) => ({ n, kind: n ? ['locked', 'empty', 'nest', 'inmate'][n % 4] : 'office' }));
  for (const cell of board.cells) if (cell.kind === 'nest') cell.due = 6;
  for (const [n, beast, tier] of [[3, 'lion', 0], [7, 'wolf', 1], [11, 'rat', 2], [15, 'mammoth', 4]]) Object.assign(board.cells[n], { beast, tier });
  const png = new PNG({ width: 320, height: 320 });
  for (let y = 0; y < png.height; y++) for (let x = 0; x < png.width; x++) {
    const n = Math.floor(y / 80) * 4 + Math.floor(x / 80), cell = board.cells[n];
    const u = (x / png.width - points[n].x) / calibration.radius, v = (y / png.height - points[n].y) / calibration.radius;
    let rgb = [8, 8, 8];
    if (Math.abs(u) < 1.01 && Math.abs(v) < 1.01) {
      if (overrides[n]) rgb = overrides[n];
      else if (cell.kind === 'empty') rgb = [70, 75, 80];
      else if (cell.kind === 'locked') rgb = Math.floor((u + 1) * 5) % 2 ? [15, 18, 20] : [130, 135, 140];
      else if (cell.kind === 'nest') rgb = u * u + v * v < 0.4 ? [100, 65, 30] : [10, 20, 25];
      else if (cell.kind === 'inmate') rgb = Math.abs(u) > 0.65 || Math.abs(v) > 0.65 ? tiers[cell.tier] : beasts[cell.beast];
    }
    png.data.set([...rgb, 255], (y * png.width + x) * 4);
  }
  return { board, dataUrl: 'data:image/png;base64,' + PNG.sync.write(png).toString('base64'), calibration };
}

beforeEach(t => {
  originalDataDirectory = process.env.TORCHLIGHT_DATA_DIR;
  directory = fs.mkdtempSync(path.join(os.tmpdir(), 'torchlight-vision-service-'));
  process.env.TORCHLIGHT_DATA_DIR = directory;
  const noNetwork = () => { throw new Error('离线识图测试禁止网络访问'); };
  t.mock.method(http, 'request', noNetwork);
  t.mock.method(http, 'get', noNetwork);
  t.mock.method(https, 'request', noNetwork);
  t.mock.method(https, 'get', noNetwork);
  t.mock.method(net, 'connect', noNetwork);
  t.mock.method(net, 'createConnection', noNetwork);
  t.mock.method(globalThis, 'fetch', noNetwork);
  const readFile = fs.readFileSync.bind(fs);
  t.mock.method(fs, 'readFileSync', (filename, ...args) => {
    if (typeof filename === 'string' && /(?:tartarus-simulator|[/\\]reference(?:[/\\.]|$)|scan-data)/i.test(filename)) throw new Error('离线识图测试禁止读取参考文件');
    return readFile(filename, ...args);
  });
});

afterEach(() => {
  if (originalDataDirectory === undefined) delete process.env.TORCHLIGHT_DATA_DIR;
  else process.env.TORCHLIGHT_DATA_DIR = originalDataDirectory;
  fs.rmSync(directory, { recursive: true, force: true });
});

test('learning persists only numeric local templates and visionProfile reads them from disk', () => {
  const payload = fixture(), before = JSON.stringify(payload);
  assert.equal(service.visionProfile().ready, false);
  assert.throws(() => service.scan(payload.dataUrl), /校准/);
  const learned = service.learnVision(payload);
  assert.equal(learned.ready, true);
  assert.equal(JSON.stringify(payload), before);
  const filename = path.join(directory, 'vision-profile.json');
  assert.equal(fs.existsSync(filename), true);
  const raw = fs.readFileSync(filename, 'utf8'), stored = JSON.parse(raw);
  assert.deepEqual(service.visionProfile().profile, stored);
  assert.deepEqual(stored.calibration, calibration);
  assert.ok(!raw.includes('base64') && !raw.includes('data:image'));
  assert.deepEqual(Object.keys(stored).sort(), ['calibration', 'samples', 'updatedAt', 'version']);
  for (const group of Object.values(stored.samples)) for (const samples of Object.values(group)) for (const vector of samples) {
    assert.equal(vector.length, 71);
    assert.ok(vector.every(v => typeof v === 'number' && Number.isFinite(v)));
  }
  assert.deepEqual(fs.readdirSync(directory), ['vision-profile.json']);
  if (process.platform !== 'win32') assert.equal(fs.statSync(filename).mode & 0o777, 0o600);
  stored.updatedAt = '2026-10-17T00:00:00.000Z';
  fs.writeFileSync(filename, JSON.stringify(stored));
  assert.equal(service.visionProfile().profile.updatedAt, stored.updatedAt);
});

test('known appearance scan preserves control fields and always requires their manual confirmation', () => {
  const payload = fixture();
  service.learnVision(payload);
  const reading = service.scan(payload.dataUrl, payload.board);
  assert.equal(reading.recognition.ok, true);
  assert.equal(reading.board.confirmed, false);
  for (const key of ['day', 'position', 'phase', 'usedAction', 'objective', 'usurpStage']) assert.equal(reading.board[key], payload.board[key]);
  assert.deepEqual(reading.board.destinations, payload.board.destinations);
  assert.deepEqual(reading.board.tools, payload.board.tools);
  for (const n of [3, 7, 11, 15]) {
    assert.equal(reading.board.cells[n].beast, payload.board.cells[n].beast);
    assert.equal(reading.board.cells[n].tier, payload.board.cells[n].tier);
  }
  assert.match(reading.board.needsReview.join(' '), /不读取控制文字/);
  assert.match(reading.board.needsReview.join(' '), /天数/);
  assert.match(reading.board.needsReview.join(' '), /刑具/);
  assert.throws(() => service.recommend(reading.board), /核对/);
  assert.doesNotThrow(() => service.normalizeBoard(reading.board));
});

test('import and reset complete a local sharing cycle and strip untrusted metadata', () => {
  const payload = fixture(), learned = service.learnVision(payload);
  const exported = structuredClone(learned.profile);
  exported.image = payload.dataUrl;
  exported.path = '/do-not-open-or-write';
  exported.command = 'do-not-execute';
  exported.calibration.path = '/do-not-open';
  const reset = service.resetVision();
  assert.equal(reset.ready, false);
  assert.equal(fs.existsSync(path.join(directory, 'vision-profile.json')), false);
  assert.throws(() => service.scan(payload.dataUrl), /校准/);
  const imported = service.importVision(exported);
  assert.equal(imported.ready, true);
  assert.deepEqual(imported.profile.samples, learned.profile.samples);
  assert.equal(Object.hasOwn(imported.profile, 'image'), false);
  assert.equal(Object.hasOwn(imported.profile, 'path'), false);
  assert.equal(Object.hasOwn(imported.profile, 'command'), false);
  assert.equal(Object.hasOwn(imported.profile.calibration, 'path'), false);
  assert.equal(service.scan(payload.dataUrl, payload.board).recognition.ok, true);
  service.resetVision();
  service.resetVision();
  assert.equal(service.visionProfile().ready, false);
});

test('unknown species and unrelated room appearances stay editable and can learn corrections', () => {
  const payload = fixture(), state = service.learnVision(payload);
  const sparse = structuredClone(state.profile);
  sparse.samples.beast.rat = [];
  service.importVision(sparse);
  const uncertain = service.scan(payload.dataUrl, payload.board);
  assert.equal(uncertain.board.confirmed, false);
  assert.ok(uncertain.issues.some(issue => issue.cell === 11 && /种族/.test(issue.message)));
  assert.equal(uncertain.board.cells[11].beast, 'rat');
  assert.doesNotThrow(() => service.normalizeBoard(uncertain.board));
  assert.doesNotThrow(() => service.scan(payload.dataUrl, uncertain.board));
  const unrelated = fixture({ 4: [255, 0, 255] });
  const unknownRoom = service.scan(unrelated.dataUrl, uncertain.board);
  assert.ok(unknownRoom.issues.some(issue => issue.cell === 4));
  assert.doesNotThrow(() => service.normalizeBoard(unknownRoom.board));
  assert.equal(service.learnVision(payload).counts.beast.rat, 1);
  const corrected = service.scan(payload.dataUrl, payload.board);
  assert.equal(corrected.issues.some(issue => issue.cell === 11 && /种族/.test(issue.message)), false);
});

test('bad imports do not overwrite existing samples and damaged files can be reset', () => {
  const payload = fixture(), state = service.learnVision(payload);
  const malformed = structuredClone(state.profile);
  malformed.samples.kind.locked = [[1, 2]];
  const storedBefore = fs.readFileSync(path.join(directory, 'vision-profile.json'), 'utf8');
  assert.throws(() => service.importVision(malformed), /模板数值/);
  assert.equal(fs.readFileSync(path.join(directory, 'vision-profile.json'), 'utf8'), storedBefore);
  assert.throws(() => service.importVision('/tmp/profile.json'), /JSON对象/);
  fs.writeFileSync(path.join(directory, 'vision-profile.json'), '{broken');
  assert.equal(service.visionProfile().ready, false);
  assert.match(service.visionProfile().summary, /损坏/);
  assert.equal(service.resetVision().ready, false);
  assert.equal(service.learnVision(payload).ready, true);
});
