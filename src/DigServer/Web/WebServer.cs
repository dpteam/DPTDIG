using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Dig.Core;
using Dig.Store;

namespace Dig.Web
{
    /// <summary>
    /// One listener = one board. The public board and the admin board are separate
    /// instances with separate ports, so /api/admin/ does not exist on the public
    /// port at all.
    /// </summary>
    public sealed partial class WebServer
    {
        private readonly ServerContext _ctx;
        private readonly bool _admin;
        private readonly RateLimiter _rate;
        private readonly Semaphore _slots;
        private readonly string _serverHeader;
        private TcpListener _listener;
        private Thread _acceptThread;
        private volatile bool _running;
        private int _port;
        private long _served;
        private string _root;

        public WebServer(ServerContext ctx, bool admin)
        {
            _ctx = ctx;
            _admin = admin;
            _serverHeader = admin ? "dig-admin" : "dig";
            _rate = new RateLimiter(admin ? 0 : ctx.Cfg.RequestsPerMinute);
            _slots = new Semaphore(Math.Max(4, ctx.Cfg.MaxConcurrentRequests), Math.Max(4, ctx.Cfg.MaxConcurrentRequests));
            _root = ctx.Cfg.Resolve(ctx.Cfg.WebRoot);
        }

        public int Port { get { return _port; } }
        public bool Running { get { return _running; } }
        public long Served { get { return Interlocked.Read(ref _served); } }
        public string BaseUrl
        {
            get
            {
                string host = _admin ? "127.0.0.1" : "localhost";
                return "http://" + host + ":" + _port.ToString(CultureInfo.InvariantCulture) + _ctx.Cfg.BasePath() + "/";
            }
        }

        // ------------------------------------------------------------------- start

        public bool Start()
        {
            IPAddress bindAddress = IPAddress.Any;
            try
            {
                IPAddress parsed;
                string bind = _admin ? _ctx.Cfg.AdminBind : _ctx.Cfg.Bind;
                if (!string.IsNullOrEmpty(bind) && IPAddress.TryParse(bind, out parsed)) bindAddress = parsed;
            }
            catch { }

            int[] ports = _admin ? _ctx.Cfg.AdminPorts() : _ctx.Cfg.Ports();
            for (int i = 0; i < ports.Length; i++)
            {
                try
                {
                    TcpListener listener = new TcpListener(bindAddress, ports[i]);
                    listener.Start(_ctx.Cfg.Backlog);
                    _listener = listener;
                    _port = ports[i];
                    break;
                }
                catch (Exception ex)
                {
                    Log.Warn("WEB", "cannot bind " + (_admin ? "admin" : "public") + " tcp/" + ports[i] + ": " + ex.Message);
                }
            }

            if (_listener == null)
            {
                Log.Error("WEB", (_admin ? "admin" : "public") + " web server could not bind any port");
                return false;
            }

            _running = true;
            _acceptThread = new Thread(AcceptLoop);
            _acceptThread.IsBackground = true;
            _acceptThread.Name = _admin ? "web-admin" : "web-public";
            _acceptThread.Start();

            Log.Info("WEB", (_admin ? "admin" : "public") + " board listening on " +
                             bindAddress + ":" + _port + _ctx.Cfg.BasePath() + "/  root=" + _root);
            return true;
        }

        public void Stop()
        {
            _running = false;
            try { if (_listener != null) _listener.Stop(); } catch { }
            try { if (_acceptThread != null) _acceptThread.Join(2000); } catch { }
        }

        private void AcceptLoop()
        {
            while (_running)
            {
                TcpClient client;
                try
                {
                    client = _listener.AcceptTcpClient();
                }
                catch
                {
                    if (!_running) break;
                    continue;
                }

                if (!_slots.WaitOne(1000))
                {
                    try { client.Close(); } catch { }
                    continue;
                }
                ThreadPool.QueueUserWorkItem(HandleClient, client);
            }
        }

