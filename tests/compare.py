"""Сравнение журналов наборов A06 двух прогонов. Ненулевой код возврата: разное число записей,
отсутствующий журнал, записи, не прошедшие разбор, или различия (кроме полей времени).
--allow-hash: различия только в полях hash не считаются ошибкой (смена сеток при сохранной геометрии)."""
import json, sys, re
args = [a for a in sys.argv[1:] if not a.startswith('--')]
allow_hash = '--allow-hash' in sys.argv
base, work = args[:2]
names = ['numerics','rotations','suite','target','a01-tests','a01-corpus','a01-sequences','a03-hints','a04-cylinder']
bad = 0
def load(p):
    out, broken = [], 0
    for l in open(p, encoding='utf-8', errors='replace'):
        l = l.strip()
        if not l.startswith('{'): continue
        try: d = json.loads(l)
        except Exception: broken += 1; continue
        out.append({k: v for k, v in d.items() if not re.search(r'ms|Ms|time|Time|elapsed', k)})
    return out, broken
for a in names:
    try:
        x, bx = load(f'{base}/{a}.jsonl'); y, by = load(f'{work}/{a}.jsonl')
    except FileNotFoundError as e:
        print(f"{a:15s} MISSING {e.filename}"); bad += 1; continue
    diffs = []
    for i, j in zip(x, y):
        if json.dumps(i, sort_keys=True) == json.dumps(j, sort_keys=True): continue
        ks = [k for k in set(i) | set(j) if i.get(k) != j.get(k)]
        if allow_hash and all(k == 'hash' for k in ks): continue
        diffs.append((i, j, ks))
    lenbad = len(x) != len(y)
    status = 'FAIL' if (lenbad or diffs or bx or by) else 'ok'
    if status == 'FAIL': bad += 1
    print(f"{a:15s} {status} {len(x)} vs {len(y)} lines; differing={len(diffs)}; unparsed={bx}/{by}")
    for i, j, ks in diffs[:3]:
        print("   ", {k: (str(i.get(k))[:70], str(j.get(k))[:70]) for k in ks})
sys.exit(1 if bad else 0)
