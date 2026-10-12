#!/bin/bash
# Попеременный замер: базовая сборка и текущая, ROUNDS раз по сценарию; печатает медианы и p95 по объединённым замерам.
# Использование (BASE_BIN, CUR_BIN — каталоги сборок K01Checks): perf_ab.sh <сценарий> [ROUNDS] [PERF_RUNS]
sc=$1; R=${2:-3}; N=${3:-5}
for i in $(seq $R); do
  for v in base cur; do
    bin=${CUR_BIN:-out/k01}; [ $v = base ] && bin=${BASE_BIN:-out/k01_base}
    PERF_RUNS=$N DOTNET_TieredCompilation=0 $bin/K01Checks perf $sc 2>/dev/null | grep '^{"perf"' | sed "s/^{/{\"v\":\"$v\",/"
  done
done | python3 -c "
import sys,json
S={'base':[], 'cur':[]}; A={'base':[], 'cur':[]}
for l in sys.stdin:
    d=json.loads(l); S[d['v']]+= d['samples']; A[d['v']].append(d['medianAllocMB'])
def q(x,p): x=sorted(x); import math; return x[min(len(x)-1, math.ceil(p*len(x))-1)]
for v in ['base','cur']:
    if S[v]: print(f'$sc {v:4s}: n={len(S[v])} median {q(S[v],.5):8.1f} ms  p95 {q(S[v],.95):8.1f} ms  alloc {sorted(A[v])[len(A[v])//2]} MB')
if S['base'] and S['cur']: print(f'$sc ratio median {q(S[\"cur\"],.5)/q(S[\"base\"],.5):.3f}  p95 {q(S[\"cur\"],.95)/q(S[\"base\"],.95):.3f}')
"
