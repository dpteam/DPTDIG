using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Dig.Core
{
    /// <summary>
    /// Bencode reader/writer (BEP 3). Strings come back as byte[] because torrent
    /// metadata is binary; dictionary keys are UTF-8 strings because every key we
    /// care about is ASCII.
    ///
    /// The decoder is strict on purpose: a malformed packet from a random peer must
    /// throw and be dropped, never allocate unbounded memory.
    /// </summary>
    public static class Bencode
    {
        public const int MaxDepth = 32;

        public static object Decode(byte[] data)
        {
            if (data == null) throw new ArgumentNullException("data");
            int pos = 0;
            object value = Read(data, 0, data.Length, 0, ref pos);
            return value;
        }

        public static object Decode(byte[] data, int offset, int count)
        {
            if (data == null) throw new ArgumentNullException("data");
            int pos = offset;
            object value = Read(data, offset, offset + count, 0, ref pos);
            return value;
        }

        /// <summary>Decodes one value and reports where it ended (needed for BEP 9 pieces).</summary>
        public static object DecodeAt(byte[] data, int offset, int limit, out int end)
        {
            int pos = offset;
            object value = Read(data, offset, limit, 0, ref pos);
            end = pos;
            return value;
        }

        public static bool TryDecode(byte[] data, out object value)
        {
            value = null;
            try
            {
                value = Decode(data);
                return true;
            }
            catch
            {
                return false;
            }
        }

        // ------------------------------------------------------------------ read

        private static object Read(byte[] d, int start, int limit, int depth, ref int pos)
        {
            if (depth > MaxDepth) throw new FormatException("bencode: nesting too deep");
            if (pos >= limit) throw new FormatException("bencode: unexpected end of data");

            byte c = d[pos];
            if (c == (byte)'i') return ReadInt(d, limit, ref pos);
            if (c == (byte)'l') return ReadList(d, limit, depth, ref pos);
            if (c == (byte)'d') return ReadDict(d, limit, depth, ref pos);
            if (c >= (byte)'0' && c <= (byte)'9') return ReadBytes(d, limit, ref pos);
            throw new FormatException("bencode: unexpected byte 0x" + c.ToString("x2", CultureInfo.InvariantCulture));
        }

        private static long ReadInt(byte[] d, int limit, ref int pos)
        {
            pos++; // 'i'
            int begin = pos;
            bool negative = false;
            if (pos < limit && d[pos] == (byte)'-') { negative = true; pos++; }
            long value = 0;
            int digits = 0;
            while (pos < limit && d[pos] != (byte)'e')
            {
                byte b = d[pos];
                if (b < (byte)'0' || b > (byte)'9') throw new FormatException("bencode: bad integer");
                if (digits > 18) throw new FormatException("bencode: integer too large");
                value = value * 10 + (b - (byte)'0');
                digits++;
                pos++;
            }
            if (pos >= limit) throw new FormatException("bencode: unterminated integer");
            if (digits == 0) throw new FormatException("bencode: empty integer");
            pos++; // 'e'
            if (negative) value = -value;
            if (pos - begin > 32) throw new FormatException("bencode: integer too long");
            return value;
        }

        private static byte[] ReadBytes(byte[] d, int limit, ref int pos)
        {
            int len = 0;
            int digits = 0;
            while (pos < limit && d[pos] != (byte)':')
            {
                byte b = d[pos];
                if (b < (byte)'0' || b > (byte)'9') throw new FormatException("bencode: bad string length");
                if (digits > 9) throw new FormatException("bencode: string length too large");
                len = len * 10 + (b - (byte)'0');
                digits++;
                pos++;
            }
            if (pos >= limit) throw new FormatException("bencode: unterminated string length");
            pos++; // ':'
            if (len < 0 || len > limit - pos) throw new FormatException("bencode: string out of range");
            byte[] result = new byte[len];
            Buffer.BlockCopy(d, pos, result, 0, len);
            pos += len;
            return result;
        }

        private static List<object> ReadList(byte[] d, int limit, int depth, ref int pos)
        {
            pos++; // 'l'
            List<object> list = new List<object>();
            while (true)
            {
                if (pos >= limit) throw new FormatException("bencode: unterminated list");
                if (d[pos] == (byte)'e') { pos++; return list; }
                list.Add(Read(d, pos, limit, depth + 1, ref pos));
            }
        }

        private static Dictionary<string, object> ReadDict(byte[] d, int limit, int depth, ref int pos)
        {
            pos++; // 'd'
            Dictionary<string, object> dict = new Dictionary<string, object>(StringComparer.Ordinal);
            while (true)
            {
                if (pos >= limit) throw new FormatException("bencode: unterminated dictionary");
                if (d[pos] == (byte)'e') { pos++; return dict; }
                byte[] keyBytes = ReadBytes(d, limit, ref pos);
                string key = Encoding.UTF8.GetString(keyBytes);
                object value = Read(d, pos, limit, depth + 1, ref pos);
                dict[key] = value;
            }
        }

        // ----------------------------------------------------------------- write

        public static byte[] Encode(object value)
        {
            using (MemoryStream ms = new MemoryStream(256))
            {
                Write(ms, value);
                return ms.ToArray();
            }
        }

        public static void Write(Stream stream, object value)
        {
            if (value == null) { Raw(stream, "0:"); return; }

            string s = value as string;
            if (s != null)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(s);
                Raw(stream, bytes.Length.ToString(CultureInfo.InvariantCulture) + ":");
                stream.Write(bytes, 0, bytes.Length);
                return;
            }

            byte[] data = value as byte[];
            if (data != null)
            {
                Raw(stream, data.Length.ToString(CultureInfo.InvariantCulture) + ":");
                stream.Write(data, 0, data.Length);
                return;
            }

            if (value is int || value is long || value is short || value is byte)
            {
                Raw(stream, "i" + Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture) + "e");
                return;
            }

            if (value is bool)
            {
                Raw(stream, "i" + ((bool)value ? "1" : "0") + "e");
                return;
            }

            IDictionary<string, object> dict = value as IDictionary<string, object>;
            if (dict != null)
            {
                Raw(stream, "d");
                List<string> keys = new List<string>(dict.Keys);
                keys.Sort(StringComparer.Ordinal);
                for (int i = 0; i < keys.Count; i++)
                {
                    string key = keys[i];
                    byte[] kb = Encoding.UTF8.GetBytes(key);
                    Raw(stream, kb.Length.ToString(CultureInfo.InvariantCulture) + ":");
                    stream.Write(kb, 0, kb.Length);
                    Write(stream, dict[key]);
                }
                Raw(stream, "e");
                return;
            }

            IEnumerable<object> list = value as IEnumerable<object>;
            if (list != null)
            {
                Raw(stream, "l");
                foreach (object item in list) Write(stream, item);
                Raw(stream, "e");
                return;
            }

            throw new ArgumentException("bencode: unsupported type " + value.GetType().FullName);
        }

        private static void Raw(Stream stream, string ascii)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(ascii);
            stream.Write(bytes, 0, bytes.Length);
        }

        // --------------------------------------------------------------- helpers

        public static Dictionary<string, object> Dict()
        {
            return new Dictionary<string, object>(StringComparer.Ordinal);
        }

        public static object Get(Dictionary<string, object> dict, string key)
        {
            object v;
            return dict != null && dict.TryGetValue(key, out v) ? v : null;
        }

        public static string GetString(Dictionary<string, object> dict, string key)
        {
            byte[] b = Get(dict, key) as byte[];
            return b == null ? null : Encoding.UTF8.GetString(b);
        }

        public static long GetLong(Dictionary<string, object> dict, string key, long fallback)
        {
            object v = Get(dict, key);
            if (v is long) return (long)v;
            if (v is int) return (int)v;
            return fallback;
        }

        public static Dictionary<string, object> GetDict(Dictionary<string, object> dict, string key)
        {
            return Get(dict, key) as Dictionary<string, object>;
        }

        public static List<object> GetList(Dictionary<string, object> dict, string key)
        {
            return Get(dict, key) as List<object>;
        }
    }
}
