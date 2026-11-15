@echo off
REM System Assistant portable single-file publish entry point (double-click).
REM   Default          : framework-dependent (requires .NET 9 Desktop Runtime)
REM   -IncludeRuntime  : self-contained (no runtime required on target PC)
setlocal
set SCRIPT_DIR=%~dp0
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_DIR%publish.ps1" %*
endlocal
pause
