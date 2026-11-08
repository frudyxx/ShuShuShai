@echo off
chcp 65001 >nul
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\dev.ps1" run
if errorlevel 1 pause
