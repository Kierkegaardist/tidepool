using System.Diagnostics;
using System.Text.Json;
using Windows.Media.Control;

namespace ConsoleDesktop;

/// <summary>Now playing + play/pause/next/prev/seek through the Windows media session (the same thing the volume flyout shows), of one player only
/// (config.json "music.player", Cfg.Player): a YouTube tab or any other player never takes the desktop's player (user, 13 Nov: "exclusively
/// connected to Spotifast"). Any player that shows in Windows' volume flyout works; "any" follows whichever one Windows calls current.
/// Also keeps what was heard (app/.heard.json, newest first) so the Music row can list it and play a song again.</summary>
sealed class Media
{
    /// <summary>pos/dur in seconds (0 = the player does not say); at = when pos was true (unix ms): the page counts on from there while playing.</summary>
    public record Playing(string app, string title, string artist, string album, bool playing, string? art, double pos, double dur, long at, bool canSeek);
    public record Heard(string app, string title, string artist, string album, long at, string? art);
    /// <summary>A music player the desktop can follow. match: parts of its media-session app id (lower case); search: where a song is looked up
    /// when it cannot be played directly ({q} = the words); loose: fall back to Windows' current session (players that live in a browser tab).</summary>
    public record Player(string id, string name, string[] match, string? search, Func<bool> installed, bool loose = false);
    static string Local => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    static string Roaming => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    static bool Store(string family) { try { var d = Path.Combine(Local, "Packages"); return Directory.Exists(d) && Directory.EnumerateDirectories(d, family + "*").Any(); } catch { return false; } }   // a Store app's data folder
    public static readonly Player[] Players =
    {
        new("spotifast", "Spotifast", new[] { "spotifast" }, null, () => File.Exists(SpotifastExe)),
        new("spotify", "Spotify", new[] { "spotify" }, "spotify:search:{q}", () => File.Exists(Path.Combine(Roaming, @"Spotify\Spotify.exe")) || Store("SpotifyAB.SpotifyMusic") || Studio.Listed("Spotify")),
        new("tidal", "TIDAL", new[] { "tidal" }, "https://listen.tidal.com/search?q={q}", () => File.Exists(Path.Combine(Local, @"TIDAL\TIDAL.exe")) || Store("WiMPMusic") || Studio.Listed("TIDAL")),
        new("apple", "Apple Music", new[] { "applemusic", "itunes" }, "https://music.apple.com/search?term={q}", () => Store("AppleInc.AppleMusicWin") || Store("AppleInc.iTunes")),
        new("ytmusic", "YouTube Music", new[] { "youtube music", "youtubemusic", "youtube-music", "ytmdesktop", "cinhimbnkkaeohfgghhklpknlkffjgod" }, "https://music.youtube.com/search?q={q}", () => false, loose: true),   // the long id = its Chrome/Edge web app
        new("deezer", "Deezer", new[] { "deezer" }, "https://www.deezer.com/search/{q}", () => Store("Deezer.") || Directory.Exists(Path.Combine(Local, @"Programs\deezer-desktop")) || Studio.Listed("Deezer")),
        new("amazon", "Amazon Music", new[] { "amazon music", "amazonmusic", "amazonmobilellc" }, "https://music.amazon.com/search/{q}", () => Directory.Exists(Path.Combine(Local, "Amazon Music")) || Store("AmazonMobileLLC.AmazonMusic") || Studio.Listed("Amazon Music")),
        new("qobuz", "Qobuz", new[] { "qobuz" }, "https://play.qobuz.com/search?q={q}", () => Directory.Exists(Path.Combine(Local, "Qobuz")) || Studio.Listed("Qobuz")),
        new("any", "Whatever is playing", Array.Empty<string>(), "https://music.youtube.com/search?q={q}", () => false, loose: true),
    };
    /// <summary>The player by id; an id not in the list is taken as a program's own name ("foobar2000", "musicbee"), shown as `name`.</summary>
    public static Player Find(string id, string? name = null) => Players.FirstOrDefault(p => p.id == id) is { } p ? (name == null ? p : p with { name = name })
        : new(id, name ?? id, new[] { id.ToLowerInvariant() }, "https://music.youtube.com/search?q={q}", () => true);
    static Player Me => Cfg.Player;

    GlobalSystemMediaTransportControlsSessionManager? manager;
    readonly Action<Playing?> changed; readonly Action<List<Heard>> heardChanged;
    string last = ""; Playing? now;
    readonly string heardFile = Path.Combine(Paths.App, ".heard.json");
    List<Heard> heard = new();
    public List<Heard> HeardList() { lock (heard) return heard.ToList(); }   // a copy: reports add to the list on pool threads

