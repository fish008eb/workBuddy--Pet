#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
build-naigai-atlas.py —— 把「猫猫」素材目录里的青色底长条，抠图、切帧、对齐，
打成一张 6x6 的雪碧图，并输出与图集一一对应的 JSON 元数据。

为什么要有这个脚本
    新形象是一套照片级英短素材，原始文件是青底（#00FBFD 附近）的整条动作序列，
    每格的猫大小、站立基线都不一样。手动切图无法保证「同一只猫」的比例与落点，
    换帧时会左右乱跳。脚本把三件事固定下来：
      1. 青底 → 透明（带边缘羽化与青色溢色压制，避免白毛边上挂一圈绿边）；
      2. 长条按等分 + 谷值校正切帧（猫与猫之间几乎贴在一起，纯连通域会粘成一片）；
      3. 同一条内部保留原始相对位移（跳跃/扑蝴蝶的腾空才不会塌回地面），
         跨条按「最高一帧的高度」统一缩放，保证六组动作是同一只猫。

用法
    python tools/build-naigai-atlas.py                  # 用默认素材目录
    python tools/build-naigai-atlas.py <素材目录>
    python tools/build-naigai-atlas.py --preview        # 额外输出编号预览图

输出
    assets/atlas/naigai-atlas@2x.png    雪碧图（6 列 x 6 行，单格 320）
    assets/atlas/naigai-atlas@2x.json   每格的绝对序号 / 裁切矩形 / 源文件
