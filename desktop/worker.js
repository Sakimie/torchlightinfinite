'use strict';

const { parentPort } = require('node:worker_threads');

const METHODS = new Set(['bootstrap', 'scan', 'recommend', 'demo', 'report', 'newBoard', 'visionProfile', 'learnVision', 'importVision', 'resetVision']);
let pending = Promise.resolve();

parentPort.on('message', (message) => {
  pending = pending.then(async () => {
    const { id, method, args } = message;
    try {
      if (!METHODS.has(method)) throw new Error('不支持此分析操作。');
      const service = require('../lib/service.js');
      if (typeof service[method] !== 'function') throw new Error('本地分析组件尚未就绪。');
      const result = await service[method](...(Array.isArray(args) ? args : []));
      parentPort.postMessage({ id, ok: true, result });
    } catch (error) {
      parentPort.postMessage({ id, ok: false, error: error && error.message ? error.message : String(error) });
    }
  });
});
