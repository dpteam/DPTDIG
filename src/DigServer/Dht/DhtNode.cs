using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Dig.Core;
namespace Dig.Dht
{
    /// <summary>
    /// A mainline DHT node (BEP 5) with the BEP 51 sampling extension.
    ///
    /// It is both a good citizen and a crawler: it answers ping / find_node /
    /// get_peers / announce_peer, keeps a routing table, and watches the traffic it
    /// receives for info hashes. Hashes are the raw material; turning them into
    /// names happens in <see cref="MetadataFetcher"/>.
    /// </summary>
    public sealed class DhtNode : IDisposable
    {
        private sealed class Pending
        {
            public IPEndPoint EndPoint;
            public ManualResetEvent Event;
            public Dictionary<string, object> Response;
            public string Error;
            public long CreatedTicks;
        }

        private readonly Config _cfg;
        private readonly RoutingTable _table;
        private readonly Dictionary<string, Pending> _pending = new Dictionary<string, Pending>(StringComparer.Ordinal);
        private readonly object _pendingLock = new object();
        private readonly object _sendLock = new object();
        private readonly byte[] _secret;

        private UdpClient _udp;
        private Thread _receiveThread;
        private volatile bool _running;
        private byte[] _selfId;
        private int _port;
        private int _tid;
        private long _queriesSent;
        private long _responses;
        private long _queriesIn;
        private long _errors;
        private long _packetsIn;
        private DateTime _startedUtc = DateTime.UtcNow;

        public event Action<byte[], IPEndPoint> OnAnnounce;
        public event Action<byte[]> OnInfohash;

        public DhtNode(Config cfg)
        {
            _cfg = cfg;
            _secret = Crypto.RandomBytes(16);
            _selfId = LoadOrCreateId(cfg);
            _table = new RoutingTable(_selfId, cfg.RoutingTableSize);
        }

        public byte[] SelfId { get { return _selfId; } }
        public string SelfHex { get { return Hex.Encode(_selfId); } }
        public int Port { get { return _port; } }
        public bool Running { get { return _running; } }
        public RoutingTable Table { get { return _table; } }
        public int Nodes { get { return _table.Count; } }
        public long QueriesSent { get { return Interlocked.Read(ref _queriesSent); } }
        public long Responses { get { return Interlocked.Read(ref _responses); } }
        public long QueriesIn { get { return Interlocked.Read(ref _queriesIn); } }
        public long Errors { get { return Interlocked.Read(ref _errors); } }
        public long PacketsIn { get { return Interlocked.Read(ref _packetsIn); } }
        public DateTime StartedUtc { get { return _startedUtc; } }

        // ------------------------------------------------------------------ start

        public bool Start()
        {
            IPAddress bindAddress = IPAddress.Any;
            try
            {
                IPAddress parsed;
                if (!string.IsNullOrEmpty(_cfg.DhtBind) && IPAddress.TryParse(_cfg.DhtBind, out parsed)) bindAddress = parsed;
            }
            catch { }

            int[] ports = _cfg.UdpPorts();
            for (int i = 0; i < ports.Length; i++)
            {
                try
                {
                    UdpClient client = new UdpClient(new IPEndPoint(bindAddress, ports[i]));
                    client.Client.ReceiveTimeout = 1000;
                    client.Client.SendTimeout = 3000;
                    try { client.Client.ReceiveBufferSize = 1024 * 1024; } catch { }
                    _udp = client;
                    _port = ports[i];
                    break;
                }
                catch (Exception ex)
                {
                    Log.Warn("DHT", "cannot bind udp/" + ports[i] + ": " + ex.Message +
                                    (i == 0 ? " - another torrent client (qBittorrent, Transmission) may hold it" : ""));
                }
            }

            if (_udp == null)
            {
                Log.Error("DHT", "no free UDP port for the DHT node");
                return false;
            }

            _running = true;
            _startedUtc = DateTime.UtcNow;
            _receiveThread = new Thread(ReceiveLoop);
            _receiveThread.IsBackground = true;
            _receiveThread.Name = "dht-recv";
            _receiveThread.Start();

            Log.Info("DHT", "node " + SelfHex.Substring(0, 8) + "\u2026 listening on udp/" + _port);
            return true;
        }

