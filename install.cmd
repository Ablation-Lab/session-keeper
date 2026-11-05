@echo off
rem Double-click to install or update SessionKeeper for the current user (no admin rights needed).
rem Runs install.ps1 with -ExecutionPolicy Bypass for this run only, then waits so you can read the output.
setlocal
set "PS=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"
title SessionKeeper installer
"%PS%" -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" -Interactive %*
set "RC=%ERRORLEVEL%"
echo.
if not "%RC%"=="0" goto failed
echo Done. You can open SessionKeeper from the Start menu.
goto end
:failed
echo The install did not finish (exit code %RC%). Read the message above, then run install.cmd again.
:end
echo.
pause
exit /b %RC%
