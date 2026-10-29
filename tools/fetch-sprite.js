#!/usr/bin/env node
/**
 * fetch-sprite.js — 下载/读取 APNG 形象图，拆成序列帧供悬浮窗播放。
 *
 * 用法：
 *   node tools/fetch-sprite.js                     # 使用默认官方形象
 *   node tools/fetch-sprite.js <url>               # 指定网络图片
 *   node tools/fetch-sprite.js <本地路径>           # 指定本地 APNG/PNG
 *   node tools/fetch-sprite.js <源> --out <目录>    # 指定输出目录
 *   node tools/fetch-sprite.js <源> --height 220   # 指定输出帧高度（默认 220）
 *
 * 输出：
 *   <out>/f00_<w>x<h>.png ...   逐帧图片（透明背景）
 *   <out>/manifest.json         帧尺寸 + 每帧延时
 *   <out>/delays.txt            纯文本延时表（C# 版直接读这个）
 *
 * 说明：
 *   APNG 的帧只存变化区域，需要按 blend_op / dispose_op 合成到整张画布，
 *   再统一裁剪到所有帧的并集包围盒，否则各帧位置会错位。
 *   本脚本零第三方依赖（Node 内置 zlib 即可）。
 */

'use strict';

const fs = require('fs');
const path = require('path');
const zlib = require('zlib');
const https = require('https');
const http = require('http');

const DEFAULT_SOURCE = 'https://download.codebuddy.cn/growth-plan/sprite_dragon_shift28.png';
const PNG_SIG = Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);

// ---------- args ----------
function parseArgs(argv) {
  const opts = { source: DEFAULT_SOURCE, out: path.join(__dirname, '..', 'assets', 'frames'), height: 220 };
  const rest = [];
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a === '--out') { opts.out = argv[++i]; }
    else if (a === '--height') { opts.height = parseInt(argv[++i], 10) || 220; }
    else if (a === '-h' || a === '--help') { opts.help = true; }
    else { rest.push(a); }
  }
  if (rest.length) { opts.source = rest[0]; }
  return opts;
}

// ---------- fetch ----------
function fetchBuffer(url, redirects = 0) {
  return new Promise((resolve, reject) => {
    if (redirects > 5) { return reject(new Error('重定向次数过多')); }
    const mod = url.startsWith('https:') ? https : http;
    const req = mod.get(url, { headers: { 'User-Agent': 'wb-usage-pet/1.0' } }, (res) => {
      if (res.statusCode >= 300 && res.statusCode < 400 && res.headers.location) {
        res.resume();
        const next = new URL(res.headers.location, url).toString();
        return resolve(fetchBuffer(next, redirects + 1));
      }
      if (res.statusCode !== 200) {
        res.resume();
        return reject(new Error('HTTP ' + res.statusCode));
      }
      const bufs = [];
      res.on('data', (c) => bufs.push(c));
      res.on('end', () => resolve(Buffer.concat(bufs)));
    });
    req.on('error', reject);
    req.setTimeout(30000, () => { req.destroy(new Error('请求超时')); });
  });
}

// ---------- png ----------
function crc32(buf) {
  let c, crc = 0xffffffff;
  for (let n = 0; n < buf.length; n++) {
    c = (crc ^ buf[n]) & 0xff;
    for (let k = 0; k < 8; k++) { c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1; }
    crc = (crc >>> 8) ^ c;
  }
  return (crc ^ 0xffffffff) >>> 0;
}

function chunk(type, data) {
  const len = Buffer.alloc(4);
  len.writeUInt32BE(data.length);
  const t = Buffer.from(type, 'ascii');
  const crc = Buffer.alloc(4);
  crc.writeUInt32BE(crc32(Buffer.concat([t, data])));
  return Buffer.concat([len, t, data, crc]);
}

