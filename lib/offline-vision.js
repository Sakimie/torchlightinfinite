'use strict';

// Original, local-only image features. No game assets, models, OCR, or network IO.
// Templates are learned exclusively from screenshots the user confirms locally.
const VERSION = 1;
const FEATURE_SIZE = 71;
const MAX_SAMPLES = 12;
const KINDS = ['locked', 'empty', 'nest', 'inmate'];
const BEASTS = ['rat', 'wolf', 'lion', 'mammoth'];
const TIERS = ['0', '1', '2', '3', '4'];
const CLASSES = { kind: KINDS, beast: BEASTS, tier: TIERS };
const NOTE = '离线模板只识别本机已校准的牢房外观；典狱长位置、天数、刑具、事件、标记和操作阶段需要人工确认。';

function checkImage(img) {
  if (!img || !Number.isInteger(img.width) || !Number.isInteger(img.height) || img.width < 2 || img.height < 2 || img.width * img.height > 24_000_000 || !img.data || img.data.length !== img.width * img.height * 4) {
    throw new Error('截图须为24百万像素以内的完整 RGBA 像素数据。');
  }
}

function checkCalibration(calibration) {
  const c = calibration;
  if (!c || !Number.isInteger(c.width) || !Number.isInteger(c.height) || c.width < 2 || c.height < 2 || !Array.isArray(c.points) || c.points.length !== 16) {
    throw new Error('请依次标记审讯室与15间牢房的中心，共16个位置，并保存截图尺寸。');
  }
  for (let n = 0; n < 16; n++) {
    const p = c.points[n];
    if (!p || !Number.isFinite(p.x) || !Number.isFinite(p.y) || p.x < 0 || p.x > 1 || p.y < 0 || p.y > 1) throw new Error(`第${n + 1}个校准位置必须是0至1的归一化坐标。`);
    if (c.points.slice(0, n).some(q => Math.hypot(p.x - q.x, p.y - q.y) < 0.00001)) throw new Error('校准位置不能重复，请重新点击16个牢房中心。');
  }
  if (c.radius !== undefined && (!Number.isFinite(c.radius) || c.radius <= 0 || c.radius > 0.15)) throw new Error('校准采样半径须大于0且不超过截图短边的15%。');
}

function compatible(img, calibration) {
  const change = Math.abs((img.width / img.height) / (calibration.width / calibration.height) - 1);
  if (change > 0.08) throw new Error('截图宽高比与校准时相差超过8%，请重新标记16个牢房中心。');
}

function validateProfile(profile) {
  try {
    if (!profile || profile.version !== VERSION) throw new Error('尚未保存离线识图校准，请先标记16个牢房中心并确认一张棋盘。');
    checkCalibration(profile.calibration);
    if (!profile.samples || typeof profile.samples !== 'object') throw new Error('离线校准缺少人工确认的外观样本，请重新保存。');
    const counts = { kind: {}, beast: {}, tier: {} };
    for (const [group, labels] of Object.entries(CLASSES)) {
      const samples = profile.samples[group];
      if (!samples || typeof samples !== 'object' || Array.isArray(samples)) throw new Error('离线模板格式损坏，请重新校准。');
      if (Object.keys(samples).some(key => !labels.includes(key))) throw new Error('离线模板包含不支持的种类，请重新校准。');
      for (const label of labels) {
        const list = samples[label] || [];
        if (!Array.isArray(list) || list.length > MAX_SAMPLES || list.some(feature => !Array.isArray(feature) || feature.length !== FEATURE_SIZE || feature.some(v => !Number.isFinite(v) || v < 0 || v > 1))) throw new Error('离线模板数值损坏，请重新校准。');
        counts[group][label] = list.length;
      }
    }
    return { ok: true, counts };
  } catch (error) {
    return { ok: false, reason: error.message };
  }
}

function radiusFor(img, calibration) {
  const short = Math.min(img.width, img.height);
  if (calibration.radius !== undefined) return Math.max(1, calibration.radius * short);
  let nearest = Infinity;
  for (let i = 0; i < 16; i++) for (let j = 0; j < i; j++) {
    const a = calibration.points[i], b = calibration.points[j];
    nearest = Math.min(nearest, Math.hypot((a.x - b.x) * img.width, (a.y - b.y) * img.height));
  }
  return Math.max(1, Math.min(short * 0.15, nearest * 0.36));
}

