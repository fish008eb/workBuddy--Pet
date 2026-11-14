#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
fetch-model.py — 按需拉取 MOSS 声线模型与推理代码

为什么权重不随仓库走
  模型许可原文：不得 copy / distribute / reproduce / 用于商业或军事用途。
  把 152MB 的 moss.pth 提交进仓库，等于替作者再分发一次，这事儿不能干。
  所以仓库只留脚本，权重由本脚本在本地拉取。

用法（任意 Python 3.10+ 均可，不需要 venv）
  python tools/moss-voice/fetch-model.py

拉取完成后，合成用：
  tools/.venv-moss/Scripts/python.exe tools/moss-voice/synth.py --manifest tools/moss-voice/eng-all.json --out out
"""

import io
import os
import sys
import urllib.request
import zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
MODEL_DIR = os.path.join(HERE, 'model')
SVC_DIR = os.path.join(HERE, 'moss-tts-service-master')

# hf-mirror 是国内可达的 HF 镜像；官方站点在没有代理时会超时。
# 想换源就改这两个常量。
MODEL_REPO = 'https://hf-mirror.com/OpenMOSS/moss-vits-model/resolve/main/'
MODEL_FILES = ['moss.pth', 'config.json', 'MODEL_LICENSE']
SVC_ZIPS = [
    'https://codeload.github.com/open-moss/moss-tts-service/zip/refs/heads/master',
    'https://codeload.github.com/open-moss/moss-tts-service/zip/refs/heads/main',
]

UA = {'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64)'}


def fetch(url, timeout=180):
    return urllib.request.urlopen(urllib.request.Request(url, headers=UA), timeout=timeout).read()


def download_model():
    os.makedirs(MODEL_DIR, exist_ok=True)
    for name in MODEL_FILES:
        dst = os.path.join(MODEL_DIR, name)
        if os.path.exists(dst) and os.path.getsize(dst) > 0:
            print(f'  跳过 {name}（已存在，{os.path.getsize(dst)/1048576:.1f}MB）')
            continue
        print(f'  下载 {name} ...', end='', flush=True)
        data = fetch(MODEL_REPO + name)
        with open(dst, 'wb') as f:
            f.write(data)
        print(f' {len(data)/1048576:.1f}MB')
    return os.path.exists(os.path.join(MODEL_DIR, 'moss.pth'))


def download_service():
    if os.path.isdir(SVC_DIR):
        print('  跳过推理代码（已存在）')
        return True
    for url in SVC_ZIPS:
        try:
            print(f'  下载推理代码 {url.rsplit("/", 1)[-1]} ...', end='', flush=True)
            data = fetch(url)
            with zipfile.ZipFile(io.BytesIO(data)) as z:
                z.extractall(HERE)
                name = z.namelist()[0].split('/')[0]
            print(f' {len(data)//1024}KB')
            if os.path.isdir(os.path.join(HERE, name)) and name != os.path.basename(SVC_DIR):
                os.rename(os.path.join(HERE, name), SVC_DIR)
            return True
        except Exception as e:
            print(f' 失败：{e}')
    return False


def main():
    print('== 1/2 模型权重 ==')
    if not download_model():
        sys.exit('权重下载失败，检查网络或更换 MODEL_REPO')

    print('== 2/2 推理代码 ==')
    if not download_service():
        sys.exit('推理代码下载失败')

    print()
    print('完成。下一步：')
    print('  1) 建环境（首次）: python -m venv tools/.venv-moss')
    print('     tools/.venv-moss/Scripts/pip install torch --index-url https://download.pytorch.org/whl/cpu')
    print('     tools/.venv-moss/Scripts/pip install pypinyin jieba cn2an unidecode numpy scipy')
    print('  2) 合成: tools/.venv-moss/Scripts/python.exe tools/moss-voice/synth.py \\')
    print('           --manifest tools/moss-voice/eng-all.json --out out')
    print('  3) 重建索引: tools/.venv-sound/Scripts/python.exe tools/rebuild-voice-index.py')
    print()
    print('提醒：模型许可禁止商用与再分发，产物可用于个人/非商业用途。')


if __name__ == '__main__':
    main()
