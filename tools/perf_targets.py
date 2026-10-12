#!/usr/bin/env python3
"""Ворота g.13: сравнение замеров базового прогона с целями пользователя (согласованы 2026-10-12).
Запуск: perf_targets.py <каталог прогона>. Источники: perf.jsonl (K01Checks perftargets), d8/prof.tsv (время наших булевых
серии 8, DIFFPROF), d8/occt_time.tsv (OpenCascade на тех же операндах). Итог — строка «PERF: met k/N».
Время — на стенде прогона (Linux, 2 ядра); на Windows перемеряется при g.12."""
import sys, os, json, statistics as st

out = sys.argv[1]

def pct(v, q):
    s = sorted(v); return s[min(len(s) - 1, int(q * len(s)))]

rows = []  # (цель, значение, предел, единица, годно)
def target(name, value, limit, unit):
    rows.append((name, value, limit, unit, value is not None and value <= limit))

perf = [json.loads(l) for l in open(os.path.join(out, 'perf.jsonl'))] if os.path.exists(os.path.join(out, 'perf.jsonl')) else []
parts = [r for r in perf if r['kind'] == 'part']
if parts:
    target('предпросмотр правки последней операции, p95 (макс. по деталям)', max(r['previewLastMs'] for r in parts), 150, 'мс')
    target('правка последней операции, макс.', max(r['editLastMs'] for r in parts), 500, 'мс')
    target('правка средней операции, макс.', max(r['editMidMs'] for r in parts), 1000, 'мс')
    target('полное перестроение детали ≤ 25 операций, макс.', max(r['fullMs'] for r in parts), 2000, 'мс')
    target('выделения памяти на полное перестроение, макс.', max(r['allocMb'] for r in parts), 500, 'МБ')
for r in perf:
    if r['kind'] == 'holes100': target('деталь со 100 отверстиями, полное перестроение', r['fullMs'], 3000, 'мс')
    if r['kind'] == 'session': target('долгая сессия (200 правок): рост управляемой памяти', r['growthMb'], 50, 'МБ')

ours, occt = {}, {}
pp, po = os.path.join(out, 'd8', 'prof.tsv'), os.path.join(out, 'd8', 'occt_time.tsv')
if os.path.exists(pp) and os.path.exists(po):
    for l in open(pp):
        f = l.rstrip('\n').split('\t')
        ours[f[0]] = [(float(x.split(':')[1]), x.split(':')[2].split('/')[-1]) for x in f[2].split(',') if x.count(':') >= 2]
    for l in open(po):
        f = l.rstrip('\n').split('\t'); occt[f[0]] = [float(x) for x in f[1].split(',') if x]
    a, b = [], []
    for k, v in ours.items():
        if k in occt and len(occt[k]) == len(v):
            for (ms, g), om in zip(v, occt[k]):
                if g == 'ExactAnalytic': a.append(ms); b.append(om)
    if a:
        target('точная булева / OpenCascade, медиана (серия 8)', st.median(a) / st.median(b), 3, '×')
        target('точная булева / OpenCascade, p90 (серия 8)', pct(a, 0.9) / pct(b, 0.9), 5, '×')
        print(f'серия 8: точных операций {len(a)}; наши медиана {st.median(a):.0f} мс, p90 {pct(a, 0.9):.0f} мс; '
              f'OpenCascade медиана {st.median(b):.1f} мс, p90 {pct(b, 0.9):.1f} мс')

for name, v, lim, unit, ok in rows:
    vs = '—' if v is None else (f'{v:.2f}' if unit == '×' else f'{v:.0f}')
    print(f"{'да ' if ok else 'НЕТ'} {name}: {vs} {unit} (цель ≤ {lim} {unit})")
print(f"PERF: met {sum(1 for r in rows if r[4])}/{len(rows)}")
