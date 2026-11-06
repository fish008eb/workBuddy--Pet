#!/usr/bin/env node
/**
 * WorkBuddy 本轮用量提示（Stop hook）
 *
 * 触发时机：主代理响应结束
 * 数据来源：Stop hook stdin 的 transcript_path 指向的会话 jsonl
 * 输出方式：Windows 系统通知（toast），不写 stdout、不阻塞对话
 *
 * 口径说明：
 *   一轮 = 最后一条 role=user 的 message 之后的所有模型请求
 *   一轮内每次工具调用都是一次独立 API 请求，prompt 会重复携带历史，
 *   所以这里按"真实计费口径"累加，反映本轮实际消耗（token 雪球效应）。
 */

'use strict';

const fs = require('fs');
const os = require('os');
const path = require('path');
const { spawn, spawnSync } = require('child_process');

const HOOKS_DIR = __dirname;
const LOG_FILE = path.join(HOOKS_DIR, 'turn-usage.log');
const MSG_FILE = path.join(HOOKS_DIR, 'last-turn.txt');
const EVENT_FILE = path.join(HOOKS_DIR, 'pet-events.txt');
const LEDGER_FILE = path.join(HOOKS_DIR, 'pet-ledger.txt');
const STATE_FILE = path.join(HOOKS_DIR, 'pet-state.txt');
const BUDGET_FILE = path.join(HOOKS_DIR, 'pet-budget.txt');
const HISTORY_FILE = path.join(HOOKS_DIR, 'pet-history.txt');
const SUMMARY_FILE = path.join(HOOKS_DIR, 'pet-summary.txt');
const PROJECTS_FILE = path.join(HOOKS_DIR, 'pet-projects.txt');

/**
 * personas.txt 的位置有两种情况：
 *   - 安装后：~/.workbuddy/hooks/assets/personas.txt（与脚本同级）
 *   - 源码仓库：<repo>/assets/personas.txt（脚本在 src/ 下）
 * 依次探测，取第一个存在的。
 */
function firstExisting(list) {
  for (const p of list) {
    try { if (fs.existsSync(p)) { return p; } } catch (_) { /* ignore */ }
  }
  return list[0];
}

const PERSONAS_FILE = firstExisting([
  path.join(HOOKS_DIR, 'assets', 'personas.txt'),
  path.join(HOOKS_DIR, '..', 'assets', 'personas.txt'),
]);
const MAX_LOG_BYTES = 256 * 1024;   // 日志超过 256KB 轮转一次
const MAX_TRANSCRIPT_BYTES = 40 * 1024 * 1024; // 超过 40MB 只读尾部

const ledgerLib = require('./pet-ledger');
const emotionLib = require('./pet-emotion');
const eventLib = require('./pet-event-writer');

function log(msg) {
  try {
    if (fs.existsSync(LOG_FILE) && fs.statSync(LOG_FILE).size > MAX_LOG_BYTES) {
      fs.renameSync(LOG_FILE, LOG_FILE + '.old');
    }
    fs.appendFileSync(
      LOG_FILE,
      `[${new Date().toISOString()}] ${msg}\n`,
      'utf8'
    );
  } catch (_) { /* 日志失败不影响主流程 */ }
}

function num(v) {
  const n = Number(v);
  return Number.isFinite(n) ? n : 0;
}

/** 中文习惯的数字缩写：1.2万 / 3.4亿 */
function fmt(n) {
  n = num(n);
  if (n >= 1e8) return (n / 1e8).toFixed(2) + '亿';
  if (n >= 1e4) return (n / 1e4).toFixed(1) + '万';
  return String(Math.round(n));
}

function fmtDuration(ms) {
  if (!Number.isFinite(ms) || ms <= 0) return null;
  const s = Math.round(ms / 1000);
  if (s < 60) return s + 's';
  const m = Math.floor(s / 60);
  const rs = s % 60;
  if (m < 60) return rs ? `${m}m${rs}s` : `${m}m`;
  const h = Math.floor(m / 60);
  return `${h}h${m % 60}m`;
}

/** 读取 transcript，返回按时间排序的记录数组 */
function readTranscript(p) {
  let buf;
  const st = fs.statSync(p);
  if (st.size > MAX_TRANSCRIPT_BYTES) {
    // 超大文件只读尾部，避免拖慢对话
    const fd = fs.openSync(p, 'r');
    const len = MAX_TRANSCRIPT_BYTES;
    buf = Buffer.alloc(len);
    fs.readSync(fd, buf, 0, len, st.size - len);
    fs.closeSync(fd);
  } else {
    buf = fs.readFileSync(p);
  }
  const text = buf.toString('utf8');
  const out = [];
  for (const line of text.split('\n')) {
    const t = line.trim();
    if (!t || t[0] !== '{') continue; // 尾部读取时首行可能被截断
    try {
      out.push(JSON.parse(t));
    } catch (_) { /* 半行忽略 */ }
  }
  return out;
}

function tsOf(rec) {
  const t = rec && rec.timestamp;
  if (typeof t === 'number') return t;
  if (typeof t === 'string') {
    const n = Date.parse(t);
    if (Number.isFinite(n)) return n;
    const n2 = Number(t);
    if (Number.isFinite(n2)) return n2;
  }
  return NaN;
}

