using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Dig.Core;
using Dig.Store;

namespace Dig.Web
{
    /// <summary>
    /// Server-rendered pages. Search engines need real HTML, so the result list is
    /// rendered on the server and JavaScript only adds the small things (theme,
    /// copy buttons, live counters).
    /// </summary>
    public static class Pages
    {
        private static string H(string value) { return Text.HtmlEscape(value ?? ""); }

        private static string U(ServerContext ctx, string path) { return H(ctx.Url(path)); }

        // ------------------------------------------------------------------- shell

        public static string Layout(ServerContext ctx, Lang lang, string title, string description,
                                    string canonicalPath, string body, string extraHead)
        {
            Config cfg = ctx.Cfg;
            StringBuilder sb = new StringBuilder(16384);
            string fullTitle = string.IsNullOrEmpty(title) ? cfg.SiteName : title + " \u2014 " + cfg.SiteName;

            sb.Append("<!doctype html>\n<html lang=\"").Append(H(lang.Code)).Append("\" data-theme=\"")
              .Append(H(cfg.DefaultTheme)).Append("\">\n<head>\n");
            sb.Append("<meta charset=\"utf-8\">\n");
            sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
            sb.Append("<title>").Append(H(fullTitle)).Append("</title>\n");
            sb.Append("<meta name=\"description\" content=\"").Append(H(description ?? cfg.SiteDescription)).Append("\">\n");
            if (cfg.NoIndex) sb.Append("<meta name=\"robots\" content=\"noindex, nofollow\">\n");
            sb.Append("<link rel=\"canonical\" href=\"").Append(H(ctx.AbsoluteUrl(canonicalPath ?? "/"))).Append("\">\n");
            sb.Append("<link rel=\"icon\" href=\"").Append(H(Favicon(ctx))).Append("\" type=\"image/svg+xml\">\n");
            sb.Append("<link rel=\"manifest\" href=\"").Append(U(ctx, "/manifest.webmanifest")).Append("\">\n");
            sb.Append("<meta name=\"theme-color\" content=\"#101418\">\n");
            sb.Append("<meta name=\"dig-api\" content=\"").Append(U(ctx, "/api/")).Append("\">\n");
            sb.Append("<meta property=\"og:site_name\" content=\"").Append(H(cfg.SiteName)).Append("\">\n");
            sb.Append("<meta property=\"og:title\" content=\"").Append(H(fullTitle)).Append("\">\n");
            sb.Append("<meta property=\"og:description\" content=\"").Append(H(description ?? cfg.SiteDescription)).Append("\">\n");
            sb.Append("<meta property=\"og:type\" content=\"website\">\n");
            sb.Append("<meta property=\"og:url\" content=\"").Append(H(ctx.AbsoluteUrl(canonicalPath ?? "/"))).Append("\">\n");
            sb.Append("<link rel=\"stylesheet\" href=\"").Append(U(ctx, "/assets/css/app.css")).Append("\">\n");
            sb.Append("<link rel=\"stylesheet\" href=\"").Append(U(ctx, "/brand.css")).Append("\">\n");
            sb.Append("<script src=\"").Append(U(ctx, "/assets/js/theme.js")).Append("\"></script>\n");
            if (!string.IsNullOrEmpty(extraHead)) sb.Append(extraHead);

            sb.Append("</head>\n<body>\n");
            sb.Append(Header(ctx, lang));
            sb.Append("<main class=\"wrap\">\n");
            if (!string.IsNullOrEmpty(cfg.Motd))
                sb.Append("<div class=\"motd\">").Append(H(cfg.Motd)).Append("</div>\n");
            sb.Append(body);
            sb.Append("</main>\n");
            sb.Append(Footer(ctx, lang));
            sb.Append("<script src=\"").Append(U(ctx, "/assets/js/app.js")).Append("\" defer></script>\n");
            sb.Append("</body>\n</html>\n");
            return sb.ToString();
        }

