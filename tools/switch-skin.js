#!/usr/bin/env node
/**
 * switch-skin.js — 切换悬浮窗形象（换肤）。
 *
 * 用法：
 *   node tools/switch-skin.js list                 # 列出内置形象
 *   node tools/switch-skin.js <id>                 # 切到指定形象（如 dragon）
 *   node tools/switch-skin.js <url|本地路径>        # 切到自定义图片
 *   node tools/switch-skin.js <id> --force         # 忽略缓存，重新下载
 *   node tools/switch-skin.js <id> --height 240    # 指定帧高度
 *
 * 目录约定（都在项目根的 assets/ 下）：
 *   skins.json         形象清单
 *   skins/<id>/        该形象的序列帧（首次切换时生成，之后复用）
 *   skins/current.txt  当前形象：第一行 id，第二行名称
 *
 * 面板轮询 current.txt，值变化就重新加载帧，所以切完不用重启面板。
 */

'use strict';

const fs = require('fs');
const path = require('path');
const { extractTo } = require('./fetch-sprite');

const ROOT = path.join(__dirname, '..');
const ASSETS = path.join(ROOT, 'assets');
const SKINS_DIR = path.join(ASSETS, 'skins');
const CATALOG = path.join(ASSETS, 'skins.json');
const CURRENT = path.join(SKINS_DIR, 'current.txt');
const STATUS = path.join(SKINS_DIR, 'status.txt');

function readCatalog() {
  try {
    const raw = fs.readFileSync(CATALOG, 'utf8');
    const json = JSON.parse(raw);
    return Array.isArray(json.skins) ? json.skins : [];
  } catch (_) {
    return [];
  }
}

function readCurrent() {
  try {
    const lines = fs.readFileSync(CURRENT, 'utf8').split(/\r?\n/);
    return { id: (lines[0] || '').trim(), name: (lines[1] || '').trim() };
  } catch (_) {
    return { id: '', name: '' };
  }
}

function writeCurrent(id, name) {
  fs.mkdirSync(SKINS_DIR, { recursive: true });
  fs.writeFileSync(CURRENT, id + '\n' + name + '\n', 'utf8');
}

function writeStatus(text) {
  try {
    fs.mkdirSync(SKINS_DIR, { recursive: true });
    if (text) { fs.writeFileSync(STATUS, text, 'utf8'); }
    else if (fs.existsSync(STATUS)) { fs.unlinkSync(STATUS); }
  } catch (_) { }
}

function hasFrames(dir) {
  try {
    if (!fs.existsSync(path.join(dir, 'manifest.json'))) { return false; }
    return fs.readdirSync(dir).some((f) => /\.png$/i.test(f));
  } catch (_) {
    return false;
  }
}

async function main() {
  const argv = process.argv.slice(2);
  const force = argv.includes('--force');
  let height = 220;
  const hi = argv.indexOf('--height');
  if (hi !== -1 && argv[hi + 1]) { height = parseInt(argv[hi + 1], 10) || 220; }

  const positional = [];
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a === '--height') { i++; continue; }
    if (a === '--force') { continue; }
    if (a.startsWith('--')) { continue; }
    positional.push(a);
  }
  const target = positional[0];

  const skins = readCatalog();
  const current = readCurrent();

  if (!target || target === 'list') {
    console.log('可用形象：');
    for (const s of skins) {
      const cached = hasFrames(path.join(SKINS_DIR, s.id));
      const mark = s.id === current.id ? '*' : ' ';
      console.log(`  ${mark} ${s.id.padEnd(9)} ${s.name}${cached ? '  (已缓存)' : ''}`);
    }
    console.log('\n切换：node tools/switch-skin.js <id>');
    return;
  }

  let id, name, source;
  const matched = skins.find((s) => s.id === target || s.name === target);
  if (matched) {
    id = matched.id;
    name = matched.name;
    source = matched.url;
  } else if (/^https?:\/\//i.test(target) || fs.existsSync(target)) {
    id = 'custom';
    name = path.basename(target).replace(/\.[^.]+$/, '') || '自定义';
    source = target;
  } else {
    console.error(`没有找到形象 "${target}"。用 node tools/switch-skin.js list 查看清单。`);
    process.exit(2);
  }

  const dir = path.join(SKINS_DIR, id);

  if (hasFrames(dir) && !force) {
    writeCurrent(id, name);
    console.log(`已切换到 ${name}（${id}，使用本地缓存）`);
    return;
  }

  writeStatus(`正在下载 ${name} ...`);
  console.log(`下载并拆分：${name}`);
  try {
    const r = await extractTo(source, dir, height);
    writeCurrent(id, name);
    writeStatus('');
    console.log(`完成：${r.frames} 帧，${r.w}x${r.h}，一圈 ${r.ms}ms`);
    console.log(`当前形象：${name}（${id}）`);
  } catch (e) {
    writeStatus(`切换失败：${e && e.message ? e.message : e}`);
    console.error('失败：' + (e && e.message ? e.message : e));
    process.exit(1);
  }
}

main();
