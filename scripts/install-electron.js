'use strict';
const fs = require('node:fs');
const path = require('node:path');
const { execFileSync } = require('node:child_process');

function installElectron() {
  const root = path.join(__dirname, '..');
  const packageRoot = path.join(root, 'node_modules', 'electron');
  const marker = path.join(packageRoot, 'path.txt');
  if (fs.existsSync(marker) && fs.existsSync(path.join(packageRoot, 'dist', fs.readFileSync(marker, 'utf8').trim()))) return;
  const args = [];
  if (process.allowedNodeEnvironmentFlags.has('--use-env-proxy')) args.push('--use-env-proxy');
  args.push(path.join(packageRoot, 'install.js'));
  execFileSync(process.execPath, args, { stdio: 'inherit', env: { ...process.env, electron_config_cache: path.join(root, '.cache', 'electron') }, timeout: 300000, windowsHide: true });
}
if (require.main === module) {
  try { installElectron(); console.log('Electron运行时已就绪。'); }
  catch (e) { console.error('Electron运行时安装失败，请检查网络连接后重试。', e.message); process.exitCode = 1; }
}
module.exports = { installElectron };
