using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Dig.Core;
using Dig.Store;

namespace Dig.Dht
{
    /// <summary>A hash waiting to become a name. Peer may be null when it came from a sample.</summary>
    public sealed class HashJob
    {
        public byte[] Hash;
        public string Hex;
        public IPEndPoint Peer;
        public int Attempts;
        public long NextTryTicks;
        public int Seeders;
        public int Leechers;
        public int PeersFound;
    }

    /// <summary>
    /// BEP 9 metadata fetcher. Connects to a peer over TCP, performs the BitTorrent
    /// handshake with the extension bit set, exchanges ut_metadata messages and
    /// reassembles the info dictionary. The SHA-1 of the result must equal the info
    /// hash, otherwise the metadata is thrown away.
    /// </summary>
    public sealed class MetadataFetcher
    {
        private const int PieceSize = 16384;
        private static readonly byte[] PeerId = BuildPeerId();

        private readonly Config _cfg;
        private readonly DhtNode _dht;
        private readonly Queue<HashJob> _ready = new Queue<HashJob>();
        private readonly List<HashJob> _delayed = new List<HashJob>();
        private readonly object _sync = new object();
        private Thread[] _workers;
        private Thread _housekeeping;
        private volatile bool _running;
        private int _active;
        private long _done;
        private long _failed;
        private long _attempts;
        private int _queued;

        public Action<HashJob, TorrentInfo> OnFetched;
        public Action<HashJob, string> OnFailed;
        /// <summary>Called once a hash has used all its retries; the caller may keep it.</summary>
        public Action<HashJob> OnGiveUp;
        /// <summary>Every peer address discovered for a hash (feeds the address index).</summary>
        public Action<byte[], IPEndPoint> OnPeers;

        public MetadataFetcher(Config cfg, DhtNode dht)
        {
            _cfg = cfg;
            _dht = dht;
        }

        public int Active { get { return _active; } }
        public long Done { get { return Interlocked.Read(ref _done); } }
        public long Failed { get { return Interlocked.Read(ref _failed); } }
        public long Attempts { get { return Interlocked.Read(ref _attempts); } }
        public int Queued { get { lock (_sync) { return _queued; } } }

        public void Start()
        {
            if (!_cfg.MetadataEnabled) return;
            _running = true;
            int count = _cfg.MetadataConcurrency;
            _workers = new Thread[count];
            for (int i = 0; i < count; i++)
            {
                _workers[i] = new Thread(WorkerLoop);
                _workers[i].IsBackground = true;
                _workers[i].Name = "meta-" + i;
                _workers[i].Start();
            }
            _housekeeping = new Thread(HousekeepingLoop);
            _housekeeping.IsBackground = true;
            _housekeeping.Name = "meta-housekeeping";
            _housekeeping.Start();
            Log.Info("META", "ut_metadata fetcher started, concurrency=" + count);
        }

        public void Stop()
        {
            _running = false;
            lock (_sync) Monitor.PulseAll(_sync);
            try { if (_housekeeping != null) _housekeeping.Join(1000); } catch { }
            if (_workers != null)
                for (int i = 0; i < _workers.Length; i++)
                    try { _workers[i].Join(1000); } catch { }
        }

        public bool Enqueue(HashJob job)
        {
            if (job == null || job.Hash == null || job.Hash.Length != 20) return false;
            lock (_sync)
            {
                if (_queued >= _cfg.QueueMax) return false;
                _queued++;
                _ready.Enqueue(job);
                Monitor.Pulse(_sync);
            }
            return true;
        }

        private HashJob Dequeue()
        {
            lock (_sync)
            {
                while (_running)
                {
                    if (_ready.Count > 0)
                    {
                        _queued--;
                        return _ready.Dequeue();
                    }
                    Monitor.Wait(_sync, 500);
                }
                return null;
            }
        }

