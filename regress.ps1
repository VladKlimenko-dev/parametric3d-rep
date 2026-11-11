# Parametric3D: регрессия ядра и приложения на этом компьютере (k156).
# Запуск: двойной щелчок по regress.bat (рядом) или в PowerShell: .\regress.ps1
#   -Parallel   — этапы ядра одновременно (allB, тестовые модели, allA, корпус): быстрее на 8+ ядрах, нужно 16+ ГБ памяти
#   -Only abc   — allA, allB, allC; -Only thread — адресные проверки резьбы
#   -Only allB  — один этап: allA, allB, allC, thread, fix, seq, ui
#   -NoPull     — без обновления с GitHub
# Итог — logs\regress_<дата_время>\SUMMARY.txt: число пройденных и проваленных проверок по этапам, имена провалов.
# Этот файл (или всю папку логов zip-архивом) пришлите в чат.

param([switch]$Parallel,
    [ValidateSet('', 'allA', 'allB', 'allC', 'abc', 'thread', 'fix', 'seq', 'ui')]
    [string]$Only = '', [switch]$NoPull)

$ErrorActionPreference = 'Stop'
Set-Location -Path $PSScriptRoot
function Fail($msg) { Write-Host $msg -ForegroundColor Red; Read-Host 'Нажмите Enter, чтобы закрыть'; exit 1 }
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { Fail 'dotnet не найден. Установите .NET 8 SDK: https://dotnet.microsoft.com/download' }

if (-not $NoPull -and (Test-Path .git) -and (Get-Command git -ErrorAction SilentlyContinue)) {
    if (-not (git status --porcelain --untracked-files=no)) {
        Write-Host 'Обновление с GitHub…' -ForegroundColor Cyan
        git pull --ff-only | Out-Host
        if ($LASTEXITCODE -ne 0) { Fail 'git pull не выполнен; тестирование другой версии остановлено.' }
    }
}
$ver = if ((Test-Path .git) -and (Get-Command git -ErrorAction SilentlyContinue)) { git log -1 --format='%h %ad %s' --date=short } else { '' }
# k159: архив с GitHub (без .git) — версия по первому разделу CHANGELOG
if (-not $ver -and (Test-Path 'src/docs/release/CHANGELOG_K00_K10.md')) {
    $match = Select-String -Path 'src/docs/release/CHANGELOG_K00_K10.md' -Pattern '^## (k\d+[^:]*)' | Select-Object -First 1
    $ver = if ($match) { $match.Matches[0].Groups[1].Value + ' (архив)' } else { 'архив без Git SHA' }
}
Write-Host "Версия: $ver" -ForegroundColor Green

$stamp = Get-Date -Format 'yyyyMMdd_HHmmss'
$logs = Join-Path $PSScriptRoot "logs\regress_$stamp"
New-Item -ItemType Directory -Force -Path $logs | Out-Null
$out = Join-Path $env:TEMP "p3d_regress_build"
$k01 = Join-Path $out 'k01'; $ui = Join-Path $out 'ui'

Write-Host 'Сборка проверок ядра…' -ForegroundColor Cyan
dotnet build tests\k01\K01Checks.csproj -c Release -o $k01 -v q --nologo 2>&1 | Out-File (Join-Path $logs 'build_k01.log')
if ($LASTEXITCODE -ne 0) { Fail "Сборка проверок ядра не удалась — см. $logs\build_k01.log" }
if ($Only -eq '' -or $Only -eq 'ui') {
    Write-Host 'Сборка проверок приложения…' -ForegroundColor Cyan
dotnet build tests\ui\UiChecks.csproj -c Release -o $ui -v q --nologo 2>&1 | Out-File (Join-Path $logs 'build_ui.log')
    if ($LASTEXITCODE -ne 0) { Fail "Сборка проверок приложения не удалась — см. $logs\build_ui.log" }
}

