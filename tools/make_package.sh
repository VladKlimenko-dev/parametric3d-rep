#!/usr/bin/env bash
# KRN-11 (11.8): САМОДОСТАТОЧНАЯ ПОСТАВКА ПРИЛОЖЕНИЯ И ЯДРА. Запуск из корня Parametric3D:
#   bash tools/make_package.sh <каталог вывода> [RID]      (RID по умолчанию — среда выполнения стенда, напр. ubuntu.24.04-x64)
# Собирает приложение со средой выполнения .NET внутри (--self-contained: на машине пользователя .NET не нужен), кладёт
# рядом исходники ядра и приложения, документы и манифест выпуска (release_manifest/), пакует в один архив и проверяет:
# приложение из распакованного архива запускается БЕЗ установленного .NET (чистое окружение, env -i), открывает пробную
# деталь и рисует кадр OpenGL (под Xvfb, если нет экрана); затем кнопка «Тест», раздел «Отмена и отказ».
# Без сети: пакеты — из -p:UiPackages и локального кэша NuGet (среда выполнения для RID должна быть в кэше).
set -uo pipefail
OUT="${1:?каталог вывода}"; ROOT=$(pwd)
RID="${2:-$(dotnet --info 2>/dev/null | awk '/RID:/{print $2; exit}')}"
UIPKG="${UIPKG:-/home/claude/uipkgs/nupkgs}"
mkdir -p "$OUT"; OUT=$(cd "$OUT" && pwd)
STAGE="$OUT/Parametric3D-$RID"; rm -rf "$STAGE"; mkdir -p "$STAGE/app"
export AVALONIA_TELEMETRY_OPTOUT=1
# k133 (11.8, 11.6): RID «portable» — переносная поставка для Windows и Linux x64 (нужен установленный .NET 8): одна сборка
# приложения, машинные библиотеки обеих платформ (runtimes/win-x64, runtimes/linux-x64), запуск — Parametric3D.cmd
# (Windows) или Parametric3D.sh. Среда выполнения win-x64 для самодостаточной поставки Windows в автономном кэше NuGet
# отсутствует — поэтому Windows получает переносную.
if [ "$RID" = "portable" ]; then
  dotnet publish src/Parametric3D.csproj -c Release --self-contained false -p:UseAppHost=false -o "$STAGE/app" \
    -p:LocalDeps="$ROOT/tools/deps" -p:UiPackages="$UIPKG" > "$OUT/publish.log" 2>&1 || { echo "PACKAGE: publish failed (см. $OUT/publish.log)"; exit 1; }
  printf '@echo off\r\nrem Parametric3D: переносная поставка (нужен .NET 8 Desktop Runtime x64)\r\ndotnet "%%~dp0app\\Parametric3D.dll" %%*\r\n' > "$STAGE/Parametric3D.cmd"
  printf '@echo off\r\nrem Кнопка «Тест» без окна: отчёт в Документы\\Parametric3D\\Tests\r\ndotnet "%%~dp0app\\Parametric3D.dll" --selftest %%*\r\npause\r\n' > "$STAGE/Parametric3D_Test.cmd"
  printf '#!/bin/sh\nexec dotnet "$(dirname "$0")/app/Parametric3D.dll" "$@"\n' > "$STAGE/Parametric3D.sh"; chmod +x "$STAGE/Parametric3D.sh"
  # только две платформы поставки; отладочные символы машинных библиотек не нужны
  find "$STAGE/app/runtimes" -mindepth 1 -maxdepth 1 -type d ! -name win-x64 ! -name linux-x64 -exec rm -rf {} +
  find "$STAGE/app/runtimes" -name '*.pdb' -delete
  for n in libSkiaSharp.dll libHarfBuzzSharp.dll av_libglesv2.dll; do
    [ -f "$STAGE/app/runtimes/win-x64/native/$n" ] || { echo "PACKAGE: нет runtimes/win-x64/native/$n"; exit 1; }
  done
  echo "PORTABLE: машинные библиотеки win-x64: $(ls "$STAGE/app/runtimes/win-x64/native" | tr '\n' ' ')"
else
dotnet publish src/Parametric3D.csproj -c Release -r "$RID" --self-contained true -o "$STAGE/app" \
  -p:LocalDeps="$ROOT/tools/deps" -p:UiPackages="$UIPKG" > "$OUT/publish.log" 2>&1 || { echo "PACKAGE: publish failed (см. $OUT/publish.log)"; exit 1; }
