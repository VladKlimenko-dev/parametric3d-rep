#!/usr/bin/env python3
"""m.v2: время OpenCascade на тех же файлах, что пишет K01Checks perftargets (каталог io): чтение STEP (ReadFile + TransferRoots)
и запись STEP (Transfer + Write), медиана из 3. Запуск: occt_io_time.py <каталог io> <выход.tsv>; строки: имя, чтение мс, запись мс."""
import sys, os, time, statistics as st, tempfile
from OCP.STEPControl import STEPControl_Reader, STEPControl_Writer, STEPControl_AsIs
from OCP.IFSelect import IFSelect_RetDone

d, out = sys.argv[1], sys.argv[2]
rows = []
for f in sorted(os.listdir(d)):
    if not f.endswith('.step'): continue
    tr, tw = [], []
    for _ in range(3):
        t0 = time.perf_counter(); r = STEPControl_Reader()
        if r.ReadFile(os.path.join(d, f)) != IFSelect_RetDone: break
        r.TransferRoots(); shape = r.OneShape(); tr.append((time.perf_counter() - t0) * 1000)
        t0 = time.perf_counter(); w = STEPControl_Writer(); w.Transfer(shape, STEPControl_AsIs)
        tmp = os.path.join(tempfile.gettempdir(), 'occt_io_' + f); w.Write(tmp); tw.append((time.perf_counter() - t0) * 1000)
        os.remove(tmp)
    if tr: rows.append(f"{f[:-5]}\t{st.median(tr):.1f}\t{st.median(tw):.1f}")
open(out, 'w').write('\n'.join(rows) + '\n')
print('\n'.join(rows))