        private void HandleClient(object state)
        {
            TcpClient client = (TcpClient)state;
            HttpConnection connection = null;
            try
            {
                client.ReceiveTimeout = _ctx.Cfg.RequestTimeoutMs;
                client.SendTimeout = _ctx.Cfg.RequestTimeoutMs;
                client.NoDelay = true;

                IPEndPoint remote = client.Client.RemoteEndPoint as IPEndPoint;
                connection = new HttpConnection(client, _ctx.Cfg.MaxBodyBytes);
                if (remote != null)
                {
                    connection.RemoteIp = remote.Address.ToString();
                    connection.RemotePort = remote.Port;
                }

                int served = 0;
                while (_running && served < _ctx.Cfg.KeepAliveMax)
                {
                    HttpRequest request;
                    int errorStatus;
                    try
                    {
                        if (!connection.TryReadRequest(out request, out errorStatus))
                        {
                            if (errorStatus > 0)
                                connection.WriteSimple(errorStatus, HttpResponse.Reason(errorStatus), true, _serverHeader);
                            break;
                        }
                    }
                    catch (HttpProtocolException ex)
                    {
                        connection.WriteSimple(ex.Status, ex.Message, true, _serverHeader);
                        break;
                    }
                    catch
                    {
                        break;
                    }

                    if (request == null) break;

                    HttpResponse response = new HttpResponse();
                    DateTime started = DateTime.UtcNow;
                    try
                    {
                        Route(request, response);
                    }
                    catch (Exception ex)
                    {
                        Log.Error("WEB", request.Method + " " + request.Path + " crashed: " + ex);
                        response = ErrorResponse(500, "Internal error", "Something broke on our side. The request was not completed.");
                    }

                    ApplyCommonHeaders(request, response);
                    Interlocked.Increment(ref _served);
                    Interlocked.Increment(ref _ctx.RequestsServed);

                    bool head = string.Equals(request.Method, "HEAD", StringComparison.OrdinalIgnoreCase);
                    connection.WriteResponse(response, head, request.KeepAlive, _serverHeader);
                    Log.Api("WEB", request.Method + " " + request.Path + " -> " + response.Status +
                                   " (" + (int)(DateTime.UtcNow - started).TotalMilliseconds + "ms, " +
                                   response.Body.Length + "b)");

                    served++;
                    if (!request.KeepAlive) break;
                }
            }
            catch (Exception ex)
            {
                Log.Debug("WEB", "connection: " + ex.Message);
            }
            finally
            {
                try { if (connection != null) connection.Dispose(); } catch { }
                try { client.Close(); } catch { }
                try { _slots.Release(); } catch { }
            }
        }

        // ------------------------------------------------------------------ routing

