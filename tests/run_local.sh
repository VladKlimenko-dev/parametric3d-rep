#!/usr/bin/env bash
# Сборка проверочных программ наборов A06. Ошибка сборки любого проекта — ненулевой код возврата,
# и прежние двоичные файлы удаляются заранее (не запускать старую сборку под видом новой).
# Использование: run_local.sh <src> <out> [LocalDeps]
set -uo pipefail
SRC="$1"; OUT="$2"; DEPS="${3:-}"; mkdir -p "$OUT"
export DOTNET_TieredCompilation=0
H="$(cd "$(dirname "$0")" && pwd)"
EXTRA=(); [ -n "$DEPS" ] && EXTRA+=("-p:LocalDeps=$DEPS")
fail=0
for p in fillet:FilletChecks a01:FixChecks a03:ExactChecks a04:CylinderChecks speed:Speed; do
  d=${p%%:*}; n=${p##*:}
  rm -rf "$OUT/bin-$d"
  if ! dotnet build "$H/$d/$n.csproj" -c Release -p:SourceRoot="$SRC" -p:DefineConstants=CONTACT_CHECKS "${EXTRA[@]}" -o "$OUT/bin-$d" > "$OUT/build-$d.log" 2>&1; then
    echo "BUILD FAIL $d"; tail -20 "$OUT/build-$d.log"; fail=1
  fi
done
exit $fail