function pixel(img, x, y) {
  x = Math.max(0, Math.min(img.width - 1, x));
  y = Math.max(0, Math.min(img.height - 1, y));
  const x0 = Math.floor(x), y0 = Math.floor(y), fx = x - x0, fy = y - y0;
  const x1 = Math.min(x0 + 1, img.width - 1), y1 = Math.min(y0 + 1, img.height - 1);
  const a = (y0 * img.width + x0) * 4, b = (y0 * img.width + x1) * 4;
  const c = (y1 * img.width + x0) * 4, d = (y1 * img.width + x1) * 4;
  return [0, 1, 2].map(k => ((img.data[a + k] * (1 - fx) + img.data[b + k] * fx) * (1 - fy) + (img.data[c + k] * (1 - fx) + img.data[d + k] * fx) * fy) / 255);
}

function feature(img, point, radius, part) {
  const size = 12, pixels = [], hist = Array(24).fill(0), means = [0, 0, 0];
  const spatial = Array(36).fill(0), weights = Array(36).fill(0), gradients = Array(8).fill(0);
  const scale = part === 'beast' ? 0.58 : 1;
  let total = 0;
  for (let y = 0; y < size; y++) for (let x = 0; x < size; x++) {
    const u = (x + 0.5) / size * 2 - 1, v = (y + 0.5) / size * 2 - 1;
    if (part === 'tier' && Math.abs(u) < 0.65 && Math.abs(v) < 0.65) { pixels.push(null); continue; }
    const rgb = pixel(img, point.x * img.width + u * radius * scale, point.y * img.height + v * radius * scale);
    const gray = 0.299 * rgb[0] + 0.587 * rgb[1] + 0.114 * rgb[2];
    pixels.push(gray); total++;
    const bin = Math.floor(y / 2) * 6 + Math.floor(x / 2);
    spatial[bin] += gray; weights[bin]++;
    rgb.forEach((value, channel) => { means[channel] += value; hist[channel * 8 + Math.min(7, Math.floor(value * 8))]++; });
  }
  let edges = 0;
  for (let y = 0; y < size - 1; y++) for (let x = 0; x < size - 1; x++) {
    const i = y * size + x;
    if (pixels[i] === null || pixels[i + 1] === null || pixels[i + size] === null) continue;
    const dx = pixels[i + 1] - pixels[i], dy = pixels[i + size] - pixels[i];
    const magnitude = Math.min(1, Math.hypot(dx, dy));
    const bin = Math.min(7, Math.floor((Math.atan2(dy, dx) + Math.PI) / (2 * Math.PI) * 8));
    gradients[bin] += magnitude; edges++;
  }
  return [...spatial.map((v, i) => weights[i] ? v / weights[i] : 0.5), ...hist.map(v => v / total), ...means.map(v => v / total), ...gradients.map(v => v / Math.max(1, edges))].map(v => Math.round(v * 100000) / 100000);
}

function distance(a, b) {
  let sum = 0;
  for (let i = 0; i < FEATURE_SIZE; i++) sum += (a[i] - b[i]) ** 2;
  return Math.sqrt(sum / FEATURE_SIZE);
}

function identify(vector, samples) {
  const candidates = Object.entries(samples).filter(([, list]) => list.length).map(([label, list]) => ({ label, distance: Math.min(...list.map(v => distance(vector, v))) })).sort((a, b) => a.distance - b.distance);
  if (!candidates.length) return { conf: 0, reason: '尚无此类人工确认样本' };
  const best = candidates[0];
  // With a single learned class, require a particularly close match. A class is
  // never inferred merely because it is the only label in the local profile.
  const limit = candidates.length === 1 ? 0.065 : 0.115;
  const next = candidates[1];
  const margin = next ? (next.distance - best.distance) / Math.max(0.025, next.distance) : 1;
  if (best.distance > limit || margin < 0.16) return { conf: 0, reason: '外观与已学样本不匹配或类别难以区分' };
  const proximity = Math.max(0, 1 - best.distance / limit);
  const separation = next ? Math.min(1, margin / 0.6) : 1;
  const conf = Math.max(0, Math.min(0.94, 0.94 * proximity * separation));
  if (conf < 0.45) return { conf, reason: '外观差异较大，需要人工确认' };
  return { label: best.label, conf };
}

function blankSamples() {
  return Object.fromEntries(Object.entries(CLASSES).map(([group, labels]) => [group, Object.fromEntries(labels.map(label => [label, []]))]));
}

function addSample(list, vector) {
  if (list.some(old => distance(old, vector) < 0.003)) return;
  list.push(vector);
  if (list.length > MAX_SAMPLES) list.shift();
}

