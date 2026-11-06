#!/usr/bin/env python3
"""从本机安装的有道词典里重新找接口地址，接口失效时用。只用标准库。

    python tools/find_endpoints.py <有道安装目录>

扫描 exe/dll 里的字符串和 resultui 下的前端代码，打印 youdao.com 的 URL，
并列出前端发起词典查询的代码片段。详细流程见 docs/MAINTENANCE.md。
"""
import pathlib
import re
import sys

URL = re.compile(rb'https?://[a-zA-Z0-9.-]*youdao\.com[a-zA-Z0-9./?=_&%{}:-]*')
URL_UTF16 = re.compile(rb'(?:h\x00t\x00t\x00p\x00s?\x00?:\x00/\x00/\x00)(?:[\x21-\x7e]\x00)+')
KEYWORDS = ('dict', 'translate', 'fanyi', 'voice', 'jsonapi', 'suggest', 'fsearch')
SNIPPET = re.compile(r'.{0,120}(?:jsonapi_s|dicttranslate|webtranslate|dictvoice).{0,240}')


def scan_binary(file):
    data = file.read_bytes()
    found = {m.group().decode('ascii', 'replace') for m in URL.finditer(data)}
    for match in URL_UTF16.finditer(data):
        text = match.group().decode('utf-16le', 'replace')
        if 'youdao.com' in text:
            found.add(text)
    return found


def main():
    sys.stdout.reconfigure(encoding='utf-8')
    if len(sys.argv) != 2:
        print(__doc__)
        return 2
    root = pathlib.Path(sys.argv[1])

    urls = set()
    for file in root.rglob('*'):
        if file.suffix.lower() in ('.exe', '.dll') and file.stat().st_size < 50 * 1024 * 1024:
            urls |= scan_binary(file)

    print('== 和查词相关的 URL')
    for url in sorted(urls):
        if any(word in url.lower() for word in KEYWORDS):
            print(url)

    print('\n== 前端查询代码')
    for file in sorted(root.rglob('resultui/html/js/*.js')):
        text = file.read_text(encoding='utf-8', errors='replace')
        for match in list(SNIPPET.finditer(text))[:2]:
            print('[%s] %s\n' % (file.name, match.group()))
    return 0


if __name__ == '__main__':
    sys.exit(main())