/** 从一条记录的 rawUsage 里提取四段 token 与 credit */
function extractUsage(rec) {
  const raw = rec && rec.providerData && rec.providerData.rawUsage;
  if (!raw) return null;
  const prompt = num(raw.prompt_tokens);
  const completion = num(raw.completion_tokens);
  const detailCached = raw.prompt_tokens_details
    ? num(raw.prompt_tokens_details.cached_tokens)
    : 0;
  const hit = num(raw.prompt_cache_hit_tokens) || detailCached;
  const miss = raw.prompt_cache_miss_tokens != null
    ? num(raw.prompt_cache_miss_tokens)
    : Math.max(prompt - hit, 0);
  const reasoning = raw.completion_tokens_details
    ? num(raw.completion_tokens_details.reasoning_tokens)
    : num(raw.reasoning_tokens);
  return {
    prompt,
    completion,
    hit,
    miss,
    reasoning,
    credit: num(raw.credit),
    model: (rec.providerData && rec.providerData.model) || null,
    ts: tsOf(rec),
  };
}

/** 计算本轮用量 */
function computeTurn(records) {
  const usage = [];
  let lastUserTs = NaN;

  for (const rec of records) {
    const ts = tsOf(rec);
    if (rec.type === 'message' && rec.role === 'user') {
      if (Number.isFinite(ts)) lastUserTs = ts;
    }
    const u = extractUsage(rec);
    if (u) usage.push(u);
  }

  if (!usage.length) return null;

  // 只保留本轮：时间不早于最后一条用户消息
  let turn = Number.isFinite(lastUserTs)
    ? usage.filter((u) => !Number.isFinite(u.ts) || u.ts >= lastUserTs)
    : [];
  if (!turn.length) turn = usage.slice(-1); // 兜底：只算最后一次模型调用

  const sum = {
    prompt: 0, completion: 0, hit: 0, miss: 0,
    reasoning: 0, credit: 0, calls: turn.length,
    model: null, firstTs: NaN, lastTs: NaN,
  };
  for (const u of turn) {
    sum.prompt += u.prompt;
    sum.completion += u.completion;
    sum.hit += u.hit;
    sum.miss += u.miss;
    sum.reasoning += u.reasoning;
    sum.credit += u.credit;
    if (u.model) sum.model = u.model;
    if (Number.isFinite(u.ts)) {
      if (!Number.isFinite(sum.firstTs) || u.ts < sum.firstTs) sum.firstTs = u.ts;
      if (!Number.isFinite(sum.lastTs) || u.ts > sum.lastTs) sum.lastTs = u.ts;
    }
  }

  // token 相关的耗时：从用户发问到最后一个请求结束
  sum.durationMs = Number.isFinite(lastUserTs) && Number.isFinite(sum.lastTs)
    ? sum.lastTs - lastUserTs
    : (Number.isFinite(sum.lastTs) && Number.isFinite(sum.firstTs)
      ? sum.lastTs - sum.firstTs
      : NaN);

  // 缓存命中率：命中 / 总输入
  const denom = sum.hit + sum.miss;
  sum.cacheRate = denom > 0 ? (sum.hit / denom) * 100 : null;

  return { turn: sum, all: usage };
}

function buildMessage(s) {
  const rate = s.cacheRate == null ? '缓存 —'
    : `缓存 ${s.cacheRate.toFixed(1)}%`;
  const line1 = `本轮 ↑${fmt(s.prompt)} ↓${fmt(s.completion)} · ${rate}`;

  const parts = [`${s.calls} 次调用`];
  if (s.credit > 0) parts.push(`${s.credit.toFixed(2)} 积分`);
  const dur = fmtDuration(s.durationMs);
  if (dur) parts.push(dur);
  if (s.model) parts.push(s.model);
  const line2 = parts.join(' · ');

  // 单行紧凑版：给 systemMessage 用，尽量短
  const short = [`↑${fmt(s.prompt)}`, `↓${fmt(s.completion)}`, rate];
  if (s.cacheRate != null) {
    short[2] = `缓存${s.cacheRate.toFixed(1)}%`;
  }
  short.push(`${s.calls}次调用`);
  if (s.credit > 0) short.push(`${s.credit.toFixed(2)}积分`);
  if (dur) short.push(dur);

  return { title: 'WorkBuddy · 本轮用量', line1, line2, summary: short.join(' · ') };
}

/**
 * 双通道输出：
 *   1) stdout 的 {"systemMessage": ...} —— 官方定义为"仅显示给用户"的消息，
 *      桌面 App 若渲染它，就能在对话里直接看到本轮用量，且不进入模型上下文。
 *   2) Windows toast —— 受系统通知开关影响，被禁用时静默失败。
 * 任一通道生效即可，互不影响。
 *   3) 悬浮窗数据文件 panel-lines.txt —— 桌面 App 对前两者都不渲染时的兜底，
 *      由 floating-panel.ps1 轮询读取。
 */
function emitStdout(msg) {
  try {
    process.stdout.write(JSON.stringify({ systemMessage: msg.summary }) + '\n');
  } catch (_) { /* 忽略 */ }
}

