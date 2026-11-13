# Search

The index answers one question fast: *which torrents mention these words?*

## Documents

A document is one torrent. Its searchable text is:

* the torrent name, tokenized into at most 24 tokens;
* the first 12 file paths, 6 tokens each.

Tokens are lowercased alphanumeric runs of 2–48 characters. A small stop-word list
drops filler (`the`, `part`, `disc`, `cd1`, `dvd`, `iso`...) so that a query like
"the office" does not match every release with `part` in the name.

Names are kept exactly as published (UTF-8, any script) for display; only the
token forms are lowercased for matching.

## Structure

```
token  ->  [docId, docId, ...]        (sorted, append-only)
```

`SortedDictionary<string, List<int>>` in memory. Adding a document walks its token
set and appends the id to each posting list. There is no delete: a blocked torrent
is filtered at read time.

Memory: roughly 8 bytes per posting entry plus the string key, so a million
torrents with ~10 unique tokens each lands around 100–200 MB of postings. That is
the practical ceiling of the current design and the reason for the sharding plan
below.

## Query evaluation

1. Tokenize the query (max 8 tokens).
2. Look up every token. **AND** the posting lists — a torrent must contain all
   terms.
3. If the intersection is empty, fall back to the union (**OR**) so a
   half-remembered name still returns something.
4. If there are still no candidates and the query is at least 2 characters, do a
   linear substring scan over the newest 300 000 documents (prefixes, partial
   words, CJK strings without spaces). This is the slow path and it is capped.
5. Score and sort.

## Scoring

```
score  = Σ tokenWeight
       + 60        if the whole query appears as a substring of the name
       + 3·ln(1+seeders)
       + 0.5·max(0, 30 - daysSinceLastSeen)

tokenWeight = 8 + 2·ln(N / (1 + df))     // rarity, BM25-flavoured
```

A token found only in file paths (not in the name) contributes 25 % of its weight.
Ties break on `lastSeen`, newest first.

`sort=new|size|seeders` bypasses scoring entirely and sorts on the stored field.

## Complexity

| Query | Cost |
|---|---|
| common term | O(df) to build the candidate set, O(k log k) to sort k matches |
| two terms | O(df₁ + df₂) set work |
| substring fallback | O(300k) string comparisons, bounded |

Measured on this box: 100k documents, a two-word query returns in 3–8 ms.

## Known limits and the next step

* **No stemming, no synonyms.** "movies" does not match "movie". Cheap to add for
  English; pointless for CJK.
* **No ranking signals beyond seeders and recency.** No click data, and we should
  not invent any.
* **Postings live in RAM.** A restart rebuilds them from the index file in a few
  seconds per million documents.
* **Single writer.** Fine for one box; a second box would need a shared index.

The planned upgrade, in order of value:

1. **On-disk postings with an LRU cache** — removes the RAM ceiling, keeps the
   same query code.
2. **Prefix index** — a trie or a sorted-key binary search removes the linear
   fallback for prefix queries.
3. **Sharded index across instances** — each instance owns a hash range, the web
   layer fans out and merges. This is what makes "big" possible without a cluster
   framework.

None of these change the API or the storage format, so they can land later
without a migration.
