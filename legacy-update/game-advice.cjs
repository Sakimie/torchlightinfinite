#!/usr/bin/env node
'use strict';
const { recommend } = require('./game-runtime.cjs');
const LIMIT = 1024 * 1024;
let size = 0, chunks = [];
process.stdin.on('data', chunk => {
  size += chunk.length;
  if (size > LIMIT) {
    process.stderr.write('棋盘输入超过1 MiB，已停止读取。\n');
    process.exit(1);
  }
  chunks.push(chunk);
});
process.stdin.on('end', () => {
  try {
    const payload = JSON.parse(Buffer.concat(chunks).toString('utf8'));
    if (!payload || typeof payload !== 'object' || Array.isArray(payload) || Object.keys(payload).some(key => !['board', 'options'].includes(key))) throw new Error('只接受board棋盘及可选options，不能传入文件路径或命令。');
    if (payload.options !== undefined && (!payload.options || typeof payload.options !== 'object' || Array.isArray(payload.options) || Object.keys(payload.options).some(key => !['budgetMs', 'rollouts'].includes(key)))) throw new Error('不接受外部路径、代码或运行时设置。');
    // Current version uses deterministic single-step advice; timing options do not change it.
    const result = recommend(payload.board);
    process.stdout.write(`${JSON.stringify(result)}\n`);
  } catch (error) {
    const expected = error instanceof SyntaxError ? '输入不是有效的JSON棋盘。' : /ENOENT/.test(error.message) ? '未找到原v0.3的本地引擎或策略。请将补丁放入原app目录并按说明启动。' : error.message;
    process.stderr.write(`${String(expected).slice(0, 1000)}\n`); process.exitCode = 1;
  }
});
process.stdin.on('error', () => { process.stderr.write('无法读取棋盘输入。\n'); process.exitCode = 1; });
