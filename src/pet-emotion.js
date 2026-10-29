#!/usr/bin/env node
/**
 * pet-emotion.js — 情绪判定与文案渲染。
 *
 * 输入：本轮统计（turn）、会话统计（sess）、今日统计（today）、ledger 记忆
 * 输出：最多一条情绪事件（emotion / 优先级 / 动作 / 强调色 / 文案）
 *
 * 判定全部在这里完成，因为下发给面板的数据都是格式化过的字符串（「146.4万」），
 * 面板无法再做数值比较。
 *
 * 规则编号与 docs/emotion-design.md 一一对应：
 *   1-9  单轮数据规则      10-11 数据降级     17 跨天
 *   12-16 时间规则由面板时钟判断（见 WbPet.cs EvaluateAmbient）
 */

'use strict';

const fs = require('fs');
const led = require('./pet-ledger');

const EMOTIONS = {
  happy:     { accent: '#F9A8D4', motion: 'bounce' },
  proud:     { accent: '#FBBF24', motion: 'bounce3' },
  worried:   { accent: '#F87171', motion: 'shake' },
  tender:    { accent: '#FDA4AF', motion: 'breathe' },
  sleepy:    { accent: '#6B7B9E', motion: 'sleep' },
  surprised: { accent: '#A78BFA', motion: 'squash' },
  calm:      { accent: '#333949', motion: 'none' },
};

const FALLBACK_TEXTS = {
  turnHigh: '\u672c\u8f6e\u8f93\u5165 {in}',
  callsHigh: '\u672c\u8f6e {calls} \u6b21\u8c03\u7528',
  creditHigh: '\u672c\u8f6e {credit} \u79ef\u5206',
  cacheBest: '\u7f13\u5b58 {cache}%\uff0c\u65b0\u7eaa\u5f55',
  cacheGood: '\u7f13\u5b58 {cache}%',
  cacheBad: '\u7f13\u5b58\u4ec5 {cache}%',
  creditMilestone: '\u4eca\u65e5 {today} \u79ef\u5206',
  turnsMilestone: '\u7b2c {n} \u8f6e',
  firstMeet: '\u7b2c\u4e00\u6b21\u89c1',
  degradedToday: '\u4eca\u65e5\u6c47\u603b\u5931\u8d25',
  noTranscript: '\u4f1a\u8bdd\u65e5\u5fd7\u7f3a\u5931',
  newDay: '\u65b0\u7684\u4e00\u5929',
  chatty: '\u2191{in} \u2193{out} \u00b7 \u7f13\u5b58 {cache}%',
};

function num(v, d) {
  const n = Number(v);
  return Number.isFinite(n) ? n : d;
}

/** 中文习惯缩写：1.2万 / 3.4亿 */
function fmt(n) {
  n = num(n, 0);
  if (n >= 1e8) { return (n / 1e8).toFixed(2) + '\u4ebf'; }
  if (n >= 1e4) { return (n / 1e4).toFixed(1) + '\u4e07'; }
  return String(Math.round(n));
}

function fill(tpl, vars) {
  return String(tpl).replace(/\{(\w+)\}/g, (m, k) => (vars[k] === undefined ? m : vars[k]));
}

/** 读 personas.txt → { personas: {id:{name,accent}}, lines: {'cute|cacheGood':[{v,text,emoji}]} } */
function loadPersonas(file) {
  const store = { personas: {}, lines: {} };
  let text;
  try {
    text = fs.readFileSync(file, 'utf8');
  } catch (_) {
    return store;
  }

  for (const raw of text.split('\n')) {
    const line = raw.trim();
    if (!line || line.startsWith('#')) { continue; }
    const p = line.split('|');
    if (p[0] === 'persona' && p.length >= 4) {
      store.personas[p[1]] = { id: p[1], name: p[2], accent: p[3] };
    } else if (p[0] === 'line' && p.length >= 6) {
      const key = p[1] + '|' + p[2];
      if (!store.lines[key]) { store.lines[key] = []; }
      store.lines[key].push({ v: num(p[3], 1), text: p[4], emoji: p[5] });
    }
  }
  return store;
}

