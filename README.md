# dig

**A self-hosted search engine for the public BitTorrent DHT.**

It walks the distributed hash table, harvests info hashes, fetches torrent
metadata from peers and indexes names, file lists and sizes. One `.exe`, no
NuGet, no database server, no files hosted.

<p align="center">
  <img src="public/assets/img/logo.svg" alt="dig" width="96" height="96">
</p>

<p align="center">
  <code>.NET Framework 4.0</code> · <code>C# 4</code> · <code>Windows XP SP3+</code> ·
  <code>zero dependencies</code> · <code>MIT</code>
</p>

---

## What it does

| | |
|---|---|
| **Crawls** | mainline DHT (BEP 5) + `sample_infohashes` (BEP 51) |
| **Fetches** | `ut_metadata` (BEP 9), verified with SHA-1 against the info hash |
| **Indexes** | name, total size, file list, first/last seen, seeder count |
| **Remembers** | which address was seen with which torrent (bounded, opt-out) |
| **Imports** | your own `.torrent` files and hash lists, offline |
| **Serves** | server-rendered HTML, JSON API, sitemap, robots |
| **Never** | hosts, stores or transfers any actual file |

## Quick start

```cmd
build.cmd
run.cmd
```

```
  PUBLIC:  http://localhost:8090/
  ADMIN :  http://localhost:8095/
  token :  <generated on first start>
```

The first run creates `bin\config.ini`, generates the admin token and secrets,
joins the DHT and starts filling the index. Give it ten minutes before judging the
result list — a cold index is empty by definition.

Then open `tools\firewall.cmd` once, so the DHT can actually receive UDP.

Run it as a Windows service instead (auto-start, restarts on failure):

```cmd
tools\service.cmd
```

## How it works

```
DHT (UDP 6881) ──► harvest hashes ──► get_peers ──► ut_metadata (TCP)
                                                        │
                                                   SHA-1 check
                                                        │
                                              append-only index ──► inverted
                                                        │            index
                                                        ▼              │
                                            HTML + JSON API ◄──────────┘
```

* **Harvest** — three cheap sources: BEP 51 samples, incoming `announce_peer`,
  incoming `get_peers`. Every hash passes a rate limit and the blocklist.
* **Resolve** — a hash with no peer attached gets an iterative `get_peers` over a
  few routing-table nodes.
* **Fetch** — a short TCP connection, extension handshake, piece-by-piece
  `ut_metadata`. The result is accepted only if `SHA1(info) == info_hash`.
* **Index** — one length-prefixed record appended to `data/index.db`, plus an
  in-memory posting list per token.
* **Serve** — HTML for people and crawlers; JSON for anything else.

Details: [ARCHITECTURE](docs/ARCHITECTURE.md) · [DHT](docs/DHT.md) ·
[SEARCH](docs/SEARCH.md) · [API](docs/API.md).

## Features

**Crawler**
- Kademlia routing table, 160 buckets × 8 contacts, stale eviction
- BEP 51 sampling with configurable interval and fan-out
- Harvest from announces and queries, not just samples
- Global hash rate limit, bounded dedupe, persistent node id
- Blocklist enforced at harvest time, hot reload

**Metadata**
- BEP 9 `ut_metadata`, up to 8 peers per hash, configurable concurrency
- SHA-1 verification *before* parsing or storing — no poisoned index
- Private-range peers never dialled
- Retry queue with back-off for torrents that were dead at fetch time

**Index**
- Append-only log, torn-tail tolerant, atomic compaction, no database
- Inverted index: AND with OR fallback, bounded substring scan for prefixes
- BM25-flavoured scoring + seeder and freshness boosts
- Four sort orders, blocklist filtering at read time
- Unresolved hashes kept in `data/pending.txt` and retried on every start
- Offline import of `.torrent` files and hash lists

**Address index**
- `/ip/<address>`: which torrents an address was seen with
- Built from `announce_peer` and `get_peers` — public DHT data only
- Retention, hard caps, optional last-octet masking, one flag to disable
- `noindex` pages, never in the sitemap

**Web**
- Hand-written HTTP/1.1 (no `HttpListener`, no URL ACL, XP-safe)
- Server-rendered HTML — works with JavaScript disabled
- RU/EN, light / dark / OLED, copy-to-clipboard, `/` to focus search
- JSON API, sitemap, robots, PWA manifest, ETag + gzip
- Strict CSP and hardening headers, proxy-aware client IP

**Admin**
- Separate port, token → HMAC-signed cookie, private-only by default
- Live counters, log tail, compaction, crawler pause/resume, blocklist reload
- Config view with secrets redacted

**Operations**
- INI config, unknown keys ignored, broken file quarantined
- Trace-based rolling logs, no `App.config`
- `--no-dht` viewer mode, `--no-fetch` harvest-only mode, `--headless`
- `--self-test` offline suite, CI on every push
- Firewall script that reads the real ports from `config.ini`
- Windows service with one command: `tools\service.cmd`

## Configuration

Everything a deployment might want to rename lives in `config.ini` — the
repository stays neutral, so anyone can run their own instance without a fork.

```ini
[site]
name=dig
shortName=D
tagline=Search the BitTorrent DHT
baseUrl=https://dig.example.com
defaultTheme=dark
defaultLanguage=ru

[branding]
accent=#4f9cf9
darkAccent=#4f9cf9
```

The palette is rendered into `/brand.css` at runtime; no file in `public/` is
edited. See [BRANDING](docs/BRANDING.md) for the full debranding guide.

## API

```bash
curl 'http://localhost:8090/api/search?q=ubuntu&per=5&sort=seeders'
curl 'http://localhost:8090/api/torrent/<40-char-hash>'
curl 'http://localhost:8090/api/ip/203.0.113.7'
curl 'http://localhost:8090/api/stats'
```

Full reference: [docs/API.md](docs/API.md).

## Importing old records

The DHT only shows what peers announce now, so old torrents come from files you
already have:

```cmd
bin\DigServer.exe --import D:\torrents
bin\DigServer.exe --import-hashes hashes.txt
```

Everything you saw but could not resolve is kept in `data/pending.txt` and retried
on every start. Details: [docs/IMPORT.md](docs/IMPORT.md).

## Deploy

Reverse proxy, service setup, tuning, backups: [docs/DEPLOY.md](docs/DEPLOY.md).
Security model and blocklist: [docs/SECURITY.md](docs/SECURITY.md).
Address index and its limits: [docs/PRIVACY.md](docs/PRIVACY.md).

```nginx
location / {
    proxy_pass http://192.168.1.50:8090;
    proxy_set_header X-Real-IP $remote_addr;
    proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
    proxy_set_header X-Forwarded-Proto $scheme;
}
```

## Requirements

* Windows XP SP3 or newer, .NET Framework 4.0
* ~300 bytes of disk per torrent, ~1 KB of RAM per torrent
* Outbound UDP and TCP; inbound UDP strongly recommended

## Tests

```cmd
bin\DigServer.exe --self-test
```

Covers bencode round-trips and malformed input, torrent metadata parsing, the
tokenizer, the store with compaction, and the search index. No sockets, no
network — safe in CI.

```cmd
tools\ci-test.ps1
```

Build, self-test, start on a scratch port, then exercise the pages, the API, the
admin login and path traversal. Exit code 0 means everything answered as expected.

## Legal

This software indexes *public metadata* that the BitTorrent network already
publishes. It hosts nothing and cannot remove a torrent from the DHT. Operators
are responsible for what their instance indexes; the blocklist exists so an
operator can honour a takedown request.

## License

MIT — see [LICENSE](LICENSE).
