using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Dig.Core;

namespace Dig.Web
{
    /// <summary>
    /// Admin board: its own port, its own cookie, no shared session with the public
    /// side. The token lives in config.ini; the browser gets an HMAC-signed cookie
    /// and never sees the token again.
    /// </summary>
    public sealed partial class WebServer
    {
        private readonly RateLimiter _loginRate = new RateLimiter(10);
        private const string AdminCookie = "digadm";

        private string AdminCookieValue()
        {
            return Crypto.HmacSha1Hex(_ctx.Cfg.ServerSecret, "admin|" + _ctx.Cfg.AdminToken);
        }

        private bool IsAdmin(HttpRequest request)
        {
            string cookie = request.Cookie(AdminCookie);
            return !string.IsNullOrEmpty(cookie) && Crypto.Equals(cookie, AdminCookieValue());
        }

        private void RouteAdmin(HttpRequest request, HttpResponse response, string ip)
        {
            string path = request.Path ?? "/";
            string basePath = _ctx.Cfg.BasePath();
            if (basePath.Length > 0 && path.StartsWith(basePath + "/", StringComparison.Ordinal))
                path = path.Substring(basePath.Length);

            // The admin board reuses the same static assets (css, js, favicon), but
            // serves no page from them: everything else needs a session.
            if (TryStatic(path, request, response)) return;

            if (path == "/login")
            {
                if (string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase))
                {
                    if (!_loginRate.Allow(ip))
                    {
                        response.Status = 429;
                        Html(response, AdminPages.Login(_ctx, "Too many attempts, wait a minute."));
                        return;
                    }

                    Dictionary<string, string> form = request.Form();
                    string token;
                    form.TryGetValue("token", out token);
                    if (Crypto.Equals((token ?? "").Trim(), _ctx.Cfg.AdminToken))
                    {
                        bool https = string.Equals(request.Header("X-Forwarded-Proto"), "https", StringComparison.OrdinalIgnoreCase);
                        response.SetCookie(AdminCookie, AdminCookieValue(), CookiePath(), 60 * 60 * 12, https, true, "Lax");
                        Redirect(response, _ctx.Url("/"));
                        return;
                    }

                    Log.Warn("ADMIN", "failed login from " + ip);
                    response.Status = 401;
                    Html(response, AdminPages.Login(_ctx, "Wrong token."));
                    return;
                }

                Html(response, AdminPages.Login(_ctx, null));
                return;
            }

            if (path == "/logout")
            {
                response.SetCookie(AdminCookie, "", CookiePath(), 0, false, true, "Lax");
                Redirect(response, _ctx.Url("/login"));
                return;
            }

            bool authenticated = IsAdmin(request);
            if (!authenticated && _ctx.Cfg.AllowQueryToken)
            {
                string queryToken = request.QueryString("token");
                if (!string.IsNullOrEmpty(queryToken) && Crypto.Equals(queryToken, _ctx.Cfg.AdminToken))
                {
                    response.SetCookie(AdminCookie, AdminCookieValue(), CookiePath(), 60 * 60 * 12, false, true, "Lax");
                    authenticated = true;
                }
            }

            if (!authenticated)
            {
                if (path.StartsWith("/api/", StringComparison.Ordinal))
                {
                    response.Status = 401;
                    Json(response, Api.Error("unauthorized"));
                }
                else
                {
                    Html(response, AdminPages.Login(_ctx, null));
                }
                return;
            }

            if (path == "/" || path == "/index.html")
            {
                Html(response, AdminPages.Dashboard(_ctx));
                return;
            }

            if (path == "/api/stats")
            {
                Json(response, Api.Stats(_ctx));
                return;
            }

            if (path == "/api/log")
            {
                int lines = ParseInt(request.QueryString("lines"), 200);
                if (lines < 1) lines = 1;
                if (lines > 2000) lines = 2000;
                long since;
                long.TryParse(request.QueryString("since"), out since);

                JsonWriter w = new JsonWriter();
                w.BeginObject();
                w.Prop("ok", true);
                List<LogEntry> entries = Log.Tail(since, lines, null, request.QueryString("cat"));
                w.Key("entries").BeginArray();
                for (int i = 0; i < entries.Count; i++)
                {
                    LogEntry e = entries[i];
                    w.BeginObject();
                    w.Prop("seq", e.Seq);
                    w.Prop("time", e.Utc.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture));
                    w.Prop("level", Log.LevelLetter(e.Level));
                    w.Prop("cat", e.Category);
                    w.Prop("msg", e.Message);
                    w.EndObject();
                }
                w.EndArray();
                w.Prop("last", Log.LastSeq);
                w.EndObject();
                Json(response, w.ToString());
                return;
            }

            if (path == "/api/compact" && IsPost(request))
            {
                _ctx.Store.Compact();
                Json(response, Api.Ok("compacted"));
                return;
            }

            if (path == "/api/crawl" && IsPost(request))
            {
                if (_ctx.Crawler == null)
                {
                    Json(response, Api.Error("crawler disabled"));
                    return;
                }
                string action = request.QueryString("action") ?? "toggle";
                if (action == "pause") _ctx.Crawler.Paused = true;
                else if (action == "resume") _ctx.Crawler.Paused = false;
                else _ctx.Crawler.Paused = !_ctx.Crawler.Paused;
                Json(response, Api.Ok(_ctx.Crawler.Paused ? "paused" : "resumed"));
                return;
            }

            if (path == "/api/reload" && IsPost(request))
            {
                if (_ctx.Crawler != null) _ctx.Crawler.LoadBlocklist();
                Json(response, Api.Ok("blocklist reloaded"));
                return;
            }

            if (path == "/api/config")
            {
                Json(response, AdminPages.ConfigJson(_ctx));
                return;
            }

            response.Status = 404;
            Html(response, AdminPages.Login(_ctx, null));
        }

        private static bool IsPost(HttpRequest request)
        {
            return string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase);
        }
    }
}
