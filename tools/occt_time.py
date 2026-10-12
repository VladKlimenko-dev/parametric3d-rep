#!/usr/bin/env python3
"""g.13: время булевых OpenCascade на случаях разностной серии (выгрузка K01Checks diffdump): операнды читаются из точного
STEP (вне замера), та же цепочка строится BRepAlgoAPI; время каждой операции, мс. Запуск: occt_time.py <каталог> <выход.tsv>"""
import sys, os, time
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from occt_diff import read
from OCP.BRepAlgoAPI import BRepAlgoAPI_Cut, BRepAlgoAPI_Fuse, BRepAlgoAPI_Common
d, outp = sys.argv[1], sys.argv[2]
with open(outp, 'w') as out:
    for line in open(os.path.join(d, 'manifest.tsv')).read().splitlines()[1:]:
        f = line.split('\t'); cid, ops = f[0], [o for o in f[1].split(',') if o]
        try:
            shapes = [read(os.path.join(d, f"{cid}_op{k}.step")) for k in range(len(ops) + 1)]
        except Exception:
            continue
        cur = shapes[0]; ts = []
        for k, op in enumerate(ops):
            t = time.perf_counter()
            alg = {"cut": BRepAlgoAPI_Cut, "fuse": BRepAlgoAPI_Fuse, "common": BRepAlgoAPI_Common}[op](cur, shapes[k + 1])
            ok = alg.IsDone(); cur = alg.Shape() if ok else cur
            ts.append((time.perf_counter() - t) * 1000)
        out.write(f"{cid}\t{','.join('%.2f' % x for x in ts)}\n")
