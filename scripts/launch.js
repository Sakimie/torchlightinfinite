'use strict';
const path = require('node:path');
const { spawn } = require('node:child_process');
try {
  require('./install-electron').installElectron();
  const launchEnv = { ...process.env };
  delete launchEnv.ELECTRON_RUN_AS_NODE;
  const child = spawn(require('electron'), [path.join(__dirname, '..'), ...process.argv.slice(2)], { stdio: 'inherit', env: launchEnv, windowsHide: false });
  child.on('error', e => { console.error(e.message); process.exitCode = 1; });
  child.on('exit', code => { process.exitCode = code ?? 1; });
} catch (e) { console.error(e.message); process.exitCode = 1; }
