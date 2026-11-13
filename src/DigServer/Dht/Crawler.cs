using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using Dig.Core;
using Dig.Store;

namespace Dig.Dht
{
    /// <summary>
    /// Drives the crawl: keeps the routing table fresh, samples info hashes (BEP 51),
    /// harvests announce_peer traffic and hands hashes to the metadata fetcher.
    /// Everything is rate limited so a single box can run unattended.
    /// </summary>
    public sealed class Crawler
    {
        private readonly Config _cfg;
        private readonly DhtNode _dht;
        private readonly TorrentStore _store;
        private readonly MetadataFetcher _fetcher;

        private readonly object _knownLock = new object();
        private readonly HashSet<string> _known = new HashSet<string>(StringComparer.Ordinal);
        private readonly Queue<string> _knownOrder = new Queue<string>();
        private readonly object _rateLock = new object();
        private readonly HashSet<string> _blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Semaphore _sampleSlots;

        private Thread _crawlThread;
        private Thread _sampleThread;
        private volatile bool _running;
        private volatile bool _paused;

        private DateTime _rateWindow = DateTime.UtcNow;
        private int _rateCount;
        private DateTime _nextBootstrap = DateTime.MinValue;
        private DateTime _nextCacheSave = DateTime.UtcNow.AddSeconds(60);
        private int _bootstrapAttempts;

        private long _harvested;
        private long _queued;
        private long _dropped;
        private long _announces;
        private long _blockedHits;
        private long _sampled;
        private long _fetched;
        private long _failed;
        private long _storeErrors;

        public Crawler(Config cfg, DhtNode dht, TorrentStore store)
        {
            _cfg = cfg;
            _dht = dht;
            _store = store;
            _fetcher = new MetadataFetcher(cfg, dht);
            _sampleSlots = new Semaphore(Math.Max(4, cfg.QueryConcurrency / 2), Math.Max(4, cfg.QueryConcurrency / 2));

            _fetcher.OnFetched = OnFetched;
            _fetcher.OnFailed = OnFailed;
            dht.OnAnnounce += OnAnnounce;
            dht.OnInfohash += OnInfohash;
        }

        public MetadataFetcher Fetcher { get { return _fetcher; } }
        public bool Paused { get { return _paused; } set { _paused = value; } }
        public long Harvested { get { return Interlocked.Read(ref _harvested); } }
        public long Queued { get { return Interlocked.Read(ref _queued); } }
        public long Dropped { get { return Interlocked.Read(ref _dropped); } }
        public long Announces { get { return Interlocked.Read(ref _announces); } }
        public long BlockedHits { get { return Interlocked.Read(ref _blockedHits); } }
        public long Sampled { get { return Interlocked.Read(ref _sampled); } }
        public long Fetched { get { return Interlocked.Read(ref _fetched); } }
        public long Failed { get { return Interlocked.Read(ref _failed); } }
        public long StoreErrors { get { return Interlocked.Read(ref _storeErrors); } }
        public int KnownCount { get { lock (_knownLock) { return _known.Count; } } }

        // ------------------------------------------------------------------- start

        public void Start()
        {
            LoadBlocklist();
            _fetcher.Start();
            _running = true;

            _crawlThread = new Thread(CrawlLoop);
            _crawlThread.IsBackground = true;
            _crawlThread.Name = "dht-crawl";
            _crawlThread.Start();

            _sampleThread = new Thread(SampleLoop);
            _sampleThread.IsBackground = true;
            _sampleThread.Name = "dht-sample";
            _sampleThread.Start();

            Log.Info("CRAWL", "crawler started (sample=" + (_cfg.SampleEnabled ? "on" : "off") +
                              ", harvest announce=" + (_cfg.HarvestAnnounce ? "on" : "off") +
                              ", get_peers=" + (_cfg.HarvestGetPeers ? "on" : "off") + ")");
        }

        public void Stop()
        {
            _running = false;
            try { if (_crawlThread != null) _crawlThread.Join(2000); } catch { }
            try { if (_sampleThread != null) _sampleThread.Join(2000); } catch { }
            try { _fetcher.Stop(); } catch { }
            try { _dht.SaveCache(); } catch { }
            Log.Info("CRAWL", "crawler stopped");
        }

