#!/usr/bin/env bash
# Прогон наборов A06. Код возврата — ненулевой, если хоть один набор завершился с ошибкой
# или не нашёл свою программу.
set -uo pipefail
OUT="$1"; cd "$OUT"; export DOTNET_TieredCompilation=0
status=0
run(){ local name=$1; shift; local s=$(date +%s.%N); local rc=0
  if [ ! -x "$1" ]; then echo "$name MISSING $1"; status=1; return; fi
  "$@" > "$name.jsonl" 2> "$name.err" || rc=$?
  [ $rc -ne 0 ] && status=1
  echo "$name rc=$rc t=$(echo "$(date +%s.%N)-$s"|bc)s :: $(tail -c 300 $name.jsonl | tail -n1)"; }
run numerics ./bin-fillet/FilletChecks numerics
run rotations ./bin-fillet/FilletChecks rotations
run suite ./bin-fillet/FilletChecks suite
run target ./bin-fillet/FilletChecks target
run a01-tests ./bin-a01/FixChecks
run a01-corpus ./bin-a01/FixChecks corpus
run a01-sequences ./bin-a01/FixChecks sequences
run a03-hints ./bin-a03/ExactChecks
run a04-cylinder ./bin-a04/CylinderChecks
exit $status