/** 会话累计 */
function computeSession(usage) {
  const s = {
    prompt: 0, completion: 0, hit: 0, miss: 0,
    credit: 0, calls: usage.length, model: null,
  };
  for (const u of usage) {
    s.prompt += u.prompt;
    s.completion += u.completion;
    s.hit += u.hit;
    s.miss += u.miss;
    s.credit += u.credit;
    if (u.model) s.model = u.model;
  }
  const denom = s.hit + s.miss;
  s.cacheRate = denom > 0 ? (s.hit / denom) * 100 : null;
  return s;
}

function hhmm() {
  const d = new Date();
  return `${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}`;
}

/**
 * 今日汇总（跨会话、跨工作空间）
 *
 * 性能策略：只扫描 mtime 在今天 0 点之后的 jsonl。文件今天有写入，mtime
 * 必然落在今天，所以这个前置过滤是安全的；跨天的会话文件仍会进入候选，
 * 再由行内 timestamp 二次过滤。
 */
function computeToday() {
  const projectsDir = path.join(os.homedir(), '.workbuddy', 'projects');
  const start = new Date();
  start.setHours(0, 0, 0, 0);
  const cutoff = start.getTime();

  let dirs = [];
  try {
    dirs = fs.readdirSync(projectsDir);
  } catch (_) {
    return null;
  }

  const files = [];
  for (const d of dirs) {
    const dir = path.join(projectsDir, d);
    let files2 = [];
    try {
      if (!fs.statSync(dir).isDirectory()) continue;
      files2 = fs.readdirSync(dir);
    } catch (_) { continue; }

    for (const n of files2) {
      if (!n.endsWith('.jsonl')) continue;
      const f = path.join(dir, n);
      try {
        if (fs.statSync(f).mtimeMs >= cutoff) files.push({ file: f, proj: d });
      } catch (_) { /* 跳过不可读文件 */ }
    }
  }

  const agg = {
    prompt: 0, completion: 0, hit: 0, miss: 0,
    credit: 0, calls: 0, sessions: files.length, projects: [],
  };

  const byProject = {};
  for (const item of files) {
    let recs;
    try {
      recs = readTranscript(item.file);
    } catch (_) { continue; }
    let fileCredit = 0;
    let fileUsable = false;
    for (const r of recs) {
      const ts = tsOf(r);
      if (!Number.isFinite(ts) || ts < cutoff) continue;
      const u = extractUsage(r);
      if (!u) continue;
      agg.prompt += u.prompt;
      agg.completion += u.completion;
      agg.hit += u.hit;
      agg.miss += u.miss;
      agg.credit += u.credit;
      agg.calls += 1;
      fileCredit += u.credit;
      fileUsable = true;
    }
    if (!fileUsable) continue;
    const key = item.proj;
    if (!byProject[key]) { byProject[key] = { name: projectLabel(key), sessions: 0, credit: 0 }; }
    byProject[key].sessions += 1;
    byProject[key].credit += fileCredit;
  }

  agg.projects = Object.keys(byProject)
    .map((k) => byProject[k])
    .sort((a, b) => b.credit - a.credit);

  const denom = agg.hit + agg.miss;
  agg.cacheRate = denom > 0 ? (agg.hit / denom) * 100 : null;
  return agg;
}

/**
 * 把会话目录名还原成人能读的项目名。
 * 目录名是工作区绝对路径的安全化结果：`c-Users-fish0-Desktop-wb开发`。
 * 只按几个常见父目录标记切分，切不出来就保留原名——宁可难看，不要猜错。
 */
function projectLabel(encoded) {
  if (!encoded) { return '未知项目'; }
  let out = encoded;
  const marks = ['-Desktop-', '-Documents-', '-Downloads-'];
  for (const m of marks) {
    const i = out.lastIndexOf(m);
    if (i >= 0 && i + m.length < out.length) {
      out = out.slice(i + m.length);
      break;
    }
  }
  // 有些目录名本身就是一串时间戳，太长会把「项目分布」那行撑爆
  if (out.length > 22) { out = out.slice(0, 21) + '…'; }
  return out;
}

/** 预算配置：pet-budget.txt 里 `daily=` / `monthly=` / `ctxlimit=`，0 或缺失表示不启用 */
function readBudget() {
  const out = { daily: 0, monthly: 0, ctxLimit: 200000 };
  let text;
  try {
    text = fs.readFileSync(BUDGET_FILE, 'utf8');
  } catch (_) {
    return out;
  }
  for (const line of text.split('\n')) {
    const i = line.indexOf('=');
    if (i <= 0) { continue; }
    const k = line.slice(0, i).trim();
    const n = Number(line.slice(i + 1).trim());
    if (!Number.isFinite(n) || n <= 0) { continue; }
    if (k === 'daily') { out.daily = n; }
    if (k === 'monthly') { out.monthly = n; }
    if (k === 'ctxlimit') { out.ctxLimit = n; }
  }
  return out;
}

/**
 * 每日积分历史（pet-history.txt，`YYYY-MM-DD=积分`）。
 * 用于月度累计和后续的趋势图；只增不减，因为今日总额本身是单调累积的。
 */