        // ------------------------------------------------------------------ harvest

        private void OnAnnounce(byte[] hash, IPEndPoint peer)
        {
            Interlocked.Increment(ref _announces);
            Harvest(hash, peer, 1, 0);
        }

        private void OnInfohash(byte[] hash)
        {
            Harvest(hash, null, 0, 0);
        }

        public void Harvest(byte[] hash, IPEndPoint peer, int seeders, int leechers)
        {
            if (hash == null || hash.Length != 20 || _paused) return;
            string hex = Hex.Encode(hash);

            if (_blocked.Contains(hex))
            {
                Interlocked.Increment(ref _blockedHits);
                return;
            }

            if (!AllowRate())
            {
                Interlocked.Increment(ref _dropped);
                return;
            }

            lock (_knownLock)
            {
                if (!_known.Add(hex)) return;
                _knownOrder.Enqueue(hex);
                if (_known.Count > 1000000)
                {
                    for (int i = 0; i < 200000 && _knownOrder.Count > 0; i++)
                        _known.Remove(_knownOrder.Dequeue());
                }
            }

            Interlocked.Increment(ref _harvested);
            HashJob job = new HashJob
            {
                Hash = hash,
                Hex = hex,
                Peer = peer,
                Seeders = seeders,
                Leechers = leechers
            };

            if (!_fetcher.Enqueue(job))
            {
                Interlocked.Increment(ref _dropped);
                return;
            }
            Interlocked.Increment(ref _queued);
        }

        private bool AllowRate()
        {
            lock (_rateLock)
            {
                DateTime now = DateTime.UtcNow;
                if ((now - _rateWindow).TotalSeconds >= 60)
                {
                    _rateWindow = now;
                    _rateCount = 0;
                }
                if (_rateCount >= _cfg.MaxInfohashesPerMin) return false;
                _rateCount++;
                return true;
            }
        }

        // -------------------------------------------------------------------- loops

