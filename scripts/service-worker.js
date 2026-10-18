'use strict';
const { parentPort } = require('node:worker_threads');
const service = require('../lib/service');
const allowed = new Set(['bootstrap', 'demo', 'scan', 'recommend', 'report', 'newBoard', 'visionProfile', 'learnVision', 'importVision', 'resetVision']);
parentPort.on('message', async message => {
  try {
    if (!allowed.has(message.method)) throw new Error('未知操作。');
    parentPort.postMessage({ id: message.id, result: await service[message.method](...(message.args || [])) });
  } catch (e) { parentPort.postMessage({ id: message.id, error: e.message }); }
});
