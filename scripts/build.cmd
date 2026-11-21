@echo off
REM Restore + build the whole solution.
setlocal
set ROOT=%~dp0..
dotnet restore "%ROOT%\SystemAssistant.sln"
if errorlevel 1 goto :fail
dotnet build "%ROOT%\SystemAssistant.sln" -c Debug --no-restore
if errorlevel 1 goto :fail
echo.
echo Build succeeded: src\SystemAssistant.App\bin\Debug\net9.0-windows\Syspulse.exe
goto :end
:fail
echo Build failed. See the errors above.
:end
endlocal
pause
