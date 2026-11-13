using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using Dig.Core;

namespace Dig.Store
{
    /// <summary>One "this address was seen on this torrent" fact.</summary>
    public sealed class PeerHit
    {
        public string Ip;
        public string Hash;
        public long FirstSeen;
        public long LastSeen;
    }

    /// <summary>Everything seen from one address.</summary>
    public sealed class IpRecord
    {
        public string Ip;
        public long LastSeen;
        public readonly Dictionary<string, PeerHit> Hits = new Dictionary<string, PeerHit>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Which addresses were seen holding which torrents.
    ///
    /// The DHT publishes this constantly: every announce_peer names a peer, and
    /// every get_peers answer carries compact peer lists. We only mirror what the
    /// network already broadcasts, and we never claim an identity - an IP is not a
    /// person, and a NAT or a VPN exit belongs to many.
    ///
    /// Bounded on purpose: a hard cap on addresses, a cap per address, and a
    /// retention window. Masking the last octet is one config flag away.
    /// </summary>
    public sealed class PeerStore
    {
        private readonly Config _cfg;
        private readonly string _path;
        private readonly object _sync = new object();
        private readonly Dictionary<string, IpRecord> _byIp = new Dictionary<string, IpRecord>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, HashSet<string>> _byHash = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        private readonly Queue<string> _ipOrder = new Queue<string>();

        private StreamWriter _writer;
        private long _records;
        private long _dropped;
        private long _writeErrors;
        private long _lastPrune = DateTime.UtcNow.Ticks;
        private bool _dirty;

        public PeerStore(Config cfg)
        {
            _cfg = cfg;
            _path = cfg.DataPath("peers.db");
        }

        public bool Enabled { get { return _cfg.PeerTrackingEnabled; } }
        public int IpCount { get { lock (_sync) { return _byIp.Count; } } }
        public long RecordCount { get { lock (_sync) { return _records; } } }
        public long Dropped { get { return Interlocked.Read(ref _dropped); } }
        public long WriteErrors { get { return Interlocked.Read(ref _writeErrors); } }

        // -------------------------------------------------------------------- load

        public void Load()
        {
            if (!Enabled) return;
            lock (_sync)
            {
                _byIp.Clear();
                _byHash.Clear();
                _ipOrder.Clear();
                _records = 0;

                if (!File.Exists(_path))
                {
                    OpenAppend();
                    return;
                }

                int broken = 0;
                try
                {
                    using (StreamReader reader = new StreamReader(_path, Encoding.UTF8, true))
                    {
                        string line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            if (line.Length == 0 || line[0] == '#') continue;
                            string[] parts = line.Split('|');
                            if (parts.Length < 4) { broken++; continue; }

                            long first, last;
                            if (!long.TryParse(parts[2], out first) || !long.TryParse(parts[3], out last)) { broken++; continue; }
                            if (!Hex.IsHash40(parts[1])) { broken++; continue; }
                            if (!IsUsableAddress(parts[0])) { broken++; continue; }

                            ApplyNoLock(parts[0], parts[1].ToLowerInvariant(), first, last, false);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("PEERS", "cannot read peers.db: " + ex.Message);
                }

                OpenAppend();
                Log.Info("PEERS", "loaded " + _byIp.Count + " addresses / " + _records + " records" +
                                  (broken > 0 ? " (" + broken + " broken lines skipped)" : ""));
            }
            Prune();
        }

        private void OpenAppend()
        {
            try
            {
                string dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                FileStream fs = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read);
                _writer = new StreamWriter(fs, new UTF8Encoding(false));
                _writer.AutoFlush = false;
            }
            catch (Exception ex)
            {
                Log.Error("PEERS", "cannot open peers.db for writing: " + ex.Message);
            }
        }

        // ------------------------------------------------------------------- note

        /// <summary>Records that an address was seen on a torrent. Cheap and idempotent.</summary>
        public void Note(IPAddress address, string hashHex)
        {
            if (!Enabled || address == null || string.IsNullOrEmpty(hashHex)) return;
            string ip = Normalize(address);
            if (ip == null) return;
            Note(ip, hashHex);
        }

        public void Note(string ip, string hashHex)
        {
            if (!Enabled || string.IsNullOrEmpty(ip) || string.IsNullOrEmpty(hashHex)) return;
            if (hashHex.Length != 40 || !Hex.IsHash40(hashHex)) return;

            long now = Clock.Now();
            lock (_sync)
            {
                bool isNew = ApplyNoLock(ip, hashHex.ToLowerInvariant(), now, now, true);
                if (isNew) _dirty = true;
                if (_dirty && _writer != null) FlushNoLock();
            }
        }

