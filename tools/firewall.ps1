# =========================================================================
#  dig - Windows Firewall setup
#
#  Adds inbound rules for:
#    * public web board (TCP)   - LAN/private sources by default, because the
#                                 usual setup is Nginx Proxy Manager on the LAN
#    * admin board (TCP)        - private sources only, always
#    * DHT node (UDP)           - any source by default: the DHT is peer to peer
#
#  Rules are scoped to DigServer.exe and removed/recreated on every run.
#  Requires admin rights: the script relaunches itself with a UAC prompt.
#
#  usage (double click firewall.cmd, or):
#    powershell -ExecutionPolicy Bypass -File tools\firewall.ps1
#    ... -PublicAny      allow the web port from anywhere (direct expose)
#    ... -DhtLanOnly     do not expose the DHT UDP port to the internet
#    ... -Remove         delete the dig rules
# =========================================================================
[CmdletBinding()]
param(
    [switch]$PublicAny,
    [switch]$DhtLanOnly,
    [switch]$Remove,
    [switch]$NoPause
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root 'bin\DigServer.exe'
$ini = Join-Path $root 'bin\config.ini'
$reportPath = Join-Path $env:TEMP 'dig-firewall.log'
$report = New-Object 'System.Collections.Generic.List[string]'

function Add-Line([string]$text) {
    $report.Add($text)
    Write-Host $text
}

function Save-Report {
    try { Set-Content -Path $reportPath -Value $report -Encoding UTF8 } catch { }
}

# --------------------------------------------------------------- elevation

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"{0}"' -f $PSCommandPath))
    if ($PublicAny) { $argList += '-PublicAny' }
    if ($DhtLanOnly) { $argList += '-DhtLanOnly' }
    if ($Remove) { $argList += '-Remove' }
    if ($NoPause) { $argList += '-NoPause' }

    Write-Host "Нужны права администратора - сейчас появится запрос UAC (нажмите «Да»)." -ForegroundColor Yellow
    try {
        Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList $argList | Out-Null
    } catch {
        Write-Host "Повышение прав отменено: $($_.Exception.Message)" -ForegroundColor Red
        Write-Host "Запустите этот файл от имени администратора вручную." -ForegroundColor Red
    }
    Start-Sleep -Seconds 1
    exit
}

# ------------------------------------------------------------------- config

function Read-IniValues([string]$path) {
    $map = @{}
    if (-not (Test-Path $path)) { return $map }
    $section = ''
    foreach ($line in Get-Content $path) {
        if ($line -match '^\s*\[([^\]]+)\]') {
            $section = $Matches[1].Trim().ToLowerInvariant()
            continue
        }
        if ($line -match '^\s*([A-Za-z0-9_.]+)\s*=\s*(.*?)\s*$') {
            $map[$section + '.' + $Matches[1].ToLowerInvariant()] = $Matches[2]
        }
    }
    return $map
}

$values = Read-IniValues $ini
$portWeb = 8090
$portAdmin = 8095
$portDht = 6881
$dhtEnabled = $true
$adminEnabled = $true
if ($values.ContainsKey('server.port') -and $values['server.port']) { $portWeb = [int]$values['server.port'] }
if ($values.ContainsKey('server.adminport') -and $values['server.adminport']) { $portAdmin = [int]$values['server.adminport'] }
if ($values.ContainsKey('dht.udpport') -and $values['dht.udpport']) { $portDht = [int]$values['dht.udpport'] }
if ($values.ContainsKey('dht.enabled') -and $values['dht.enabled'] -match '^(false|0|no|off)$') { $dhtEnabled = $false }
if ($values.ContainsKey('server.adminenabled') -and $values['server.adminenabled'] -match '^(false|0|no|off)$') { $adminEnabled = $false }

$bindWeb = if ($values.ContainsKey('server.bind')) { $values['server.bind'] } else { '0.0.0.0' }
$bindAdmin = if ($values.ContainsKey('server.adminbind')) { $values['server.adminbind'] } else { '127.0.0.1' }

# --------------------------------------------------------------- addresses

$lanRemote = @(
    'LocalSubnet',
    '127.0.0.1/32',
    '10.0.0.0/8',
    '172.16.0.0/12',
    '192.168.0.0/16',
    '169.254.0.0/16',
    '100.64.0.0/10'
)
$anyRemote = @('Any')

