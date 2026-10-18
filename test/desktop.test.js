'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const path = require('node:path');
const fs = require('node:fs/promises');
const os = require('node:os');
const vm = require('node:vm');
const { pathToFileURL } = require('node:url');
const { isLocalRendererURL, captureSize, friendlyError, assertDataURL, assertVisionProfile, ServiceWorker, CaptureSources } = require('../desktop/main.js');

test('only the exact local renderer can use the desktop bridge', () => {
  const entry = path.resolve('renderer/index.html');
  const url = pathToFileURL(entry).href;
  assert.equal(isLocalRendererURL(url, entry), true);
  assert.equal(isLocalRendererURL(`${url}#board`, entry), true);
  for (const candidate of [
    'https://example.com/renderer/index.html',
    'data:text/html,hello',
    pathToFileURL(path.resolve('renderer/other.html')).href,
    `${url}?load=https://example.com`,
    'file://remote/renderer/index.html',
    'not a URL',
  ]) assert.equal(isLocalRendererURL(candidate, entry), false, candidate);
});

test('screen capture requests the physical resolution on scaled displays', () => {
  const displays = [{ id: 10, size: { width: 2560, height: 1440 }, scaleFactor: 1.5 }];
  assert.deepEqual(captureSize(displays, { id: 'screen:10:0', display_id: '10' }), { width: 3840, height: 2160 });
  const window = captureSize(displays, { id: 'window:1:0' });
  assert.ok(window.width >= 3840);
  assert.ok(window.height >= 2160);
});

test('capture requires an enumerated source and checks that it still exists', async () => {
  const image = { getSize: () => ({ width: 1920, height: 1080 }), isEmpty: () => false, toDataURL: () => 'data:image/png;base64,AAAA' };
  let calls = 0;
  let available = true;
  const requests = [];
  const capturer = { async getSources(options) {
    calls += 1;
    requests.push(options);
    return available ? [{ id: 'window:10:0', name: 'Torchlight: Infinite', display_id: '', thumbnail: image }] : [];
  } };
  const capture = new CaptureSources(capturer, { getAllDisplays: () => [] });
  await assert.rejects(capture.capture('window:10:0'), /请先刷新来源列表/);
  assert.equal(calls, 0, 'no screenshot should occur before source selection');
  const list = await capture.sources();
  assert.equal(list[0].name, 'Torchlight: Infinite');
  assert.equal(list[0].thumbnailUrl, null);
  assert.deepEqual(requests[0].thumbnailSize, { width: 0, height: 0 });
  const frame = await capture.capture('window:10:0');
  assert.equal(frame.width, 1920);
  assert.equal(frame.height, 1080);
  assert.deepEqual(requests[1].types, ['window']);
  assert.ok(!Number.isNaN(Date.parse(frame.capturedAt)));
  available = false;
  await assert.rejects(capture.capture('window:10:0'), /所选窗口已关闭/);
  await assert.rejects(capture.capture('/etc/passwd'), /请先刷新来源列表/);
});

test('scan bridge rejects non-image URLs', () => {
  assert.doesNotThrow(() => assertDataURL('data:image/png;base64,AAAA'));
  for (const value of ['file:///etc/passwd', 'https://example.com/x.png', 'data:text/html;base64,AAAA', '', null]) {
    assert.throws(() => assertDataURL(value), /请提供/);
  }
});

test('incomplete desktop packages ask for a complete extraction', () => {
  assert.equal(friendlyError(new Error("Cannot find module '../lib/service.js'")), '应用文件不完整，请重新解压完整压缩包后打开助手。');
  assert.equal(friendlyError(new Error('operation timed out')), '本地分析超时，请稍后重试。');
});

test('calibration imports accept profile content rather than arbitrary paths', () => {
  assert.doesNotThrow(() => assertVisionProfile({ version: 1, samples: [] }));
  for (const value of ['/etc/passwd', 'C:\\Users\\friend\\secret.txt', null, []]) {
    assert.throws(() => assertVisionProfile(value), /不能使用文件路径/);
  }
  assert.throws(() => assertVisionProfile({ data: 'x'.repeat(2 * 1024 * 1024) }), /小于 2 MB/);
  const circular = {};
  circular.self = circular;
  assert.throws(() => assertVisionProfile(circular), /格式无效/);
});