$env:DOTNET_TieredCompilation = '0'
if (Test-Path 'tests\ui\data') { $env:P3D_USERMODELS = (Resolve-Path 'tests\ui\data').Path }
elseif ($Only -ne 'thread') { Write-Warning 'tests/ui/data отсутствует: прогон неполного checkout не квалифицирует полный корпус.' }
$env:P3D_THREADS = $PSScriptRoot
$env:P3D_ALLC_OUT = Join-Path $logs 'allC_models'
New-Item -ItemType Directory -Force -Path $env:P3D_ALLC_OUT | Out-Null
dotnet --info | Out-File (Join-Path $logs 'dotnet-info.txt') -Encoding UTF8
if (Get-Command git -ErrorAction SilentlyContinue) { git status --short | Out-File (Join-Path $logs 'git-status.txt') -Encoding UTF8 }
Get-ChildItem Env: | Where-Object { $_.Name -like 'P3D_*' -or $_.Name -like 'DOTNET_*' } |
    Format-Table -AutoSize | Out-String -Width 240 | Out-File (Join-Path $logs 'test-settings.txt') -Encoding UTF8
$exe = Join-Path $k01 'K01Checks.exe'

# этапы ядра: имя, аргументы
$stages = @(
    @{ Name = 'allB'; Args = @('allB') },
    @{ Name = 'fix';  Args = @('loftcorpus140') },
    @{ Name = 'allA'; Args = @('allA') },
    @{ Name = 'allC'; Args = @('allC') },
    @{ Name = 'thread'; Args = @('threadchecks') },
    @{ Name = 'threadexit'; Args = @('threadexit') },
    @{ Name = 'threadcylinder'; Args = @('threadcylinder') },
    @{ Name = 'seq';  Args = @('loftseq', '10', '101', '6', (Join-Path $logs 'seq')) }
) | Where-Object { $Only -eq '' -or $_.Name -eq $Only -or ($Only -eq 'thread' -and $_.Name -in @('threadexit', 'threadcylinder')) -or ($Only -eq 'abc' -and $_.Name -in @('allA', 'allB', 'allC')) }

$t0 = Get-Date
$exitCodes = @{}
if ($Parallel) {
    $procs = foreach ($s in $stages) {
        if ($s.Name -eq 'seq') { New-Item -ItemType Directory -Force -Path (Join-Path $logs 'seq') | Out-Null }
        Write-Host "Запуск $($s.Name)…" -ForegroundColor Cyan
        $proc = Start-Process -FilePath $exe -ArgumentList @($s.Args | ForEach-Object { '"{0}"' -f $_ }) -NoNewWindow -PassThru `
            -RedirectStandardOutput (Join-Path $logs "$($s.Name).log") -RedirectStandardError (Join-Path $logs "$($s.Name).err")
        $null = $proc.Handle
        @{ Name = $s.Name; Process = $proc }
    }
    foreach ($item in $procs) {
        $item.Process.WaitForExit()
        $exitCodes[$item.Name] = $item.Process.ExitCode
    }
} else {
    foreach ($s in $stages) {
        if ($s.Name -eq 'seq') { New-Item -ItemType Directory -Force -Path (Join-Path $logs 'seq') | Out-Null }
        $ts = Get-Date
        Write-Host "$($s.Name)… (начало $($ts.ToString('HH:mm')))" -ForegroundColor Cyan
        $proc = Start-Process -FilePath $exe -ArgumentList @($s.Args | ForEach-Object { '"{0}"' -f $_ }) -NoNewWindow -Wait -PassThru `
            -RedirectStandardOutput (Join-Path $logs "$($s.Name).log") -RedirectStandardError (Join-Path $logs "$($s.Name).err")
        $exitCodes[$s.Name] = $proc.ExitCode
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
        $proc = Start-Process -FilePath $uiExe -ArgumentList "`"$chunk`"" -NoNewWindow -Wait -PassThru `
            -RedirectStandardOutput (Join-Path $logs "ui$i.log") -RedirectStandardError (Join-Path $logs "ui$i.err")
        $exitCodes["ui$i"] = $proc.ExitCode
    }
}