function updateHistory(dayKey, dayCredit) {
  const map = {};
  let text = '';
  try { text = fs.readFileSync(HISTORY_FILE, 'utf8'); } catch (_) { /* 首次运行 */ }
  for (const line of text.split('\n')) {
    const i = line.indexOf('=');
    if (i <= 0) { continue; }
    const k = line.slice(0, i).trim();
    const v = Number(line.slice(i + 1).trim());
    if (/^\d{4}-\d{2}-\d{2}$/.test(k) && Number.isFinite(v)) { map[k] = v; }
  }

  if (dayCredit > (map[dayKey] || 0)) { map[dayKey] = dayCredit; }

  const keys = Object.keys(map).sort().slice(-400);
  const lines = keys.map((k) => k + '=' + map[k].toFixed(2));
  try { fs.writeFileSync(HISTORY_FILE, lines.join('\n') + '\n', 'utf8'); } catch (_) { /* 忽略 */ }

  const prefix = dayKey.slice(0, 7) + '-';
  let month = 0;
  let days = 0;
  for (const k of keys) {
    if (k.startsWith(prefix)) { month += map[k]; days += 1; }
  }
  return { month: month, days: days, map: map };
}

/**
 * 本月分项目成本（pet-projects.txt）。
 *
 * 刻意**不做全月扫描**：每个项目的月内成本由每轮增量累加得到——本轮花的钱
 * 必然发生在当前这个项目里，所以增量归属是准确的，而且不随月份增长变慢。
 * 代价是首月从安装那天开始记，不回填历史。
 */
function updateProjectMonth(monthKey, label, turn) {
  const map = {};
  let stored = '';
  let since = '';
  let text = '';
  try { text = fs.readFileSync(PROJECTS_FILE, 'utf8'); } catch (_) { /* 首次运行 */ }

  const today = ledgerLib.dayKeyOf(Date.now());
  const PSEUDO = '~\u8BB0\u8D26\u524D\u672A\u5206\u644A';
  let pseudo = null;

  for (const line of text.split('\n')) {
    if (line.startsWith('month=')) { stored = line.slice(6).trim(); continue; }
    if (line.startsWith('since=')) { since = line.slice(6).trim(); continue; }
    if (!line.startsWith('p|')) { continue; }
    const p = line.split('|');
    if (p.length < 5) { continue; }
    const row = {
      credit: Number(p[2]) || 0,
      prompt: Number(p[3]) || 0,
      calls: Number(p[4]) || 0,
    };
    // `~` 开头是派生行：它代表的钱已经在今日全量里，不能再参与累加
    if (p[1] === PSEUDO) { pseudo = row; continue; }
    map[p[1]] = row;
  }

  if (stored !== monthKey) {
    for (const k of Object.keys(map)) { delete map[k]; }
    since = '';
    pseudo = null;
  }

  // 记账起点：分段累加不是从月初开始的，界面要能说清「为什么本月合计比今日还小」
  if (!/^\d{4}-\d{2}-\d{2}$/.test(since)) { since = today; }

  const key = String(label || '未知项目').replace(/\|/g, '/');
  if (!map[key]) { map[key] = { credit: 0, prompt: 0, calls: 0 }; }
  map[key].credit += num(turn.credit, 0);
  map[key].prompt += num(turn.prompt, 0);
  map[key].calls += num(turn.calls, 0);

  const rows = Object.keys(map)
    .map((k) => ({ name: k, credit: map[k].credit, prompt: map[k].prompt, calls: map[k].calls }))
    .filter((r) => r.credit > 0 || r.calls > 0)
    .sort((a, b) => b.credit - a.credit)
    .slice(0, 50);

  // 把「今日全量」和「已分摊之和」的差额单列一行，否则首日会出现
  // 本月合计 < 今日，看着像算错了。差额是记账之前那段，确实无法分摊到项目。
  // 只在差额为正时重算；为零后保留原值，别让已经记进去的钱凭空消失。
  const booked = rows.reduce((s, r) => s + r.credit, 0);
  const gap = num(turn.todayCredit, 0) - booked;
  if (gap > 0.05) {
    const bookedPrompt = rows.reduce((s, r) => s + r.prompt, 0);
    const bookedCalls = rows.reduce((s, r) => s + r.calls, 0);
    pseudo = {
      credit: gap,
      prompt: Math.max(0, num(turn.todayPrompt, 0) - bookedPrompt),
      calls: Math.max(0, num(turn.todayCalls, 0) - bookedCalls),
    };
  }
  if (pseudo && pseudo.credit > 0) {
    rows.push({
      name: PSEUDO,
      credit: pseudo.credit,
      prompt: pseudo.prompt,
      calls: pseudo.calls,
    });
  }

  const lines = ['month=' + monthKey, 'since=' + since];
  for (const r of rows) {
    lines.push(`p|${r.name}|${r.credit.toFixed(2)}|${Math.round(r.prompt)}|${r.calls}`);
  }
  try { fs.writeFileSync(PROJECTS_FILE, lines.join('\n') + '\n', 'utf8'); } catch (_) { /* 忽略 */ }
  return rows;
}


