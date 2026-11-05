# Parametric3D: цель Я1 — зависит ли шаг от истории сеанса (BACKLOG k149, корпус s106; REFERENCE_SOLUTIONS, п. 6).
#
# Что делает:
#   1. Собирает стенд K01Checks (как regress.ps1).
#   2. Прогоняет последовательный корпус с проверкой «шаг в сеансе = тот же шаг из файла без тел» (SEQ_REBUILDCHECK=1):
#      модель s106 отдельно и весь корпус s101–s110 одним процессом.
#   3. Повторяет прогон, выключая по одной памяти ядра (мутации P3D_*=0). Если с выключенной памятью расхождение
#      пропадает — причина в ней.
#   4. Пишет отчёт K01Checks globals: изменяемые глобальные поля и коллекции в полях static readonly.
#
# Запуск: в PowerShell из корня клона:  .\tools\seq_state_probe.ps1
#   -Full    — мутации на всём корпусе (дольше; нужно, если s106 отдельно расхождения не даёт)
#   -NoPull  — без обновления с GitHub
# Итог — logs\stateprobe_<дата_время>\SUMMARY.txt. Пришлите его в чат (или всю папку zip-архивом).
#
# Без PowerShell (Linux), вручную:
#   dotnet build tests/k01/K01Checks.csproj -c Release -o out/k01 -p:LocalDeps=tools/deps
#   SEQ_REBUILDCHECK=1 DOTNET_TieredCompilation=0 out/k01/K01Checks loftseq 1 106 6 out/s106
#   SEQ_REBUILDCHECK=1 DOTNET_TieredCompilation=0 P3D_EXACTINCACHE=0 out/k01/K01Checks loftseq 1 106 6 out/s106_exactin

param([switch]$Full, [switch]$NoPull)

$ErrorActionPreference = 'Stop'
Set-Location -Path (Split-Path -Parent $PSScriptRoot)
function Fail($msg) { Write-Host $msg -ForegroundColor Red; Read-Host 'Нажмите Enter, чтобы закрыть'; exit 1 }
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { Fail 'dotnet не найден. Установите .NET 8 SDK: https://dotnet.microsoft.com/download' }

if (-not $NoPull -and (Test-Path .git) -and (Get-Command git -ErrorAction SilentlyContinue)) {
    if (-not (git status --porcelain --untracked-files=no)) { Write-Host 'Обновление с GitHub…' -ForegroundColor Cyan; git pull --ff-only | Out-Host }
}
$ver = if ((Test-Path .git) -and (Get-Command git -ErrorAction SilentlyContinue)) { git log -1 --format='%h %ad %s' --date=short } else { '' }

$stamp = Get-Date -Format 'yyyyMMdd_HHmm'
$logs = Join-Path (Get-Location) "logs\stateprobe_$stamp"
New-Item -ItemType Directory -Force -Path $logs | Out-Null
$k01 = Join-Path $env:TEMP 'p3d_stateprobe_build\k01'

Write-Host 'Сборка проверок ядра…' -ForegroundColor Cyan
dotnet build tests\k01\K01Checks.csproj -c Release -o $k01 -v q --nologo | Out-File (Join-Path $logs 'build_k01.log')
if ($LASTEXITCODE -ne 0) { Fail "Сборка не удалась — см. $logs\build_k01.log" }
$exe = Join-Path $k01 'K01Checks.exe'

$env:DOTNET_TieredCompilation = '0'
$env:P3D_USERMODELS = (Resolve-Path 'tests\ui\data').Path
$env:SEQ_REBUILDCHECK = '1'

# памяти ядра, переживающие операцию (REFERENCE_SOLUTIONS, п. 6); у каждой — своя мутация
$mutations = @('P3D_EXACTINCACHE', 'P3D_FACEMESHMEMO', 'P3D_AREACACHE', 'P3D_VALCACHE', 'P3D_MESHVALMEMO',
               'P3D_AUDITCACHE', 'P3D_AUDITCHORDMEMO', 'P3D_MESHREUSE')

function Run($name, $loftArgs, $mutation) {
    $out = Join-Path $logs $name
    New-Item -ItemType Directory -Force -Path $out | Out-Null
    if ($mutation) { Set-Item -Path "env:$mutation" -Value '0' }
    $ts = Get-Date
    Write-Host ("{0}… (начало {1})" -f $name, $ts.ToString('HH:mm')) -ForegroundColor Cyan
    Start-Process -FilePath $exe -ArgumentList ($loftArgs + @($out)) -NoNewWindow -Wait `
        -RedirectStandardOutput (Join-Path $logs "$name.log") -RedirectStandardError (Join-Path $logs "$name.err")
    if ($mutation) { Remove-Item -Path "env:$mutation" }
    Write-Host ("  {0}: {1:0} мин" -f $name, ((Get-Date) - $ts).TotalMinutes)
}

$s106 = @('loftseq', '1', '106', '6')
$corpus = @('loftseq', '10', '101', '6')

Run 's106' $s106 $null
Run 'corpus' $corpus $null
foreach ($m in $mutations) {
    $short = $m.Substring(4).ToLower()
    if ($Full) { Run "corpus_$short" $corpus $m } else { Run "s106_$short" $s106 $m }
}

# отчёт об изменяемом глобальном состоянии
& $exe globals | Out-File (Join-Path $logs 'globals.txt')

# сводка: по каждому прогону — итог, число расхождений «сеанс ≠ файл», строки шага 5 модели s106
$sum = New-Object System.Collections.Generic.List[string]
$sum.Add("Parametric3D — Я1, состояние между операциями. Версия: $ver")
$sum.Add("Режим: " + ($(if ($Full) { 'мутации на всём корпусе' } else { 'мутации на модели s106' })))
$sum.Add('')
foreach ($log in Get-ChildItem -Path $logs -Filter '*.log' | Where-Object { $_.Name -ne 'build_k01.log' } | Sort-Object Name) {
    $name = [IO.Path]::GetFileNameWithoutExtension($log.Name)
    $lines = Get-Content $log.FullName
    $total = $lines | Where-Object { $_ -match '"ok":\d+,"bad"' } | Select-Object -Last 1
    $rc = $lines | Where-Object { $_ -match '"rebuildChecked"' } | Select-Object -Last 1
    $sum.Add("== $name")
    $sum.Add("   итог: $total")
    $sum.Add("   сеанс против файла: $rc")
    foreach ($l in $lines | Where-Object { $_ -match '"rebuildcheck"' }) { $sum.Add("   РАСХОЖДЕНИЕ: $l") }
    foreach ($l in $lines | Where-Object { $_ -match '"tag":"s106 step 5' }) { $sum.Add("   s106 шаг 5: $l") }
}
$sum.Add('')
$sum.Add('== коллекции в полях static readonly (общие на процесс памяти; опись G5 их не видит)')
foreach ($l in Get-Content (Join-Path $logs 'globals.txt') | Where-Object { $_ -like 'static-readonly-collection*' }) { $sum.Add("   $l") }
$sum | Out-File (Join-Path $logs 'SUMMARY.txt') -Encoding utf8

Write-Host ''
Write-Host "Готово. Итог: $logs\SUMMARY.txt — пришлите его в чат." -ForegroundColor Green
Read-Host 'Нажмите Enter, чтобы закрыть'