    public Media(Action<Playing?> changed, Action<List<Heard>> heardChanged)
    {
        this.changed = changed; this.heardChanged = heardChanged;
        try { if (File.Exists(heardFile)) heard = JsonSerializer.Deserialize<List<Heard>>(File.ReadAllText(heardFile)) ?? new(); } catch (Exception e) { Log.Write("heard list unreadable: " + e.Message); }
        heard = DropBorrowedCovers(heard);
        if (Me.id == "spotifast" && heard.RemoveAll(h => h.app != "spotifast") > 0) try { File.WriteAllText(heardFile, JsonSerializer.Serialize(heard)); } catch { }   // browser songs heard before the player was Spotifast-only
        _ = Start();
    }

    async Task Start()
    {
        try
        {
            manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            manager.SessionsChanged += (_, _) => Watch();   // the player starting or quitting
            if (Me.loose) manager.CurrentSessionChanged += (_, _) => Watch();
            Watch();
        }
        catch (Exception e) { Log.Write("media session unavailable: " + e.Message); }
    }

    GlobalSystemMediaTransportControlsSession? watched;
    /// <summary>The page's name for whose song this is: the chosen player's id (so "spotifast" stays "spotifast"), else the program's own name.</summary>
    static string AppOf(GlobalSystemMediaTransportControlsSession s) =>
        s.SourceAppUserModelId.ToLowerInvariant() is var a && Me.match.Any(a.Contains) ? Me.id : Path.GetFileNameWithoutExtension(s.SourceAppUserModelId.Split('!')[0]).ToLowerInvariant();
    /// <summary>The chosen player's media session (its app id holds one of the player's names), null while it is not running.</summary>
    GlobalSystemMediaTransportControlsSession? Session()
    {
        try
        {
            var hit = manager?.GetSessions().FirstOrDefault(x => x.SourceAppUserModelId.ToLowerInvariant() is var a && Me.match.Any(a.Contains));
            return hit ?? (Me.loose ? manager?.GetCurrentSession() : null);
        }
        catch { return null; }
    }
    void Watch()
    {
        var s = Session();
        if (s != watched)
        {
            watched = s;
            if (s != null) { s.MediaPropertiesChanged += (_, _) => _ = Report(); s.PlaybackInfoChanged += (_, _) => _ = Report(); s.TimelinePropertiesChanged += (_, _) => _ = Report(); }
        }
        _ = Report();
    }

    async Task Report()
    {
        Playing? p = null;
        try
        {
            if (Session() is { } s)
            {
                var props = await s.TryGetMediaPropertiesAsync();
                var app = AppOf(s);
                string? art = null;
                if (props.Thumbnail != null)
                {
                    using var st = (await props.Thumbnail.OpenReadAsync()).AsStreamForRead(); using var ms = new MemoryStream();
                    await st.CopyToAsync(ms);
                    if (ms.Length is > 0 and < 2_000_000) art = "data:image/jpeg;base64," + Convert.ToBase64String(ms.ToArray());   // browsers sniff png too
                }
                var tl = s.GetTimelineProperties(); var info = s.GetPlaybackInfo(); var playing = info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                var dur = (tl.EndTime - tl.StartTime).TotalSeconds;
                p = new(app, props.Title ?? "", props.Artist ?? "", props.AlbumTitle ?? "", playing, art, dur > 0 ? Math.Round(tl.Position.TotalSeconds, 1) : 0, Math.Round(Math.Max(dur, 0), 1), tl.LastUpdatedTime.ToUnixTimeMilliseconds(), info.Controls.IsPlaybackPositionEnabled);
            }
        }
        catch (Exception e) { Log.Write("media read failed: " + e.Message); }
        // the timeline ticks while playing: only a jump (a seek, a new track) or a state change is news, so the art is not resent every second
        var key = JsonSerializer.Serialize(p == null ? null : p with { pos = 0, at = 0 });
        if (key == last && !(p != null && now != null && Math.Abs(p.pos - Expected(now, p.at)) > 2)) return;
        last = key; now = p; changed(p);
        if (p is { playing: true, title: not "" }) Remember(p);
    }

