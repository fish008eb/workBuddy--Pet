# moss-voice — MOSS 原声合成

用《流浪地球 1》《流浪地球 2》里 MOSS 的原声训练出来的 VITS 模型合成语音。
不是 TTS 模仿，是训练得到的那个音色本身。

## 组成

| 路径 | 说明 |
|---|---|
| `fetch-model.py` | 一键拉取权重与推理代码（**新克隆仓库先跑这个**） |
| `model/moss.pth` | 声线权重，151.5MB —— **不入库**，由上面的脚本下载 |
| `model/config.json` | VITS 配置：16kHz、单说话人、中文注音音素表 |
| `moss-tts-service-master/` | 官方推理代码（`open-moss/moss-tts-service`）—— **不入库**，同样由脚本下载 |
| `synth.py` | 本项目的合成入口，CPU 可跑 |
| `eng-all.json` | eng 人格全部 31 条台词（由 `make-voice.py` 的 `LINES` 导出） |

## 首次使用

```bash
python tools/moss-voice/fetch-model.py
```

权重和推理代码不进仓库是有意的：模型许可写明不得 copy / distribute / reproduce，
提交进 git 等于替作者再分发。仓库里只留脚本，其余本地下载。

## 用法

```bash
# 单句
tools/.venv-moss/Scripts/python.exe tools/moss-voice/synth.py --text "任务已接收。" --out out

# 整批（推荐，清单来自 make-voice.py）
tools/.venv-moss/Scripts/python.exe tools/moss-voice/synth.py \
    --manifest tools/moss-voice/eng-all.json --out assets/sounds/voice-eng-new
```

输出是 44.1kHz / 16bit / 单声道，响度统一归一到 -18.9dBFS，
和 `make-voice.py` 生成的 cute/snark 那批对齐，切人格不会忽大忽小。

改完台词后要重跑 `tools/rebuild-voice-index.py` 重建索引。

## 环境

```
tools/.venv-moss/          Python 3.13 + torch(cpu) + pypinyin/jieba/cn2an/unidecode
```

本机没有独显，纯 CPU 推理，一句 3~5 秒的台词约 1 秒合成完。

## 三个坑（改代码前必读）

**1. `monotonic_align` 的 C 扩展只编了 Python 3.8**

仓库自带 `core.cp38-win_amd64.pyd`，本机是 3.13，`import` 直接抛 ImportError。
它只在训练时做时长对齐，推理用不到，所以 `lib/monotonic_align/__init__.py` 已改成
try/except 空实现。**不要把这段改回去**，改了推理就跑不起来。

**2. `lib.text` 的 `__init__` 会连带拉入日文前端**

`lib/text/__init__.py` → `cleaners.py` → `japanese.py` → `pyopenjtalk`。
pyopenjtalk 要编译 C 代码，Windows + Python 3.13 基本装不上。
`synth.py` 改为按文件路径单独加载 `mandarin.py`，绕开整个包。
只做中文时不要 `from lib.text import ...`。

**3. 音素表里没有英文字母**

清洗前必须把 `MOSS` 替换成 `mos`，否则整个词会被丢掉，听起来像漏字。
`synth.py` 的 `prepare_text()` 做了这件事，和官方 `server.py` 一致。

## 许可

模型仓库标注：**禁止商用**，以及不得用于危害国家安全、损害公共利益等用途。

这与电影方的授权是两件独立的事。个人使用、开源分发没问题；
如果本项目将来商业化，这两个许可都要单独确认。

音频随包分发时，建议在项目 README 里注明音色来源，比藏着更省事。
