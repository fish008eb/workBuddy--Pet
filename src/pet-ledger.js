#!/usr/bin/env node
/**
 * pet-ledger.js — 情绪系统的「记忆」：只有 Stop hook 写，面板只读。
 *
 * 记录跨轮次需要的统计量：
 *   - 指数滑动平均（EMA）：判断「这一轮是不是异常」
 *   - 历史最好缓存命中：判断「破纪录」
 *   - 今日积分 / 累计轮次：判断阈值与里程碑
 *   - 最近活动时间：面板判断打盹
 *
 * 文件格式为 key=value（C# 版无 JSON 库，两端都只靠 IndexOf('=') 解析）。
 */

'use strict';

const fs = require('fs');
const path = require('path');

const EMA_ALPHA = 0.3;
const CREDIT_THRESHOLDS = [50, 100, 200, 500];
const TURN_MILESTONES = [10, 50, 100, 500];

function defaults() {
  return {
    version: 1,
    dayKey: '',
    totalTurns: 0,
    dayTurns: 0,
    dayCredit: 0,
    lastThreshold: 0,
    emaCredit: 0,
    emaCache: 0,
    emaPrompt: 0,
    bestCache: 0,
    firstSeenTs: 0,
    sessionStartTs: 0,
    lastActivityTs: 0,
    lastEventSeq: 0,
    flags: [],
  };
}

function num(v, fallback) {
  const n = Number(v);
  return Number.isFinite(n) ? n : fallback;
}

function readLedger(file) {
  const base = defaults();
  let text;
  try {
    text = fs.readFileSync(file, 'utf8');
  } catch (_) {
    return { ledger: base, isNew: true };
  }

  const map = {};
  for (const line of text.split('\n')) {
    const i = line.indexOf('=');
    if (i > 0) {
      map[line.slice(0, i).trim()] = line.slice(i + 1).trim();
    }
  }

  base.version = num(map.version, 1);
  base.dayKey = map.dayKey || '';
  base.totalTurns = num(map.totalTurns, 0);
  base.dayTurns = num(map.dayTurns, 0);
  base.dayCredit = num(map.dayCredit, 0);
  base.lastThreshold = num(map.lastThreshold, 0);
  base.emaCredit = num(map.emaCredit, 0);
  base.emaCache = num(map.emaCache, 0);
  base.emaPrompt = num(map.emaPrompt, 0);
  base.bestCache = num(map.bestCache, 0);
  base.firstSeenTs = num(map.firstSeenTs, 0);
  base.sessionStartTs = num(map.sessionStartTs, 0);
  base.lastActivityTs = num(map.lastActivityTs, 0);
  base.lastEventSeq = num(map.lastEventSeq, 0);
  base.flags = map.flags ? map.flags.split(',').filter(Boolean) : [];

  return { ledger: base, isNew: false };
}

function writeLedger(file, ledger) {
  const lines = [
    'version=' + ledger.version,
    'dayKey=' + ledger.dayKey,
    'totalTurns=' + ledger.totalTurns,
    'dayTurns=' + ledger.dayTurns,
    'dayCredit=' + ledger.dayCredit.toFixed(2),
    'lastThreshold=' + ledger.lastThreshold,
    'emaCredit=' + ledger.emaCredit.toFixed(3),
    'emaCache=' + ledger.emaCache.toFixed(2),
    'emaPrompt=' + Math.round(ledger.emaPrompt),
    'bestCache=' + ledger.bestCache.toFixed(2),
    'firstSeenTs=' + ledger.firstSeenTs,
    'sessionStartTs=' + ledger.sessionStartTs,
    'lastActivityTs=' + ledger.lastActivityTs,
    'lastEventSeq=' + ledger.lastEventSeq,
    'flags=' + ledger.flags.join(','),
  ];

  const tmp = file + '.tmp';
  try {
    fs.writeFileSync(tmp, lines.join('\n') + '\n', 'utf8');
    fs.renameSync(tmp, file);
  } catch (_) {
    try { fs.unlinkSync(tmp); } catch (_e) { /* ignore */ }
  }
}

function dayKeyOf(now) {
  const d = new Date(now);
  const m = String(d.getMonth() + 1).padStart(2, '0');
  const day = String(d.getDate()).padStart(2, '0');
  return d.getFullYear() + '-' + m + '-' + day;
}

/** 跨天时清空「今日」类计数，返回是否发生了跨天 */
function rollDay(ledger, now) {
  const key = dayKeyOf(now);
  if (ledger.dayKey === key) { return false; }
  ledger.dayKey = key;
  ledger.dayTurns = 0;
  ledger.dayCredit = 0;
  ledger.lastThreshold = 0;
  return true;
}

/** 指数滑动平均：prev 为 0 时直接取新值（冷启动） */
function ema(prev, value, alpha) {
  const a = alpha === undefined ? EMA_ALPHA : alpha;
  if (!prev) { return value; }
  return prev * (1 - a) + value * a;
}

/** 今日积分跨过的最高阈值（未跨过返回 null） */
function creditThreshold(ledger, dayCredit) {
  let hit = null;
  for (const t of CREDIT_THRESHOLDS) {
    if (dayCredit >= t && ledger.lastThreshold < t) { hit = t; }
  }
  return hit;
}

/** 累计轮次跨过的最高里程碑（未跨过返回 null） */
function turnMilestone(ledger, totalTurns) {
  let hit = null;
  for (const t of TURN_MILESTONES) {
    if (totalTurns >= t && !ledger.flags.includes('turns' + t)) { hit = t; }
  }
  return hit;
}

function markFlag(ledger, flag) {
  if (!ledger.flags.includes(flag)) { ledger.flags.push(flag); }
}

function ensureDirs(file) {
  try { fs.mkdirSync(path.dirname(file), { recursive: true }); } catch (_) { /* ignore */ }
}

module.exports = {
  EMA_ALPHA,
  CREDIT_THRESHOLDS,
  TURN_MILESTONES,
  defaults,
  readLedger,
  writeLedger,
  dayKeyOf,
  rollDay,
  ema,
  creditThreshold,
  turnMilestone,
  markFlag,
  ensureDirs,
};
