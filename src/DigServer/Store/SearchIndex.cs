using System;
using System.Collections.Generic;
using Dig.Core;

namespace Dig.Store
{
    public sealed class SearchResult
    {
        public List<Torrent> Items = new List<Torrent>();
        public int Total;
        public int Page = 1;
        public int Pages = 1;
        public string Query = "";
        public string Sort = "relevance";
        public int TookMs;
    }

    /// <summary>
    /// In-memory inverted index over torrent names and file paths.
    ///
    /// token -> sorted list of document ids. Queries are intersected (AND), with an
    /// OR fallback so a half-remembered name still finds something. Scoring is a
    /// small BM25-flavoured formula: token rarity, an exact substring boost, seeders
    /// and freshness.
    ///
    /// Known limit: prefix queries fall back to a linear scan of the newest 300k
    /// documents. Fine for a single box; a sharded on-disk index is the next step
    /// (see docs/SEARCH.md).
    /// </summary>
    public sealed class SearchIndex
    {
        private readonly object _sync = new object();
        private readonly SortedDictionary<string, List<int>> _postings =
            new SortedDictionary<string, List<int>>(StringComparer.Ordinal);

        private List<Torrent> _docs = new List<Torrent>();
        private const int MaxNameTokens = 24;
        private const int MaxFileTokens = 12;
        private const int LinearScanLimit = 300000;

        public int Documents { get { lock (_sync) { return _docs.Count; } } }
        public int Terms { get { lock (_sync) { return _postings.Count; } } }

        public void Attach(List<Torrent> docs)
        {
            lock (_sync)
            {
                _docs = docs ?? new List<Torrent>();
                _postings.Clear();
                for (int i = 0; i < _docs.Count; i++) AddNoLock(_docs[i], i);
            }
            Log.Info("SEARCH", "index built: " + _docs.Count + " documents, " + Terms + " terms");
        }

        public void Add(Torrent torrent, int id)
        {
            lock (_sync) { AddNoLock(torrent, id); }
        }

        private void AddNoLock(Torrent torrent, int id)
        {
            if (torrent == null) return;

            // The store owns the canonical list; the index keeps the same order so
            // posting lists can hold plain integer ids. New documents arrive with
            // id == count, so growing here is enough.
            while (_docs.Count <= id) _docs.Add(null);
            _docs[id] = torrent;

            HashSet<string> tokens = new HashSet<string>(StringComparer.Ordinal);

            List<string> nameTokens = Text.Tokenize(torrent.Name, MaxNameTokens);
            for (int i = 0; i < nameTokens.Count; i++) tokens.Add(nameTokens[i]);

            if (torrent.Files != null)
            {
                int limit = Math.Min(torrent.Files.Count, MaxFileTokens);
                for (int i = 0; i < limit; i++)
                {
                    List<string> fileTokens = Text.Tokenize(torrent.Files[i].Path, 6);
                    for (int j = 0; j < fileTokens.Count; j++) tokens.Add(fileTokens[j]);
                }
            }

            foreach (string token in tokens)
            {
                List<int> list;
                if (!_postings.TryGetValue(token, out list))
                {
                    list = new List<int>(4);
                    _postings[token] = list;
                }
                if (list.Count == 0 || list[list.Count - 1] != id) list.Add(id);
            }
        }

