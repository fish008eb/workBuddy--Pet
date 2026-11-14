#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
rebuild-voice-index.py — 按素材目录实际情况重建 voice-index.txt

为什么单独写一个
  make-voice.py 的 write_index 只认它自己生成的那批（edge-tts）。
  eng 现在是用 MOSS 原声模型（tools/moss-voice/synth.py）合成的，
  文件名相同但来源不同，两边各写一份索引必然漂移。
  所以索引统一由这里扫目录生成：文件在就登记，不在就不登记，永远和磁盘一致。

用法（用 .venv-sound，因为它需要 import make-voice.py 拿文案）
  python tools/rebuild-voice-index.py
"""

import glob
import importlib.util
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
VOICE_DIR = os.path.join(ROOT, 'assets', 'sounds', 'voice')

# 人格顺序决定索引里的排列，也决定菜单顺序
PERSONA_ORDER = ['eng', 'cute', 'snark']
FUNC_NODES = ['start', 'done', 'alert', 'idle']


def load_lines():
    """从 make-voice.py 取「文件名 → 文案」，没取到就退化成空文案。"""
    path = os.path.join(HERE, 'make-voice.py')
    spec = importlib.util.spec_from_file_location('mv', path)
    mv = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mv)
    return {name: text for name, _persona, text in mv.LINES}


def sort_key(name):
    persona, _, node = name.partition('_')
    p = PERSONA_ORDER.index(persona) if persona in PERSONA_ORDER else 99
    # 功能节点排在事件节点前面，菜单里更好找
    f = FUNC_NODES.index(node) if node in FUNC_NODES else 90
    return (p, f, node, name)


def main():
    if not os.path.isdir(VOICE_DIR):
        sys.exit(f'目录不存在：{VOICE_DIR}')

    texts = load_lines()
    files = sorted(
        (os.path.splitext(os.path.basename(p))[0] for p in glob.glob(os.path.join(VOICE_DIR, '*.wav'))),
        key=sort_key,
    )

    rows = []
    for stem in files:
        persona, _, node = stem.partition('_')
        if not node:
            continue
        # 事件节点没有序号，功能节点带序号，但索引里统一按节点归组
        node_norm = node.rsplit('_', 1)[0] if node.rsplit('_', 1)[-1].isdigit() else node
        rows.append((stem + '.wav', persona, node_norm, texts.get(stem, '')))

    header = """# voice-index.txt — 状态语音清单（由 tools/rebuild-voice-index.py 生成，别手改）
#
# 格式：文件名|人格|节点|文案
# 节点：start 开工 / done 完成 / alert 告警 / idle 空闲，
#       其余为事件节点（budgetover、ctxhigh、sitlong……），名字就是 personas.txt 里的事件 key。
# 面板按「人格 + 节点」找文件，同一节点有多个变体时随机选一条。
# eng 人格的音频来自 MOSS 原声模型（tools/moss-voice/synth.py），
# cute / snark 来自 edge-tts（tools/make-voice.py）。
"""
    out = os.path.join(VOICE_DIR, 'voice-index.txt')
    with open(out, 'w', encoding='utf-8', newline='\n') as f:
        f.write(header)
        for row in rows:
            f.write('|'.join(row) + '\n')

    print(f'索引已重建：{len(rows)} 条 → {out}')
    by_persona = {}
    for r in rows:
        by_persona[r[1]] = by_persona.get(r[1], 0) + 1
    for k, v in by_persona.items():
        print(f'  {k:<6} {v} 条')


if __name__ == '__main__':
    main()
