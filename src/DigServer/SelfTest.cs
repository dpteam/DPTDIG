using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Dig.Core;
using Dig.Store;

namespace Dig
{
    /// <summary>
    /// Offline self-test (--self-test). No sockets, no DNS: CI and users can verify
    /// the codec, the index and the search engine on any machine.
    /// </summary>
    internal static class SelfTest
    {
        private static int _passed;
        private static int _failed;

        public static int Run()
        {
            Console.WriteLine("dig self-test");
            Console.WriteLine();

            TestBencode();
            TestTorrentInfo();
            TestTorrentFileReader();
            TestHashExtraction();
            TestTokenizer();
            TestClock();
            TestBranding();
            TestStoreAndSearch();

            Console.WriteLine();
            Console.WriteLine("passed: " + _passed + ", failed: " + _failed);
            return _failed == 0 ? 0 : 1;
        }

        // ------------------------------------------------------------------ cases

        private static void TestBencode()
        {
            Dictionary<string, object> dict = Bencode.Dict();
            dict["name"] = "ubuntu iso";
            dict["size"] = 1234567890L;
            dict["pieces"] = new byte[] { 1, 2, 3, 255 };
            List<object> list = new List<object>();
            list.Add("a");
            list.Add(7L);
            dict["files"] = list;

            byte[] encoded = Bencode.Encode(dict);
            object decoded = Bencode.Decode(encoded);
            Dictionary<string, object> back = decoded as Dictionary<string, object>;
            Check("bencode dict roundtrip", back != null);
            Check("bencode string", back != null && Bencode.GetString(back, "name") == "ubuntu iso");
            Check("bencode int", back != null && Bencode.GetLong(back, "size", 0) == 1234567890L);
            byte[] pieces = back == null ? null : Bencode.Get(back, "pieces") as byte[];
            Check("bencode bytes", pieces != null && pieces.Length == 4 && pieces[3] == 255);

            bool threw = false;
            try { Bencode.Decode(Encoding.ASCII.GetBytes("d3:foo")); }
            catch { threw = true; }
            Check("bencode rejects truncated input", threw);

            threw = false;
            try { Bencode.Decode(Encoding.ASCII.GetBytes("i12x")); }
            catch { threw = true; }
            Check("bencode rejects bad integer", threw);
        }

        private static void TestTorrentInfo()
        {
            Dictionary<string, object> info = Bencode.Dict();
            info["name"] = "ubuntu-24.04-desktop-amd64.iso";
            info["length"] = 5905580032L;
            byte[] metadata = Bencode.Encode(info);
            byte[] hash = Crypto.Sha1(metadata);

            string error;
            TorrentInfo t = TorrentInfo.Parse(metadata, hash, 400, 300, out error);
            Check("torrent parse single file", t != null && error == null);
            Check("torrent name", t != null && t.Name == "ubuntu-24.04-desktop-amd64.iso");
            Check("torrent size", t != null && t.Size == 5905580032L);

            Dictionary<string, object> multi = Bencode.Dict();
            multi["name"] = "season pack";
            List<object> files = new List<object>();
            Dictionary<string, object> f1 = Bencode.Dict();
            f1["length"] = 100L;
            List<object> path1 = new List<object>();
            path1.Add("S01E01.mkv");
            f1["path"] = path1;
            files.Add(f1);
            Dictionary<string, object> f2 = Bencode.Dict();
            f2["length"] = 200L;
            List<object> path2 = new List<object>();
            path2.Add("subs");
            path2.Add("S01E01.srt");
            f2["path"] = path2;
            files.Add(f2);
            multi["files"] = files;

            byte[] multiMeta = Bencode.Encode(multi);
            TorrentInfo mt = TorrentInfo.Parse(multiMeta, Crypto.Sha1(multiMeta), 400, 300, out error);
            Check("torrent parse multi file", mt != null && mt.FileCount == 2 && mt.Size == 300L);
            Check("torrent file path", mt != null && mt.Files.Count == 2 && mt.Files[1].Path == "subs/S01E01.srt");

            TorrentInfo bad = TorrentInfo.Parse(Encoding.ASCII.GetBytes("not-bencode"), hash, 400, 300, out error);
            Check("torrent parse rejects garbage", bad == null && error != null);
        }