        private void CrawlLoop()
        {
            while (_running)
            {
                try
                {
                    _dht.SweepPending();

                    if (_dht.Nodes < 8 && DateTime.UtcNow >= _nextBootstrap)
                    {
                        Bootstrap();
                    }
                    else if (_dht.Nodes > 0)
                    {
                        List<DhtPeerNode> nodes = _dht.RandomNodes(8);
                        for (int i = 0; i < nodes.Count; i++)
                        {
                            // fire and forget: the answer only feeds the routing table
                            _dht.FindNode(nodes[i].EndPoint, _dht.RandomId(), 0);
                        }
                    }

                    if (DateTime.UtcNow >= _nextCacheSave && _dht.Nodes >= 8)
                    {
                        _nextCacheSave = DateTime.UtcNow.AddSeconds(60);
                        try { _dht.SaveCache(); } catch { }
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn("CRAWL", "crawl loop: " + ex.Message);
                }
                Sleep(_cfg.CrawlIntervalMs);
            }
        }

        /// <summary>
        /// Bootstraps with an exponential back-off. The public routers rate limit
        /// clients that ask too often, so hammering them is worse than waiting.
        /// </summary>
        private void Bootstrap()
        {
            int alive = 0;
            try
            {
                // the on-disk cache first: no router involved, and it survives restarts
                alive = _dht.SeedFromCache();
                if (alive == 0) alive = _dht.Bootstrap();
            }
            catch (Exception ex)
            {
                Log.Warn("CRAWL", "bootstrap: " + ex.Message);
            }

            if (alive > 0)
            {
                _bootstrapAttempts = 0;
                _nextBootstrap = DateTime.UtcNow.AddSeconds(30);
                try { _dht.SaveCache(); } catch { }
                return;
            }

            _bootstrapAttempts++;
            int delaySec = Math.Min(300, 20 << Math.Min(_bootstrapAttempts, 4));
            _nextBootstrap = DateTime.UtcNow.AddSeconds(delaySec);
            Log.Warn("CRAWL", "no DHT contact answered; next attempt in " + delaySec + "s " +
                             "(routers rate limit clients that retry too fast - inbound UDP " +
                             "from the internet helps a lot)");
        }

        private void SampleLoop()
        {
            while (_running)
            {
                Sleep(_cfg.SampleIntervalMs);
                if (!_running) break;
                if (!_cfg.SampleEnabled) continue;

                try
                {
                    List<DhtPeerNode> nodes = _dht.RandomNodes(_cfg.SampleNodesPerRound);
                    for (int i = 0; i < nodes.Count; i++)
                    {
                        if (!_sampleSlots.WaitOne(0)) break;
                        ThreadPool.QueueUserWorkItem(SampleOne, nodes[i]);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn("CRAWL", "sample loop: " + ex.Message);
                }
            }
        }

        private void SampleOne(object state)
        {
            try
            {
                DhtPeerNode node = (DhtPeerNode)state;
                if (node == null) return;
                Dictionary<string, object> response = _dht.SampleInfohashes(node.EndPoint, _dht.RandomId(), 3000);
                if (response == null) return;

                Interlocked.Increment(ref _sampled);
                byte[] samples = Bencode.Get(response, "samples") as byte[];
                if (samples == null || samples.Length < 20) return;

                for (int offset = 0; offset + 20 <= samples.Length; offset += 20)
                {
                    byte[] hash = new byte[20];
                    Buffer.BlockCopy(samples, offset, hash, 0, 20);
                    Harvest(hash, null, 0, 0);
                }
            }
            catch (Exception ex)
            {
                Log.Dht("CRAWL", "sample: " + ex.Message);
            }
            finally
            {
                try { _sampleSlots.Release(); } catch { }
            }
        }

        // ------------------------------------------------------------------ results

        private void OnFetched(HashJob job, TorrentInfo info)
        {
            Interlocked.Increment(ref _fetched);
            try
            {
                Torrent torrent = new Torrent();
                torrent.Hash = info.Hash;
                torrent.Hex = info.HexHash;
                torrent.Name = info.Name;
                torrent.Size = info.Size;
                torrent.FileCount = info.FileCount;
                torrent.Files = info.Files;
                torrent.Seeders = info.Seeders;
                torrent.Leechers = info.Leechers;
                long now = Clock.Now();
                torrent.FirstSeen = now;
                torrent.LastSeen = now;
                torrent.Hits = 1;
                _store.Add(torrent);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _storeErrors);
                Log.Warn("CRAWL", "store " + job.Hex + ": " + ex.Message);
            }
        }

        private void OnFailed(HashJob job, string error)
        {
            Interlocked.Increment(ref _failed);
            Log.Dht("CRAWL", "give up " + job.Hex.Substring(0, 8) + "\u2026: " + error);
        }

        // --------------------------------------------------------------- blocklist

        public void LoadBlocklist()
        {
            HashSet<string> fresh = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string path = _cfg.Resolve(_cfg.BlocklistFile);
            try
            {
                if (File.Exists(path))
                {
                    string[] lines = File.ReadAllLines(path);
                    for (int i = 0; i < lines.Length; i++)
                    {
                        string line = lines[i].Trim();
                        if (line.Length == 0 || line[0] == '#') continue;
                        int tab = line.IndexOf('\t');
                        if (tab < 0) tab = line.IndexOf(' ');
                        string hash = tab > 0 ? line.Substring(0, tab).Trim() : line;
                        if (Hex.IsHash40(hash)) fresh.Add(hash);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("BLOCK", "cannot read " + path + ": " + ex.Message);
            }

            lock (_blocked)
            {
                _blocked.Clear();
                foreach (string hash in fresh) _blocked.Add(hash);
            }
            Log.Info("BLOCK", "blocklist loaded: " + fresh.Count + " hashes");
        }

        public bool IsBlocked(string hex)
        {
            return !string.IsNullOrEmpty(hex) && _blocked.Contains(hex);
        }

        public int BlockedCount { get { lock (_blocked) { return _blocked.Count; } } }

        private void Sleep(int ms)
        {
            int remaining = ms;
            while (remaining > 0 && _running)
            {
                int slice = Math.Min(remaining, 250);
                Thread.Sleep(slice);
                remaining -= slice;
            }
        }
    }
}