function decodePng(buf) {
  const w = buf.readUInt32BE(16);
  const h = buf.readUInt32BE(20);
  const bitDepth = buf[24];
  const colorType = buf[25];
  if (bitDepth !== 8) { throw new Error('仅支持 8bit 位深，当前 ' + bitDepth); }
  const ch = colorType === 6 ? 4 : colorType === 2 ? 3 : colorType === 4 ? 2 : 1;

  let off = 8;
  const idat = [];
  while (off < buf.length) {
    const len = buf.readUInt32BE(off);
    const type = buf.toString('ascii', off + 4, off + 8);
    if (type === 'IDAT') { idat.push(buf.slice(off + 8, off + 8 + len)); }
    if (type === 'IEND') { break; }
    off += 12 + len;
  }

  const raw = zlib.inflateSync(Buffer.concat(idat));
  const stride = w * ch;
  const out = Buffer.alloc(h * stride);
  let pos = 0;
  for (let y = 0; y < h; y++) {
    const filter = raw[pos++];
    const line = raw.slice(pos, pos + stride);
    pos += stride;
    const cur = out.slice(y * stride, (y + 1) * stride);
    const prev = y > 0 ? out.slice((y - 1) * stride, y * stride) : Buffer.alloc(stride);
    for (let x = 0; x < stride; x++) {
      const a = x >= ch ? cur[x - ch] : 0;
      const b = prev[x];
      const c = x >= ch ? prev[x - ch] : 0;
      let v = line[x];
      if (filter === 1) { v += a; }
      else if (filter === 2) { v += b; }
      else if (filter === 3) { v += Math.floor((a + b) / 2); }
      else if (filter === 4) {
        const p = a + b - c;
        const pa = Math.abs(p - a);
        const pb = Math.abs(p - b);
        const pc = Math.abs(p - c);
        v += (pa <= pb && pa <= pc) ? a : (pb <= pc ? b : c);
      }
      cur[x] = v & 0xff;
    }
  }
  return { w, h, ch, data: out };
}

function encodePng(w, h, rgba) {
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(w, 0);
  ihdr.writeUInt32BE(h, 4);
  ihdr[8] = 8;
  ihdr[9] = 6;
  const stride = w * 4;
  const rows = [];
  for (let y = 0; y < h; y++) {
    rows.push(Buffer.from([0]));
    rows.push(rgba.slice(y * stride, (y + 1) * stride));
  }
  return Buffer.concat([
    PNG_SIG,
    chunk('IHDR', ihdr),
    chunk('IDAT', zlib.deflateSync(Buffer.concat(rows), { level: 9 })),
    chunk('IEND', Buffer.alloc(0)),
  ]);
}

// ---------- apng ----------
function isApng(buf) {
  return buf.indexOf(Buffer.from('acTL', 'ascii')) !== -1;
}

function splitApng(buf) {
  let off = 8;
  const chunks = [];
  while (off < buf.length) {
    const len = buf.readUInt32BE(off);
    const type = buf.toString('ascii', off + 4, off + 8);
    chunks.push({ type, data: buf.slice(off + 8, off + 8 + len) });
    if (type === 'IEND') { break; }
    off += 12 + len;
  }

  const ihdr = chunks[0].data;
  const W = ihdr.readUInt32BE(0);
  const H = ihdr.readUInt32BE(4);

  const frames = [];
  let cur = null;
  for (const c of chunks) {
    if (c.type === 'fcTL') {
      cur = {
        w: c.data.readUInt32BE(4),
        h: c.data.readUInt32BE(8),
        x: c.data.readUInt32BE(12),
        y: c.data.readUInt32BE(16),
        delayNum: c.data.readUInt16BE(20),
        delayDen: c.data.readUInt16BE(22),
        dispose: c.data[24],
        blend: c.data[25],
        parts: [],
      };
      frames.push(cur);
    } else if (c.type === 'IDAT') {
      if (cur) { cur.parts.push(c.data); }
    } else if (c.type === 'fdAT') {
      if (cur) { cur.parts.push(c.data.slice(4)); }
    }
  }
  return { W, H, frames };
}

