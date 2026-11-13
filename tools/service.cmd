@echo off
rem ============================================================
rem  dig - Windows service helper (asks for UAC "Yes")
rem
rem    service.cmd            install + start the service
rem    service.cmd -status    show the current state
rem    service.cmd -restart   stop + start
rem    service.cmd -stop      stop it
rem    service.cmd -remove    stop + delete the registration
rem ============================================================
setlocal
set "EXE=%~dp0..\bin\DigServer.exe"

if /i "%~1"=="-status" (
  sc query dig
  goto :eof
)
if /i "%~1"=="-stop" (
  sc stop dig
  goto :eof
)
if /i "%~1"=="-restart" (
  sc stop dig
  timeout /t 3 /nobreak >nul
  sc start dig
  goto :eof
)
if /i "%~1"=="-remove" (
  powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0service.ps1" -Remove
  goto :eof
)

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0service.ps1" -Install
endlocal
