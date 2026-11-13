# =========================================================================
#  dig - install / remove the Windows service
#
#  Wraps the exe's own --install-service / --uninstall-service switches so the
#  whole thing can be scripted. Requires admin rights: the script relaunches
#  itself with a UAC prompt.
#
#  usage:
#    powershell -ExecutionPolicy Bypass -File tools\service.ps1 -Install
#    ... -Remove
#    ... -Status
# =========================================================================
[CmdletBinding()]
param(
    [switch]$Install,
    [switch]$Remove,
    [switch]$Status,
    [switch]$NoPause
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root 'bin\DigServer.exe'
$serviceName = 'dig'

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $Status -and -not $isAdmin) {
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"{0}"' -f $PSCommandPath))
    if ($Install) { $argList += '-Install' }
    if ($Remove) { $argList += '-Remove' }
    if ($NoPause) { $argList += '-NoPause' }

    Write-Host 'Нужны права администратора - сейчас появится запрос UAC (нажмите «Да»).' -ForegroundColor Yellow
    try {
        Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList $argList | Out-Null
    } catch {
        Write-Host "Повышение прав отменено: $($_.Exception.Message)" -ForegroundColor Red
        exit 1
    }
    Start-Sleep -Seconds 2
    exit
}

if (-not (Test-Path $exe)) {
    Write-Host "DigServer.exe не найден: $exe" -ForegroundColor Red
    Write-Host 'Сначала запустите build.cmd' -ForegroundColor Red
    exit 1
}

if ($Status) {
    Write-Host ''
    Write-Host '=== dig service status ===' -ForegroundColor Cyan
    $svc = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if ($null -eq $svc) {
        Write-Host '  not installed' -ForegroundColor DarkGray
    } else {
        Write-Host ('  status  : ' + $svc.Status)
        Write-Host ('  startup : ' + (Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -ErrorAction SilentlyContinue).StartMode)
        $cfg = Join-Path $root 'bin\config.ini'
        $port = 8090
        if (Test-Path $cfg) {
            $m = Select-String -Path $cfg -Pattern '^port=(\d+)' | Select-Object -First 1
            if ($m) { $port = [int]$m.Matches[0].Groups[1].Value }
        }
        try {
            $r = Invoke-WebRequest -UseBasicParsing ('http://127.0.0.1:' + $port + '/api/health') -TimeoutSec 5
            Write-Host ('  health  : HTTP ' + [int]$r.StatusCode + ' ' + $r.Content) -ForegroundColor Green
        } catch {
            Write-Host ('  health  : not answering on port ' + $port) -ForegroundColor Yellow
        }
    }
    Write-Host ''
    exit 0
}

if ($Remove) {
    Write-Host ''
    & $exe --uninstall-service
    Write-Host ''
    if (-not $NoPause) { Read-Host 'Готово. Enter - закрыть окно' | Out-Null }
    exit
}

if ($Install) {
    Write-Host ''
    & $exe --install-service
    $code = $LASTEXITCODE
    if ($code -eq 0) {
        Write-Host '--- status ---' -ForegroundColor Cyan
        Start-Sleep -Seconds 3
        Get-Service -Name $serviceName -ErrorAction SilentlyContinue |
            Select-Object Name, Status, StartType | Format-Table -AutoSize
        Write-Host 'Подсказка: tools\firewall.cmd открывает порты (UDP для DHT обязателен).' -ForegroundColor DarkGray
    }
    Write-Host ''
    if (-not $NoPause) { Read-Host 'Готово. Enter - закрыть окно' | Out-Null }
    exit $code
}

Write-Host 'usage: service.ps1 -Install | -Remove | -Status'
