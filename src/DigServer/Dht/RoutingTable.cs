using System;
using System.Collections.Generic;
using System.Net;
using Dig.Core;

namespace Dig.Dht
{
    /// <summary>One contact in the routing table.</summary>
    public sealed class DhtPeerNode
    {
        public byte[] Id;
        public IPEndPoint EndPoint;
        public long LastSeenTicks;
        public int Failures;

        public string HexId { get { return Hex.Encode(Id); } }
    }

    /// <summary>
    /// Kademlia routing table with 160 buckets, 8 contacts each (BEP 5). The
    /// implementation is intentionally small: good enough to keep a few thousand
    /// live nodes, which is all a crawler needs.
    /// </summary>
    public sealed class RoutingTable
    {
        private const int BucketCount = 160;
        private const int PerBucket = 8;
        private static readonly long StaleTicks = TimeSpan.FromMinutes(20).Ticks;

        private readonly object _sync = new object();
        private readonly List<DhtPeerNode>[] _buckets = new List<DhtPeerNode>[BucketCount];
        private readonly byte[] _selfId;
        private readonly int _capacity;
        private readonly Random _random = new Random(unchecked(Environment.TickCount * 31 + 17));
        private int _count;

        public RoutingTable(byte[] selfId, int capacity)
        {
            _selfId = selfId;
            _capacity = capacity < 64 ? 64 : capacity;
            for (int i = 0; i < BucketCount; i++) _buckets[i] = new List<DhtPeerNode>(PerBucket);
        }

        public int Count { get { lock (_sync) { return _count; } } }

        public bool Add(byte[] id, IPEndPoint endPoint)
        {
            if (id == null || id.Length != 20 || endPoint == null) return false;
            if (Crypto.Equals(id, _selfId)) return false;

            lock (_sync)
            {
                int index = BucketIndex(id);
                List<DhtPeerNode> bucket = _buckets[index];

                for (int i = 0; i < bucket.Count; i++)
                {
                    if (Crypto.Equals(bucket[i].Id, id))
                    {
                        bucket[i].EndPoint = endPoint;
                        bucket[i].LastSeenTicks = DateTime.UtcNow.Ticks;
                        bucket[i].Failures = 0;
                        return true;
                    }
                }

                if (bucket.Count < PerBucket)
                {
                    bucket.Add(new DhtPeerNode
                    {
                        Id = id,
                        EndPoint = endPoint,
                        LastSeenTicks = DateTime.UtcNow.Ticks
                    });
                    _count++;
                    return true;
                }

                // Bucket full: replace a stale contact, otherwise ignore the newcomer.
                long now = DateTime.UtcNow.Ticks;
                int stale = -1;
                long oldest = long.MaxValue;
                for (int i = 0; i < bucket.Count; i++)
                {
                    if (now - bucket[i].LastSeenTicks > StaleTicks && bucket[i].LastSeenTicks < oldest)
                    {
                        oldest = bucket[i].LastSeenTicks;
                        stale = i;
                    }
                }
                if (stale >= 0)
                {
                    bucket[stale] = new DhtPeerNode
                    {
                        Id = id,
                        EndPoint = endPoint,
                        LastSeenTicks = now
                    };
                    return true;
                }
                return false;
            }
        }

        public void NoteSeen(byte[] id, IPEndPoint endPoint)
        {
            Add(id, endPoint);
        }

        public void NoteFailure(IPEndPoint endPoint)
        {
            if (endPoint == null) return;
            lock (_sync)
            {
                for (int b = 0; b < BucketCount; b++)
                {
                    List<DhtPeerNode> bucket = _buckets[b];
                    for (int i = 0; i < bucket.Count; i++)
                    {
                        if (bucket[i].EndPoint.Equals(endPoint))
                        {
                            bucket[i].Failures++;
                            if (bucket[i].Failures >= 3)
                            {
                                bucket.RemoveAt(i);
                                _count--;
                            }
                            return;
                        }
                    }
                }
            }
        }

        /// <summary>The closest contacts to a target, best first.</summary>
        public List<DhtPeerNode> Closest(byte[] target, int max)
        {
            List<DhtPeerNode> all = Snapshot(0);
            all.Sort(delegate(DhtPeerNode a, DhtPeerNode b)
            {
                return CompareDistance(target, a.Id, b.Id);
            });
            if (max > 0 && all.Count > max) all.RemoveRange(max, all.Count - max);
            return all;
        }

        public List<DhtPeerNode> Snapshot(int max)
        {
            List<DhtPeerNode> list = new List<DhtPeerNode>();
            lock (_sync)
            {
                for (int b = 0; b < BucketCount; b++)
                {
                    List<DhtPeerNode> bucket = _buckets[b];
                    for (int i = 0; i < bucket.Count; i++) list.Add(bucket[i]);
                }
            }
            if (max > 0 && list.Count > max)
            {
                // random-ish sample, keeps the crawler from hammering the same corner
                for (int i = list.Count - 1; i > 0; i--)
                {
                    int j = _random.Next(i + 1);
                    DhtPeerNode t = list[i]; list[i] = list[j]; list[j] = t;
                }
                list.RemoveRange(max, list.Count - max);
            }
            return list;
        }

        public List<DhtPeerNode> Random(int count)
        {
            return Snapshot(count);
        }

        public byte[] CompactClosest(byte[] target, int max)
        {
            List<DhtPeerNode> nodes = Closest(target, max);
            byte[] data = new byte[nodes.Count * 26];
            for (int i = 0; i < nodes.Count; i++)
            {
                byte[] compact = NetUtil.CompactNode(nodes[i].Id, nodes[i].EndPoint);
                Buffer.BlockCopy(compact, 0, data, i * 26, 26);
            }
            return data;
        }

        public int BucketIndex(byte[] id)
        {
            int common = CommonPrefixBits(id);
            return common >= BucketCount ? BucketCount - 1 : common;
        }

        private int CommonPrefixBits(byte[] id)
        {
            int bits = 0;
            for (int i = 0; i < 20; i++)
            {
                int x = _selfId[i] ^ id[i];
                if (x == 0) { bits += 8; continue; }
                for (int bit = 7; bit >= 0; bit--)
                {
                    if ((x & (1 << bit)) != 0) return bits;
                    bits++;
                }
            }
            return BucketCount;
        }

        public static int CompareDistance(byte[] target, byte[] a, byte[] b)
        {
            for (int i = 0; i < 20; i++)
            {
                int da = a[i] ^ target[i];
                int db = b[i] ^ target[i];
                if (da != db) return da < db ? -1 : 1;
            }
            return 0;
        }

        public int Capacity { get { return _capacity; } }
    }
}
