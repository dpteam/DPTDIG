using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using Dig.Core;

namespace Dig.Store
{
    /// <summary>One indexed torrent, as stored in memory and on disk.</summary>
    public sealed class Torrent
    {
        public byte[] Hash;
        public string Hex = "";
        public string Name = "";
        public long Size;
        public int FileCount;
        public List<TorrentFile> Files = new List<TorrentFile>();
        public long FirstSeen;
        public long LastSeen;
        public int Seeders;
        public int Leechers;
        public int Hits;

        public string Magnet()
        {
            StringBuilder sb = new StringBuilder(160);
            sb.Append("magnet:?xt=urn:btih:").Append(Hex);
            if (!string.IsNullOrEmpty(Name)) sb.Append("&dn=").Append(Text.UrlEncode(Name));
            return sb.ToString();
        }
    }

    /// <summary>
    /// Append-only torrent index.
    ///
    /// File layout: 8 byte magic, then records of
    ///   int32 payloadLength | byte type (1 = torrent) | payload
    /// A torn record at the tail is ignored on load, so a hard power loss costs at
    /// most the last few seconds. Every N minutes (or on exit) the log is rewritten
    /// into a compact file through a temp file + atomic replace.
    ///
    /// This is deliberately not SQLite: no NuGet, no native DLLs, and the whole
    /// working set fits in memory for the target size (millions of names).
    /// </summary>
    public sealed class TorrentStore
    {
        private const string Magic = "DIGIDX1\n";
        private const int MaxRecordBytes = 8 * 1024 * 1024;

        private readonly Config _cfg;
        private readonly string _path;
        private readonly object _sync = new object();
        private readonly List<Torrent> _list = new List<Torrent>(1024);
        private readonly Dictionary<string, int> _map = new Dictionary<string, int>(1024, StringComparer.OrdinalIgnoreCase);

        private FileStream _stream;
        private BinaryWriter _writer;
        private long _lastFlushTicks;
        private long _totalBytes;
        private int _addedToday;
        private long _dropped;
        private long _writeErrors;
        private Thread _maintenance;
        private volatile bool _running;

        public SearchIndex Index;

        public TorrentStore(Config cfg)
        {
            _cfg = cfg;
            string dir = cfg.Resolve(cfg.DataDir);
            try { Directory.CreateDirectory(dir); } catch { }
            _path = Path.Combine(dir, "index.db");
        }

        public int Count { get { lock (_sync) { return _list.Count; } } }
        public long TotalBytes { get { lock (_sync) { return _totalBytes; } } }
        public int AddedToday { get { lock (_sync) { return _addedToday; } } }
        public long Dropped { get { return Interlocked.Read(ref _dropped); } }
        public long WriteErrors { get { return Interlocked.Read(ref _writeErrors); } }
        public string FilePath { get { return _path; } }

        // -------------------------------------------------------------------- load

