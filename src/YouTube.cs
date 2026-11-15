using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ConsoleDesktop;

/// <summary>The Video tab (BRIDGE.md "YouTube in Video"): search and stream urls through yt-dlp, no API key, no ads.
/// Streams are WebM (VP9 video up to 1080p + Opus audio) as two direct googlevideo urls the page plays in a video and an audio element:
/// Chromium seeks WebM through its Cues, while YouTube's DASH MP4 makes it read the whole file up to the seek point.
/// ponytail: one yt-dlp process per call (~2.5 s search, ~3.5 s a stream); a warm yt-dlp or the InnerTube API if that ever feels slow.</summary>
static class YouTube
{
    static string? Exe => Cfg.Tool("yt-dlp");   // config.json "tools.yt-dlp", app\extras, or PATH
    static string? Node => Cfg.Tool("node");     // YouTube's JS challenges; deno works too
    static string? Deno => Cfg.Tool("deno");
    const string Format = "bv[ext=webm][height<=1080]+ba[ext=webm]/bv[height<=1080]+ba/b";
    static readonly Regex VideoId = new(@"^[A-Za-z0-9_-]{11}$"), ListId = new(@"^[A-Za-z0-9_-]{10,64}$");
    static readonly ConcurrentDictionary<string, (DateTime at, Task<object> job)> streams = new();   // by video id; googlevideo urls live ~6 h, kept 1 h

    /// <summary>A page call: { op: "search", q } -> "yt-search", { op: "stream", id } -> "yt-stream", answered through reply (the asking page).
    /// Fields are read here, before the caller's JsonDocument is disposed.</summary>
    public static void Do(JsonElement m, Action<string, object> reply)
    {
        var op = m.GetProperty("op").GetString();
        string Opt(string k) => m.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        if (op == "search")
        {
            var q = (m.GetProperty("q").GetString() ?? "").Trim();
            string sort = Opt("sort"), kind = Opt("kind"), len = Opt("len");
            if (q.Length is > 0 and < 200) Task.Run(() => reply("yt-search", Search(q, sort, kind, len)));
        }
        else if (op == "list")
        {
            string kind = Opt("kind"), id = Opt("id"), sort = Opt("sort");
            if (kind is "channel" or "playlist" or "mix" && ListId.IsMatch(id)) Task.Run(() => reply("yt-list", List(kind, id, 30, sort)));
        }
        else if (op == "feed")
        {
            var ids = m.TryGetProperty("ids", out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.StartsWith("UC") && ListId.IsMatch(x)).Distinct().Take(100).ToArray() : [];
            Task.Run(async () => reply("yt-feed", await Feed(ids)));
        }
        else if (op == "pop") PopOut.Open(m.GetRawText());   // on the UI thread (Host.FromPage): the floating player window
        else if (op == "stream")
        {
            var id = m.GetProperty("id").GetString() ?? "";
            if (VideoId.IsMatch(id)) Stream(id).ContinueWith(t => reply("yt-stream", t.Result));
        }
    }

    /// <summary>24 results. sort: "" (relevance), "date", "views", "rating"; kind: "" (everything), "video", "channel", "playlist"; len: "", "short" (under 4 min),
    /// "medium" (4-20), "long" (over 20). Any of them set: YouTube's own results page with its sp filter (a small protobuf, base64), else ytsearch24.</summary>
    public static object Search(string q, string sort = "", string kind = "", string len = "")
    {
        var sw = Stopwatch.StartNew();
        int s = sort switch { "rating" => 1, "date" => 2, "views" => 3, _ => 0 }, k = kind switch { "video" => 1, "channel" => 2, "playlist" => 3, _ => 0 }, d = len switch { "short" => 1, "long" => 2, "medium" => 3, _ => 0 };
        var f = new List<byte>(); if (k > 0) f.AddRange([0x10, (byte)k]); if (d > 0) f.AddRange([0x18, (byte)d]);
        var sp = new List<byte>(); if (s > 0) sp.AddRange([0x08, (byte)s]); if (f.Count > 0) { sp.AddRange([0x12, (byte)f.Count]); sp.AddRange(f); }
        var (doc, err) = sp.Count == 0 ? Run("--flat-playlist", "-J", "ytsearch24:" + q)
            : Run("--flat-playlist", "-J", "--playlist-end", "24", $"https://www.youtube.com/results?search_query={Uri.EscapeDataString(q)}&sp={Uri.EscapeDataString(Convert.ToBase64String(sp.ToArray()))}");
        if (doc == null) return new { q, sort, kind, len, items = Array.Empty<object>(), error = err };
        using (doc)
        {
            var items = Entries(doc.RootElement, null, null);
            Log.Write($"youtube: search \"{q}\" {sort}/{kind}/{len} -> {items.Count} in {sw.ElapsedMilliseconds} ms");
            return new { q, sort, kind, len, items };
        }
    }

