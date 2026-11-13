# =========================================================================
#  dig - live stats in the console
#
#  Prints the crawler counters every few seconds so you can watch the index
#  grow without keeping a browser tab open.
#
#  usage: powershell -ExecutionPolicy Bypass -File tools\watch.ps1
#         ... -Port 8090 -IntervalSec 5 -Once
# =========================================================================
[CmdletBinding()]
param(
    [int]$Port = 8090,
    [int]$IntervalSec = 5,
    [switch]$Once,
    [switch]$Log
)

$ErrorActionPreference = 'Stop'
$url = "http://127.0.0.1:$Port/api/stats"
$logPath = Join-Path $env:TEMP 'dig-watch.log'

function Row([string]$label, $value) {
    Write-Host ('  ' + $label.PadRight(18) + ' ' + $value)
}

while ($true) {
    try {
        $r = Invoke-WebRequest -UseBasicParsing -Uri $url -TimeoutSec 5
        $s = $r.Content | ConvertFrom-Json

        $stamp = (Get-Date).ToString('HH:mm:ss')
        Write-Host ''
        Write-Host ("=== dig @ $stamp ===") -ForegroundColor Cyan
        Row 'torrents'    $s.torrents
        Row 'size'        ([math]::Round($s.bytes / 1GB, 2).ToString() + ' GB')
        Row 'added today' $s.addedToday
        Row 'requests'    $s.requests
        Row 'uptime'      ([TimeSpan]::FromSeconds($s.uptimeSec).ToString('d\d\ hh\h\ mm\m'))

        Write-Host '  --- dht ---' -ForegroundColor DarkGray
        Row 'nodes'       $s.dht.nodes
        Row 'queries out' $s.dht.queries
        Row 'answers'     $s.dht.responses
        Row 'queries in'  $s.dht.queriesIn

        Write-Host '  --- crawler ---' -ForegroundColor DarkGray
        Row 'harvested'   $s.crawler.harvested
        Row 'announces'   $s.crawler.announces
        Row 'fetched'     $s.crawler.fetched
        Row 'failed'      $s.crawler.failed
        Row 'queue'       ($s.crawler.queue.ToString() + ' (active ' + $s.crawler.active + ')')
        Row 'paused'      $s.crawler.paused
        Row 'terms'       $s.index.terms

        if ($Log) {
            $line = "$stamp torrents=$($s.torrents) nodes=$($s.dht.nodes) harvested=$($s.crawler.harvested) fetched=$($s.crawler.fetched) queue=$($s.crawler.queue)"
            Add-Content -Path $logPath -Value $line
        }
    } catch {
        Write-Host ("  server not reachable on port $Port : " + $_.Exception.Message) -ForegroundColor Red
    }

    if ($Once) { break }
    Start-Sleep -Seconds $IntervalSec
}