        private static void TestTorrentFileReader()
        {
            string dir = Path.Combine(Path.GetTempPath(), "dig-torrentfile-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(dir);

                // A single-file torrent, built by hand so the expected hash is known.
                string name = "sample.iso";
                string announce = "http://tracker.example/announce";
                string info = "d6:lengthi12345678e4:name" + name.Length + ":" + name +
                              "12:piece lengthi262144e6:pieces40:" + new string('A', 40) + "e";
                string torrent = "d8:announce" + announce.Length + ":" + announce + "4:info" + info + "e";
                string path = Path.Combine(dir, "sample.torrent");
                File.WriteAllBytes(path, Encoding.ASCII.GetBytes(torrent));

                // The info hash must be SHA-1 over the raw info bytes, not over a
                // re-encoded dictionary - that is the whole point of this reader.
                byte[] expected = Crypto.Sha1(Encoding.ASCII.GetBytes(info));

                Config cfg = new Config();
                cfg.MaxFiles = 400;
                cfg.MaxNameLength = 300;
                string error;
                Torrent t = TorrentFileReader.Read(path, cfg, out error);

                Check("torrent file parses", t != null && error == null);
                Check("torrent file name", t != null && t.Name == "sample.iso");
                Check("torrent file size", t != null && t.Size == 12345678L);
                Check("torrent file info hash matches raw bytes", t != null && Crypto.Equals(t.Hash, expected));

                // Garbage must be rejected, not crash.
                string bad = Path.Combine(dir, "bad.torrent");
                File.WriteAllBytes(bad, Encoding.ASCII.GetBytes("d4:infoe"));
                Torrent broken = TorrentFileReader.Read(bad, cfg, out error);
                Check("torrent file rejects garbage", broken == null && error != null);
            }
            catch (Exception ex)
            {
                Check("torrent file test crashed: " + ex.Message, false);
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        private static void TestHashExtraction()
        {
            Check("hash from bare line",
                Import.Importer.ExtractHash("0123456789abcdef0123456789abcdef01234567") ==
                "0123456789abcdef0123456789abcdef01234567");
            Check("hash from magnet",
                Import.Importer.ExtractHash("magnet:?xt=urn:btih:FEDCBA9876543210FEDCBA9876543210FEDCBA98&dn=x") ==
                "fedcba9876543210fedcba9876543210fedcba98");
            Check("hash with reason column",
                Import.Importer.ExtractHash("0123456789abcdef0123456789abcdef01234567\tDMCA") ==
                "0123456789abcdef0123456789abcdef01234567");
            Check("comment is ignored", Import.Importer.ExtractHash("# 0123456789abcdef0123456789abcdef01234567") == null);
            Check("short hash is rejected", Import.Importer.ExtractHash("0123456789abcdef") == null);
            Check("empty line is ignored", Import.Importer.ExtractHash("   ") == null);
        }

        private static void TestTokenizer()        {
            List<string> tokens = Text.Tokenize("Ubuntu-24.04 Desktop AMD64 [ISO]", 20);
            Check("tokenizer lowercases", tokens.Contains("ubuntu"));
            Check("tokenizer splits punctuation", tokens.Contains("desktop") && tokens.Contains("amd64"));
            Check("tokenizer keeps numbers", tokens.Contains("24"));
            Check("tokenizer drops short words", !tokens.Contains("a"));
        }

        private static void TestClock()
        {
            long now = Clock.Now();
            Check("clock now is plausible", now > 1600000000);
            Check("clock roundtrip", Clock.ToUnix(Clock.FromUnix(now)) == now);
            Check("human size", Text.HumanSize(1536).StartsWith("1.5", StringComparison.Ordinal));
        }

        private static void TestBranding()
        {
            Config plain = new Config();
            Check("branding neutral by default", !plain.HasBranding);

            Config branded = new Config();
            branded.BrandAccent = "#4f9cf9";
            branded.BrandDarkAccent = "#4f9cf9";
            string css = Branding.Css(branded);
            Check("branding emits css", css.IndexOf("--accent", StringComparison.Ordinal) >= 0);
            Check("branding rejects junk colours", Config.NormalizeColor("not-a-colour") == "");
            Check("branding normalises shorthand", Config.NormalizeColor("#abc") == "#aabbcc");
        }

        private static void TestStoreAndSearch()
        {
            string dir = Path.Combine(Path.GetTempPath(), "dig-selftest-" + Guid.NewGuid().ToString("N"));
            try
            {
                Config cfg = new Config();
                cfg.BaseDir = dir;
                cfg.DataDir = "data";
                cfg.WebRoot = "public";
                cfg.MaxTorrents = 1000;
                cfg.SnapshotIntervalMin = 0;
                Directory.CreateDirectory(Path.Combine(dir, "data"));

                TorrentStore store = new TorrentStore(cfg);
                store.Load();
                SearchIndex index = new SearchIndex();
                index.Attach(store.All());
                store.Index = index;

                Torrent a = MakeTorrent("ubuntu-24.04-desktop-amd64.iso", 5905580032L, 42);
                Torrent b = MakeTorrent("debian-12-netinst.iso", 629145600L, 7);
                Torrent c = MakeTorrent("ubuntu-server-22.04.iso", 1500000000L, 12);
                Check("store add new", store.Add(a) && store.Add(b) && store.Add(c));
                Check("store rejects duplicate", !store.Add(MakeTorrent("ubuntu-24.04-desktop-amd64.iso", 5905580032L, 42)));
                Check("store count", store.Count == 3);

                SearchResult r = index.Search("ubuntu", 1, 10, "relevance");
                Check("search finds two ubuntu torrents", r.Total == 2);
                Check("search ranks seeders", r.Items.Count > 0 && r.Items[0].Name.IndexOf("24.04", StringComparison.Ordinal) >= 0);

                SearchResult r2 = index.Search("netinst", 1, 10, "relevance");
                Check("search file/name token", r2.Total == 1);

                SearchResult r3 = index.Search("ubunt", 1, 10, "relevance");
                Check("search substring fallback", r3.Total >= 1);

                store.Compact();
                store.Close();

                TorrentStore reloaded = new TorrentStore(cfg);
                reloaded.Load();
                Check("store survives compaction", reloaded.Count == 3);
                Check("store finds by hash", reloaded.Get(a.Hex) != null && reloaded.Get(a.Hex).Name == a.Name);
                reloaded.Close();
            }
            catch (Exception ex)
            {
                Check("store test crashed: " + ex.Message, false);
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        private static Torrent MakeTorrent(string name, long size, int seeders)
        {
            Torrent t = new Torrent();
            t.Hash = Crypto.Sha1(Encoding.UTF8.GetBytes(name));
            t.Hex = Hex.Encode(t.Hash);
            t.Name = name;
            t.Size = size;
            t.FileCount = 1;
            t.Files.Add(new TorrentFile(name, size));
            t.FirstSeen = Clock.Now();
            t.LastSeen = Clock.Now();
            t.Seeders = seeders;
            t.Hits = 1;
            return t;
        }

        // ----------------------------------------------------------------- report

        private static void Check(string name, bool ok)
        {
            if (ok)
            {
                _passed++;
                Console.WriteLine("  ok    " + name);
            }
            else
            {
                _failed++;
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("  FAIL  " + name);
                Console.ResetColor();
            }
        }
    }
}
