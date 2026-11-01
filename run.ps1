# Parametric3D: обновить код с GitHub, собрать и запустить приложение.
# Запуск: двойной щелчок по run.bat (рядом) или в PowerShell: .\run.ps1
# Аргументы передаются приложению: .\run.ps1 --sample   |   .\run.ps1 "C:\модели\деталь.p3d"
# Без обновления (как есть): .\run.ps1 -NoPull

param([switch]$NoPull, [Parameter(ValueFromRemainingArguments = $true)][string[]]$AppArgs)

$ErrorActionPreference = 'Stop'
Set-Location -Path $PSScriptRoot

function Fail($msg) { Write-Host $msg -ForegroundColor Red; Read-Host 'Нажмите Enter, чтобы закрыть'; exit 1 }

if (-not (Get-Command git -ErrorAction SilentlyContinue)) { Fail 'Git не найден. Откройте новое окно PowerShell или переустановите Git (вариант PATH: «Git from the command line…»).' }
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { Fail 'dotnet не найден. Установите .NET 8 SDK: https://dotnet.microsoft.com/download' }

# Скрипт может лежать не в клоне репозитория (скопирован в другую папку, распакован из zip): тогда — клон в
# «Документы\parametric3d-rep»; нет его — предложить скачать туда.
$repoUrl = 'https://github.com/VladKlimenko-dev/parametric3d-rep.git'
function IsClone($dir) { (Test-Path (Join-Path $dir '.git')) -and (Test-Path (Join-Path $dir 'src\Parametric3D.csproj')) }
if (-not (IsClone $PWD.Path)) {
    # «Документы» (у OneDrive — перенаправленные) и папка профиля\Documents — где клон делали по инструкции
    $cands = @((Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'parametric3d-rep'), (Join-Path $HOME 'Documents\parametric3d-rep'))
    $clone = ($cands | Where-Object { IsClone $_ } | Select-Object -First 1)
    if (-not $clone) { $clone = $cands[0] }
    if (IsClone $clone) {
        Write-Host "Эта папка — не клон репозитория ($($PWD.Path)). Работаю с клоном: $clone" -ForegroundColor Yellow
        Set-Location -Path $clone
    } else {
        Write-Host "Эта папка — не клон репозитория ($($PWD.Path)), и клона нет в $clone." -ForegroundColor Yellow
        $ans = Read-Host "Скачать репозиторий в $clone? (Y/N)"
        if ($ans -notmatch '^(y|д)') { Fail 'Без клона обновление невозможно.' }
        git clone $repoUrl $clone
        if ($LASTEXITCODE -ne 0) { Fail 'git clone не удался (нет сети?).' }
        Set-Location -Path $clone
        $NoPull = $true
    }
    Write-Host "Дальше запускайте run.bat из папки $((Get-Location).Path)" -ForegroundColor Yellow
}

if (-not $NoPull) {
    $changed = git status --porcelain --untracked-files=no
    if ($changed) {
        Write-Host 'В папке есть свои изменения файлов — обновление пропущено, чтобы их не потерять:' -ForegroundColor Yellow
        $changed | ForEach-Object { Write-Host "  $_" }
        Write-Host 'Вернуть всё как в репозитории: git checkout -- .   (свои правки пропадут)' -ForegroundColor Yellow
    } else {
        $branch = git rev-parse --abbrev-ref HEAD
        if ($branch -ne 'main') {
            Write-Host "Сейчас выбрана версия не с ветки main ($branch) — обновление пропущено. Вернуться к последней: git checkout main" -ForegroundColor Yellow
        } else {
            Write-Host 'Обновление с GitHub…' -ForegroundColor Cyan
            git pull --ff-only
            if ($LASTEXITCODE -ne 0) { Fail 'git pull не удался (нет сети или конфликт). Можно запустить без обновления: .\run.ps1 -NoPull' }
        }
    }
}

$ver = git log -1 --format='%h %ad %s' --date=short
Write-Host "Версия: $ver" -ForegroundColor Green

Write-Host 'Сборка и запуск…' -ForegroundColor Cyan
dotnet run --project src\Parametric3D.csproj -c Release -- @AppArgs
if ($LASTEXITCODE -ne 0) { Fail "Приложение завершилось с кодом $LASTEXITCODE." }
