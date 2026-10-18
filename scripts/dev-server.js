'use strict';
const http = require('node:http');
const fs = require('node:fs');
const path = require('node:path');
const { Worker } = require('node:worker_threads');

function createServer() {
  const root = path.resolve(__dirname, '..', 'renderer');
  const worker = new Worker(path.join(__dirname, 'service-worker.js'));
  let next = 0;
  const pending = new Map();
  worker.on('message', message => {
    const call = pending.get(message.id);
    if (!call) return;
    pending.delete(message.id);
    clearTimeout(call.timer);
    message.error ? call.reject(new Error(message.error)) : call.resolve(message.result);
  });
  const cancel = error => { for (const p of pending.values()) { clearTimeout(p.timer); p.reject(error); } pending.clear(); };
  worker.on('error', cancel);
  worker.on('exit', () => cancel(new Error('分析服务已停止。')));
  const invoke = (method, args) => new Promise((resolve, reject) => {
    const id = ++next;
    const timer = setTimeout(() => { pending.delete(id); reject(new Error('分析超时，请减少采样次数后重试。')); }, 30000);
    pending.set(id, { resolve, reject, timer });
    worker.postMessage({ id, method, args });
  });
  const json = (res, status, value) => { res.writeHead(status, { 'Content-Type': 'application/json; charset=utf-8', 'Cache-Control': 'no-store', 'X-Content-Type-Options': 'nosniff' }); res.end(JSON.stringify(value)); };
  const routes = { '/api/bootstrap': 'bootstrap', '/api/demo': 'demo', '/api/scan': 'scan', '/api/recommend': 'recommend', '/api/report': 'report', '/api/new-board': 'newBoard', '/api/vision-profile': 'visionProfile', '/api/learn-vision': 'learnVision', '/api/import-vision': 'importVision', '/api/reset-vision': 'resetVision' };
  const server = http.createServer(async (req, res) => {
    try {
      const url = new URL(req.url, 'http://127.0.0.1');
      if (url.pathname.startsWith('/api/')) {
        const method = routes[url.pathname];
        if (!method) return json(res, 404, { error: '接口不存在。' });
        if (req.headers.origin && req.headers.origin !== `http://${req.headers.host}`) return json(res, 403, { error: '仅接受本地应用请求。' });
        let body = {};
        if (['scan', 'recommend', 'learnVision', 'importVision', 'resetVision'].includes(method)) {
          if (req.method !== 'POST') return json(res, 405, { error: '需要POST请求。' });
          if (!(req.headers['content-type'] || '').startsWith('application/json')) return json(res, 415, { error: '需要JSON格式。' });
          let size = 0;
          const parts = [];
          for await (const chunk of req) {
            size += chunk.length;
            if (size > 34 * 1024 * 1024) return json(res, 413, { error: '图片过大。' });
            parts.push(chunk);
          }
          body = JSON.parse(Buffer.concat(parts).toString('utf8'));
        } else if (req.method !== 'GET') return json(res, 405, { error: '需要GET请求。' });
        const args = method === 'scan' ? [body.dataUrl, body.previousBoard] : method === 'recommend' ? [body.board, body.options] : method === 'learnVision' ? [body] : method === 'importVision' ? [body.profile] : [];
        return json(res, 200, await invoke(method, args));
      }
      if (req.method !== 'GET') { res.writeHead(405); return res.end(); }
      const files = { '/': 'index.html', '/index.html': 'index.html', '/app.js': 'app.js', '/styles.css': 'styles.css' };
      const file = files[url.pathname];
      if (!file) { res.writeHead(404); return res.end('Not found'); }
      const contentTypes = { '.html': 'text/html; charset=utf-8', '.js': 'text/javascript; charset=utf-8', '.css': 'text/css; charset=utf-8' };
      res.writeHead(200, { 'Content-Type': contentTypes[path.extname(file)], 'Cache-Control': 'no-store', 'Content-Security-Policy': "default-src 'self'; img-src 'self' data: blob:; style-src 'self' 'unsafe-inline'; script-src 'self'; connect-src 'self'; base-uri 'none'; frame-ancestors 'none'", 'X-Content-Type-Options': 'nosniff' });
      res.end(fs.readFileSync(path.join(root, file)));
    } catch (e) { if (!res.headersSent) json(res, 400, { error: e.message || '读取失败。' }); else res.end(); }
  });
  server.on('error', () => { cancel(new Error('本地服务启动失败。')); worker.terminate(); });
  server.on('close', () => { cancel(new Error('服务已关闭。')); worker.terminate(); });
  return server;
}
if (require.main === module) {
  try {
    const port = Number(process.env.PORT || 4317);
    const server = createServer();
    server.on('error', e => { console.error(e.code === 'EADDRINUSE' ? `端口${port}已被占用，请关闭已有本地服务或设置PORT。` : e.message); process.exitCode = 1; });
    server.listen(port, '127.0.0.1', () => console.log(`典狱助手本地开发服务已启动（端口${port}）。网页模式支持导图和手动棋盘；游戏窗口截图请用 npm start。`));
  } catch (e) { console.error(e.message); process.exitCode = 1; }
}
module.exports = { createServer };