        private static string Favicon(ServerContext ctx)
        {
            return string.IsNullOrEmpty(ctx.Cfg.BrandFaviconUrl) ? ctx.Url("/favicon.svg") : ctx.Cfg.BrandFaviconUrl;
        }

        private static string Header(ServerContext ctx, Lang lang)
        {
            Config cfg = ctx.Cfg;
            StringBuilder sb = new StringBuilder(2048);
            sb.Append("<header class=\"topbar\">\n<div class=\"topbar-inner\">\n");

            sb.Append("<a class=\"brand\" href=\"").Append(U(ctx, "/")).Append("\">");
            if (!string.IsNullOrEmpty(cfg.BrandLogoUrl))
                sb.Append("<img class=\"logo-img\" src=\"").Append(H(cfg.BrandLogoUrl)).Append("\" alt=\"").Append(H(cfg.SiteName)).Append("\">");
            else
                sb.Append("<span class=\"logo\">").Append(H(cfg.SiteShort)).Append("</span>");
            sb.Append("<span class=\"brand-text\"><b>").Append(H(cfg.SiteName)).Append("</b><small>")
              .Append(H(string.IsNullOrEmpty(cfg.BrandLogoNote) ? cfg.SiteTagline : cfg.BrandLogoNote))
              .Append("</small></span></a>\n");

            sb.Append("<form class=\"topsearch\" action=\"").Append(U(ctx, "/search")).Append("\" method=\"get\" role=\"search\">")
              .Append("<input type=\"search\" name=\"q\" placeholder=\"").Append(H(lang.T("search_placeholder")))
              .Append("\" aria-label=\"").Append(H(lang.T("search"))).Append("\" autocomplete=\"off\" maxlength=\"")
              .Append(cfg.MaxQueryLength.ToString(CultureInfo.InvariantCulture)).Append("\">")
              .Append("<button type=\"submit\" aria-label=\"").Append(H(lang.T("search"))).Append("\">\u2192</button></form>\n");

            sb.Append("<nav class=\"topnav\">");
            sb.Append("<a href=\"").Append(U(ctx, "/search?sort=new")).Append("\">").Append(H(lang.T("latest"))).Append("</a>");
            sb.Append("<a href=\"").Append(U(ctx, "/stats")).Append("\">").Append(H(lang.T("stats"))).Append("</a>");
            sb.Append("<a href=\"").Append(U(ctx, "/about")).Append("\">").Append(H(lang.T("about"))).Append("</a>");
            sb.Append("<button class=\"icon-btn\" id=\"theme-toggle\" type=\"button\" title=\"")
              .Append(H(lang.T("theme"))).Append("\" aria-label=\"").Append(H(lang.T("theme"))).Append("\">\u25d1</button>");
            string other = lang.Code == "ru" ? "en" : "ru";
            sb.Append("<a class=\"lang\" href=\"?lang=").Append(other).Append("\" title=\"").Append(H(lang.T("language")))
              .Append("\">").Append(other.ToUpperInvariant()).Append("</a>");
            sb.Append("</nav>\n</div>\n</header>\n");
            return sb.ToString();
        }

        private static string Footer(ServerContext ctx, Lang lang)
        {
            Config cfg = ctx.Cfg;
            StringBuilder sb = new StringBuilder(1024);
            sb.Append("<footer class=\"foot\">\n<div class=\"wrap foot-inner\">\n");
            sb.Append("<div class=\"foot-brand\"><b>").Append(H(cfg.SiteName)).Append("</b> \u2014 ")
              .Append(H(cfg.SiteTagline)).Append("</div>\n");
            sb.Append("<div class=\"foot-links\">");
            sb.Append("<a href=\"").Append(U(ctx, "/about")).Append("\">").Append(H(lang.T("about"))).Append("</a>");
            sb.Append("<a href=\"").Append(U(ctx, "/abuse")).Append("\">").Append(H(lang.T("abuse_title"))).Append("</a>");
            sb.Append("<a href=\"").Append(U(ctx, "/api/stats")).Append("\">").Append(H(lang.T("api"))).Append("</a>");
            sb.Append("</div>\n");
            if (!string.IsNullOrEmpty(cfg.Footer)) sb.Append("<div class=\"foot-note\">").Append(H(cfg.Footer)).Append("</div>\n");
            sb.Append("<div class=\"foot-note\">").Append(H(lang.T("search_engine_note"))).Append("</div>\n");
            sb.Append("</div>\n</footer>\n");
            return sb.ToString();
        }

