# Importing old records

The DHT cannot be enumerated backwards. It only holds what peers are announcing
**right now**, so "find everything that ever existed" is not a thing the network
allows. What *is* possible:

1. **Index the .torrent files you already have** — complete metadata, offline, no
   network, instant.
2. **Keep every hash you have seen but could not resolve** — the crawler retries
   them on every start, so an old torrent resolves the moment a peer reappears.
3. **Feed in hash lists from anywhere** — dumps, backups, other indexes.

All three paths verify the info hash with SHA-1 before anything is stored.

## Import .torrent files

```cmd
bin\DigServer.exe --import D:\torrents
bin\DigServer.exe --import D:\torrents\ubuntu-24.04.torrent
bin\DigServer.exe --import D:\torrents --no-recursive
```

* Directories are walked recursively by default.
* `.txt`, `.magnet` and `.list` files in the same tree are read as hash lists.
* Duplicates are counted, not re-added.
* The run ends with a compaction, so the index is in a clean state afterwards.

The info hash is computed from the **raw bytes** of the info dictionary, not from a
re-encoded copy. That matters: re-encoding can reorder keys and produce a different
hash, which would make every imported torrent unresolvable. `Store/TorrentFileReader.cs`
walks the bencode structure and remembers the exact byte span.

Timestamps: the torrent's file modification time is used as `lastSeen`, so an
imported 2011 release does not look like it appeared today.

## Import hash lists

```cmd
bin\DigServer.exe --import-hashes hashes.txt
```

Accepted per line:

```
0123456789abcdef0123456789abcdef01234567
0123456789abcdef0123456789abcdef01234567    DMCA - reported
magnet:?xt=urn:btih:FEDCBA9876543210FEDCBA9876543210FEDCBA98&dn=whatever
```

Lines starting with `#` are comments. The parser finds a 40-character hash
anywhere in the line, so pasted tables work.

Imported hashes go to `data/pending.txt`, **not** to the index: a hash alone has no
name, size or file list, so indexing it would just add noise. The fetcher picks
them up on the next start.

## The pending list

`data/pending.txt` holds hashes that were seen but never resolved:

* written when a hash exhausts its retries (`metadata.maxPeersPerHash` peers, three
  attempts);
* capped by `[index] pendingHashesMax` (default 500 000), oldest dropped first;
* replayed slowly on every start (200 hashes every 2 s) so a huge backlog never
  starves live traffic;
* capped by the same rate limiter as fresh traffic.

This is the closest thing to "recover the past": the record of a dead torrent
survives, and if a peer ever comes back, the metadata follows within minutes.

## Why this is the practical limit

| Want | Reality |
|---|---|
| "all torrents ever" | impossible: the DHT is a live index, not an archive |
| "everything from tracker X" | only what its peers still announce |
| "rescan from 2010" | no archive exists; see below |

Realistic sources of old data, in order of value:

1. your own `.torrent` collection (best: full metadata);
2. hash dumps from public datasets and research papers;
3. hash lists from other indexers, if their terms allow it;
4. `pending.txt` — your own accumulated history.

Note that many public hash dumps are dead: a 2012 dump mostly contains torrents no
peer holds any more. They still cost a `get_peers` round each, so import them into
`pending.txt` and let the retry queue work through them instead of expecting
instant results.

## Scale

Import speed is bound by disk and SHA-1, not by the network:

| Work | Rough rate |
|---|---|
| 100 000 `.torrent` files | 1–3 minutes on a modern box |
| 1 000 000 hashes into `pending.txt` | ~20 seconds |
| replaying 1 000 000 pending hashes | days (rate limited, by design) |

The pending replay is intentionally slow: it must never compete with live harvest
for the fetcher's 32 slots.
