using System;
using System.Globalization;
using System.IO;
using System.Threading;
using Dig.Core;
using Dig.Dht;
using Dig.Store;
using Dig.Web;

namespace Dig
{
    /// <summary>
    /// Process entry point: config, logging, index, DHT, crawler, two web boards.
    /// No App.config, no service host, no dependencies - just an exe.
    /// </summary>
    internal static class Program
    {
        private static ServerContext _ctx;
        private static WebServer _public;
        private static WebServer _admin;
        private static DhtNode _dht;
        private static Crawler _crawler;
        private static TorrentStore _store;
        private static readonly ManualResetEvent StopEvent = new ManualResetEvent(false);
        private static int _exitCode;

        private static int Main(string[] args)
        {
            try { Directory.SetCurrentDirectory(AppDomain.CurrentDomain.BaseDirectory); } catch { }

            bool headless = false;
            bool noDht = false;
            bool noFetch = false;
            bool selfTest = false;
            string levelOverride = null;
            int? port = null;
            int? adminPort = null;
            int? udpPort = null;

            try
            {
                for (int i = 0; i < args.Length; i++)
                {
                    string a = args[i] ?? "";
                    if (Equals(a, "--headless")) headless = true;
                    else if (Equals(a, "--no-dht")) noDht = true;
                    else if (Equals(a, "--no-fetch")) noFetch = true;
                    else if (Equals(a, "--self-test")) selfTest = true;
                    else if (Equals(a, "--level") && i + 1 < args.Length) levelOverride = args[++i];
                    else if (Equals(a, "--port") && i + 1 < args.Length) port = ParseInt(args[++i]);
                    else if (Equals(a, "--admin-port") && i + 1 < args.Length) adminPort = ParseInt(args[++i]);
                    else if (Equals(a, "--udp-port") && i + 1 < args.Length) udpPort = ParseInt(args[++i]);
                    else if (Equals(a, "--help") || Equals(a, "-h")) { PrintUsage(); return 0; }
                    else if (Equals(a, "--version")) { Console.WriteLine("dig " + Version); return 0; }
                    else
                    {
                        Console.Error.WriteLine("Unknown argument: " + a);
                        PrintUsage();
                        return 1;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Bad arguments: " + ex.Message);
                return 1;
            }

            AppDomain.CurrentDomain.UnhandledException += OnUnhandled;
            Console.CancelKeyPress += OnCancelKey;

            if (selfTest) return SelfTest.Run();

            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            Config cfg = Config.Load(baseDir);
            if (levelOverride != null) cfg.LogLevel = Config.ParseLevel(levelOverride, cfg.LogLevel);
            if (port.HasValue && port.Value > 0) cfg.Port = port.Value;
            if (adminPort.HasValue && adminPort.Value > 0) cfg.AdminPort = adminPort.Value;
            if (udpPort.HasValue && udpPort.Value > 0) cfg.UdpPort = udpPort.Value;
            if (noDht) cfg.DhtEnabled = false;
            if (noFetch) cfg.MetadataEnabled = false;
            cfg.Normalize();
            cfg.EnsureSecrets();

            Log.Configure(cfg);
            Banner(cfg);
            for (int i = 0; i < cfg.Warnings.Count; i++) Log.Warn("CFG", cfg.Warnings[i]);

            try
            {
                Directory.CreateDirectory(cfg.Resolve(cfg.DataDir));
                Directory.CreateDirectory(cfg.Resolve(cfg.LogDir));
            }
            catch (Exception ex)
            {
                Log.Error("APP", "cannot prepare directories: " + ex.Message);
            }

            _store = new TorrentStore(cfg);
            _store.Load();

            SearchIndex index = new SearchIndex();
            index.Attach(_store.All());
            _store.Index = index;

            _ctx = new ServerContext();
            _ctx.Cfg = cfg;
            _ctx.Store = _store;
            _ctx.Index = index;
            _ctx.Version = Version;

            if (cfg.DhtEnabled)
            {
                _dht = new DhtNode(cfg);
                if (_dht.Start())
                {
                    _ctx.Dht = _dht;
                    _crawler = new Crawler(cfg, _dht, _store);
                    _ctx.Crawler = _crawler;
                    _crawler.Start();
                }
                else
                {
                    Log.Error("APP", "DHT node failed to start; the web board stays read-only");
                }
            }
            else
            {
                Log.Info("APP", "DHT is disabled in config (dht.enabled=false)");
            }

            _store.StartMaintenance();

            _public = new WebServer(_ctx, false);
            if (!_public.Start()) _exitCode = 2;

            if (cfg.AdminEnabled)
            {
                _admin = new WebServer(_ctx, true);
                if (!_admin.Start()) Log.Warn("APP", "admin board did not start");
            }

            Log.Info("APP", cfg.SiteName + " v" + Version + " ready. public=" +
                            (_public.Running ? _public.Port.ToString(CultureInfo.InvariantCulture) : "off") +
                            " admin=" + (_admin != null && _admin.Running ? _admin.Port.ToString(CultureInfo.InvariantCulture) : "off") +
                            " dht=" + (_dht != null ? "udp/" + _dht.Port : "off") +
                            " index=" + _store.Count);

            if (!headless)
            {
                PrintAdminHint(cfg);
                StartConsoleLoop(headless);
            }
            else
            {
                Log.Info("APP", "headless mode: no console commands, Ctrl+C to stop");
            }

            StopEvent.WaitOne();
            Shutdown();
            return _exitCode;
        }

        public static string Version { get { return "1.0.0"; } }

        private static bool Equals(string a, string b)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        private static int ParseInt(string s)
        {
            int v;
            if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) return v;
            throw new ArgumentException("expected a number, got '" + s + "'");
        }

        private static void PrintUsage()
        {
            Console.WriteLine("dig " + Version + " - DHT search engine");
            Console.WriteLine("  --port N         override server.port");
            Console.WriteLine("  --admin-port N   override server.adminPort");
            Console.WriteLine("  --udp-port N     override dht.udpPort");
            Console.WriteLine("  --level X        Error|Warn|Info|Debug|Verbose");
            Console.WriteLine("  --no-dht         do not join the DHT (viewer mode)");
            Console.WriteLine("  --no-fetch       harvest hashes but do not fetch metadata");
            Console.WriteLine("  --headless       no interactive console (Ctrl+C to stop)");
            Console.WriteLine("  --self-test      run the offline test suite and exit");
            Console.WriteLine("  --help           this text");
        }

        private static void Banner(Config cfg)
        {
            try
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine();
                Console.WriteLine("      _ _       ");
                Console.WriteLine("   __| (_) __ _ ");
                Console.WriteLine("  / _` | |/ _` |");
                Console.WriteLine(" | (_| | | (_| |");
                Console.WriteLine("  \\__,_|_|\\__, |");
                Console.WriteLine("          |___/ ");
                Console.ResetColor();
                Console.WriteLine("  " + cfg.SiteName + " - DHT search engine / indexer");
                Console.WriteLine("  web " + cfg.Port + " | admin " + cfg.AdminPort + " | dht udp/" + cfg.UdpPort +
                                  " | log " + cfg.LogLevel);
                Console.WriteLine("  .NET 4.0 / C# 4 - no external libraries - Windows XP SP3 ready");
                Console.WriteLine();
            }
            catch { }
        }

