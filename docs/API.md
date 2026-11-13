# API

Base: `http://your-host:8090/api/` — JSON, UTF-8, no authentication on the public
side, no cookies. Admin endpoints live on the **admin port only**.

All responses are JSON objects. Errors use the HTTP status code and this shape:

```json
{ "ok": false, "error": "not found" }
```

## GET /api/search

Full-text search over indexed names and file lists.

| Parameter | Default | Description |
|---|---|---|
| `q` | *(empty)* | query, up to `limits.maxQueryLength` (120) characters |
| `page` | `1` | 1-based page number |
| `per` | `25` | page size, clamped to `limits.resultsPerPage`..`limits.maxResultsPerPage` |
| `sort` | `relevance` | `relevance`, `new`, `size`, `seeders` |

An empty `q` lists the newest additions.

```bash
curl 'http://localhost:8090/api/search?q=ubuntu&per=5&sort=seeders'
```

```json
{
  "query": "ubuntu",
  "page": 1,
  "pages": 3,
  "perPage": 5,
  "total": 12,
  "tookMs": 4,
  "sort": "seeders",
  "results": [
    {
      "hash": "43f3cd230601099a98ca0d5454394065971421d9",
      "name": "ubuntu-24.04-desktop-amd64.iso",
      "size": 5905580032,
      "fileCount": 1,
      "seeders": 42,
      "leechers": 3,
      "firstSeen": 1794609375,
      "lastSeen": 1794613375,
      "magnet": "magnet:?xt=urn:btih:43f3...&dn=ubuntu-24.04-desktop-amd64.iso",
      "url": "https://dig.example.com/t/43f3..."
    }
  ]
}
```

* `size` is in bytes, timestamps are Unix seconds (UTC).
* `tookMs` is the server-side search time, useful for tuning.
* `total` is the number of matches, not the number of returned items.

## GET /api/torrent/{hash}

One torrent with its file list. `{hash}` is the 40-character info hash; a
trailing path segment is ignored (`/api/torrent/<hash>/anything`).

```json
{
  "hash": "43f3cd230601099a98ca0d5454394065971421d9",
  "name": "ubuntu-24.04-desktop-amd64.iso",
  "size": 5905580032,
  "fileCount": 1,
  "seeders": 42,
  "leechers": 3,
  "firstSeen": 1794609375,
  "lastSeen": 1794613375,
  "hits": 1,
  "magnet": "magnet:?xt=urn:btih:...",
  "url": "https://dig.example.com/t/43f3...",
  "files": [ { "path": "ubuntu-24.04-desktop-amd64.iso", "size": 5905580032 } ]
}
```

`404` when the hash is unknown or blocked.

## GET /api/stats

Instance counters. Safe to poll; the public page uses it for live numbers.

```json
{
  "ok": true,
  "version": "1.0.0",
  "uptimeSec": 3600,
  "torrents": 128400,
  "bytes": 4200000000000,
  "addedToday": 2104,
  "requests": 98211,
  "dht": {
    "enabled": true, "port": 6881, "nodes": 412,
    "queries": 512400, "responses": 318222, "queriesIn": 4401,
    "self": "43b4b6702c69299dfaa2d22060f5ca922419c850"
  },
  "crawler": {
    "paused": false, "harvested": 8842011, "announces": 55120,
    "dropped": 0, "fetched": 128400, "failed": 7712, "blocked": 3,
    "queue": 41, "active": 32
  },
  "index": { "terms": 5120000, "documents": 128400 },
  "peers": { "enabled": true, "addresses": 39412, "records": 120883, "retentionDays": 30 }
}
```

## GET /api/ip/{address}

Torrents that this address was seen with in public DHT peer lists. Only available
when `[privacy] peerTracking=true` and `publicLookup=true`; otherwise `404`.

```json
{
  "ok": true,
  "ip": "203.0.113.7",
  "count": 1,
  "torrents": [
    {
      "hash": "43f3cd230601099a98ca0d5454394065971421d9",
      "name": "ubuntu-24.04-desktop-amd64.iso",
      "size": 5905580032,
      "firstSeen": 1794609375,
      "lastSeen": 1794613375
    }
  ]
}
```

`name` is `null` when the hash was never resolved. Invalid input → `400`.

A record means "this address appeared in a peer list", nothing more — see
[PRIVACY.md](PRIVACY.md).

## GET /api/health

Liveness probe for a reverse proxy or an uptime monitor. `200` while the process
serves requests; `503`-style failures are reported by the proxy, not here.

```json
{ "ok": true, "uptimeSec": 3600, "torrents": 128400, "nodes": 412 }
```

## Non-API endpoints

| Path | Purpose |
|---|---|
| `/robots.txt` | honours `[site] noIndex` || `/sitemap.xml` | home, newest search, last 500 torrent pages |
| `/brand.css` | palette generated from `config.ini [branding]` |
| `/manifest.webmanifest` | PWA metadata, branding aware |
| `/assets/*` | static files from `public/`, ETag + gzip |
| `/ip` | address lookup form (noindex) |
| `/ip/{address}` | torrents seen from an address (noindex) |

## Admin API (admin port only)

Requires the `digadm` cookie, obtained by posting the token to `/login`.
Without it every admin endpoint answers `401 {"ok":false,"error":"unauthorized"}`.

| Method | Path | Purpose |
|---|---|---|
| GET | `/api/stats` | same payload as the public stats |
| GET | `/api/log?lines=200&since=<seq>` | log ring buffer, `since` for tailing |
| GET | `/api/config` | effective config, **secrets redacted** |
| POST | `/api/compact` | rewrite and compact the index file |
| POST | `/api/crawl?action=pause\|resume\|toggle` | stop or resume harvesting |
| POST | `/api/reload` | re-read `data/blocklist.txt` |

`/api/config` never returns `adminToken` or `serverSecret`; it only reports
`adminTokenSet: true`.

## Rate limits

| Scope | Default | Key |
|---|---|---|
| public requests | 600/min | client IP (proxy-aware) |
| search | shares the above | — |
| admin login attempts | 10/min | client IP |

Over the limit: `429` with `Retry-After: 60`. Set `server.requestsPerMinute=0`
to disable (not recommended on a public host).

## CORS

Deliberately absent. The API is meant to be consumed server-side; a browser
front end on another origin should proxy through its own backend.

## Client example

```bash
# search and open the first magnet in a client that accepts magnet links
hash=$(curl -s 'http://localhost:8090/api/search?q=ubuntu&per=1' | grep -o '"hash":"[^"]*"' | cut -d'"' -f4)
xdg-open "magnet:?xt=urn:btih:$hash"
```
