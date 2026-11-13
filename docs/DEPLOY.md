# Deployment

## Requirements

| Thing | Minimum | Notes |
|---|---|---|
| OS | Windows XP SP3 / Server 2003+ | tested on modern Windows; no newer APIs used |
| Runtime | .NET Framework 4.0 | 4.x also fine, `csc.exe` from 4.0.30319 is enough |
| Disk | 1 GB + index growth | ~300 B per torrent |
| RAM | 512 MB | ~1 KB per torrent, more for big file lists |
| Network | outbound UDP + TCP | inbound UDP strongly recommended |

No NuGet, no MSBuild, no Visual Studio, no native DLLs.

## Build

```cmd
build.cmd
```

Compiles with the stock `csc.exe`, copies `public/` and `config.example.ini` into
`bin/`. Output: `bin\DigServer.exe`.

## Run

```cmd
run.cmd
```

First start creates `bin\config.ini` from the example, generates
`[security] adminToken` and `serverSecret`, prints both boards and the token, then
starts crawling.

```
  PUBLIC:  http://localhost:8090/
  ADMIN :  http://localhost:8095/
  token :  3f2a...
```

Useful flags:

```cmd
DigServer.exe --no-dht          viewer only, no UDP, no crawling
DigServer.exe --no-fetch        harvest hashes, never open TCP to peers
DigServer.exe --headless        no console commands (for a scheduled task)
DigServer.exe --port 9000 --admin-port 9001 --udp-port 6900
DigServer.exe --level Debug
DigServer.exe --self-test       offline test suite, exit code 0/1
```

Console commands: `stats`, `pause`, `resume`, `compact`, `reload`, `quit`.

## Firewall

```cmd
tools\firewall.cmd
```

Asks for UAC, reads the real ports from `bin\config.ini`, creates rules scoped to
`DigServer.exe`:

* `dig web` — TCP 8090, LAN sources by default;
* `dig admin` — TCP 8095, private sources only;
* `dig dht` — UDP 6881, any source (the DHT is peer to peer).

Then prints listening sockets, local IPv4 addresses and a health check.

```cmd
tools\firewall.cmd -PublicAny      # direct expose of the web port, no proxy
tools\firewall.cmd -DhtLanOnly     # do not accept DHT traffic from the internet
tools\firewall.cmd -Remove         # delete the rules
```

> Nginx Proxy Manager in Docker cannot reach `127.0.0.1` of the host — point the
> proxy at the LAN IP printed by the script.

## Reverse proxy

TLS terminates at the proxy; the app speaks plain HTTP.

```nginx
server {
    listen 443 ssl http2;
    server_name dig.example.com;

    location / {
        proxy_pass http://192.168.1.50:8090;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_read_timeout 30s;
    }
}
```

In `config.ini`:

```ini
[site]
baseUrl=https://dig.example.com

[server]
publicBasePath=
trustProxyHeaders=true
trustedProxies=127.0.0.1,::1,10.0.0.0/8,172.16.0.0/12,192.168.0.0/16
hsts=true
```

Published under a sub-path (`example.com/dig/`) instead? Set
`server.publicBasePath=/dig` and proxy `location /dig/ { proxy_pass
http://host:8090/dig/; }`. Links, cookies, `sitemap.xml` and the manifest all
follow that prefix.

## Running as a Windows service

The exe installs itself; there is no separate wrapper and no MSI.

```cmd
tools\service.cmd            :: install + start (UAC prompt)
tools\service.cmd -status    :: state + health check
tools\service.cmd -restart
tools\service.cmd -stop
tools\service.cmd -remove
```

What it does under the hood: `sc create dig binPath= "<dir>\DigServer.exe --service"
start= auto obj= LocalSystem`, plus a failure policy that restarts the service
after 5 s, 10 s and 30 s. The service runs in session 0 as `LocalSystem`, with its
working directory in `bin\`, so `data/`, `logs/` and `config.ini` are the same ones
the console run uses.

Notes:

* Rebuilding while the service runs fails — the compiler cannot overwrite a
  running exe. `sc stop dig` first (admin), or `tools\service.cmd -stop`.
* `--service` from an ordinary console does **not** hang waiting for a service
  dispatcher: it detects an interactive session and runs headless instead.
* The service logs everything to `bin\logs\dig-*.log`; the Application event log
  only gets the SCM's own start/stop entries.
* `tools\ci-test.ps1` deliberately leaves a running service alone and tests the
  existing binary on its own ports. Killing the service would make the SCM record
  an unexpected termination.

## Running as a scheduled task

If you prefer not to install a service:

```cmd
schtasks /create /tn "dig" /tr "E:\dig\bin\DigServer.exe --headless" ^
  /sc onstart /ru SYSTEM /rl HIGHEST /f
schtasks /run /tn "dig"
```

Either way the process must run in `bin\` (it sets its working directory to the
exe location anyway) and needs write access to `data/` and `logs/`.

## First-day tuning

1. Let it run for an hour, then read `/api/stats`:
   * `dht.nodes` should be in the hundreds. Under 50 → UDP is blocked.
   * `crawler.fetched` should grow. Flat while `queue` grows → raise
     `metadata.concurrency`.
2. Set `[index] snapshotIntervalMin=10` (default) so compaction happens during
   quiet periods; the file is rewritten atomically, no downtime.
3. Watch `bin\data\index.db` growth for a day to predict disk needs. A busy
   instance adds 50–200 MB/day of text at the default harvest rate.

## Backups

Only two things matter:

* `bin\data\index.db` — the index (rebuildable, but expensive);
* `bin\config.ini` — ports, token, secrets, branding.

`data/node.id` is worth keeping too: a stable node id keeps you in other nodes'
routing tables. Copy the files while the service runs (`index.db` is opened with
`FileShare.Read`); for a guaranteed consistent snapshot stop the service or use
`POST /api/compact` on the admin board first.

## Upgrading

```cmd
build.cmd
```

`bin\config.ini`, `data/` and `logs/` are untouched by the build. New config keys
fall back to their defaults when absent, so an old `config.ini` keeps working.
Read `CHANGELOG.md` for anything that needs a manual edit.

## Docker (optional)

Not shipped: the project targets .NET Framework 4.0, which does not run under
Linux. If you need containers, run it on a Windows container image with .NET
Framework 4.x, or use Wine — neither is tested. A Linux port would be a rewrite of
`Web/HttpCore.cs` and `Dht/DhtNode.cs` onto `HttpListener`/`Socket` async APIs
plus a Mono/`dotnet` build; the store and search layers would carry over unchanged.
