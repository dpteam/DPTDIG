# Architecture

```
                    ┌──────────────────────────────────────────────┐
   UDP 6881         │                  DigServer.exe               │
  ┌──────────┐      │                                              │
  │  DHT     │◄────►│  DhtNode ── RoutingTable (160 buckets)       │
  │  network │      │      │                                       │
  └──────────┘      │      │ sample_infohashes / announce_peer     │
                    │      ▼                                       │
  ┌──────────┐      │  Crawler ── rate limiter ── blocklist        │
  │  peers   │      │      │                                       │
  │ (TCP)    │◄────►│      ▼                                       │
  └──────────┘      │  MetadataFetcher (BEP 9, ut_metadata)        │
                    │      │  verified with SHA-1                 │
                    │      ▼                                       │
                    │  TorrentStore (append log + compaction)      │
                    │      │                                       │
                    │      ▼                                       │
                    │  SearchIndex (inverted, in memory)           │
                    │      │                                       │
                    │      ▼                                       │
                    │  WebServer (public)  WebServer (admin)       │
                    └──────┬───────────────────────┬───────────────┘
                           │ TCP 8090              │ TCP 8095
                           ▼                       ▼
                    browser / crawler        operator (LAN)
```

## Layers

| Layer | Namespace | Responsibility |
|---|---|---|
| Core | `Dig.Core` | bencode, hex/crypto, config, logging, JSON, i18n, branding |
| DHT | `Dig.Dht` | KRPC node, routing table, crawler, metadata fetcher |
| Store | `Dig.Store` | torrent model, append-only index, search index |
| Web | `Dig.Web` | HTTP/1.1 server, pages, JSON API, admin board |

Nothing crosses these boundaries except plain data: the web layer never touches a
socket of the DHT, and the DHT never touches HTTP. That is what makes the
`--no-dht` viewer mode (index only, no network) and the `--no-fetch` mode
(harvest hashes, do not open TCP) possible without special cases.

## Process model

* One process, several threads, no external services.
* `DhtNode` runs one receive thread; all outbound queries are synchronous with a
  timeout and a pending-transaction table (2-byte transaction ids).
* `MetadataFetcher` runs N worker threads (default 32), each doing a short
  connect/handshake/fetch/close cycle. A worker never holds a lock while a socket
  blocks.
* `WebServer` uses `TcpListener` + a semaphore for concurrency; each connection is
  a `ThreadPool` work item. Two instances (public, admin) live in the same process.
* `TorrentStore` has one writer, guarded by a lock; readers copy out the list.
  Compaction happens on a background thread with a temp file + atomic replace.

## Why no database

The project targets .NET 4.0 with no NuGet, no native DLLs and XP compatibility.
SQLite via `System.Data.SQLite` would drag a native binary per architecture.
Instead:

* the index is an append-only log of length-prefixed records (torn tail tolerated);
* the working set lives in memory (names, sizes, file lists);
* the search index is a token → document-id inverted list.

The target is a single box indexing a few million torrents. Beyond that, see
[SEARCH.md](SEARCH.md) for the sharding plan.

## Data flow of one torrent

1. **Harvest** — a hash arrives from `sample_infohashes` (BEP 51), from an
   incoming `announce_peer`, or from a `get_peers` query another node sent us.
2. **Dedupe** — a bounded `HashSet` keeps the last ~1M hashes; the per-minute rate
   limiter caps new hashes (default 6000/min).
3. **Resolve peers** — if the hash came without a peer, an iterative `get_peers`
   over a few routing-table nodes produces compact peer addresses.
4. **Fetch metadata** — TCP handshake with the extension bit, `ut_metadata`
   handshake, piece requests, SHA-1 verification against the info hash.
5. **Parse** — name (`name.utf-8` first), total size, file list, truncation at
   `index.maxFiles`.
6. **Store** — one record appended to `data/index.db`; memory and the inverted
   index updated in the same lock.
7. **Serve** — HTML for people and crawlers, JSON for clients.

## Failure behaviour

| Failure | What happens |
|---|---|
| No UDP port available | DHT logs an error, web board keeps serving the existing index |
| Bootstrap unreachable | crawler retries every 20 s, harvest still works from inbound queries |
| Peer hangs | connect/read timeouts (4 s / 8 s), hash parked for a retry |
| Bad metadata | SHA-1 mismatch → discarded, counted in `crawler.failed` |
| Disk full | write errors counted, reads keep working; `index.db` stays readable |
| Torn write on power loss | the incomplete record at the tail is skipped on load |
| Config broken | file quarantined as `config.ini.bad-*`, defaults used |
| Admin port busy | logged as a warning, the public board is unaffected |

## Configuration surface

`config.ini` is the only place a deployment customises behaviour and identity:
site name, colours, ports, limits, DHT parameters, blocklist path. The repository
ships `config.example.ini` with neutral values, and `config.ini` is generated on
first start (and is git-ignored). See [BRANDING.md](BRANDING.md).
