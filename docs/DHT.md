# DHT crawling

The crawler speaks the mainline BitTorrent DHT. Three specifications matter:

| BEP | Name | Used for |
|---|---|---|
| 5 | DHT protocol | `ping`, `find_node`, `get_peers`, `announce_peer` |
| 9 | Extension for peers | `ut_metadata` — the info dictionary |
| 51 | `sample_infohashes` | bulk hash discovery, the efficient path |

## Node identity

A 20-byte node id is generated once and stored in `data/node.id`. Keeping it
stable means other nodes keep us in their routing tables, which is what makes the
node useful across restarts. Delete the file to get a new identity.

The id is random rather than derived from the IP: that is slightly rude to the
network but avoids leaking the server address into the DHT.

## Harvesting hashes

Three independent sources, all cheap:

1. **`sample_infohashes` (BEP 51)** — every `dht.sampleIntervalMs` (15 s) we ask
   `dht.sampleNodesPerRound` (24) random routing-table nodes for up to 20 hashes
   each. Nodes that do not implement BEP 51 answer with error 204 and are simply
   skipped. This is the main feed.
2. **`announce_peer`** — when a peer announces a torrent through us we see the
   info hash and the peer's address, i.e. a ready-to-use fetch job.
3. **`get_peers`** — any node asking us for peers reveals an info hash. Enabled
   with `dht.harvestGetPeers`.

Every new hash passes a global rate limit (`dht.maxInfohashesPerMin`, default
6000/min) and the blocklist. Over the cap, hashes are dropped and counted, never
queued — the DHT is infinite, disk is not.

## Routing table

160 buckets × 8 contacts. A bucket fills with contacts sharing a prefix length
with our id; when it is full, a stale contact (>20 min) is replaced, otherwise the
newcomer is ignored. Three consecutive failures evict a contact.

The table is a tool, not a goal: `dht.routingTableSize` caps it, and the crawler
works fine with a few hundred live nodes.

## Turning a hash into a name

Bare hashes have no name. `MetadataFetcher`:

1. finds peers — from the announce, or by an iterative `get_peers` over six random
   nodes (two rounds);
2. opens TCP, sends the 68-byte handshake with extension bit `0x10`;
3. requires the peer's handshake to echo the info hash and advertise extensions;
4. sends `{"m":{"ut_metadata":1}}` on extension id 0;
5. reads the peer's extension handshake, takes `metadata_size` and the peer's
   `ut_metadata` id, requests every 16 KiB piece;
6. reassembles the info dictionary and verifies `SHA1(info) == info_hash`;
7. parses name, size and file list; rejects anything malformed.

Up to `metadata.maxPeersPerHash` (8) peers are tried per hash. A hash that fails
three times is parked for `metadata.retryAfterMin` (180 min) and then retried —
popular torrents appear later with more peers.

**Nothing is ever written to disk before the SHA-1 check passes**, so the index
cannot be poisoned with mismatched metadata.

## Cost

| Resource | Default | Note |
|---|---|---|
| UDP | ~50 packets/s | sampling + find_node walks |
| TCP out | 32 concurrent | short-lived metadata fetches |
| Disk | ~200–400 B/torrent | plus file lists |
| RAM | ~1 KB/torrent | names, sizes, file paths, postings |

A cheap VPS with 1 vCPU / 1 GB RAM indexes a few hundred thousand torrents per
day. The bottleneck is metadata fetches, not bandwidth.

## Tuning

```ini
[dht]
sampleNodesPerRound=24      ; more = more hashes, more UDP noise
maxInfohashesPerMin=6000    ; raise only if the fetcher keeps up
queryConcurrency=48         ; KRPC queries in flight

[metadata]
concurrency=32              ; raise to ~64 on a fast box
maxPeersPerHash=8           ; raise for rare torrents
```

Signals to watch in `/api/stats`:

* `crawler.queue` growing and `crawler.fetched` flat → the fetcher is the
  bottleneck; raise `metadata.concurrency` or lower the harvest rate.
* `dht.nodes` under ~50 → outbound UDP is probably blocked; check the firewall
  script and `dht.bootstrap`.
* `crawler.failed` close to `fetched` → most peers do not serve metadata (normal
  for dead torrents; the retry queue handles it).

## Politeness

* Every query is a normal KRPC query with our own node id — we are a participant,
  not a scraper of a single node.
* The rate limiter is global, so a burst of announces cannot flood the fetcher.
* `sample_infohashes` answers are used for discovery only; we do not re-announce
  other people's hashes.
* `metadata.enabled=false` turns the crawler into a pure hash collector, useful
  when the box must not open outbound TCP.
