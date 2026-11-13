# =========================================================================
#  dig - CI test script
#
#  build -> self-test -> start on scratch ports -> HTTP/API/admin checks.
#  No DHT: CI machines often block UDP, and this suite must stay deterministic.
#
#  usage: powershell -ExecutionPolicy Bypass -File tools\ci-test.ps1
# =========================================================================
[CmdletBinding()]
param(
    [int]$Port = 18090,
    [int]$AdminPort = 18095,
    [int]$StartTimeoutSec = 25,
    [switch]$KeepBin
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$bin = Join-Path $root 'bin'
$exe = Join-Path $bin 'DigServer.exe'
$failures = New-Object 'System.Collections.Generic.List[string]'
$checks = 0

function Check([string]$name, [bool]$ok, [string]$detail) {
    $script:checks++
    if ($ok) {
        Write-Host ("  ok    " + $name) -ForegroundColor DarkGray
    } else {
        $script:failures.Add($name + $(if ($detail) { " -> " + $detail } else { "" }))
        Write-Host ("  FAIL  " + $name + $(if ($detail) { " -> " + $detail } else { "" })) -ForegroundColor Red
    }
}

function Wait-Http([string]$url, [int]$timeoutSec) {
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    while ((Get-Date) -lt $deadline) {
        try {
            $r = Invoke-WebRequest -UseBasicParsing -Uri $url -TimeoutSec 3
            if ($r.StatusCode -eq 200) { return $r }
        } catch { }
        Start-Sleep -Milliseconds 400
    }
    return $null
}

function Status([string]$url) {
    try {
        $r = Invoke-WebRequest -UseBasicParsing -Uri $url -TimeoutSec 5
        return [int]$r.StatusCode
    } catch {
        if ($_.Exception.Response) { return [int]$_.Exception.Response.StatusCode }
        return 0
    }
}

Write-Host ''
Write-Host '=== dig CI ===' -ForegroundColor Cyan

# ------------------------------------------------------------------- build

Write-Host '[1/4] build' -ForegroundColor Cyan
& cmd /c "`"$root\build.cmd`"" | Out-Null
Check 'build produced DigServer.exe' (Test-Path $exe)

if (-not (Test-Path $exe)) {
    Write-Host 'build failed, stopping' -ForegroundColor Red
    exit 1
}

# --------------------------------------------------------------- self-test

Write-Host '[2/4] self-test' -ForegroundColor Cyan
$selfOutput = & $exe --self-test 2>&1
$selfCode = $LASTEXITCODE
$selfOutput | Where-Object { $_ -match 'FAIL|passed:' } | ForEach-Object { Write-Host ('  ' + $_) }
Check 'self-test exit code 0' ($selfCode -eq 0) "exit=$selfCode"

# ------------------------------------------------------------------- start

Write-Host '[3/4] start server' -ForegroundColor Cyan
Get-Process DigServer -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } | Stop-Process -Force
Start-Sleep -Milliseconds 300

$outLog = Join-Path $bin 'ci-out.log'
$errLog = Join-Path $bin 'ci-err.log'
$args = @('--headless', '--no-dht', '--port', $Port, '--admin-port', $AdminPort, '--level', 'Debug')
$proc = Start-Process -FilePath $exe -ArgumentList $args -WorkingDirectory $bin -PassThru `
    -RedirectStandardOutput $outLog -RedirectStandardError $errLog

$base = "http://127.0.0.1:$Port"
$admin = "http://127.0.0.1:$AdminPort"

try {
    $health = Wait-Http "$base/api/health" $StartTimeoutSec
    Check 'server answers /api/health' ($health -ne $null)

    if ($health -eq $null) {
        if (Test-Path $errLog) { Get-Content $errLog -Tail 20 | ForEach-Object { Write-Host ('  ' + $_) } }
        throw 'server did not start'
    }

    # ---------------------------------------------------------------- pages

    Write-Host '[4/4] http checks' -ForegroundColor Cyan

    Check 'GET /'                ((Status "$base/") -eq 200)
    Check 'GET /search'          ((Status "$base/search?q=test") -eq 200)
    Check 'GET /stats'           ((Status "$base/stats") -eq 200)
    Check 'GET /about'           ((Status "$base/about") -eq 200)
    Check 'GET /abuse'           ((Status "$base/abuse") -eq 200)
    Check 'GET /robots.txt'      ((Status "$base/robots.txt") -eq 200)
    Check 'GET /sitemap.xml'     ((Status "$base/sitemap.xml") -eq 200)
    Check 'GET /brand.css'       ((Status "$base/brand.css") -eq 200)
    Check 'GET /manifest'        ((Status "$base/manifest.webmanifest") -eq 200)
    Check 'GET /favicon.svg'     ((Status "$base/favicon.svg") -eq 200)
    Check 'GET /assets css'      ((Status "$base/assets/css/app.css") -eq 200)
    Check 'GET /assets js'       ((Status "$base/assets/js/app.js") -eq 200)

    Check '404 for unknown path'        ((Status "$base/definitely-not-here") -eq 404)
    Check '404 for bad hash'            ((Status "$base/t/zzz") -eq 404)
    Check '404 for unknown hash'        ((Status "$base/t/0123456789abcdef0123456789abcdef01234567") -eq 404)
    Check 'path traversal blocked'      ((Status "$base/assets/../config.ini") -eq 404)

    # ------------------------------------------------------------------ api

    $r = Invoke-WebRequest -UseBasicParsing "$base/api/search?q=test" -TimeoutSec 5
    $json = $r.Content | ConvertFrom-Json
    Check 'api/search returns json' ([bool]($json.query -eq 'test'))
    Check 'api/search has results array' ($null -ne $json.results)

    $r = Invoke-WebRequest -UseBasicParsing "$base/api/stats" -TimeoutSec 5
    $stats = $r.Content | ConvertFrom-Json
    Check 'api/stats ok' ([bool]($stats.ok -eq $true))
    Check 'api/stats reports version' ([bool]($stats.version.Length -gt 0))
    Check 'api/stats hides nothing secret' ([bool]($r.Content -notmatch 'serverSecret|adminToken'))

    # ---------------------------------------------------------------- admin

    $adminAnonymous = Status "$admin/"
    Check 'admin board serves login' ($adminAnonymous -eq 200)

    $configPath = Join-Path $bin 'config.ini'
    $token = $null
    if (Test-Path $configPath) {
        $match = Select-String -Path $configPath -Pattern '^adminToken=(.+)$' | Select-Object -First 1
        if ($match) { $token = $match.Matches[0].Groups[1].Value.Trim() }
    }
    Check 'admin token generated' ($token -ne $null -and $token.Length -ge 16)

    if ($token) {
        $session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
        $login = Invoke-WebRequest -UseBasicParsing "$admin/login" -Method Post -Body @{ token = $token } -WebSession $session -TimeoutSec 5
        Check 'admin login succeeds' ($login.Content -match 'Overview')

        $s = Invoke-WebRequest -UseBasicParsing "$admin/api/stats" -WebSession $session -TimeoutSec 5
        Check 'admin api/stats ok' (($s.Content | ConvertFrom-Json).ok -eq $true)

        $c = Invoke-WebRequest -UseBasicParsing "$admin/api/config" -WebSession $session -TimeoutSec 5
        Check 'admin config redacts secrets' ($c.Content -notmatch 'adminToken"|serverSecret"')

        $l = Invoke-WebRequest -UseBasicParsing "$admin/api/log?lines=5" -WebSession $session -TimeoutSec 5
        Check 'admin api/log ok' (($l.Content | ConvertFrom-Json).ok -eq $true)

        $anonAdmin = $null
        try { $anonAdmin = Invoke-WebRequest -UseBasicParsing "$admin/api/stats" -TimeoutSec 5 } catch { }
        Check 'admin api rejects anonymous' ($anonAdmin -eq $null)

        $wrong = $null
        try { $wrong = Invoke-WebRequest -UseBasicParsing "$admin/login" -Method Post -Body @{ token = 'wrong' } -TimeoutSec 5 } catch { }
        Check 'admin login rejects wrong token' ($wrong -eq $null)
    }

    # -------------------------------------------------------------- headers

    $r = Invoke-WebRequest -UseBasicParsing "$base/" -TimeoutSec 5
    Check 'CSP header present' ($r.Headers['Content-Security-Policy'] -ne $null)
    Check 'nosniff present'    ($r.Headers['X-Content-Type-Options'] -eq 'nosniff')
    Check 'frame options deny' ($r.Headers['X-Frame-Options'] -eq 'DENY')

    $gzip = $null
    try {
        $gzip = Invoke-WebRequest -UseBasicParsing "$base/assets/css/app.css" -Headers @{ 'Accept-Encoding' = 'gzip' } -TimeoutSec 5
    } catch { }
    Check 'gzip works on static assets' ($gzip -ne $null -and $gzip.RawContentLength -gt 0)
}
finally {
    if ($proc -and -not $proc.HasExited) {
        try { $proc.Kill() } catch { }
        $proc.WaitForExit(5000) | Out-Null
    }
    if (-not $KeepBin) {
        foreach ($f in @($outLog, $errLog)) { if (Test-Path $f) { Remove-Item $f -Force -ErrorAction SilentlyContinue } }
    }
}

Write-Host ''
if ($failures.Count -eq 0) {
    Write-Host ("PASS: " + $checks + " checks") -ForegroundColor Green
    exit 0
}

Write-Host ("FAIL: " + $failures.Count + " of " + $checks + " checks") -ForegroundColor Red
foreach ($f in $failures) { Write-Host ('  - ' + $f) -ForegroundColor Red }
exit 1