        public bool Load()
        {
            lock (_sync)
            {
                _list.Clear();
                _map.Clear();
                _totalBytes = 0;
                _addedToday = 0;

                if (!File.Exists(_path))
                {
                    Log.Info("INDEX", "new index at " + _path);
                    OpenAppend(true);
                    return true;
                }

                int broken = 0;
                try
                {
                    using (FileStream fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (BinaryReader reader = new BinaryReader(fs))
                    {
                        if (fs.Length >= Magic.Length)
                        {
                            byte[] magic = reader.ReadBytes(Magic.Length);
                            if (Encoding.ASCII.GetString(magic) != Magic)
                            {
                                Log.Warn("INDEX", "index.db has an unknown header, starting empty");
                                fs.Close();
                                OpenAppend(true);
                                return false;
                            }
                        }

                        while (fs.Position + 5 <= fs.Length)
                        {
                            int length;
                            byte type;
                            try
                            {
                                length = reader.ReadInt32();
                                type = reader.ReadByte();
                            }
                            catch { break; }

                            if (length <= 0 || length > MaxRecordBytes || fs.Position + length > fs.Length)
                            {
                                broken++;
                                break;
                            }

                            byte[] payload = reader.ReadBytes(length);
                            if (payload.Length != length) { broken++; break; }

                            if (type != 1) continue;
                            try
                            {
                                Torrent t = ReadPayload(payload);
                                if (t != null && t.Hash != null && t.Hash.Length == 20) AppendInMemory(t);
                            }
                            catch
                            {
                                broken++;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("INDEX", "cannot read index.db: " + ex.Message);
                }

                OpenAppend(false);
                Log.Info("INDEX", "loaded " + _list.Count + " torrents from " + _path +
                                  (broken > 0 ? " (" + broken + " broken record(s) skipped)" : ""));
                return true;
            }
        }

        private void AppendInMemory(Torrent t)
        {
            if (string.IsNullOrEmpty(t.Hex)) t.Hex = Hex.Encode(t.Hash);
            if (_map.ContainsKey(t.Hex)) return;
            _map[t.Hex] = _list.Count;
            _list.Add(t);
            _totalBytes += t.Size;
            long todayStart = Clock.ToUnix(DateTime.UtcNow.Date);
            if (t.FirstSeen >= todayStart) _addedToday++;
        }

        private void OpenAppend(bool create)
        {
            try
            {
                bool fresh = !File.Exists(_path);
                _stream = new FileStream(_path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
                _writer = new BinaryWriter(_stream);
                if (fresh || (create && _stream.Length == 0))
                {
                    byte[] magic = Encoding.ASCII.GetBytes(Magic);
                    _writer.Write(magic);
                    _writer.Flush();
                }
                else
                {
                    _stream.Seek(0, SeekOrigin.End);
                }
                _lastFlushTicks = DateTime.UtcNow.Ticks;
            }
            catch (Exception ex)
            {
                Log.Error("INDEX", "cannot open index.db for writing: " + ex.Message);
            }
        }

        // --------------------------------------------------------------------- add

        /// <summary>Adds a new torrent or refreshes an existing one. Returns true when new.</summary>
        public bool Add(Torrent t)
        {
            if (t == null || t.Hash == null || t.Hash.Length != 20) return false;
            if (string.IsNullOrEmpty(t.Hex)) t.Hex = Hex.Encode(t.Hash);
            if (t.Name == null) t.Name = "";
            if (t.Files == null) t.Files = new List<TorrentFile>();

            lock (_sync)
            {
                int id;
                bool isNew = !_map.TryGetValue(t.Hex, out id);

                if (isNew)
                {
                    if (_list.Count >= _cfg.MaxTorrents)
                    {
                        Interlocked.Increment(ref _dropped);
                        return false;
                    }
                    id = _list.Count;
                    _map[t.Hex] = id;
                    _list.Add(t);
                    _totalBytes += t.Size;
                    long todayStart = Clock.ToUnix(DateTime.UtcNow.Date);
                    if (t.FirstSeen >= todayStart) _addedToday++;
                }
                else
                {
                    Torrent existing = _list[id];
                    existing.LastSeen = Math.Max(existing.LastSeen, t.LastSeen);
                    existing.Hits++;
                    if (t.Seeders > existing.Seeders) existing.Seeders = t.Seeders;
                    existing.Leechers = t.Leechers;
                    t = existing;
                }

                WriteRecord(t);

                if (isNew && Index != null)
                {
                    try { Index.Add(t, id); }
                    catch (Exception ex) { Log.Warn("INDEX", "search index: " + ex.Message); }
                }
                return isNew;
            }
        }

        public Torrent Get(string hex)
        {
            if (string.IsNullOrEmpty(hex)) return null;
            lock (_sync)
            {
                int id;
                return _map.TryGetValue(hex, out id) ? _list[id] : null;
            }
        }

        public List<Torrent> All()
        {
            lock (_sync) { return new List<Torrent>(_list); }
        }

        public List<Torrent> Recent(int limit, int offset)
        {
            List<Torrent> result = new List<Torrent>();
            lock (_sync)
            {
                int end = _list.Count - Math.Max(0, offset);
                int start = Math.Max(0, end - Math.Max(0, limit));
                for (int i = end - 1; i >= start; i--) result.Add(_list[i]);
            }
            return result;
        }

        // ------------------------------------------------------------------ writing

        private void WriteRecord(Torrent t)
        {
            if (_writer == null) return;
            try
            {
                byte[] payload = BuildPayload(t);
                _writer.Write(payload.Length);
                _writer.Write((byte)1);
                _writer.Write(payload);

                long now = DateTime.UtcNow.Ticks;
                if ((now - _lastFlushTicks) / TimeSpan.TicksPerMillisecond >= _cfg.SaveDebounceMs)
                {
                    _writer.Flush();
                    _lastFlushTicks = now;
                }
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _writeErrors);
                Log.Warn("INDEX", "write failed: " + ex.Message);
                try { _writer.Flush(); } catch { }
            }
        }

        private static byte[] BuildPayload(Torrent t)
        {
            using (MemoryStream ms = new MemoryStream(256))
            using (BinaryWriter w = new BinaryWriter(ms))
            {
                w.Write(t.Hash, 0, 20);
                w.Write(t.Size);
                w.Write(t.FileCount);
                w.Write(t.FirstSeen);
                w.Write(t.LastSeen);
                w.Write(t.Seeders);
                w.Write(t.Leechers);
                w.Write(t.Hits);

                int files = t.Files == null ? 0 : t.Files.Count;
                w.Write(files);
                WriteString(w, t.Name);
                for (int i = 0; i < files; i++)
                {
                    WriteString(w, t.Files[i].Path);
                    w.Write(t.Files[i].Size);
                }
                w.Flush();
                return ms.ToArray();
            }
        }

        private static void WriteString(BinaryWriter w, string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value ?? "");
            if (bytes.Length > 60000)
            {
                byte[] cut = new byte[60000];
                Buffer.BlockCopy(bytes, 0, cut, 0, cut.Length);
                bytes = cut;
            }
            w.Write((ushort)bytes.Length);
            w.Write(bytes);
        }

        private static Torrent ReadPayload(byte[] payload)
        {
            using (MemoryStream ms = new MemoryStream(payload, false))
            using (BinaryReader r = new BinaryReader(ms))
            {
                Torrent t = new Torrent();
                t.Hash = r.ReadBytes(20);
                t.Hex = Hex.Encode(t.Hash);
                t.Size = r.ReadInt64();
                t.FileCount = r.ReadInt32();
                t.FirstSeen = r.ReadInt64();
                t.LastSeen = r.ReadInt64();
                t.Seeders = r.ReadInt32();
                t.Leechers = r.ReadInt32();
                t.Hits = r.ReadInt32();

                int files = r.ReadInt32();
                if (files < 0 || files > 100000) throw new InvalidDataException("bad file count");
                t.Name = ReadString(r);
                for (int i = 0; i < files; i++)
                {
                    string path = ReadString(r);
                    long size = r.ReadInt64();
                    t.Files.Add(new TorrentFile(path, size));
                }
                return t;
            }
        }

        private static string ReadString(BinaryReader r)
        {
            ushort length = r.ReadUInt16();
            byte[] bytes = r.ReadBytes(length);
            return Encoding.UTF8.GetString(bytes);
        }

        // --------------------------------------------------------------- compaction

        public void Compact()
        {
            lock (_sync)
            {
                if (_writer != null) { try { _writer.Flush(); } catch { } }

                string tmp = _path + ".tmp";
                try
                {
                    using (FileStream fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                    using (BinaryWriter w = new BinaryWriter(fs))
                    {
                        w.Write(Encoding.ASCII.GetBytes(Magic));
                        for (int i = 0; i < _list.Count; i++)
                        {
                            byte[] payload = BuildPayload(_list[i]);
                            w.Write(payload.Length);
                            w.Write((byte)1);
                            w.Write(payload);
                        }
                        w.Flush();
                        try { fs.Flush(true); } catch { }
                    }

                    CloseWriter();

                    if (File.Exists(_path))
                    {
                        string backup = _path + ".bak";
                        try { File.Replace(tmp, _path, backup, true); }
                        catch
                        {
                            File.Copy(tmp, _path, true);
                            try { File.Delete(tmp); } catch { }
                        }
                        try { if (File.Exists(backup)) File.Delete(backup); } catch { }
                    }
                    else
                    {
                        File.Move(tmp, _path);
                    }

                    OpenAppend(false);
                    Log.Info("INDEX", "compacted " + _list.Count + " torrents into " + _path);
                }
                catch (Exception ex)
                {
                    Log.Warn("INDEX", "compaction failed: " + ex.Message);
                    try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                    if (_writer == null) OpenAppend(false);
                }
            }
        }

        public void StartMaintenance()
        {
            _running = true;
            _maintenance = new Thread(MaintenanceLoop);
            _maintenance.IsBackground = true;
            _maintenance.Name = "index-maint";
            _maintenance.Start();
        }

        private void MaintenanceLoop()
        {
            DateTime lastCompact = DateTime.UtcNow;
            while (_running)
            {
                Thread.Sleep(5000);
                if (!_running) break;
                try
                {
                    lock (_sync) { if (_writer != null) _writer.Flush(); }
                    if (_cfg.SnapshotIntervalMin > 0 &&
                        (DateTime.UtcNow - lastCompact).TotalMinutes >= _cfg.SnapshotIntervalMin)
                    {
                        lastCompact = DateTime.UtcNow;
                        Compact();
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn("INDEX", "maintenance: " + ex.Message);
                }
            }
        }

        private void CloseWriter()
        {
            try { if (_writer != null) { _writer.Flush(); _writer.Dispose(); } } catch { }
            try { if (_stream != null) _stream.Dispose(); } catch { }
            _writer = null;
            _stream = null;
        }

        public void Close()
        {
            _running = false;
            try { if (_maintenance != null) _maintenance.Join(2000); } catch { }
            lock (_sync)
            {
                try { if (_writer != null) _writer.Flush(); } catch { }
                CloseWriter();
            }
            Log.Info("INDEX", "index closed, " + Count + " torrents on disk");
        }
    }
}
