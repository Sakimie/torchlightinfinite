'use strict';
const { test } = require('node:test');
const assert = require('node:assert/strict');
const { scan, trainProfile, validateProfile } = require('../lib/offline-vision');

// All fixtures below are original geometric drawings, not game captures/assets.
const points = Array.from({ length: 16 }, (_, n) => ({ x: (n % 4 + 0.5) / 4, y: (Math.floor(n / 4) + 0.5) / 4 }));
const calibration = { width: 400, height: 400, points, radius: 0.095 };
const cells = Array.from({ length: 16 }, (_, n) => ({ n, kind: n === 0 ? 'office' : ['locked', 'empty', 'nest', 'inmate'][n % 4] }));
for (const [n, beast, tier] of [[3, 'lion', 0], [7, 'wolf', 1], [11, 'rat', 2], [15, 'mammoth', 4]]) Object.assign(cells[n], { beast, tier });
const board = { cells };
const beastColors = { lion: [210, 50, 20], wolf: [20, 60, 210], rat: [40, 170, 60], mammoth: [210, 135, 30] };
const tierColors = [[70, 70, 70], [20, 220, 220], [150, 25, 220], [220, 220, 220], [240, 235, 20]];

function drawing(size = 400, overrides = {}) {
  const img = { width: size, height: size, data: new Uint8Array(size * size * 4) };
  for (let y = 0; y < size; y++) for (let x = 0; x < size; x++) {
    const n = Math.floor(y / (size / 4)) * 4 + Math.floor(x / (size / 4));
    const c = cells[n], p = points[n];
    const u = (x / size - p.x) / calibration.radius, v = (y / size - p.y) / calibration.radius;
    let color = [8, 8, 8];
    if (Math.abs(u) < 1.01 && Math.abs(v) < 1.01) {
      if (overrides[n]) color = overrides[n];
      else if (c.kind === 'empty') color = [70, 75, 80];
      else if (c.kind === 'locked') color = Math.floor((u + 1) * 5) % 2 ? [15, 18, 20] : [130, 135, 140];
      else if (c.kind === 'nest') color = (u * u + v * v < 0.4) ? [100, 65, 30] : [10, 20, 25];
      else if (c.kind === 'inmate') color = Math.abs(u) > 0.65 || Math.abs(v) > 0.65 ? tierColors[c.tier] : beastColors[c.beast];
    }
    const offset = (y * size + x) * 4;
    img.data.set([...color, 255], offset);
  }
  return img;
}

function learned() { return trainProfile(drawing(), board, calibration); }

test('calibration requires sixteen distinct, normalized points and a valid radius', () => {
  for (const bad of [
    { ...calibration, points: points.slice(1) },
    { ...calibration, points: points.map((p, n) => n === 1 ? points[0] : p) },
    { ...calibration, points: points.map((p, n) => n === 1 ? { x: 1.5, y: p.y } : p) },
    { ...calibration, radius: 0 },
    { ...calibration, radius: 0.5 },
  ]) assert.throws(() => trainProfile(drawing(), board, bad), /16|归一化|重复|半径/);
  assert.equal(validateProfile(learned()).ok, true);
});

test('learned geometric fixtures classify locally and leave nonvisual fields unclaimed', () => {
  const result = scan(drawing(), learned());
  assert.equal(result.ok, true);
  assert.equal(result.offlineCalibration, true);
  for (let n = 0; n < 16; n++) {
    const expected = cells[n], actual = result.cells[n];
    assert.equal(actual.n, n);
    assert.equal(actual.kind, expected.kind === 'nest' ? 'locked' : expected.kind);
    assert.ok(actual.conf >= 0.65, `cell ${n}: ${JSON.stringify(actual)}`);
    if (expected.kind === 'nest') assert.equal(actual.nest, true);
    if (expected.kind === 'inmate') {
      assert.equal(actual.beast, expected.beast);
      assert.equal(actual.tier, expected.tier);
    }
  }
  for (const key of ['warden', 'doors', 'days', 'chromatic', 'decision']) assert.equal(result[key], null);
  assert.deepEqual(result.tools, { tools: [] });
  assert.match(result.notes.join(' '), /人工确认/);
});

test('normalized positions support proportional resolution changes and estimated radius', () => {
  const profile = learned();
  const result = scan(drawing(800), profile);
  assert.equal(result.ok, true);
  for (const n of [3, 7, 11, 15]) {
    assert.equal(result.cells[n].beast, cells[n].beast);
    assert.equal(result.cells[n].tier, cells[n].tier);
    assert.ok(result.cells[n].conf > 0.65);
  }
  const noRadius = { width: 400, height: 400, points };
  assert.equal(scan(drawing(), trainProfile(drawing(), board, noRadius)).ok, true);
  const changedAspect = { width: 800, height: 400, data: new Uint8Array(800 * 400 * 4) };
  assert.match(scan(changedAspect, profile).reason, /宽高比/);
});