fi
# исходники ядра и приложения, проверки, инструменты, документы; манифест выпуска
mkdir -p "$STAGE/source"
tar -C "$ROOT" --exclude='*/bin' --exclude='*/obj' --exclude='logs' -cf - src tests tools release_manifest *.md *.tsv 2>/dev/null | tar -C "$STAGE/source" -xf -
cp src/docs/release/KNOWN_LIMITATIONS.md "$STAGE/" 2>/dev/null
cat > "$STAGE/README.txt" <<EOF
Parametric3D — самодостаточная поставка ($RID)
app/Parametric3D      — приложение (среда выполнения .NET внутри; установка .NET не нужна)
source/               — исходники ядра и приложения, проверки, инструменты, манифест выпуска (release_manifest/)
Запуск: app/Parametric3D [файл детали] [--sample]; кнопка «Тест» в приложении — отчёт в Документы/Parametric3D/Tests.
EOF
( cd "$OUT" && rm -f "Parametric3D-$RID.tar.gz" && tar -czf "Parametric3D-$RID.tar.gz" "Parametric3D-$RID" )
[ "$RID" = "portable" ] && ( cd "$OUT" && rm -f "Parametric3D-$RID.zip" && zip -qr "Parametric3D-$RID.zip" "Parametric3D-$RID" && echo "ZIP: $(du -h "Parametric3D-$RID.zip" | cut -f1) $OUT/Parametric3D-$RID.zip" )
echo "PACKAGE: $(du -h "$OUT/Parametric3D-$RID.tar.gz" | cut -f1) $OUT/Parametric3D-$RID.tar.gz"
( cd "$STAGE/app" && find . -type f | LC_ALL=C sort | xargs -d '\n' sha256sum ) > "$OUT/Parametric3D-$RID.app.sha256"
echo "APP-SHA256: $(sha256sum "$OUT/Parametric3D-$RID.app.sha256" | cut -c1-16) ($(wc -l < "$OUT/Parametric3D-$RID.app.sha256") файлов)"
# проверка из распакованного архива в чистом окружении
CHK="$OUT/check"; rm -rf "$CHK"; mkdir -p "$CHK/home"; tar -C "$CHK" -xzf "$OUT/Parametric3D-$RID.tar.gz"
APP="$CHK/Parametric3D-$RID/app/Parametric3D"
DOTNET_ROOT_DIR=$(dirname "$(readlink -f "$(command -v dotnet)")")
[ "$RID" = "portable" ] && APP="$CHK/Parametric3D-$RID/Parametric3D.sh"
EXTRA_PATH=""; [ "$RID" = "portable" ] && EXTRA_PATH=":$DOTNET_ROOT_DIR"
XP=""; DISP="${DISPLAY:-}"
if [ -z "$DISP" ] && command -v Xvfb >/dev/null; then DN=$((100 + RANDOM % 800)); Xvfb :$DN -screen 0 1600x1000x24 >/dev/null 2>&1 & XP=$!; DISP=:$DN; sleep 1; fi
( cd "$CHK" && env -i HOME="$CHK/home" DISPLAY="$DISP" PATH=/usr/bin:/bin$EXTRA_PATH timeout 120 "$APP" --sample --shot "$CHK/shot.png" > "$CHK/run.log" 2>&1 )
echo "SMOKE: $(grep -m1 SHOT "$CHK/run.log" || echo 'нет кадра — см. '"$CHK/run.log")"
( cd "$CHK" && env -i HOME="$CHK/home" DISPLAY="$DISP" PATH=/usr/bin:/bin$EXTRA_PATH P3D_SELFTEST_ONLY=cancel timeout 900 "$APP" --selftest > "$CHK/selftest.log" 2>&1 )
REP=$(awk '/^REPORT /{print $2}' "$CHK/selftest.log"); [ -n "$REP" ] && cp "$REP" "$OUT/selftest_cancel.txt"
echo "SELFTEST: $( [ -n "$REP" ] && grep -c 'да' "$OUT/selftest_cancel.txt" ) строк «да»; $( [ -n "$REP" ] && grep -c 'НЕТ\|ОШИБКА' "$OUT/selftest_cancel.txt" ) замечаний"
[ -n "$XP" ] && kill $XP 2>/dev/null
exit 0
