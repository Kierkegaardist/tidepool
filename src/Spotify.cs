using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ConsoleDesktop;

/// <summary>Spotify for the Music tab. Signed out it reads Spotifast's own cache (every playlist in the user's sidebar order, the songs of the
/// playlists opened in Spotifast, Liked Songs, mixes, recently played, top tracks), and plays through Spotifast's command line.
/// After one approval through the user's own Spotify app (Spotifast's web_client_id; redirect http://127.0.0.1:8989/login, the one
/// Spotifast's guide has them register) the Web API adds search, adding to playlists, liking, the queue and playing inside a playlist.
/// The refresh token is kept DPAPI-sealed in app/.spotify. Results go out as events: sp-state, sp-search, sp-list, sp-done.</summary>
sealed class Spotify
{
    static readonly string Local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"paolino\spotifast");
    static readonly string Settings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"paolino\spotifast\config\settings.json");
    static readonly string TokenFile = Path.Combine(Paths.App, ".spotify");
    const string Redirect = "http://127.0.0.1:8989/login", Api = "https://api.spotify.com/v1";
    const string Scopes = "playlist-read-private playlist-read-collaborative playlist-modify-private playlist-modify-public user-library-read user-library-modify " +
                          "user-read-playback-state user-modify-playback-state user-read-currently-playing user-read-recently-played user-top-read";

    readonly Action<string, object?> send;
    readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(15) };
    string? refresh, access, user, userId; DateTime accessUntil;
    static bool Fast => ConsoleDesktop.Cfg.Player.id == "spotifast";   // Spotifast's cache and command line are there; plain Spotify gets the same lists through the Web API
    string stateJson = "";
    List<Lst>? saved; DateTime savedAt; int savedBusy;   // the user's saved albums (Web API), fetched in the background, refreshed after 30 minutes or a save
    int connecting;

    public Spotify(Action<string, object?> send) { this.send = send; refresh = Unseal(); }

    public bool Connected => refresh != null;

    // ---------- Spotifast's cache ----------
    static JsonNode? Read(string path) { try { return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) : null; } catch { return null; } }
    static JsonNode? Newest(string dir) => Directory.Exists(dir) ? Directory.GetFiles(dir, "*.json").Select(f => new FileInfo(f)).OrderByDescending(f => f.LastWriteTimeUtc).Select(f => Read(f.FullName)).FirstOrDefault() : null;
    static JsonNode? Cfg() => Read(Settings);
    static string? Account() => Fast ? Newest(Path.Combine(Local, @"cache\library"))?["account_id"]?.GetValue<string>() : null;
    string? Me => Account() ?? userId;

    static string? Art(JsonNode? images, int want = 300)
    {
        if (images is not JsonArray a || a.Count == 0) return null;
        var l = a.Select(i => (url: i?["url"]?.GetValue<string>(), w: i?["width"]?.GetValue<int?>() ?? 0)).Where(i => i.url != null).ToList();
        return (l.Where(i => i.w >= want).OrderBy(i => i.w).FirstOrDefault().url) ?? l.OrderByDescending(i => i.w).FirstOrDefault().url;
    }

    public record Song(string uri, string name, string artists, string? artist, string? album, string? albumUri, string? art, int dur);
    public record Lst(string? id, string uri, string name, string kind, string? owner, bool mine, int? count, string? art, string desc);

    /// <summary>A track as the page wants it (from the cache or the Web API: same shape).</summary>
    public static Song? Track(JsonNode? t, string? albumArt = null)
    {
        var uri = t?["uri"]?.GetValue<string>();
        if (t == null || string.IsNullOrEmpty(uri) || uri.StartsWith("spotify:local")) return null;
        var arts = t["artists"] as JsonArray;
        return new Song(uri, t["name"]?.GetValue<string>() ?? "",
            string.Join(", ", arts?.Select(a => a?["name"]?.GetValue<string>()).Where(n => n != null) ?? Array.Empty<string>()),
            arts?.FirstOrDefault()?["uri"]?.GetValue<string>(),
            t["album"]?["name"]?.GetValue<string>(), t["album"]?["uri"]?.GetValue<string>() is { Length: > 0 } au ? au : null,
            Art(t["album"]?["images"]) ?? albumArt, (t["duration_ms"]?.GetValue<int?>() ?? 0) / 1000);
    }

    static Lst? List(JsonNode? p, string? me)
    {
        var uri = p?["uri"]?.GetValue<string>(); if (p == null || uri == null || uri.Split(':').Length < 3) return null;   // local files' albums have no uri
        var owner = p["owner"]?["id"]?.GetValue<string>();
        return new Lst(p["id"]?.GetValue<string>(), uri, p["name"]?.GetValue<string>() ?? "", uri.Split(':')[1],
            p["owner"]?["display_name"]?.GetValue<string>() ?? p["artists"]?[0]?["name"]?.GetValue<string>(),
            owner != null && owner == me || p["collaborative"]?.GetValue<bool?>() == true,
            p["items"]?["total"]?.GetValue<int?>() ?? p["tracks"]?["total"]?.GetValue<int?>() ?? p["total_tracks"]?.GetValue<int?>(),
            Art(p["images"]), System.Text.RegularExpressions.Regex.Replace(p["description"]?.GetValue<string>() ?? "", "<[^>]+>", ""));
    }

    /// <summary>Everything the Music row shows, from the cache. Sent only when it changed.</summary>
    public void SendState(bool force = false)
    {
        try
        {
            var me = Account();
            if (me == null) { SendWebState(force); return; }   // no Spotifast here: plain Spotify through the Web API, or nothing until it is connected
            var order = (Cfg()?["sidebar_order"] as JsonArray)?.Select(x => x?.GetValue<string>()).ToList() ?? new();
            var lib = (Newest(Path.Combine(Local, @"cache\library"))?["data"] as JsonArray)?.Select(p => List(p, me)).OfType<Lst>().ToList() ?? new();
            lib = lib.OrderBy(p => order.IndexOf(p.uri) is var i && i < 0 ? 9999 : i).ToList();   // the user's own sidebar order
            var home = Newest(Path.Combine(Local, @"cache\home"))?["data"];
            var liked = Newest(Path.Combine(Local, @"cache\liked-songs"));
            var hist = Read(Path.Combine(Local, @"data\history.json")) as JsonArray;
            var lately = (hist ?? new JsonArray()).Select(i => i?["track"]?["album"]).Concat(((home?["recently_played"] as JsonArray) ?? new JsonArray()).Select(i => i?["track"]?["album"]))   // albums of what was played, newest first
                .Select(a => List(a, me)).OfType<Lst>().Where(a => a.kind == "album").DistinctBy(a => a.uri).Take(30).ToList();
            if (Connected && (saved == null || DateTime.UtcNow - savedAt > TimeSpan.FromMinutes(30))) _ = FetchSaved();
            var mixes = new[] { "made_for_you", "made_for_you_personalized", "top_mixes" }.SelectMany(k => (home?[k] as JsonArray) ?? new JsonArray())
                .Select(p => List(p, me)).OfType<Lst>().DistinctBy(x => x.uri).ToList();
            Push(force, true, lib, mixes, lately, liked?["total"]?.GetValue<int?>() ?? 0, (liked?["items"] as JsonArray)?.Select(i => Track(i?["track"])).OfType<Song>(),
                (home?["recently_played"] as JsonArray)?.Select(i => Track(i?["track"])).OfType<Song>().DistinctBy(x => x.uri), (home?["top_tracks"] as JsonArray)?.Select(t => Track(t)).OfType<Song>(),
                (home?["top_artists"] as JsonArray)?.Select(a => (object)new { uri = a?["uri"]?.GetValue<string>(), name = a?["name"]?.GetValue<string>(), art = Art(a?["images"]) }),
                hist?.Take(200).Select(i => Track(i?["track"])).OfType<Song>());   // history: what Spotifast played, newest first: heard songs find their uri here
        }
        catch (Exception e) { Log.Write("spotify state failed: " + e.Message); }
    }

    /// <summary>The sp-state event (BRIDGE.md), the same shape from either source. cache: Spotifast's files are there; can: a Spotify app id is known, so Connect can work.</summary>
    void Push(bool force, bool cache, List<Lst> playlists, List<Lst> mixes, List<Lst> lately, int likedTotal, IEnumerable<Song>? likedItems, IEnumerable<Song>? recent, IEnumerable<Song>? top, IEnumerable<object>? artists, IEnumerable<Song>? history)
    {
        var state = new
        {
            connected = Connected, user, cache, can = ClientId() != null,
            playlists, mixes, albums = saved ?? new List<Lst>(), lately,
            liked = new { total = likedTotal, items = likedItems }, recent, top, artists, history,
        };
        var j = JsonSerializer.Serialize(state);
        if (force || j != stateJson) { stateJson = j; send("sp-state", state); }
    }

    // ---------- plain Spotify: the same lists through the Web API (playlists, Liked Songs, played lately, top songs and artists) ----------
    sealed record WebLib(List<Lst> playlists, int likedTotal, List<Song> liked, List<Song> recent, List<Song> top, List<object> artists);
    WebLib? web; DateTime webAt; int webBusy;

    void SendWebState(bool force)
    {
        if (Connected && DateTime.UtcNow - webAt > TimeSpan.FromMinutes(10)) _ = FetchWeb();   // webAt moves on a failure too, so a refused call is not asked again at once
        if (Connected && (saved == null || DateTime.UtcNow - savedAt > TimeSpan.FromMinutes(30))) _ = FetchSaved();
        var w = Connected ? web : null;
        var lately = (w?.recent ?? new()).Where(t => t.albumUri != null).DistinctBy(t => t.albumUri).Take(30).Select(t => new Lst(t.albumUri!.Split(':')[^1], t.albumUri!, t.album ?? "", "album", t.artists, false, null, t.art, "")).ToList();
        Push(force, false, w?.playlists ?? new(), new(), lately, w?.likedTotal ?? 0, w?.liked, w?.recent.DistinctBy(x => x.uri), w?.top, w?.artists, w?.recent);
    }

    async Task FetchWeb()
    {
        if (Interlocked.Exchange(ref webBusy, 1) == 1) return;
        try
        {
            if (await Token() == null) return;
            // one refused list (a missing scope, Spotify's Feb 2026 rules) leaves the others
            async Task<(List<JsonNode?> items, int total)> Items(string path, int max)
            {
                var all = new List<JsonNode?>(); int total = 0;
                try
                {
                    for (var next = path; next != null && all.Count < max;)
                    {
                        var r = await Call(HttpMethod.Get, next);
                        all.AddRange((r?["items"] as JsonArray) ?? new JsonArray()); total = r?["total"]?.GetValue<int?>() ?? all.Count;
                        next = r?["next"]?.GetValue<string>()?.Replace(Api, "");
                    }
                }
                catch (Exception e) { Log.Write($"spotify {path}: {e.Message}"); }
                return (all, total);
            }
            var lists = await Items("/me/playlists?limit=50", 500);
            var liked = await Items("/me/tracks?limit=50", 50);
            var recent = await Items("/me/player/recently-played?limit=50", 50);
            var top = await Items("/me/top/tracks?limit=20", 20);
            var artists = await Items("/me/top/artists?limit=20", 20);
            web = new(lists.items.Select(p => List(p, userId)).OfType<Lst>().ToList(), liked.total, liked.items.Select(i => Track(i?["track"])).OfType<Song>().ToList(),
                recent.items.Select(i => Track(i?["track"])).OfType<Song>().ToList(), top.items.Select(t => Track(t)).OfType<Song>().ToList(),
                artists.items.Select(a => (object)new { uri = a?["uri"]?.GetValue<string>(), name = a?["name"]?.GetValue<string>(), art = Art(a?["images"]) }).ToList());
            Log.Write($"spotify web library: {web.playlists.Count} playlists, {web.likedTotal} liked");
        }
        catch (Exception e) { Log.Write("spotify web library: " + e.Message); }
        finally { webAt = DateTime.UtcNow; webBusy = 0; }
        SendState();
    }

    /// <summary>Saved albums, newest saved first (GET /me/albums, up to 2000), then the state again.</summary>
    async Task FetchSaved()
    {
        if (Interlocked.Exchange(ref savedBusy, 1) == 1) return;
        try
        {
            var me = Me; var all = new List<Lst>();
            for (var next = "/me/albums?limit=50"; next != null && all.Count < 2000;)
            {
                var r = await Call(HttpMethod.Get, next);
                all.AddRange((r?["items"] as JsonArray)?.Select(i => List(i?["album"], me)).OfType<Lst>() ?? Enumerable.Empty<Lst>());
                next = r?["next"]?.GetValue<string>()?.Replace(Api, "");
            }
            saved = all; savedAt = DateTime.UtcNow; Log.Write($"spotify: {all.Count} saved albums");
        }
        catch (Exception e) { Log.Write("spotify saved albums: " + e.Message); saved ??= new(); savedAt = DateTime.UtcNow; }
        finally { savedBusy = 0; }
        SendState();
    }

    /// <summary>Songs Spotifast cached for a playlist (it keeps the playlists you opened in it).</summary>
    static List<Song>? CachedSongs(string id)
    {
        var root = Path.Combine(Local, @"cache\playlists");
        var dir = Fast && Directory.Exists(root) ? Directory.GetDirectories(root).FirstOrDefault() : null;
        var rows = dir == null ? null : Directory.GetFiles(dir, id + ".rows.*").Select(f => new FileInfo(f)).OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
        return rows == null ? null : (Read(rows.FullName) as JsonArray)?.Select(i => Track(i?["item"] ?? i?["track"])).OfType<Song>().ToList();
    }

    // ---------- page calls ----------
    public void Do(JsonElement m)
    {
        string S(string k) => m.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        var op = S("op");
        _ = Task.Run(async () =>
        {
            try
            {
                switch (op)
                {
                    case "state": SendState(true); break;
                    case "connect": await Connect(); break;
                    case "disconnect": refresh = access = user = userId = null; web = null; webAt = default; try { File.Delete(TokenFile); } catch { } SendState(true); break;
                    case "search": await Search(S("q")); break;
                    case "list": await Songs(S("uri")); break;
                    case "play": await Play(S("uri"), S("context")); break;
                    case "best": Best(S("q"), S("kind")); break;
                    case "queue": await Need(); await Call(HttpMethod.Post, $"/me/player/queue?uri={Uri.EscapeDataString(S("uri"))}{await Device("&")}"); Done(op, true, S("name")); break;
                    case "like": await Need(); await Like(S("uri")); Done(op, true, S("name")); break;
                    case "save": await Need(); await Like(S("uri")); savedAt = DateTime.MinValue; Done(op, true, S("name"), S("uri")); SendState(); break;   // an album (or artist) into the library: the same PUT /me/library
                    case "add": await Need(); await Add(S("id"), S("uri")); Done(op, true, S("name"), S("id")); break;
                    case "now": await Now(); break;
                }
            }
            catch (Exception e) { Log.Write($"spotify {op}: {e.Message}"); Done(op, false, e is NeedConnect ? "connect" : e.Message, S("id")); }
        });
    }

    sealed class NeedConnect : Exception { public NeedConnect() : base("Connect Spotify first") { } }
    sealed class ApiError : Exception { public readonly int Code; public ApiError(int code, string msg) : base(msg) { Code = code; } }
    async Task Need() { if (await Token() == null) throw new NeedConnect(); }
    void Done(string op, bool ok, string message, string? id = null) => send("sp-done", new { op, ok, message, id });

    async Task Search(string q)
    {
        q = q.Trim(); if (q.Length == 0) return;
        if (await Token() == null) { send("sp-search", Fast ? LocalSearch(q) : new { q, local = true, tracks = Array.Empty<object>(), artists = Array.Empty<object>(), albums = Array.Empty<object>(), playlists = Array.Empty<object>() }); return; }   // signed out: Spotifast's cache, or nothing but "play the best match"   // signed out: the songs and playlists Spotifast has cached
        var r = await Call(HttpMethod.Get, $"/search?q={Uri.EscapeDataString(q)}&type=track,artist,album,playlist&limit=10");
        var me = Account();
        send("sp-search", new
        {
            q, local = false,
            tracks = (r?["tracks"]?["items"] as JsonArray)?.Select(t => Track(t)).Where(x => x != null),
            artists = (r?["artists"]?["items"] as JsonArray)?.Where(a => a != null).Select(a => new { uri = a!["uri"]?.GetValue<string>(), name = a["name"]?.GetValue<string>(), art = Art(a["images"]), followers = a["followers"]?["total"]?.GetValue<int?>() }),
            albums = (r?["albums"]?["items"] as JsonArray)?.Select(a => List(a, me)).Where(x => x != null),
            playlists = (r?["playlists"]?["items"] as JsonArray)?.Select(p => List(p, me)).Where(x => x != null),
        });
    }

    object LocalSearch(string q)
    {
        var words = q.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        bool Hit(params string?[] f) { var s = string.Join(" ", f).ToLowerInvariant(); return words.All(s.Contains); }
        var me = Account();
        var songs = new List<JsonNode?>();
        var liked = Newest(Path.Combine(Local, @"cache\liked-songs"));
        songs.AddRange((liked?["items"] as JsonArray)?.Select(i => i?["track"]) ?? Enumerable.Empty<JsonNode?>());
        var dir = Directory.GetDirectories(Path.Combine(Local, @"cache\playlists")).FirstOrDefault();
        if (dir != null) foreach (var f in Directory.GetFiles(dir, "*.rows.*")) songs.AddRange((Read(f) as JsonArray)?.Select(i => i?["item"] ?? i?["track"]) ?? Enumerable.Empty<JsonNode?>());
        songs.AddRange((Read(Path.Combine(Local, @"data\history.json")) as JsonArray)?.Select(i => i?["track"]) ?? Enumerable.Empty<JsonNode?>());
        var tracks = songs.Where(t => t != null && Hit(t["name"]?.GetValue<string>(), string.Join(" ", (t["artists"] as JsonArray)?.Select(a => a?["name"]?.GetValue<string>()) ?? Array.Empty<string>()), t["album"]?["name"]?.GetValue<string>()))
            .Select(t => Track(t)).OfType<Song>().DistinctBy(x => x.uri).Take(10).ToList();
        var lists = (Newest(Path.Combine(Local, @"cache\library"))?["data"] as JsonArray)?.Where(p => Hit(p?["name"]?.GetValue<string>())).Select(p => List(p, me)).Where(x => x != null).Take(10).ToList();
        return new { q, local = true, tracks, artists = Array.Empty<object>(), albums = Array.Empty<object>(), playlists = lists };
    }

    /// <summary>A playlist's or album's songs, an artist's albums: answered as sp-list { uri, items | albums, note }.</summary>
    async Task Songs(string uri)
    {
        var p = uri.Split(':'); if (p.Length < 3) return;
        string kind = p[1], id = p[2]; var token = await Token();
        if (uri == "spotifast:liked-songs" || kind == "collection")
        {
            if (token == null) { send("sp-list", new { uri, items = (Newest(Path.Combine(Local, @"cache\liked-songs"))?["items"] as JsonArray)?.Select(i => Track(i?["track"])).Where(x => x != null), note = "The newest 50 · connect Spotify for all of them" }); return; }
            var all = new List<Song>();
            for (var next = "/me/tracks?limit=50"; next != null && all.Count < 1000;)
            {
                var r = await Call(HttpMethod.Get, next); all.AddRange((r?["items"] as JsonArray)?.Select(i => Track(i?["track"])).OfType<Song>() ?? Enumerable.Empty<Song>());
                next = r?["next"]?.GetValue<string>()?.Replace(Api, "");
                if (all.Count == 50) send("sp-list", new { uri, items = all.ToList(), more = true });   // the first page right away
            }
            send("sp-list", new { uri, items = all }); return;
        }
        if (kind == "playlist")
        {
            var cached = CachedSongs(id);
            if (token != null)
                try
                {
                    var all = new List<Song>();
                    for (var next = $"/playlists/{id}/items?limit=100"; next != null && all.Count < 2000;)
                    {
                        var r = await Call(HttpMethod.Get, next); if (r?["items"] is not JsonArray a) break;   // not the user's: Spotify sends no items (Feb 2026 rule)
                        all.AddRange(a.Select(i => Track(i?["item"] ?? i?["track"])).OfType<Song>());
                        next = r["next"]?.GetValue<string>()?.Replace(Api, "");
                    }
                    if (all.Count > 0 || cached == null) { send("sp-list", new { uri, items = all, note = all.Count == 0 ? "Spotify only lists songs of playlists you own · open it once in Spotifast and its songs show up here" : null }); return; }
                }
                catch (ApiError) { }
            send("sp-list", new { uri, items = cached ?? new List<Song>(), note = cached == null ? "Open it once in Spotifast and its songs show up here · ✕ plays it from the start" : null }); return;
        }
        if (token == null) { send("sp-list", new { uri, items = new List<Song>(), note = "Connect Spotify to see what's inside · ✕ plays it" }); return; }
        if (kind == "album")
        {
            var r = await Call(HttpMethod.Get, $"/albums/{id}"); var art = Art(r?["images"]);
            send("sp-list", new { uri, items = (r?["tracks"]?["items"] as JsonArray)?.Select(t => { if (t != null) t["album"] ??= new JsonObject { ["name"] = r?["name"]?.GetValue<string>(), ["uri"] = uri }; return Track(t, art); }).Where(x => x != null) });
        }
        else if (kind == "artist")
        {
            var r = await Call(HttpMethod.Get, $"/artists/{id}/albums?include_groups=album,single&limit=20"); var me = Account();
            send("sp-list", new { uri, albums = (r?["items"] as JsonArray)?.Select(a => List(a, me)).Where(x => x != null) });
        }
    }

    /// <summary>Plays a song (inside its playlist or album when given) or a whole playlist/album/artist. Signed in: on Spotifast's Connect device
    /// through the Web API, so a song picked in a playlist plays on through that playlist. Otherwise Spotifast's own play-uri.</summary>
    async Task Play(string uri, string context)
    {
        if (context == "spotifast:liked-songs" && Me is { } me) context = $"spotify:user:{me}:collection";   // Liked Songs as a Spotify context
        if (uri == "spotifast:liked-songs" && Me is { } me2) uri = $"spotify:user:{me2}:collection";
        if (await Token() != null)
            try
            {
                object body = uri.StartsWith("spotify:track") ? (context.StartsWith("spotify:playlist") || context.StartsWith("spotify:album") || context.EndsWith(":collection") ? new { context_uri = context, offset = new { uri } } : new { uris = new[] { uri } }) : new { context_uri = uri };
                await Call(HttpMethod.Put, "/me/player/play" + await Device("?"), body); return;
            }
            catch (Exception e) { Log.Write("spotify play via the Web API failed, using play-uri: " + e.Message); }
        if (Fast ? Media.Spotifast("play-uri", uri) : Open(uri)) return;
        Done("play", false, Fast ? "Spotifast isn't installed" : "Connect Spotify to play from here");
    }

    /// <summary>Plain Spotify, not connected: the song or list opens in the Spotify app (a spotify: link), which shows it but may not start it.</summary>
    static bool Open(string uri)
    {
        if (!uri.StartsWith("spotify:")) return false;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri) { UseShellExecute = true }); return true; } catch { return false; }
    }

    /// <summary>Plays the best match for these words: Spotifast searches and plays it itself; any other player opens its own search at them.</summary>
    public static bool Best(string words, string kind)
    {
        if (!Fast) return Media.OpenSearch(words);
        return Media.Spotifast(new[] { "play-search", "--kind", kind is "album" or "artist" or "playlist" ? kind : "track", "--" }.Concat(words.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToArray());   // "--": a word like "-live-" is part of the song, not an option
    }

    async Task<string> Device(string sep)
    {
        var name = Cfg()?["device_name"]?.GetValue<string>() ?? "Spotifast";
        try
        {
            var r = await Call(HttpMethod.Get, "/me/player/devices");
            var all = r?["devices"] as JsonArray;
            var d = (Fast ? all?.FirstOrDefault(x => x?["name"]?.GetValue<string>() == name) : null) ?? all?.FirstOrDefault(x => x?["is_active"]?.GetValue<bool>() == true)
                ?? all?.FirstOrDefault(x => x?["type"]?.GetValue<string>() == "Computer") ?? all?.FirstOrDefault();   // plain Spotify: the one playing, else this PC's app
            return d?["id"]?.GetValue<string>() is { } id ? $"{sep}device_id={id}" : "";
        }
        catch { return ""; }
    }

    async Task Like(string uri)
    {
        try { await Call(HttpMethod.Put, "/me/library", new { uris = new[] { uri } }); }
        catch (ApiError e) when (e.Code is 400 or 404 or 405) { await Call(HttpMethod.Put, "/me/library?uris=" + Uri.EscapeDataString(uri)); }   // the Feb 2026 endpoint takes uris; older ones a query
    }

    async Task Add(string id, string uri)
    {
        try { await Call(HttpMethod.Post, $"/playlists/{id}/items", new { uris = new[] { uri } }); }
        catch (ApiError e) when (e.Code is 404 or 405) { await Call(HttpMethod.Post, $"/playlists/{id}/tracks", new { uris = new[] { uri } }); }
    }

    /// <summary>The song playing now, with its uri (sp-done op "now"): the Web API when signed in, else Spotifast's history.json.</summary>
    async Task Now()
    {
        object? t = null;
        if (await Token() != null) try { t = Track((await Call(HttpMethod.Get, "/me/player/currently-playing"))?["item"]); } catch { }
        t ??= (Read(Path.Combine(Local, @"data\history.json")) as JsonArray)?.Select(i => Track(i?["track"])).FirstOrDefault(x => x != null);
        send("sp-now", t);
    }

    // ---------- Web API ----------
    async Task<JsonNode?> Call(HttpMethod method, string path, object? body = null, bool again = true)
    {
        var token = await Token() ?? throw new NeedConnect();
        using var req = new HttpRequestMessage(method, path.StartsWith("http") ? path : Api + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body != null) req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        else if (method != HttpMethod.Get) req.Content = new StringContent("", Encoding.UTF8, "application/json");   // Spotify wants a length on empty PUT/POST
        using var res = await http.SendAsync(req);
        var text = await res.Content.ReadAsStringAsync();
        if (res.StatusCode == HttpStatusCode.Unauthorized && again) { access = null; return await Call(method, path, body, false); }
        if (!res.IsSuccessStatusCode)
        {
            var msg = (Try(text)?["error"]?["message"]?.GetValue<string>()) ?? res.ReasonPhrase ?? "";
            throw new ApiError((int)res.StatusCode, res.StatusCode switch
            {
                HttpStatusCode.NotFound when path.Contains("/me/player") => ConsoleDesktop.Cfg.Player.name + " isn't playing on this PC · open it once",
                HttpStatusCode.Forbidden => "Spotify said no: " + msg, (HttpStatusCode)429 => "Spotify asks to wait a moment (rate limit)", _ => $"Spotify: {msg} ({(int)res.StatusCode})",
            });
        }
        return text.Length == 0 ? null : Try(text);
    }
    static JsonNode? Try(string s) { try { return JsonNode.Parse(s); } catch { return null; } }

    async Task<string?> Token()
    {
        if (access != null && DateTime.UtcNow < accessUntil) return access;
        if (refresh == null) return null;
        var r = await TokenCall(new() { ["grant_type"] = "refresh_token", ["refresh_token"] = refresh, ["client_id"] = ClientId() ?? "" });
        if (r == null) { Log.Write("spotify: refresh refused, signed out"); refresh = null; try { File.Delete(TokenFile); } catch { } SendState(true); return null; }
        return access;
    }

    async Task<JsonNode?> TokenCall(Dictionary<string, string> form)
    {
        using var res = await http.PostAsync("https://accounts.spotify.com/api/token", new FormUrlEncodedContent(form));
        var j = Try(await res.Content.ReadAsStringAsync());
        if (!res.IsSuccessStatusCode || j?["access_token"] == null) { Log.Write($"spotify token: {(int)res.StatusCode} {j?["error"]}"); return null; }
        access = j["access_token"]!.GetValue<string>(); accessUntil = DateTime.UtcNow.AddSeconds((j["expires_in"]?.GetValue<int>() ?? 3600) - 60);
        if (j["refresh_token"]?.GetValue<string>() is { } rt && rt != refresh) { refresh = rt; Seal(rt); }   // PKCE refresh tokens rotate: keep the newest
        if (user == null) try { var who = await Call(HttpMethod.Get, "/me"); user = who?["display_name"]?.GetValue<string>(); userId = who?["id"]?.GetValue<string>(); } catch { }
        return j;
    }

    /// <summary>The user's own Spotify app (developer.spotify.com, redirect http://127.0.0.1:8989/login): Setup's field, else the one Spotifast was given.</summary>
    static string? ClientId() => ConsoleDesktop.Cfg.Get("music.spotifyClientId") ?? (Cfg()?["web_client_id"]?.GetValue<string>() is { Length: > 0 } id ? id : null);

    /// <summary>PKCE sign-in: the browser asks the user once; Spotify sends the code to 127.0.0.1:8989/login, which this listens on for 3 minutes.</summary>
    async Task Connect()
    {
        if (Interlocked.Exchange(ref connecting, 1) == 1) return;
        try
        {
            var cid = ClientId(); if (cid == null) { Done("connect", false, Fast ? "Spotifast has no personal Spotify app set up (Settings › Personal Spotify app)" : "Add your Spotify app's client id in Setup › Music first"); return; }
            var verifier = B64(RandomNumberGenerator.GetBytes(48)); var state = B64(RandomNumberGenerator.GetBytes(12));
            var challenge = B64(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
            TcpListener tl;
            try { tl = new TcpListener(IPAddress.Loopback, 8989); tl.Start(); }
            catch { Done("connect", false, "Port 8989 is busy (Spotifast's own sign-in?) · close it and try again"); return; }
            try
            {
                Library.OpenUrl($"https://accounts.spotify.com/authorize?client_id={cid}&response_type=code&redirect_uri={Uri.EscapeDataString(Redirect)}&code_challenge_method=S256&code_challenge={challenge}&state={state}&scope={Uri.EscapeDataString(Scopes)}");
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                while (true)
                {
                    using var c = await tl.AcceptTcpClientAsync(cts.Token);
                    var s = c.GetStream(); var buf = new byte[8192]; var n = await s.ReadAsync(buf, cts.Token);
                    var line = Encoding.ASCII.GetString(buf, 0, n).Split('\n')[0];   // GET /login?code=...&state=... HTTP/1.1
                    var qs = System.Web.HttpUtility.ParseQueryString(new Uri("http://x" + line.Split(' ').ElementAtOrDefault(1)).Query);
                    if (qs["state"] != state) { await Reply(s, "Not this sign-in."); continue; }
                    var code = qs["code"];
                    var ok = code != null && await TokenCall(new() { ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = Redirect, ["client_id"] = cid, ["code_verifier"] = verifier }) != null;
                    await Reply(s, ok ? "Spotify is connected to your desktop. You can close this tab." : "Spotify didn't connect: " + (qs["error"] ?? "the code was refused") + ". Try again from the Music tab.");
                    Log.Write("spotify connect: " + (ok ? "signed in as " + user : "failed " + qs["error"]));
                    Done("connect", ok, ok ? user ?? "" : qs["error"] ?? "refused");
                    SendState(true); return;
                }
            }
            catch (OperationCanceledException) { Done("connect", false, "No answer from the browser in 3 minutes"); }
            finally { tl.Stop(); }
        }
        finally { connecting = 0; }
    }

    static async Task Reply(Stream s, string text)
    {
        var html = $"<!doctype html><meta charset=utf-8><title>Tidepool</title><body style=\"font:20px system-ui;background:#0b2233;color:#e8f6ff;display:grid;place-items:center;height:90vh\">{WebUtility.HtmlEncode(text)}";
        var b = Encoding.UTF8.GetBytes(html);
        await s.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {b.Length}\r\nConnection: close\r\n\r\n"));
        await s.WriteAsync(b);
    }

    static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // ---------- the refresh token, sealed to this Windows user (DPAPI) ----------
    [StructLayout(LayoutKind.Sequential)] struct Blob { public int n; public IntPtr p; }
    [DllImport("crypt32.dll", SetLastError = true)] static extern bool CryptProtectData(ref Blob i, string? d, IntPtr e, IntPtr r, IntPtr pr, int f, ref Blob o);
    [DllImport("crypt32.dll", SetLastError = true)] static extern bool CryptUnprotectData(ref Blob i, IntPtr d, IntPtr e, IntPtr r, IntPtr pr, int f, ref Blob o);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr p);
    internal static byte[]? Dpapi(byte[] data, bool seal)
    {
        var h = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            var i = new Blob { n = data.Length, p = h.AddrOfPinnedObject() }; var o = new Blob();
            if (!(seal ? CryptProtectData(ref i, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, ref o) : CryptUnprotectData(ref i, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, ref o))) return null;
            var r = new byte[o.n]; Marshal.Copy(o.p, r, 0, o.n); LocalFree(o.p); return r;
        }
        finally { h.Free(); }
    }
    static void Seal(string token) { try { File.WriteAllBytes(TokenFile, Dpapi(Encoding.UTF8.GetBytes(token), true)!); } catch (Exception e) { Log.Write("spotify: token not saved: " + e.Message); } }
    static string? Unseal() { try { return File.Exists(TokenFile) && Dpapi(File.ReadAllBytes(TokenFile), false) is { } b ? Encoding.UTF8.GetString(b) : null; } catch { return null; } }
}
