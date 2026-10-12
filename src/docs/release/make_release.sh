#!/usr/bin/env bash
# K10. Reproducible source release: a sorted file list with SHA-256 sums, the dependency list
# and the toolchain used. Usage: docs/release/make_release.sh <source-root> <out-dir>
set -euo pipefail
SRC="$(cd "$1" && pwd)"; OUT="$2"; mkdir -p "$OUT"
cd "$SRC"
# Only sources and docs; build outputs and IDE state are excluded.
find . -type f \( -name '*.cs' -o -name '*.csproj' -o -name '*.slnx' -o -name '*.md' -o -name '*.resx' -o -name '*.json' -o -name '*.sh' -o -name '*.py' \) \
     -not -path './bin/*' -not -path './obj/*' -not -path '*/bin/*' -not -path '*/obj/*' -not -path './.vs/*' \
  | LC_ALL=C sort > "$OUT/FILES.txt"
xargs -a "$OUT/FILES.txt" -d '\n' sha256sum > "$OUT/SHA256SUMS.txt"
{
  echo "# Dependencies"
  grep -rh "PackageReference" --include=*.csproj . | sed -E 's/.*Include="([^"]+)".*Version="([^"]+)".*/\1 \2/' | LC_ALL=C sort -u
  echo
  echo "# Toolchain"
  (dotnet --version 2>/dev/null | sed 's/^/dotnet SDK /') || echo "dotnet SDK: not available"
  grep -h "TargetFramework" Parametric3D.csproj | sed -E 's/.*<TargetFramework>(.*)<\/TargetFramework>.*/target \1/'
} > "$OUT/DEPENDENCIES.txt"
echo "files: $(wc -l < "$OUT/FILES.txt")"
sha256sum "$OUT/SHA256SUMS.txt" | cut -d' ' -f1 > "$OUT/RELEASE_ID.txt"
echo "release id: $(cat "$OUT/RELEASE_ID.txt")"