function composite(W, H, frames) {
  const canvas = Buffer.alloc(W * H * 4);
  const out = [];

  const clearRect = (r) => {
    for (let y = r.y; y < r.y + r.h; y++) {
      for (let x = r.x; x < r.x + r.w; x++) {
        const o = (y * W + x) * 4;
        canvas[o] = canvas[o + 1] = canvas[o + 2] = canvas[o + 3] = 0;
      }
    }
  };

  for (const f of frames) {
    const ih = Buffer.alloc(13);
    ih.writeUInt32BE(f.w, 0);
    ih.writeUInt32BE(f.h, 4);
    ih[8] = 8;
    ih[9] = 6;
    const png = Buffer.concat([
      PNG_SIG,
      chunk('IHDR', ih),
      chunk('IDAT', Buffer.concat(f.parts)),
      chunk('IEND', Buffer.alloc(0)),
    ]);
    const img = decodePng(png);
    const backup = f.dispose === 2 ? Buffer.from(canvas) : null;

    for (let y = 0; y < f.h; y++) {
      for (let x = 0; x < f.w; x++) {
        const si = (y * f.w + x) * 4;
        const di = ((f.y + y) * W + (f.x + x)) * 4;
        const sa = img.data[si + 3] / 255;
        if (f.blend === 0) {
          canvas[di] = img.data[si];
          canvas[di + 1] = img.data[si + 1];
          canvas[di + 2] = img.data[si + 2];
          canvas[di + 3] = img.data[si + 3];
        } else {
          if (sa === 0) { continue; }
          const da = canvas[di + 3] / 255;
          const oa = sa + da * (1 - sa);
          if (oa === 0) {
            canvas[di] = canvas[di + 1] = canvas[di + 2] = canvas[di + 3] = 0;
            continue;
          }
          for (let k = 0; k < 3; k++) {
            canvas[di + k] = Math.round((img.data[si + k] * sa + canvas[di + k] * da * (1 - sa)) / oa);
          }
          canvas[di + 3] = Math.round(oa * 255);
        }
      }
    }

    const delay = (f.delayNum || 1) / (f.delayDen || 100);
    out.push({ buf: Buffer.from(canvas), delay });

    if (f.dispose === 1) { clearRect({ x: f.x, y: f.y, w: f.w, h: f.h }); }
    else if (f.dispose === 2 && backup) { backup.copy(canvas); }
  }

  return out;
}

function unionBox(frames, W, H, step = 2) {
  let minX = W, minY = H, maxX = 0, maxY = 0;
  for (const f of frames) {
    for (let y = 0; y < H; y += step) {
      for (let x = 0; x < W; x += step) {
        if (f.buf[((y * W + x) * 4) + 3] > 20) {
          if (x < minX) { minX = x; }
          if (x > maxX) { maxX = x; }
          if (y < minY) { minY = y; }
          if (y > maxY) { maxY = y; }
        }
      }
    }
  }
  return { minX, minY, maxX, maxY };
}

