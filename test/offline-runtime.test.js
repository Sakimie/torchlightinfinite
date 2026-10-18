'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { execFileSync } = require('node:child_process');
const path = require('node:path');

test('local service starts and computes recommendations with all network, git and reference reads forbidden', () => {
  const result = execFileSync(process.execPath, ['-e', `
    const fs=require('node:fs');
    const read=fs.readFileSync;
    fs.readFileSync=function(file,...args){
      if(String(file).includes('.cache/reference') || String(file).includes('scan-data.js') || String(file).includes('tool-combos.js'))throw new Error('External reference read forbidden');
      return read.call(this,file,...args);
    };
    const deny=()=>{throw new Error('Network and child tools forbidden')};
    for(const key of ['execFileSync','spawn','exec','execSync'])require('node:child_process')[key]=deny;
    for(const name of ['node:http','node:https']){require(name).request=deny;require(name).get=deny;}
    global.fetch=deny;
    const s=require('./lib/service');
    const boot=s.bootstrap();
    const answer=s.recommend(s.demo(),{rollouts:4,budgetMs:1000});
    if(!boot.offline||!answer.primary.action)throw new Error('Offline startup failed');
    const imports=Object.keys(require.cache);
    if(imports.some(p=>p.includes('.cache/reference')))throw new Error('Reference module imported');
    console.log(JSON.stringify({offline:boot.offline,action:answer.primary.action.type,tools:boot.tools.length}));
  `], { cwd: path.join(__dirname, '..'), encoding: 'utf8', timeout: 10000 });
  const value = JSON.parse(result.trim());
  assert.equal(value.offline, true);
  assert.ok(value.action);
  assert.ok(value.tools >= 20);
});