        private void Route(HttpRequest request, HttpResponse response)
        {
            string ip = ClientIp(request);

            if (_admin)
            {
                if (_ctx.Cfg.AdminPrivateOnly && !NetUtil.IsPrivate(ParseIp(ip)))
                {
                    response.Status = 403;
                    response.ContentType = "text/plain; charset=utf-8";
                    response.Body = Encoding.UTF8.GetBytes("admin board is private-only\n");
                    return;
                }
                RouteAdmin(request, response, ip);
                return;
            }

            if (!_rate.Allow(ip))
            {
                response.Status = 429;
                response.ContentType = "text/html; charset=utf-8";
                Lang limited = _ctx.Language(request);
                response.Body = Encoding.UTF8.GetBytes(Pages.ErrorPage(_ctx, limited, 429, "429", limited.T("no_results_hint")));
                response.Set("Retry-After", "60");
                return;
            }

            string path = request.Path ?? "/";
            string basePath = _ctx.Cfg.BasePath();
            if (basePath.Length > 0)
            {
                if (path == basePath || path == basePath + "/") path = "/";
                else if (path.StartsWith(basePath + "/", StringComparison.Ordinal)) path = path.Substring(basePath.Length);
                else
                {
                    response.Status = 404;
                    response.ContentType = "text/html; charset=utf-8";
                    Lang missing = _ctx.Language(request);
                    response.Body = Encoding.UTF8.GetBytes(Pages.ErrorPage(_ctx, missing, 404, missing.T("not_found"), missing.T("not_found_hint")));
                    return;
                }
            }

            if (TryStatic(path, request, response)) return;

            Lang lang = _ctx.Language(request);
            if (request.QueryString("lang") != null)
                response.SetCookie("diglang", lang.Code, CookiePath(), 31536000, false, false, "Lax");

            if (path == "/" || path == "/index.html")
            {
                Html(response, Pages.Home(_ctx, lang));
                return;
            }

            if (path == "/search")
            {
                string query = (request.QueryString("q") ?? "").Trim();
                if (query.Length > _ctx.Cfg.MaxQueryLength) query = query.Substring(0, _ctx.Cfg.MaxQueryLength);
                int page = ParseInt(request.QueryString("page"), 1);
                int perPage = Clamp(ParseInt(request.QueryString("per"), _ctx.Cfg.ResultsPerPage),
                                    _ctx.Cfg.ResultsPerPage, _ctx.Cfg.MaxResultsPerPage);
                string sort = NormalizeSort(request.QueryString("sort"));
                SearchResult result = _ctx.Index.Search(query, page, perPage, sort);
                Html(response, Pages.Search(_ctx, lang, result, query, sort, perPage));
                return;
            }

            if (path == "/latest")
            {
                Redirect(response, _ctx.Url("/search?sort=new"));
                return;
            }

            if (path == "/stats")
            {
                Html(response, Pages.StatsPage(_ctx, lang));
                return;
            }

            if (path == "/about")
            {
                Html(response, Pages.About(_ctx, lang));
                return;
            }

            if (path == "/abuse")
            {
                Html(response, Pages.Abuse(_ctx, lang));
                return;
            }

            if (path.StartsWith("/t/", StringComparison.Ordinal))
            {
                Torrent torrent = FindTorrent(path.Substring(3));
                if (torrent == null)
                {
                    response.Status = 404;
                    Html(response, Pages.ErrorPage(_ctx, lang, 404, lang.T("not_found"), lang.T("not_found_hint")));
                    return;
                }
                Html(response, Pages.TorrentPage(_ctx, lang, torrent));
                return;
            }

            if (path == "/api/search")
            {
                string query = (request.QueryString("q") ?? "").Trim();
                if (query.Length > _ctx.Cfg.MaxQueryLength) query = query.Substring(0, _ctx.Cfg.MaxQueryLength);
                int page = ParseInt(request.QueryString("page"), 1);
                int perPage = Clamp(ParseInt(request.QueryString("per"), _ctx.Cfg.ResultsPerPage),
                                    _ctx.Cfg.ResultsPerPage, _ctx.Cfg.MaxResultsPerPage);
                SearchResult result = _ctx.Index.Search(query, page, perPage, NormalizeSort(request.QueryString("sort")));
                Json(response, Api.Search(_ctx, result, perPage));
                return;
            }

            if (path.StartsWith("/api/torrent/", StringComparison.Ordinal))
            {
                Torrent torrent = FindTorrent(path.Substring("/api/torrent/".Length));
                if (torrent == null)
                {
                    response.Status = 404;
                    Json(response, "{\"error\":\"not found\"}");
                    return;
                }
                Json(response, Api.Torrent(_ctx, torrent));
                return;
            }

            if (path == "/api/stats")
            {
                Json(response, Api.Stats(_ctx));
                return;
            }

            if (path == "/api/health")
            {
                Json(response, Api.Health(_ctx));
                return;
            }

            response.Status = 404;
            Html(response, Pages.ErrorPage(_ctx, lang, 404, lang.T("not_found"), lang.T("not_found_hint")));
        }

        private Torrent FindTorrent(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            string hex = raw.Trim();
            int slash = hex.IndexOf('/');
            if (slash > 0) hex = hex.Substring(0, slash);
            if (!Hex.IsHash40(hex)) return null;
            if (_ctx.Crawler != null && _ctx.Crawler.IsBlocked(hex)) return null;
            return _ctx.Store.Get(hex);
        }

        // ------------------------------------------------------------------- static