        private void HousekeepingLoop()
        {
            while (_running)
            {
                try
                {
                    Thread.Sleep(2000);
                    long now = DateTime.UtcNow.Ticks;
                    lock (_sync)
                    {
                        for (int i = _delayed.Count - 1; i >= 0; i--)
                        {
                            if (_delayed[i].NextTryTicks > now) continue;
                            HashJob job = _delayed[i];
                            _delayed.RemoveAt(i);
                            _queued++;
                            _ready.Enqueue(job);
                            Monitor.Pulse(_sync);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn("META", "housekeeping: " + ex.Message);
                }
            }
        }

        private void WorkerLoop()
        {
            while (_running)
            {
                HashJob job = Dequeue();
                if (job == null) continue;

                Interlocked.Increment(ref _active);
                try
                {
                    Process(job);
                }
                catch (Exception ex)
                {
                    Log.Debug("META", "job " + job.Hex + " failed: " + ex.Message);
                    ReportFailure(job, ex.Message);
                }
                finally
                {
                    Interlocked.Decrement(ref _active);
                }
            }
        }

        private void Process(HashJob job)
        {
            Interlocked.Increment(ref _attempts);
            List<IPEndPoint> peers = new List<IPEndPoint>();
            if (job.Peer != null) peers.Add(job.Peer);
            else peers.AddRange(ResolvePeers(job.Hash));

            if (peers.Count == 0)
            {
                ReportFailure(job, "no peers");
                return;
            }

            Action<byte[], IPEndPoint> peerSink = OnPeers;
            if (peerSink != null)
            {
                for (int i = 0; i < peers.Count; i++)
                {
                    try { peerSink(job.Hash, peers[i]); }
                    catch { }
                }
            }

            string lastError = "no peers";
            int limit = Math.Min(peers.Count, _cfg.MaxPeersPerHash);
            for (int i = 0; i < limit; i++)
            {
                if (!_running) return;
                if (NetUtil.IsPrivate(peers[i].Address)) continue;

                TorrentInfo info;
                string error;
                if (TryFetch(job.Hash, peers[i], out info, out error))
                {
                    info.Seeders = job.Seeders;
                    info.Leechers = job.Leechers;
                    Interlocked.Increment(ref _done);
                    Action<HashJob, TorrentInfo> handler = OnFetched;
                    if (handler != null)
                    {
                        try { handler(job, info); }
                        catch (Exception ex) { Log.Warn("META", "store: " + ex.Message); }
                    }
                    return;
                }
                lastError = error;
                Log.Dht("META", job.Hex.Substring(0, 8) + "\u2026 " + NetUtil.Format(peers[i]) + ": " + error);
            }

            ReportFailure(job, lastError);
        }

        private void ReportFailure(HashJob job, string error)
        {
            Interlocked.Increment(ref _failed);
            job.Attempts++;
            if (job.Attempts < 3 && _running)
            {
                job.NextTryTicks = DateTime.UtcNow.AddMinutes(_cfg.RetryAfterMin).Ticks;
                lock (_sync) _delayed.Add(job);
            }
            else
            {
                Action<HashJob> giveUp = OnGiveUp;
                if (giveUp != null)
                {
                    try { giveUp(job); }
                    catch { }
                }
            }

            Action<HashJob, string> handler = OnFailed;
            if (handler != null)
            {
                try { handler(job, error); }
                catch { }
            }
        }

        /// <summary>Iterative get_peers on a few random nodes to find a peer for a bare hash.</summary>
        private List<IPEndPoint> ResolvePeers(byte[] hash)
        {
            List<IPEndPoint> result = new List<IPEndPoint>();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);

            List<DhtPeerNode> nodes = _dht.RandomNodes(6);
            List<DhtPeerNode> secondRound = new List<DhtPeerNode>();

            for (int round = 0; round < 2 && result.Count < _cfg.MaxPeersPerHash; round++)
            {
                List<DhtPeerNode> current = round == 0 ? nodes : secondRound;
                for (int i = 0; i < current.Count && result.Count < _cfg.MaxPeersPerHash; i++)
                {
                    Dictionary<string, object> response = _dht.GetPeers(current[i].EndPoint, hash, 2000);
                    if (response == null) continue;
                    CollectPeers(response, result, seen);
                    if (round == 0) CollectNodes(response, secondRound, 4);
                }
                if (result.Count > 0) break;
            }
            return result;
        }

        private static void CollectPeers(Dictionary<string, object> response, List<IPEndPoint> result, HashSet<string> seen)
        {
            object raw = Bencode.Get(response, "values");
            List<object> values = raw as List<object>;
            if (values != null)
            {
                for (int i = 0; i < values.Count; i++)
                {
                    byte[] compact = values[i] as byte[];
                    IPEndPoint ep;
                    if (compact != null && NetUtil.TryParseCompactPeer(compact, 0, out ep))
                        AddPeer(ep, result, seen);
                }
                return;
            }

            // some implementations return one concatenated byte string
            byte[] blob = raw as byte[];
            if (blob != null)
            {
                for (int offset = 0; offset + 6 <= blob.Length; offset += 6)
                {
                    IPEndPoint ep;
                    if (NetUtil.TryParseCompactPeer(blob, offset, out ep)) AddPeer(ep, result, seen);
                }
            }
        }

        private static void AddPeer(IPEndPoint ep, List<IPEndPoint> result, HashSet<string> seen)
        {
            string key = ep.ToString();
            if (seen.Contains(key)) return;
            seen.Add(key);
            result.Add(ep);
        }

        private static void CollectNodes(Dictionary<string, object> response, List<DhtPeerNode> result, int max)
        {
            byte[] nodes = Bencode.Get(response, "nodes") as byte[];
            if (nodes == null) return;
            for (int offset = 0; offset + 26 <= nodes.Length && result.Count < max; offset += 26)
            {
                byte[] id;
                IPEndPoint ep;
                if (!NetUtil.TryParseCompactNode(nodes, offset, out id, out ep)) continue;
                DhtPeerNode node = new DhtPeerNode();
                node.Id = id;
                node.EndPoint = ep;
                result.Add(node);
            }
        }

        // ------------------------------------------------------------- peer wire

        private bool TryFetch(byte[] hash, IPEndPoint peer, out TorrentInfo info, out string error)
        {
            info = null;
            error = null;
            TcpClient client = new TcpClient();
            try
            {
                client.NoDelay = true;
                IAsyncResult connect = client.BeginConnect(peer.Address, peer.Port, null, null);
                if (!connect.AsyncWaitHandle.WaitOne(_cfg.ConnectTimeoutMs))
                {
                    error = "connect timeout";
                    return false;
                }
                client.EndConnect(connect);
                client.ReceiveTimeout = _cfg.ReadTimeoutMs;
                client.SendTimeout = _cfg.ReadTimeoutMs;
                NetworkStream stream = client.GetStream();

                byte[] handshake = new byte[68];
                handshake[0] = 19;
                Buffer.BlockCopy(Encoding.ASCII.GetBytes("BitTorrent protocol"), 0, handshake, 1, 19);
                handshake[25] = 0x10; // extension protocol (BEP 10)
                Buffer.BlockCopy(hash, 0, handshake, 28, 20);
                Buffer.BlockCopy(PeerId, 0, handshake, 48, 20);
                stream.Write(handshake, 0, handshake.Length);
                stream.Flush();

                byte[] answer = ReadExact(stream, 68);
                if (answer == null) { error = "no handshake"; return false; }
                if (answer[0] != 19) { error = "bad handshake"; return false; }
                for (int i = 0; i < 20; i++)
                {
                    if (answer[28 + i] != hash[i]) { error = "handshake hash mismatch"; return false; }
                }
                if ((answer[25] & 0x10) == 0) { error = "peer does not support extensions"; return false; }

                Dictionary<string, object> ext = Bencode.Dict();
                Dictionary<string, object> m = Bencode.Dict();
                m["ut_metadata"] = 1L;
                ext["m"] = m;
                ext["v"] = "dig/1.0";
                ext["p"] = 0L;
                SendExtended(stream, 0, Bencode.Encode(ext));

                int peerMetadataId = 0;
                int totalPieces = 0;
                long metadataSize = 0;
                byte[] metadata = null;
                bool[] have = null;
                int received = 0;
                bool started = false;

                DateTime deadline = DateTime.UtcNow.AddMilliseconds(_cfg.ReadTimeoutMs * 2);
                while (DateTime.UtcNow < deadline)
                {
                    int length = ReadInt32(stream);
                    if (length < 0) { if (error == null) error = "eof"; break; }
                    if (length == 0) continue; // keep-alive
                    if (length > 2 * 1024 * 1024) { error = "peer message too large"; break; }

                    byte[] message = ReadExact(stream, length);
                    if (message == null) { error = "eof in message"; break; }
                    if (message.Length < 2) continue;

                    byte id = message[0];
                    if (id != 20) continue; // bitfield / have / choke - not interesting here

                    byte extId = message[1];
                    if (extId == 0)
                    {
                        Dictionary<string, object> hs = null;
                        try { hs = Bencode.Decode(message, 2, message.Length - 2) as Dictionary<string, object>; }
                        catch { }
                        if (hs == null) continue;

                        Dictionary<string, object> theirM = Bencode.GetDict(hs, "m");
                        peerMetadataId = theirM == null ? 0 : (int)Bencode.GetLong(theirM, "ut_metadata", 0);
                        metadataSize = Bencode.GetLong(hs, "metadata_size", 0);

                        if (peerMetadataId <= 0) { error = "peer has no ut_metadata"; break; }
                        if (metadataSize <= 0) { error = "peer has no metadata_size"; break; }
                        if (metadataSize > _cfg.MaxMetadataBytes) { error = "metadata too large"; break; }

                        totalPieces = (int)((metadataSize + PieceSize - 1) / PieceSize);
                        if (totalPieces <= 0 || totalPieces > _cfg.MaxPieces) { error = "too many pieces"; break; }

                        metadata = new byte[metadataSize];
                        have = new bool[totalPieces];
                        for (int piece = 0; piece < totalPieces; piece++)
                            RequestPiece(stream, peerMetadataId, piece);
                        started = true;
                        continue;
                    }

                    if (extId != 1 || !started) continue; // 1 is the id we advertised

                    int end;
                    Dictionary<string, object> pieceMsg = null;
                    try { pieceMsg = Bencode.DecodeAt(message, 2, message.Length, out end) as Dictionary<string, object>; }
                    catch { continue; }
                    if (pieceMsg == null) continue;

                    long msgType = Bencode.GetLong(pieceMsg, "msg_type", 0);
                    long pieceIndex = Bencode.GetLong(pieceMsg, "piece", -1);
                    if (msgType == 2) { error = "piece rejected by peer"; break; }
                    if (msgType != 1 || pieceIndex < 0 || pieceIndex >= totalPieces) continue;

                    int offset = (int)pieceIndex * PieceSize;
                    int take = Math.Min(PieceSize, (int)metadataSize - offset);
                    int available = message.Length - end;
                    if (available < take) take = available;
                    if (take > 0) Buffer.BlockCopy(message, end, metadata, offset, take);
                    have[pieceIndex] = true;
                    received++;
                    if (received >= totalPieces) break;
                }

                if (metadata == null) { if (error == null) error = "no metadata"; return false; }
                for (int i = 0; i < have.Length; i++)
                {
                    if (!have[i]) { error = "missing piece " + i; return false; }
                }

                if (!Crypto.Equals(Crypto.Sha1(metadata), hash)) { error = "hash mismatch"; return false; }
                info = TorrentInfo.Parse(metadata, hash, _cfg.MaxFiles, _cfg.MaxNameLength, out error);
                return info != null;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
            finally
            {
                try { client.Close(); } catch { }
            }
        }

        private static void SendExtended(NetworkStream stream, byte extId, byte[] payload)
        {
            int length = 2 + payload.Length;
            byte[] header = new byte[6];
            header[0] = (byte)(length >> 24);
            header[1] = (byte)(length >> 16);
            header[2] = (byte)(length >> 8);
            header[3] = (byte)length;
            header[4] = 20;
            header[5] = extId;
            stream.Write(header, 0, header.Length);
            stream.Write(payload, 0, payload.Length);
            stream.Flush();
        }

        private static void RequestPiece(NetworkStream stream, int peerMetadataId, int piece)
        {
            Dictionary<string, object> request = Bencode.Dict();
            request["msg_type"] = 0L;
            request["piece"] = (long)piece;
            SendExtended(stream, (byte)peerMetadataId, Bencode.Encode(request));
        }

        private static int ReadInt32(NetworkStream stream)
        {
            byte[] header = ReadExact(stream, 4);
            if (header == null) return -1;
            return (header[0] << 24) | (header[1] << 16) | (header[2] << 8) | header[3];
        }

        private static byte[] ReadExact(NetworkStream stream, int count)
        {
            byte[] buffer = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                int read = stream.Read(buffer, offset, count - offset);
                if (read <= 0) return null;
                offset += read;
            }
            return buffer;
        }

        private static byte[] BuildPeerId()
        {
            byte[] id = new byte[20];
            byte[] prefix = Encoding.ASCII.GetBytes("-DG0100-");
            Buffer.BlockCopy(prefix, 0, id, 0, prefix.Length);
            byte[] random = Crypto.RandomBytes(12);
            Buffer.BlockCopy(random, 0, id, 8, 12);
            return id;
        }
    }
}