    /// <summary>The entries of a flat yt-dlp list as the page's tiles: videos, and (in searches) channels and playlists with a kind.
    /// A channel's own list names its channel only once, at the top: chan/chanId fill it in.</summary>
    static List<object> Entries(JsonElement root, string? chan, string? chanId)
    {
        var items = new List<object>();
        if (!root.TryGetProperty("entries", out var es) || es.ValueKind != JsonValueKind.Array) return items;
        foreach (var e in es.EnumerateArray())
        {
            var id = Str(e, "id"); if (id == null) continue;
            string? thumb = null;   // the biggest one listed (hq720, 16:9); mqdefault is 16:9 without bars too
            if (e.TryGetProperty("thumbnails", out var ts) && ts.ValueKind == JsonValueKind.Array && ts.GetArrayLength() > 0) thumb = Str(ts[ts.GetArrayLength() - 1], "url");
            if (thumb?.StartsWith("//") == true) thumb = "https:" + thumb;   // channel avatars come without a scheme
            if (Str(e, "ie_key") == "YoutubeTab")
            {
                if (!ListId.IsMatch(id)) continue;
                if (id.StartsWith("UC")) items.Add(new { kind = "channel", id, title = Str(e, "channel") ?? Str(e, "title") ?? "", followers = Num(e, "channel_follower_count"), thumb, desc = Str(e, "description") ?? "", handle = Str(e, "uploader_id") });
                else items.Add(new { kind = "playlist", id, title = Str(e, "title") ?? "", channel = Str(e, "channel") ?? Str(e, "uploader") ?? "", channelId = Str(e, "channel_id"), count = Num(e, "playlist_count"), thumb });
                continue;
            }
            if (!VideoId.IsMatch(id)) continue;
            items.Add(new
            {
                id, title = Str(e, "title") ?? "", channel = Str(e, "channel") ?? Str(e, "uploader") ?? chan ?? "", channelId = Str(e, "channel_id") ?? chanId,
                dur = Num(e, "duration"), views = Num(e, "view_count"), live = Str(e, "live_status") == "is_live", published = Num(e, "timestamp"),
                thumb = thumb ?? $"https://i.ytimg.com/vi/{id}/mqdefault.jpg", desc = Str(e, "description") ?? "",
            });
        }
        return items;
    }