        /// <summary>Returns true when a new record was created.</summary>
        private bool ApplyNoLock(string ip, string hash, long first, long last, bool enforceCaps)
        {
            IpRecord record;
            if (!_byIp.TryGetValue(ip, out record))
            {
                if (enforceCaps && _byIp.Count >= _cfg.PeerMaxIps && !EvictOneNoLock())
                {
                    Interlocked.Increment(ref _dropped);
                    return false;
                }
                record = new IpRecord { Ip = ip, LastSeen = last };
                _byIp[ip] = record;
                _ipOrder.Enqueue(ip);
            }

            PeerHit hit;
            if (record.Hits.TryGetValue(hash, out hit))
            {
                if (last > hit.LastSeen) hit.LastSeen = last;
                if (first < hit.FirstSeen) hit.FirstSeen = first;
                if (last > record.LastSeen) record.LastSeen = last;
                return false;
            }

            if (enforceCaps && record.Hits.Count >= _cfg.PeerMaxHashesPerIp)
            {
                Interlocked.Increment(ref _dropped);
                return false;
            }

            hit = new PeerHit { Ip = ip, Hash = hash, FirstSeen = first, LastSeen = last };
            record.Hits[hash] = hit;
            if (last > record.LastSeen) record.LastSeen = last;
            _records++;
            _pending.Enqueue(hit);

            HashSet<string> set;
            if (!_byHash.TryGetValue(hash, out set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _byHash[hash] = set;
            }
            set.Add(ip);
            return true;
        }

        /// <summary>Drops the least recently seen address to make room for a new one.</summary>
        private bool EvictOneNoLock()
        {
            int attempts = Math.Min(_ipOrder.Count, 2000);
            for (int i = 0; i < attempts; i++)
            {
                string candidate = _ipOrder.Dequeue();
                IpRecord record;
                if (!_byIp.TryGetValue(candidate, out record)) continue;
                if (_ipOrder.Count > 0 && record.LastSeen > Clock.Now() - 3600) 
                {
                    _ipOrder.Enqueue(candidate);
                    continue;
                }
                _byIp.Remove(candidate);
                _records -= record.Hits.Count;
                foreach (KeyValuePair<string, PeerHit> hit in record.Hits)
                {
                    HashSet<string> set;
                    if (_byHash.TryGetValue(hit.Key, out set))
                    {
                        set.Remove(candidate);
                        if (set.Count == 0) _byHash.Remove(hit.Key);
                    }
                }
                return true;
            }
            return false;
        }

        private void FlushNoLock()
        {
            if (_writer == null) return;
            if (_pending.Count == 0) { _dirty = false; return; }
            try
            {
                StringBuilder sb = new StringBuilder(4096);
                int written = 0;
                while (_pending.Count > 0 && written < 500)
                {
                    PeerHit hit = _pending.Dequeue();
                    sb.Append(hit.Ip).Append('|').Append(hit.Hash).Append('|')
                      .Append(hit.FirstSeen).Append('|').Append(hit.LastSeen).Append('\n');
                    written++;
                }
                _writer.Write(sb.ToString());
                _writer.Flush();
                _dirty = _pending.Count > 0;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _writeErrors);
                Log.Warn("PEERS", "append failed: " + ex.Message);
            }
        }

        private readonly Queue<PeerHit> _pending = new Queue<PeerHit>();

        // ------------------------------------------------------------------ lookup

        public List<PeerHit> ForIp(string ip)
        {
            List<PeerHit> result = new List<PeerHit>();
            if (!Enabled || string.IsNullOrEmpty(ip)) return result;
            string key = NormalizeText(ip);
            if (key == null) return result;

            lock (_sync)
            {
                IpRecord record;
                if (!_byIp.TryGetValue(key, out record)) return result;
                foreach (KeyValuePair<string, PeerHit> kv in record.Hits) result.Add(kv.Value);
            }
            result.Sort(delegate(PeerHit a, PeerHit b) { return b.LastSeen.CompareTo(a.LastSeen); });
            return result;
        }

        public List<string> IpsForHash(string hashHex)
        {
            List<string> result = new List<string>();
            if (!Enabled || string.IsNullOrEmpty(hashHex)) return result;
            lock (_sync)
            {
                HashSet<string> set;
                if (_byHash.TryGetValue(hashHex, out set)) result.AddRange(set);
            }
            return result;
        }

        public int IpCountForHash(string hashHex)
        {
            if (!Enabled || string.IsNullOrEmpty(hashHex)) return 0;
            lock (_sync)
            {
                HashSet<string> set;
                return _byHash.TryGetValue(hashHex, out set) ? set.Count : 0;
            }
        }

