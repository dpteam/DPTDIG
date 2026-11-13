using System;
using System.Threading;
using Dig.Core;
using Dig.Dht;
using Dig.Store;

namespace Dig.Web
{
    /// <summary>Everything the request handlers need, wired once in Program.</summary>
    public sealed class ServerContext
    {
        public Config Cfg;
        public TorrentStore Store;
        public SearchIndex Index;
        public PeerStore Peers;
        public DhtNode Dht;
        public Crawler Crawler;
        public string Version = "1.0.0";
        public DateTime StartedUtc = DateTime.UtcNow;
        public volatile bool ShuttingDown;
        public long RequestsServed;

        public Lang Language(HttpRequest request)
        {
            string code = request == null ? null : request.QueryString("lang");
            if (string.IsNullOrEmpty(code) && request != null) code = request.Cookie("diglang");
            if (string.IsNullOrEmpty(code)) code = Cfg.DefaultLanguage;
            return Lang.For(code);
        }

        public string Url(string path)
        {
            return Cfg.BasePath() + (path ?? "/");
        }

        public string AbsoluteUrl(string path)
        {
            return Cfg.Url(path);
        }

        public string Uptime()
        {
            return Clock.Duration(DateTime.UtcNow - StartedUtc);
        }
    }

    /// <summary>Fixed-window per-IP limiter. One dictionary, cleaned lazily.</summary>
    public sealed class RateLimiter
    {
        private sealed class Bucket
        {
            public int Count;
            public long WindowTicks;
        }

        private readonly object _sync = new object();
        private readonly System.Collections.Generic.Dictionary<string, Bucket> _buckets =
            new System.Collections.Generic.Dictionary<string, Bucket>(StringComparer.Ordinal);
        private readonly int _limit;
        private long _lastCleanup = DateTime.UtcNow.Ticks;

        public RateLimiter(int perMinute)
        {
            _limit = perMinute;
        }

        public bool Allow(string key)
        {
            if (_limit <= 0) return true;
            long now = DateTime.UtcNow.Ticks;
            lock (_sync)
            {
                if ((now - _lastCleanup) > TimeSpan.FromMinutes(5).Ticks)
                {
                    _lastCleanup = now;
                    System.Collections.Generic.List<string> dead = new System.Collections.Generic.List<string>();
                    foreach (System.Collections.Generic.KeyValuePair<string, Bucket> kv in _buckets)
                        if ((now - kv.Value.WindowTicks) > TimeSpan.FromMinutes(2).Ticks) dead.Add(kv.Key);
                    for (int i = 0; i < dead.Count; i++) _buckets.Remove(dead[i]);
                }

                Bucket bucket;
                if (!_buckets.TryGetValue(key, out bucket))
                {
                    bucket = new Bucket { WindowTicks = now };
                    _buckets[key] = bucket;
                }
                if ((now - bucket.WindowTicks) >= TimeSpan.FromMinutes(1).Ticks)
                {
                    bucket.WindowTicks = now;
                    bucket.Count = 0;
                }
                bucket.Count++;
                return bucket.Count <= _limit;
            }
        }
    }
}
