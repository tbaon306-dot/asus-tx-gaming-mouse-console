@echo off
REM Stop Armoury Crate services and processes so onboard saves actually persist.
REM Double-click this file. It will ask for administrator rights.

net session >nul 2>&1
if errorlevel 1 (
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0stop-ac.ps1"
echo.
pause