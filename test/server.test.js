'use strict';
const { test } = require('node:test');
const assert = require('node:assert/strict');
const { once } = require('node:events');
const { createServer } = require('../scripts/dev-server');

test('local server serves the app and real recommendations; rejects cross-origin state requests', async () => {
  const server = createServer();
  server.listen(0, '127.0.0.1');
  await once(server, 'listening');
  const origin = `http://127.0.0.1:${server.address().port}`;
  try {
    const page = await fetch(origin);
    assert.equal(page.status, 200);
    assert.match(await page.text(), /火炬之光助手/);
    const boot = await (await fetch(origin + '/api/bootstrap')).json();
    assert.ok(boot.tools.length > 20);
    const board = await (await fetch(origin + '/api/demo')).json();
    const res = await fetch(origin + '/api/recommend', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ board, options: { rollouts: 4, budgetMs: 500 } }) });
    assert.equal(res.status, 200);
    assert.ok((await res.json()).primary.action);
    const denied = await fetch(origin + '/api/recommend', { method: 'POST', headers: { 'Content-Type': 'application/json', Origin: 'https://elsewhere.example' }, body: '{}' });
    assert.equal(denied.status, 403);
    assert.equal((await fetch(origin + '/../../package.json')).status, 404);
  } finally { await new Promise(resolve => server.close(resolve)); }
});
