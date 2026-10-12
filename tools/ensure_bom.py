#!/usr/bin/env python3
"""Документы с русским текстом — UTF-8 с меткой BOM: без неё Excel и часть редакторов Windows читают файл как ANSI
(кракозябры после скачивания), а GitHub показывает верно и так. Использование: ensure_bom.py <файлы или папки>."""
import os, sys
EXT = ('.tsv', '.csv', '.txt', '.md')
def fix(p):
    b = open(p, 'rb').read()
    if b.startswith(b'\xef\xbb\xbf'): return False
    b.decode('utf-8')                       # не UTF-8 — ошибка, а не порча
    open(p, 'wb').write(b'\xef\xbb\xbf' + b); return True
n = 0
for a in sys.argv[1:]:
    paths = [os.path.join(r, f) for r, _, fs in os.walk(a) for f in fs] if os.path.isdir(a) else [a]
    for p in paths:
        if p.endswith(EXT) and fix(p): n += 1; print('BOM:', p)
print(n, 'file(s) fixed')
