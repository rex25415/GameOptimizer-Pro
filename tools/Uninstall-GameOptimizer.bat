@echo off
chcp 65001 >nul
title GameOptimizer Pro - Clean Uninstall
set "SCRIPT=%~dp0uninstall.ps1"
if not exist "%SCRIPT%" set "SCRIPT=%~dp0tools\uninstall.ps1"
if not exist "%SCRIPT%" (
    echo [ERROR] uninstall.ps1 not found next to this launcher.
    pause
    exit /b 1
)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT%"
echo.
pause
