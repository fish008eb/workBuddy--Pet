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
const { spawn } = require('child_process');

const HOOKS_DIR = __dirname;
const LOG_FILE = path.join(HOOKS_DIR, 'turn-usage.log');
const MSG_FILE = path.join(HOOKS_DIR, 'last-turn.txt');
const MAX_LOG_BYTES = 256 * 1024;   // 日志超过 256KB 轮转一次
const MAX_TRANSCRIPT_BYTES = 40 * 1024 * 1024; // 超过 40MB 只读尾部

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
        if (fs.statSync(f).mtimeMs >= cutoff) files.push(f);
      } catch (_) { /* 跳过不可读文件 */ }
    }
  }

  const agg = {
    prompt: 0, completion: 0, hit: 0, miss: 0,
    credit: 0, calls: 0, sessions: files.length,
  };

  for (const f of files) {
    let recs;
    try {
      recs = readTranscript(f);
    } catch (_) { continue; }
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
    }
  }

  const denom = agg.hit + agg.miss;
  agg.cacheRate = denom > 0 ? (agg.hit / denom) * 100 : null;
  return agg;
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

/** 结构化数据，供面板渲染成图标卡片（key=value，一行一项） */
function writePanelData(turn, sess, today) {
  const rate = turn.cacheRate == null ? '—' : turn.cacheRate.toFixed(1);
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
  ];
  try {
    fs.writeFileSync(path.join(HOOKS_DIR, 'panel-data.txt'), kv.join('\n') + '\n', 'utf8');
  } catch (e) {
    log('写面板结构数据失败: ' + e.message);
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
      'powershell.exe',
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

function main() {
  let input = '';
  process.stdin.setEncoding('utf8');
  process.stdin.on('data', (c) => { input += c; });
  process.stdin.on('end', () => {
    try {
      const payload = JSON.parse(input || '{}');
      const tp = payload.transcript_path;
      if (!tp) { log('payload 无 transcript_path'); return; }
      if (!fs.existsSync(tp)) { log('transcript 不存在: ' + tp); return; }

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

      emitStdout(msg);
      writePanel(buildPanelLines(sum, sess, today));
      writePanelData(sum, sess, today);
      notify(msg);
    } catch (e) {
      log('异常: ' + (e && e.stack ? e.stack : e));
    }
  });
}

main();
