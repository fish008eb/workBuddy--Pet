#!/usr/bin/env node
/**
 * make-sound.js — 合成提示音（零依赖，直接写 PCM WAV）。
 *
 * 用法：
 *   node tools/make-sound.js                 # 生成 assets/sounds/turn-done.wav
 *   node tools/make-sound.js --tone alert    # 生成偏低沉的版本
 *
 * 设计：
 *   两个音符的短提示音（A5 → D6），每个音符用正弦 + 一点三次谐波增加厚度，
 *   指数衰减包络避免"咔"声，整体 0.45s、峰值 -4dBFS 左右，不刺耳。
 */

'use strict';

const fs = require('fs');
const path = require('path');

const RATE = 44100;

function parseArgs(argv) {
  const out = { tone: 'done', outFile: '' };
  for (let i = 0; i < argv.length; i++) {
    if (argv[i] === '--tone') { out.tone = argv[++i]; }
    else if (argv[i] === '--out') { out.outFile = argv[++i]; }
  }
  return out;
}

/** 一个音符：正弦 + 三次谐波，指数衰减 */
function note(freq, seconds, amp, startSample) {
  const n = Math.floor(RATE * seconds);
  const buf = new Float32Array(n);
  for (let i = 0; i < n; i++) {
    const t = i / RATE;
    const env = Math.exp(-4.2 * t) * Math.min(1, i / (RATE * 0.006));
    const s = Math.sin(2 * Math.PI * freq * t) * 0.82
      + Math.sin(2 * Math.PI * freq * 3 * t) * 0.12
      + Math.sin(2 * Math.PI * freq * 2 * t) * 0.06;
    buf[i] = s * env * amp;
  }
  return { buf, startSample };
}

function mix(totalSamples, parts) {
  const out = new Float32Array(totalSamples);
  for (const p of parts) {
    for (let i = 0; i < p.buf.length; i++) {
      const j = p.startSample + i;
      if (j < totalSamples) { out[j] += p.buf[i]; }
    }
  }
  return out;
}

function toWav(samples) {
  const n = samples.length;
  const buffer = Buffer.alloc(44 + n * 2);
  buffer.write('RIFF', 0, 'ascii');
  buffer.writeUInt32LE(36 + n * 2, 4);
  buffer.write('WAVE', 8, 'ascii');
  buffer.write('fmt ', 12, 'ascii');
  buffer.writeUInt32LE(16, 16);
  buffer.writeUInt16LE(1, 20);
  buffer.writeUInt16LE(1, 22);
  buffer.writeUInt32LE(RATE, 24);
  buffer.writeUInt32LE(RATE * 2, 28);
  buffer.writeUInt16LE(2, 32);
  buffer.writeUInt16LE(16, 34);
  buffer.write('data', 36, 'ascii');
  buffer.writeUInt32LE(n * 2, 40);

  let peak = 0;
  for (let i = 0; i < n; i++) { peak = Math.max(peak, Math.abs(samples[i])); }
  const gain = peak > 0 ? 0.63 / peak : 1;

  for (let i = 0; i < n; i++) {
    let v = samples[i] * gain;
    v = Math.max(-1, Math.min(1, v));
    buffer.writeInt16LE(Math.round(v * 32767), 44 + i * 2);
  }
  return buffer;
}

function build(tone) {
  if (tone === 'alert') {
    const parts = [
      note(587.33, 0.22, 0.55, 0),                 // D5
      note(440.00, 0.30, 0.45, Math.floor(RATE * 0.10)), // A4
    ];
    return mix(Math.floor(RATE * 0.62), parts);
  }
  const parts = [
    note(880.00, 0.18, 0.50, 0),                    // A5
    note(1174.66, 0.26, 0.46, Math.floor(RATE * 0.075)), // D6
  ];
  return mix(Math.floor(RATE * 0.48), parts);
}

function main() {
  const args = parseArgs(process.argv.slice(2));
  const outFile = args.outFile || path.join(__dirname, '..', 'assets', 'sounds',
    args.tone === 'alert' ? 'turn-alert.wav' : 'turn-done.wav');

  const wav = toWav(build(args.tone));
  fs.mkdirSync(path.dirname(outFile), { recursive: true });
  fs.writeFileSync(outFile, wav);
  console.log('已生成 ' + outFile + ' (' + Math.round(wav.length / 1024) + ' KB, ' +
    (wav.length / 2 / RATE).toFixed(2) + 's)');
}

main();