/** 悬浮窗显示的四行文本（中文在此生成，ps1 保持纯 ASCII） */
function buildPanelLines(turn, sess, today) {
  const rate = (r) => (r == null ? '—' : r.toFixed(1) + '%');
  const lines = [
    `WorkBuddy 用量 · ${hhmm()}`,
    `本轮 ↑${fmt(turn.prompt)} ↓${fmt(turn.completion)} · 缓存${rate(turn.cacheRate)} · ${turn.calls}次`,
    `会话 ↑${fmt(sess.prompt)} · 缓存${rate(sess.cacheRate)} · ${sess.credit.toFixed(1)}积分`,
  ];
  if (today) {
    lines.push(
      `今日 ↑${fmt(today.prompt)} · 缓存${rate(today.cacheRate)} · ${today.credit.toFixed(1)}积分 · ${today.sessions}会话`
    );
  }
  return lines.join('\n');
}

function writePanel(lines) {
  try {
    fs.writeFileSync(path.join(HOOKS_DIR, 'panel-lines.txt'), lines + '\n', 'utf8');
  } catch (e) {
    log('写面板数据失败: ' + e.message);
  }
}

/**
 * 月度消耗预测。
 * 日均按「有记录的天数」算，不按自然日——周末不办公的话，按自然日会把均值摊薄，
 * 预测就会乐观得离谱。天数不足时（比如月初第一天）预测本身没意义，
 * 所以只在「按当前速度会超」时才给日期，否则直接说够用。
 */
function computeForecast(month, days, monthly, now) {
  const out = { avg: 0, pct: 0, text: '', date: '' };
  if (!(monthly > 0)) { return out; }

  out.pct = (month / monthly) * 100;
  out.avg = days > 0 ? month / days : 0;
  if (month >= monthly) { out.text = '预算已用完'; return out; }

  const d = new Date(now);
  const daysInMonth = new Date(d.getFullYear(), d.getMonth() + 1, 0).getDate();
  const leftInMonth = daysInMonth - d.getDate() + 1;
  const projected = month + out.avg * leftInMonth;

  if (out.avg > 0 && projected > monthly) {
    const need = Math.ceil((monthly - month) / out.avg);
    const e = new Date(now);
    e.setDate(e.getDate() + need);
    out.date = String(e.getMonth() + 1).padStart(2, '0') + '-' + String(e.getDate()).padStart(2, '0');
    out.text = '预计 ' + out.date + ' 见底';
  } else {
    out.text = '本月够用';
  }
  return out;
}

/** 结构化数据，供面板渲染成图标卡片（key=value，一行一项） */
function writePanelData(turn, sess, today, extra) {
  const ex = extra || {};
  const rate = turn.cacheRate == null ? '—' : turn.cacheRate.toFixed(1);
  const dayCredit = today ? today.credit : 0;
  const budget = ex.budget ? ex.budget.daily : 0;
  const budgetPct = budget > 0 ? Math.min(999, (dayCredit / budget) * 100) : 0;
  const kv = [
    'time=' + hhmm(),
    'turnIn=' + fmt(turn.prompt),
    'turnOut=' + fmt(turn.completion),
    'cacheRate=' + rate,
    'calls=' + turn.calls,
    'credit=' + (turn.credit > 0 ? turn.credit.toFixed(2) : '0'),
    'sessIn=' + fmt(sess.prompt),
    'sessCredit=' + (sess.credit > 0 ? sess.credit.toFixed(1) : '0'),
    'todayIn=' + (today ? fmt(today.prompt) : '—'),
    'todayCredit=' + (today ? today.credit.toFixed(1) : '—'),
    'todaySessions=' + (today ? today.sessions : 0),
    'todayCalls=' + (today ? today.calls : 0),
    'budget=' + (budget > 0 ? budget.toFixed(0) : '0'),
    'budgetPct=' + budgetPct.toFixed(1),
    'monthCredit=' + (ex.month == null ? '0' : ex.month.toFixed(1)),
    'monthBudget=' + (ex.budget && ex.budget.monthly > 0 ? ex.budget.monthly.toFixed(0) : '0'),
    'monthPct=' + (num(ex.monthPct, 0)).toFixed(1),
    'avgDaily=' + (num(ex.avgDaily, 0)).toFixed(1),
    'days=' + num(ex.days, 0),
    'forecast=' + (ex.forecast || ''),
    'projects=' + (today && today.projects ? today.projects.length : 0),
  ];
  try {
    fs.writeFileSync(path.join(HOOKS_DIR, 'panel-data.txt'), kv.join('\n') + '\n', 'utf8');
  } catch (e) {
    log('写面板结构数据失败: ' + e.message);
  }
}

/**
 * 今日小结：一段可以直接粘进周报/日报的纯文本。
 * 面板右键「复制今日小结」读的就是这个文件——排版放在这里，C# 侧只管复制。
 */
