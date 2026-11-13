using System;
using System.Globalization;
using System.IO;
using System.Threading;
using Dig.Core;
using Dig.Dht;
using Dig.Import;
using Dig.Store;
using Dig.Web;

namespace Dig
{
    /// <summary>
    /// Process entry point: config, logging, index, DHT, crawler, two web boards.
    /// No App.config, no NuGet - just an exe that also knows how to be a service.
    /// </summary>
    internal static class Program
    {
        private static ServerContext _ctx;
        private static WebServer _public;
        private static WebServer _admin;
        private static DhtNode _dht;
        private static Crawler _crawler;
        private static TorrentStore _store;
        private static PendingHashes _pending;
        private static PeerStore _peers;
        private static Config _cfg;
        private static readonly ManualResetEvent StopEvent = new ManualResetEvent(false);
        private static int _exitCode;

        public static string Version { get { return "1.1.0"; } }

        private static int Main(string[] args)
        {
            try { Directory.SetCurrentDirectory(AppDomain.CurrentDomain.BaseDirectory); } catch { }

            Options options;
            try
            {
                options = Options.Parse(args);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Bad arguments: " + ex.Message);
                PrintUsage();
                return 1;
            }

            if (options.ShowHelp) { PrintUsage(); return 0; }
            if (options.ShowVersion) { Console.WriteLine("dig " + Version); return 0; }
            if (options.SelfTest) return SelfTest.Run();

            AppDomain.CurrentDomain.UnhandledException += OnUnhandled;
            Console.CancelKeyPress += OnCancelKey;

            Config cfg = Config.Load(AppDomain.CurrentDomain.BaseDirectory);
            options.Apply(cfg);
            cfg.Normalize();
            cfg.EnsureSecrets();

            Log.Configure(cfg);

            if (options.ImportPath != null) return RunImport(cfg, options);
            if (options.InstallService) return ServiceHost.Install(cfg);
            if (options.UninstallService) return ServiceHost.Uninstall();

            // Service mode only makes sense when the SCM started us. Environment.
            // UserInteractive is false exactly for service processes; running
            // --service by hand must not block forever on a dispatcher that will
            // never connect, so it degrades to a normal headless run instead.
            if (options.RunAsService && Environment.UserInteractive)
            {
                Log.Warn("SVC", "--service was requested from an interactive session; " +
                                "running headless instead (install it with --install-service)");
                options.Headless = true;
                return RunConsole(cfg, options);
            }

            if (options.RunAsService) return ServiceHost.Run(cfg);

            return RunConsole(cfg, options);
        }

        // ------------------------------------------------------------------ console

        private static int RunConsole(Config cfg, Options options)
        {
            Banner(cfg);
            for (int i = 0; i < cfg.Warnings.Count; i++) Log.Warn("CFG", cfg.Warnings[i]);

            if (!StartCore(cfg))
            {
                Shutdown();
                return _exitCode != 0 ? _exitCode : 2;
            }

            if (!options.Headless)
            {
                PrintAdminHint(cfg);
                StartConsoleLoop();
            }
            else
            {
                Log.Info("APP", "headless mode: no console commands, Ctrl+C to stop");
            }

            StopEvent.WaitOne();
            Shutdown();

            if (!options.Headless)
            {
                try
                {
                    Console.WriteLine();
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.WriteLine("Server stopped. Press ENTER to close this window.");
                    Console.ResetColor();
                    Console.ReadLine();
                }
                catch { }
            }
            return _exitCode;
        }

