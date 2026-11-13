using System;
using System.Collections.Generic;
using System.Text;

namespace Dig.Store
{
    public sealed class TorrentFile
    {
        public string Path;
        public long Size;

        public TorrentFile() { }

        public TorrentFile(string path, long size)
        {
            Path = path;
            Size = size;
        }
    }

    /// <summary>Everything BEP 9 gives us about one torrent, before it is indexed.</summary>
    public sealed class TorrentInfo
    {
        public byte[] Hash;
        public string Name = "";
        public long Size;
        public int FileCount;
        public List<TorrentFile> Files = new List<TorrentFile>();
        public int Seeders;
        public int Leechers;

        public string HexHash { get { return Core.Hex.Encode(Hash); } }

        /// <summary>
        /// Parses a verified info dictionary. Returns null (and a reason) when the
        /// dictionary is malformed, empty or has no usable name.
        /// </summary>
        public static TorrentInfo Parse(byte[] metadata, byte[] hash, int maxFiles, int maxNameLength, out string error)
        {
            error = null;
            try
            {
                Dictionary<string, object> info = Core.Bencode.Decode(metadata) as Dictionary<string, object>;
                if (info == null) { error = "info dict is not a dictionary"; return null; }

                TorrentInfo t = new TorrentInfo();
                t.Hash = hash;

                string name = Core.Bencode.GetString(info, "name.utf-8");
                if (string.IsNullOrEmpty(name)) name = Core.Bencode.GetString(info, "name");
                if (string.IsNullOrEmpty(name))
                {
                    Dictionary<string, object> inner = Core.Bencode.GetDict(info, "info");
                    if (inner != null)
                    {
                        name = Core.Bencode.GetString(inner, "name.utf-8");
                        if (string.IsNullOrEmpty(name)) name = Core.Bencode.GetString(inner, "name");
                        info = inner;
                    }
                }

                name = CleanName(name);
                if (name.Length == 0) { error = "empty name"; return null; }
                t.Name = name.Length > maxNameLength ? name.Substring(0, maxNameLength) : name;

                List<object> files = Core.Bencode.GetList(info, "files");
                if (files != null && files.Count > 0)
                {
                    t.FileCount = files.Count;
                    long total = 0;
                    int stored = 0;
                    for (int i = 0; i < files.Count; i++)
                    {
                        Dictionary<string, object> f = files[i] as Dictionary<string, object>;
                        if (f == null) continue;
                        long length = Core.Bencode.GetLong(f, "length", 0);
                        if (length < 0) length = 0;
                        total += length;
                        if (stored < maxFiles)
                        {
                            t.Files.Add(new TorrentFile(JoinPath(f), length));
                            stored++;
                        }
                    }
                    t.Size = total;
                }
                else
                {
                    long length = Core.Bencode.GetLong(info, "length", 0);
                    if (length < 0) length = 0;
                    t.Size = length;
                    t.FileCount = 1;
                    t.Files.Add(new TorrentFile(t.Name, length));
                }

                if (t.FileCount == 0) t.FileCount = 1;
                return t;
            }
            catch (Exception ex)
            {
                error = "parse: " + ex.Message;
                return null;
            }
        }

        private static string JoinPath(Dictionary<string, object> file)
        {
            List<object> parts = Core.Bencode.GetList(file, "path.utf-8");
            if (parts == null || parts.Count == 0) parts = Core.Bencode.GetList(file, "path");
            if (parts == null || parts.Count == 0) return "?";
            StringBuilder sb = new StringBuilder(64);
            for (int i = 0; i < parts.Count; i++)
            {
                byte[] part = parts[i] as byte[];
                if (part == null) continue;
                if (sb.Length > 0) sb.Append('/');
                sb.Append(CleanName(Encoding.UTF8.GetString(part)));
            }
            return sb.Length == 0 ? "?" : sb.ToString();
        }

        /// <summary>Strips control characters and path separators that would break the UI.</summary>
        public static string CleanName(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            StringBuilder sb = new StringBuilder(value.Length);
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c < ' ' || c == '\u007f') continue;
                if (c == '\\') c = '/';
                sb.Append(c);
            }
            string s = sb.ToString().Trim();
            while (s.IndexOf("//", StringComparison.Ordinal) >= 0)
                s = s.Replace("//", "/");
            return s;
        }
    }
}
