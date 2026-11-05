#!/usr/bin/env bash
# Сборка стенда K01Checks без сети (FProbe убран из репозитория — его в tests/ нет). Запуск из корня Parametric3D:  bash tools/build_checks.sh <каталог вывода>
# Прежние двоичные файлы удаляются: старая сборка не запускается под видом новой.
set -euo pipefail
OUT="${1:?каталог вывода}"; ROOT=$(pwd); DEPS="$ROOT/tools/deps"; mkdir -p "$OUT"
for p in k01:K01Checks; do
  d=${p%%:*}; n=${p##*:}; rm -rf "$OUT/$d"
  if ! dotnet build "tests/$d/$n.csproj" -c Release -o "$OUT/$d" -p:LocalDeps="$DEPS" > "$OUT/build-$d.log" 2>&1; then
    echo "BUILD FAIL $d"; grep -E " error " "$OUT/build-$d.log" | sort -u | head; exit 1
  fi
done
echo "built: $OUT/k01/K01Checks"