function writeSummary(turn, sess, today, extra) {
  const ex = extra || {};
  const d = new Date();
  const dayKey = `${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
  const rate = (r) => (r == null ? '—' : r.toFixed(1) + '%');

  const lines = [`WorkBuddy 小结 ${dayKey}`];
  if (today) {
    lines.push(
      `${today.sessions} 个会话 · ${today.projects ? today.projects.length : 0} 个项目 · ` +
      `${today.credit.toFixed(1)} 积分 · ${fmt(today.prompt)} token`
    );
    lines.push(`缓存命中 ${rate(today.cacheRate)} · ${today.calls} 次模型调用`);
    if (today.projects && today.projects.length > 0) {
      const top = today.projects.slice(0, 5)
        .map((p) => `${p.name} ${p.sessions} 会话`)
        .join(' · ');
      lines.push(`项目分布：${top}`);
    }
  } else {
    lines.push('今日汇总不可用（会话日志读取失败）');
  }
  if (ex.budget && ex.budget.daily > 0 && today) {
    const pct = (today.credit / ex.budget.daily) * 100;
    lines.push(`日预算 ${today.credit.toFixed(1)} / ${ex.budget.daily.toFixed(0)}（${pct.toFixed(0)}%）`);
  }
  lines.push(`本轮：↑${fmt(turn.prompt)} ↓${fmt(turn.completion)} · 缓存 ${rate(turn.cacheRate)} · ${turn.credit.toFixed(2)} 积分`);

  try {
    fs.writeFileSync(SUMMARY_FILE, lines.join('\n') + '\n', 'utf8');
  } catch (e) {
    log('写今日小结失败: ' + e.message);
  }
}

/** hook 进程的 PATH 不含 System32，powershell/wscript 一律走绝对路径 */
function windowsRoot() {
  return process.env.SystemRoot || process.env.WINDIR || 'C:\\Windows';
}

function resolvePowerShell() {
  const cands = [
    path.join(windowsRoot(), 'System32', 'WindowsPowerShell', 'v1.0', 'powershell.exe'),
    path.join(windowsRoot(), 'Sysnative', 'WindowsPowerShell', 'v1.0', 'powershell.exe'),
  ];
  for (const c of cands) {
    try { if (fs.existsSync(c)) { return c; } } catch (_) {}
  }
  return 'powershell.exe';
}

/**
 * 面板自愈：WorkBuddy 恢复旧会话时不一定触发 SessionStart，
 * 面板就会一直不出现。每轮结束顺手检查一次，没在跑就拉起来。
 */
function ensurePanel() {
  try {
    const exe = path.join(HOOKS_DIR, 'WbPet.exe');
    const vbs = path.join(HOOKS_DIR, 'run-panel.vbs');
    let target = '';
    if (fs.existsSync(exe)) {
      target = `"${exe}"`;
    } else if (fs.existsSync(vbs)) {
      target = `"${path.join(windowsRoot(), 'System32', 'wscript.exe')}" "${vbs}"`;
    }
    if (!target) { return; }

    const cmd =
      `$ErrorActionPreference='SilentlyContinue'; ` +
      `$n = @(Get-CimInstance Win32_Process -Filter "Name='WbPet.exe'").Count; ` +
      `if ($n -gt 0) { exit 0 }; ` +
      `$r = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments ` +
      `@{ CommandLine = '${target.replace(/'/g, "''")}' }; ` +
      `if ($r.ReturnValue -ne 0) { exit 1 }`;

    const r = spawnSync(resolvePowerShell(),
      ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-Command', cmd],
      { timeout: 6000, windowsHide: true, stdio: 'ignore' });

    if (r.error) {
      log('面板自愈失败: ' + r.error.message);
    } else if (r.status === 0) {
      log('面板自愈检查: 已在运行或已拉起');
    } else {
      log('面板自愈检查: 拉起失败 exit=' + r.status);
    }
  } catch (e) {
    log('面板自愈异常: ' + (e && e.message ? e.message : e));
  }
}

function notify(msg) {
  // 交给 PowerShell 弹 toast；文本经 UTF-8 文件传递，规避命令行编码问题
  const payload = `${msg.title}\n${msg.line1}\n${msg.line2}`;
  try {
    fs.writeFileSync(MSG_FILE, payload, 'utf8');
  } catch (e) {
    log('写消息文件失败: ' + e.message);
  }

  const ps1 = path.join(HOOKS_DIR, 'toast.ps1');
  if (!fs.existsSync(ps1)) {
    log('toast.ps1 不存在，跳过通知');
    return;
  }
  try {
    const child = spawn(
      resolvePowerShell(),
      ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass',
        '-File', ps1, '-MsgFile', MSG_FILE],
      { detached: true, stdio: 'ignore', windowsHide: true }
    );
    child.on('error', (e) => log('spawn powershell 失败: ' + e.message));
    child.unref();
  } catch (e) {
    log('通知异常: ' + e.message);
  }
}

/** 面板写在 pet-state.txt 里的偏好（人格 / 唠叨档），读不到用默认值 */
function readUiState() {
  const ui = { persona: 'cute', verbosity: 'normal' };
  try {
    const text = fs.readFileSync(STATE_FILE, 'utf8');
    for (const line of text.split('\n')) {
      const i = line.indexOf('=');
      if (i <= 0) { continue; }
      const k = line.slice(0, i).trim();
      const v = line.slice(i + 1).trim();
      if (k === 'persona' && v) { ui.persona = v; }
      if (k === 'verbosity' && v) { ui.verbosity = v; }
    }
  } catch (_) { /* 面板还没写过，用默认 */ }
  return ui;
}

/**
 * 预算提醒：月超支 > 月接近 > 日超支 > 日接近，每档每周期只报一次。
 * 月预算优先，因为那才是额度本身；日预算只负责「今天别失控」。
 * @returns {object|null} makeAlert 事件或 null
 */
