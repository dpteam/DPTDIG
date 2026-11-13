using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Dig.Core;

namespace Dig.Store
{
    /// <summary>
    /// Hashes we have seen but could not resolve yet.
    ///
    /// The DHT only holds what peers announce right now, so an old torrent is often
    /// visible as a hash with no live peer. Throwing those away means losing the
    /// record forever; keeping them means the next time a peer appears we finally
    /// get a name. The list is plain text, deduplicated, capped, and fed back to the
    /// fetcher slowly on every start.
    /// </summary>
    public sealed class PendingHashes
    {
        private readonly string _path;
        private readonly int _cap;
        private readonly object _sync = new object();
        private readonly HashSet<string> _set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Queue<string> _order = new Queue<string>();
        private bool _dirty;

        public PendingHashes(Config cfg, int cap)
        {
            _path = cfg.DataPath("pending.txt");
            _cap = cap < 1000 ? 1000 : cap;
        }

        public string FilePath { get { return _path; } }

        public int Count
        {
            get { lock (_sync) { return _set.Count; } }
        }

        public void Load()
        {
            lock (_sync)
            {
                _set.Clear();
                _order.Clear();
                try
                {
                    if (!File.Exists(_path)) return;
                    using (StreamReader reader = new StreamReader(_path, Encoding.UTF8, true))
                    {
                        string line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            string hex = line.Trim();
                            if (hex.Length != 40 || !Hex.IsHash40(hex)) continue;
                            hex = hex.ToLowerInvariant();
                            if (_set.Add(hex)) _order.Enqueue(hex);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn("PENDING", "cannot read pending list: " + ex.Message);
                }
            }
            Log.Info("PENDING", "unresolved hashes kept from previous runs: " + Count);
        }

        public bool Add(string hex)
        {
            if (string.IsNullOrEmpty(hex) || hex.Length != 40 || !Hex.IsHash40(hex)) return false;
            string key = hex.ToLowerInvariant();
            lock (_sync)
            {
                if (!_set.Add(key)) return false;
                _order.Enqueue(key);
                _dirty = true;

                // Drop the oldest entries when the cap is reached: the tail of the
                // list is the least likely to ever find a peer again.
                while (_order.Count > _cap)
                {
                    string oldest = _order.Dequeue();
                    _set.Remove(oldest);
                }
                return true;
            }
        }

        /// <summary>Returns up to <paramref name="max"/> hashes, oldest first.</summary>
        public List<string> Take(int max)
        {
            List<string> result = new List<string>();
            lock (_sync)
            {
                while (_order.Count > 0 && result.Count < max)
                {
                    string hex = _order.Dequeue();
                    _set.Remove(hex);
                    result.Add(hex);
                    _dirty = true;
                }
            }
            return result;
        }

        public void Save()
        {
            lock (_sync)
            {
                if (!_dirty) return;
                try
                {
                    string dir = Path.GetDirectoryName(_path);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                    StringBuilder sb = new StringBuilder(_set.Count * 42 + 128);
                    sb.Append("# unresolved info hashes - retried on start, oldest first\n");
                    foreach (string hex in _order) sb.Append(hex).Append('\n');

                    string tmp = _path + ".tmp";
                    File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
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
                }
                catch (Exception ex)
                {
                    Log.Warn("PENDING", "cannot write pending list: " + ex.Message);
                }
            }
        }

        public void Clear()
        {
            lock (_sync)
            {
                _set.Clear();
                _order.Clear();
                _dirty = true;
            }
        }
    }
}