/**
 * 取一条文案（三级兜底：指定变体 → 变体 1 → 内置兜底句）
 * @returns {{text:string, emoji:string, name:string, accent:string}}
 */
function render(store, personaId, key, vars, seed) {
  const persona = store.personas[personaId] || store.personas.cute || { id: 'cute', name: 'Cute', accent: '#F9A8D4' };
  const list = store.lines[personaId + '|' + key] || [];

  let picked = null;
  if (list.length > 0) {
    const idx = Math.abs(num(seed, 0)) % list.length;
    picked = list[idx] || list[0];
  }

  let text;
  let emoji;
  if (picked) {
    text = fill(picked.text, vars);
    emoji = picked.emoji;
  } else {
    text = fill(FALLBACK_TEXTS[key] || key, vars);
    emoji = '';
  }

  return { text, emoji, name: persona.name, accent: persona.accent };
}

/** 组装事件对象（面板直接消费） */
function makeEvent(store, personaId, rule) {
  const emo = EMOTIONS[rule.emotion] || EMOTIONS.calm;
  const rendered = render(store, personaId, rule.key, rule.vars || {}, rule.seed);
  const title = (rendered.emoji ? rendered.emoji + ' ' : '') + rendered.name;
  return {
    emotion: rule.emotion,
    pri: rule.pri,
    motion: emo.motion,
    accent: emo.accent,
    title,
    text: rendered.text,
    durSec: rule.durSec || 6,
    key: rule.key,
  };
}

/**
 * 判定一轮的情绪，并顺带更新 ledger（不落盘，由调用方 writeLedger）。
 *
 * @param {object} ctx
 *   turn       {prompt, completion, credit, calls, cacheRate}
 *   sess       {credit, prompt}
 *   today      {credit} 或 null
 *   ledger     readLedger 返回的 ledger 对象（会被就地修改）
 *   personaId  'cute' | 'snark' | 'eng'
 *   verbosity  'minimal' | 'normal' | 'chatty'
 *   store      loadPersonas 结果
 *   now        Date.now()
 *   turnId     Stop payload 的 generation_id
 * @returns {object|null} 事件对象（不含 seq/ts）
 */
