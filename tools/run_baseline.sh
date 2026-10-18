#!/usr/bin/env bash
# KRN-00: полный базовый прогон одной ревизии. Запуск из корня Parametric3D:  bash tools/run_baseline.sh <каталог вывода>
# Пишет журналы наборов и манифест; итог — строки «набор: passed/failed».
set -uo pipefail
OUT="${1:?каталог вывода}"; ROOT=$(pwd); mkdir -p "$OUT"
export DOTNET_TieredCompilation=0
bash tools/build_checks.sh "$OUT" || exit 1
for s in allA allB allS; do "$OUT/k01/K01Checks" $s > "$OUT/$s.log" 2>&1; echo "$s: $(tail -1 "$OUT/$s.log")"; done
# Ворота g.11: сетка показа многогранных результатов многообразна во float (случаи разностных серий 8, 9)
"$OUT/k01/K01Checks" g11poly > "$OUT/g11poly.log" 2>&1; echo "G11POLY: $(tail -1 "$OUT/g11poly.log")"
"$OUT/fprobe/FProbe" rotations > "$OUT/fprobe.log" 2>&1; echo "FProbe: $(tail -1 "$OUT/fprobe.log")"
bash tests/run_local.sh "$ROOT/src" "$OUT/a06" "$ROOT/tools/deps" > "$OUT/a06_build.log" 2>&1 && bash tests/run_suites.sh "$OUT/a06" > "$OUT/a06_summary.txt" 2>&1
echo "A06: $(grep -c 'rc=0' "$OUT/a06_summary.txt")/9"
# g.13: серверы сборки (компилятор, MSBuild) после сборок не нужны — замеры времени дальше идут без них
dotnet build-server shutdown > /dev/null 2>&1
# Ворота g.14: независимая проверка точного STEP другим ядром (OpenCascade, пакет cadquery-ocp; без него — пропуск с пометкой)
if python3 -c "import OCP" 2>/dev/null; then
  "$OUT/k01/K01Checks" g14dump "$OUT/g14" > "$OUT/g14_dump.log" 2>&1
  python3 "$ROOT/tools/occt_check.py" "$OUT/g14" --json "$OUT/g14/occt.json" > "$OUT/g14.log" 2>&1
  echo "G14: $(tail -1 "$OUT/g14.log")"
  # Матрица m.q2: объём, центр масс и тензор инерции с границей ошибки — сверка с OpenCascade (VolumeProperties)
  "$OUT/k01/K01Checks" q2dump "$OUT/q2" > "$OUT/q2_dump.log" 2>&1
  python3 "$ROOT/tools/occt_mass.py" "$OUT/q2" --json "$OUT/q2/occt.json" > "$OUT/q2.log" 2>&1
  echo "Q2MASS: $(tail -1 "$OUT/q2.log")"
  # Матрица m.x2: IGES тел каталога g.14 — чтение OpenCascade (сшивка, объём)
  "$OUT/k01/K01Checks" igesdump "$OUT/iges" > "$OUT/iges_dump.log" 2>&1
  python3 "$ROOT/tools/occt_iges.py" "$OUT/iges" --json "$OUT/iges/occt.json" > "$OUT/iges.log" 2>&1
  echo "IGES: $(tail -1 "$OUT/iges.log")"
  # Ворота g.05 (KRN-04): каталог событий пересечения (узкие интервалы, касания, вершины, швы, петли, полюса) — OpenCascade
  "$OUT/k01/K01Checks" krn04dump "$OUT/krn04" > "$OUT/krn04_dump.log" 2>&1
  python3 "$ROOT/tools/occt_diff.py" "$OUT/krn04" --json "$OUT/krn04/occt.json" > "$OUT/krn04.log" 2>&1
  echo "KRN04: $(tail -1 "$OUT/krn04.log")"
  # KRN-11 (11.1): разностные серии 8–10 (случайные цепочки 1–2 булевых над примитивами; OpenCascade + арбитр) — часть
  # базового прогона (все сохранённые сценарии в конфигурации выпуска). P3D_BASELINE_SERIES=0 — пропуск.
  if [ "${P3D_BASELINE_SERIES:-1}" != "0" ]; then
    for seed in 8 9 10; do
      if [ "$seed" = "8" ]; then export DIFFPROF="$OUT/d8/prof.tsv"; else unset DIFFPROF; fi
      timeout 4000 "$OUT/k01/K01Checks" diffdump "$OUT/d$seed" "$seed" 200 > /dev/null 2>&1
      timeout 6000 python3 "$ROOT/tools/occt_diff.py" "$OUT/d$seed" --json "$OUT/d$seed/r.json" > "$OUT/diff$seed.txt" 2>&1
      echo "DIFF$seed: $(tail -1 "$OUT/diff$seed.txt")"
    done
    unset DIFFPROF
    # g.13: OpenCascade на тех же операндах серии 8 (время), затем замеры деталей и сравнение с целями
    timeout 1200 python3 "$ROOT/tools/occt_time.py" "$OUT/d8" "$OUT/d8/occt_time.tsv" > /dev/null 2>&1
    echo "DIFF exact: $(cat "$OUT"/d8/manifest.tsv "$OUT"/d9/manifest.tsv "$OUT"/d10/manifest.tsv 2>/dev/null | grep -o ExactAnalytic | wc -l)"
  fi
else echo "G14: SKIPPED (OCP not installed: pip install cadquery-ocp)"; fi
# g.13 / m.v1 / m.v2: замеры по целям пользователя (один процесс, после всех наборов) и сравнение с целями
timeout 1800 "$OUT/k01/K01Checks" perftargets "$OUT/perf.jsonl" > "$OUT/perf_run.log" 2>&1
python3 -c "import OCP" 2>/dev/null && timeout 1200 python3 "$ROOT/tools/occt_io_time.py" "$OUT/io" "$OUT/io/occt_io.tsv" > /dev/null 2>&1
python3 "$ROOT/tools/perf_targets.py" "$OUT" > "$OUT/perf.txt" 2>&1; echo "PERF: $(tail -1 "$OUT/perf.txt")"
bash tools/make_manifest.sh "$OUT/k01/K01Checks"
