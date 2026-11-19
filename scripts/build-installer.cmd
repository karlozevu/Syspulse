@echo off
setlocal EnableExtensions DisableDelayedExpansion

rem ==========================================================================
rem  Build SystemAssistant-Setup-1.1.5.exe with Inno Setup 6 (ISCC.exe).
rem  Toolchain is kept portable inside the workspace at .tools\inno\.
rem ==========================================================================

set "SCRIPT_DIR=%~dp0"
for %%I in ("%SCRIPT_DIR%..")     do set "PROJ_DIR=%%~fI"
for %%I in ("%SCRIPT_DIR%..\..")  do set "WS_DIR=%%~fI"

set "ISS=%PROJ_DIR%\installer\SystemAssistant-Setup.iss"
set "SRC=%PROJ_DIR%\artifacts\win-x64-self-contained\SystemAssistant.exe"
set "OUT=%PROJ_DIR%\artifacts\SystemAssistant-Setup-1.1.5.exe"

echo [1/4] Locating Inno Setup compiler (ISCC.exe) ...
set "ISCC=%WS_DIR%\.tools\inno\ISCC.exe"
if not exist "%ISCC%" set "ISCC=%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe"
if not exist "%ISCC%" set "ISCC=%ProgramFiles%\Inno Setup 6\ISCC.exe"
if not exist "%ISCC%" set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
if not exist "%ISCC%" (
  echo [ERROR] ISCC.exe not found.
  echo         Extract Inno Setup 6 into "%WS_DIR%\.tools\inno\" and retry.
  exit /b 1
)
echo       Using: "%ISCC%"

echo [2/4] Checking inputs ...
if not exist "%ISS%" (
  echo [ERROR] Missing installer script: "%ISS%"
  exit /b 1
)
if not exist "%SRC%" (
  echo [ERROR] Missing self-contained build: "%SRC%"
  echo         Run scripts\publish.ps1 first.
  exit /b 1
)
if not exist "%PROJ_DIR%\artifacts" mkdir "%PROJ_DIR%\artifacts"
for %%F in ("%SRC%") do echo       Source exe: %%~zF bytes

echo [3/4] Compiling installer ...
rem Run from installer\ so that relative SourceDir / OutputDir / MessagesFile
rem paths in the .iss resolve consistently.
pushd "%PROJ_DIR%\installer"
"%ISCC%" "%ISS%"
set "RC=%ERRORLEVEL%"
popd
if not "%RC%"=="0" (
  echo [ERROR] ISCC.exe failed with exit code %RC%.
  exit /b %RC%
)

echo [4/4] Verifying output ...
if not exist "%OUT%" (
  echo [ERROR] Expected output not found: "%OUT%"
  exit /b 1
)
for %%F in ("%OUT%") do echo       Output: %%~fF
for %%F in ("%OUT%") do echo       Size:   %%~zF bytes
echo [OK] Build complete: SystemAssistant-Setup-1.1.5.exe
exit /b 0
