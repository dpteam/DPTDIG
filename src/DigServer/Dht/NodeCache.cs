using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using Dig.Core;

namespace Dig.Dht
{
    /// <summary>
    /// On-disk contact cache.
    ///
    /// The public routers are flaky and rate limit clients that ask too often: a
    /// restart that depends on them alone can sit at "0 nodes" for minutes. BEP 5
    /// explicitly recommends caching nodes, so we keep the last known good contacts
    /// and seed the routing table from them before touching a router.
    ///
    /// Format is one line per contact: 40 hex chars, space, address:port. Plain text
    /// on purpose - it is small, human readable and needs no parser.
    /// </summary>
    public static class NodeCache
    {
        private const int MaxEntries = 400;

        public static List<DhtPeerNode> Load(Config cfg)
        {
            List<DhtPeerNode> list = new List<DhtPeerNode>();
            string path = cfg.DataPath("nodes.dat");
            try
            {
                if (!File.Exists(path)) return list;
                string[] lines = File.ReadAllLines(path);
                for (int i = 0; i < lines.Length && list.Count < MaxEntries; i++)
                {
                    string line = lines[i].Trim();
                    if (line.Length < 44 || line[0] == '#') continue;
                    int space = line.IndexOf(' ');
                    if (space != 40) continue;

                    byte[] id;
                    if (!Hex.TryDecode(line.Substring(0, 40), out id) || id.Length != 20) continue;

                    IPEndPoint ep = NetUtil.ParseEndPoint(line.Substring(space + 1).Trim(), 0);
                    if (ep == null || ep.Port <= 0) continue;
                    if (NetUtil.IsPrivate(ep.Address)) continue;

                    DhtPeerNode node = new DhtPeerNode();
                    node.Id = id;
                    node.EndPoint = ep;
                    node.LastSeenTicks = DateTime.UtcNow.Ticks;
                    list.Add(node);
                }
            }
            catch (Exception ex)
            {
                Log.Warn("DHT", "cannot read node cache: " + ex.Message);
            }
            return list;
        }

        public static void Save(Config cfg, List<DhtPeerNode> nodes)
        {
            if (nodes == null) return;
            string path = cfg.DataPath("nodes.dat");
            try
            {
                StringBuilder sb = new StringBuilder(nodes.Count * 60 + 128);
                sb.Append("# dig node cache - last known DHT contacts\n");
                int written = 0;
                for (int i = 0; i < nodes.Count && written < MaxEntries; i++)
                {
                    DhtPeerNode node = nodes[i];
                    if (node == null || node.EndPoint == null || node.Id == null) continue;
                    if (NetUtil.IsPrivate(node.EndPoint.Address)) continue;
                    sb.Append(Hex.Encode(node.Id)).Append(' ').Append(NetUtil.Format(node.EndPoint)).Append('\n');
                    written++;
                }

                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                string tmp = path + ".tmp";
                File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
                if (File.Exists(path))
                {
                    try { File.Replace(tmp, path, null, true); }
                    catch { File.Copy(tmp, path, true); try { File.Delete(tmp); } catch { } }
                }
                else
                {
                    File.Move(tmp, path);
                }
            }
            catch (Exception ex)
            {
                Log.Warn("DHT", "cannot write node cache: " + ex.Message);
            }
        }
    }
}
