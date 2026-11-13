@echo off
setlocal
cd /d "%~dp0bin"
if not exist "DigServer.exe" (
  echo DigServer.exe not found, run build.cmd first.
  exit /b 1
)
DigServer.exe %*
endlocal