test('the preload exposes only named offline calibration operations', async () => {
  const calls = [];
  let api;
  const electron = {
    contextBridge: { exposeInMainWorld(name, value) { assert.equal(name, 'torchlight'); api = value; } },
    ipcRenderer: { invoke(...args) { calls.push(args); return Promise.resolve('ok'); } },
  };
  vm.runInNewContext(await fs.readFile(path.join(__dirname, '..', 'desktop', 'preload.js'), 'utf8'), {
    require(name) { assert.equal(name, 'electron'); return electron; },
  });
  const payload = { dataUrl: 'data:image/png;base64,AAAA', board: {}, calibration: {} };
  const profile = { version: 1 };
  await api.newBoard();
  await api.visionProfile();
  await api.learnVision(payload);
  await api.importVision(profile);
  await api.resetVision();
  assert.equal(Object.isFrozen(api), true);
  assert.equal(api.invoke, undefined);
  assert.deepEqual(calls, [
    ['torchlight:new-board'], ['torchlight:vision-profile'], ['torchlight:learn-vision', payload],
    ['torchlight:import-vision', profile], ['torchlight:reset-vision'],
  ]);
});

test('desktop worker boots with only the local service and recovers after a rejected request', async (t) => {
  const dir = await fs.mkdtemp(path.join(os.tmpdir(), 'torchlight-offline-worker-'));
  await fs.mkdir(path.join(dir, 'desktop'));
  await fs.mkdir(path.join(dir, 'lib'));
  const workerPath = path.join(dir, 'desktop', 'worker.js');
  await fs.copyFile(path.join(__dirname, '..', 'desktop', 'worker.js'), workerPath);
  // There is deliberately no download helper or reference checkout in this fixture.
  await fs.writeFile(path.join(dir, 'lib', 'service.js'), `
    exports.bootstrap = async () => ({ tools: [{ id: 'local-tool' }], offline: true });
    exports.recommend = async (board) => ({ primary: { action: board.phase } });
    exports.newBoard = async () => ({ day: 1 });
    exports.visionProfile = async () => ({ version: 1, samples: [] });
    exports.learnVision = async (payload) => ({ learned: !!payload.dataUrl });
    exports.importVision = async (profile) => ({ imported: profile.version });
    exports.resetVision = async () => ({ reset: true });
  `);
  const worker = new ServiceWorker({ workerPath, timeoutMs: 3000 });
  t.after(async () => { await worker.close(); await fs.rm(dir, { recursive: true, force: true }); });
  const bootstrap = await worker.call('bootstrap');
  assert.equal(bootstrap.offline, true);
  assert.equal(bootstrap.tools[0].id, 'local-tool');
  assert.equal((await worker.call('newBoard')).day, 1);
  assert.equal((await worker.call('visionProfile')).version, 1);
  assert.equal((await worker.call('learnVision', [{ dataUrl: 'data:image/png;base64,AAAA' }])).learned, true);
  assert.equal((await worker.call('importVision', [{ version: 1 }])).imported, 1);
  assert.equal((await worker.call('resetVision')).reset, true);
  await assert.rejects(worker.call('unsupported'), /不支持此分析操作/);
  const result = await worker.call('recommend', [{ phase: 'patrol' }]);
  assert.equal(result.primary.action, 'patrol');
});

test('CPU-heavy analysis runs outside the main event loop and worker errors are recoverable', async (t) => {
  const dir = await fs.mkdtemp(path.join(os.tmpdir(), 'torchlight-worker-test-'));
  const workerPath = path.join(dir, 'fixture.cjs');
  await fs.writeFile(workerPath, `
    const { parentPort } = require('node:worker_threads');
    parentPort.on('message', ({id, method}) => {
      if (method === 'crash') throw new Error('fixture crash');
      const until = Date.now() + 100;
      while (Date.now() < until) { /* Simulate synchronous rollouts. */ }
      parentPort.postMessage({ id, ok: true, result: 'finished' });
    });
  `);
  const worker = new ServiceWorker({ workerPath, timeoutMs: 3000 });
  t.after(async () => { await worker.close(); await fs.rm(dir, { recursive: true, force: true }); });
  const heavy = worker.call('recommend');
  let mainLoopRan = false;
  await new Promise((resolve) => setTimeout(() => { mainLoopRan = true; resolve(); }, 15));
  assert.equal(mainLoopRan, true);
  assert.equal(worker.pending.size, 1, 'analysis is still running while the main loop responds');
  assert.equal(await heavy, 'finished');
  await assert.rejects(worker.call('crash'), /fixture crash/);
  assert.equal(await worker.call('recommend'), 'finished', 'a new worker can run after a crash');
});
