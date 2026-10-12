#!/usr/bin/env python3
"""Подсчёт выполнения по src/docs/release/PROGRESS_ITEMS.tsv.  Запуск: python3 tools/progress_score.py [файл]
Три числа, у каждого свой смысл:
  1. Выполнение инструкции — доля требований шагов KRN-00…11 (по пунктам и с весами трудоёмкости шагов).
  2. Покрытие обязательной матрицы — среднее по 11 группам матрицы (внутри группы — среднее по строкам).
  3. Ворота выпуска — закрытые пункты чек-листа промышленного выпуска (инструкция: «100%» = все закрыты).
"""
import sys, collections
WEIGHTS = {"KRN-00": 2, "KRN-01": 5, "KRN-02": 6, "KRN-03": 8, "KRN-04": 10, "KRN-05": 10,
           "KRN-06": 18, "KRN-07": 18, "KRN-08": 8, "KRN-09": 7, "KRN-10": 5, "KRN-11": 3}
path = sys.argv[1] if len(sys.argv) > 1 else "src/docs/release/PROGRESS_ITEMS.tsv"
rows = []
for line in open(path, encoding="utf-8-sig"):
    if line.startswith("#") or line.startswith("kind\t") or not line.strip():
        continue
    k, g, i, s, req, ev = (line.rstrip("\n").split("\t") + [""] * 6)[:6]
    s = float(s)
    assert s in (0, 0.25, 0.5, 1), f"{i}: статус {s}"
    if k == "gate":
        assert s in (0, 1), f"{i}: ворота только 0/1"
    if k == "step":
        assert s in (0, 0.5, 1), f"{i}: требование шага только 0/0.5/1"
    rows.append((k, g, i, s, req, ev))
assert sum(WEIGHTS.values()) == 100

steps = collections.OrderedDict()
for k, g, i, s, *_ in rows:
    if k == "step":
        steps.setdefault(g, []).append(s)
print("Шаг      пунктов  выполнено  доля   вес  вклад")
total_w = 0.0
for g, w in WEIGHTS.items():
    l = steps.get(g, [])
    frac = sum(l) / len(l) if l else 0.0
    total_w += frac * w
    print(f"{g}   {len(l):5d}    {sum(l):6.1f}   {frac*100:5.1f}%  {w:3d}  {frac*w:5.2f}")
n_items = sum(len(l) for l in steps.values()); done = sum(sum(l) for l in steps.values())
print(f"Выполнение инструкции: по пунктам {done:.1f}/{n_items} = {done/n_items*100:.1f}%; с весами шагов {total_w:.1f}%")

groups = collections.OrderedDict()
for k, g, i, s, *_ in rows:
    if k == "matrix":
        groups.setdefault(g, []).append(s)
print("\nГруппа матрицы            строк  покрытие")
for g, l in groups.items():
    print(f"{g:24s} {len(l):5d}   {sum(l)/len(l)*100:5.1f}%")
cov = sum(sum(l) / len(l) for l in groups.values()) / len(groups)
print(f"Покрытие обязательной матрицы ({len(groups)} групп): {cov*100:.1f}%")

gates = [r for r in rows if r[0] == "gate"]
print(f"\nВорота промышленного выпуска закрыты: {int(sum(r[3] for r in gates))}/{len(gates)}")
