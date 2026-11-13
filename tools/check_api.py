#!/usr/bin/env python3
"""检查 Double Ctrl 依赖的有道接口是否还能用。只用标准库。

    python tools/check_api.py

全部通过时退出码为 0。失败项对应的修复方法见 docs/MAINTENANCE.md。
"""
import hashlib
import json
import sys
import urllib.parse
import urllib.request

API = 'https://dict.youdao.com/jsonapi_s?doctype=json&jsonversion=4'
VOICE = 'https://dict.youdao.com/dictvoice?audio='
DICTS = '{"count":99,"dicts":[["ec","ce","newcj","newjc","kc","ck","fc","cf","multle"],["web_trans"],["fanyi"]]}'
UA = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) DoubleCtrl/1.0'


SIGN_KEY = 'Mk6hqtUp33DGGtoS63tTJbMUYjRrG1Lu'


def md5(text):
    return hashlib.md5(text.encode('utf-8')).hexdigest()


# 和 src/DictClient.cs 的签名一致。注意 C# 的 Length 按 UTF-16 计数，这里也按 UTF-16 算。
def sign(text):
    t = str((len((text + 'webdict').encode('utf-16-le')) // 2) % 10)
    return t, md5('web' + text + t + SIGN_KEY + md5(text + 'webdict'))


def query(text, le):
    t, s = sign(text)
    body = urllib.parse.urlencode({'q': text, 'le': le, 't': t, 'sign': s, 'client': 'web', 'keyfrom': 'webdict', 'dicts': DICTS})
    request = urllib.request.Request(API, body.encode(), {'User-Agent': UA, 'Content-Type': 'application/x-www-form-urlencoded'})
    with urllib.request.urlopen(request, timeout=15) as response:
        return json.load(response)


def path(node, *keys):
    for key in keys:
        if isinstance(node, list):
            node = node[0] if node else None
        node = node.get(key) if isinstance(node, dict) else None
    return node


# 名称、查询词、le、结果里必须存在的字段路径。这些路径和 src/DictClient.cs 的解析一一对应。
CASES = [
    ('英文单词 ec', 'translate', 'en', ('ec', 'word', 'trs')),
    ('英文音标', 'translate', 'en', ('ec', 'word', 'usphone')),
    ('中文单词 ce', '翻译', 'en', ('ce', 'word', 'trs')),
    ('拼音 simple', '翻译', 'en', ('simple', 'word', 'phone')),
    ('网络释义', 'translate', 'en', ('web_trans', 'web-translation', 'trans')),
    ('英译中句子', 'I love programming very much.', 'en', ('fanyi', 'tran')),
    ('中译英句子', '今天天气很好，我们一起去公园散步。', 'en', ('fanyi', 'tran')),
    ('日文句子', '今日はいい天気ですね', 'ja', ('fanyi', 'tran')),
    ('日文单词 newjc', 'ありがとう', 'ja', ('newjc', 'word', 'sense')),
    ('韩文单词 kc', '안녕하세요', 'ko', ('kc', 'word', 'trs')),
    ('法文单词 fc', 'bonjour', 'fr', ('fc', 'word', 'trs')),
]


def main():
    sys.stdout.reconfigure(encoding='utf-8')
    failed = 0
    for name, text, le, keys in CASES:
        try:
            value = path(query(text, le), *keys)
            detail = '' if value else ' 缺少字段 ' + '.'.join(keys)
        except Exception as error:
            value, detail = None, ' %s: %s' % (type(error).__name__, error)
        failed += 0 if value else 1
        print(('通过 ' if value else '失败 ') + name + detail)

    # 签名失效时接口不报错，而是对中文返回随机词条，所以要核对内容本身。
    try:
        word = path(query('主控', 'en'), 'ce', 'word', 'trs', '#text')
        ok = word == 'master control'
        detail = '' if ok else ' 返回了 %r，签名可能失效' % word
    except Exception as error:
        ok, detail = False, ' %s: %s' % (type(error).__name__, error)
    failed += 0 if ok else 1
    print(('通过 ' if ok else '失败 ') + '签名（主控 → master control）' + detail)

    try:
        request = urllib.request.Request(VOICE + 'translate&type=2', headers={'User-Agent': UA})
        with urllib.request.urlopen(request, timeout=15) as response:
            kind = response.headers.get('Content-Type', '')
            ok = kind.startswith('audio/') and len(response.read()) > 1000
        detail = '' if ok else ' Content-Type=' + kind
    except Exception as error:
        ok, detail = False, ' %s: %s' % (type(error).__name__, error)
    failed += 0 if ok else 1
    print(('通过 ' if ok else '失败 ') + '发音 dictvoice' + detail)

    print('\n全部通过' if not failed else '\n%d 项失败，见 docs/MAINTENANCE.md' % failed)
    return 1 if failed else 0


if __name__ == '__main__':
    sys.exit(main())