# итог
$badRun = $false
foreach ($code in $exitCodes.Values) { if ($null -eq $code -or $code -ne 0) { $badRun = $true } }
$sum = New-Object System.Collections.Generic.List[string]
$sum.Add("Parametric3D — регрессия на $env:COMPUTERNAME ($([Environment]::ProcessorCount) ядер), $((Get-Date).ToString('yyyy-MM-dd HH:mm'))")
$sum.Add("Версия: $ver")
$sum.Add(("Время: {0:0} мин" -f ((Get-Date) - $t0).TotalMinutes))
$sum.Add("Коды завершения: $(($exitCodes.GetEnumerator() | Sort-Object Name | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join '; ')")
foreach ($f in Get-ChildItem $logs -Filter '*.log' | Where-Object { $_.Name -notlike 'build_*' -and $_.Name -ne 'seq.log' }) {
    $text = Get-Content $f.FullName
    $total = ($text | Select-String -Pattern '^\{"passed":\d+,"failed":\d+\}' | Select-Object -Last 1)
    # k159: ожидаемый отказ (случаи корпуса «overflow», отметка expected) — не провал
    $fails = $text | Select-String -Pattern '"(ok|Ok)":false' | Where-Object { $_.Line -notmatch '"expected":"refusal"' } | ForEach-Object { $_.Line.Substring(0, [Math]::Min(300, $_.Line.Length)) }
    $sum.Add("")
    $sum.Add("== $($f.BaseName): $(if ($total) { $total.Line } else { '(итога нет — этап прерван?)' })")
    if (-not $total) { $badRun = $true }
    elseif (($total.Line | ConvertFrom-Json).failed -gt 0) { $badRun = $true }
    if ($f.BaseName -eq 'allC') {
        $allc = $text | Select-String -Pattern '"test":"ALLC-SUMMARY"' | Select-Object -Last 1
        if ($allc) {
            $sum.Add("  ALLC: $($allc.Line)")
            $allcResult = $allc.Line | ConvertFrom-Json
            if ($allcResult.fail -gt 0 -or $allcResult.steps -le 0) {
                $badRun = $true
                $sum.Add("  ALLC: есть отказы или корпус пуст; passed=0/failed=0 не означает успех.")
            }
        }
        else { $badRun = $true; $sum.Add("  ALLC: полного итога нет, прогон нельзя считать завершённым.") }
    }
    foreach ($x in $fails) { $sum.Add("  ПРОВАЛ: $x") }
    $errFile = Join-Path $logs "$($f.BaseName).err"
    if ((Test-Path $errFile) -and (Get-Item $errFile).Length -gt 0) { $sum.Add("  STDERR: $errFile (включить в отчёт)") }
}
$seq = Join-Path $logs 'seq.log'
if (Test-Path $seq) {
    $exact = (Select-String -Path $seq -Pattern '"guarantee":"ExactAnalytic"').Count
    $ops = (Select-String -Path $seq -Pattern '"tag":').Count
    $sum.Add("")
    $sum.Add("== seq (корпус лофтов): операций $ops, точных $exact (k152–k155: 60 операций, 28 точных)")
}
$sumPath = Join-Path $logs 'SUMMARY.txt'
$sum.Add("Статус: $(if ($badRun) { 'FAIL / INCOMPLETE' } else { 'завершён без обнаруженных отказов' })")
$sum | Out-File -FilePath $sumPath -Encoding UTF8
Write-Host ''
$sum | ForEach-Object { Write-Host $_ }
Write-Host ''
Write-Host "Итог: $sumPath — пришлите его в чат." -ForegroundColor Green
Read-Host 'Нажмите Enter, чтобы закрыть'
if ($badRun) { exit 1 }
exit 0
