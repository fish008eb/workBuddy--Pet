#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
build-hoops-atlas.py —— 把 Petdex 的 Codex pet 雪碧图重打成项目自己的方形格图集。

源图是 8 列 × 9 行、单格 192×208（非正方形），而 PetAtlas 的裁格逻辑按正方形 cell
计算（x = column*cell, y = row*cell, w = h = cell），所以必须重排一次：
  每帧先裁 192×208，左右各补 4~8px 撑成正方形，再整体放大到 cell=416。

行序原样保留（index = row*8 + column），manifest 里的 seq 直接按这个序号写，
不用再翻一层映射表。

用法
    python tools/build-hoops-atlas.py                    # 从 Petdex 下载
    python tools/build-hoops-atlas.py --from x.webp      # 用本地已下载的源图
    python tools/build-hoops-atlas.py --scale 1          # 不放大（省体积）

输出
    assets/atlas/hoops-atlas@2x.png / .json
"""

from __future__ import print_function

import argparse
import json
import os
import sys

import numpy as np
from PIL import Image, ImageDraw

URL = "https://assets.petdex.dev/pets/hoops-206e70d7dbd5/sprite.webp"
COLS, ROWS = 8, 9
SRC_CELL_W, SRC_CELL_H = 192, 208
SCALE = 2                      # 2x：显示 200 DIP、150% 缩放下仍然清晰
OUT_NAME = "hoops-atlas@2x.png"

# 行 → 动作（顺序与源图一致，也是 index 的计算依据）
ROWS_MAP = [
    ("idle", "待机 · 呼吸眨眼", 6),
    ("runRight", "向右跑", 8),
    ("runLeft", "向左跑", 8),
    ("waving", "挥手 · 打招呼", 4),
    ("jumping", "跳跃", 5),
    ("failed", "失败 · 流泪", 8),
    ("waiting", "等待 · 耐心待机", 6),
    ("running", "原地跑", 6),
    ("review", "端详 · 思考", 6),
]

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
ATLAS_DIR = os.path.join(ROOT, "assets", "atlas")


def load_source(local):
    if local:
        return Image.open(local)
    import urllib.request
    req = urllib.request.Request(URL, headers={
        "User-Agent": "Mozilla/5.0",
        "Referer": "https://petdex.dev/pets/hoops",
    })
    data = urllib.request.urlopen(req, timeout=60).read()
    tmp = os.path.join(ATLAS_DIR, "_hoops-src.webp")
    with open(tmp, "wb") as fh:
        fh.write(data)
    return Image.open(tmp)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--from", dest="src", default="")
    ap.add_argument("--scale", type=int, default=SCALE)
    args = ap.parse_args()

    im = load_source(args.src).convert("RGBA")
    if im.size != (COLS * SRC_CELL_W, ROWS * SRC_CELL_H):
        raise SystemExit("源图尺寸不符：%s，期望 %dx%d" % (
            im.size, COLS * SRC_CELL_W, ROWS * SRC_CELL_H))

    cell = SRC_CELL_H * args.scale                 # 正方形边长，等比放大
    pad_x = (SRC_CELL_H - SRC_CELL_W) // 2         # 192 → 208，左右各补 8
    sheet = Image.new("RGBA", (COLS * cell, ROWS * cell), (0, 0, 0, 0))

    a = np.array(im)
    frames = []
    for r, (key, label, count) in enumerate(ROWS_MAP):
        filled = 0
        for c in range(COLS):
            alpha = a[r * SRC_CELL_H:(r + 1) * SRC_CELL_H, c * SRC_CELL_W:(c + 1) * SRC_CELL_W, 3]
            if int((alpha > 8).sum()) <= 50:
                continue
            filled += 1
            frame = im.crop((c * SRC_CELL_W, r * SRC_CELL_H,
                             (c + 1) * SRC_CELL_W, (r + 1) * SRC_CELL_H))
            square = Image.new("RGBA", (SRC_CELL_H, SRC_CELL_H), (0, 0, 0, 0))
            square.paste(frame, (pad_x, 0))
            if args.scale != 1:
                square = square.resize((cell, cell), Image.LANCZOS)
            sheet.paste(square, (c * cell, r * cell))

            ys, xs = np.where(alpha > 8)
            frames.append({
                "index": r * COLS + c,
                "column": c, "row": r,
                "x": c * cell, "y": r * cell, "w": cell, "h": cell,
                "action": key, "label": label, "stripIndex": c, "source": "petdex:hoops",
                "trim": {
                    "x": c * cell + int(xs.min()) * args.scale,
                    "y": r * cell + int(ys.min()) * args.scale,
                    "w": int(xs.max() - xs.min() + 1) * args.scale,
                    "h": int(ys.max() - ys.min() + 1) * args.scale,
                },
            })
        if filled != count:
            print("  [warn] %s 实际 %d 帧，元数据写的是 %d 帧" % (key, filled, count))
        print("  %-9s %d 帧  行 %d  起始序号 %d" % (key, filled, r, r * COLS))

    os.makedirs(ATLAS_DIR, exist_ok=True)
    png_path = os.path.join(ATLAS_DIR, OUT_NAME)
    sheet.save(png_path, "PNG", optimize=True)

    meta = {
        "image": OUT_NAME,
        "character": "hoops",
        "source": {"url": URL, "creator": "kaidi c.", "page": "https://petdex.dev/pets/hoops",
                   "note": "Petdex 上的用户投稿同人形象，版权归原作者；本项目仅作本地展示。"},
        "frameCount": len(frames),
        "columns": COLS,
        "rows": ROWS,
        "cellWidth": cell,
        "cellHeight": cell,
        "imageWidth": COLS * cell,
        "imageHeight": ROWS * cell,
        "background": "transparent",
        "anchor": "bottomCenter",
        "note": "index = row * 8 + column，行内空位不占内容；seq 用这里的绝对序号。",
        "rows": [{"row": r, "action": k, "label": l, "frames": n} for r, (k, l, n) in enumerate(ROWS_MAP)],
        "frames": frames,
    }
    json_path = os.path.join(ATLAS_DIR, "hoops-atlas@2x.json")
    with open(json_path, "w", encoding="utf-8") as fh:
        json.dump(meta, fh, ensure_ascii=False, indent=2)

    # 编号预览：改 seq 时对着看
    prev = Image.new("RGBA", sheet.size, (27, 30, 38, 255))
    prev.alpha_composite(sheet)
    d = ImageDraw.Draw(prev)
    for r in range(ROWS):
        for c in range(COLS):
            ox, oy = c * cell, r * cell
            d.rectangle([ox, oy, ox + cell - 1, oy + cell - 1], outline=(255, 0, 120, 120))
            d.text((ox + 6, oy + 4), "#%d" % (r * COLS + c), fill=(255, 60, 120, 255))
    prev_path = os.path.join(ATLAS_DIR, "hoops-preview.png")
    prev.convert("RGB").save(prev_path)

    print("图集：%s（%d 帧，%dx%d，%.2f MB）" % (
        png_path, len(frames), COLS * cell, ROWS * cell,
        os.path.getsize(png_path) / 1048576.0))
    print("元数据：%s" % json_path)
    print("预览：%s" % prev_path)


if __name__ == "__main__":
    main()
