@echo off
rem Parametric3D: регрессия ядра и приложения на этом компьютере (двойной щелчок). Быстрее на 8+ ядрах: regress.bat -Parallel
chcp 65001 >nul
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0regress.ps1" %*
