'use strict';
try {
  require('./install-electron').installElectron();
  console.log('开发运行时安装完成。应用规则与识图组件均已内置，运行 npm start。');
} catch (e) { console.error(e.message); process.exitCode = 1; }