Add-Line ''
Add-Line '=== dig - firewall setup ==='
Add-Line ('config : ' + $(if (Test-Path $ini) { $ini } else { 'not found, using defaults' }))
Add-Line ('exe    : ' + $(if (Test-Path $exe) { $exe } else { 'NOT FOUND (rules will be port-only)' }))
Add-Line ('ports  : web=' + $portWeb + ' admin=' + $portAdmin + ' dht=udp/' + $(if ($dhtEnabled) { $portDht } else { 'off' }))
Add-Line ('bind   : web=' + $bindWeb + ' admin=' + $bindAdmin)
Add-Line ''

$rules = @(
    @{
        Name   = 'dig web'
        Port   = $portWeb
        Proto  = 'TCP'
        Remote = $(if ($PublicAny) { $anyRemote } else { $lanRemote })
        Note   = 'public search board (TCP)'
    }
)

if ($adminEnabled) {
    $rules += @{
        Name   = 'dig admin'
        Port   = $portAdmin
        Proto  = 'TCP'
        Remote = $lanRemote
        Note   = 'admin board (TCP, private only)'
    }
}

if ($dhtEnabled) {
    $rules += @{
        Name   = 'dig dht'
        Port   = $portDht
        Proto  = 'UDP'
        Remote = $(if ($DhtLanOnly) { $lanRemote } else { $anyRemote })
        Note   = 'DHT node (UDP)'
    }
}

$haveCmdlets = $null -ne (Get-Command New-NetFirewallRule -ErrorAction SilentlyContinue)

function Remove-Rule([string]$name) {
    if ($haveCmdlets) {
        Get-NetFirewallRule -DisplayName $name -ErrorAction SilentlyContinue |
            Remove-NetFirewallRule -ErrorAction SilentlyContinue
    } else {
        netsh advfirewall firewall delete rule name="$name" | Out-Null
    }
}

function Add-Rule($rule) {
    $name = $rule.Name
    $port = $rule.Port
    $proto = $rule.Proto
    $remote = $rule.Remote
    $note = $rule.Note

    Remove-Rule $name
    if ($Remove) { return }

    if ($haveCmdlets) {
        $params = @{
            DisplayName   = $name
            Description   = 'dig - ' + $note
            Direction     = 'Inbound'
            Action        = 'Allow'
            Protocol      = $proto
            LocalPort     = $port
            RemoteAddress = $remote
            Profile       = 'Any'
            Enabled       = 'True'
        }
        if (Test-Path $exe) { $params['Program'] = $exe }
        New-NetFirewallRule @params | Out-Null
    } else {
        $cmd = 'netsh advfirewall firewall add rule name="' + $name + '" dir=in action=allow protocol=' + $proto + ' localport=' + $port + ' profile=any enable=yes'
        if (Test-Path $exe) { $cmd += ' program="' + $exe + '"' }
        if ($remote -notcontains 'Any') { $cmd += ' remoteip=' + ($remote -join ',') }
        Invoke-Expression $cmd | Out-Null
    }
}

# ------------------------------------------------------------------- apply

if ($Remove) {
    Add-Line 'Removing dig firewall rules...'
} else {
    Add-Line 'Adding inbound rules...'
}

foreach ($rule in $rules) {
    try {
        Add-Rule $rule
        if ($Remove) {
            Add-Line ('  removed : ' + $rule.Name)
        } else {
            $scope = if ($rule.Remote -contains 'Any') { 'any source' } else { 'LAN/private only' }
            Add-Line ('  ok      : ' + $rule.Name.PadRight(12) + ' ' + $rule.Proto.ToLower() + '/' + $rule.Port + '  (' + $scope + ')')
        }
    } catch {
        Add-Line ('  FAILED  : ' + $rule.Name + ' -> ' + $_.Exception.Message)
    }
}

# ------------------------------------------------------------- diagnostics

