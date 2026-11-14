#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
synth.py — 用 MOSS 声线 VITS 模型合成语音（CPU 可跑）

模型：OpenMOSS/moss-vits-model（900 epochs，从《流浪地球》1&2 的 MOSS 原声微调）
代码：open-moss/moss-tts-service（VITS 推理，本脚本只取模型与文本前端）

与 make-voice.py 的分工
  make-voice.py  edge-tts 合成 + 后期处理，音色是"模拟 MOSS"，泛用
  synth.py       直接跑 MOSS 声线模型，输出就是训练出来的那个音色

注意事项
  * 模型输出采样率是 16000（config.json 里写死的），不是 44100。
  * 文本会先做 "moss" → "mos" 替换，和官方 server.py 保持一致：
    音素表里没有英文字母，不替换会把 MOSS 整个词吞掉。
  * 模型许可证禁止商用，个人/非商业用途没问题。

用法
  python synth.py --text "任务已接收。" --text "执行完毕。"
  python synth.py --manifest lines.json --out out --speed 0.92
"""

import argparse
import json
import os
import re
import sys
import wave

HERE = os.path.dirname(os.path.abspath(__file__))
SVC = os.path.join(HERE, 'moss-tts-service-master')
MODEL_DIR = os.path.join(HERE, 'model')
sys.path.insert(0, SVC)

import numpy as np
import torch
from torch import no_grad, LongTensor

import lib.commons as commons
from lib.models import SynthesizerTrn
from lib.utils import get_hparams_from_dir

# 不能 `from lib.text import ...`：lib/text/__init__.py 会连带 import cleaners.py，
# 而 cleaners.py 在模块级就把 pyopenjtalk（日文前端）拉进来。那个包要编译 C 代码，
# Windows + Python 3.13 基本装不上，而我们要的只是中文那一段。
# 所以按文件路径单独加载 mandarin.py，绕开整个包。
import importlib.util as _ilu

_mandarin_path = os.path.join(SVC, 'lib', 'text', 'mandarin.py')
_spec = _ilu.spec_from_file_location('moss_mandarin', _mandarin_path)
mandarin = _ilu.module_from_spec(_spec)
_spec.loader.exec_module(mandarin)

number_to_chinese = mandarin.number_to_chinese
chinese_to_bopomofo = mandarin.chinese_to_bopomofo
latin_to_bopomofo = mandarin.latin_to_bopomofo


def chinese_cleaners(text: str) -> str:
    """与 lib/text/cleaners.py 里的 chinese_cleaners 完全一致。"""
    text = text.replace('[ZH]', '')
    text = number_to_chinese(text)
    text = chinese_to_bopomofo(text)
    text = latin_to_bopomofo(text)
    text = re.sub(r'([ˉˊˇˋ˙])$', r'\1。', text)
    return text


def text_to_sequence(text: str, symbols) -> list:
    symbol_to_id = {s: i for i, s in enumerate(symbols)}
    cleaned = chinese_cleaners(text)
    return [symbol_to_id[c] for c in cleaned if c in symbol_to_id]


def load_model(model_dir=MODEL_DIR):
    hps = get_hparams_from_dir(model_dir)
    net = SynthesizerTrn(
        len(hps.symbols),
        hps.data.filter_length // 2 + 1,
        hps.train.segment_size // hps.data.hop_length,
        n_speakers=hps.data.n_speakers,
        **hps.model
    )
    net.eval()

    ckpt = torch.load(
        os.path.join(model_dir, 'moss.pth'),
        map_location='cpu',
        weights_only=False,   # torch 2.6+ 默认只认张量，这里存的是完整 checkpoint
    )
    state = ckpt.get('model', ckpt) if isinstance(ckpt, dict) else ckpt
    # 多卡训练出来的权重会带 module. 前缀
    if any(k.startswith('module.') for k in state):
        state = {k[len('module.'):]: v for k, v in state.items()}

    net.load_state_dict(state)
    return net, hps


def prepare_text(text: str) -> str:
    """和官方 server.py 一致：音素表没有英文字母，MOSS 要先降级成 mos。"""
    return re.sub(r'moss', 'mos', text, flags=re.I)


def synth(net, hps, text, speed=1.0, noise_scale=0.667, noise_scale_w=0.8):
    seq = text_to_sequence(prepare_text(text), hps.symbols)
    if hps.data.add_blank:
        seq = commons.intersperse(seq, 0)
    if not seq:
        raise RuntimeError(f'文本清洗后为空：{text!r}')

    x = LongTensor(seq).unsqueeze(0)
    x_len = LongTensor([len(seq)])
    sid = LongTensor([0])   # config 里只有 MOSS 一个说话人

    with no_grad():
        audio = net.infer(
            x, x_len,
            sid=sid,
            noise_scale=noise_scale,
            noise_scale_w=noise_scale_w,
            length_scale=1.0 / speed,
        )[0][0, 0].data.cpu().float().numpy()
    return audio


def resample(x: np.ndarray, src: int, dst: int) -> np.ndarray:
    if src == dst or len(x) == 0:
        return x
    n = int(round(len(x) * dst / src))
    idx = np.linspace(0, len(x) - 1, n, dtype=np.float64)
    return np.interp(idx, np.arange(len(x)), x).astype(np.float32)


def biquad(x, sr, kind, freq, q=0.707, gain_db=0.0):
    """二阶 IIR。kind: highpass / lowpass / lowshelf。"""
    w0 = 2.0 * np.pi * freq / sr
    cw, sw = np.cos(w0), np.sin(w0)
    alpha = sw / (2.0 * q)

    if kind == 'lowshelf':
        A = 10 ** (gain_db / 40.0)
        b0 = A * ((A + 1) - (A - 1) * cw + 2 * np.sqrt(A) * alpha)
        b1 = 2 * A * ((A - 1) - (A + 1) * cw)
        b2 = A * ((A + 1) - (A - 1) * cw - 2 * np.sqrt(A) * alpha)
        a0 = (A + 1) + (A - 1) * cw + 2 * np.sqrt(A) * alpha
        a1 = -2 * ((A - 1) + (A + 1) * cw)
        a2 = (A + 1) + (A - 1) * cw - 2 * np.sqrt(A) * alpha
    elif kind == 'lowpass':
        b0, b1, b2 = (1 - cw) / 2, 1 - cw, (1 - cw) / 2
        a0, a1, a2 = 1 + alpha, -2 * cw, 1 - alpha
    else:
        b0, b1, b2 = (1 + cw) / 2, -(1 + cw), (1 + cw) / 2
        a0, a1, a2 = 1 + alpha, -2 * cw, 1 - alpha

    b0, b1, b2, a1, a2 = b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0
    y = np.zeros_like(x, dtype=np.float64)
    x1 = x2 = y1 = y2 = 0.0
    for i, xn in enumerate(x):
        yn = b0 * xn + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2
        x2, x1 = x1, xn
        y2, y1 = y1, yn
        y[i] = yn
    return y.astype(np.float32)


def film_post(x: np.ndarray, sr: int) -> np.ndarray:
    """
    "电影处理"：给模型干声补上电影混音里的那层体积感。

    实测模型原始输出低频(<300Hz)只占 15%，而电影里的 MOSS 是 26% 左右——
    差的就是胸腔共鸣和舱内反射。走三段：
      80Hz 高通    去掉合成器带出来的超低频轰鸣
      160Hz +5dB   补胸腔，这是"MOSS 听着厚"的主要来源
      7500Hz 低通  16kHz 采样在 8k 处有硬截止，先抹平，避免刺耳
      13/29ms 混响 舱内金属反射，和 make-voice.py 用的是同一组参数
    """
    x = biquad(x, sr, 'highpass', 80)
    x = biquad(x, sr, 'lowshelf', 160, gain_db=5.0)
    x = biquad(x, sr, 'lowpass', 7500)
    out = x.copy()
    for delay_ms, gain in ((13.0, 0.55), (29.0, 0.34)):
        d = int(sr * delay_ms / 1000.0)
        if d < len(x):
            out[d:] += x[:-d] * gain * 0.18
    return out


def write_wav(path, x, sr, rms_db=-18.9):
    """
    按响度归一化，不按峰值。

    两套素材要交替播放，必须响度一致，否则切换时音量会跳。
    统一补到 -18.9dBFS —— 和 make-voice.py 那批 21 条的实测均值对齐。
    补过头会削顶，所以最后走一次软限幅。
    """
    os.makedirs(os.path.dirname(path) or '.', exist_ok=True)

    m = float(np.max(np.abs(x))) or 1.0
    x = x * (0.90 / m)

    r = float(np.sqrt(np.mean(x ** 2)))
    if r > 1e-6:
        x = x * min((10 ** (rms_db / 20.0)) / r, 4.0)

    th = 0.92
    a = np.abs(x)
    if float(np.max(a)) > th:
        over = a > th
        x = x.copy()
        x[over] = np.sign(x[over]) * (th + (1 - th) * np.tanh((a[over] - th) / (1 - th)))

    pcm = np.clip(x * 32767.0, -32768, 32767).astype('<i2')
    with wave.open(path, 'wb') as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(sr)
        w.writeframes(pcm.tobytes())


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--text', action='append', default=[], help='要合成的句子，可重复')
    ap.add_argument('--manifest', help='JSON 文件：[{"name":"eng_x","text":"..."}]')
    ap.add_argument('--out', default='out')
    ap.add_argument('--speed', type=float, default=1.0, help='语速，1.0 为模型原始速度')
    ap.add_argument('--sr', type=int, default=44100, help='输出采样率，0 表示保持模型原始 16000')
    ap.add_argument('--peak', type=float, default=None, help='（已弃用，改为按响度归一化）')
    ap.add_argument('--post', choices=['none', 'film'], default='none',
                    help="none=模型干声；film=补低频+抹高频+舱内混响，更接近电影混音")
    args = ap.parse_args()

    items = []
    if args.manifest:
        with open(args.manifest, encoding='utf-8') as f:
            for i, it in enumerate(json.load(f), 1):
                items.append((it.get('name') or f'line_{i}', it['text']))
    for i, t in enumerate(args.text, 1):
        items.append((f'line_{i}', t))
    if not items:
        sys.exit('没有输入：用 --text 或 --manifest 指定')

    net, hps = load_model()
    src_sr = hps.data.sampling_rate
    print(f'模型采样率 {src_sr}Hz，共 {len(items)} 条')

    for i, (name, text) in enumerate(items, 1):
        try:
            audio = synth(net, hps, text, speed=args.speed)
            sr = src_sr
            if args.sr and args.sr != src_sr:
                audio = resample(audio, src_sr, args.sr)
                sr = args.sr
            if args.post == 'film':
                audio = film_post(audio, sr)
            path = os.path.join(args.out, name + '.wav')
            write_wav(path, audio, sr)
            print(f'[{i}/{len(items)}] {name:<20} {len(audio)/sr:.2f}s  {path}')
        except Exception as e:
            print(f'[{i}/{len(items)}] {name:<20} 失败: {e}')


if __name__ == '__main__':
    main()
