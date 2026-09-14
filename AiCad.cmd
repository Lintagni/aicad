@echo off
rem ---------------------------------------------------------------------------
rem  AiCad - one-click start.
rem
rem  Extract the ZIP, double-click this file. It builds on first run (using the
rem  C# compiler that ships with Windows and your own AutoCAD install), makes
rem  sure AutoCAD can see the drawing engine, then opens the UI in your browser.
rem ---------------------------------------------------------------------------
setlocal
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0start.ps1" %*
if errorlevel 1 (
  echo.
  echo AiCad did not start. The message above says why.
  pause
)
endlocal
