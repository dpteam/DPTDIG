# Changelog

All notable changes to this project. Format loosely follows Keep a Changelog;
versions are `MAJOR.MINOR.PATCH`.

## [1.0.0] - 2026-11-14

First public cut. A complete DHT search engine in one .NET 4.0 executable.

### Added

**Crawler**

- Mainline DHT node (BEP 5): `ping`, `find_node`, `get_peers`, `announce_peer`,
  with a 160×8 Kademlia routing table and stale-contact eviction.
- BEP 51 `sample_infohashes` discovery, configurable interval and fan-out.
- Hash harvesting from incoming `announce_peer` and `get_peers` traffic.
- Global rate limiter on new hashes (default 6000/min) and a bounded dedupe set.
- Persistent node identity in `data/node.id`.
- Blocklist enforced at harvest time, reloadable without a restart.

**Metadata**

- BEP 9 `ut_metadata` fetcher: extension handshake, piece requests, reassembly.
- SHA-1 verification against the info hash before anything is stored or parsed.
- Multi-file and single-file torrents, `name.utf-8` / `path.utf-8` preferred.
- Per-hash peer retry queue with configurable back-off.
- Private-range peers never dialled.

**Index**

- Append-only record log with a torn-tail-tolerant reader.
- Atomic compaction through a temp file + `File.Replace`.
- In-memory inverted search index: AND with OR fallback, bounded substring scan.
- BM25-flavoured scoring with seeder and freshness boosts.
- Sorts: relevance, newest, largest, most seeders.

**Web**

- Hand-written HTTP/1.1 server (no `HttpListener`, no URL ACL, XP-safe).
- Server-rendered pages: home, search, torrent, stats, about, abuse, errors.
- RU/EN interface, light / dark / OLED themes, copy buttons, `/` hotkey.
- JSON API: `/api/search`, `/api/torrent/{hash}`, `/api/stats`, `/api/health`.
- `/robots.txt`, `/sitemap.xml`, `/manifest.webmanifest`, ETag + gzip.
- Strict CSP and the usual hardening headers; proxy-aware client IP.
- Separate admin board: token → HMAC cookie, live stats, log tail, compaction,
  crawler pause/resume, blocklist reload, redacted config view.

**Operations**

- INI configuration with quarantine of broken files and auto-generated secrets.
- Trace-based rolling file logging with an in-memory ring for the admin panel.
- `--no-dht`, `--no-fetch`, `--headless`, `--self-test`, port overrides.
- Interactive console: `stats`, `pause`, `resume`, `compact`, `reload`, `quit`.
- `build.cmd` with stock `csc.exe`, `run.cmd`, `tools\firewall.cmd`.
- Offline self-test suite (bencode, metadata parsing, tokenizer, store, search).
- GitHub Actions workflow that builds, self-tests, smoke-tests and packages.

### Known limitations

- Single writer, in-memory postings: practical ceiling is a few million torrents
  on one box. See `docs/SEARCH.md` for the sharding plan.
- No stemming or transliteration; prefix queries fall back to a bounded scan.
- Seeders are a snapshot from fetch time, not live.
- Windows only (`.NET Framework 4.0`).
