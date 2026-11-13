using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Dig.Core;
using Dig.Store;

namespace Dig.Import
{
    /// <summary>Result of one import run, printed to the console and the log.</summary>
    public sealed class ImportReport
    {
        public int FilesSeen;
        public int Added;
        public int Duplicates;
        public int Invalid;
        public int HashesSeen;
        public int HashesQueued;
        public long Bytes;
        public readonly List<string> Errors = new List<string>();

        public void Print()
        {
            Console.WriteLine();
            Console.WriteLine("  files scanned : " + FilesSeen);
            Console.WriteLine("  added         : " + Added);
            Console.WriteLine("  duplicates    : " + Duplicates);
            Console.WriteLine("  invalid       : " + Invalid);
            if (HashesSeen > 0)
            {
                Console.WriteLine("  hashes read   : " + HashesSeen);
                Console.WriteLine("  hashes queued : " + HashesQueued);
            }
            Console.WriteLine("  size          : " + Text.HumanSize(Bytes));
            if (Errors.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("  first problems:");
                int limit = Math.Min(Errors.Count, 10);
                for (int i = 0; i < limit; i++) Console.WriteLine("    " + Errors[i]);
            }
            Console.WriteLine();
        }
    }

    /// <summary>
    /// Backfill from local data. The DHT cannot be enumerated backwards - it only
    /// holds what peers are announcing right now - so "old" torrents come from
    /// files you already have:
    ///
    ///   * .torrent files   -> complete metadata, indexed offline, no network at all
    ///   * hash lists       -> bare 40-char hashes, queued for the fetcher and for
    ///                         the persistent retry list, so they resolve whenever
    ///                         a peer shows up
    ///
    /// Both paths verify the info hash with SHA-1 before anything is stored.
    /// </summary>
    public static class Importer
    {
        public static ImportReport ImportPath(Config cfg, TorrentStore store, string target, bool recursive,
                                              ImportReport report, Action<string> enqueueHash)
        {
            if (report == null) report = new ImportReport();
            if (string.IsNullOrEmpty(target))
            {
                report.Errors.Add("no path given");
                return report;
            }

            string full = Path.GetFullPath(target);
            if (Directory.Exists(full))
            {
                ImportDirectory(cfg, store, full, recursive, report, enqueueHash);
                return report;
            }

            if (File.Exists(full))
            {
                string ext = Path.GetExtension(full).ToLowerInvariant();
                if (ext == ".torrent") ImportTorrent(cfg, store, full, report);
                else ImportHashes(full, report, enqueueHash);
                return report;
            }

            report.Errors.Add("path not found: " + full);
            return report;
        }

        private static void ImportDirectory(Config cfg, TorrentStore store, string dir, bool recursive,
                                            ImportReport report, Action<string> enqueueHash)
        {
            SearchOption option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            string[] files;
            try
            {
                files = Directory.GetFiles(dir, "*", option);
            }
            catch (Exception ex)
            {
                report.Errors.Add("cannot list " + dir + ": " + ex.Message);
                return;
            }

            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < files.Length; i++)
            {
                string ext = Path.GetExtension(files[i]).ToLowerInvariant();
                if (ext == ".torrent") ImportTorrent(cfg, store, files[i], report);
                else if (ext == ".txt" || ext == ".magnet" || ext == ".list")
                    ImportHashes(files[i], report, enqueueHash);
            }
        }

        public static void ImportTorrent(Config cfg, TorrentStore store, string path, ImportReport report)
        {
            report.FilesSeen++;
            string error;
            Torrent torrent = TorrentFileReader.Read(path, cfg, out error);
            if (torrent == null)
            {
                report.Invalid++;
                if (report.Errors.Count < 200) report.Errors.Add(Path.GetFileName(path) + ": " + (error ?? "unknown"));
                return;
            }

            bool added = store.Add(torrent);
            if (added)
            {
                report.Added++;
                report.Bytes += torrent.Size;
                Log.Debug("IMPORT", "added " + torrent.Hex.Substring(0, 8) + "\u2026 " + Text.Truncate(torrent.Name, 70));
            }
            else
            {
                report.Duplicates++;
            }
        }

        /// <summary>
        /// Reads 40-char hashes (or magnet links) from a text file. When a queue
        /// target is given the hashes are handed to it, otherwise the report only
        /// counts them so the caller can decide.
        /// </summary>
        public static void ImportHashes(string path, ImportReport report, Action<string> enqueue)
        {
            try
            {
                using (StreamReader reader = new StreamReader(path, Encoding.UTF8, true))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        string hash = ExtractHash(line);
                        if (hash == null) continue;
                        report.HashesSeen++;
                        if (enqueue != null)
                        {
                            enqueue(hash);
                            report.HashesQueued++;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                report.Errors.Add("cannot read " + path + ": " + ex.Message);
            }
        }

        /// <summary>Pulls an info hash out of a bare hash, a magnet link or a comment.</summary>
        public static string ExtractHash(string line)
        {
            if (string.IsNullOrEmpty(line)) return null;
            string text = line.Trim();
            if (text.Length == 0 || text[0] == '#') return null;

            int marker = text.IndexOf("btih:", StringComparison.OrdinalIgnoreCase);
            if (marker >= 0)
            {
                string tail = text.Substring(marker + 5);
                if (tail.Length >= 40)
                {
                    string candidate = tail.Substring(0, 40);
                    if (Hex.IsHash40(candidate)) return candidate.ToLowerInvariant();
                }
                return null;
            }

            // bare hash somewhere in the line (tab separated reason, quotes, etc.)
            for (int i = 0; i + 40 <= text.Length; i++)
            {
                if (!Hex.IsHash40(text.Substring(i, 40))) continue;
                bool leftOk = i == 0 || !IsHexChar(text[i - 1]);
                bool rightOk = i + 40 >= text.Length || !IsHexChar(text[i + 40]);
                if (leftOk && rightOk) return text.Substring(i, 40).ToLowerInvariant();
            }
            return null;
        }

        private static bool IsHexChar(char c)
        {
            return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
        }
    }
}
