#!/usr/bin/env node
/**
 * pet-event-writer.js — 把一条情绪事件追加到 pet-events.txt。
 *
 * 单行固定列，`|` 分隔，文案放最后几列：
 *   seq|ts|turnId|emotion|pri|motion|accent|title|text|durSec
 *
 * 为什么用追加日志而不是覆盖式状态文件：
 *   面板按字节偏移量增量读，天然不会漏事件；seq 用于去重；
 *   一次 turn 写一条，不需要锁。
 *
 * 轮转：超过 128KB 时改名为 pet-events.old.txt 并重建，seq 保持单调递增。
 */

'use strict';

const fs = require('fs');

const MAX_BYTES = 128 * 1024;
const TAIL_BYTES = 4096;
const MAX_TEXT = 400;

/** 读文件尾部，取最后一行的 seq（找不到返回 0） */
function lastSeqOf(file) {
  let fd;
  try {
    const st = fs.statSync(file);
    const len = Math.min(TAIL_BYTES, st.size);
    if (len <= 0) { return 0; }
    const buf = Buffer.alloc(len);
    fd = fs.openSync(file, 'r');
    fs.readSync(fd, buf, 0, len, st.size - len);
    const text = buf.toString('utf8');
    const lines = text.split('\n').filter((l) => l.trim().length > 0);
    for (let i = lines.length - 1; i >= 0; i--) {
      const seq = parseInt(lines[i].split('|')[0], 10);
      if (Number.isFinite(seq)) { return seq; }
    }
  } catch (_) {
    return 0;
  } finally {
    if (fd !== undefined) { try { fs.closeSync(fd); } catch (_) { /* ignore */ } }
  }
  return 0;
}

function sanitize(s, limit) {
  let v = String(s === undefined || s === null ? '' : s);
  v = v.replace(/\|/g, '\uff5c').replace(/[\r\n]+/g, ' ');
  const max = limit || MAX_TEXT;
  if (v.length > max) { v = v.slice(0, max) + '\u2026'; }
  return v.trim();
}

function formatEvent(ev) {
  return [
    ev.seq,
    ev.ts,
    sanitize(ev.turnId, 64),
    sanitize(ev.emotion, 16),
    ev.pri,
    sanitize(ev.motion, 16),
    sanitize(ev.accent, 16),
    sanitize(ev.title, 40),
    sanitize(ev.text, MAX_TEXT),
    ev.durSec,
  ].join('|');
}

/**
 * 追加一条事件。
 * @param {string} file     pet-events.txt 路径
 * @param {object} ev       {emotion, pri, motion, accent, title, text, durSec, turnId}
 * @param {number} baseSeq  ledger 里记录的上一个 seq（文件被轮转/删除时兜底）
 * @returns {number} 实际写入的 seq
 */
function appendEvent(file, ev, baseSeq) {
  let fileSeq = lastSeqOf(file);
  if (fileSeq < (baseSeq || 0)) { fileSeq = baseSeq || 0; }

  let size = 0;
  try { size = fs.statSync(file).size; } catch (_) { size = 0; }
  if (size > MAX_BYTES) {
    try {
      const old = file + '.old';
      try { fs.unlinkSync(old); } catch (_) { /* ignore */ }
      fs.renameSync(file, old);
    } catch (_) { /* ignore */ }
  }

  const seq = fileSeq + 1;
  const record = {
    seq,
    ts: ev.ts || Date.now(),
    turnId: ev.turnId || '',
    emotion: ev.emotion || 'calm',
    pri: ev.pri === undefined ? 2 : ev.pri,
    motion: ev.motion || 'none',
    accent: ev.accent || '#333949',
    title: ev.title || '',
    text: ev.text || '',
    durSec: ev.durSec === undefined ? 6 : ev.durSec,
  };

  try {
    fs.appendFileSync(file, formatEvent(record) + '\n', 'utf8');
  } catch (_) {
    return 0;
  }
  return seq;
}

/** 解析一行事件（面板侧逻辑同样在 C# 里实现一份，这里给测试与 --demo 用） */
function parseLine(line) {
  const p = line.split('|');
  if (p.length < 10) { return null; }
  return {
    seq: Number(p[0]),
    ts: Number(p[1]),
    turnId: p[2],
    emotion: p[3],
    pri: Number(p[4]),
    motion: p[5],
    accent: p[6],
    title: p[7],
    text: p[8],
    durSec: Number(p[9]),
  };
}

module.exports = { appendEvent, lastSeqOf, parseLine, formatEvent, MAX_BYTES };
