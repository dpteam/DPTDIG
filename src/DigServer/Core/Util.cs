using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Dig.Core
{
    /// <summary>Hex helpers. Hashes are always handled as 20-byte arrays internally.</summary>
    public static class Hex
    {
        private const string Digits = "0123456789abcdef";

        public static string Encode(byte[] data)
        {
            if (data == null) return "";
            char[] chars = new char[data.Length * 2];
            for (int i = 0; i < data.Length; i++)
            {
                chars[i * 2] = Digits[data[i] >> 4];
                chars[i * 2 + 1] = Digits[data[i] & 0x0f];
            }
            return new string(chars);
        }

        public static byte[] Decode(string hex)
        {
            byte[] result;
            if (!TryDecode(hex, out result)) throw new FormatException("invalid hex string");
            return result;
        }

        public static bool TryDecode(string hex, out byte[] result)
        {
            result = null;
            if (hex == null) return false;
            string s = hex.Trim();
            if (s.Length == 0 || (s.Length & 1) != 0) return false;
            byte[] bytes = new byte[s.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                int hi = Nibble(s[i * 2]);
                int lo = Nibble(s[i * 2 + 1]);
                if (hi < 0 || lo < 0) return false;
                bytes[i] = (byte)((hi << 4) | lo);
            }
            result = bytes;
            return true;
        }

        private static int Nibble(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return -1;
        }

        public static bool IsHash40(string s)
        {
            if (s == null || s.Length != 40) return false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                bool ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!ok) return false;
            }
            return true;
        }
    }

    /// <summary>SHA-1 / HMAC helpers and random generators. No secrets are ever logged.</summary>
    public static class Crypto
    {
        public static byte[] Sha1(byte[] data)
        {
            using (SHA1 sha = new SHA1CryptoServiceProvider())
                return sha.ComputeHash(data);
        }

        public static string Sha1Hex(string text)
        {
            return Hex.Encode(Sha1(Encoding.UTF8.GetBytes(text ?? "")));
        }

        public static byte[] HmacSha1(byte[] key, byte[] data)
        {
            using (HMACSHA1 mac = new HMACSHA1(key))
                return mac.ComputeHash(data);
        }

        public static string HmacSha1Hex(string key, string text)
        {
            return Hex.Encode(HmacSha1(Encoding.UTF8.GetBytes(key ?? ""), Encoding.UTF8.GetBytes(text ?? "")));
        }

        public static byte[] RandomBytes(int count)
        {
            byte[] bytes = new byte[count];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create())
                rng.GetBytes(bytes);
            return bytes;
        }

        public static string RandomHex(int byteCount)
        {
            return Hex.Encode(RandomBytes(byteCount));
        }

        /// <summary>Constant time comparison for tokens and hashes.</summary>
        public static bool Equals(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        public static bool Equals(string a, string b)
        {
            if (a == null || b == null) return a == b;
            if (a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }
    }

    /// <summary>Presentation helpers shared by the API and the HTML pages.</summary>
    public static class Text
    {
        public static string HtmlEscape(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            StringBuilder sb = new StringBuilder(value.Length + 16);
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                switch (c)
                {
                    case '&': sb.Append("&amp;"); break;
                    case '<': sb.Append("&lt;"); break;
                    case '>': sb.Append("&gt;"); break;
                    case '"': sb.Append("&quot;"); break;
                    case '\'': sb.Append("&#39;"); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }

        public static string UrlEncode(string value)
        {
            return Uri.EscapeDataString(value ?? "");
        }

        public static string Truncate(string value, int max)
        {
            if (value == null) return "";
            if (max <= 0 || value.Length <= max) return value;
            if (max <= 1) return value.Substring(0, max);
            return value.Substring(0, max - 1) + "\u2026";
        }

        public static string HumanSize(long bytes)
        {
            if (bytes < 0) return "?";
            if (bytes < 1024) return bytes.ToString(CultureInfo.InvariantCulture) + " B";
            double v = bytes;
            string[] units = new string[] { "KB", "MB", "GB", "TB", "PB" };
            int unit = -1;
            while (v >= 1024 && unit < units.Length - 1)
            {
                v /= 1024;
                unit++;
            }
            string format = v >= 100 ? "0" : (v >= 10 ? "0.0" : "0.00");
            return v.ToString(format, CultureInfo.InvariantCulture) + " " + units[unit];
        }

        public static string HumanCount(long value)
        {
            if (value < 1000) return value.ToString(CultureInfo.InvariantCulture);
            if (value < 1000000) return (value / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + "k";
            if (value < 1000000000) return (value / 1000000.0).ToString("0.0", CultureInfo.InvariantCulture) + "M";
            return (value / 1000000000.0).ToString("0.0", CultureInfo.InvariantCulture) + "G";
        }

        public static string HumanRate(double perSecond)
        {
            if (perSecond < 0.1) return perSecond.ToString("0.00", CultureInfo.InvariantCulture);
            if (perSecond < 10) return perSecond.ToString("0.0", CultureInfo.InvariantCulture);
            return Math.Round(perSecond).ToString("0", CultureInfo.InvariantCulture);
        }

        public static string Iso(DateTime utc)
        {
            return utc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        }

        /// <summary>Splits a name into lowercase tokens for the inverted index.</summary>
        public static List<string> Tokenize(string text, int maxTokens)
        {
            List<string> tokens = new List<string>();
            if (string.IsNullOrEmpty(text)) return tokens;
            StringBuilder current = new StringBuilder(24);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (char.IsLetterOrDigit(c))
                {
                    current.Append(char.ToLowerInvariant(c));
                }
                else if (current.Length > 0)
                {
                    FlushToken(tokens, current, maxTokens);
                    if (maxTokens > 0 && tokens.Count >= maxTokens) return tokens;
                }
            }
            if (current.Length > 0) FlushToken(tokens, current, maxTokens);
            return tokens;
        }

        private static void FlushToken(List<string> tokens, StringBuilder current, int maxTokens)
        {
            if (current.Length >= 2 && current.Length <= 48)
            {
                string token = current.ToString();
                if (!IsStopWord(token)) tokens.Add(token);
            }
            current.Length = 0;
        }

        private static readonly string[] StopWords = new string[]
        {
            "the", "and", "for", "with", "from", "that", "this", "you", "your", "are",
            "not", "but", "all", "any", "can", "has", "have", "was", "were", "will",
            "ver", "vol", "part", "disc", "disk", "cd1", "cd2", "dvd", "iso"
        };

        private static bool IsStopWord(string token)
        {
            for (int i = 0; i < StopWords.Length; i++)
                if (string.Equals(StopWords[i], token, StringComparison.Ordinal)) return true;
            return false;
        }
    }

    /// <summary>Unix time helpers (DateTimeOffset.ToUnixTimeSeconds is .NET 4.6+).</summary>
    public static class Clock
    {
        public static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public static long Now()
        {
            return ToUnix(DateTime.UtcNow);
        }

        public static long ToUnix(DateTime utc)
        {
            if (utc.Kind != DateTimeKind.Utc) utc = utc.ToUniversalTime();
            return (long)(utc - Epoch).TotalSeconds;
        }

        public static DateTime FromUnix(long seconds)
        {
            if (seconds < 0) seconds = 0;
            return Epoch.AddSeconds(seconds);
        }

        public static string Age(long unixSeconds, string language)
        {
            if (unixSeconds <= 0) return language == "ru" ? "неизвестно" : "unknown";
            TimeSpan span = DateTime.UtcNow - FromUnix(unixSeconds);
            if (span.TotalSeconds < 60) return language == "ru" ? "только что" : "just now";
            if (span.TotalMinutes < 60)
            {
                int m = (int)span.TotalMinutes;
                return language == "ru" ? (m + " мин назад") : (m + (m == 1 ? " min ago" : " min ago"));
            }
            if (span.TotalHours < 24)
            {
                int h = (int)span.TotalHours;
                return language == "ru" ? (h + " ч назад") : (h + (h == 1 ? " hour ago" : " hours ago"));
            }
            if (span.TotalDays < 30)
            {
                int d = (int)span.TotalDays;
                return language == "ru" ? (d + " дн назад") : (d + (d == 1 ? " day ago" : " days ago"));
            }
            if (span.TotalDays < 365)
            {
                int mo = (int)(span.TotalDays / 30);
                return language == "ru" ? (mo + " мес назад") : (mo + (mo == 1 ? " month ago" : " months ago"));
            }
            int y = (int)(span.TotalDays / 365);
            return language == "ru" ? (y + " г назад") : (y + (y == 1 ? " year ago" : " years ago"));
        }

        public static string Duration(TimeSpan span)
        {
            if (span.TotalDays >= 1) return ((int)span.TotalDays) + "d " + span.Hours + "h";
            if (span.TotalHours >= 1) return ((int)span.TotalHours) + "h " + span.Minutes + "m";
            if (span.TotalMinutes >= 1) return ((int)span.TotalMinutes) + "m " + span.Seconds + "s";
            return ((int)span.TotalSeconds) + "s";
        }
    }

    /// <summary>Endpoint parsing/formatting used by the DHT layer.</summary>
    public static class NetUtil
    {
        public static IPEndPoint ParseEndPoint(string hostPort, int defaultPort)
        {
            if (string.IsNullOrEmpty(hostPort)) return null;
            string s = hostPort.Trim();
            int colon = s.LastIndexOf(':');
            string host = colon > 0 ? s.Substring(0, colon) : s;
            int port = defaultPort;
            if (colon > 0)
            {
                int parsed;
                if (int.TryParse(s.Substring(colon + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                    port = parsed;
            }
            if (port <= 0 || port > 65535) port = defaultPort;

            IPAddress ip;
            if (IPAddress.TryParse(host, out ip)) return new IPEndPoint(ip, port);

            try
            {
                IPAddress[] addresses = Dns.GetHostAddresses(host);
                for (int i = 0; i < addresses.Length; i++)
                {
                    if (addresses[i].AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        return new IPEndPoint(addresses[i], port);
                }
                if (addresses.Length > 0) return new IPEndPoint(addresses[0], port);
            }
            catch
            {
            }
            return null;
        }

        public static string Format(IPEndPoint ep)
        {
            if (ep == null) return "";
            return ep.Address + ":" + ep.Port.ToString(CultureInfo.InvariantCulture);
        }

        public static bool IsPrivate(IPAddress ip)
        {
            if (ip == null) return true;
            if (IPAddress.IsLoopback(ip)) return true;
            byte[] b = ip.GetAddressBytes();
            if (b.Length != 4) return false;
            if (b[0] == 10) return true;
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
            if (b[0] == 192 && b[1] == 168) return true;
            if (b[0] == 169 && b[1] == 254) return true;
            if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return true;
            return false;
        }

        /// <summary>Compact node info: 20 byte id + 4 byte IPv4 + 2 byte port (BEP 5).</summary>
        public static byte[] CompactNode(byte[] id, IPEndPoint ep)
        {
            byte[] b = new byte[26];
            Buffer.BlockCopy(id, 0, b, 0, 20);
            byte[] addr = ep.Address.GetAddressBytes();
            Buffer.BlockCopy(addr, 0, b, 20, 4);
            b[24] = (byte)(ep.Port >> 8);
            b[25] = (byte)(ep.Port & 0xff);
            return b;
        }

        public static bool TryParseCompactNode(byte[] data, int offset, out byte[] id, out IPEndPoint ep)
        {
            id = null;
            ep = null;
            if (data == null || offset < 0 || offset + 26 > data.Length) return false;
            id = new byte[20];
            Buffer.BlockCopy(data, offset, id, 0, 20);
            byte[] addr = new byte[4];
            Buffer.BlockCopy(data, offset + 20, addr, 0, 4);
            int port = (data[offset + 24] << 8) | data[offset + 25];
            if (port <= 0) return false;
            ep = new IPEndPoint(new IPAddress(addr), port);
            return true;
        }

        /// <summary>Compact peer info: 4 byte IPv4 + 2 byte port (BEP 5 values list).</summary>
        public static bool TryParseCompactPeer(byte[] data, int offset, out IPEndPoint ep)
        {
            ep = null;
            if (data == null || offset < 0 || offset + 6 > data.Length) return false;
            byte[] addr = new byte[4];
            Buffer.BlockCopy(data, offset, addr, 0, 4);
            int port = (data[offset + 4] << 8) | data[offset + 5];
            if (port <= 0) return false;
            ep = new IPEndPoint(new IPAddress(addr), port);
            return true;
        }
    }
}