// ---------- main ----------
async function main() {
  const opts = parseArgs(process.argv.slice(2));
  if (opts.help) {
    console.log(fs.readFileSync(__filename, 'utf8').split('*/')[0]);
    return;
  }

  const isUrl = /^https?:\/\//i.test(opts.source);
  console.log('源：' + (isUrl ? opts.source : path.resolve(opts.source)));

  let buf;
  if (isUrl) {
    buf = await fetchBuffer(opts.source);
  } else {
    buf = fs.readFileSync(opts.source);
  }
  console.log('大小：' + (buf.length / 1024).toFixed(0) + ' KB');

  if (buf.slice(0, 8).toString('hex') !== PNG_SIG.toString('hex')) {
    throw new Error('不是 PNG 文件（可能是 JPEG/WebP）');
  }

  let frames = [];
  let W = buf.readUInt32BE(16);
  let H = buf.readUInt32BE(20);

  if (isApng(buf)) {
    const parsed = splitApng(buf);
    W = parsed.W;
    H = parsed.H;
    console.log('APNG：' + parsed.frames.length + ' 帧，画布 ' + W + 'x' + H);
    frames = composite(W, H, parsed.frames);
  } else {
    console.log('静态 PNG，按单帧处理');
    const img = decodePng(buf);
    const rgba = Buffer.alloc(img.w * img.h * 4);
    for (let i = 0, j = 0; i < img.data.length; i += img.ch, j += 4) {
      rgba[j] = img.data[i];
      rgba[j + 1] = img.data[i + 1] !== undefined ? img.data[i + 1] : img.data[i];
      rgba[j + 2] = img.data[i + 2] !== undefined ? img.data[i + 2] : img.data[i];
      rgba[j + 3] = img.ch === 4 ? img.data[i + 3] : 255;
    }
    frames = [{ buf: rgba, delay: 0.1 }];
    W = img.w;
    H = img.h;
  }

  const box = unionBox(frames, W, H);
  const pad = 2;
  const minX = Math.max(0, box.minX - pad);
  const minY = Math.max(0, box.minY - pad);
  const maxX = Math.min(W - 1, box.maxX + pad);
  const maxY = Math.min(H - 1, box.maxY + pad);
  const cw = maxX - minX + 1;
  const chh = maxY - minY + 1;
  console.log('内容包围盒：' + cw + 'x' + chh);

  const scale = Math.min(1, opts.height / chh);
  const tw = Math.max(1, Math.round(cw * scale));
  const th = Math.max(1, Math.round(chh * scale));

  fs.mkdirSync(opts.out, { recursive: true });
  for (const f of fs.readdirSync(opts.out)) {
    if (/\.png$/i.test(f)) { fs.unlinkSync(path.join(opts.out, f)); }
  }

  const manifest = [];
  frames.forEach((frame, i) => {
    const out = Buffer.alloc(tw * th * 4);
    for (let y = 0; y < th; y++) {
      const sy0 = Math.floor(y / scale) + minY;
      const sy1 = Math.min(H, Math.floor((y + 1) / scale) + minY);
      for (let x = 0; x < tw; x++) {
        const sx0 = Math.floor(x / scale) + minX;
        const sx1 = Math.min(W, Math.floor((x + 1) / scale) + minX);
        let r = 0, g = 0, b = 0, a = 0, n = 0;
        for (let sy = sy0; sy < Math.max(sy1, sy0 + 1); sy++) {
          for (let sx = sx0; sx < Math.max(sx1, sx0 + 1); sx++) {
            const o = (sy * W + sx) * 4;
            const al = frame.buf[o + 3] / 255;
            r += frame.buf[o] * al;
            g += frame.buf[o + 1] * al;
            b += frame.buf[o + 2] * al;
            a += al;
            n++;
          }
        }
        const o = (y * tw + x) * 4;
        if (a > 0) {
          out[o] = Math.round(r / a);
          out[o + 1] = Math.round(g / a);
          out[o + 2] = Math.round(b / a);
          out[o + 3] = Math.round((a / n) * 255);
        }
      }
    }
    const name = 'f' + String(i).padStart(2, '0') + '_' + tw + 'x' + th + '.png';
    fs.writeFileSync(path.join(opts.out, name), encodePng(tw, th, out));
    manifest.push({ file: name, delay: Math.round(frame.delay * 1000) });
  });

  fs.writeFileSync(
    path.join(opts.out, 'manifest.json'),
    JSON.stringify({ w: tw, h: th, frames: manifest }, null, 2),
    'utf8'
  );
  fs.writeFileSync(
    path.join(opts.out, 'delays.txt'),
    manifest.map((m) => m.delay).join('\n') + '\n',
    'utf8'
  );

  const total = manifest.reduce((s, m) => s + m.delay, 0);
  console.log('完成：' + manifest.length + ' 帧，' + tw + 'x' + th + '，一圈 ' + total + 'ms');
  console.log('输出目录：' + path.resolve(opts.out));
}

main().catch((e) => {
  console.error('失败：' + (e && e.message ? e.message : e));
  process.exit(1);
});