function evaluateTurn(ctx) {
  const turn = ctx.turn || {};
  const ledger = ctx.ledger;
  const now = ctx.now || Date.now();
  const store = ctx.store;
  const personaId = ctx.personaId || 'cute';
  const verbosity = ctx.verbosity || 'normal';

  const cacheRate = num(turn.cacheRate, 0);
  const calls = num(turn.calls, 0);
  const credit = num(turn.credit, 0);
  const prompt = num(turn.prompt, 0);

  const coldStart = ledger.totalTurns < 5;
  const rolled = led.rollDay(ledger, now);
  const isFirstMeet = ledger.totalTurns === 0 && ledger.flags.length === 0;

  // 先算「是否跨天」，跨天首轮也算一次 happy（规则 17）
  const dayCreditBefore = ledger.dayCredit;
  ledger.dayCredit = dayCreditBefore + credit;
  ledger.totalTurns += 1;
  ledger.dayTurns += 1;
  if (!ledger.firstSeenTs) { ledger.firstSeenTs = now; }
  if (!ledger.sessionStartTs || now - ledger.lastActivityTs > 30 * 60 * 1000) {
    ledger.sessionStartTs = now;
  }
  ledger.lastActivityTs = now;

  const threshold = led.creditThreshold(ledger, ledger.dayCredit);
  if (threshold) { ledger.lastThreshold = threshold; }
  const milestone = led.turnMilestone(ledger, ledger.totalTurns);
  if (milestone) { led.markFlag(ledger, 'turns' + milestone); }

  const isBestCache = cacheRate > 0 && cacheRate > ledger.bestCache;
  if (isBestCache && calls >= 5) { ledger.bestCache = cacheRate; }

  const vars = {
    in: fmt(prompt),
    out: fmt(turn.completion),
    cache: cacheRate.toFixed(1),
    credit: credit.toFixed(1),
    calls: calls,
    today: fmt(ledger.dayCredit),
    n: ledger.totalTurns,
    h: (Math.max(1, Math.round((now - ledger.sessionStartTs) / 3600000))),
  };
  const seed = ledger.totalTurns;
  const rules = [];

  // 里程碑类先入列：优先级相同时 sort 稳定，里程碑会赢过普通单轮规则
  if (isFirstMeet) {
    rules.push({ key: 'firstMeet', emotion: 'happy', pri: 2 });
  }
  if (milestone) {
    rules.push({ key: 'turnsMilestone', emotion: 'proud', pri: 2 });
  }
  if (threshold) {
    rules.push({ key: 'creditMilestone', emotion: 'proud', pri: 2 });
  }

  if (prompt >= 2e7 || (!coldStart && ledger.emaPrompt > 0 && prompt >= ledger.emaPrompt * 3)) {
    rules.push({ key: 'turnHigh', emotion: 'surprised', pri: 2 });
  }
  if (calls >= 40) {
    rules.push({ key: 'callsHigh', emotion: 'surprised', pri: 2 });
  }
  if (credit >= 5 && (coldStart || ledger.emaCredit <= 0 || credit >= ledger.emaCredit * 3)) {
    rules.push({ key: 'creditHigh', emotion: 'worried', pri: 2 });
  }
  if (cacheRate >= 99.5 && calls >= 5 && isBestCache) {
    rules.push({ key: 'cacheBest', emotion: 'proud', pri: 2 });
  } else if (cacheRate >= 99.0 && calls >= 3) {
    rules.push({ key: 'cacheGood', emotion: 'happy', pri: 1 });
  }
  if (cacheRate > 0 && (cacheRate < 85 || (!coldStart && ledger.emaCache > 0 && ledger.emaCache - cacheRate >= 10))) {
    rules.push({ key: 'cacheBad', emotion: 'worried', pri: 2 });
  }
  if (rolled && !isFirstMeet) {
    rules.push({ key: 'newDay', emotion: 'happy', pri: 1 });
  }
  if (ctx.today === null || ctx.today === undefined) {
    rules.push({ key: 'degradedToday', emotion: 'worried', pri: 3 });
  }

  // 更新 EMA（放在判定之后，避免本轮值影响本轮判断）
  ledger.emaPrompt = led.ema(ledger.emaPrompt, prompt);
  ledger.emaCache = led.ema(ledger.emaCache, cacheRate);
  ledger.emaCredit = led.ema(ledger.emaCredit, credit);

  // 唠叨程度过滤：cacheGood 属于「每轮都想听」的内容，只在话痨档放行
  let usable = rules;
  if (verbosity === 'minimal') {
    usable = rules.filter((r) => r.pri === 3 || r.key === 'turnsMilestone' || r.key === 'creditMilestone');
  } else if (verbosity === 'normal') {
    usable = rules.filter((r) => r.key !== 'cacheGood');
  } else if (verbosity === 'chatty' && rules.length === 0) {
    usable = [{ key: 'chatty', emotion: 'happy', pri: 1 }];
  }

  if (usable.length === 0) { return null; }

  usable.sort((a, b) => b.pri - a.pri);
  const top = usable[0];
  top.vars = vars;
  top.seed = seed;
  return makeEvent(store, personaId, top);
}

/** 降级/异常事件的快捷构造（规则 10、11） */
function makeAlert(store, personaId, key, emotion, pri) {
  return makeEvent(store, personaId, { key: key, emotion: emotion || 'worried', pri: pri || 3 });
}

module.exports = {
  EMOTIONS,
  fmt,
  loadPersonas,
  render,
  evaluateTurn,
  makeAlert,
};