Add-Line ''
Add-Line '--- listening sockets ---'
try {
    $netstat = netstat -ano
    foreach ($port in @($portWeb, $portAdmin)) {
        $hit = $netstat | Select-String ('TCP\s+\S+:' + $port + '\s+\S+\s+LISTENING') | Select-Object -First 1
        if ($hit) { Add-Line ('  tcp/' + $port + ' -> listening') }
        else { Add-Line ('  tcp/' + $port + ' -> not listening (server stopped?)') }
    }
    if ($dhtEnabled) {
        $hit = $netstat | Select-String ('UDP\s+\S+:' + $portDht + '\s') | Select-Object -First 1
        if ($hit) { Add-Line ('  udp/' + $portDht + ' -> bound') }
        else { Add-Line ('  udp/' + $portDht + ' -> not bound (server stopped?)') }
    }
} catch {
    Add-Line ('  netstat failed: ' + $_.Exception.Message)
}

Add-Line ''
Add-Line '--- local IPv4 addresses (используйте их в NGinx Proxy Manager) ---'
try {
    $ips = Get-NetIPAddress -AddressFamily IPv4 -ErrorAction Stop |
        Where-Object { $_.IPAddress -notlike '127.*' -and $_.IPAddress -notlike '169.254.*' }
    foreach ($ip in $ips) {
        Add-Line ('  ' + $ip.IPAddress + '/' + $ip.PrefixLength + '   [' + $ip.InterfaceAlias + ']')
    }
} catch {
    Add-Line ('  ' + $_.Exception.Message)
}

Add-Line ''
Add-Line '--- local health check (bypasses the firewall, checks the server itself) ---'
try {
    $r = Invoke-WebRequest -UseBasicParsing -Uri ('http://127.0.0.1:' + $portWeb + '/api/health') -TimeoutSec 5
    Add-Line ('  ' + $portWeb + ' -> HTTP ' + [int]$r.StatusCode + ' ' + $r.Content)
} catch {
    Add-Line ('  ' + $portWeb + ' -> FAILED: ' + $_.Exception.Message)
}
if ($adminEnabled) {
    # the admin board answers 200 with the login page; its API stays locked
    try {
        $r = Invoke-WebRequest -UseBasicParsing -Uri ('http://127.0.0.1:' + $portAdmin + '/') -TimeoutSec 5
        Add-Line ('  ' + $portAdmin + ' -> HTTP ' + [int]$r.StatusCode + ' (admin login page)')
    } catch {
        Add-Line ('  ' + $portAdmin + ' -> FAILED: ' + $_.Exception.Message)
    }
}

Add-Line ''
Add-Line '--- rules ---'
if ($haveCmdlets) {
    try {
        Get-NetFirewallRule -DisplayName 'dig *' -ErrorAction SilentlyContinue |
            ForEach-Object {
                $pf = $_ | Get-NetFirewallPortFilter
                $af = $_ | Get-NetFirewallAddressFilter
                Add-Line ('  ' + $_.DisplayName + ' | ' + $_.Enabled + ' | ' + $_.Direction + '/' + $_.Action +
                          ' | ' + $pf.Protocol + '/' + $pf.LocalPort + ' | remote: ' + ($af.RemoteAddress -join ','))
            }
    } catch {
        Add-Line ('  ' + $_.Exception.Message)
    }
}

Add-Line ''
Add-Line 'Подсказки:'
Add-Line '  * NGinx Proxy Manager в Docker НЕ видит 127.0.0.1 хоста - укажите LAN-IP сверху.'
Add-Line ('  * Схема в NPM: http + <LAN-IP> + порт ' + $portWeb + ', без пути (или с /dig).')
Add-Line '  * Публичная доска рассчитана на LAN-источник (NPM рядом). Прямой проброс ' + $portWeb
Add-Line '    наружу потребует запуска с -PublicAny.'
Add-Line ('  * Админка (порт ' + $portAdmin + ') остаётся только в приватных сетях.')
Add-Line ('  * DHT работает по UDP/' + $portDht + ': без входящего UDP узел всё равно')
Add-Line '    сможет опрашивать других, но его хуже видят в таблицах маршрутизации.'
Add-Line ''
Add-Line ('Отчёт: ' + $reportPath)

Save-Report

if (-not $NoPause) {
    Write-Host ''
    Read-Host 'Готово. Enter - закрыть окно' | Out-Null
}
