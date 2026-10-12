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
# Ворота g.14: независимая проверка точного STEP другим ядром (OpenCascade, пакет cadquery-ocp; без него — пропуск с пометкой)
if python3 -c "import OCP" 2>/dev/null; then
  "$OUT/k01/K01Checks" g14dump "$OUT/g14" > "$OUT/g14_dump.log" 2>&1
  python3 "$ROOT/tools/occt_check.py" "$OUT/g14" --json "$OUT/g14/occt.json" > "$OUT/g14.log" 2>&1
  echo "G14: $(tail -1 "$OUT/g14.log")"
  # Ворота g.05 (KRN-04): каталог событий пересечения (узкие интервалы, касания, вершины, швы, петли, полюса) — OpenCascade
  "$OUT/k01/K01Checks" krn04dump "$OUT/krn04" > "$OUT/krn04_dump.log" 2>&1
  python3 "$ROOT/tools/occt_diff.py" "$OUT/krn04" --json "$OUT/krn04/occt.json" > "$OUT/krn04.log" 2>&1
  echo "KRN04: $(tail -1 "$OUT/krn04.log")"
else echo "G14: SKIPPED (OCP not installed: pip install cadquery-ocp)"; fi
bash tools/make_manifest.sh "$OUT/k01/K01Checks"