        public SearchResult Search(string query, int page, int perPage, string sort)
        {
            DateTime started = DateTime.UtcNow;
            if (page < 1) page = 1;
            if (perPage < 1) perPage = 25;

            SearchResult result = new SearchResult();
            result.Query = query ?? "";
            result.Sort = string.IsNullOrEmpty(sort) ? "relevance" : sort;

            lock (_sync)
            {
                if (_docs.Count == 0)
                {
                    result.TookMs = Elapsed(started);
                    return result;
                }

                List<string> tokens = Text.Tokenize(query, 8);
                HashSet<int> candidates = null;
                Dictionary<string, double> weights = new Dictionary<string, double>(StringComparer.Ordinal);
                bool anyTerm = false;

                for (int i = 0; i < tokens.Count; i++)
                {
                    List<int> list;
                    if (!_postings.TryGetValue(tokens[i], out list) || list.Count == 0) continue;
                    anyTerm = true;
                    double idf = Math.Log((double)Math.Max(2, _docs.Count) / (1.0 + list.Count));
                    weights[tokens[i]] = 8.0 + idf * 2.0;

                    HashSet<int> set = new HashSet<int>(list);
                    if (candidates == null) candidates = set;
                    else candidates.IntersectWith(set);
                }

                if (candidates == null || candidates.Count == 0)
                {
                    // OR fallback: a half-remembered name still deserves results.
                    candidates = new HashSet<int>();
                    for (int i = 0; i < tokens.Count; i++)
                    {
                        List<int> list;
                        if (_postings.TryGetValue(tokens[i], out list))
                            for (int j = 0; j < list.Count; j++) candidates.Add(list[j]);
                    }
                }

                if (candidates.Count == 0 && !string.IsNullOrEmpty(query))
                {
                    candidates = LinearFallback(query);
                }

                if (candidates.Count == 0 && tokens.Count == 0)
                {
                    // empty query = browse everything, newest first
                    for (int i = 0; i < _docs.Count; i++) candidates.Add(i);
                }

                if (candidates.Count == 0 && !anyTerm && !string.IsNullOrEmpty(query))
                {
                    candidates = LinearFallback(query);
                }

                List<Scored> scored = new List<Scored>(candidates.Count);
                string queryLower = (query ?? "").Trim().ToLowerInvariant();
                long now = Clock.Now();

                foreach (int id in candidates)
                {
                    if (id < 0 || id >= _docs.Count) continue;
                    Torrent t = _docs[id];
                    if (t == null) continue;

                    double score = 0;
                    string nameLower = t.Name == null ? "" : t.Name.ToLowerInvariant();

                    foreach (KeyValuePair<string, double> kv in weights)
                    {
                        if (nameLower.IndexOf(kv.Key, StringComparison.Ordinal) >= 0) score += kv.Value;
                        else score += kv.Value * 0.25; // matched a file name only
                    }

                    if (queryLower.Length >= 3 && nameLower.IndexOf(queryLower, StringComparison.Ordinal) >= 0)
                        score += 60;

                    if (t.Seeders > 0) score += Math.Log(1 + t.Seeders) * 3.0;

                    double days = Math.Max(0, (now - t.LastSeen) / 86400.0);
                    score += Math.Max(0, 30 - days) * 0.5;

                    scored.Add(new Scored { Id = id, Score = score });
                }

                Comparison<Scored> comparer;
                if (result.Sort == "new")
                    comparer = delegate(Scored a, Scored b) { return _docs[b.Id].LastSeen.CompareTo(_docs[a.Id].LastSeen); };
                else if (result.Sort == "size")
                    comparer = delegate(Scored a, Scored b) { return _docs[b.Id].Size.CompareTo(_docs[a.Id].Size); };
                else if (result.Sort == "seeders")
                    comparer = delegate(Scored a, Scored b) { return _docs[b.Id].Seeders.CompareTo(_docs[a.Id].Seeders); };
                else
                    comparer = delegate(Scored a, Scored b)
                    {
                        int byScore = b.Score.CompareTo(a.Score);
                        return byScore != 0 ? byScore : _docs[b.Id].LastSeen.CompareTo(_docs[a.Id].LastSeen);
                    };

                scored.Sort(comparer);

                result.Total = scored.Count;
                result.Pages = Math.Max(1, (scored.Count + perPage - 1) / perPage);
                if (page > result.Pages) page = result.Pages;
                result.Page = page;

                int start = (page - 1) * perPage;
                int end = Math.Min(scored.Count, start + perPage);
                for (int i = start; i < end; i++) result.Items.Add(_docs[scored[i].Id]);
            }

            result.TookMs = Elapsed(started);
            return result;
        }

        private HashSet<int> LinearFallback(string query)
        {
            HashSet<int> found = new HashSet<int>();
            string needle = query.Trim();
            if (needle.Length < 2) return found;

            int start = Math.Max(0, _docs.Count - LinearScanLimit);
            for (int i = _docs.Count - 1; i >= start; i--)
            {
                Torrent t = _docs[i];
                if (t == null || string.IsNullOrEmpty(t.Name)) continue;
                if (t.Name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    found.Add(i);
                    if (found.Count >= 2000) break;
                }
            }
            return found;
        }

        private static int Elapsed(DateTime started)
        {
            return (int)Math.Max(0, (DateTime.UtcNow - started).TotalMilliseconds);
        }

        private struct Scored
        {
            public int Id;
            public double Score;
        }
    }
}