        public void Stop()
        {
            _running = false;
            try { if (_udp != null) _udp.Close(); } catch { }
            try { if (_receiveThread != null) _receiveThread.Join(2000); } catch { }
            lock (_pendingLock)
            {
                foreach (KeyValuePair<string, Pending> kv in _pending)
                {
                    if (kv.Value.Event != null) { try { kv.Value.Event.Set(); } catch { } }
                }
                _pending.Clear();
            }
            Log.Info("DHT", "node stopped");
        }

        public void Dispose() { Stop(); }

        // ---------------------------------------------------------------- bootstrap

        /// <summary>Pings the configured bootstrap nodes and seeds the routing table.</summary>
        public int Bootstrap()
        {
            string[] hosts = _cfg.BootstrapList();
            if (hosts.Length == 0) return 0;

            // Resolve first (DNS can be slow), then query all routers in parallel:
            // sequentially they cost a full timeout each and the public routers
            // rate limit clients that look impatient.
            List<IPEndPoint> targets = new List<IPEndPoint>();
            for (int i = 0; i < hosts.Length; i++)
            {
                IPEndPoint ep = NetUtil.ParseEndPoint(hosts[i], 6881);
                if (ep == null)
                {
                    Log.Warn("DHT", "cannot resolve bootstrap node " + hosts[i]);
                    continue;
                }
                targets.Add(ep);
            }
            if (targets.Count == 0) return 0;

            int alive = 0;
            object gate = new object();
            List<WaitHandle> handles = new List<WaitHandle>();
            for (int i = 0; i < targets.Count; i++)
            {
                IPEndPoint ep = targets[i];
                ManualResetEvent done = new ManualResetEvent(false);
                handles.Add(done);
                ThreadPool.QueueUserWorkItem(delegate(object state)
                {
                    try
                    {
                        if (Introduce(ep, "bootstrap"))
                        {
                            lock (gate) alive++;
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Dht("DHT", "bootstrap " + NetUtil.Format(ep) + ": " + ex.Message);
                    }
                    finally
                    {
                        try { done.Set(); } catch { }
                    }
                });
            }

            try { WaitHandle.WaitAll(handles.ToArray(), 8000); }
            catch { }
            for (int i = 0; i < handles.Count; i++) { try { handles[i].Close(); } catch { } }

            Log.Info("DHT", "bootstrap: " + alive + "/" + targets.Count + " nodes answered, table=" + _table.Count);
            return alive;
        }

        /// <summary>
        /// One ping + find_node against a single contact. Returns true when it answered.
        /// </summary>
        public bool Introduce(IPEndPoint ep, string label)
        {
            if (ep == null) return false;
            Dictionary<string, object> ping = Query(ep, "ping", Bencode.Dict(), 3000);
            if (ping == null)
            {
                Log.Debug("DHT", (label ?? "node") + " " + NetUtil.Format(ep) + " did not answer ping");
                return false;
            }
            byte[] id = Bencode.Get(ping, "id") as byte[];
            if (id != null && id.Length == 20) _table.Add(id, ep);

            Dictionary<string, object> args = Bencode.Dict();
            args["target"] = _selfId;
            Query(ep, "find_node", args, 3000);
            return true;
        }

        /// <summary>
        /// Seeds the table from the on-disk cache, then asks a few of those nodes for
        /// their neighbours. Cheap, and it does not depend on the public routers.
        /// </summary>
        public int SeedFromCache()
        {
            List<DhtPeerNode> cached = NodeCache.Load(_cfg);
            if (cached.Count == 0) return 0;

            for (int i = 0; i < cached.Count; i++) _table.Add(cached[i].Id, cached[i].EndPoint);
            Log.Info("DHT", "node cache: " + cached.Count + " contacts loaded");

            int probed = Math.Min(cached.Count, 8);
            int alive = 0;
            object gate = new object();
            List<WaitHandle> handles = new List<WaitHandle>();
            for (int i = 0; i < probed; i++)
            {
                DhtPeerNode node = cached[i];
                ManualResetEvent done = new ManualResetEvent(false);
                handles.Add(done);
                ThreadPool.QueueUserWorkItem(delegate(object state)
                {
                    try
                    {
                        if (_running && Introduce(node.EndPoint, "cache"))
                        {
                            lock (gate) alive++;
                        }
                    }
                    catch { }
                    finally
                    {
                        try { done.Set(); } catch { }
                    }
                });
            }
            try { WaitHandle.WaitAll(handles.ToArray(), 6000); }
            catch { }
            for (int i = 0; i < handles.Count; i++) { try { handles[i].Close(); } catch { } }

            Log.Info("DHT", "node cache: " + alive + "/" + probed + " contacts answered, table=" + _table.Count);
            return alive;
        }

        public void SaveCache()
        {
            NodeCache.Save(_cfg, _table.Snapshot(MaxCacheEntries));
        }

        private const int MaxCacheEntries = 400;

        // ----------------------------------------------------------------- queries

        public Dictionary<string, object> Ping(IPEndPoint ep, int timeoutMs)
        {
            return Query(ep, "ping", Bencode.Dict(), timeoutMs);
        }

        public Dictionary<string, object> FindNode(IPEndPoint ep, byte[] target, int timeoutMs)
        {
            Dictionary<string, object> args = Bencode.Dict();
            args["target"] = target ?? _selfId;
            return Query(ep, "find_node", args, timeoutMs);
        }

        public Dictionary<string, object> GetPeers(IPEndPoint ep, byte[] infoHash, int timeoutMs)
        {
            Dictionary<string, object> args = Bencode.Dict();
            args["info_hash"] = infoHash;
            return Query(ep, "get_peers", args, timeoutMs);
        }

        /// <summary>BEP 51: ask a node for a random sample of info hashes in its subtree.</summary>
        public Dictionary<string, object> SampleInfohashes(IPEndPoint ep, byte[] target, int timeoutMs)
        {
            Dictionary<string, object> args = Bencode.Dict();
            args["target"] = target ?? _selfId;
            return Query(ep, "sample_infohashes", args, timeoutMs);
        }

        private Dictionary<string, object> Query(IPEndPoint ep, string method, Dictionary<string, object> args, int timeoutMs)
        {
            if (!_running || ep == null) return null;
            if (args == null) args = Bencode.Dict();
            args["id"] = _selfId;

            byte[] tid = NextTransactionId();
            string key = Hex.Encode(tid);

            Dictionary<string, object> message = Bencode.Dict();
            message["t"] = tid;
            message["y"] = "q";
            message["q"] = method;
            message["a"] = args;

            Pending pending = new Pending
            {
                EndPoint = ep,
                Event = new ManualResetEvent(false),
                CreatedTicks = DateTime.UtcNow.Ticks
            };

            lock (_pendingLock) _pending[key] = pending;

            if (!Send(ep, message))
            {
                lock (_pendingLock) _pending.Remove(key);
                return null;
            }

            Interlocked.Increment(ref _queriesSent);
            if (timeoutMs <= 0)
            {
                // fire and forget: the answer only feeds the routing table, so the
                // transaction entry must not be left behind
                lock (_pendingLock) _pending.Remove(key);
                return null;
            }

            bool signaled = pending.Event.WaitOne(timeoutMs);
            lock (_pendingLock) _pending.Remove(key);

            if (!signaled)
            {
                Log.Dht("DHT", method + " " + NetUtil.Format(ep) + " timed out");
                return null;
            }
            if (pending.Response == null)
            {
                Interlocked.Increment(ref _errors);
                return null;
            }
            return pending.Response;
        }

        private byte[] NextTransactionId()
        {
            int value = Interlocked.Increment(ref _tid);
            return new byte[] { (byte)(value >> 8), (byte)value };
        }

        private bool Send(IPEndPoint ep, Dictionary<string, object> message)
        {
            try
            {
                byte[] data = Bencode.Encode(message);
                lock (_sendLock)
                {
                    _udp.Send(data, data.Length, ep);
                }
                return true;
            }
            catch (Exception ex)
            {
                Log.Dht("DHT", "send to " + NetUtil.Format(ep) + " failed: " + ex.Message);
                return false;
            }
        }

        // ---------------------------------------------------------------- receive

        private void ReceiveLoop()
        {
            byte[] buffer = new byte[8192];
            IPEndPoint remote = new IPEndPoint(IPAddress.Any, 0);
            while (_running)
            {
                byte[] data;
                try
                {
                    data = _udp.Receive(ref remote);
                }
                catch (SocketException)
                {
                    continue;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch
                {
                    continue;
                }

                if (data == null || data.Length < 2) continue;
                Interlocked.Increment(ref _packetsIn);
                try
                {
                    HandlePacket(data, remote);
                }
                catch (Exception ex)
                {
                    Log.Dht("DHT", "bad packet from " + NetUtil.Format(remote) + ": " + ex.Message);
                }
            }
        }

        private void HandlePacket(byte[] data, IPEndPoint remote)
        {
            object decoded;
            if (!Bencode.TryDecode(data, out decoded)) return;
            Dictionary<string, object> msg = decoded as Dictionary<string, object>;
            if (msg == null) return;

            string type = Bencode.GetString(msg, "y");
            byte[] tid = Bencode.Get(msg, "t") as byte[];
            if (tid == null) return;

            if (type == "q")
            {
                Interlocked.Increment(ref _queriesIn);
                HandleQuery(msg, tid, remote);
                return;
            }

            if (type == "r")
            {
                Interlocked.Increment(ref _responses);
                HandleResponse(tid, Bencode.GetDict(msg, "r"), remote);
                return;
            }

            if (type == "e")
            {
                Interlocked.Increment(ref _errors);
                string key = Hex.Encode(tid);
                lock (_pendingLock)
                {
                    Pending p;
                    if (_pending.TryGetValue(key, out p))
                    {
                        p.Error = "remote error";
                        if (p.Event != null) p.Event.Set();
                        if (p.Event == null) _pending.Remove(key);
                    }
                }
            }
        }

        private void HandleResponse(byte[] tid, Dictionary<string, object> r, IPEndPoint remote)
        {
            if (r == null) return;

            byte[] responder = Bencode.Get(r, "id") as byte[];
            if (responder != null && responder.Length == 20) _table.Add(responder, remote);

            byte[] nodes = Bencode.Get(r, "nodes") as byte[];
            HarvestCompactNodes(nodes);

            string key = Hex.Encode(tid);
            lock (_pendingLock)
            {
                Pending pending;
                if (_pending.TryGetValue(key, out pending))
                {
                    pending.Response = r;
                    if (pending.Event != null) pending.Event.Set();
                    else _pending.Remove(key);
                }
            }
        }

        private void HarvestCompactNodes(byte[] nodes)
        {
            if (nodes == null || nodes.Length < 26) return;
            for (int offset = 0; offset + 26 <= nodes.Length; offset += 26)
            {
                byte[] id;
                IPEndPoint ep;
                if (NetUtil.TryParseCompactNode(nodes, offset, out id, out ep)) _table.Add(id, ep);
            }
        }

        // ---------------------------------------------------------------- queries in

        private void HandleQuery(Dictionary<string, object> msg, byte[] tid, IPEndPoint remote)
        {
            string method = Bencode.GetString(msg, "q");
            Dictionary<string, object> args = Bencode.GetDict(msg, "a");
            if (method == null || args == null) return;

            byte[] senderId = Bencode.Get(args, "id") as byte[];
            if (senderId != null && senderId.Length == 20) _table.Add(senderId, remote);

            if (method == "ping")
            {
                Reply(tid, remote, Bencode.Dict());
                return;
            }

            if (method == "find_node")
            {
                byte[] target = Bencode.Get(args, "target") as byte[];
                Dictionary<string, object> r = Bencode.Dict();
                r["nodes"] = _table.CompactClosest(target ?? _selfId, 8);
                Reply(tid, remote, r);
                return;
            }

            if (method == "get_peers")
            {
                byte[] hash = Bencode.Get(args, "info_hash") as byte[];
                if (hash != null && hash.Length == 20 && _cfg.HarvestGetPeers)
                    RaiseInfohash(hash);

                Dictionary<string, object> r = Bencode.Dict();
                r["token"] = Token(remote.Address);
                r["nodes"] = _table.CompactClosest(hash ?? _selfId, 8);
                Reply(tid, remote, r);
                return;
            }

            if (method == "announce_peer")
            {
                byte[] hash = Bencode.Get(args, "info_hash") as byte[];
                if (hash != null && hash.Length == 20 && _cfg.HarvestAnnounce)
                {
                    long port = Bencode.GetLong(args, "port", 0);
                    long implied = Bencode.GetLong(args, "implied_port", 0);
                    IPEndPoint peer = null;
                    if (implied != 0 || port <= 0)
                        peer = new IPEndPoint(remote.Address, remote.Port);
                    else if (port > 0 && port <= 65535)
                        peer = new IPEndPoint(remote.Address, (int)port);
                    RaiseAnnounce(hash, peer);
                }
                Reply(tid, remote, Bencode.Dict());
                return;
            }

            if (method == "sample_infohashes")
            {
                // We are an indexer, not a storage node: report an empty sample and
                // a healthy interval so the caller does not retry us too often.
                Dictionary<string, object> r = Bencode.Dict();
                r["interval"] = 60L;
                r["num"] = 0L;
                r["samples"] = new byte[0];
                byte[] target = Bencode.Get(args, "target") as byte[];
                r["nodes"] = _table.CompactClosest(target ?? _selfId, 8);
                Reply(tid, remote, r);
                return;
            }

            SendError(tid, remote, 204, "Method Unknown");
        }

        private void Reply(byte[] tid, IPEndPoint remote, Dictionary<string, object> r)
        {
            r["id"] = _selfId;
            Dictionary<string, object> msg = Bencode.Dict();
            msg["t"] = tid;
            msg["y"] = "r";
            msg["r"] = r;
            Send(remote, msg);
        }

        private void SendError(byte[] tid, IPEndPoint remote, int code, string message)
        {
            Dictionary<string, object> e = Bencode.Dict();
            e["t"] = tid;
            e["y"] = "e";
            List<object> list = new List<object>();
            list.Add((long)code);
            list.Add(message);
            e["e"] = list;
            Send(remote, e);
        }

        private byte[] Token(IPAddress address)
        {
            byte[] ip = address.GetAddressBytes();
            byte[] mac = Crypto.HmacSha1(_secret, ip);
            byte[] token = new byte[8];
            Buffer.BlockCopy(mac, 0, token, 0, 8);
            return token;
        }

        private void RaiseInfohash(byte[] hash)
        {
            Action<byte[]> handler = OnInfohash;
            if (handler == null) return;
            try { handler(hash); }
            catch (Exception ex) { Log.Warn("DHT", "hash handler: " + ex.Message); }
        }

        private void RaiseAnnounce(byte[] hash, IPEndPoint peer)
        {
            Action<byte[], IPEndPoint> handler = OnAnnounce;
            if (handler == null) return;
            try { handler(hash, peer); }
            catch (Exception ex) { Log.Warn("DHT", "announce handler: " + ex.Message); }
        }

        // --------------------------------------------------------------- housekeeping

        /// <summary>Drops transaction entries that never got an answer.</summary>
        public void SweepPending()
        {
            long cutoff = DateTime.UtcNow.Ticks - TimeSpan.FromSeconds(60).Ticks;
            List<string> dead = null;
            lock (_pendingLock)
            {
                foreach (KeyValuePair<string, Pending> kv in _pending)
                {
                    if (kv.Value.CreatedTicks > cutoff) continue;
                    if (dead == null) dead = new List<string>();
                    dead.Add(kv.Key);
                }
                if (dead != null)
                {
                    for (int i = 0; i < dead.Count; i++)
                    {
                        Pending p = _pending[dead[i]];
                        if (p.Event != null) { try { p.Event.Set(); } catch { } }
                        _pending.Remove(dead[i]);
                    }
                }
            }
        }

        public List<DhtPeerNode> RandomNodes(int count)
        {
            return _table.Random(count);
        }

        public byte[] RandomId()
        {
            return Crypto.RandomBytes(20);
        }

        // ------------------------------------------------------------------- node id

        private static byte[] LoadOrCreateId(Config cfg)
        {
            string path = cfg.Resolve(cfg.NodeIdFile);
            try
            {
                if (File.Exists(path))
                {
                    string text = File.ReadAllText(path).Trim();
                    byte[] id;
                    if (Hex.TryDecode(text, out id) && id.Length == 20) return id;
                }
            }
            catch { }

            byte[] created = Crypto.RandomBytes(20);
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(path, Hex.Encode(created), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                Log.Warn("DHT", "cannot persist node id: " + ex.Message);
            }
            return created;
        }

        public string UptimeText()
        {
            TimeSpan span = DateTime.UtcNow - _startedUtc;
            if (span.TotalDays >= 1) return ((int)span.TotalDays) + "d " + span.Hours + "h";
            if (span.TotalHours >= 1) return ((int)span.TotalHours) + "h " + span.Minutes + "m";
            return ((int)span.TotalMinutes) + "m " + span.Seconds.ToString(CultureInfo.InvariantCulture) + "s";
        }
    }
}