        // -------------------------------------------------------------------- home

        public static string Home(ServerContext ctx, Lang lang)
        {
            Config cfg = ctx.Cfg;
            List<Torrent> latest = ctx.Store.Recent(10, 0);
            StringBuilder body = new StringBuilder(8192);

            body.Append("<section class=\"hero\">\n");
            body.Append("<h1 class=\"hero-title\">").Append(H(cfg.SiteName)).Append("</h1>\n");
            body.Append("<p class=\"hero-sub\">").Append(H(cfg.SiteTagline)).Append("</p>\n");
            body.Append("<form class=\"searchbox\" action=\"").Append(U(ctx, "/search")).Append("\" method=\"get\" role=\"search\">")
                .Append("<input type=\"search\" name=\"q\" placeholder=\"").Append(H(lang.T("search_placeholder")))
                .Append("\" aria-label=\"").Append(H(lang.T("search"))).Append("\" autofocus autocomplete=\"off\" maxlength=\"")
                .Append(cfg.MaxQueryLength.ToString(CultureInfo.InvariantCulture)).Append("\">")
                .Append("<button type=\"submit\">").Append(H(lang.T("search"))).Append("</button></form>\n");
            body.Append("<p class=\"hero-hint\">").Append(H(lang.T("search_hint"))).Append("</p>\n");
            body.Append("</section>\n");

            body.Append("<section class=\"statstrip\">");
            StatChip(body, Text.HumanCount(ctx.Store.Count), lang.T("indexed"));
            StatChip(body, ctx.Dht != null ? Text.HumanCount(ctx.Dht.Nodes) : "0", lang.T("nodes"));
            StatChip(body, ctx.Crawler != null ? Text.HumanCount(ctx.Crawler.Harvested) : "0", lang.T("hashes"));
            StatChip(body, Text.HumanSize(ctx.Store.TotalBytes), lang.T("total_size"));
            body.Append("</section>\n");

            body.Append("<section class=\"panel\">\n<div class=\"panel-head\"><h2>").Append(H(lang.T("latest")))
                .Append("</h2><a class=\"more\" href=\"").Append(U(ctx, "/search?sort=new")).Append("\">")
                .Append(H(lang.T("browse_all"))).Append("</a></div>\n");

            if (latest.Count == 0)
            {
                body.Append("<div class=\"empty\"><p>").Append(H(lang.T("no_results"))).Append("</p><p class=\"muted\">")
                    .Append(H(lang.T("no_results_hint"))).Append("</p></div>\n");
            }
            else
            {
                body.Append("<ol class=\"results\">\n");
                for (int i = 0; i < latest.Count; i++) ResultRow(body, ctx, lang, latest[i]);
                body.Append("</ol>\n");
            }
            body.Append("</section>\n");

            return Layout(ctx, lang, null, cfg.SiteDescription, "/", body.ToString(), null);
        }

        private static void StatChip(StringBuilder sb, string value, string label)
        {
            sb.Append("<div class=\"chip\"><b>").Append(H(value)).Append("</b><span>").Append(H(label)).Append("</span></div>");
        }

        // ------------------------------------------------------------------ search