        private static void PrintAdminHint(Config cfg)
        {
            try
            {
                Console.WriteLine();
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("  PUBLIC:  http://localhost:" + cfg.Port + cfg.BasePath() + "/");
                Console.WriteLine("  ADMIN :  http://localhost:" + cfg.AdminPort + cfg.BasePath() + "/");
                Console.WriteLine("  token :  " + cfg.AdminToken);
                Console.ResetColor();
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine("  The token lives in config.ini [security] adminToken.");
                Console.WriteLine("  Type 'help' for console commands.");
                Console.ResetColor();
                Console.WriteLine();
            }
            catch { }
        }

        private static void StartConsoleLoop(bool headless)
        {
            if (headless) return;

            Thread status = new Thread(StatusLoop);
            status.IsBackground = true;
            status.Name = "console-status";
            status.Start();

            Thread commands = new Thread(CommandLoop);
            commands.IsBackground = true;
            commands.Name = "console-cmd";
            commands.Start();
        }

        private static void StatusLoop()
        {
            while (!StopEvent.WaitOne(10000))
            {
                try
                {
                    long nodes = _dht != null ? _dht.Nodes : 0;
                    long harvested = _crawler != null ? _crawler.Harvested : 0;
                    long fetched = _crawler != null ? _crawler.Fetched : 0;
                    Console.WriteLine("  [" + DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "] " +
                                      "torrents=" + _store.Count +
                                      " nodes=" + nodes +
                                      " hashes=" + harvested +
                                      " fetched=" + fetched +
                                      " queue=" + (_crawler != null ? _crawler.Fetcher.Queued : 0));
                }
                catch { }
            }
        }

