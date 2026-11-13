#!/usr/bin/env node
/**
 * switch-skin.js — 切换悬浮窗形象（换肤）。
 *
 * 用法：
 *   node tools/switch-skin.js list                 # 列出内置形象
 *   node tools/switch-skin.js <id>                 # 切到指定形象（如 dragon）
 *   node tools/switch-skin.js <url|本地路径>        # 切到自定义图片
 *   node tools/switch-skin.js <id> --force         # 忽略缓存，重新下载（本地形象会忽略 --force）
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

/**
 * 判断这个形象的资源是否已经就位。
 * 两种形态：
 *   1. 逐帧皮肤：manifest.json + 目录里的 *.png（下载拆帧产物）
 *   2. atlas 皮肤：manifest.json 的 kind=atlas，目录里**不放** png，
 *      而是由 image 指向共用的 assets/atlas/pet-atlas@2x.png（两只角色共用一张）
 */
function hasFrames(dir) {
  try {
    const manifest = path.join(dir, 'manifest.json');
    if (!fs.existsSync(manifest)) { return false; }
    if (fs.readdirSync(dir).some((f) => /\.png$/i.test(f))) { return true; }

    let raw;
    try { raw = fs.readFileSync(manifest, 'utf8'); } catch (_) { return false; }
    const m = raw.match(/"image"\s*:\s*"([^"]+)"/);
    if (!m) { return false; }
    // image 是相对 manifest 所在目录的路径，解析成绝对路径再判断
    return fs.existsSync(path.resolve(dir, m[1]));
  } catch (_) {
    return false;
  }
}

/**
 * 本地形象资源缺失时，给出真正对得上的原因。
 * atlas 皮肤的图集**不在**自己目录里（两只角色共用 assets/atlas/），
 * 一律套用「某个具体形象的路径 + 某个生成脚本」会把人带偏。
 * 返回 console（命令行正文）与 status（写进 status.txt 的一行）两套文案，
 * 因为两者原本就不一样：命令行说清路径，状态栏只留一句结论。
 */
function describeLocalProblem(dir, name) {
  let raw = '';
  try { raw = fs.readFileSync(path.join(dir, 'manifest.json'), 'utf8'); } catch (_) { raw = ''; }

  const kindMatch = raw.match(/"kind"\s*:\s*"([^"]+)"/);
  const imageMatch = raw.match(/"image"\s*:\s*"([^"]+)"/);
  const kind = kindMatch ? kindMatch[1] : '';
  const image = imageMatch ? imageMatch[1] : '';

  if (kind === 'atlas' && image) {
    const abs = path.resolve(dir, image);
    if (!fs.existsSync(abs)) {
      const line = `切换失败：雪碧图缺失 ${abs}（manifest 的 image 字段指向它）`;
      return {
        console: line,
        status: line,
        hint: '请确认 assets/atlas/ 已随包复制，或修正 manifest.json 的 image 字段。'
      };
    }
  }

  return {
    console: `本地形象 "${name}" 的资源目录不存在：${dir}`,
    status: `切换失败：本地形象 ${name} 资源缺失`,
    hint: '请确认该形象的资源目录已随包复制（本地形象见 assets/skins/<id>/，雪碧图形象还需 assets/atlas/）；自定义形象可用 node tools/switch-skin.js <url|路径> 重新生成。'
  };
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

  // 随包入库的本地形象（skins.json 里登记了但没有 url）资源必须随包存在，
  // 不存在「重新下载」这回事，所以 --force 对它们没有意义：忽略强制刷新、直接复用本地缓存。
  // 自定义图片路径（matched 为空）不在此列，仍按原流程下载/拆帧。
  const isLocalSkin = Boolean(matched) && !source;

  const dir = path.join(SKINS_DIR, id);

  if (hasFrames(dir) && (!force || isLocalSkin)) {
    writeCurrent(id, name);
    if (isLocalSkin && force) {
      console.log(`本地形象不使用 --force，已使用本地缓存：${name}（${id}）`);
    } else {
      console.log(`已切换到 ${name}（${id}，使用本地缓存）`);
    }
    return;
  }

  // 随包入库的本地形象（雪碧图）不填 url：资源必须随包存在，否则给出清晰错误而不是拿 undefined 去下载
  if (!source) {
    const problem = describeLocalProblem(dir, name);
    writeStatus(problem.status);
    console.error(problem.console);
    console.error(problem.hint);
    process.exit(1);
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