    /// <summary>Lists saved before covers were named by content can hold another album's cover (the player's stale thumbnail). The oldest album to show a cover keeps it; a later, different album showing the same picture loses it.</summary>
    static List<Heard> DropBorrowedCovers(List<Heard> list)
    {
        var owner = new Dictionary<string, string>(); var dropped = 0;
        var fixedList = Enumerable.Reverse(list).Select(h =>
        {
            if (h.art is not { } a) return h;
            var file = Path.Combine(Icons.Dir, a.Split('/').Last());
            if (!File.Exists(file)) return h with { art = null };
            var key = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(File.ReadAllBytes(file))); var album = h.artist + "|" + (h.album != "" ? h.album : h.title);
            if (owner.TryAdd(key, album) || owner[key] == album) return h;
            if (Path.GetFileName(file) == "art-" + key[..16] + Path.GetExtension(file)) return h;   // named by its content: saved after the fix, so two albums really share this cover
            dropped++; return h with { art = null };
        }).Reverse().ToList();
        if (dropped > 0) { Log.Write($"heard list: {dropped} borrowed covers dropped"); try { File.WriteAllText(Path.Combine(Paths.App, ".heard.json"), JsonSerializer.Serialize(fixedList)); } catch { } }   // saved, so it is done once
        return fixedList;
    }

    static double Expected(Playing p, long atMs) => p.playing ? p.pos + (atMs - p.at) / 1000.0 : p.pos;

    /// <summary>A song counts as heard once it plays; skipping on within 30 s replaces it instead of piling up skips.</summary>
    void Remember(Playing p) { lock (heard) Add(p); }   // reports land on pool threads, sometimes two at once
    void Add(Playing p)
    {
        var top = heard.FirstOrDefault(); var t = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string? art = null;
        if (p.art?.Split(',') is [_, var b64]) try { art = Icons.Save(Convert.FromBase64String(b64), ".jpg"); } catch { }
        if (top != null && top.title == p.title && top.artist == p.artist)
        {
            if (art == null || art == top.art) return;
            heard[0] = top with { art = art };   // the cover often lands a moment after the title (the first report still carried the last song's)
        }
        else
        {
            if (top != null && t - top.at < 30) heard.RemoveAt(0);
            heard.Insert(0, new(p.app, p.title, p.artist, p.album, t, art));
        }
        if (heard.Count > 200) heard.RemoveRange(200, heard.Count - 200);
        try { File.WriteAllText(heardFile, JsonSerializer.Serialize(heard)); } catch (Exception e) { Log.Write("heard list not saved: " + e.Message); }
        heardChanged(heard.ToList());   // under the lock (Add runs inside it)
    }

    public async void Do(string action, double at = 0)
    {
        try
        {
            if (Session() is not { } s) return;   // the desktop's buttons drive Spotifast only, never a browser tab
            if (action == "seek" && now?.app == "spotifast") { Spotifast("seek-to", Math.Round(Math.Max(0, at)).ToString()); return; }   // Spotifast takes the media session's seek and does nothing with it; its own command line works
            _ = action switch
            {
                "playpause" => await s.TryTogglePlayPauseAsync(), "next" => await s.TrySkipNextAsync(), "prev" => await s.TrySkipPreviousAsync(),
                "seek" => await s.TryChangePlaybackPositionAsync(TimeSpan.FromSeconds(Math.Max(0, at)).Ticks), _ => false,
            };
        }
        catch (Exception e) { Log.Write($"media {action} failed: {e.Message}"); }
    }

    /// <summary>What to search for to hear a heard song (or its album, or more by its artist) again; Spotify.Best plays or looks it up in the chosen player.</summary>
    public static string? ReplayWords(Heard h, string kind) =>
        kind == "track" ? $"{h.artist} {h.title}" : kind == "album" ? $"{h.artist} {h.album}" : kind == "artist" ? h.artist : null;

    /// <summary>Opens the chosen player's own search for these words (its app through a link like spotify:search:, else its web player).</summary>
    public static bool OpenSearch(string words)
    {
        if (Me.search == null) return false;
        var url = Me.search.Replace("{q}", Uri.EscapeDataString(words));
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); Log.Write($"music search in {Me.name}: {words}"); return true; }
        catch (Exception e) { Log.Write($"music search in {Me.name} failed: {e.Message}"); return false; }
    }

    static string SpotifastExe => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\Spotifast\spotifast.exe");
    /// <summary>Runs Spotifast's command line (it hands the command to the running Spotifast).</summary>
    internal static bool Spotifast(params string[] args)
    {
        var exe = SpotifastExe;
        if (!File.Exists(exe)) return false;
        var si = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) si.ArgumentList.Add(a);
        // Its command line only talks to a running Spotifast and does nothing otherwise (18 Nov: plays from the Music tab went nowhere while it
        // was closed). So start it first and send the command once its window is up.
        if (args.Length > 0 && args[0] != "seek-to" && Process.GetProcessesByName("spotifast").Length == 0)
            try
            {
                var app = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true })!;
                Log.Write("spotifast was closed, starting it for: " + string.Join(' ', args));
                _ = Task.Run(() =>
                {
                    for (int i = 0; i < 60 && app.MainWindowHandle == IntPtr.Zero && !app.HasExited; i++) { Thread.Sleep(250); app.Refresh(); }
                    Thread.Sleep(1500);   // ponytail: fixed grace for its single-instance listener; poll for it if commands still get lost
                    try { Process.Start(si); Log.Write("spotifast " + string.Join(' ', args)); } catch (Exception e) { Log.Write("spotifast failed: " + e.Message); }
                });
                return true;
            }
            catch (Exception e) { Log.Write("spotifast start failed: " + e.Message); return false; }
        try { Process.Start(si); Log.Write("spotifast " + string.Join(' ', args)); return true; } catch (Exception e) { Log.Write("spotifast failed: " + e.Message); return false; }
    }
}