        private bool TryStatic(string path, HttpRequest request, HttpResponse response)
        {
            if (path == "/brand.css")
            {
                response.ContentType = "text/css; charset=utf-8";
                response.Body = Encoding.UTF8.GetBytes(Branding.Css(_ctx.Cfg));
                response.Set("Cache-Control", "public, max-age=300");
                return true;
            }

            if (path == "/manifest.webmanifest")
            {
                JsonWriter w = new JsonWriter();
                w.BeginObject();
                w.Prop("name", _ctx.Cfg.SiteName);
                w.Prop("short_name", _ctx.Cfg.SiteShort);
                w.Prop("description", _ctx.Cfg.SiteDescription);
                w.Prop("start_url", _ctx.Cfg.BasePath().Length == 0 ? "/" : _ctx.Cfg.BasePath() + "/");
                w.Prop("scope", _ctx.Cfg.BasePath().Length == 0 ? "/" : _ctx.Cfg.BasePath() + "/");
                w.Prop("display", "standalone");
                w.Prop("background_color", "#0d1116");
                w.Prop("theme_color", "#101418");
                w.Key("icons").BeginArray();
                w.BeginObject();
                w.Prop("src", string.IsNullOrEmpty(_ctx.Cfg.BrandFaviconUrl) ? _ctx.Cfg.BasePath() + "/favicon.svg" : _ctx.Cfg.BrandFaviconUrl);
                w.Prop("sizes", "any");
                w.Prop("type", "image/svg+xml");
                w.Prop("purpose", "any");
                w.EndObject();
                w.EndArray();
                w.EndObject();
                response.ContentType = "application/manifest+json; charset=utf-8";
                response.Body = Encoding.UTF8.GetBytes(w.ToString());
                response.Set("Cache-Control", "public, max-age=3600");
                return true;
            }

            if (path == "/robots.txt")
            {
                response.ContentType = "text/plain; charset=utf-8";
                string body = _ctx.Cfg.NoIndex
                    ? "User-agent: *\nDisallow: /\n"
                    : "User-agent: *\nDisallow: /api/\nAllow: /\nSitemap: " + _ctx.Cfg.Url("/sitemap.xml") + "\n";
                response.Body = Encoding.UTF8.GetBytes(body);
                response.Set("Cache-Control", "public, max-age=3600");
                return true;
            }

            if (path == "/sitemap.xml")
            {
                response.ContentType = "application/xml; charset=utf-8";
                response.Body = Encoding.UTF8.GetBytes(Pages.Sitemap(_ctx));
                response.Set("Cache-Control", "public, max-age=3600");
                return true;
            }

            if (path == "/favicon.ico")
            {
                path = "/favicon.svg";
            }

            bool allowed = path.StartsWith("/assets/", StringComparison.Ordinal) ||
                           path == "/favicon.svg" ||
                           path == "/manifest.webmanifest" ||
                           path == "/og.svg";
            if (!allowed) return false;

            try
            {
                string relative = path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
                string root = Path.GetFullPath(_root);
                string full = Path.GetFullPath(Path.Combine(root, relative));
                if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    return false;
                if (!File.Exists(full)) return false;

                FileInfo info = new FileInfo(full);
                string etag = "\"" + info.Length.ToString(CultureInfo.InvariantCulture) + "-" +
                              info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture) + "\"";
                if (string.Equals(request.Header("If-None-Match"), etag, StringComparison.Ordinal))
                {
                    response.Status = 304;
                    response.Set("ETag", etag);
                    response.Set("Cache-Control", "public, max-age=3600");
                    return true;
                }

                response.ContentType = ContentType(full);
                response.Body = File.ReadAllBytes(full);
                response.Set("ETag", etag);
                response.Set("Cache-Control", "public, max-age=3600");
                return true;
            }
            catch (Exception ex)
            {
                Log.Debug("WEB", "static " + path + ": " + ex.Message);
                return false;
            }
        }

        private static string ContentType(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            switch (ext)
            {
                case ".css": return "text/css; charset=utf-8";
                case ".js": return "application/javascript; charset=utf-8";
                case ".svg": return "image/svg+xml";
                case ".png": return "image/png";
                case ".ico": return "image/x-icon";
                case ".json": return "application/json; charset=utf-8";
                case ".webmanifest": return "application/manifest+json; charset=utf-8";
                case ".txt": return "text/plain; charset=utf-8";
                case ".woff2": return "font/woff2";
                default: return "application/octet-stream";
            }
        }

        // ------------------------------------------------------------------ headers

