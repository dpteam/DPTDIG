@echo off
rem ============================================================
rem  dig - Windows Firewall rules (asks for UAC "Yes")
rem  Ports are read from bin\config.ini.
rem
rem  extra options:
rem    firewall.cmd -PublicAny   allow the web port from anywhere
rem    firewall.cmd -DhtLanOnly  do not expose the DHT UDP port
rem    firewall.cmd -Remove      delete the dig rules
rem ============================================================
setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0firewall.ps1" %*
endlocal