依赖：Pillow、numpy（仅出图时需要，运行时零依赖）
"""

from __future__ import print_function

import json
import os
import sys

import numpy as np
from PIL import Image, ImageDraw

# ---------- 布局常量 ----------
CELL = 320                 # 单格边长（显示尺寸 196 时约 1.6x，照片级细节够用）
COLUMNS = 6
TARGET_H = 286             # 角色在单格里的目标高度：留 34px 给落点与呼吸位移
BASELINE_PAD = 16          # 脚底距格子底边
SIDE_PAD = 8               # 左右最小留白，超出就把整体缩放再压一点

# 素材文件 → (帧数, 动作键, 中文名)。顺序即图集顺序，改这里要同步 manifest.json 的 seq
SOURCES = [
    ("主体形象.png", 1, "sit", "主体形象 · 端坐"),
    ("看周围.png", 4, "look4", "看周围 · 小幅张望"),
    ("看周围1.png", 8, "lookUp", "抬头 · 低头到抬头"),
    ("看周围2.png", 8, "lookDown", "低头 · 抬头到低头"),
    ("吃饭.png", 6, "eat", "吃饭 · 埋头吃粮"),
    ("扑蝴蝶.png", 6, "play", "扑蝴蝶 · 起身扑击"),
]

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
ATLAS_DIR = os.path.join(ROOT, "assets", "atlas")
DEFAULT_SRC = os.path.join(os.path.dirname(ROOT), "猫猫")


def estimate_bg(a):
    """取四条边的中位数当青色底：素材是纯色背景，中位数比取角落更稳。"""
    h, w, _ = a.shape
    edge = np.concatenate([
        a[0:2, :, :].reshape(-1, 3), a[h - 2:h, :, :].reshape(-1, 3),
        a[:, 0:2, :].reshape(-1, 3), a[:, w - 2:w, :].reshape(-1, 3),
    ])
    return np.median(edge, axis=0)


def key_out(a, bg):
    """青色底 → RGBA。alpha 用「与底色距离」做软阈值，再压掉边缘的青色溢色。"""
    f = a.astype(np.float32)
    r, g, b = f[:, :, 0], f[:, :, 1], f[:, :, 2]
    d = np.sqrt((r - bg[0]) ** 2 + (g - bg[1]) ** 2 + (b - bg[2]) ** 2)

    t0, t1 = 46.0, 104.0          # 距离 <= t0 全透明，>= t1 全不透明，中间线性羽化
    alpha = np.clip((d - t0) / (t1 - t0), 0.0, 1.0)

    # 溢色：白毛边缘被青底染成 (120,250,250) 这类颜色，按「绿蓝超出红多少」线性回拉
    excess = (g + b) * 0.5 - r
    over = np.clip((excess - 10.0) / 70.0, 0.0, 1.0) * alpha
    g = g - excess * over
    b = b - excess * over

    out = np.dstack([np.clip(r, 0, 255), np.clip(g, 0, 255), np.clip(b, 0, 255), alpha * 255.0])
    return out.astype(np.uint8)


def split_bounds(alpha, count):
    """等分给出预期边界，再在预期位置附近找「前景最少的列」当真实切缝。

    素材里猫几乎是肩并肩贴着的，纯连通域分割会把两只粘成一个，所以必须有预期值兜底。
    """
    h, w = alpha.shape
    occ = (alpha > 24).sum(axis=0).astype(np.float32)
    span = max(1.0, w / float(count))
    bounds = [0]
    for i in range(1, count):
        expect = i * span
        lo = max(1, int(expect - span * 0.22))
        hi = min(w - 2, int(expect + span * 0.22))
        if hi <= lo:
            bounds.append(int(round(expect)))
            continue
        window = occ[lo:hi + 1]
        # 取谷值；同一谷底有多列时取最靠近预期的一列，避免切进角色身体
        best = float(window.min())
        cands = np.where(window <= best + 0.5)[0] + lo
        bounds.append(int(cands[np.argmin(np.abs(cands - expect))]))
    bounds.append(w)
    return bounds


def content_boxes(rgba, count):
    """按切缝取每帧的不透明包围盒（alpha>12 才算内容，压掉羽化边噪点）。"""
    alpha = rgba[:, :, 3]
    bounds = split_bounds(alpha, count)
    boxes = []
    for i in range(count):
        x0, x1 = bounds[i], bounds[i + 1]
        sub = alpha[:, x0:x1]
        ys, xs = np.where(sub > 12)
        if len(xs) == 0:
            boxes.append(None)
            continue
        boxes.append((x0 + int(xs.min()), int(ys.min()),
                      x0 + int(xs.max()) + 1, int(ys.max()) + 1))
    return boxes


def build(src_dir, want_preview):
    frames = []          # 每项：dict(key, name, source, image(RGBA), bbox, strip, idx)
    strip_meta = []

    for fname, count, key, label in SOURCES:
        path = os.path.join(src_dir, fname)
        if not os.path.exists(path):
            raise SystemExit("缺少素材：%s" % path)

        im = Image.open(path).convert("RGB")
        arr = np.array(im)
        bg = estimate_bg(arr)
        rgba_arr = key_out(arr, bg)
        rgba = Image.fromarray(rgba_arr, "RGBA")

        boxes = content_boxes(rgba_arr, count)
        if any(b is None for b in boxes):
            raise SystemExit("%s 切帧失败：有帧是空的，检查切缝" % fname)

        heights = [b[3] - b[1] for b in boxes]
        widths = [b[2] - b[0] for b in boxes]
        max_h = float(max(heights))
        scale = TARGET_H / max_h
        # 最宽的一帧也不能顶到格子边缘：真顶到了就整体再缩
        if max(widths) * scale > CELL - SIDE_PAD * 2:
            scale = min(scale, (CELL - SIDE_PAD * 2) / float(max(widths)))

        centers = [(b[0] + b[2]) / 2.0 for b in boxes]
        strip_cx = float(np.median(centers))
        strip_bottom = float(max(b[3] for b in boxes))

        strip_meta.append({
            "file": fname, "key": key, "label": label, "count": count,
            "bg": [int(v) for v in bg], "maxH": int(max_h),
            "scale": round(scale, 4),
        })

        for i, b in enumerate(boxes):
            crop = rgba.crop(b)
            frames.append({
                "key": key, "label": label, "source": fname, "stripIndex": i,
                "image": crop, "box": b, "scale": scale,
                "stripCx": strip_cx, "stripBottom": strip_bottom,
            })

    total = len(frames)
    rows = (total + COLUMNS - 1) // COLUMNS
    sheet = Image.new("RGBA", (COLUMNS * CELL, rows * CELL), (0, 0, 0, 0))

    meta_frames = []
    for idx, fr in enumerate(frames):
        crop = fr["image"]
        s = fr["scale"]
        w = max(1, int(round(crop.width * s)))
        h = max(1, int(round(crop.height * s)))
        scaled = crop.resize((w, h), Image.LANCZOS)

        # 横向一律居中对齐：长条里每只猫的左右位置只是排版，不是动作位移，
        # 沿用条内相对位置会让整帧被推出格子。
        x = int(round((CELL - w) / 2.0))

        # 纵向保留条内相对高度：扑蝴蝶的腾空帧必须比落地帧高，否则跳跃会塌掉。
        # 但不能高到把头顶切掉——超出可用高度就压平落差，姿势本身已经足够表达腾空。
        dy = (fr["stripBottom"] - fr["box"][3]) * s
        dy_max = CELL - BASELINE_PAD - 4 - h
        if dy > dy_max:
            dy = max(0.0, dy_max)
        y = int(round(CELL - BASELINE_PAD - dy - h))
        y = max(0, min(CELL - h, y))

        col = idx % COLUMNS
        row = idx // COLUMNS
        sheet.paste(scaled, (col * CELL + x, row * CELL + y), scaled)

        meta_frames.append({
            "index": idx, "column": col, "row": row,
            "x": col * CELL, "y": row * CELL, "w": CELL, "h": CELL,
            "action": fr["key"], "label": fr["label"],
            "stripIndex": fr["stripIndex"], "source": fr["source"],
            "trim": {"x": x, "y": y, "w": w, "h": h},
        })

    os.makedirs(ATLAS_DIR, exist_ok=True)
    png_path = os.path.join(ATLAS_DIR, "naigai-atlas@2x.png")
    sheet.save(png_path, "PNG", optimize=True)

    meta = {
        "image": "naigai-atlas@2x.png",
        "character": "naigai",
        "frameCount": total,
        "columns": COLUMNS,
        "rows": rows,
        "cellWidth": CELL,
        "cellHeight": CELL,
        "imageWidth": COLUMNS * CELL,
        "imageHeight": rows * CELL,
        "background": "transparent",
        "anchor": "bottomCenter",
        "note": "trim 为角色在该格内的落点，用于对齐与命中检测；同一条动作内的相对位移已保留。",
        "strips": strip_meta,
        "frames": meta_frames,
    }
    json_path = os.path.join(ATLAS_DIR, "naigai-atlas@2x.json")
    with open(json_path, "w", encoding="utf-8") as fh:
        json.dump(meta, fh, ensure_ascii=False, indent=2)

    preview_path = None
    if want_preview:
        preview_path = os.path.join(ATLAS_DIR, "naigai-preview.png")
        for name, bgcolor in (("naigai-preview.png", (27, 30, 38, 255)),
                              ("naigai-preview-light.png", (245, 245, 245, 255))):
            base = Image.new("RGBA", sheet.size, bgcolor)
            base.alpha_composite(sheet)
            draw = ImageDraw.Draw(base)
            for idx in range(total):
                col, row = idx % COLUMNS, idx // COLUMNS
                ox, oy = col * CELL, row * CELL
                draw.rectangle([ox, oy, ox + CELL - 1, oy + CELL - 1], outline=(255, 0, 120, 160))
                draw.text((ox + 6, oy + 4), "#%d %s" % (idx, frames[idx]["key"]), fill=(255, 40, 120, 255))
                draw.line([ox, oy + CELL - BASELINE_PAD, ox + CELL, oy + CELL - BASELINE_PAD],
                          fill=(0, 160, 255, 120))
            base.convert("RGB").save(os.path.join(ATLAS_DIR, name))

    print("图集：%s（%d 帧，%dx%d，%.2f MB）" % (
        png_path, total, COLUMNS * CELL, rows * CELL,
        os.path.getsize(png_path) / 1048576.0))
    print("元数据：%s" % json_path)
    if preview_path:
        print("预览：%s" % preview_path)
    for st in strip_meta:
        print("  %-12s %d 帧  最大高 %4d  缩放 %.3f" % (st["file"], st["count"], st["maxH"], st["scale"]))
    return meta


def main():
    argv = [a for a in sys.argv[1:]]
    preview = "--preview" in argv
    argv = [a for a in argv if not a.startswith("--")]
    src = argv[0] if argv else DEFAULT_SRC
    if not os.path.isdir(src):
        raise SystemExit("素材目录不存在：%s" % src)
    build(src, preview)


if __name__ == "__main__":
    main()
