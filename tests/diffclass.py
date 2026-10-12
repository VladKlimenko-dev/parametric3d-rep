"""Классификация различий журналов A06 двух прогонов (дополнение к compare.py).

Использование: diffclass.py <база> <новый> --labels <регулярное выражение> [--keys k1,k2,...]

Ненулевой код возврата, если:
  • разное число записей или запись не разбирается;
  • в новом прогоне есть провал (ok:false) или passed/failed хуже базы;
  • различается запись, метка которой НЕ подходит под --labels;
  • различается поле не из --keys (по умолчанию: before, after, gain, error, hash, warnings);
  • в warnings появилось предупреждение нового вида (сравнение без чисел), кроме исчезнувших.
Печатает сводку: какие предупреждения исчезли и какие добавились.
"""
import json, re, sys

names = ['numerics', 'rotations', 'suite', 'target', 'a01-tests', 'a01-corpus', 'a01-sequences', 'a03-hints', 'a04-cylinder']
argv = sys.argv[1:]
base, work = argv[0], argv[1]
labels = re.compile(argv[argv.index('--labels') + 1]) if '--labels' in argv else None
keys = set(argv[argv.index('--keys') + 1].split(',')) if '--keys' in argv else {'before', 'after', 'gain', 'error', 'hash', 'warnings'}
timing = re.compile(r'ms|Ms|time|Time|elapsed')
shape = lambda w: re.sub(r'[-+]?\d+(\.\d+)?(E[-+]?\d+)?', '#', w)


def load(p):
    out = []
    for line in open(p, encoding='utf-8', errors='replace'):
        line = line.strip()
        if not line.startswith('{'): continue
        d = json.loads(line)
        out.append({k: v for k, v in d.items() if not timing.search(k)})
    return out


bad, total, gone, new = 0, 0, {}, {}
for n in names:
    a, b = load(f'{base}/{n}.jsonl'), load(f'{work}/{n}.jsonl')
    if len(a) != len(b): print(f'{n}: {len(a)} vs {len(b)} records'); bad += 1; continue
    for x, y in zip(a, b):
        if y.get('ok') is False: print(f'{n}: FAILED {y}'); bad += 1
        if 'failed' in y and y['failed'] > x.get('failed', 0): print(f'{n}: more failures {x} -> {y}'); bad += 1
        if x == y: continue
        if set(x) & {'passed', 'failed'}: continue
        total += 1
        label = str(x.get('scenario') or x.get('test') or '?')
        diff = sorted(k for k in set(x) | set(y) if x.get(k) != y.get(k))
        if labels and not labels.search(label): print(f'{n}: unexpected record {label}: {diff}'); bad += 1
        extra = [k for k in diff if k not in keys]
        if extra: print(f'{n}: {label}: unexpected fields {extra}'); bad += 1
        if 'warnings' in diff:
            ws, wn = {shape(w) for w in x.get('warnings', [])}, {shape(w) for w in y.get('warnings', [])}
            for w in ws - wn: gone[w] = gone.get(w, 0) + 1
            for w in wn - ws: new[w] = new.get(w, 0) + 1
print(f'differing records: {total}')
for w, c in sorted(gone.items()): print(f'  warning gone  x{c}: {w}')
for w, c in sorted(new.items()): print(f'  warning added x{c}: {w}')
if new: bad += 1
print('OK' if bad == 0 else f'FAIL ({bad})')
sys.exit(1 if bad else 0)
