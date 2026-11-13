# Roadmap

Ordered by value, not by effort. Everything here keeps the .NET 4.0 / no-NuGet
constraint unless it says otherwise.

## Now (works)

- [x] mainline DHT node: ping / find_node / get_peers / announce_peer
- [x] BEP 51 `sample_infohashes` harvesting
- [x] BEP 9 `ut_metadata` fetcher with SHA-1 verification
- [x] append-only index with atomic compaction
- [x] in-memory inverted search index with AND/OR and substring fallback
- [x] server-rendered pages, RU/EN, three themes
- [x] JSON API, sitemap, robots, manifest
- [x] separate admin board with token → HMAC cookie
- [x] blocklist, rate limits, firewall script, CI, offline self-test

## Next

- [ ] **On-disk postings with an LRU cache** — lifts the RAM ceiling. Same query
      code, new storage layer behind `SearchIndex`.
- [ ] **Prefix index** — removes the linear fallback for `ubun*`.
- [ ] **Re-check seeders** — periodically `get_peers` for indexed hashes so the
      seeder count is not a snapshot from fetch time.
- [ ] **Duplicate grouping** — same name + size, different hashes; show the best
      one and a "N copies" badge.
- [ ] **Category guesser** — derive `video / audio / game / software / book` from
      file extensions and show a filter row.
- [ ] **Cyrillic/Latin transliteration** — "фильм" should find "film" releases.
- [ ] **ETag on search pages** — cheap cache for crawlers hitting `/search?q=`.

## Later

- [ ] **Sharded index** — instances own a hash range, the web layer fans out and
      merges. Enables horizontal scale without a cluster framework.
- [ ] **Persistent blocklist reasons** — show "removed on request" instead of a
      bare 404.
- [ ] **OpenSearch description** — let browsers add the engine.
- [ ] **RSS / Atom feed** for the newest additions.
- [ ] **Prometheus-style `/metrics`** — text format, no client library needed.
- [ ] **Torrent file download** — reconstruct a `.torrent` from the cached info
      dict. Technically easy, legally sensitive; off by default if it ever lands.
- [ ] **Linux port** — new HTTP and socket layer on `dotnet`, store and search
      reused unchanged.

## Explicitly not planned

* User accounts, comments, votes. This is an index, not a community.
* Hosting, seeding or proxying content.
* Scraping other indexers. The DHT is the source of truth here.
* A JavaScript SPA. Server-rendered HTML is faster, cacheable and works without JS.

## Ideas worth writing down before they are forgotten

* **Hash neighbours**: when a hash resolves, ask `get_peers` for it and harvest
  every peer address as a *seed* for other hashes via `sample_infohashes` from
  those peers. Peers that hold one torrent often hold related ones.
* **Popularity from announces**: count `announce_peer` events per hash per hour —
  that is a free "trending" signal with no extra traffic.
* **Cheap dedupe of dead torrents**: if a hash fails metadata three times over a
  week, drop it from the retry queue entirely and stop spending sockets on it.
* **Name-only mode**: some torrents never resolve (private, dead). Their hashes
  are still useful as "seen" evidence — a "hash only" entry with no name is noise
  in search but gold for coverage statistics.
