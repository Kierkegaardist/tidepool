using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ConsoleDesktop;

/// <summary>What the page can launch: installed Steam and Epic games, and Start-menu apps (shortcuts plus Store apps). host.launch(id) only runs ids from the last scan.</summary>
static class Library
{
    public record Art(string? header, string? portrait, string? hero, string? logo, string? icon = null);   // icon: the game's own exe icon (Epic has no cached art)
    public record Game(string id, string name, string source, Art art, double sizeGB, long lastPlayed, int minutes = 0);   // minutes: total playtime (Steam keeps it; 0 = unknown)
    public record App(string id, string name, string folder, string? art = null);

    static Dictionary<string, string> targets = new();   // id -> what to shell-execute
    public static string? SteamDir => (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string)?.Replace('/', '\\');
    public static string? SteamArtDir => SteamDir is { } d && Directory.Exists(Path.Combine(d, @"appcache\librarycache")) ? Path.Combine(d, @"appcache\librarycache") : null;

    /// <summary>Scans everything; safe to call off the UI thread.</summary>
    public record Item(string id, string name, string detail, long modified, string? art = null, object? info = null);   // art: icon url (https://icons.local/...) or null; info: the row's own facts (BRIDGE.md)   // files, studio projects, web sites

    /// <summary>Every list the page can launch from, by bridge key. One failing source leaves its list empty, never the others.</summary>
    public static Dictionary<string, object> Scan()
    {
        var t = new Dictionary<string, string>();
        List<T> Try<T>(string what, Func<List<T>> f) { try { return f(); } catch (Exception e) { Log.Write($"{what} scan failed: {e.Message}"); return new(); } }
        var games = Try("steam", () => Steam(t).ToList()).Concat(Try("steam shortcuts", () => SteamShortcuts(t))).Concat(Try("epic", () => Epic(t).ToList())).ToList();
        games = games.Concat(Try("other launchers", () => Uninstalled(t, games))).OrderByDescending(g => g.lastPlayed).ThenBy(g => g.name).ToList();
        var found = new Dictionary<string, object>
        {
            ["games"] = games,
            ["apps"] = Try("start menu", () => StartMenu(t)),
            ["files"] = Try("recent files", () => Places.Files(t)),
            ["studio"] = Try("studio", () => Cfg.On("studio") ? Studio.Projects(t) : new()),
            ["tracks"] = Try("tracks", () => Cfg.On("studio") ? Studio.Tracks(t) : new()),
            ["web"] = Try(Cfg.Browser.name, () => Cfg.On("web") ? Web.Scan(t) : new()),
        };
        targets = t;
        return found;
    }