        private static void CommandLoop()
        {
            while (!StopEvent.WaitOne(0))
            {
                string line;
                try { line = Console.ReadLine(); }
                catch { return; }
                if (line == null) return;
                line = line.Trim().ToLowerInvariant();
                if (line.Length == 0) continue;

                if (line == "quit" || line == "exit" || line == "q")
                {
                    RequestShutdown();
                    return;
                }
                if (line == "help" || line == "?")
                {
                    Console.WriteLine("  commands: stats, pause, resume, compact, reload, quit");
                    continue;
                }
                if (line == "stats")
                {
                    Console.WriteLine(Api.Stats(_ctx));
                    continue;
                }
                if (line == "pause" && _crawler != null) { _crawler.Paused = true; Console.WriteLine("  crawler paused"); continue; }
                if (line == "resume" && _crawler != null) { _crawler.Paused = false; Console.WriteLine("  crawler resumed"); continue; }
                if (line == "compact") { _store.Compact(); Console.WriteLine("  index compacted"); continue; }
                if (line == "reload" && _crawler != null) { _crawler.LoadBlocklist(); Console.WriteLine("  blocklist reloaded"); continue; }
                Console.WriteLine("  unknown command, try 'help'");
            }
        }

        private static void RequestShutdown()
        {
            try { StopEvent.Set(); } catch { }
        }

        private static void OnCancelKey(object sender, ConsoleCancelEventArgs e)
        {
            e.Cancel = true;
            Log.Warn("APP", "Ctrl+C received, shutting down");
            RequestShutdown();
        }

        private static void OnUnhandled(object sender, UnhandledExceptionEventArgs e)
        {
            try
            {
                Exception ex = e.ExceptionObject as Exception;
                Log.Error("APP", "UNHANDLED: " + (ex == null ? "unknown" : ex.ToString()));
                Log.Shutdown();
            }
            catch { }
            if (e.IsTerminating) _exitCode = 3;
        }

        private static void Shutdown()
        {
            _ctx.ShuttingDown = true;
            Log.Info("APP", "shutdown requested");

            try { if (_public != null) _public.Stop(); } catch (Exception ex) { Log.Warn("APP", "public stop: " + ex.Message); }
            try { if (_admin != null) _admin.Stop(); } catch (Exception ex) { Log.Warn("APP", "admin stop: " + ex.Message); }
            try { if (_crawler != null) _crawler.Stop(); } catch (Exception ex) { Log.Warn("APP", "crawler stop: " + ex.Message); }
            try { if (_dht != null) { _dht.SaveCache(); _dht.Stop(); } } catch (Exception ex) { Log.Warn("APP", "dht stop: " + ex.Message); }
            try { if (_store != null) _store.Close(); } catch (Exception ex) { Log.Warn("APP", "index close: " + ex.Message); }

            Log.Info("APP", "goodbye");
            Log.Shutdown();
        }
    }
}