function pickBudgetAlert(ledger, store, personaId, ctx) {
  const budget = ctx.budget || {};
  const dayKey = ledgerLib.dayKeyOf(Date.now());
  const monthKey = dayKey.slice(0, 7);

  // flags 里只保留本日 / 本月的预算标记，跨天跨月自动失效，避免无限增长
  ledger.flags = ledger.flags.filter((f) => {
    if (f.indexOf('budget') !== 0) { return true; }
    return f.indexOf(dayKey) >= 0 || f.indexOf(monthKey) >= 0;
  });

  function fire(key, emotion, pri, flag, vars) {
    if (ledger.flags.indexOf(flag) >= 0) { return null; }
    ledger.flags.push(flag);
    return emotionLib.makeAlert(store, personaId, key, emotion, pri, vars, ledger.totalTurns);
  }

  const monthly = num(budget.monthly, 0);
  const month = num(ctx.month, 0);
  if (monthly > 0) {
    const mp = (month / monthly) * 100;
    const vars = { month: fmt(month), mbudget: fmt(monthly), mpct: mp.toFixed(0) };
    if (mp >= 100) {
      return fire('budgetMonthOver', 'worried', 3, 'budgetMon-' + monthKey + '-over', vars);
    }
    if (mp >= 85) {
      return fire('budgetMonthWarn', 'tender', 2, 'budgetMon-' + monthKey + '-warn', vars);
    }
  }

  const daily = num(budget.daily, 0);
  if (daily > 0 && ctx.today) {
    const dp = (ctx.today.credit / daily) * 100;
    const vars = { today: ctx.today.credit.toFixed(1), budget: daily.toFixed(0), pct: dp.toFixed(0) };
    if (dp >= 100) {
      return fire('budgetOver', 'worried', 3, 'budgetDay-' + dayKey + '-over', vars);
    }
    if (dp >= 80) {
      return fire('budgetWarn', 'tender', 2, 'budgetDay-' + dayKey + '-warn', vars);
    }
  }
  return null;
}

/**
 * 判定本轮情绪 → 追加事件 → 落盘 ledger。
 * @returns {{event: object|null, seq: number}}
 */
function emitEmotion(ctx) {
  const ui = readUiState();
  const store = emotionLib.loadPersonas(PERSONAS_FILE);
  const read = ledgerLib.readLedger(LEDGER_FILE);
  const ledger = read.ledger;

  const ev0 = emotionLib.evaluateTurn({
    turn: ctx.turn,
    sess: ctx.sess,
    today: ctx.today,
    ledger: ledger,
    personaId: ui.persona,
    verbosity: ui.verbosity,
    store: store,
    now: Date.now(),
    turnId: ctx.turnId || '',
  });

  // 预算规则插队：花超了比「缓存不错」更值得打断。月预算优先于日预算。
  let ev = ev0;
  const bAlert = pickBudgetAlert(ledger, store, ui.persona, {
    budget: ctx.budget,
    today: ctx.today,
    month: ctx.month,
  });
  if (bAlert) { ev = bAlert; }

  let seq = ledger.lastEventSeq;
  if (ev) {
    const written = eventLib.appendEvent(EVENT_FILE, {
      ts: Date.now(),
      turnId: ctx.turnId || '',
      emotion: ev.emotion,
      pri: ev.pri,
      motion: ev.motion,
      accent: ev.accent,
      title: ev.title,
      text: ev.text,
      durSec: ev.durSec,
    }, ledger.lastEventSeq);
    if (written) { seq = written; }
  }
  ledger.lastEventSeq = seq;
  ledgerLib.writeLedger(LEDGER_FILE, ledger);
  return { event: ev, seq: seq };
}

/** 数据降级时直接抛一条紧急事件（不经过本轮统计） */
function emitAlert(key, emotion, pri) {
  const ui = readUiState();
  const store = emotionLib.loadPersonas(PERSONAS_FILE);
  const read = ledgerLib.readLedger(LEDGER_FILE);
  const ledger = read.ledger;

  const ev = emotionLib.makeAlert(store, ui.persona, key, emotion || 'worried', pri || 3);
  const written = eventLib.appendEvent(EVENT_FILE, {
    ts: Date.now(),
    turnId: 'alert-' + Date.now(),
    emotion: ev.emotion,
    pri: ev.pri,
    motion: ev.motion,
    accent: ev.accent,
    title: ev.title,
    text: ev.text,
    durSec: ev.durSec,
  }, ledger.lastEventSeq);

  ledger.lastEventSeq = written || ledger.lastEventSeq;
  ledger.lastActivityTs = Date.now();
  ledgerLib.writeLedger(LEDGER_FILE, ledger);
  return ev;
}