test('absent profiles, empty profiles and malformed templates fail with a Chinese reason', () => {
  const img = drawing();
  assert.equal(scan(img).ok, false);
  assert.match(scan(img).reason, /校准/);
  const profile = learned();
  for (const group of Object.values(profile.samples)) for (const key of Object.keys(group)) group[key] = [];
  assert.match(scan(img, profile).reason, /样本/);
  profile.samples.kind.empty = [[Infinity]];
  assert.equal(validateProfile(profile).ok, false);
  assert.equal(scan(img, profile).ok, false);
});

test('unknown appearances and missing species samples never receive confident labels', () => {
  const profile = learned();
  const unrelated = scan(drawing(400, { 3: [255, 0, 255] }), profile).cells[3];
  assert.ok(unrelated.conf < 0.65);
  assert.equal(unrelated.unknown, true);
  profile.samples.beast.rat = [];
  const missingSpecies = scan(drawing(), profile).cells[11];
  assert.ok(missingSpecies.conf < 0.65);
  assert.equal(missingSpecies.unknown, true);
  assert.equal(missingSpecies.beast, undefined);
  assert.match(missingSpecies.reason, /种族/);
});

test('a single learned class does not label unrelated appearances and unlearned tiers stay unknown', () => {
  const sparse = learned();
  for (const group of Object.values(sparse.samples)) for (const label of Object.keys(group)) {
    if (group !== sparse.samples.kind || label !== 'locked') group[label] = [];
  }
  const result = scan(drawing(400, { 1: [255, 0, 255] }), sparse);
  assert.equal(result.ok, true);
  assert.equal(result.cells[4].kind, 'locked');
  assert.ok(result.cells[4].conf > 0.65);
  assert.equal(result.cells[1].unknown, true);
  assert.equal(result.cells[1].conf, 0);
  const missingTier = learned();
  missingTier.samples.tier['4'] = [];
  const cell = scan(drawing(), missingTier).cells[15];
  assert.equal(cell.unknown, true);
  assert.equal(cell.tier, undefined);
  assert.match(cell.reason, /稀有度/);
});

test('ambiguous classes remain uncertain and users can supplement missing classes', () => {
  const profile = learned();
  profile.samples.kind.empty = structuredClone(profile.samples.kind.locked);
  const ambiguous = scan(drawing(), profile).cells[4];
  assert.ok(ambiguous.conf < 0.65);
  const sparse = learned();
  sparse.samples.beast.rat = [];
  const supplemented = trainProfile(drawing(), board, calibration, sparse);
  assert.ok(supplemented.samples.beast.rat.length);
  assert.equal(scan(drawing(), supplemented).cells[11].beast, 'rat');
});

test('training and scanning preserve inputs, cap samples and store only numeric features', () => {
  const img = drawing(), bytes = img.data.slice();
  const boardBefore = JSON.stringify(board), calibrationBefore = JSON.stringify(calibration);
  let profile = learned();
  const before = JSON.stringify(profile);
  const next = trainProfile(img, board, calibration, profile);
  scan(img, profile);
  assert.equal(JSON.stringify(profile), before);
  assert.equal(JSON.stringify(board), boardBefore);
  assert.equal(JSON.stringify(calibration), calibrationBefore);
  assert.deepEqual(img.data, bytes);
  profile = next;
  for (let i = 0; i < 18; i++) profile = trainProfile(drawing(400, { 1: [i * 12, 60, 90] }), board, calibration, profile);
  assert.ok(profile.samples.kind.empty.length <= 12);
  for (const groups of Object.values(profile.samples)) for (const samples of Object.values(groups)) for (const sample of samples) {
    assert.equal(sample.length, 71);
    assert.ok(sample.every(v => typeof v === 'number' && Number.isFinite(v) && v >= 0 && v <= 1));
  }
  assert.equal(Object.hasOwn(profile, 'image'), false);
  assert.equal(Object.hasOwn(profile, 'data'), false);
});

test('changed calibration replaces incompatible old features instead of mixing locations', () => {
  const profile = learned();
  profile.samples.beast.lion.push(Array(71).fill(0.12345));
  const changed = { ...calibration, radius: 0.09 };
  const next = trainProfile(drawing(), board, changed, profile);
  assert.equal(next.samples.beast.lion.length, 1);
  assert.throws(() => trainProfile(drawing(), { cells: [] }, calibration), /人工确认/);
  assert.throws(() => trainProfile({ width: 1, height: 1, data: [] }, board, calibration), /RGBA/);
});
