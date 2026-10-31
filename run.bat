@echo off
rem Parametric3D: обновить с GitHub, собрать и запустить (двойной щелчок)
chcp 65001 >nul
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run.ps1" %*