    public static bool Launch(string id, Screen? on = null)
    {
        if (id.StartsWith("tab:")) return Web.Switch(id);   // an open Firefox tab: switch to it, not a second copy
        if (id.StartsWith("web:") && targets.TryGetValue(id, out var page)) { OpenUrl(page, on); return true; }   // in the browser picked in Setup, not just Windows' default
        var target = targets.GetValueOrDefault(id) ?? Browsed(id);
        if (target == null) { Log.Write($"launch refused: unknown id {id}"); return false; }
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); Log.Write($"launch {id} -> {target}"); return true; }
        catch (Exception e) { Log.Write($"launch {id} failed: {e.Message}"); return false; }
    }

    /// <summary>Shows a known file, project or shortcut selected in Explorer (the context menu's "Show in folder").</summary>
    public static bool Reveal(string id)
    {
        var target = targets.GetValueOrDefault(id) ?? Browsed(id);
        if (target == null || !File.Exists(target) && !Directory.Exists(target)) { Log.Write($"reveal refused: {id}"); return false; }
        Process.Start("explorer.exe", $"/select,\"{target}\""); return true;
    }

    /// <summary>A file or folder the Files column browsed to (Places.Browse ids): opened like a double-click in Explorer, only while it exists.</summary>
    static string? Browsed(string id) => id.StartsWith("file:") && File.Exists(id[5..]) ? id[5..] : id.StartsWith("dir:") && Directory.Exists(id[4..]) ? id[4..] : null;

    /// <summary>Opens a web address in the browser picked in Setup, else the default one (the search's "Search the web"); http and https only.</summary>
    public static void OpenUrl(string url, Screen? on = null) => Web.Open(url, on);

    static string Vdf(string text, string key) => Regex.Match(text, $"\"{key}\"\\s+\"([^\"]*)\"", RegexOptions.IgnoreCase) is { Success: true } m ? m.Groups[1].Value : "";

    static IEnumerable<Game> Steam(Dictionary<string, string> t)
    {
        if (SteamDir is not { } steam) yield break;
        var libs = Regex.Matches(File.ReadAllText(Path.Combine(steam, @"steamapps\libraryfolders.vdf")), "\"path\"\\s+\"([^\"]*)\"")
            .Select(m => m.Groups[1].Value.Replace(@"\\", @"\")).Append(steam).Distinct(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(); var playtime = SteamPlaytime(steam);
        foreach (var lib in libs)
        {
            var dir = Path.Combine(lib, "steamapps");
            if (!Directory.Exists(dir)) continue;
            foreach (var acf in Directory.EnumerateFiles(dir, "appmanifest_*.acf"))
            {
                var a = File.ReadAllText(acf);
                var appid = Vdf(a, "appid"); var name = Vdf(a, "name");
                if (appid == "" || !seen.Add(appid) || !int.TryParse(Vdf(a, "StateFlags"), out var flags) || (flags & 4) == 0) continue;   // 4 = fully installed
                if (appid == "228980" || name.StartsWith("Proton") || name.StartsWith("Steam Linux Runtime") || name.StartsWith("Steamworks")) continue;   // tools, not games
                t["steam:" + appid] = "steam://rungameid/" + appid;
                long.TryParse(Vdf(a, "SizeOnDisk"), out var size); long.TryParse(Vdf(a, "LastPlayed"), out var played);
                yield return new Game("steam:" + appid, name, "steam", SteamArt(appid), Math.Round(size / 1e9, 1), played, playtime.GetValueOrDefault(appid));
            }
        }
    }

    /// <summary>"Add a Non-Steam Game" entries: userdata/&lt;user&gt;/config/shortcuts.vdf (binary VDF). Launched through Steam so its overlay and input config apply.</summary>
    static List<Game> SteamShortcuts(Dictionary<string, string> t)
    {
        var o = new List<Game>();
        var ud = SteamDir is { } s ? Path.Combine(s, "userdata") : null;
        if (ud == null || !Directory.Exists(ud)) return o;
        foreach (var f in Directory.GetDirectories(ud).Select(d => Path.Combine(d, @"config\shortcuts.vdf")).Where(File.Exists))
            foreach (var e in BinaryVdfEntries(File.ReadAllBytes(f)))
            {
                if (!e.TryGetValue("appid", out var a) || a is not uint appid || e.GetValueOrDefault("appname") is not string name || name == "") continue;
                if (e.GetValueOrDefault("ishidden") is uint h && h != 0) continue;
                var id = "steam:" + appid;
                if (t.ContainsKey(id)) continue;   // same shortcut in several Steam accounts
                t[id] = "steam://rungameid/" + (((ulong)appid << 32) | 0x02000000);
                var exe = (e.GetValueOrDefault("exe") as string ?? "").Trim('"');
                o.Add(new Game(id, name, "steam", new(null, null, null, null, File.Exists(exe) ? Icons.Shell(exe) : null), 0, e.GetValueOrDefault("lastplaytime") is uint p ? p : 0));
            }
        return o;
    }

    /// <summary>The second-level maps of a binary VDF (shortcuts/&lt;n&gt;/...): string (01) and int32 (02) fields, keys lowercased. Nested maps (tags) are skipped.</summary>
    static List<Dictionary<string, object>> BinaryVdfEntries(byte[] b)
    {
        var list = new List<Dictionary<string, object>>(); int i = 0, depth = 0; Dictionary<string, object>? cur = null;
        string Str() { int s = i; while (i < b.Length && b[i] != 0) i++; return System.Text.Encoding.UTF8.GetString(b, s, i++ - s); }
        while (i < b.Length)
        {
            var type = b[i++];
            if (type == 8) { if (--depth == 1 && cur != null) { list.Add(cur); cur = null; } if (depth < 0) break; continue; }
            var key = Str().ToLowerInvariant();
            if (type == 0) { if (++depth == 2) cur = new(); }
            else if (type == 1) { var v = Str(); if (depth == 2) cur![key] = v; }
            else if (type == 2) { if (i + 4 > b.Length) break; var v = BitConverter.ToUInt32(b, i); i += 4; if (depth == 2) cur![key] = v; }
            else break;   // unknown type: stop rather than misread
        }
        return list;
    }

    /// <summary>--selftest: the binary VDF reader on a one-shortcut file shaped like Steam's (with a nested tags map), plus a print of the real shortcuts.</summary>
    public static bool SelfTest()
    {
        var m = new MemoryStream(); void W(params object[] xs) { foreach (var x in xs) m.Write(x is string s ? System.Text.Encoding.UTF8.GetBytes(s + "\0") : x is uint u ? BitConverter.GetBytes(u) : new[] { (byte)(int)x }); }
        W(0, "shortcuts", 0, "0", 2, "appid", 7u, 1, "AppName", "Game", 1, "Exe", "\"C:\\g.exe\"", 0, "tags", 1, "0", "x", 8, 2, "LastPlayTime", 9u, 8, 8, 8);
        var e = BinaryVdfEntries(m.ToArray());
        var ok = e.Count == 1 && e[0]["appid"] is 7u && e[0]["appname"] is "Game" && e[0]["exe"] is "\"C:\\g.exe\"" && e[0]["lastplaytime"] is 9u;
        Console.WriteLine($"binary vdf: {(ok ? "ok" : "FAIL")}; shortcuts found: {string.Join(", ", SteamShortcuts(new()).Select(g => g.name))}");
        var lt = new Dictionary<string, string>();
        Console.WriteLine($"other launchers: {string.Join(", ", Uninstalled(lt, new()).Select(g => $"{g.name} [{g.source}, {g.sizeGB} GB] -> {lt[g.id]}"))}");
        var store = StartMenu(new()).Where(a => a.id.StartsWith("app:shell:")).ToList();   // Store apps come with the Start-menu scan
        Console.WriteLine($"store apps: {store.Count} ({store.Count(a => a.art != null)} with icons): {string.Join(", ", store.Select(a => a.name))}");
        return ok && store.Count > 0;
    }

    /// <summary>Minutes played per appid, from the Steam user's localconfig.vdf (apps/&lt;appid&gt;/Playtime). The most recently used Steam account wins.</summary>
    static Dictionary<string, int> SteamPlaytime(string steam)
    {
        var o = new Dictionary<string, int>();
        try
        {
            var ud = Path.Combine(steam, "userdata");
            var f = Directory.Exists(ud) ? Directory.GetDirectories(ud).Select(d => Path.Combine(d, @"config\localconfig.vdf")).Where(File.Exists).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault() : null;
            if (f == null) return o;
            foreach (Match m in Regex.Matches(File.ReadAllText(f), @"""(\d+)""\s*\{((?:[^{}]|\{[^{}]*\})*)\}"))   // "<appid>" { ... } with one level of nested blocks
                if (Regex.Match(m.Groups[2].Value, @"""Playtime""\s+""(\d+)""") is { Success: true } p) o[m.Groups[1].Value] = int.Parse(p.Groups[1].Value);
        }
        catch (Exception e) { Log.Write("steam playtime unreadable: " + e.Message); }
        return o;
    }

    /// <summary>Steam's cached art: librarycache/&lt;appid&gt;/[hash/]header.jpg (2024+) or the older flat &lt;appid&gt;_header.jpg. Served as https://steam.local/...</summary>
    static Art SteamArt(string appid)
    {
        if (SteamArtDir is not { } root) return new(null, null, null, null);
        var dir = Path.Combine(root, appid);
        string? Find(string file, string flat)
        {
            var hit = Directory.Exists(dir) ? Directory.EnumerateFiles(dir, file, SearchOption.AllDirectories).FirstOrDefault() : null;
            if (hit == null && File.Exists(Path.Combine(root, $"{appid}_{flat}"))) hit = Path.Combine(root, $"{appid}_{flat}");
            return hit == null ? null : "https://steam.local/" + Path.GetRelativePath(root, hit).Replace('\\', '/');
        }
        // newer Steam clients name the 600x900 cover library_capsule.jpg and the header library_header.jpg
        return new(Find("header.jpg", "header.jpg") ?? Find("library_header.jpg", "library_header.jpg"),
                   Find("library_600x900.jpg", "library_600x900.jpg") ?? Find("library_capsule.jpg", "library_capsule.jpg"),
                   Find("library_hero.jpg", "library_hero.jpg"), Find("logo.png", "logo.png"));
    }

    static IEnumerable<Game> Epic(Dictionary<string, string> t)
    {
        var dir = @"C:\ProgramData\Epic\EpicGamesLauncher\Data\Manifests";
        if (!Directory.Exists(dir)) yield break;
        foreach (var f in Directory.EnumerateFiles(dir, "*.item"))
        {
            JsonElement j;
            try { j = JsonDocument.Parse(File.ReadAllText(f)).RootElement; } catch { continue; }
            string S(string k) => j.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
            if (j.TryGetProperty("bIsIncompleteInstall", out var inc) && inc.ValueKind == JsonValueKind.True) continue;
            var id = "epic:" + S("AppName");
            t[id] = $"com.epicgames.launcher://apps/{S("CatalogNamespace")}%3A{S("CatalogItemId")}%3A{S("AppName")}?action=launch&silent=true";
            var size = j.TryGetProperty("InstallSize", out var sz) && sz.TryGetInt64(out var n) ? n : 0;
            var exe = Path.Combine(S("InstallLocation"), S("LaunchExecutable"));
            yield return new Game(id, S("DisplayName"), "epic", new(null, null, null, null, File.Exists(exe) ? Icons.Shell(exe) : null), Math.Round(size / 1e9, 1), 0);
        }
    }

    /// <summary>Games from launchers that keep no readable library (Battle.net, EA, Ubisoft, Rockstar, GOG): their Windows uninstall entries, matched by publisher.
    /// Launched through the game's own "* Launcher.exe" (Blizzard), else its exe (EA and Ubisoft hand over to their app).</summary>
    static List<Game> Uninstalled(Dictionary<string, string> t, List<Game> have)
    {
        var pubs = new (string re, string src)[] { ("Blizzard", "battlenet"), ("Electronic Arts", "ea"), ("Ubisoft", "ubisoft"), ("Rockstar", "rockstar"), ("GOG", "gog") };
        static string Key(string n) => Regex.Replace(n.ToLowerInvariant(), "[^a-z0-9]", "");
        static bool IsLauncher(string n) => Regex.IsMatch(n, @"launcher|^battle\.net$|^ea app$|connect$|sdk|social club|galaxy", RegexOptions.IgnoreCase);
        var seen = new HashSet<string>(have.Select(g => Key(g.name)));   // a Steam or Epic copy of the same game wins
        var rows = new List<(string name, string src, string? exe, string? dir, double gb)>();
        foreach (var root in new[] { Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"), Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"), Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall") })
            foreach (var k in root?.GetSubKeyNames() ?? [])
            {
                using var e = root!.OpenSubKey(k);
                if (e?.GetValue("DisplayName") is not string name || e.GetValue("Publisher") is not string pub || pubs.FirstOrDefault(p => pub.Contains(p.re, StringComparison.OrdinalIgnoreCase)).src is not { } src) continue;
                var exe = (e.GetValue("DisplayIcon") as string)?.Split(',')[0].Trim().Trim('"');
                var size = e.GetValue("EstimatedSize") is int kb ? kb : 0;   // KB
                rows.Add((name.Replace("™", "").Replace("®", "").Trim(), src, exe != null && exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(exe) ? exe : null, e.GetValue("InstallLocation") as string, Math.Round(size / 1e6, 1)));
            }
        var o = new List<Game>();
        foreach (var r in rows.Where(r => !IsLauncher(r.name) && r.exe != null && seen.Add(Key(r.name))))
        {
            var own = r.dir is { } d && Directory.Exists(d) ? Directory.EnumerateFiles(d, "* Launcher.exe").FirstOrDefault() : null;
            var id = r.src + ":" + Key(r.name);
            t[id] = own ?? r.exe!;   // single-game launchers (Tarkov etc.) are added by hand in Games
            o.Add(new Game(id, r.name, r.src, new(null, null, null, null, Icons.Shell(r.exe!)), r.gb, 0));
        }
        return o;
    }

    static List<App> StartMenu(Dictionary<string, string> t)
    {
        var roots = new[] { Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), Environment.GetFolderPath(Environment.SpecialFolder.Programs) };
        var apps = new List<App>(); var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots.Where(Directory.Exists))
            foreach (var lnk in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories).Where(p => p.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".url", StringComparison.OrdinalIgnoreCase)))
            {
                var name = Path.GetFileNameWithoutExtension(lnk);
                if (Regex.IsMatch(name, @"^(uninstall|readme|help|license)|\buninstall\b", RegexOptions.IgnoreCase) || !names.Add(name)) continue;
                var id = "app:" + lnk;
                t[id] = lnk;
                apps.Add(new App(id, name, Path.GetDirectoryName(Path.GetRelativePath(root, lnk)) ?? "", Icons.Shell(lnk)));   // the shortcut's own icon (cached in .icons after the first scan)
            }
        var sw = Stopwatch.StartNew(); int store = 0;
        try
        {
            foreach (var p in new Windows.Management.Deployment.PackageManager().FindPackagesForUser(""))   // Store apps (WhatsApp, Netflix, Xbox...) have no shortcut file
            {
                if (p.IsFramework || p.IsResourcePackage) continue;
                IReadOnlyList<Windows.ApplicationModel.Core.AppListEntry> entries;
                try { entries = p.GetAppListEntries(); } catch { continue; }
                foreach (var e in entries)
                {
                    var name = e.DisplayInfo.DisplayName;
                    if (string.IsNullOrWhiteSpace(name) || !names.Add(name)) continue;   // a shortcut of the same name wins
                    var target = @"shell:AppsFolder\" + e.AppUserModelId;   // what Start runs
                    var id = "app:" + target;
                    t[id] = target;
                    apps.Add(new App(id, name, "", Icons.Shell(target))); store++;
                }
            }
        }
        catch (Exception e) { Log.Write("store apps scan failed: " + e.Message); }
        Log.Write($"store apps: {store} in {sw.ElapsedMilliseconds} ms");
        return apps.OrderBy(a => a.name).ToList();
    }
}