function main() {
  let input = '';
  process.stdin.setEncoding('utf8');
  process.stdin.on('data', (c) => { input += c; });
  process.stdin.on('end', () => {
    // 先保面板存活，避免 SessionStart 未触发时整轮白白跑完
    ensurePanel();

    try {
      const payload = JSON.parse(input || '{}');
      const tp = payload.transcript_path;
      if (!tp) { log('payload 无 transcript_path'); return; }
      if (!fs.existsSync(tp)) {
        log('transcript 不存在: ' + tp);
        emitAlert('noTranscript', 'worried', 3);
        return;
      }

      const records = readTranscript(tp);
      const res = computeTurn(records);
      if (!res) { log('未找到 usage 记录'); return; }

      const sum = res.turn;
      const sess = computeSession(res.all);
      const msg = buildMessage(sum);

      const t0 = Date.now();
      let today = null;
      try {
        today = computeToday();
      } catch (e) {
        log('今日汇总失败: ' + e.message);
      }
      const todayMs = Date.now() - t0;

      log(`OK calls=${sum.calls} in=${sum.prompt} out=${sum.completion} ` +
          `hit=${sum.hit} rate=${sum.cacheRate == null ? 'n/a' : sum.cacheRate.toFixed(1)} ` +
          `credit=${sum.credit} model=${sum.model} ` +
          `today=${today ? today.prompt + '/' + today.credit.toFixed(2) + '/' + today.sessions + 'sess' : 'n/a'} ` +
          `todayMs=${todayMs}`);

      // 预算与历史：两者都不依赖会话日志，即使 today 拿不到也要落盘
      const budget = readBudget();
      let month = 0;
      let days = 0;
      try {
        const hist = updateHistory(ledgerLib.dayKeyOf(Date.now()), today ? today.credit : 0);
        month = hist.month;
        days = hist.days;
      } catch (e) {
        log('历史累计失败: ' + e.message);
      }
      const fc = computeForecast(month, days, budget.monthly, Date.now());
      const extra = {
        budget: budget,
        month: month,
        days: days,
        avgDaily: fc.avg,
        monthPct: fc.pct,
        forecast: fc.text,
        exhaustDate: fc.date,
      };

      // 分项目成本：用本轮增量累加到当前项目，不做全月扫描
      try {
        const projDir = path.basename(path.dirname(tp));
        updateProjectMonth(
          ledgerLib.dayKeyOf(Date.now()).slice(0, 7),
          projectLabel(projDir),
          Object.assign({}, sum, {
            todayCredit: today ? today.credit : 0,
            todayPrompt: today ? today.prompt : 0,
            todayCalls: today ? today.calls : 0,
          })
        );
      } catch (e) {
        log('项目成本累计失败: ' + e.message);
      }

      emitStdout(msg);
      writePanel(buildPanelLines(sum, sess, today));
      writePanelData(sum, sess, today, extra);
      writeSummary(sum, sess, today, extra);

      // 情绪事件（阶段一规则 1-11 + 跨天 + 预算超支），写成功才更新 ledger 的 seq
      try {
        const turnId = payload.generation_id || payload.session_id || '';
        const emo = emitEmotion({
          turn: sum, sess: sess, today: today, turnId: turnId,
          budget: budget, month: month,
        });
        if (emo.event) {
          log('emotion=' + emo.event.emotion + ' key=' + emo.event.key + ' seq=' + emo.seq);
        }
      } catch (e) {
        log('情绪判定失败: ' + (e && e.message ? e.message : e));
      }

      notify(msg);
    } catch (e) {
      log('异常: ' + (e && e.stack ? e.stack : e));
    }
  });
}

/**
 * --demo <emotion> [key]：伪造一条情绪事件，用于验收，不依赖真实会话。
 *   node src/turn-usage.js --demo happy cacheGood
 */
function runDemo(args) {
  const ui = readUiState();
  const store = emotionLib.loadPersonas(PERSONAS_FILE);
  const emotion = (args[0] || 'happy').toLowerCase();
  const key = args[1] || 'chatty';
  const meta = emotionLib.EMOTIONS[emotion] || emotionLib.EMOTIONS.happy;

  const vars = {
    in: '146.4\u4e07', out: '1.2\u4e07', cache: '99.5', credit: '1.21',
    calls: 7, today: '80.6', n: 128, h: 2,
  };
  const rendered = emotionLib.render(store, ui.persona, key, vars, 0);
  const title = (rendered.emoji ? rendered.emoji + ' ' : '') + rendered.name;

  const read = ledgerLib.readLedger(LEDGER_FILE);
  const seq = eventLib.appendEvent(EVENT_FILE, {
    ts: Date.now(),
    turnId: 'demo-' + Date.now(),
    emotion: emotion,
    pri: 2,
    motion: meta.motion,
    accent: meta.accent,
    title: title,
    text: rendered.text,
    durSec: 6,
  }, read.ledger.lastEventSeq);

  read.ledger.lastEventSeq = seq || read.ledger.lastEventSeq;
  ledgerLib.writeLedger(LEDGER_FILE, read.ledger);

  console.log('demo: emotion=' + emotion + ' key=' + key + ' seq=' + seq + ' persona=' + ui.persona);
  console.log('  ' + title + '  |  ' + rendered.text);
  if (!seq) { console.log('  \u5199\u5165\u5931\u8d25\uff0c\u8bf7\u68c0\u67e5 ' + EVENT_FILE); }
}

module.exports = {
  computeTurn,
  computeSession,
  computeToday,
  emitEmotion,
  emitAlert,
  readUiState,
};

const argv = process.argv.slice(2);
if (argv[0] === '--demo') {
  runDemo(argv.slice(1));
} else {
  main();
}