    /// <summary>A list for a column: a channel's newest uploads (30; dates are YouTube's "3 days ago" made into a time, yt-dlp's approximate_date), a YouTube
    /// playlist (first 100), or a video's mix (YouTube's radio list for it: the player's "Up next"). Answer: { kind, id, sort, title, channel, channelId, followers, items, error? }.
    /// A channel's sort: "" newest, "popular" (YouTube's own UULP list, most viewed first), "oldest" (all of UULF, the long-form uploads, reversed),
    /// "playlists", "shorts", "streams" (those tabs). ponytail: oldest reads every upload (~4 s per 500); a channel with 10k+ takes a minute.</summary>
    public static object List(string kind, string id, int n = 30, string sort = "")
    {
        var sw = Stopwatch.StartNew();
        if (kind != "channel" || !id.StartsWith("UC")) sort = "";
        var url = kind switch
        {
            "channel" => sort switch
            {
                "popular" => $"https://www.youtube.com/playlist?list=UULP{id[2..]}", "oldest" => $"https://www.youtube.com/playlist?list=UULF{id[2..]}",
                "playlists" or "shorts" or "streams" => $"https://www.youtube.com/channel/{id}/{sort}", _ => $"https://www.youtube.com/channel/{id}/videos",
            },
            "playlist" => $"https://www.youtube.com/playlist?list={id}", _ => $"https://www.youtube.com/watch?v={id}&list=RD{id}",
        };
        var (doc, err) = sort == "oldest" ? Run("--flat-playlist", "-J", url)
            : Run("--flat-playlist", "-J", "--playlist-end", kind == "playlist" ? "100" : n.ToString(), "--extractor-args", "youtubetab:approximate_date", url);
        if (doc == null) return new { kind, id, sort, items = Array.Empty<object>(), error = err };
        using (doc)
        {
            var r = doc.RootElement;
            string? chan = Str(r, "channel") ?? Str(r, "uploader"), chanId = Str(r, "channel_id") ?? (kind == "channel" ? id : null);
            var items = Entries(r, kind == "channel" ? chan : null, kind == "channel" ? chanId : null);
            if (sort == "oldest") { items.Reverse(); items = items.Take(n).ToList(); }
            Log.Write($"youtube: {kind} {id} {sort} -> {items.Count} in {sw.ElapsedMilliseconds} ms");
            return new { kind, id, sort, title = kind == "mix" ? "Up next" : (Str(r, "title") ?? "").Replace(" - Videos", ""), channel = chan, channelId = chanId, followers = Num(r, "channel_follower_count"), items };
        }
    }

