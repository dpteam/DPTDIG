# Address index (privacy)

`/ip/<address>` answers one question: **which torrents has this address been seen
with?** It is the same idea as iknowwhatyoudownload, built from data the DHT
already broadcasts.

## Where the data comes from

The DHT publishes peer addresses constantly:

| Source | What it gives |
|---|---|
| `announce_peer` we receive | the announcing peer's address + info hash |
| `get_peers` answers we get while resolving a hash | compact peer lists (6 bytes each) |
| `announce_peer` we forward | same as above |

Nothing is probed, scraped or guessed. If a peer never announces and never appears
in a peer list we fetched, it does not exist for us.

## What a record means — and does not

A record means exactly one thing:

> this address appeared in a peer list for this torrent at this time

It does **not** mean a download happened, that the address downloaded the whole
torrent, or that a person at that address did anything. One address can be:

* a household behind one NAT;
* an office, a school, a café;
* a mobile carrier's CGNAT gateway serving thousands of subscribers;
* a VPN or Tor exit shared by strangers.

The UI says this in plain words on every `/ip/` page, and the page carries
`X-Robots-Tag: noindex, nofollow, noarchive` plus a `robots` meta tag, so it cannot
be indexed by search engines.

## Configuration

```ini
[privacy]
peerTracking=true        ; false = no /ip/ pages, nothing written to disk
retentionDays=30         ; 0 = keep forever
maxIps=200000            ; hard cap, least recently seen evicted first
maxHashesPerIp=200       ; per-address cap
maskLastOctet=false      ; true = store 1.2.3.0 instead of 1.2.3.4
publicLookup=true        ; false = the pages disappear entirely
```

Turning `peerTracking` off is a full stop: the store is not loaded, nothing is
recorded, the routes answer 404 and `data/peers.db` is left untouched on disk (so
you can decide later whether to delete it).

`maskLastOctet=true` keeps the useful part (provider, region) and drops the
subscriber — a good middle ground for a public instance.

## Storage

`data/peers.db` is plain text, one fact per line:

```
# address|info hash|first seen|last seen (unix seconds)
193.123.238.33|43b2d05390b7d13c5ce47769d393b8e4d326b7e9|1794611897|1794611897
```

* Appends happen in batches from a queue; a crash loses at most a few seconds.
* Compaction rewrites the file through a temp file + atomic replace, after pruning.
* Retention is enforced at load and every 6 hours.
* Private ranges (10/8, 172.16/12, 192.168/16, 169.254/16, 100.64/10, loopback)
  are never stored — they are meaningless on the public internet and usually
  belong to the operator's own LAN.

## API

```
GET /api/ip/203.0.113.7
```

```json
{
  "ok": true,
  "ip": "203.0.113.7",
  "count": 2,
  "torrents": [
    { "hash": "43b2...", "name": "ubuntu-24.04-desktop-amd64.iso", "size": 5905580032,
      "firstSeen": 1794609375, "lastSeen": 1794613375 }
  ]
}
```

`name` is `null` when the hash was seen but never resolved — the record is still
real, the metadata is just missing.

## Why this is bounded

An open-ended address index is a liability. The caps exist so that a flood of fake
announces cannot turn the disk into a database of noise, and the retention window
means the answer to "what did this address do" fades on its own. Both are cheap to
tighten:

```ini
retentionDays=7
maxIps=50000
maskLastOctet=true
```

## Legal note

This mirrors publicly broadcast metadata; it does not observe traffic. Still, an
operator publishing `/ip/` pages should be aware that in some jurisdictions this is
treated as personal data. The honest position is the one the page states: an
address is not a person, and a peer list is not a download log. If your jurisdiction
disagrees, set `publicLookup=false` and keep the data for your own stats only, or
`peerTracking=false` and keep nothing at all.