        public static string Search(ServerContext ctx, Lang lang, SearchResult result, string query, string sort, int perPage)
        {
            StringBuilder body = new StringBuilder(16384);
            body.Append("<section class=\"search-head\">\n");
            body.Append("<form class=\"searchbox compact\" action=\"").Append(U(ctx, "/search")).Append("\" method=\"get\" role=\"search\">")
                .Append("<input type=\"search\" name=\"q\" value=\"").Append(H(query)).Append("\" placeholder=\"")
                .Append(H(lang.T("search_placeholder"))).Append("\" aria-label=\"").Append(H(lang.T("search")))
                .Append("\" autocomplete=\"off\" maxlength=\"").Append(ctx.Cfg.MaxQueryLength.ToString(CultureInfo.InvariantCulture)).Append("\">")
                .Append("<button type=\"submit\">").Append(H(lang.T("search"))).Append("</button></form>\n");

            body.Append("<div class=\"result-meta\">");
            if (query.Length > 0)
                body.Append("<span>").Append(H(lang.T("results_for"))).Append(" <b>").Append(H(query)).Append("</b></span>");
            body.Append("<span>").Append(Text.HumanCount(result.Total)).Append(' ').Append(H(lang.T("results_count")))
                .Append(" \u00b7 ").Append(result.TookMs.ToString(CultureInfo.InvariantCulture)).Append(" ms</span>");
            body.Append("</div>\n");

            body.Append("<div class=\"sorts\">");
            SortLink(body, ctx, lang, sort, "relevance", lang.T("sort_relevance"), query);
            SortLink(body, ctx, lang, sort, "new", lang.T("sort_new"), query);
            SortLink(body, ctx, lang, sort, "seeders", lang.T("sort_seeders"), query);
            SortLink(body, ctx, lang, sort, "size", lang.T("sort_size"), query);
            body.Append("</div>\n</section>\n");

            body.Append("<section class=\"panel results-panel\">\n");
            if (result.Items.Count == 0)
            {
                body.Append("<div class=\"empty\"><h2>").Append(H(lang.T("no_results"))).Append("</h2><p class=\"muted\">")
                    .Append(H(lang.T("no_results_hint"))).Append("</p><p><a href=\"")
                    .Append(U(ctx, "/search?sort=new")).Append("\">").Append(H(lang.T("browse_all"))).Append("</a></p></div>\n");
            }
            else
            {
                body.Append("<ol class=\"results\">\n");
                for (int i = 0; i < result.Items.Count; i++) ResultRow(body, ctx, lang, result.Items[i]);
                body.Append("</ol>\n");
                Pagination(body, ctx, lang, query, sort, perPage, result);
            }
            body.Append("</section>\n");

            string title = query.Length > 0 ? lang.T("results_for") + " " + query : lang.T("latest");
            string canonical = query.Length > 0
                ? "/search?q=" + Text.UrlEncode(query) + (sort != "relevance" ? "&sort=" + sort : "")
                : "/search?sort=" + sort;
            return Layout(ctx, lang, title, ctx.Cfg.SiteDescription, canonical, body.ToString(), null);
        }

        private static void SortLink(StringBuilder sb, ServerContext ctx, Lang lang, string current, string value, string label, string query)
        {
            sb.Append("<a class=\"sort").Append(current == value ? " active" : "").Append("\" href=\"")
              .Append(U(ctx, "/search?q=" + Text.UrlEncode(query) + "&sort=" + value)).Append("\">")
              .Append(H(label)).Append("</a>");
        }

