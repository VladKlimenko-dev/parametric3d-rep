# Parametric3D: регрессия ядра и приложения на этом компьютере (k156).
# Запуск: двойной щелчок по regress.bat (рядом) или в PowerShell: .\regress.ps1
#   -Parallel   — этапы ядра одновременно (allB, тестовые модели, allA, корпус): быстрее на 8+ ядрах, нужно 16+ ГБ памяти
#   -Only allB  — один этап: allB, fix, allA, seq, ui
#   -NoPull     — без обновления с GitHub
# Итог — logs\regress_<дата_время>\SUMMARY.txt: число пройденных и проваленных проверок по этапам, имена провалов.
# Этот файл (или всю папку логов zip-архивом) пришлите в чат.

param([switch]$Parallel, [string]$Only = '', [switch]$NoPull)

$ErrorActionPreference = 'Stop'
Set-Location -Path $PSScriptRoot
function Fail($msg) { Write-Host $msg -ForegroundColor Red; Read-Host 'Нажмите Enter, чтобы закрыть'; exit 1 }
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { Fail 'dotnet не найден. Установите .NET 8 SDK: https://dotnet.microsoft.com/download' }

if (-not $NoPull -and (Test-Path .git) -and (Get-Command git -ErrorAction SilentlyContinue)) {
    if (-not (git status --porcelain --untracked-files=no)) { Write-Host 'Обновление с GitHub…' -ForegroundColor Cyan; git pull --ff-only | Out-Host }
}
$ver = if ((Test-Path .git) -and (Get-Command git -ErrorAction SilentlyContinue)) { git log -1 --format='%h %ad %s' --date=short } else { '' }
# k159: архив с GitHub (без .git) — версия по первому разделу CHANGELOG
if (-not $ver -and (Test-Path 'CHANGELOG_K00_K10.md')) { $ver = (Select-String -Path 'CHANGELOG_K00_K10.md' -Pattern '^## (k\d+[^:]*)' | Select-Object -First 1).Matches[0].Groups[1].Value + ' (архив)' }
Write-Host "Версия: $ver" -ForegroundColor Green

$stamp = Get-Date -Format 'yyyyMMdd_HHmm'
$logs = Join-Path $PSScriptRoot "logs\regress_$stamp"
New-Item -ItemType Directory -Force -Path $logs | Out-Null
$out = Join-Path $env:TEMP "p3d_regress_build"
$k01 = Join-Path $out 'k01'; $ui = Join-Path $out 'ui'

Write-Host 'Сборка проверок ядра…' -ForegroundColor Cyan
dotnet build tests\k01\K01Checks.csproj -c Release -o $k01 -v q --nologo | Out-File (Join-Path $logs 'build_k01.log')
if ($LASTEXITCODE -ne 0) { Fail "Сборка проверок ядра не удалась — см. $logs\build_k01.log" }
if ($Only -eq '' -or $Only -eq 'ui') {
    Write-Host 'Сборка проверок приложения…' -ForegroundColor Cyan
    dotnet build tests\ui\UiChecks.csproj -c Release -o $ui -v q --nologo | Out-File (Join-Path $logs 'build_ui.log')
    if ($LASTEXITCODE -ne 0) { Fail "Сборка проверок приложения не удалась — см. $logs\build_ui.log" }
}

$env:DOTNET_TieredCompilation = '0'
$env:P3D_USERMODELS = (Resolve-Path 'tests\ui\data').Path
$exe = Join-Path $k01 'K01Checks.exe'

# этапы ядра: имя, аргументы
$stages = @(
    @{ Name = 'allB'; Args = @('allB') },
    @{ Name = 'fix';  Args = @('loftcorpus140') },
    @{ Name = 'allA'; Args = @('allA') },
    @{ Name = 'seq';  Args = @('loftseq', '10', '101', '6', (Join-Path $logs 'seq')) }
) | Where-Object { $Only -eq '' -or $_.Name -eq $Only }