function sameCalibration(a, b) {
  return Math.abs((a.width / a.height) / (b.width / b.height) - 1) < 0.00001 && a.radius === b.radius && a.points.every((p, i) => p.x === b.points[i].x && p.y === b.points[i].y);
}

function trainProfile(img, board, calibration, previousProfile) {
  checkImage(img); checkCalibration(calibration); compatible(img, calibration);
  if (!board || !Array.isArray(board.cells) || board.cells.length !== 16) throw new Error('保存样本前请人工确认审讯室与15间牢房。');
  board.cells.forEach((cell, n) => {
    if (!cell || (cell.n !== undefined && cell.n !== n) || (n === 0 ? cell.kind !== 'office' : !KINDS.includes(cell.kind))) throw new Error(`请确认第${n + 1}格的牢房类型与编号。`);
    if (n && cell.kind === 'inmate' && (!BEASTS.includes(cell.beast) || !Number.isInteger(cell.tier) || cell.tier < 0 || cell.tier > 4)) throw new Error(`请确认第${n + 1}格囚犯的种族与0至4阶稀有度。`);
  });
  if (previousProfile) {
    const valid = validateProfile(previousProfile);
    if (!valid.ok) throw new Error(valid.reason);
  }
  const cleanCalibration = { width: calibration.width, height: calibration.height, points: calibration.points.map(p => ({ x: p.x, y: p.y })) };
  if (calibration.radius !== undefined) cleanCalibration.radius = calibration.radius;
  const samples = previousProfile && sameCalibration(previousProfile.calibration, calibration) ? JSON.parse(JSON.stringify(previousProfile.samples)) : blankSamples();
  for (const [group, labels] of Object.entries(CLASSES)) for (const label of labels) samples[group][label] ||= [];
  const radius = radiusFor(img, calibration);
  for (let n = 1; n < 16; n++) {
    const cell = board.cells[n], point = calibration.points[n];
    addSample(samples.kind[cell.kind], feature(img, point, radius, 'kind'));
    if (cell.kind === 'inmate') {
      addSample(samples.beast[cell.beast], feature(img, point, radius, 'beast'));
      addSample(samples.tier[String(cell.tier)], feature(img, point, radius, 'tier'));
    }
  }
  return { version: VERSION, calibration: cleanCalibration, samples, updatedAt: new Date().toISOString() };
}

function scan(img, profile) {
  const started = Date.now();
  const base = { ok: false, offlineCalibration: true, cells: [], warden: null, doors: null, days: null, tools: { tools: [] }, chromatic: null, decision: null, notes: [NOTE] };
  try {
    checkImage(img);
    const valid = validateProfile(profile);
    if (!valid.ok) throw new Error(valid.reason);
    compatible(img, profile.calibration);
    if (!Object.values(valid.counts.kind).some(Boolean)) throw new Error('尚无人工确认的牢房样本，请先保存一张已确认棋盘作为本地模板。');
    const radius = radiusFor(img, profile.calibration);
    let unknowns = 0;
    base.cells = profile.calibration.points.map((point, n) => {
      if (n === 0) return { n, kind: 'office', conf: 1, calibrated: true };
      const matched = identify(feature(img, point, radius, 'kind'), profile.samples.kind);
      if (!matched.label) {
        unknowns++;
        return { n, kind: 'empty', conf: 0, unknown: true, reason: matched.reason };
      }
      const cell = { n, kind: matched.label === 'nest' ? 'locked' : matched.label, conf: matched.conf };
      if (matched.label === 'nest') cell.nest = true;
      if (matched.label === 'inmate') {
        const beast = identify(feature(img, point, radius, 'beast'), profile.samples.beast);
        const tier = identify(feature(img, point, radius, 'tier'), profile.samples.tier);
        if (beast.label) cell.beast = beast.label;
        if (tier.label) cell.tier = Number(tier.label);
        cell.conf = Math.min(cell.conf, beast.conf, tier.conf);
        if (!beast.label || !tier.label) {
          cell.unknown = true; cell.conf = 0;
          cell.reason = !beast.label ? '未学过或无法可靠区分此囚犯种族，请人工纠正并补充样本。' : '未学过或无法可靠区分此稀有度，请人工纠正并补充样本。';
        }
      }
      if (cell.conf < 0.65) unknowns++;
      return cell;
    });
    if (unknowns) base.notes.push(`${unknowns}间牢房未可靠识别，请逐格确认；纠正后可保存为新的本地样本。`);
    base.ok = true;
  } catch (error) {
    base.reason = error.message;
    base.notes.push(error.message);
  }
  return { ...base, ms: Date.now() - started };
}

module.exports = { scan, trainProfile, validateProfile };