        private static void ResultRow(StringBuilder sb, ServerContext ctx, Lang lang, Torrent t)
        {
            string magnet = t.Magnet();
            sb.Append("<li class=\"hit\">\n");
            sb.Append("<div class=\"hit-main\"><a class=\"hit-name\" href=\"").Append(U(ctx, "/t/" + t.Hex)).Append("\">")
              .Append(H(t.Name)).Append("</a>\n");
            sb.Append("<div class=\"hit-meta\">");
            sb.Append("<span class=\"size\">").Append(H(Text.HumanSize(t.Size))).Append("</span>");
            sb.Append("<span>").Append(t.FileCount.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(H(lang.T("files"))).Append("</span>");
            if (t.Seeders > 0) sb.Append("<span class=\"seeds\">").Append(t.Seeders.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(H(lang.T("seeders"))).Append("</span>");
            sb.Append("<span class=\"muted\">").Append(H(Clock.Age(t.LastSeen, lang.Code))).Append("</span>");
            sb.Append("<span class=\"hash\" title=\"").Append(H(lang.T("hash"))).Append("\">").Append(H(t.Hex.Substring(0, 8))).Append("\u2026</span>");
            sb.Append("</div></div>\n");
            sb.Append("<div class=\"hit-actions\"><button class=\"copy\" type=\"button\" data-copy=\"").Append(H(magnet))
              .Append("\" data-copied=\"").Append(H(lang.T("copied"))).Append("\" title=\"").Append(H(lang.T("copy")))
              .Append("\">").Append(H(lang.T("copy"))).Append("</button>");
            sb.Append("<a class=\"magnet\" href=\"").Append(H(magnet)).Append("\" title=\"").Append(H(lang.T("open_magnet"))).Append("\">magnet</a></div>\n");
            sb.Append("</li>\n");
        }

        private static void Pagination(StringBuilder sb, ServerContext ctx, Lang lang, string query, string sort, int perPage, SearchResult result)
        {
            if (result.Pages <= 1) return;
            sb.Append("<nav class=\"pager\">");
            if (result.Page > 1)
                sb.Append("<a href=\"").Append(U(ctx, PageUrl(query, sort, result.Page - 1, perPage))).Append("\">\u2190 ")
                  .Append(H(lang.T("prev"))).Append("</a>");
            else
                sb.Append("<span class=\"disabled\">\u2190 ").Append(H(lang.T("prev"))).Append("</span>");

            sb.Append("<span class=\"pager-info\">").Append(H(lang.T("page"))).Append(' ')
              .Append(result.Page.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(H(lang.T("of"))).Append(' ')
              .Append(result.Pages.ToString(CultureInfo.InvariantCulture)).Append("</span>");

            if (result.Page < result.Pages)
                sb.Append("<a href=\"").Append(U(ctx, PageUrl(query, sort, result.Page + 1, perPage))).Append("\">")
                  .Append(H(lang.T("next"))).Append(" \u2192</a>");
            else
                sb.Append("<span class=\"disabled\">").Append(H(lang.T("next"))).Append(" \u2192</span>");
            sb.Append("</nav>\n");
        }

        private static string PageUrl(string query, string sort, int page, int perPage)
        {
            return "/search?q=" + Text.UrlEncode(query) + "&sort=" + sort + "&page=" + page.ToString(CultureInfo.InvariantCulture) +
                   (perPage != 25 ? "&per=" + perPage.ToString(CultureInfo.InvariantCulture) : "");
        }

        // ----------------------------------------------------------------- torrent

        public static string TorrentPage(ServerContext ctx, Lang lang, Torrent t)
        {
            StringBuilder body = new StringBuilder(16384);
            string magnet = t.Magnet();

            body.Append("<section class=\"torrent-head\">\n");
            body.Append("<a class=\"crumb\" href=\"").Append(U(ctx, "/")).Append("\">\u2190 ").Append(H(lang.T("back_home"))).Append("</a>\n");
            body.Append("<h1>").Append(H(t.Name)).Append("</h1>\n");
            body.Append("<div class=\"torrent-meta\"><span>").Append(H(Text.HumanSize(t.Size))).Append("</span><span>")
                .Append(t.FileCount.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(H(lang.T("files"))).Append("</span>");
            if (t.Seeders > 0) body.Append("<span>").Append(t.Seeders.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(H(lang.T("seeders"))).Append("</span>");
            body.Append("<span>").Append(H(lang.T("added"))).Append(' ').Append(H(Clock.Age(t.FirstSeen, lang.Code))).Append("</span>");
            body.Append("<span>").Append(H(lang.T("seen"))).Append(' ').Append(H(Clock.Age(t.LastSeen, lang.Code))).Append("</span></div>\n");
            body.Append("</section>\n");

            body.Append("<section class=\"panel magnet-panel\">\n");
            body.Append("<label class=\"label\" for=\"magnet\">").Append(H(lang.T("magnet"))).Append("</label>");
            body.Append("<div class=\"magnet-row\"><input id=\"magnet\" class=\"mono\" readonly value=\"").Append(H(magnet)).Append("\">");
            body.Append("<button class=\"copy primary\" type=\"button\" data-copy=\"").Append(H(magnet)).Append("\" data-copied=\"")
                .Append(H(lang.T("copied"))).Append("\">").Append(H(lang.T("copy"))).Append("</button>");
            body.Append("<a class=\"btn\" href=\"").Append(H(magnet)).Append("\">").Append(H(lang.T("open_magnet"))).Append("</a></div>\n");
            body.Append("<div class=\"hash-row\"><span class=\"label\">").Append(H(lang.T("hash"))).Append("</span>");
            body.Append("<code class=\"mono\">").Append(H(t.Hex)).Append("</code>");
            body.Append("<button class=\"copy\" type=\"button\" data-copy=\"").Append(H(t.Hex)).Append("\" data-copied=\"")
                .Append(H(lang.T("copied"))).Append("\">").Append(H(lang.T("copy"))).Append("</button></div>\n");
            body.Append("</section>\n");

            body.Append("<section class=\"panel\">\n<div class=\"panel-head\"><h2>").Append(H(lang.T("file_list")))
                .Append("</h2><span class=\"muted\">").Append(t.FileCount.ToString(CultureInfo.InvariantCulture)).Append(' ')
                .Append(H(lang.T("files"))).Append("</span></div>\n");

            if (t.Files == null || t.Files.Count == 0)
            {
                body.Append("<p class=\"muted\">").Append(H(lang.T("unknown"))).Append("</p>\n");
            }
            else
            {
                body.Append("<table class=\"files\"><thead><tr><th>").Append(H(lang.T("path"))).Append("</th><th class=\"num\">")
                    .Append(H(lang.T("size"))).Append("</th></tr></thead><tbody>\n");
                for (int i = 0; i < t.Files.Count; i++)
                {
                    TorrentFile f = t.Files[i];
                    body.Append("<tr><td class=\"mono path\">").Append(H(f.Path)).Append("</td><td class=\"num\">")
                        .Append(H(Text.HumanSize(f.Size))).Append("</td></tr>\n");
                }
                body.Append("</tbody></table>\n");
                if (t.FileCount > t.Files.Count)
                    body.Append("<p class=\"muted small\">+ ").Append((t.FileCount - t.Files.Count).ToString(CultureInfo.InvariantCulture))
                        .Append(" \u2026</p>\n");
            }
            body.Append("</section>\n");

            string desc = t.Name + " \u2014 " + Text.HumanSize(t.Size) + ", " + t.FileCount + " files. " + ctx.Cfg.SiteDescription;
            return Layout(ctx, lang, t.Name, Text.Truncate(desc, 200), "/t/" + t.Hex, body.ToString(), null);
        }

        // ------------------------------------------------------------------- stats

        public static string StatsPage(ServerContext ctx, Lang lang)
        {
            Config cfg = ctx.Cfg;
            StringBuilder body = new StringBuilder(8192);
            body.Append("<section class=\"page-head\"><h1>").Append(H(lang.T("stats"))).Append("</h1></section>\n");

            body.Append("<section class=\"cards\">\n");
            Card(body, lang.T("indexed"), Text.HumanCount(ctx.Store.Count), Text.HumanSize(ctx.Store.TotalBytes));
            Card(body, lang.T("hashes"), ctx.Crawler != null ? Text.HumanCount(ctx.Crawler.Harvested) : "0",
                ctx.Crawler != null ? ctx.Crawler.Sampled + " samples" : "");
            Card(body, lang.T("nodes"), ctx.Dht != null ? Text.HumanCount(ctx.Dht.Nodes) : "0",
                ctx.Dht != null ? "udp/" + ctx.Dht.Port : "");
            Card(body, lang.T("requests"), Text.HumanCount(ctx.RequestsServed), lang.T("uptime") + " " + ctx.Uptime());
            body.Append("</section>\n");

            body.Append("<section class=\"panel\">\n<div class=\"panel-head\"><h2>").Append(H(lang.T("crawler"))).Append("</h2><span class=\"dot ")
                .Append(ctx.Crawler != null && !ctx.Crawler.Paused ? "on" : "off").Append("\"></span></div>\n");
            body.Append("<table class=\"kv\">\n");
            Kv(body, lang.T("status"), ctx.Crawler != null ? (ctx.Crawler.Paused ? lang.T("stopped") : lang.T("running")) : lang.T("stopped"));
            Kv(body, lang.T("queries"), ctx.Dht != null ? Text.HumanCount(ctx.Dht.QueriesSent) : "0");
            Kv(body, lang.T("answers"), ctx.Dht != null ? Text.HumanCount(ctx.Dht.Responses) : "0");
            Kv(body, lang.T("fetched"), ctx.Crawler != null ? Text.HumanCount(ctx.Crawler.Fetched) : "0");
            Kv(body, lang.T("failed"), ctx.Crawler != null ? Text.HumanCount(ctx.Crawler.Failed) : "0");
            Kv(body, lang.T("queue"), ctx.Crawler != null ? Text.HumanCount(ctx.Crawler.Fetcher.Queued + ctx.Crawler.Fetcher.Active) : "0");
            Kv(body, lang.T("dht_port"), ctx.Dht != null ? "udp/" + ctx.Dht.Port : "\u2014");
            Kv(body, "Info hash", ctx.Dht != null ? ctx.Dht.SelfHex : "\u2014");
            body.Append("</table>\n</section>\n");

            body.Append("<section class=\"panel\">\n<div class=\"panel-head\"><h2>Index</h2></div><table class=\"kv\">\n");
            Kv(body, "Data dir", cfg.DataDir);
            Kv(body, "Max torrents", cfg.MaxTorrents.ToString(CultureInfo.InvariantCulture));
            Kv(body, "Blocked hashes", ctx.Crawler != null ? ctx.Crawler.BlockedCount.ToString(CultureInfo.InvariantCulture) : "0");
            Kv(body, "Dropped", ctx.Crawler != null ? ctx.Crawler.Dropped.ToString(CultureInfo.InvariantCulture) : "0");
            Kv(body, "Version", ctx.Version);
            body.Append("</table>\n</section>\n");

            return Layout(ctx, lang, lang.T("stats"), cfg.SiteDescription, "/stats", body.ToString(), null);
        }

        private static void Card(StringBuilder sb, string label, string value, string note)
        {
            sb.Append("<div class=\"card\"><span class=\"card-label\">").Append(H(label)).Append("</span><b class=\"card-value\">")
              .Append(H(value)).Append("</b>");
            if (!string.IsNullOrEmpty(note)) sb.Append("<span class=\"card-note\">").Append(H(note)).Append("</span>");
            sb.Append("</div>");
        }

        private static void Kv(StringBuilder sb, string key, string value)
        {
            sb.Append("<tr><th>").Append(H(key)).Append("</th><td class=\"mono\">").Append(H(value)).Append("</td></tr>\n");
        }

        // ------------------------------------------------------------- about/abuse

        public static string About(ServerContext ctx, Lang lang)
        {
            Config cfg = ctx.Cfg;
            StringBuilder body = new StringBuilder(4096);
            body.Append("<section class=\"page-head\"><h1>").Append(H(lang.T("about_title"))).Append("</h1></section>\n");
            body.Append("<article class=\"prose panel\">\n");
            body.Append("<p>").Append(H(lang.T("about_body"))).Append("</p>\n");
            body.Append("<h2>").Append(H(lang.T("about_how"))).Append("</h2>\n");
            body.Append("<p>").Append(H(lang.T("about_how_body"))).Append("</p>\n");
            if (!string.IsNullOrEmpty(cfg.SupportEmail))
                body.Append("<p>").Append(H(lang.T("about"))).Append(": <a href=\"mailto:").Append(H(cfg.SupportEmail))
                    .Append("\">").Append(H(cfg.SupportEmail)).Append("</a></p>\n");
            body.Append("<p class=\"muted small\">").Append(H(cfg.SiteName)).Append(" ").Append(H(ctx.Version)).Append("</p>\n");
            body.Append("</article>\n");
            return Layout(ctx, lang, lang.T("about_title"), cfg.SiteDescription, "/about", body.ToString(), null);
        }

        public static string Abuse(ServerContext ctx, Lang lang)
        {
            Config cfg = ctx.Cfg;
            StringBuilder body = new StringBuilder(4096);
            body.Append("<section class=\"page-head\"><h1>").Append(H(lang.T("abuse_title"))).Append("</h1></section>\n");
            body.Append("<article class=\"prose panel\">\n<p>").Append(H(lang.T("abuse_body"))).Append("</p>\n");
            if (!string.IsNullOrEmpty(cfg.AbuseEmail))
                body.Append("<p><a class=\"btn\" href=\"mailto:").Append(H(cfg.AbuseEmail)).Append("\">")
                    .Append(H(cfg.AbuseEmail)).Append("</a></p>\n");
            else
                body.Append("<p class=\"muted\">").Append(H(lang.T("no_email"))).Append("</p>\n");
            body.Append("</article>\n");
            return Layout(ctx, lang, lang.T("abuse_title"), cfg.SiteDescription, "/abuse", body.ToString(), null);
        }

        // ------------------------------------------------------------------- error

        public static string ErrorPage(ServerContext ctx, Lang lang, int status, string title, string message)
        {
            StringBuilder body = new StringBuilder(2048);
            body.Append("<section class=\"error-page\"><div class=\"error-code\">").Append(status.ToString(CultureInfo.InvariantCulture))
                .Append("</div><h1>").Append(H(title)).Append("</h1><p class=\"muted\">").Append(H(message)).Append("</p>");
            body.Append("<p><a class=\"btn\" href=\"").Append(U(ctx, "/")).Append("\">").Append(H(lang.T("back_home"))).Append("</a></p></section>\n");
            return Layout(ctx, lang, title, ctx.Cfg.SiteDescription, "/", body.ToString(), null);
        }

        // ----------------------------------------------------------------- sitemap

        public static string Sitemap(ServerContext ctx)
        {
            List<Torrent> recent = ctx.Store.Recent(500, 0);
            StringBuilder sb = new StringBuilder(8192);
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
            sb.Append("<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">\n");
            sb.Append("<url><loc>").Append(H(ctx.Cfg.Url("/"))).Append("</loc></url>\n");
            sb.Append("<url><loc>").Append(H(ctx.Cfg.Url("/search?sort=new"))).Append("</loc></url>\n");
            for (int i = 0; i < recent.Count; i++)
            {
                sb.Append("<url><loc>").Append(H(ctx.Cfg.Url("/t/" + recent[i].Hex))).Append("</loc><lastmod>")
                  .Append(Clock.FromUnix(recent[i].LastSeen).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                  .Append("</lastmod></url>\n");
            }
            sb.Append("</urlset>\n");
            return sb.ToString();
        }
    }
}