        /// <summary>Starts every component. Used by the console host and by the service host.</summary>
        internal static bool StartCore(Config cfg)
        {
            _cfg = cfg;

            try
            {
                Directory.CreateDirectory(cfg.Resolve(cfg.DataDir));
                Directory.CreateDirectory(cfg.Resolve(cfg.LogDir));
            }
            catch (Exception ex)
            {
                Log.Error("APP", "cannot prepare directories: " + ex.Message);
            }

            Log.Info("APP", cfg.SiteName + " v" + Version + " starting (CLR " + Environment.Version +
                            ", " + Environment.OSVersion.VersionString + ")");
            for (int i = 0; i < cfg.Warnings.Count; i++) Log.Warn("CFG", cfg.Warnings[i]);

            _store = new TorrentStore(cfg);
            _store.Load();

            SearchIndex index = new SearchIndex();
            index.Attach(_store.All());
            _store.Index = index;

            _peers = new PeerStore(cfg);
            _peers.Load();

            _pending = new PendingHashes(cfg, cfg.PendingHashesMax);
            _pending.Load();

            _ctx = new ServerContext();
            _ctx.Cfg = cfg;
            _ctx.Store = _store;
            _ctx.Index = index;
            _ctx.Peers = _peers;
            _ctx.Version = Version;

            if (cfg.DhtEnabled)
            {
                _dht = new DhtNode(cfg);
                if (_dht.Start())
                {
                    _ctx.Dht = _dht;
                    _crawler = new Crawler(cfg, _dht, _store);
                    _crawler.Pending = _pending;
                    _crawler.Peers = _peers;
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

            Log.Info("APP", "ready. public=" +
                            (_public.Running ? _public.Port.ToString(CultureInfo.InvariantCulture) : "off") +
                            " admin=" + (_admin != null && _admin.Running ? _admin.Port.ToString(CultureInfo.InvariantCulture) : "off") +
                            " dht=" + (_dht != null ? "udp/" + _dht.Port : "off") +
                            " index=" + _store.Count);
            return true;
        }

        internal static void RequestStop()
        {
            try { StopEvent.Set(); } catch { }
        }

        internal static void Shutdown()
        {
            if (_ctx != null) _ctx.ShuttingDown = true;
            Log.Info("APP", "shutdown requested");

            try { if (_public != null) _public.Stop(); } catch (Exception ex) { Log.Warn("APP", "public stop: " + ex.Message); }
            try { if (_admin != null) _admin.Stop(); } catch (Exception ex) { Log.Warn("APP", "admin stop: " + ex.Message); }
            try { if (_crawler != null) _crawler.Stop(); } catch (Exception ex) { Log.Warn("APP", "crawler stop: " + ex.Message); }
            try { if (_dht != null) { _dht.SaveCache(); _dht.Stop(); } } catch (Exception ex) { Log.Warn("APP", "dht stop: " + ex.Message); }
            try { if (_pending != null) _pending.Save(); } catch (Exception ex) { Log.Warn("APP", "pending save: " + ex.Message); }
            try { if (_peers != null) _peers.Close(); } catch (Exception ex) { Log.Warn("APP", "peers close: " + ex.Message); }
            try { if (_store != null) _store.Close(); } catch (Exception ex) { Log.Warn("APP", "index close: " + ex.Message); }

            Log.Info("APP", "goodbye");
            Log.Shutdown();
        }

        // ------------------------------------------------------------------- import

        private static int RunImport(Config cfg, Options options)
        {
            Console.WriteLine("dig " + Version + " - import");
            Console.WriteLine("  target: " + options.ImportPath);
            Console.WriteLine("  mode  : " + (options.ImportHashesOnly ? "hashes only (queued for later)" : "torrent files and hash lists"));

            TorrentStore store = new TorrentStore(cfg);
            store.Load();
            SearchIndex index = new SearchIndex();
            index.Attach(store.All());
            store.Index = index;

            PendingHashes pending = new PendingHashes(cfg, cfg.PendingHashesMax);
            pending.Load();

            ImportReport report = new ImportReport();
            if (options.ImportHashesOnly)
            {
                Importer.ImportHashes(options.ImportPath, report, delegate(string hex)
                {
                    if (pending.Add(hex)) report.HashesQueued++;
                });
            }
            else
            {
                Importer.ImportPath(cfg, store, options.ImportPath, options.Recursive, report, delegate(string hex)
                {
                    if (pending.Add(hex)) report.HashesQueued++;
                });
            }
            pending.Save();

            store.Compact();
            store.Close();

            report.Print();
            Log.Info("IMPORT", "path=" + options.ImportPath + " added=" + report.Added +
                               " duplicates=" + report.Duplicates + " invalid=" + report.Invalid +
                               " hashesQueued=" + report.HashesQueued);
            Log.Shutdown();
            return 0;
        }

        // ------------------------------------------------------------------ options

        private sealed class Options
        {
            public bool Headless;
            public bool SelfTest;
            public bool ShowHelp;
            public bool ShowVersion;
            public bool RunAsService;
            public bool InstallService;
            public bool UninstallService;
            public bool NoDht;
            public bool NoFetch;
            public bool Recursive = true;
            public bool ImportHashesOnly;
            public string Level;
            public string ImportPath;
            public int? Port;
            public int? AdminPort;
            public int? UdpPort;

            public static Options Parse(string[] args)
            {
                Options o = new Options();
                for (int i = 0; i < args.Length; i++)
                {
                    string a = args[i] ?? "";
                    if (Same(a, "--headless")) o.Headless = true;
                    else if (Same(a, "--no-dht")) o.NoDht = true;
                    else if (Same(a, "--no-fetch")) o.NoFetch = true;
                    else if (Same(a, "--self-test")) o.SelfTest = true;
                    else if (Same(a, "--service")) o.RunAsService = true;
                    else if (Same(a, "--install-service")) o.InstallService = true;
                    else if (Same(a, "--uninstall-service")) o.UninstallService = true;
                    else if (Same(a, "--import-hashes")) o.ImportHashesOnly = true;
                    else if (Same(a, "--no-recursive")) o.Recursive = false;
                    else if (Same(a, "--level") && i + 1 < args.Length) o.Level = args[++i];
                    else if (Same(a, "--port") && i + 1 < args.Length) o.Port = ParseInt(args[++i]);
                    else if (Same(a, "--admin-port") && i + 1 < args.Length) o.AdminPort = ParseInt(args[++i]);
                    else if (Same(a, "--udp-port") && i + 1 < args.Length) o.UdpPort = ParseInt(args[++i]);
                    else if ((Same(a, "--import") || Same(a, "-i")) && i + 1 < args.Length) o.ImportPath = args[++i];
                    else if (Same(a, "--help") || Same(a, "-h") || Same(a, "-?")) o.ShowHelp = true;
                    else if (Same(a, "--version")) o.ShowVersion = true;
                    else throw new ArgumentException("unknown option: " + a);
                }
                return o;
            }

            public void Apply(Config cfg)
            {
                if (Level != null) cfg.LogLevel = Config.ParseLevel(Level, cfg.LogLevel);
                if (Port.HasValue && Port.Value > 0) cfg.Port = Port.Value;
                if (AdminPort.HasValue && AdminPort.Value > 0) cfg.AdminPort = AdminPort.Value;
                if (UdpPort.HasValue && UdpPort.Value > 0) cfg.UdpPort = UdpPort.Value;
                if (NoDht) cfg.DhtEnabled = false;
                if (NoFetch) cfg.MetadataEnabled = false;
            }
        }

        private static bool Same(string a, string b)
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
            Console.WriteLine();
            Console.WriteLine("  --port N             override server.port");
            Console.WriteLine("  --admin-port N       override server.adminPort");
            Console.WriteLine("  --udp-port N         override dht.udpPort");
            Console.WriteLine("  --level X            Error|Warn|Info|Debug|Verbose");
            Console.WriteLine("  --no-dht             do not join the DHT (viewer mode)");
            Console.WriteLine("  --no-fetch           harvest hashes but do not fetch metadata");
            Console.WriteLine("  --headless           no interactive console (Ctrl+C to stop)");
            Console.WriteLine();
            Console.WriteLine("  --import PATH        index .torrent files and hash lists offline");
            Console.WriteLine("  --import-hashes      treat PATH as a list of hashes only");
            Console.WriteLine("  --no-recursive       do not descend into subdirectories");
            Console.WriteLine();
            Console.WriteLine("  --install-service    install and start the Windows service");
            Console.WriteLine("  --uninstall-service  stop and remove the Windows service");
            Console.WriteLine("  --service            run as a service (called by the SCM)");
            Console.WriteLine();
            Console.WriteLine("  --self-test          run the offline test suite and exit");
            Console.WriteLine("  --version            print the version");
            Console.WriteLine("  --help               this text");
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

        private static void StartConsoleLoop()
        {
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
                                      " pending=" + (_pending != null ? _pending.Count : 0) +
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
                    RequestStop();
                    return;
                }
                if (line == "help" || line == "?")
                {
                    Console.WriteLine("  commands: stats, pause, resume, compact, reload, pending, quit");
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
                if (line == "pending")
                {
                    if (_pending != null) _pending.Save();
                    Console.WriteLine("  unresolved hashes kept: " + (_pending != null ? _pending.Count : 0));
                    continue;
                }
                Console.WriteLine("  unknown command, try 'help'");
            }
        }

        private static void OnCancelKey(object sender, ConsoleCancelEventArgs e)
        {
            e.Cancel = true;
            Log.Warn("APP", "Ctrl+C received, shutting down");
            RequestStop();
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
    }
}
