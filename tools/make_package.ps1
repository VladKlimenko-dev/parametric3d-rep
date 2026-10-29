# KRN-11 (11.6, 11.8, ворота g.15): ПЕРЕНОСНАЯ ПОСТАВКА ПОД WINDOWS — то же, что `tools/make_package.sh <вывод> portable`.
# Запуск из корня репозитория в PowerShell:
#   powershell -ExecutionPolicy Bypass -File tools\make_package.ps1 [-Out C:\P3D_pkg] [-NoTest]
# Делает две сборки подряд и сравнивает хеши файлов приложения (воспроизводимость до бита), пакует zip, распаковывает в
# отдельную папку и запускает из неё «Тест» (отчёт — Документы\Parametric3D\Tests). Нужен .NET 8 SDK.
param([string]$Out = "C:\P3D_pkg", [switch]$NoTest)
$ErrorActionPreference = "Stop"
$root = (Get-Location).Path
foreach ($n in 1, 2) {
  $app = "$Out\build$n\Parametric3D-portable\app"
  Remove-Item "$Out\build$n" -Recurse -Force -ErrorAction SilentlyContinue
  dotnet publish "$root\src\Parametric3D.csproj" -c Release --self-contained false -p:UseAppHost=false -o $app
  if ($LASTEXITCODE -ne 0) { throw "publish failed" }
  Get-ChildItem "$app\runtimes" -Directory | Where-Object { $_.Name -notin 'win-x64', 'linux-x64' } | Remove-Item -Recurse -Force
  Get-ChildItem "$app\runtimes" -Recurse -Filter *.pdb | Remove-Item -Force
  Set-Content "$Out\build$n\Parametric3D-portable\Parametric3D.cmd" "@echo off`r`ndotnet `"%~dp0app\Parametric3D.dll`" %*" -Encoding ascii
  Set-Content "$Out\build$n\Parametric3D-portable\Parametric3D_Test.cmd" "@echo off`r`nchcp 65001 >nul`r`ndotnet `"%~dp0app\Parametric3D.dll`" --selftest %*`r`npause" -Encoding ascii
}
function Hashes($dir) { Get-ChildItem $dir -Recurse -File | Sort-Object FullName | ForEach-Object { "{0} {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash, $_.FullName.Substring($dir.Length) } }
$h1 = Hashes "$Out\build1\Parametric3D-portable\app"; $h2 = Hashes "$Out\build2\Parametric3D-portable\app"
$h1 | Set-Content "$Out\app1.sha256"; $h2 | Set-Content "$Out\app2.sha256"
if (Compare-Object $h1 $h2) { "REPRO: DIFFERENT"; Compare-Object $h1 $h2 } else { "REPRO: SAME ($($h1.Count) files)" }
Compress-Archive -Path "$Out\build1\Parametric3D-portable" -DestinationPath "$Out\Parametric3D-portable.zip" -Force
Remove-Item "$Out\check" -Recurse -Force -ErrorAction SilentlyContinue
Expand-Archive "$Out\Parametric3D-portable.zip" -DestinationPath "$Out\check"
"ZIP: $Out\Parametric3D-portable.zip"
if (-not $NoTest) { & "$Out\check\Parametric3D-portable\Parametric3D_Test.cmd" }
