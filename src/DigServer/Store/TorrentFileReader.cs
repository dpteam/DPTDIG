using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Dig.Core;
using Dig.Store;

namespace Dig.Store
{
    /// <summary>
    /// Reads a .torrent file without touching the network.
    ///
    /// The info hash is the SHA-1 of the *raw* bencoded info dictionary, so the
    /// dictionary has to be located byte-exactly instead of re-encoded: re-encoding
    /// can change key order and produce a different hash. That is why this file
    /// walks the bencode structure and remembers offsets.
    /// </summary>
    public static class TorrentFileReader
    {
        public static Torrent Read(string path, Config cfg, out string error)
        {
            error = null;
            try
            {
                byte[] data = File.ReadAllBytes(path);
                if (data.Length < 32) { error = "file too small"; return null; }

                // Some tools write a UTF-8 BOM in front of the dictionary; a real
                // bencode stream never starts with one, so skipping it is safe.
                int origin = 0;
                if (data.Length > 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF) origin = 3;
                if (data[origin] != (byte)'d') { error = "not a bencoded dictionary"; return null; }

                int offset;
                int length;
                if (!TryFindInfoSpan(data, origin, out offset, out length))
                {
                    error = "no info dictionary";
                    return null;
                }

                byte[] infoBytes = new byte[length];
                Buffer.BlockCopy(data, offset, infoBytes, 0, length);
                byte[] hash = Crypto.Sha1(infoBytes);

                TorrentInfo info = TorrentInfo.Parse(infoBytes, hash, cfg.MaxFiles, cfg.MaxNameLength, out error);
                if (info == null) return null;

                Torrent torrent = new Torrent();
                torrent.Hash = hash;
                torrent.Hex = info.HexHash;
                torrent.Name = info.Name;
                torrent.Size = info.Size;
                torrent.FileCount = info.FileCount;
                torrent.Files = info.Files;

                long seen = Clock.Now();
                torrent.FirstSeen = seen;
                torrent.LastSeen = seen;
                torrent.Hits = 1;

                // Keep the original creation date when the torrent carries one: an
                // imported 2011 release should not look like it appeared today.
                try
                {
                    FileInfo fi = new FileInfo(path);
                    if (fi.Exists) torrent.LastSeen = Clock.ToUnix(fi.LastWriteTimeUtc);
                }
                catch { }

                return torrent;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
        }

        /// <summary>Locates the raw bytes of the top-level "info" value.</summary>
        public static bool TryFindInfoSpan(byte[] data, out int offset, out int length)
        {
            return TryFindInfoSpan(data, 0, out offset, out length);
        }

        public static bool TryFindInfoSpan(byte[] data, int start, out int offset, out int length)
        {
            offset = -1;
            length = 0;
            if (data == null || start < 0 || start + 2 > data.Length) return false;
            if (data[start] != (byte)'d') return false;

            int pos = start + 1;
            while (pos < data.Length && data[pos] != (byte)'e')
            {
                int keyStart;
                int keyLength;
                if (!ReadStringSpan(data, ref pos, out keyStart, out keyLength)) return false;

                int valueStart = pos;
                if (!SkipValue(data, ref pos, 0)) return false;
                int valueLength = pos - valueStart;

                if (keyLength == 4 &&
                    data[keyStart] == (byte)'i' && data[keyStart + 1] == (byte)'n' &&
                    data[keyStart + 2] == (byte)'f' && data[keyStart + 3] == (byte)'o')
                {
                    if (valueLength <= 0 || data[valueStart] != (byte)'d') return false;
                    offset = valueStart;
                    length = valueLength;
                    return true;
                }
            }
            return false;
        }

        private static bool ReadStringSpan(byte[] d, ref int pos, out int start, out int length)
        {
            start = 0;
            length = 0;
            int value = 0;
            int digits = 0;
            while (pos < d.Length && d[pos] != (byte)':')
            {
                byte b = d[pos];
                if (b < (byte)'0' || b > (byte)'9') return false;
                if (digits > 8) return false;
                value = value * 10 + (b - (byte)'0');
                digits++;
                pos++;
            }
            if (pos >= d.Length || digits == 0) return false;
            pos++; // ':'
            if (value < 0 || value > d.Length - pos) return false;
            start = pos;
            length = value;
            pos += value;
            return true;
        }

        private static bool SkipValue(byte[] d, ref int pos, int depth)
        {
            if (depth > 32 || pos >= d.Length) return false;
            byte c = d[pos];

            if (c == (byte)'i')
            {
                pos++;
                while (pos < d.Length && d[pos] != (byte)'e') pos++;
                if (pos >= d.Length) return false;
                pos++;
                return true;
            }

            if (c == (byte)'l')
            {
                pos++;
                while (pos < d.Length && d[pos] != (byte)'e')
                {
                    if (!SkipValue(d, ref pos, depth + 1)) return false;
                }
                if (pos >= d.Length) return false;
                pos++;
                return true;
            }

            if (c == (byte)'d')
            {
                pos++;
                while (pos < d.Length && d[pos] != (byte)'e')
                {
                    int keyStart;
                    int keyLength;
                    if (!ReadStringSpan(d, ref pos, out keyStart, out keyLength)) return false;
                    if (!SkipValue(d, ref pos, depth + 1)) return false;
                }
                if (pos >= d.Length) return false;
                pos++;
                return true;
            }

            int start;
            int length;
            return ReadStringSpan(d, ref pos, out start, out length);
        }
    }
}
