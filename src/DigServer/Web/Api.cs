using System;
using System.Globalization;
using Dig.Core;
using Dig.Store;

namespace Dig.Web
{
    /// <summary>Public JSON API. Stable, documented in docs/API.md.</summary>
    public static class Api
    {
        public static string Search(ServerContext ctx, SearchResult result, int perPage)
        {
            JsonWriter w = new JsonWriter();
            w.BeginObject();
            w.Prop("query", result.Query);
            w.Prop("page", result.Page);
            w.Prop("pages", result.Pages);
            w.Prop("perPage", perPage);
            w.Prop("total", result.Total);
            w.Prop("tookMs", result.TookMs);
            w.Prop("sort", result.Sort);
            w.Key("results").BeginArray();
            for (int i = 0; i < result.Items.Count; i++)
            {
                Torrent t = result.Items[i];
                w.BeginObject();
                w.Prop("hash", t.Hex);
                w.Prop("name", t.Name);
                w.Prop("size", t.Size);
                w.Prop("fileCount", t.FileCount);
                w.Prop("seeders", t.Seeders);
                w.Prop("leechers", t.Leechers);
                w.Prop("firstSeen", t.FirstSeen);
                w.Prop("lastSeen", t.LastSeen);
                w.Prop("magnet", t.Magnet());
                w.Prop("url", ctx.Cfg.Url("/t/" + t.Hex));
                w.EndObject();
            }
            w.EndArray();
            w.EndObject();
            return w.ToString();
        }

        public static string Torrent(ServerContext ctx, Torrent t)
        {
            JsonWriter w = new JsonWriter();
            w.BeginObject();
            w.Prop("hash", t.Hex);
            w.Prop("name", t.Name);
            w.Prop("size", t.Size);
            w.Prop("fileCount", t.FileCount);
            w.Prop("seeders", t.Seeders);
            w.Prop("leechers", t.Leechers);
            w.Prop("firstSeen", t.FirstSeen);
            w.Prop("lastSeen", t.LastSeen);
            w.Prop("hits", t.Hits);
            w.Prop("magnet", t.Magnet());
            w.Prop("url", ctx.Cfg.Url("/t/" + t.Hex));
            w.Key("files").BeginArray();
            if (t.Files != null)
            {
                for (int i = 0; i < t.Files.Count; i++)
                {
                    w.BeginObject();
                    w.Prop("path", t.Files[i].Path);
                    w.Prop("size", t.Files[i].Size);
                    w.EndObject();
                }
            }
            w.EndArray();
            w.EndObject();
            return w.ToString();
        }

        public static string Stats(ServerContext ctx)
        {
            JsonWriter w = new JsonWriter();
            w.BeginObject();
            w.Prop("ok", true);
            w.Prop("version", ctx.Version);
            w.Prop("uptimeSec", (long)(DateTime.UtcNow - ctx.StartedUtc).TotalSeconds);
            w.Prop("torrents", ctx.Store.Count);
            w.Prop("bytes", ctx.Store.TotalBytes);
            w.Prop("addedToday", ctx.Store.AddedToday);
            w.Prop("requests", ctx.RequestsServed);

            w.Key("dht").BeginObject();
            w.Prop("enabled", ctx.Cfg.DhtEnabled);
            w.Prop("port", ctx.Dht != null ? ctx.Dht.Port : 0);
            w.Prop("nodes", ctx.Dht != null ? ctx.Dht.Nodes : 0);
            w.Prop("queries", ctx.Dht != null ? ctx.Dht.QueriesSent : 0);
            w.Prop("responses", ctx.Dht != null ? ctx.Dht.Responses : 0);
            w.Prop("queriesIn", ctx.Dht != null ? ctx.Dht.QueriesIn : 0);
            w.Prop("self", ctx.Dht != null ? ctx.Dht.SelfHex : "");
            w.EndObject();

            w.Key("crawler").BeginObject();
            w.Prop("paused", ctx.Crawler != null && ctx.Crawler.Paused);
            w.Prop("harvested", ctx.Crawler != null ? ctx.Crawler.Harvested : 0);
            w.Prop("announces", ctx.Crawler != null ? ctx.Crawler.Announces : 0);
            w.Prop("dropped", ctx.Crawler != null ? ctx.Crawler.Dropped : 0);
            w.Prop("fetched", ctx.Crawler != null ? ctx.Crawler.Fetched : 0);
            w.Prop("failed", ctx.Crawler != null ? ctx.Crawler.Failed : 0);
            w.Prop("blocked", ctx.Crawler != null ? ctx.Crawler.BlockedCount : 0);
            w.Prop("queue", ctx.Crawler != null ? ctx.Crawler.Fetcher.Queued : 0);
            w.Prop("active", ctx.Crawler != null ? ctx.Crawler.Fetcher.Active : 0);
            w.EndObject();

            w.Key("index").BeginObject();
            w.Prop("terms", ctx.Index.Terms);
            w.Prop("documents", ctx.Index.Documents);
            w.EndObject();

            w.EndObject();
            return w.ToString();
        }

        public static string Health(ServerContext ctx)
        {
            JsonWriter w = new JsonWriter();
            w.BeginObject();
            w.Prop("ok", !ctx.ShuttingDown);
            w.Prop("uptimeSec", (long)(DateTime.UtcNow - ctx.StartedUtc).TotalSeconds);
            w.Prop("torrents", ctx.Store.Count);
            w.Prop("nodes", ctx.Dht != null ? ctx.Dht.Nodes : 0);
            w.EndObject();
            return w.ToString();
        }

        public static string Ok(string message)
        {
            JsonWriter w = new JsonWriter();
            w.BeginObject();
            w.Prop("ok", true);
            w.Prop("message", message ?? "ok");
            w.EndObject();
            return w.ToString();
        }

        public static string Error(string message)
        {
            JsonWriter w = new JsonWriter();
            w.BeginObject();
            w.Prop("ok", false);
            w.Prop("error", message ?? "error");
            w.EndObject();
            return w.ToString();
        }
    }
}
