'use strict';

// All runtime modules are this project's own implementations. No reference
// checkout, downloaded model, subprocess, or network is used by this factory.
const { createEngine } = require('./offline-engine');
const { createSolver } = require('./offline-solver');
const vision = require('./offline-vision');

const ENGINE_VERSION = 'local-offline-v2';
const RULE_VERSION = 'offline-guide-v2';
const unsupported = [
  { id: 'lion-trap', names: ['染红的捕兽夹', '染红的捕兽夹：狂狮', '狂狮捕兽夹', '寻猎', 'trap'], reason: '寻猎操作消耗及标记持续时间未确认，当前不提供确定建议。' },
  { id: 'shared-fortune', names: ['有福同享'], reason: '同代号计数是否包含自身未确认。' },
  { id: 'nemesis', names: ['宿敌'], reason: '生成食物的稀有度分布未确认。' },
  { id: 'shared-transfer', names: ['同罪移押'], reason: '复制是否触发额外开门效果未确认。' },
];
const runtimes = new Map();
function loadRuntime(options = {}) {
  const timing = options.muzzleTiming || 'after';
  if (!['before', 'after'].includes(timing)) throw new Error('muzzleTiming must be before or after');
  if (runtimes.has(timing)) return runtimes.get(timing);
  const engine = createEngine({ muzzleTiming: timing });
  const solver = createSolver(engine);
  const ruleNotes = {
    version: RULE_VERSION,
    implementation: '本项目独立实现，程序、规则与校准特征均在本机运行。',
    confirmed: ['僭越第二阶段一名升两阶。', '煽动添加一个事件。', '口枷人数阈值三名。'],
    assumptions: [`口枷人数按吞噬${timing === 'after' ? '后' : '前'}判断，尚待实机确认。`, ...(engine.RULE_ASSUMPTIONS || []).filter(text => !text.startsWith('口枷'))],
    unsupported: [...unsupported, ...(engine.UNSUPPORTED_NOTES || []).filter(item => !unsupported.some(known => known.id === item.id))],
    limitations: [
      '这是自有近似规则与有限采样策略，不能证明真实游戏中的绝对最优。',
      '开门、替换、事件概率采用明确的练习假设，需要真实对局校准。',
      `口枷人数按吞噬${timing === 'after' ? '后' : '前'}计算，时点未确认。`,
      '不使用第三方识图特征，首次离线识图需要用户校准牢房位置并学习已确认样本。',
      '游戏控制信息如天数、当前位置、目的地、刑具及任务需要人工确认，不声称已具备中文OCR。',
      '主动寻猎、三个未确认事件和特殊开局不在当前验证范围。',
    ],
  };
  const runtime = { engine, solver, scan: vision, scanData: {}, revision: ENGINE_VERSION, ruleNotes };
  runtimes.set(timing, runtime);
  return runtime;
}
module.exports = { loadRuntime, ENGINE_VERSION, RULE_VERSION };