$t0 = Get-Date
if ($Parallel) {
    $procs = foreach ($s in $stages) {
        if ($s.Name -eq 'seq') { New-Item -ItemType Directory -Force -Path (Join-Path $logs 'seq') | Out-Null }
        Write-Host "Запуск $($s.Name)…" -ForegroundColor Cyan
        Start-Process -FilePath $exe -ArgumentList $s.Args -NoNewWindow -PassThru `
            -RedirectStandardOutput (Join-Path $logs "$($s.Name).log") -RedirectStandardError (Join-Path $logs "$($s.Name).err")
    }
    $procs | Wait-Process
} else {
    foreach ($s in $stages) {
        if ($s.Name -eq 'seq') { New-Item -ItemType Directory -Force -Path (Join-Path $logs 'seq') | Out-Null }
        $ts = Get-Date
        Write-Host "$($s.Name)… (начало $($ts.ToString('HH:mm')))" -ForegroundColor Cyan
        Start-Process -FilePath $exe -ArgumentList $s.Args -NoNewWindow -Wait `
            -RedirectStandardOutput (Join-Path $logs "$($s.Name).log") -RedirectStandardError (Join-Path $logs "$($s.Name).err")
        Write-Host ("  {0}: {1:0} мин" -f $s.Name, ((Get-Date) - $ts).TotalMinutes)
    }
}

# проверки приложения — тремя частями (по 50), без окна
if ($Only -eq '' -or $Only -eq 'ui') {
    $uiExe = Join-Path $ui 'Parametric3D.UiChecks.exe'
    $names = Select-String -Path 'tests\ui\Program.cs' -Pattern '^Check\("(UI-[A-Za-z0-9-]+)"' | ForEach-Object { $_.Matches[0].Groups[1].Value } | Where-Object { $_ -ne 'UI-DIAG-OPEN' }
    $parts = [Math]::Ceiling($names.Count / 50)
    for ($i = 0; $i -lt $parts; $i++) {
        $chunk = ($names | Select-Object -Skip ($i * 50) -First 50) -join '|'
        Write-Host "Проверки приложения, часть $($i + 1) из $parts…" -ForegroundColor Cyan
        Start-Process -FilePath $uiExe -ArgumentList "`"$chunk`"" -NoNewWindow -Wait `
            -RedirectStandardOutput (Join-Path $logs "ui$i.log") -RedirectStandardError (Join-Path $logs "ui$i.err")
    }
}

# итог
$sum = New-Object System.Collections.Generic.List[string]
$sum.Add("Parametric3D — регрессия на $env:COMPUTERNAME ($([Environment]::ProcessorCount) ядер), $((Get-Date).ToString('yyyy-MM-dd HH:mm'))")
$sum.Add("Версия: $ver")
$sum.Add(("Время: {0:0} мин" -f ((Get-Date) - $t0).TotalMinutes))
foreach ($f in Get-ChildItem $logs -Filter '*.log' | Where-Object { $_.Name -notlike 'build_*' -and $_.Name -ne 'seq.log' }) {
    $text = Get-Content $f.FullName
    $total = ($text | Select-String -Pattern '^\{"passed":\d+,"failed":\d+\}' | Select-Object -Last 1)
    # k159: ожидаемый отказ (случаи корпуса «overflow», отметка expected) — не провал
    $fails = $text | Select-String -Pattern '"(ok|Ok)":false' | Where-Object { $_.Line -notmatch '"expected":"refusal"' } | ForEach-Object { $_.Line.Substring(0, [Math]::Min(300, $_.Line.Length)) }
    $sum.Add("")
    $sum.Add("== $($f.BaseName): $(if ($total) { $total.Line } else { '(итога нет — этап прерван?)' })")
    foreach ($x in $fails) { $sum.Add("  ПРОВАЛ: $x") }
}
$seq = Join-Path $logs 'seq.log'
if (Test-Path $seq) {
    $exact = (Select-String -Path $seq -Pattern '"guarantee":"ExactAnalytic"').Count
    $ops = (Select-String -Path $seq -Pattern '"tag":').Count
    $sum.Add("")
    $sum.Add("== seq (корпус лофтов): операций $ops, точных $exact (k152–k155: 60 операций, 28 точных)")
}
$sumPath = Join-Path $logs 'SUMMARY.txt'
$sum | Out-File -FilePath $sumPath -Encoding UTF8
Write-Host ''
$sum | ForEach-Object { Write-Host $_ }
Write-Host ''
Write-Host "Итог: $sumPath — пришлите его в чат." -ForegroundColor Green
Read-Host 'Нажмите Enter, чтобы закрыть'
