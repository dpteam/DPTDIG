using System;
using System.Globalization;
using System.Text;
using Dig.Core;

namespace Dig.Web
{
    /// <summary>Standalone admin pages (no public chrome, no shared CSS variables).</summary>
    public static class AdminPages
    {
        private static string H(string value) { return Text.HtmlEscape(value ?? ""); }

        private static string Shell(ServerContext ctx, string title, string body, bool withJs)
        {
            StringBuilder sb = new StringBuilder(8192);
            sb.Append("<!doctype html>\n<html lang=\"en\" data-theme=\"").Append(H(ctx.Cfg.DefaultTheme)).Append("\">\n<head>\n");
            sb.Append("<meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
            sb.Append("<meta name=\"robots\" content=\"noindex, nofollow\">\n");
            sb.Append("<title>").Append(H(title)).Append(" \u2014 ").Append(H(ctx.Cfg.SiteName)).Append(" admin</title>\n");
            sb.Append("<link rel=\"icon\" href=\"").Append(H(ctx.Url("/favicon.svg"))).Append("\" type=\"image/svg+xml\">\n");
            sb.Append("<link rel=\"stylesheet\" href=\"").Append(H(ctx.Url("/assets/css/app.css"))).Append("\">\n");
            sb.Append("<script src=\"").Append(H(ctx.Url("/assets/js/theme.js"))).Append("\"></script>\n");
            sb.Append("</head>\n<body class=\"admin-body\">\n");
            sb.Append("<header class=\"admin-bar\"><span class=\"logo\">").Append(H(ctx.Cfg.SiteShort)).Append("</span>")
              .Append("<b>").Append(H(ctx.Cfg.SiteName)).Append("</b><span class=\"admin-tag\">admin</span>");
            sb.Append("<span class=\"spacer\"></span>");
            sb.Append("<button class=\"icon-btn\" id=\"theme-toggle\" type=\"button\">\u25d1</button>");
            sb.Append("<a class=\"lang\" href=\"").Append(H(ctx.Url("/logout"))).Append("\">logout</a></header>\n");
            sb.Append("<main class=\"admin-wrap\">\n").Append(body).Append("</main>\n");
            if (withJs) sb.Append("<script src=\"").Append(H(ctx.Url("/assets/js/admin.js"))).Append("\" defer></script>\n");
            sb.Append("</body>\n</html>\n");
            return sb.ToString();
        }

        public static string Login(ServerContext ctx, string error)
        {
            StringBuilder body = new StringBuilder(2048);
            body.Append("<section class=\"admin-login\">\n");
            body.Append("<h1>").Append(H(ctx.Cfg.SiteName)).Append(" admin</h1>\n");
            body.Append("<p class=\"muted\">Paste the token from <code>config.ini</code> &rarr; <code>[security] adminToken</code>.</p>\n");
            if (!string.IsNullOrEmpty(error))
                body.Append("<div class=\"alert\">").Append(H(error)).Append("</div>\n");
            body.Append("<form method=\"post\" action=\"").Append(H(ctx.Url("/login"))).Append("\">\n");
            body.Append("<input type=\"password\" name=\"token\" placeholder=\"admin token\" autofocus autocomplete=\"off\" required>\n");
            body.Append("<button type=\"submit\">Sign in</button>\n</form>\n");
            body.Append("<p class=\"muted small\">The board is bound to ").Append(H(ctx.Cfg.AdminBind))
                .Append(':').Append(ctx.Cfg.AdminPort.ToString(CultureInfo.InvariantCulture))
                .Append(". Keep it on the LAN or behind your own proxy.</p>\n");
            body.Append("</section>\n");
            return Shell(ctx, "login", body.ToString(), false);
        }

        public static string Dashboard(ServerContext ctx)
        {
            StringBuilder body = new StringBuilder(8192);
            body.Append("<section class=\"admin-head\"><h1>Overview</h1><span class=\"dot on\" id=\"live-dot\"></span>")
                .Append("<span class=\"muted small\">updates every 3s</span></section>\n");

            body.Append("<section class=\"cards\" id=\"cards\">");
            Card(body, "Torrents", "torrents", "\u2014");
            Card(body, "DHT nodes", "nodes", "\u2014");
            Card(body, "Hashes", "harvested", "\u2014");
            Card(body, "Fetched", "fetched", "\u2014");
            body.Append("</section>\n");

            body.Append("<section class=\"panel\">\n<div class=\"panel-head\"><h2>Actions</h2></div>\n<div class=\"actions\">\n");
            body.Append("<button class=\"btn\" data-action=\"/api/compact\">Compact index</button>");
            body.Append("<button class=\"btn\" data-action=\"/api/crawl?action=toggle\">Pause / resume crawler</button>");
            body.Append("<button class=\"btn\" data-action=\"/api/reload\">Reload blocklist</button>");
            body.Append("<a class=\"btn\" href=\"").Append(H(ctx.Url("/api/config"))).Append("\" target=\"_blank\">Config (JSON)</a>");
            body.Append("</div>\n<p class=\"muted small\" id=\"action-result\"></p>\n</section>\n");

            body.Append("<section class=\"panel\">\n<div class=\"panel-head\"><h2>Index</h2></div>\n<table class=\"kv\" id=\"index-table\">\n");
            body.Append("<tr><th>Data dir</th><td class=\"mono\">").Append(H(ctx.Cfg.DataDir)).Append("</td></tr>\n");
            body.Append("<tr><th>Index file</th><td class=\"mono\">").Append(H(ctx.Store.FilePath)).Append("</td></tr>\n");
            body.Append("<tr><th>Max torrents</th><td class=\"mono\">").Append(ctx.Cfg.MaxTorrents.ToString(CultureInfo.InvariantCulture)).Append("</td></tr>\n");
            body.Append("<tr><th>UDP port</th><td class=\"mono\">").Append(ctx.Cfg.UdpPort.ToString(CultureInfo.InvariantCulture)).Append("</td></tr>\n");
            body.Append("<tr><th>Node id</th><td class=\"mono\">").Append(ctx.Dht != null ? H(ctx.Dht.SelfHex) : "\u2014").Append("</td></tr>\n");
            body.Append("</table>\n</section>\n");

            body.Append("<section class=\"panel\">\n<div class=\"panel-head\"><h2>Log</h2>")
                .Append("<button class=\"icon-btn\" id=\"log-clear\" type=\"button\" title=\"clear\">\u2715</button></div>\n");
            body.Append("<pre class=\"log\" id=\"log\"></pre>\n</section>\n");

            return Shell(ctx, "admin", body.ToString(), true);
        }

        private static void Card(StringBuilder sb, string label, string id, string value)
        {
            sb.Append("<div class=\"card\"><span class=\"card-label\">").Append(H(label)).Append("</span>")
              .Append("<b class=\"card-value\" id=\"v-").Append(H(id)).Append("\">").Append(H(value)).Append("</b></div>");
        }

        public static string ConfigJson(ServerContext ctx)
        {
            Config cfg = ctx.Cfg;
            JsonWriter w = new JsonWriter();
            w.BeginObject();
            w.Prop("ok", true);

            w.Key("site").BeginObject();
            w.Prop("name", cfg.SiteName);
            w.Prop("baseUrl", cfg.BaseUrl);
            w.Prop("defaultLanguage", cfg.DefaultLanguage);
            w.Prop("defaultTheme", cfg.DefaultTheme);
            w.Prop("noIndex", cfg.NoIndex);
            w.EndObject();

            w.Key("server").BeginObject();
            w.Prop("port", cfg.Port);
            w.Prop("bind", cfg.Bind);
            w.Prop("adminPort", cfg.AdminPort);
            w.Prop("adminBind", cfg.AdminBind);
            w.Prop("publicBasePath", cfg.PublicBasePath);
            w.Prop("requestsPerMinute", cfg.RequestsPerMinute);
            w.EndObject();

            w.Key("dht").BeginObject();
            w.Prop("enabled", cfg.DhtEnabled);
            w.Prop("udpPort", cfg.UdpPort);
            w.Prop("bootstrap", cfg.Bootstrap);
            w.Prop("sampleEnabled", cfg.SampleEnabled);
            w.Prop("maxInfohashesPerMin", cfg.MaxInfohashesPerMin);
            w.EndObject();

            w.Key("metadata").BeginObject();
            w.Prop("enabled", cfg.MetadataEnabled);
            w.Prop("concurrency", cfg.MetadataConcurrency);
            w.Prop("maxMetadataBytes", cfg.MaxMetadataBytes);
            w.EndObject();

            w.Key("index").BeginObject();
            w.Prop("dataDir", cfg.DataDir);
            w.Prop("maxTorrents", cfg.MaxTorrents);
            w.Prop("snapshotIntervalMin", cfg.SnapshotIntervalMin);
            w.Prop("purgeDays", cfg.PurgeDays);
            w.Prop("blocklistFile", cfg.BlocklistFile);
            w.EndObject();

            w.Key("logging").BeginObject();
            w.Prop("level", cfg.LogLevel.ToString());
            w.Prop("dhtTrace", cfg.DhtTrace);
            w.Prop("apiTrace", cfg.ApiTrace);
            w.EndObject();

            w.Key("security").BeginObject();
            w.Prop("adminTokenSet", cfg.AdminToken.Length > 0);
            w.Prop("allowQueryToken", cfg.AllowQueryToken);
            w.EndObject();

            w.EndObject();
            return w.ToString();
        }
    }
}
