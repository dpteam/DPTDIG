using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Dig.Core
{
    /// <summary>
    /// INI configuration. No App.config, no XML, no dependencies: the file is parsed
    /// by hand, unknown keys are ignored, bad values fall back to safe defaults and a
    /// broken file is quarantined instead of stopping the server.
    ///
    /// Everything the deployment may want to rename lives here, which is what keeps
    /// the repository debranded.
    /// </summary>
    public sealed class Config
    {
        // ---------------------------------------------------------------- paths
        public string BaseDir = ".";
        public string ConfigPath = "config.ini";
        public string DataDir = "data";
        public string WebRoot = "public";
        public string LogDir = "logs";
        public string NodeIdFile = "data/node.id";
        public string BlocklistFile = "data/blocklist.txt";

        public readonly List<string> Warnings = new List<string>();

        // ----------------------------------------------------------------- [site]
        public string SiteName = "dig";
        public string SiteShort = "D";
        public string SiteTagline = "Search the BitTorrent DHT";
        public string SiteDescription = "An open index of torrents discovered on the public DHT.";
        public string BaseUrl = "";
        public string DefaultTheme = "dark";
        public string DefaultLanguage = "ru";
        public string Motd = "";
        public string Footer = "";
        public string SupportEmail = "";
        public string AbuseEmail = "";
        public bool NoIndex = false;

        // ------------------------------------------------------------- [branding]
        // Optional palette overrides. Empty values keep the neutral palette from
        // public/assets/css/app.css, so a fresh install stays debranded.
        public string BrandAccent = "";
        public string BrandAccentHi = "";
        public string BrandAccentLo = "";
        public string BrandDarkAccent = "";
        public string BrandDarkAccentHi = "";
        public string BrandDarkAccentLo = "";
        public string BrandOledAccent = "";
        public string BrandOledAccentHi = "";
        public string BrandOledAccentLo = "";
        public string BrandLightBg = "";
        public string BrandDarkBg = "";
        public string BrandDarkPanel = "";
        public string BrandDarkPanel2 = "";
        public string BrandOledBg = "";
        public string BrandOledPanel = "";
        public string BrandLogoUrl = "";
        public string BrandFaviconUrl = "";
        public string BrandLogoNote = "";

        // --------------------------------------------------------------- [server]
        public int Port = 8090;
        public int[] PortFallbacks = new int[] { 8091, 8092, 8093, 8094 };
        public string Bind = "0.0.0.0";

        // Admin board: its own listener so /api/admin/ simply does not exist on
        // the public port. Bind it to 127.0.0.1 or keep it on the LAN.
        public bool AdminEnabled = true;
        public int AdminPort = 8095;
        public int[] AdminPortFallbacks = new int[] { 8096, 8097 };
        public string AdminBind = "127.0.0.1";
        public bool AdminPrivateOnly = true;

        public int Backlog = 128;
        public int MaxConcurrentRequests = 64;
        public int RequestTimeoutMs = 20000;
        public int KeepAliveMax = 200;
        public bool Compress = true;
        public int RequestsPerMinute = 600;
        public int MaxBodyBytes = 262144;
        public string ShutdownMessage = "THE INDEX IS RESTARTING. PLEASE COME BACK IN A MINUTE.";
        public string PublicBasePath = "";
        public bool TrustProxyHeaders = true;
        public string TrustedProxies = "127.0.0.1,::1,10.0.0.0/8,172.16.0.0/12,192.168.0.0/16,169.254.0.0/16";
        public bool Hsts = false;

        // ------------------------------------------------------------------ [dht]
        public bool DhtEnabled = true;
        public int UdpPort = 6881;
        public int[] UdpPortFallbacks = new int[] { 6882, 6883, 6884 };
        public string DhtBind = "0.0.0.0";
        public string Bootstrap = "router.bittorrent.com:6881,router.utorrent.com:6881,dht.transmissionbt.com:6881,dht.aelitis.com:6881,dht.libtorrent.org:25401,dht.leechcraft.org:6881,dht.bt.am:6881";
        public int QueryConcurrency = 48;
        public int RoutingTableSize = 2000;
        public bool SampleEnabled = true;
        public int SampleIntervalMs = 15000;
        public int SampleNodesPerRound = 24;
        public int CrawlIntervalMs = 5000;
        public bool HarvestAnnounce = true;
        public bool HarvestGetPeers = true;
        public int MaxInfohashesPerMin = 6000;
        public int QueueMax = 200000;
        public bool Ipv6 = false;

        // ------------------------------------------------------------- [metadata]
        public bool MetadataEnabled = true;
        public int MetadataConcurrency = 32;
        public int ConnectTimeoutMs = 4000;
        public int ReadTimeoutMs = 8000;
        public int MaxMetadataBytes = 4194304;
        public int MaxPieces = 1024;
        public int MaxPeersPerHash = 8;
        public int RetryAfterMin = 180;
        public int MinSeeders = 1;

        // ---------------------------------------------------------------- [index]
        public int SnapshotIntervalMin = 10;
        public int SegmentMB = 64;
        public int MaxTorrents = 2000000;
        public int PurgeDays = 0;
        public int MinNameLength = 2;
        public int MaxNameLength = 300;
        public int MaxFiles = 400;
        public int SaveDebounceMs = 2000;

        // --------------------------------------------------------------- [limits]
        public int SearchPerMin = 60;
        public int ApiPerMin = 240;
        public int MaxQueryLength = 120;
        public int ResultsPerPage = 25;
        public int MaxResultsPerPage = 100;
        public int LogRingSize = 1500;

        // -------------------------------------------------------------- [logging]
        public LogLevel LogLevel = LogLevel.Info;
        public bool DhtTrace = false;
        public bool ApiTrace = true;
        public int LogMaxFileMB = 8;
        public int LogRetentionDays = 14;

        // ------------------------------------------------------------- [security]
        public string AdminToken = "";
        public string ServerSecret = "";
        public bool AllowQueryToken = false;

        // ------------------------------------------------------------------ load

        public static Config Load(string baseDir)
        {
            Config cfg = new Config();
            cfg.BaseDir = string.IsNullOrEmpty(baseDir) ? "." : baseDir;
            cfg.ConfigPath = Path.Combine(cfg.BaseDir, "config.ini");

            if (!File.Exists(cfg.ConfigPath))
            {
                string example = Path.Combine(cfg.BaseDir, "config.example.ini");
                try
                {
                    if (File.Exists(example))
                    {
                        File.Copy(example, cfg.ConfigPath);
                        cfg.Warnings.Add("config.ini was created from config.example.ini");
                    }
                }
                catch (Exception ex)
                {
                    cfg.Warnings.Add("cannot create config.ini: " + ex.Message);
                }
            }

            if (File.Exists(cfg.ConfigPath))
            {
                try
                {
                    cfg.Parse(File.ReadAllLines(cfg.ConfigPath, Encoding.UTF8));
                }
                catch (Exception ex)
                {
                    string quarantine = cfg.ConfigPath + ".bad-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                    try { File.Copy(cfg.ConfigPath, quarantine, true); } catch { }
                    cfg.Warnings.Add("config.ini is unreadable (" + ex.Message + "), using defaults");
                }
            }

            cfg.Normalize();
            cfg.EnsureSecrets();
            return cfg;
        }

        private void Parse(string[] lines)
        {
            string section = "";
            for (int i = 0; i < lines.Length; i++)
            {
                string raw = lines[i];
                if (raw == null) continue;
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == ';' || line[0] == '#') continue;

                if (line[0] == '[')
                {
                    int end = line.IndexOf(']');
                    section = end > 0 ? line.Substring(1, end - 1).Trim().ToLowerInvariant() : "";
                    continue;
                }

                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                string value = line.Substring(eq + 1).Trim();

                switch (section)
                {
                    case "site":
                        if (key == "name") SiteName = value;
                        else if (key == "shortname") SiteShort = value;
                        else if (key == "tagline") SiteTagline = value;
                        else if (key == "description") SiteDescription = value;
                        else if (key == "baseurl") BaseUrl = value;
                        else if (key == "defaulttheme") DefaultTheme = value;
                        else if (key == "defaultlanguage") DefaultLanguage = value;
                        else if (key == "motd") Motd = value;
                        else if (key == "footer") Footer = value;
                        else if (key == "supportemail") SupportEmail = value;
                        else if (key == "abuseemail") AbuseEmail = value;
                        else if (key == "noindex") NoIndex = Bool(value, NoIndex);
                        break;

                    case "branding":
                        if (key == "accent") BrandAccent = value;
                        else if (key == "accenthi") BrandAccentHi = value;
                        else if (key == "accentlo") BrandAccentLo = value;
                        else if (key == "darkaccent") BrandDarkAccent = value;
                        else if (key == "darkaccenthi") BrandDarkAccentHi = value;
                        else if (key == "darkaccentlo") BrandDarkAccentLo = value;
                        else if (key == "oledaccent") BrandOledAccent = value;
                        else if (key == "oledaccenthi") BrandOledAccentHi = value;
                        else if (key == "oledaccentlo") BrandOledAccentLo = value;
                        else if (key == "lightbg") BrandLightBg = value;
                        else if (key == "darkbg") BrandDarkBg = value;
                        else if (key == "darkpanel") BrandDarkPanel = value;
                        else if (key == "darkpanel2") BrandDarkPanel2 = value;
                        else if (key == "oledbg") BrandOledBg = value;
                        else if (key == "oledpanel") BrandOledPanel = value;
                        else if (key == "logourl") BrandLogoUrl = value;
                        else if (key == "faviconurl") BrandFaviconUrl = value;
                        else if (key == "logonote") BrandLogoNote = value;
                        break;

                    case "server":
                        if (key == "port") Port = Int(value, Port);
                        else if (key == "portfallbacks") PortFallbacks = IntList(value, PortFallbacks);
                        else if (key == "bind") Bind = value;
                        else if (key == "adminenabled") AdminEnabled = Bool(value, AdminEnabled);
                        else if (key == "adminport") AdminPort = Int(value, AdminPort);
                        else if (key == "adminportfallbacks") AdminPortFallbacks = IntList(value, AdminPortFallbacks);
                        else if (key == "adminbind") AdminBind = value;
                        else if (key == "adminprivateonly") AdminPrivateOnly = Bool(value, AdminPrivateOnly);
                        else if (key == "backlog") Backlog = Int(value, Backlog);
                        else if (key == "maxconcurrentrequests") MaxConcurrentRequests = Int(value, MaxConcurrentRequests);
                        else if (key == "requesttimeoutms") RequestTimeoutMs = Int(value, RequestTimeoutMs);
                        else if (key == "keepalivemax") KeepAliveMax = Int(value, KeepAliveMax);
                        else if (key == "compress") Compress = Bool(value, Compress);
                        else if (key == "requestsperminute") RequestsPerMinute = Int(value, RequestsPerMinute);
                        else if (key == "maxbodybytes") MaxBodyBytes = Int(value, MaxBodyBytes);
                        else if (key == "shutdownmessage") ShutdownMessage = value;
                        else if (key == "publicbasepath") PublicBasePath = value;
                        else if (key == "trustproxyheaders") TrustProxyHeaders = Bool(value, TrustProxyHeaders);
                        else if (key == "trustedproxies") TrustedProxies = value;
                        else if (key == "hsts") Hsts = Bool(value, Hsts);
                        break;

                    case "dht":
                        if (key == "enabled") DhtEnabled = Bool(value, DhtEnabled);
                        else if (key == "udpport") UdpPort = Int(value, UdpPort);
                        else if (key == "udpportfallbacks") UdpPortFallbacks = IntList(value, UdpPortFallbacks);
                        else if (key == "bind") DhtBind = value;
                        else if (key == "bootstrap") Bootstrap = value;
                        else if (key == "queryconcurrency") QueryConcurrency = Int(value, QueryConcurrency);
                        else if (key == "routingtablesize") RoutingTableSize = Int(value, RoutingTableSize);
                        else if (key == "sampleenabled") SampleEnabled = Bool(value, SampleEnabled);
                        else if (key == "sampleintervalms") SampleIntervalMs = Int(value, SampleIntervalMs);
                        else if (key == "samplenodesperround") SampleNodesPerRound = Int(value, SampleNodesPerRound);
                        else if (key == "crawlintervalms") CrawlIntervalMs = Int(value, CrawlIntervalMs);
                        else if (key == "harvestannounce") HarvestAnnounce = Bool(value, HarvestAnnounce);
                        else if (key == "harvestgetpeers") HarvestGetPeers = Bool(value, HarvestGetPeers);
                        else if (key == "maxinfohashespermin") MaxInfohashesPerMin = Int(value, MaxInfohashesPerMin);
                        else if (key == "queuemax") QueueMax = Int(value, QueueMax);
                        else if (key == "nodeidfile") NodeIdFile = value;
                        else if (key == "ipv6") Ipv6 = Bool(value, Ipv6);
                        break;

                    case "metadata":
                        if (key == "enabled") MetadataEnabled = Bool(value, MetadataEnabled);
                        else if (key == "concurrency") MetadataConcurrency = Int(value, MetadataConcurrency);
                        else if (key == "connecttimeoutms") ConnectTimeoutMs = Int(value, ConnectTimeoutMs);
                        else if (key == "readtimeoutms") ReadTimeoutMs = Int(value, ReadTimeoutMs);
                        else if (key == "maxmetadatabytes") MaxMetadataBytes = Int(value, MaxMetadataBytes);
                        else if (key == "maxpieces") MaxPieces = Int(value, MaxPieces);
                        else if (key == "maxpeersperhash") MaxPeersPerHash = Int(value, MaxPeersPerHash);
                        else if (key == "retryaftermin") RetryAfterMin = Int(value, RetryAfterMin);
                        else if (key == "minseeders") MinSeeders = Int(value, MinSeeders);
                        break;

                    case "index":
                        if (key == "datadir") DataDir = value;
                        else if (key == "webroot") WebRoot = value;
                        else if (key == "snapshotintervalmin") SnapshotIntervalMin = Int(value, SnapshotIntervalMin);
                        else if (key == "segmentmb") SegmentMB = Int(value, SegmentMB);
                        else if (key == "maxtorrents") MaxTorrents = Int(value, MaxTorrents);
                        else if (key == "purgedays") PurgeDays = Int(value, PurgeDays);
                        else if (key == "minnamelength") MinNameLength = Int(value, MinNameLength);
                        else if (key == "maxnamelength") MaxNameLength = Int(value, MaxNameLength);
                        else if (key == "maxfiles") MaxFiles = Int(value, MaxFiles);
                        else if (key == "blocklistfile") BlocklistFile = value;
                        else if (key == "savedebouncems") SaveDebounceMs = Int(value, SaveDebounceMs);
                        break;

                    case "limits":
                        if (key == "searchpermin") SearchPerMin = Int(value, SearchPerMin);
                        else if (key == "apipermin") ApiPerMin = Int(value, ApiPerMin);
                        else if (key == "maxquerylength") MaxQueryLength = Int(value, MaxQueryLength);
                        else if (key == "resultsperpage") ResultsPerPage = Int(value, ResultsPerPage);
                        else if (key == "maxresultsperpage") MaxResultsPerPage = Int(value, MaxResultsPerPage);
                        else if (key == "logringsize") LogRingSize = Int(value, LogRingSize);
                        break;

                    case "logging":
                        if (key == "dir") LogDir = value;
                        else if (key == "level") LogLevel = ParseLevel(value, LogLevel);
                        else if (key == "dhttrace") DhtTrace = Bool(value, DhtTrace);
                        else if (key == "apitrace") ApiTrace = Bool(value, ApiTrace);
                        else if (key == "maxfilemb") LogMaxFileMB = Int(value, LogMaxFileMB);
                        else if (key == "retentiondays") LogRetentionDays = Int(value, LogRetentionDays);
                        break;

                    case "security":
                        if (key == "admintoken") AdminToken = value;
                        else if (key == "serversecret") ServerSecret = value;
                        else if (key == "allowquerytoken") AllowQueryToken = Bool(value, AllowQueryToken);
                        break;
                }
            }
        }

        // ------------------------------------------------------------- normalize

        public void Normalize()
        {
            if (Port <= 0 || Port > 65535) Port = 8090;
            if (AdminPort <= 0 || AdminPort > 65535) AdminPort = 8095;
            if (UdpPort <= 0 || UdpPort > 65535) UdpPort = 6881;

            PortFallbacks = CleanPorts(PortFallbacks, Port);
            AdminPortFallbacks = CleanPorts(AdminPortFallbacks, AdminPort);
            UdpPortFallbacks = CleanPorts(UdpPortFallbacks, UdpPort);

            if (Backlog < 16) Backlog = 16;
            if (MaxConcurrentRequests < 4) MaxConcurrentRequests = 4;
            if (MaxConcurrentRequests > 512) MaxConcurrentRequests = 512;
            if (RequestTimeoutMs < 1000) RequestTimeoutMs = 1000;
            if (RequestTimeoutMs > 300000) RequestTimeoutMs = 300000;
            if (KeepAliveMax < 1) KeepAliveMax = 1;
            if (KeepAliveMax > 10000) KeepAliveMax = 10000;
            if (RequestsPerMinute < 0) RequestsPerMinute = 0;
            if (MaxBodyBytes < 4096) MaxBodyBytes = 4096;
            if (MaxBodyBytes > 16 * 1024 * 1024) MaxBodyBytes = 16 * 1024 * 1024;

            if (QueryConcurrency < 1) QueryConcurrency = 1;
            if (QueryConcurrency > 256) QueryConcurrency = 256;
            if (RoutingTableSize < 64) RoutingTableSize = 64;
            if (RoutingTableSize > 50000) RoutingTableSize = 50000;
            if (SampleIntervalMs < 1000) SampleIntervalMs = 1000;
            if (SampleNodesPerRound < 1) SampleNodesPerRound = 1;
            if (SampleNodesPerRound > 256) SampleNodesPerRound = 256;
            if (CrawlIntervalMs < 1000) CrawlIntervalMs = 1000;
            if (MaxInfohashesPerMin < 1) MaxInfohashesPerMin = 1;
            if (QueueMax < 1000) QueueMax = 1000;

            if (MetadataConcurrency < 1) MetadataConcurrency = 1;
            if (MetadataConcurrency > 128) MetadataConcurrency = 128;
            if (ConnectTimeoutMs < 500) ConnectTimeoutMs = 500;
            if (ReadTimeoutMs < 1000) ReadTimeoutMs = 1000;
            if (MaxMetadataBytes < 16384) MaxMetadataBytes = 16384;
            if (MaxMetadataBytes > 16 * 1024 * 1024) MaxMetadataBytes = 16 * 1024 * 1024;
            if (MaxPieces < 1) MaxPieces = 1;
            if (MaxPieces > 8192) MaxPieces = 8192;
            if (MaxPeersPerHash < 1) MaxPeersPerHash = 1;
            if (RetryAfterMin < 1) RetryAfterMin = 1;
            if (MinSeeders < 0) MinSeeders = 0;

            if (SnapshotIntervalMin < 0) SnapshotIntervalMin = 0;
            if (SegmentMB < 8) SegmentMB = 8;
            if (MaxTorrents < 1000) MaxTorrents = 1000;
            if (PurgeDays < 0) PurgeDays = 0;
            if (MinNameLength < 1) MinNameLength = 1;
            if (MaxNameLength < 16) MaxNameLength = 16;
            if (MaxNameLength > 2000) MaxNameLength = 2000;
            if (MaxFiles < 10) MaxFiles = 10;
            if (MaxFiles > 5000) MaxFiles = 5000;
            if (SaveDebounceMs < 100) SaveDebounceMs = 100;

            if (SearchPerMin < 0) SearchPerMin = 0;
            if (ApiPerMin < 0) ApiPerMin = 0;
            if (MaxQueryLength < 8) MaxQueryLength = 8;
            if (MaxQueryLength > 500) MaxQueryLength = 500;
            if (ResultsPerPage < 5) ResultsPerPage = 5;
            if (ResultsPerPage > 100) ResultsPerPage = 100;
            if (MaxResultsPerPage < ResultsPerPage) MaxResultsPerPage = ResultsPerPage;
            if (MaxResultsPerPage > 200) MaxResultsPerPage = 200;
            if (LogRingSize < 100) LogRingSize = 100;

            if (SiteName == null || SiteName.Trim().Length == 0) SiteName = "dig";
            SiteName = SiteName.Trim();
            if (SiteShort == null || SiteShort.Trim().Length == 0)
                SiteShort = SiteName.Substring(0, 1).ToUpperInvariant();
            SiteShort = SiteShort.Trim();
            if (SiteShort.Length > 3) SiteShort = SiteShort.Substring(0, 3);

            if (DefaultTheme != "light" && DefaultTheme != "dark" && DefaultTheme != "oled")
                DefaultTheme = "dark";
            if (DefaultLanguage != "ru" && DefaultLanguage != "en")
                DefaultLanguage = "en";

            if (PublicBasePath.Length > 0 && !PublicBasePath.StartsWith("/", StringComparison.Ordinal))
                PublicBasePath = "/" + PublicBasePath;
            if (PublicBasePath.EndsWith("/", StringComparison.Ordinal))
                PublicBasePath = PublicBasePath.Substring(0, PublicBasePath.Length - 1);

            if (string.IsNullOrEmpty(BaseUrl))
                BaseUrl = "http://localhost:" + Port.ToString(CultureInfo.InvariantCulture);
            if (BaseUrl.EndsWith("/", StringComparison.Ordinal))
                BaseUrl = BaseUrl.Substring(0, BaseUrl.Length - 1);

            if (DhtEnabled && MetadataConcurrency > QueryConcurrency) { /* independent on purpose */ }
        }

        private static int[] CleanPorts(int[] ports, int primary)
        {
            if (ports == null || ports.Length == 0) return new int[0];
            List<int> list = new List<int>();
            for (int i = 0; i < ports.Length; i++)
            {
                int p = ports[i];
                if (p > 0 && p <= 65535 && p != primary && !list.Contains(p)) list.Add(p);
            }
            return list.ToArray();
        }

        public void EnsureSecrets()
        {
            bool changed = false;
            if (string.IsNullOrEmpty(AdminToken) || AdminToken.Length < 16)
            {
                AdminToken = Crypto.RandomHex(16);
                SetIniValue("security", "adminToken", AdminToken);
                changed = true;
            }
            if (string.IsNullOrEmpty(ServerSecret) || ServerSecret.Length < 32)
            {
                ServerSecret = Crypto.RandomHex(32);
                SetIniValue("security", "serverSecret", ServerSecret);
                changed = true;
            }
            if (changed) Warnings.Add("generated missing secrets in config.ini");
        }

        /// <summary>Replaces key=... inside [section], preserving comments and layout.</summary>
        public void SetIniValue(string section, string key, string value)
        {
            try
            {
                List<string> lines = new List<string>();
                if (File.Exists(ConfigPath)) lines.AddRange(File.ReadAllLines(ConfigPath, Encoding.UTF8));

                string current = "";
                bool done = false;
                int sectionStart = -1;

                for (int i = 0; i < lines.Count; i++)
                {
                    string t = lines[i].Trim();
                    if (t.StartsWith("[", StringComparison.Ordinal))
                    {
                        int end = t.IndexOf(']');
                        string name = end > 0 ? t.Substring(1, end - 1).Trim().ToLowerInvariant() : "";
                        current = name;
                        if (name == section.ToLowerInvariant()) sectionStart = i;
                        continue;
                    }
                    if (current != section.ToLowerInvariant()) continue;
                    int eq = t.IndexOf('=');
                    if (eq <= 0) continue;
                    if (t.Substring(0, eq).Trim().ToLowerInvariant() == key.ToLowerInvariant())
                    {
                        lines[i] = key + "=" + value;
                        done = true;
                        break;
                    }
                }

                if (!done)
                {
                    if (sectionStart < 0)
                    {
                        if (lines.Count > 0 && lines[lines.Count - 1].Trim().Length > 0) lines.Add("");
                        lines.Add("[" + section + "]");
                        lines.Add(key + "=" + value);
                    }
                    else
                    {
                        int insertAt = lines.Count;
                        for (int i = sectionStart + 1; i < lines.Count; i++)
                        {
                            if (lines[i].Trim().StartsWith("[", StringComparison.Ordinal)) { insertAt = i; break; }
                        }
                        lines.Insert(insertAt, key + "=" + value);
                    }
                }

                File.WriteAllLines(ConfigPath, lines.ToArray(), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                Warnings.Add("cannot write config: " + ex.Message);
            }
        }

        // ---------------------------------------------------------------- helpers

        private static int Int(string s, int def)
        {
            int v;
            return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : def;
        }

        private static bool Bool(string s, bool def)
        {
            if (s == null) return def;
            s = s.Trim().ToLowerInvariant();
            if (s == "1" || s == "true" || s == "yes" || s == "on") return true;
            if (s == "0" || s == "false" || s == "no" || s == "off") return false;
            return def;
        }

        private static int[] IntList(string s, int[] def)
        {
            if (string.IsNullOrEmpty(s)) return def;
            List<int> list = new List<int>();
            string[] parts = s.Split(',');
            for (int i = 0; i < parts.Length; i++)
            {
                int v;
                if (int.TryParse(parts[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v) &&
                    v > 0 && v <= 65535 && !list.Contains(v))
                    list.Add(v);
            }
            return list.Count == 0 ? def : list.ToArray();
        }

        public static LogLevel ParseLevel(string s, LogLevel def)
        {
            if (s == null) return def;
            s = s.Trim().ToLowerInvariant();
            if (s == "error") return LogLevel.Error;
            if (s == "warn" || s == "warning") return LogLevel.Warn;
            if (s == "info") return LogLevel.Info;
            if (s == "debug") return LogLevel.Debug;
            if (s == "verbose") return LogLevel.Verbose;
            return def;
        }

        public int[] Ports()
        {
            List<int> list = new List<int>();
            list.Add(Port);
            for (int i = 0; i < PortFallbacks.Length; i++) list.Add(PortFallbacks[i]);
            return list.ToArray();
        }

        public int[] AdminPorts()
        {
            List<int> list = new List<int>();
            list.Add(AdminPort);
            for (int i = 0; i < AdminPortFallbacks.Length; i++) list.Add(AdminPortFallbacks[i]);
            return list.ToArray();
        }

        public int[] UdpPorts()
        {
            List<int> list = new List<int>();
            list.Add(UdpPort);
            for (int i = 0; i < UdpPortFallbacks.Length; i++) list.Add(UdpPortFallbacks[i]);
            return list.ToArray();
        }

        public string Resolve(string path)
        {
            if (string.IsNullOrEmpty(path)) return BaseDir;
            if (Path.IsPathRooted(path)) return path;
            return Path.Combine(BaseDir, path);
        }

        public string DataPath(string relative)
        {
            return Path.Combine(Resolve(DataDir), relative);
        }

        public string WebPath(string relative)
        {
            return Path.Combine(Resolve(WebRoot), relative.Replace('/', Path.DirectorySeparatorChar));
        }

        public string BasePath()
        {
            return PublicBasePath ?? "";
        }

        /// <summary>Absolute URL for a site-relative path, respecting publicBasePath.</summary>
        public string Url(string path)
        {
            string p = path ?? "/";
            if (!p.StartsWith("/", StringComparison.Ordinal)) p = "/" + p;
            return BaseUrl + BasePath() + p;
        }

        public bool HasBranding
        {
            get
            {
                return BrandAccent.Length > 0 || BrandDarkAccent.Length > 0 || BrandOledAccent.Length > 0 ||
                       BrandLightBg.Length > 0 || BrandDarkBg.Length > 0 || BrandOledBg.Length > 0 ||
                       BrandDarkPanel.Length > 0 || BrandOledPanel.Length > 0;
            }
        }

        public string[] BootstrapList()
        {
            List<string> list = new List<string>();
            if (string.IsNullOrEmpty(Bootstrap)) return list.ToArray();
            string[] parts = Bootstrap.Split(',');
            for (int i = 0; i < parts.Length; i++)
            {
                string p = parts[i].Trim();
                if (p.Length > 0) list.Add(p);
            }
            return list.ToArray();
        }

        /// <summary>Accepts #rgb / #rrggbb and returns #rrggbb, or "" when invalid/empty.</summary>
        public static string NormalizeColor(string value)
        {
            if (value == null) return "";
            string v = value.Trim();
            if (v.Length == 0) return "";
            if (!v.StartsWith("#", StringComparison.Ordinal)) v = "#" + v;
            string hex = v.Substring(1);
            if (hex.Length == 3)
                hex = new string(new char[] { hex[0], hex[0], hex[1], hex[1], hex[2], hex[2] });
            if (hex.Length != 6) return "";
            for (int i = 0; i < hex.Length; i++)
            {
                char c = hex[i];
                bool ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!ok) return "";
            }
            return "#" + hex.ToLowerInvariant();
        }
    }
}
