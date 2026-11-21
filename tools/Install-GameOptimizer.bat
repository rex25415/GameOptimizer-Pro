@echo off
chcp 65001 >nul
title GameOptimizer Pro - Install Desktop & Start Menu Shortcuts
set "SCRIPT=%~dp0deploy.ps1"
if not exist "%SCRIPT%" set "SCRIPT=%~dp0tools\deploy.ps1"
if not exist "%SCRIPT%" (
    echo [ERROR] deploy.ps1 not found next to this launcher.
    pause
    exit /b 1
)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT%"
echo.
if %errorlevel% equ 0 (
    echo Done! You can now launch GameOptimizer Pro from your Desktop or Start Menu.
) else (
    echo Install failed with exit code %errorlevel%.
)
pause
