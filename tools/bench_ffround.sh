#!/bin/bash
# скорость FreeForm: цилиндр ∪ шар, три операции × N повторов; минимум по повторам
cd /home/claude/out/ui3 && P3D_PROBEREPS=${1:-3} timeout 1800 ./Parametric3D.UiChecks probe-ffround 2>/dev/null | grep PROBE | python3 -c "
import sys,re,collections
d=collections.defaultdict(list); f=collections.defaultdict(list)
for l in sys.stdin:
    m=re.match(r'PROBE (.+?): (\d+) ms, feature (\S+) ms',l)
    if m: d[m.group(1)].append(int(m.group(2))); f[m.group(1)].append(float(m.group(3)))
for k in d: print(f'{k:22s} total min {min(d[k]):5d} ms   feature min {min(f[k]):7.0f} ms   (n={len(d[k])})')
"