    /// <summary>The Favorites feed: each favourite channel's newest 12 uploads, newest first across all of them. YouTube's channel RSS answers 404 now (Nov 2026),
    /// so it is one yt-dlp per channel, four at a time (about 2 s each). ponytail: 40 channels = 20 s; one yt-dlp with all the urls if that gets slow.</summary>
    public static async Task<object> Feed(string[] ids)
    {
        var sw = Stopwatch.StartNew();
        using var gate = new SemaphoreSlim(4);
        var all = await Task.WhenAll(ids.Select(async id => { await gate.WaitAsync(); try { return await Task.Run(() => JsonSerializer.SerializeToElement(List("channel", id, 12))); } finally { gate.Release(); } }));
        var items = all.SelectMany(x => x.TryGetProperty("items", out var it) ? it.EnumerateArray() : Enumerable.Empty<JsonElement>())
            .OrderByDescending(x => x.TryGetProperty("published", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetDouble() : 0).Take(150).ToList();
        Log.Write($"youtube: feed of {ids.Length} channels -> {items.Count} in {sw.ElapsedMilliseconds} ms");
        return new { ids, items, failed = all.Count(x => x.TryGetProperty("error", out _)) };
    }

    /// <summary>The stream urls for one video, cached, so a prefetch (the card) makes the later Play instant.</summary>
    public static Task<object> Stream(string id)
    {
        if (streams.TryGetValue(id, out var c) && DateTime.UtcNow - c.at < TimeSpan.FromHours(1) && !c.job.IsFaulted) return c.job;
        var job = Task.Run(() => Resolve(id));
        streams[id] = (DateTime.UtcNow, job);
        foreach (var k in streams.Where(x => DateTime.UtcNow - x.Value.at > TimeSpan.FromHours(1)).Select(x => x.Key).ToList()) streams.TryRemove(k, out _);
        return job;
    }

    static object Resolve(string id)
    {
        var sw = Stopwatch.StartNew();
        var (doc, err) = Run("-J", "--no-playlist", "-f", Format, "https://www.youtube.com/watch?v=" + id);
        if (doc == null) { streams.TryRemove(id, out _); return new { id, error = err }; }
        using (doc)
        {
            var r = doc.RootElement;
            string? video = Str(r, "url"), audio = null;
            if (r.TryGetProperty("requested_formats", out var rf) && rf.GetArrayLength() == 2) { video = Str(rf[0], "url"); audio = Str(rf[1], "url"); }
            if (video == null) { streams.TryRemove(id, out _); return new { id, error = "YouTube gave no playable stream" }; }
            Log.Write($"youtube: stream {id} {Num(r, "height")}p{Num(r, "fps")} {Str(r, "vcodec")}+{Str(r, "acodec")} in {sw.ElapsedMilliseconds} ms");
            var desc = Str(r, "description") ?? "";
            return new { id, video, audio, height = Num(r, "height"), fps = Num(r, "fps"), title = Str(r, "title"), channel = Str(r, "channel"), dur = Num(r, "duration"), live = r.TryGetProperty("is_live", out var l) && l.ValueKind == JsonValueKind.True,
                channelId = Str(r, "channel_id"), followers = Num(r, "channel_follower_count"), views = Num(r, "view_count"), likes = Num(r, "like_count"), published = Num(r, "timestamp"),
                desc = desc.Length > 5000 ? desc[..5000] + "…" : desc };   // the watch page's info
        }
    }

    /// <summary>yt-dlp with its JSON on stdout. --ignore-config: the user's yt-dlp.conf is for downloads (save folder, names, tags).
    /// The proxy variables are dropped: a Claude session's HTTPS_PROXY relay is not for YouTube, and the urls are bound to the asking IP.</summary>
    static (JsonDocument? doc, string? err) Run(params string[] args)
    {
        if (Exe is not { } exe) return (null, "yt-dlp is missing · Setup can fetch it");
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var k in new[] { "HTTPS_PROXY", "HTTP_PROXY", "ALL_PROXY", "https_proxy", "http_proxy", "all_proxy" }) psi.Environment.Remove(k);
        psi.ArgumentList.Add("--ignore-config"); psi.ArgumentList.Add("--no-warnings");
        if (Node is { } node) { psi.ArgumentList.Add("--js-runtimes"); psi.ArgumentList.Add("node:" + node); }
        else if (Deno is { } deno) { psi.ArgumentList.Add("--js-runtimes"); psi.ArgumentList.Add("deno:" + deno); }   // YouTube's JS challenges (stream urls)
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi)!;
            var errTask = p.StandardError.ReadToEndAsync();
            var outTask = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(40000)) { try { p.Kill(true); } catch { } return (null, "YouTube took too long"); }
            var o = outTask.Result;
            if (string.IsNullOrWhiteSpace(o))
            {
                var e = errTask.Result.Split('\n').Select(x => x.Trim()).LastOrDefault(x => x.StartsWith("ERROR:")) ?? "yt-dlp failed";
                Log.Write("youtube: " + e);
                return (null, Regex.Replace(e, @"^ERROR:\s*(\[[^\]]+\]\s*)?([\w-]{11}:\s*)?", ""));
            }
            return (JsonDocument.Parse(o), null);
        }
        catch (Exception e) { Log.Write("youtube: " + e.Message); return (null, e.Message); }
    }

    static string? Str(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    static double? Num(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    /// <summary>--youtube "query": a search, then the first result's stream, written to logs/youtube-test.json (host test without the desktop).</summary>
    public static int Test(string q)
    {
        var s = Search(q);
        var first = JsonSerializer.SerializeToElement(s).GetProperty("items").EnumerateArray().FirstOrDefault();
        object? st = first.ValueKind == JsonValueKind.Object ? Stream(first.GetProperty("id").GetString()!).Result : null;
        var cid = first.ValueKind == JsonValueKind.Object && first.TryGetProperty("channelId", out var ci) ? ci.GetString() : null;
        object? chan = cid != null ? List("channel", cid) : null, mix = first.ValueKind == JsonValueKind.Object ? List("mix", first.GetProperty("id").GetString()!) : null;
        object? feed = cid != null ? Feed([cid]).Result : null, sorted = Search(q, "views", "", "long"), channels = Search(q, "", "channel");
        File.WriteAllText(Path.Combine(Paths.Logs, "youtube-test.json"), JsonSerializer.Serialize(new { search = s, stream = st, chan, mix, feed, sorted, channels }, new JsonSerializerOptions { WriteIndented = true }));
        return st != null && JsonSerializer.SerializeToElement(st).TryGetProperty("video", out _) ? 0 : 1;
    }
}
