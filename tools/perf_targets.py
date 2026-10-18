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
# m.v2: импорт/экспорт (STEP точный, IGES, STL) — деталь со 100 отверстиями и деталь K08; сравнение чтения/записи STEP с
# OpenCascade на тех же файлах (io/occt_io.tsv, tools/occt_io_time.py)
occio = {}
pio = os.path.join(out, 'io', 'occt_io.tsv')
if os.path.exists(pio):
    for l in open(pio):
        f = l.rstrip('\n').split('\t')
        if len(f) >= 3: occio[f[0]] = (float(f[1]), float(f[2]))
for r in perf:
    if r['kind'] != 'io' or 'error' in r: continue
    n = r['name']
    target(f'{n}: экспорт точного STEP', r['stepWriteMs'], 2000, 'мс')
    target(f'{n}: импорт точного STEP (объём сходится)', r['stepReadMs'] if r['volumeIn'] > 0 and abs(r['volumeBack'] - r['volumeIn']) <= 1e-6 * abs(r['volumeIn']) else None, 3000, 'мс')
    target(f'{n}: выделения памяти на импорт STEP', r['stepReadAllocMb'], 500, 'МБ')
    target(f'{n}: экспорт сетки STL (разметка + запись)', r['stlMs'], 3000, 'мс')
    print(f"справка {n}: STEP {r['stepKb']:.0f} КБ, граней {r['faces']}; IGES запись {r['igesWriteMs']:.0f} мс, чтение {r['igesReadMs']:.0f} мс; STL {r['stlMs']:.0f} мс")
    if n in occio:
        print(f"справка {n}: OpenCascade чтение STEP {occio[n][0]:.0f} мс, запись {occio[n][1]:.0f} мс; наши — {r['stepReadMs']:.0f} и {r['stepWriteMs']:.0f} мс")
        target(f'{n}: импорт STEP / OpenCascade', r['stepReadMs'] / max(occio[n][0], 1e-3), 5, '×')
for r in perf:
    if r['kind'] == 'holes100': target('деталь со 100 отверстиями, полное перестроение', r['fullMs'], 3000, 'мс')
    if r['kind'] == 'session': target('долгая сессия (200 правок): рост управляемой памяти', r['growthMb'], 50, 'МБ')

ours, occt = {}, {}
pp, po = os.path.join(out, 'd8', 'prof.tsv'), os.path.join(out, 'd8', 'occt_time.tsv')
if os.path.exists(pp) and os.path.exists(po):
    for l in open(pp):
        f = l.rstrip('\n').split('\t')
        ours[f[0]] = [(float(x.split(':')[1]), x.split(':')[2].split('/')[-1], float(x.split(':')[3]) if x.count(':') >= 3 else float(x.split(':')[1])) for x in f[2].split(',') if x.count(':') >= 2]
    for l in open(po):
        f = l.rstrip('\n').split('\t'); occt[f[0]] = [float(x) for x in f[1].split(',') if x]
    a, b, c = [], [], []
    for k, v in ours.items():
        if k in occt and len(occt[k]) == len(v):
            for (ms, g, br), om in zip(v, occt[k]):
                if g == 'ExactAnalytic': a.append(ms); b.append(om); c.append(br)
    if a:
        # сравнение «B-rep против B-rep»: у OpenCascade — BRepAlgoAPI без проверки тела и без сетки; у нас — операция без
        # проверок результата и разметки граней для показа (они идут в фоне, этап U2); полное время — строкой ниже
        target('точная булева (B-rep) / OpenCascade, медиана (серия 8)', st.median(c) / st.median(b), 3, '×')
        target('точная булева (B-rep) / OpenCascade, p90 (серия 8)', pct(c, 0.9) / pct(b, 0.9), 5, '×')
        print(f'справка: с проверками и разметкой показа — медиана {st.median(a) / st.median(b):.2f} ×, p90 {pct(a, 0.9) / pct(b, 0.9):.2f} ×; '
              f'наши B-rep медиана {st.median(c):.0f} мс, p90 {pct(c, 0.9):.0f} мс')
        print(f'серия 8: точных операций {len(a)}; наши медиана {st.median(a):.0f} мс, p90 {pct(a, 0.9):.0f} мс; '
              f'OpenCascade медиана {st.median(b):.1f} мс, p90 {pct(b, 0.9):.1f} мс')

for name, v, lim, unit, ok in rows:
    vs = '—' if v is None else (f'{v:.2f}' if unit == '×' else f'{v:.0f}')
    print(f"{'да ' if ok else 'НЕТ'} {name}: {vs} {unit} (цель ≤ {lim} {unit})")
print(f"PERF: met {sum(1 for r in rows if r[4])}/{len(rows)}")
