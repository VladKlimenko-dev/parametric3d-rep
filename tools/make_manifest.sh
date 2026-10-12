#!/usr/bin/env bash
# KRN-00: манифест выпуска. Запуск из корня Parametric3D:  bash tools/make_manifest.sh <путь к K01Checks>
# Пишет release_manifest/: RELEASE_ID.txt (хеш дерева src+tests+tools), FILES.txt, SHA256SUMS.txt,
# DEPENDENCIES.txt (пакеты, SDK, среда выполнения), SETTINGS.json (эффективные настройки ядра от стенда).
set -euo pipefail
root=$(pwd); out=release_manifest; k01=${1:-}
mkdir -p "$out"
# Учитываемые файлы: исходники, проекты, ресурсы, документы, проверки, скрипты; без результатов сборки.
find src tests tools -type f \( -name '*.cs' -o -name '*.csproj' -o -name '*.slnx' -o -name '*.resx' -o -name '*.md' \
     -o -name '*.sh' -o -name '*.py' -o -name '*.json' -o -name '*.txt' -o -name '*.glsl' -o -name '*.ico' -o -name '*.png' \) \
     -not -path '*/bin/*' -not -path '*/obj/*' -not -path '*/TestResults/*' | LC_ALL=C sort > "$out/FILES.txt"
xargs -d '\n' sha256sum < "$out/FILES.txt" > "$out/SHA256SUMS.txt"
sha256sum "$out/SHA256SUMS.txt" | cut -c1-64 > "$out/RELEASE_ID.txt"
{
  echo "# Пакеты (из .csproj)"
  grep -ho 'PackageReference Include="[^"]*" Version="[^"]*"' $(grep -l PackageReference $(grep -E '\.csproj$' "$out/FILES.txt")) | sed -E 's/PackageReference Include="([^"]*)" Version="([^"]*)"/\1 \2/' | sort -u
  echo; echo "# Целевые платформы (из .csproj)"
  for p in $(grep -E '\.csproj$' "$out/FILES.txt"); do echo "$p: $(grep -o '<TargetFramework>[^<]*' "$p" | sed 's/<TargetFramework>//')"; done
  echo; echo "# Инструменты"
  echo "dotnet SDK $(dotnet --version 2>/dev/null || echo 'не найден')"
  echo "ОС сборки стенда: $(uname -srm)"
} > "$out/DEPENDENCIES.txt"
if [ -n "$k01" ] && [ -x "$k01" ]; then DOTNET_TieredCompilation=0 "$k01" settings | sed -n '/^{$/,$p' > "$out/SETTINGS.json"; fi
echo "RELEASE_ID $(cat $out/RELEASE_ID.txt); файлов $(wc -l < $out/FILES.txt)"