        private void ApplyCommonHeaders(HttpRequest request, HttpResponse response)
        {
            response.Set("X-Content-Type-Options", "nosniff");
            response.Set("Referrer-Policy", "no-referrer");
            response.Set("X-Frame-Options", "DENY");
            response.Set("Content-Security-Policy",
                "default-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; " +
                "script-src 'self'; object-src 'none'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'");
            response.Set("Permissions-Policy", "geolocation=(), microphone=(), camera=()");

            if (_ctx.Cfg.NoIndex) response.Set("X-Robots-Tag", "noindex, nofollow");
            if (!response.Headers.ContainsKey("Cache-Control"))
                response.Set("Cache-Control", "no-store");

            bool https = request.IsHttps ||
                         string.Equals(request.Header("X-Forwarded-Proto"), "https", StringComparison.OrdinalIgnoreCase);
            if (_ctx.Cfg.Hsts && https) response.Set("Strict-Transport-Security", "max-age=15552000");

            if (response.Body != null && response.Body.Length > 1024 && _ctx.Cfg.Compress)
            {
                string accept = request.Header("Accept-Encoding") ?? "";
                string type = response.ContentType ?? "";
                bool compressible = type.StartsWith("text/", StringComparison.Ordinal) ||
                                    type.StartsWith("application/json", StringComparison.Ordinal) ||
                                    type.StartsWith("application/javascript", StringComparison.Ordinal) ||
                                    type.StartsWith("application/xml", StringComparison.Ordinal) ||
                                    type.StartsWith("image/svg", StringComparison.Ordinal);
                if (compressible && accept.IndexOf("gzip", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    try
                    {
                        response.Body = Gzip(response.Body);
                        response.Set("Content-Encoding", "gzip");
                        response.Set("Vary", "Accept-Encoding");
                    }
                    catch { }
                }
            }
        }

        private static byte[] Gzip(byte[] data)
        {
            using (MemoryStream output = new MemoryStream(data.Length / 2 + 64))
            {
                using (GZipStream gzip = new GZipStream(output, CompressionMode.Compress, true))
                {
                    gzip.Write(data, 0, data.Length);
                }
                return output.ToArray();
            }
        }

        // ------------------------------------------------------------------ helpers

        private string ClientIp(HttpRequest request)
        {
            string ip = request.RemoteIp ?? "0.0.0.0";
            if (!_ctx.Cfg.TrustProxyHeaders) return ip;
            if (!IsTrustedProxy(ip, _ctx.Cfg.TrustedProxies)) return ip;

            string forwarded = request.Header("X-Forwarded-For");
            if (!string.IsNullOrEmpty(forwarded))
            {
                int comma = forwarded.IndexOf(',');
                string first = (comma > 0 ? forwarded.Substring(0, comma) : forwarded).Trim();
                if (first.Length > 0) return first;
            }
            string real = request.Header("X-Real-IP");
            if (!string.IsNullOrEmpty(real)) return real.Trim();
            return ip;
        }

        private static IPAddress ParseIp(string text)
        {
            IPAddress ip;
            return IPAddress.TryParse(text ?? "", out ip) ? ip : null;
        }

        private static bool IsTrustedProxy(string ip, string list)
        {
            IPAddress address = ParseIp(ip);
            if (address == null || string.IsNullOrEmpty(list)) return false;
            byte[] bytes = address.GetAddressBytes();
            string[] parts = list.Split(',');
            for (int i = 0; i < parts.Length; i++)
            {
                string entry = parts[i].Trim();
                if (entry.Length == 0) continue;
                int slash = entry.IndexOf('/');
                if (slash < 0)
                {
                    IPAddress exact = ParseIp(entry);
                    if (exact != null && exact.Equals(address)) return true;
                    continue;
                }
                IPAddress network = ParseIp(entry.Substring(0, slash));
                int bits;
                if (network == null || !int.TryParse(entry.Substring(slash + 1), out bits)) continue;
                byte[] net = network.GetAddressBytes();
                if (net.Length != bytes.Length || bits < 0 || bits > net.Length * 8) continue;
                bool match = true;
                for (int b = 0; b < net.Length && match; b++)
                {
                    int maskBits = Math.Min(8, Math.Max(0, bits - b * 8));
                    int mask = maskBits == 0 ? 0 : (0xff << (8 - maskBits)) & 0xff;
                    if ((net[b] & mask) != (bytes[b] & mask)) match = false;
                }
                if (match) return true;
            }
            return false;
        }

        private string CookiePath()
        {
            return _ctx.Cfg.BasePath().Length == 0 ? "/" : _ctx.Cfg.BasePath() + "/";
        }

        private static int ParseInt(string text, int fallback)
        {
            int value;
            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) ? value : fallback;
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        private static string NormalizeSort(string sort)
        {
            string s = (sort ?? "").Trim().ToLowerInvariant();
            if (s == "new" || s == "size" || s == "seeders") return s;
            return "relevance";
        }

        private static void Html(HttpResponse response, string html)
        {
            response.ContentType = "text/html; charset=utf-8";
            response.Body = Encoding.UTF8.GetBytes(html);
        }

        private static void Json(HttpResponse response, string json)
        {
            response.ContentType = "application/json; charset=utf-8";
            response.Body = Encoding.UTF8.GetBytes(json);
        }

        private static void Redirect(HttpResponse response, string location)
        {
            response.Status = 302;
            response.Set("Location", location);
            response.ContentType = "text/plain; charset=utf-8";
            response.Body = Encoding.UTF8.GetBytes("moved: " + location);
        }

        private HttpResponse ErrorResponse(int status, string title, string message)
        {
            HttpResponse response = new HttpResponse();
            response.Status = status;
            response.ContentType = "text/html; charset=utf-8";
            Lang lang = Lang.For(_ctx.Cfg.DefaultLanguage);
            response.Body = Encoding.UTF8.GetBytes(Pages.ErrorPage(_ctx, lang, status, title, message));
            return response;
        }
    }
}
