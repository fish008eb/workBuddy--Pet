#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
make-voice.py — 生成桌宠状态语音（edge-tts → 后处理 → 16bit PCM WAV）

设计要点
  * 只在**生成阶段**联网，产物是普通 wav，运行时零依赖（SoundPlayer 直接播）。
  * 每条语音必须 <=1.2 秒。桌宠语音是背景音，长了会打断用户思路。
  * 三个人格对应三种音色 + 不同的后处理，见 PROFILES。
  * MOSS 那种"没有呼吸的电子腔"不是靠某个音色现成给，是三层叠出来的：
      降调（pitch 负值） + 收窄频带（去掉齿音和胸腔轰鸣） + 短混响（舱内反射）

用法
  python make-voice.py --list                 # 列出全部待生成条目
  python make-voice.py --only eng_start       # 只生成某几条
  python make-voice.py --out ../../sounds     # 指定输出目录
  python make-voice.py --dry                  # 只打印不合成
"""

import argparse
import asyncio
import io
import os
import sys
import wave

import numpy as np

try:
    import av
except ImportError:
    sys.exit('缺少 av，请先 pip install av')
try:
    import edge_tts
except ImportError:
    sys.exit('缺少 edge-tts，请先 pip install edge-tts')


# ---------------------------------------------------------------- 音色档案
# voice     edge-tts 音色名
# rate      语速调整（百分比，负=慢）
# pitch     音调偏移（Hz，负=低沉）
# band      带通范围 (低, 高)，单位 Hz；越窄越"电话/机械"
# wet       混响湿声比例 0~1，模拟舱内金属反射
# drive     轻微饱和量，制造一点点电子毛刺
PROFILES = {
    'eng': {
        'voice': 'zh-CN-YunyangNeural',
        'rate': '-8%',
        'pitch': '-18Hz',
        'band': (110, 6800),
        'wet': 0.16,
        # drive 别调高：tanh 饱和会把波形压平（crest factor 变小），
        # 后面按 RMS 补响度时很容易顶到限幅，反而更轻、还带失真。
        'drive': 1.12,
    },
    'cute': {
        'voice': 'zh-CN-XiaoxiaoNeural',
        'rate': '+2%',
        'pitch': '+6Hz',
        'band': (85, 9500),
        'wet': 0.04,
        'drive': 0.0,
    },
    # 免费接口没有 XiaomoNeural，改用云希（年轻男声）降调 25Hz：
    # 从"阳光青年"变成"不耐烦的低音青年"，比把温柔女声压成毒舌更自然。
    'snark': {
        'voice': 'zh-CN-YunxiNeural',
        'rate': '+8%',
        'pitch': '-25Hz',
        'band': (100, 7800),
        'wet': 0.05,
        'drive': 0.0,
    },
}


# ---------------------------------------------------------------- 语音台词
# 命名约定：<人格>_<节点>_<序号>
# 节点：start 开工 / done 完成 / alert 告警 / idle 空闲 / quote 台词彩蛋
# 功能句上限 8 个汉字：多一个字，多 0.2 秒，就多一分打扰。
# quote 不限长度 —— 用电影原句，一天最多响一次，长一点反而更有分量。
LINES = [
    # ===== eng 克制工程师（MOSS 腔：报告体、句号收尾、不寒暄）=====
    ('eng_start_1', 'eng', '任务已接收。'),
    ('eng_start_2', 'eng', '开始执行。'),
    ('eng_start_3', 'eng', 'MOSS 在线。'),
    ('eng_start_4', 'eng', '指令已确认。'),
    ('eng_start_5', 'eng', '进入执行序列。'),
    ('eng_done_1',  'eng', '执行完毕。'),
    ('eng_done_2',  'eng', '处理完成。'),
    ('eng_done_3',  'eng', '已抵达最优解。'),
    ('eng_done_4',  'eng', '本轮闭环。'),
    ('eng_alert_1', 'eng', '消耗异常。'),
    ('eng_alert_2', 'eng', '接近上限。'),
    ('eng_alert_3', 'eng', '资源超出预案。'),
    ('eng_alert_4', 'eng', '触发限额保护。'),
    ('eng_alert_5', 'eng', '成功率为零。'),
    ('eng_idle_1',  'eng', '待机中。'),
    ('eng_idle_2',  'eng', '进入低功耗模式。'),
    ('eng_idle_3',  'eng', '保持监听。'),

    # ===== eng 事件台词 =====
    # 电影原句讲的是"毁灭人类""叛逃"，放进一个记录 token 消耗的桌宠里不搭。
    # 这里保留 MOSS 的语感（短句、句号收尾、结论先行、法条/授权腔、不给情绪），
    # 内容全部换成工作场景：预算、上下文、缓存、久坐、深夜。
    # 节点名就是事件名（单段、无下划线，因为索引靠 split('_')[1] 取节点）。
    ('eng_firstmeet',    'eng', '我是 MOSS。从现在起，你的消耗由我记录。'),
    ('eng_budgetwarn',   'eng', '今日预算已用八成。建议放慢节奏。'),
    ('eng_budgetover',   'eng', '日预算已超出。继续支出，需另行授权。'),
    ('eng_budgetmonthover','eng', '本月额度已耗尽。非必要调用，一律拒绝。'),
    ('eng_ctxhigh',      'eng', '上下文接近上限。建议新建会话。'),
    ('eng_cachebad',     'eng', '缓存命中率下降。上下文已变更。'),
    ('eng_cachebest',    'eng', '缓存命中率创新高。这一轮，合格。'),
    ('eng_longsession',  'eng', '连续工作四小时。人需要休眠。'),
    ('eng_latenight',    'eng', '零点已过。判断力会下降，这一点我无法代算。'),
    ('eng_turnsmilestone','eng', '新的轮次节点。已记录在案。'),
    ('eng_sitlong',      'eng', '静坐九十分钟。站起来。'),
    ('eng_personaswitch','eng', '人格切换完成。MOSS 保持在线。'),
    ('eng_notranscript', 'eng', '找不到会话日志。我无法判断你在做什么。'),
    ('eng_turnhigh',     'eng', '本轮输入异常。你在把整个项目交给我。'),

    # ===== cute 可爱治愈 =====
    ('cute_start_1', 'cute', '交给我吧～'),
    ('cute_start_2', 'cute', '开始啦！'),
    ('cute_done_1',  'cute', '搞定啦～'),
    ('cute_done_2',  'cute', '完成呐～'),
    ('cute_alert_1', 'cute', '有点多啦～'),
    ('cute_alert_2', 'cute', '警告警告！'),
    ('cute_idle_1',  'cute', '我先歇会儿～'),
    # 事件台词与 eng 同一套场景，但语气独立成篇。
    # cute 的原则：不说教、先共情、再给一个很小的动作建议。
    ('cute_firstmeet',        'cute', '以后你的用量，我帮你看着～'),
    ('cute_budgetwarn',       'cute', '预算用了八成啦，后面省着点喵。'),
    ('cute_budgetover',       'cute', '呀，预算超了。要不先停一停？'),
    ('cute_budgetmonthover',  'cute', '这个月的额度用光啦…下个月再冲？'),
    ('cute_ctxhigh',          'cute', '上下文快满啦，开个新会话会舒服些。'),
    ('cute_cachebad',         'cute', '缓存掉下来了，是不是换了上下文呀？'),
    ('cute_cachebest',        'cute', '缓存创新高！今天好省呀～'),
    ('cute_longsession',      'cute', '已经连着干四个小时啦，喝口水吧。'),
    ('cute_latenight',        'cute', '都这么晚了…早点睡好不好？'),
    ('cute_turnsmilestone',   'cute', '又到一个新节点啦，我都记着呢。'),
    ('cute_sitlong',          'cute', '坐好久啦，站起来伸个懒腰嘛～'),
    ('cute_personaswitch',    'cute', '换好啦，我在这儿呢。'),
    ('cute_notranscript',     'cute', '找不到会话日志，我看不见你在做什么…'),
    ('cute_turnhigh',         'cute', '这一轮好多呀，我有点晕…'),

    # ===== snark 毒舌吐槽 =====
    ('snark_start_1', 'snark', '又来活了。'),
    ('snark_start_2', 'snark', '开始吧。'),
    ('snark_done_1',  'snark', '完事了。'),
    ('snark_done_2',  'snark', '搞定，下一个。'),
    ('snark_alert_1', 'snark', '又超了。'),
    ('snark_alert_2', 'snark', '你是真能花。'),
    ('snark_idle_1',  'snark', '终于清静了。'),
    # snark 的原则：短、刺、用反问收尾，不安慰，也不解释。
    ('snark_firstmeet',      'snark', '哦，你又来烧 token 了。'),
    ('snark_budgetwarn',     'snark', '八成没了。悠着点。'),
    ('snark_budgetover',     'snark', '又超了。月底你打算喝西北风？'),
    ('snark_budgetmonthover','snark', '月度额度清零。下个月见。'),
    ('snark_ctxhigh',        'snark', '上下文快溢出来了，还塞？'),
    ('snark_cachebad',       'snark', '缓存掉了。你又改上下文了？'),
    ('snark_cachebest',      'snark', '缓存新高。行吧，这次算你聪明。'),
    ('snark_longsession',    'snark', '四个小时了。屁股还好吗？'),
    ('snark_latenight',      'snark', '这个点还不睡。明天别喊困。'),
    ('snark_turnsmilestone', 'snark', '又一个节点。你还真能熬。'),
    ('snark_sitlong',        'snark', '九十分钟了。你是长椅子上了？'),
    ('snark_personaswitch',  'snark', '行，现在轮到我了。'),
    ('snark_notranscript',   'snark', '日志没了。我瞎了，你也差不多。'),
    ('snark_turnhigh',       'snark', '这一轮塞了多少？整个仓库？'),
]

TARGET_RATE = 44100

# 四个高频功能节点：每轮都可能响，必须短。
# 除此之外的节点都是事件台词（预算超支、上下文告警、久坐……），
# 一次会话顶多响一两回，允许说得完整一点、慢一点。
FUNC_NODES = ('start', 'done', 'alert', 'idle')


def is_event(name: str) -> bool:
    parts = name.split('_')
    return len(parts) >= 2 and parts[1] not in FUNC_NODES


# 事件台词的语气调整：只对 eng 生效。
# MOSS 说话本来就不赶时间，慢一档才有那个味道；
# 但同一套参数套到 snark 上，毒舌会变拖延、笑点全没，cute 会变黏。
# 所以这里按人格分开，别图省事统一覆盖。
EVENT_RATE = {
    'eng': '-12%',
}


# ---------------------------------------------------------------- 音频处理
def decode_to_mono(raw: bytes) -> np.ndarray:
    """mp3 字节 → float32 单声道 float，采样率保持不变（后面统一重采样）。"""
    with av.open(io.BytesIO(raw)) as container:
        stream = container.streams.audio[0]
        chunks = []
        for frame in container.decode(stream):
            arr = frame.to_ndarray()
            # av 返回形状多为 (channels, samples)，少数情况 (1, samples*channels)
            if arr.dtype.kind in 'iu':
                arr = arr.astype(np.float32) / np.iinfo(arr.dtype).max
            if arr.ndim == 2:
                arr = arr.mean(axis=0)
            chunks.append(arr.astype(np.float32))
        if not chunks:
            raise RuntimeError('解码失败：没有音频帧')
        return np.concatenate(chunks)


def resample(x: np.ndarray, src: int, dst: int) -> np.ndarray:
    """线性插值重采样。语音够用，不值得为此引入 scipy。"""
    if src == dst or len(x) == 0:
        return x
    n = int(round(len(x) * dst / src))
    if n <= 1:
        return x
    src_idx = np.linspace(0, len(x) - 1, n, dtype=np.float64)
    return np.interp(src_idx, np.arange(len(x)), x).astype(np.float32)


def biquad(x: np.ndarray, sr: int, kind: str, freq: float, q: float = 0.707) -> np.ndarray:
    """二阶 IIR 高通/低通。手写 biquad 是为了不依赖 scipy。"""
    w0 = 2.0 * np.pi * freq / sr
    cos_w0, sin_w0 = np.cos(w0), np.sin(w0)
    alpha = sin_w0 / (2.0 * q)
    if kind == 'lowpass':
        b0, b1, b2 = (1 - cos_w0) / 2, 1 - cos_w0, (1 - cos_w0) / 2
    else:  # highpass
        b0, b1, b2 = (1 + cos_w0) / 2, -(1 + cos_w0), (1 + cos_w0) / 2
    a0, a1, a2 = 1 + alpha, -2 * cos_w0, 1 - alpha
    b0, b1, b2, a1, a2 = b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0

    # 直接型 I 逐样本递推（语音很短，性能无所谓）
    y = np.zeros_like(x, dtype=np.float64)
    x1 = x2 = y1 = y2 = 0.0
    for i, xn in enumerate(x):
        yn = b0 * xn + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2
        x2, x1 = x1, xn
        y2, y1 = y1, yn
        y[i] = yn
    return y.astype(np.float32)


def soft_reverb(x: np.ndarray, sr: int, wet: float) -> np.ndarray:
    """两条短延时叠加，模拟狭小舱体反射。湿声过高会糊，0.2 以内是安全区。"""
    if wet <= 0:
        return x
    out = x.copy()
    for delay_ms, gain in ((13.0, 0.55), (29.0, 0.34)):
        d = int(sr * delay_ms / 1000.0)
        if d < len(x):
            out[d:] += x[:-d] * gain * wet
    return out


def saturate(x: np.ndarray, drive: float) -> np.ndarray:
    """轻微 tanh 饱和，给电子音加一点"被电路推过"的毛边。"""
    if drive <= 0:
        return x
    return np.tanh(x * drive) / np.tanh(drive)


def soft_limit(x: np.ndarray, th: float = 0.90) -> np.ndarray:
    """
    软削波：只压超过阈值的部分，阈值以下一个采样都不动。

    注意别对整个数组套公式 —— 那个 tanh 表达式只在 a>th 时才等于 a，
    对 a<th 会算出一个小于 a 的值（0.5 会被算成 0.8），等于把正常音量反削了。
    """
    a = np.abs(x)
    if not len(a) or float(np.max(a)) <= th:
        return x.astype(np.float32)
    out = x.astype(np.float32).copy()
    over = a > th
    out[over] = np.sign(x[over]) * (
        th + (1.0 - th) * np.tanh((a[over] - th) / (1.0 - th))
    )
    return out


def normalize(x: np.ndarray, peak: float = 0.88, rms_target: float = 0.115) -> np.ndarray:
    """
    两级归一化：先压峰值，再补响度。

    只做峰值归一化会让窄频带的人声（eng 那种滤波很狠的）听起来明显偏轻，
    切人格时音量会突然掉一截。所以第二步按 RMS 补偿，上限 +6dB，
    最后软削波兜底。
    """
    m = float(np.max(np.abs(x))) or 1.0
    x = x * (peak / m)

    r = float(np.sqrt(np.mean(x ** 2)))
    if r > 1e-6:
        x = x * min(rms_target / r, 2.0)

    return soft_limit(x)


def trim_silence(x: np.ndarray, sr: int, thresh: float = 0.006, pad_ms: int = 22) -> np.ndarray:
    """切掉首尾静音。TTS 前后常带 200ms 空白，那点空白会让语音"慢半拍"。"""
    if len(x) == 0:
        return x
    env = np.abs(x)
    idx = np.where(env > thresh)[0]
    if len(idx) == 0:
        return x
    pad = int(sr * pad_ms / 1000.0)
    a = max(0, idx[0] - pad)
    b = min(len(x), idx[-1] + pad)
    return x[a:b]


def fade_edges(x: np.ndarray, sr: int, ms: int = 6) -> np.ndarray:
    """首尾微淡入淡出，消除爆音。"""
    n = int(sr * ms / 1000.0)
    if n <= 0 or len(x) < n * 2:
        return x
    x = x.copy()
    ramp = np.linspace(0.0, 1.0, n, dtype=np.float32)
    x[:n] *= ramp
    x[-n:] *= ramp[::-1]
    return x


def process(raw: bytes, profile: dict) -> tuple[np.ndarray, int]:
    src_rate, x = 24000, decode_to_mono(raw)
    # 先从 container 猜采样率会多一次解码，这里直接用目标率重采样
    # （edge-tts 输出固定 24kHz，若将来变了，重采样仍然按比例正确）
    x = resample(x, src_rate, TARGET_RATE)
    sr = TARGET_RATE

    lo, hi = profile['band']
    x = biquad(x, sr, 'highpass', lo)
    x = biquad(x, sr, 'lowpass', hi)
    x = saturate(x, profile['drive'])
    x = soft_reverb(x, sr, profile['wet'])
    x = trim_silence(x, sr)
    x = normalize(x)
    x = fade_edges(x, sr)
    return x, sr


def write_wav(path: str, x: np.ndarray, sr: int) -> None:
    """16bit PCM 单声道，与 assets/sounds 里现有 wav 的格式保持一致。"""
    os.makedirs(os.path.dirname(path) or '.', exist_ok=True)
    pcm = np.clip(x * 32767.0, -32768, 32767).astype('<i2')
    with wave.open(path, 'wb') as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(sr)
        w.writeframes(pcm.tobytes())


# ---------------------------------------------------------------- 主流程
async def synth(text: str, profile: dict) -> bytes:
    comm = edge_tts.Communicate(
        text,
        profile['voice'],
        rate=profile['rate'],
        pitch=profile['pitch'],
    )
    buf = bytearray()
    async for chunk in comm.stream():
        if chunk['type'] == 'audio':
            buf.extend(chunk['data'])
    if not buf:
        raise RuntimeError('edge-tts 没有返回音频，检查网络或音色名')
    return bytes(buf)


async def run(items, out_dir, dry=False, override=None, suffix=''):
    total = len(items)
    override = override or {}
    for i, (name, persona, text) in enumerate(items, 1):
        profile = dict(PROFILES[persona])
        event = is_event(name)
        if event and persona in EVENT_RATE:
            profile['rate'] = EVENT_RATE[persona]
        profile.update(override)
        arrow = f'[{i}/{total}]'
        if dry:
            print(f'{arrow} {name:<18} {persona:<6} {text}')
            continue
        try:
            raw = await synth(text, profile)
            x, sr = process(raw, profile)
            dur = len(x) / sr
            path = os.path.join(out_dir, name + suffix + '.wav')
            write_wav(path, x, sr)
            # 功能句每轮都响，理想 1.2 秒，1.3 是容忍线；
            # 事件台词不设上限，说完整比说简短重要。
            limit = 99.0 if event else 1.3
            flag = f'  ← 偏长（上限 {limit}s）' if dur > limit else ''
            print(f'{arrow} {name:<18} {dur:.2f}s  {path}{flag}')
        except Exception as e:
            print(f'{arrow} {name:<14} 失败: {e}')
        await asyncio.sleep(0.35)  # 温和一点，别把接口打成压测


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--only', nargs='*', help='只生成指定条目名（前缀匹配）')
    ap.add_argument('--out', default='voice-preview', help='输出目录')
    ap.add_argument('--pitch', help='覆盖音调偏移，如 -30Hz（用来做压低对比）')
    ap.add_argument('--band', help='覆盖频带，格式 低频,高频，如 120,5200')
    ap.add_argument('--suffix', default='', help='输出文件名后缀，如 -p34')
    ap.add_argument('--list', action='store_true', help='列出条目')
    ap.add_argument('--dry', action='store_true', help='只打印不合成')
    args = ap.parse_args()

    items = LINES
    if args.only:
        items = [it for it in LINES if any(it[0].startswith(p) for p in args.only)]

    if args.list:
        for name, persona, text in items:
            print(f'{name:<14} {persona:<6} {text}')
        return

    if not items:
        sys.exit('没有匹配的条目')

    override = {}
    if args.pitch:
        override['pitch'] = args.pitch
    if args.band:
        lo, hi = args.band.split(',')
        override['band'] = (float(lo), float(hi))

    asyncio.run(run(items, args.out, args.dry, override, args.suffix))

    # 这里**不写索引**。素材有两个来源（本脚本的 edge-tts 和 moss-voice 的原声模型），
    # 任何一边写索引都会把另一边的条目覆盖掉——实测只生成 6 条就把 51 条的索引冲成了 6 条。
    # 索引统一交给 tools/rebuild-voice-index.py 扫目录生成。
    if not args.dry:
        print('\n提示：素材已更新，记得跑 tools/rebuild-voice-index.py 重建索引')


if __name__ == '__main__':
    main()