        // ------------------------------------------------------------------ upkeep

        public void Prune()
        {
            if (!Enabled) return;
            if (_cfg.PeerRetentionDays <= 0) return;
            long cutoff = Clock.Now() - _cfg.PeerRetentionDays * 86400L;

            int removed = 0;
            lock (_sync)
            {
                List<string> deadIps = new List<string>();
                foreach (KeyValuePair<string, IpRecord> kv in _byIp)
                {
                    List<string> deadHashes = null;
                    foreach (KeyValuePair<string, PeerHit> hit in kv.Value.Hits)
                    {
                        if (hit.Value.LastSeen >= cutoff) continue;
                        if (deadHashes == null) deadHashes = new List<string>();
                        deadHashes.Add(hit.Key);
                    }
                    if (deadHashes != null)
                    {
                        for (int i = 0; i < deadHashes.Count; i++)
                        {
                            kv.Value.Hits.Remove(deadHashes[i]);
                            HashSet<string> set;
                            if (_byHash.TryGetValue(deadHashes[i], out set))
                            {
                                set.Remove(kv.Key);
                                if (set.Count == 0) _byHash.Remove(deadHashes[i]);
                            }
                            _records--;
                            removed++;
                        }
                    }
                    if (kv.Value.Hits.Count == 0) deadIps.Add(kv.Key);
                }
                for (int i = 0; i < deadIps.Count; i++) _byIp.Remove(deadIps[i]);
            }

            if (removed > 0) Log.Info("PEERS", "retention pruned " + removed + " records");
            _lastPrune = DateTime.UtcNow.Ticks;
            Compact();
        }

        /// <summary>Rewrites peers.db from memory. Called after pruning and on shutdown.</summary>
        public void Compact()
        {
            if (!Enabled) return;
            lock (_sync)
            {
                try
                {
                    if (_writer != null) { try { _writer.Flush(); _writer.Dispose(); } catch { } _writer = null; }

                    string tmp = _path + ".tmp";
                    using (StreamWriter w = new StreamWriter(tmp, false, new UTF8Encoding(false)))
                    {
                        w.WriteLine("# address|info hash|first seen|last seen (unix seconds)");
                        foreach (KeyValuePair<string, IpRecord> kv in _byIp)
                        {
                            foreach (KeyValuePair<string, PeerHit> hit in kv.Value.Hits)
                            {
                                w.Write(kv.Key); w.Write('|');
                                w.Write(hit.Key); w.Write('|');
                                w.Write(hit.Value.FirstSeen); w.Write('|');
                                w.WriteLine(hit.Value.LastSeen);
                            }
                        }
                    }

                    if (File.Exists(_path))
                    {
                        try { File.Replace(tmp, _path, null, true); }
                        catch { File.Copy(tmp, _path, true); try { File.Delete(tmp); } catch { } }
                    }
                    else
                    {
                        File.Move(tmp, _path);
                    }

                    _dirty = false;
                    OpenAppend();
                    Log.Debug("PEERS", "compacted " + _byIp.Count + " addresses / " + _records + " records");
                }
                catch (Exception ex)
                {
                    Log.Warn("PEERS", "compaction failed: " + ex.Message);
                    if (_writer == null) OpenAppend();
                }
            }
        }

        public void Housekeeping()
        {
            if (!Enabled) return;
            long now = DateTime.UtcNow.Ticks;
            if ((now - _lastPrune) < TimeSpan.FromHours(6).Ticks) return;
            Prune();
        }

        public void Close()
        {
            lock (_sync)
            {
                try { if (_writer != null) { _writer.Flush(); _writer.Dispose(); } } catch { }
                _writer = null;
            }
            if (Enabled) Log.Info("PEERS", "peer index closed, " + IpCount + " addresses");
        }

        // ----------------------------------------------------------------- helpers

        private string Normalize(IPAddress address)
        {
            if (address == null) return null;
            if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return null;
            if (NetUtil.IsPrivate(address)) return null;
            byte[] bytes = address.GetAddressBytes();
            if (_cfg.PeerMaskLastOctet) bytes[3] = 0;
            return new IPAddress(bytes).ToString();
        }

        private string NormalizeText(string ip)
        {
            IPAddress parsed;
            if (!IPAddress.TryParse((ip ?? "").Trim(), out parsed)) return null;
            return Normalize(parsed);
        }

        private bool IsUsableAddress(string ip)
        {
            IPAddress parsed;
            if (!IPAddress.TryParse(ip, out parsed)) return false;
            return !NetUtil.IsPrivate(parsed);
        }
    }
}
