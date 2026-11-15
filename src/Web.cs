using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using Microsoft.Data.Sqlite;
using static ConsoleDesktop.Native;

namespace ConsoleDesktop;

/// <summary>The Web row, read from the browser picked in config.json "web.browser" (Cfg.Browser).
/// Firefox family: open tabs and recently closed ones (its session file), history, bookmarks, and one tile per site you visit most (its places database).
/// Chromium family (Chrome, Edge, Brave, Vivaldi, Opera): history, bookmarks and sites (History + Bookmarks + Favicons in the profile).
/// ponytail: no open tabs for Chromium (its session files are an undocumented command log); add them through UI Automation of the tab strip if missed.
/// Databases are read from a copy, so the browser's open files are never touched; the copy is only made again when the browser wrote to it.</summary>
static partial class Web
{
    /// <summary>A browser the Web row can read. dir: its profiles' root under %APPDATA% (gecko) or %LOCALAPPDATA% (chromium); exe: its App Paths name.</summary>
    public record Browser(string id, string name, bool gecko, string dir, string exe, string[] progId, string[] procs)
    {
        /// <summary>The profile to read: the one written to last (gecko: Profiles\*; chromium: Default / Profile N), null when the browser has none here.</summary>
        public string? Root
        {
            get
            {
                try
                {
                    var baseDir = Path.Combine(Environment.GetFolderPath(gecko ? Environment.SpecialFolder.ApplicationData : Environment.SpecialFolder.LocalApplicationData), dir);
                    if (!Directory.Exists(baseDir)) return null;
                    var probe = gecko ? "places.sqlite" : "History";
                    if (File.Exists(Path.Combine(baseDir, probe))) return baseDir;   // Opera: the folder is the profile
                    return Directory.GetDirectories(baseDir).Where(d => File.Exists(Path.Combine(d, probe))).OrderByDescending(d => File.GetLastWriteTimeUtc(Path.Combine(d, probe))).FirstOrDefault();
                }
                catch { return null; }
            }
        }
        public bool Running => procs.Any(p => Process.GetProcessesByName(p).Length > 0);
        /// <summary>Its exe from Windows' App Paths (HKCU, then HKLM), null when not registered.</summary>
        public string? Exe => new[] { Microsoft.Win32.Registry.CurrentUser, Microsoft.Win32.Registry.LocalMachine }
            .Select(h => { using var k = h.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\" + exe); return k?.GetValue(null) as string; })
            .Select(p => p?.Trim('"')).FirstOrDefault(p => p != null && File.Exists(p));
    }
    public static readonly Browser[] Browsers =
    {
        new("firefox", "Firefox", true, @"Mozilla\Firefox\Profiles", "firefox.exe", new[] { "firefox" }, new[] { "firefox" }),
        new("chrome", "Chrome", false, @"Google\Chrome\User Data", "chrome.exe", new[] { "chrome" }, new[] { "chrome" }),
        new("edge", "Edge", false, @"Microsoft\Edge\User Data", "msedge.exe", new[] { "msedge" }, new[] { "msedge" }),
        new("brave", "Brave", false, @"BraveSoftware\Brave-Browser\User Data", "brave.exe", new[] { "brave" }, new[] { "brave" }),
        new("vivaldi", "Vivaldi", false, @"Vivaldi\User Data", "vivaldi.exe", new[] { "vivaldi" }, new[] { "vivaldi" }),
        new("opera", "Opera", false, @"..\Roaming\Opera Software\Opera Stable", "opera.exe", new[] { "opera" }, new[] { "opera" }),
        new("operagx", "Opera GX", false, @"..\Roaming\Opera Software\Opera GX Stable", "opera.exe", new[] { "operagx" }, new[] { "opera" }),
        new("librewolf", "LibreWolf", true, @"librewolf\Profiles", "librewolf.exe", new[] { "librewolf" }, new[] { "librewolf" }),
        new("waterfox", "Waterfox", true, @"Waterfox\Profiles", "waterfox.exe", new[] { "waterfox" }, new[] { "waterfox" }),
        new("zen", "Zen", true, @"zen\Profiles", "zen.exe", new[] { "zen" }, new[] { "zen" }),
    };
    static Browser B => Cfg.Browser;

    /// <summary>Opens a page in the picked browser (else Windows' default); http and https only.</summary>
    public static void Open(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme is not ("https" or "http")) { Log.Write("openUrl refused: " + url); return; }
        try
        {
            if (B.Exe is { } exe) { var si = new ProcessStartInfo(exe) { UseShellExecute = false }; si.ArgumentList.Add(u.AbsoluteUri); Process.Start(si); }
            else Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception e) { Log.Write($"open {url} failed: {e.Message}"); }
    }

    record Tab(string url, string title);
    static Dictionary<string, (Tab tab, string winTitle, int pos)> tabs = new();   // "tab:<window>:<n>" -> what Switch needs
    static (string key, List<Library.Item> items, Dictionary<string, string> ids, Dictionary<string, string?> hostArt)? cache;

    static long Unix(DateTime t) => new DateTimeOffset(t.ToUniversalTime()).ToUnixTimeSeconds();
    static string Host(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? Regex.Replace(u.Host, @"^www\.", "") : "";
    /// <summary>"(5) YouTube" -> "YouTube": unread counts sites put in front of their titles</summary>
    static string Clean(string? title, string url) => title is { Length: > 0 } ? Regex.Replace(title, @"^\(\d+\+?\)\s*", "") : Host(url) is { Length: > 0 } h ? h : url;
    static bool Web_(string url) => url.StartsWith("https://") || url.StartsWith("http://");

    public static List<Library.Item> Scan(Dictionary<string, string> t)
    {
        var prof = B.Root;
        if (prof == null) return new();
        if (!B.gecko) return Chromium(prof, t);
        var running = B.Running;
        var db = Places(prof);
        foreach (var (k, v) in db.ids) t[k] = v;
        var list = new List<Library.Item>();
        using (var s = Session(prof, running)) list.AddRange(Tabs(s, running, db.hostArt, t));
        list.AddRange(db.items);
        return list;
    }

    // ---- the session file: open and recently closed tabs ----

    /// <summary>Firefox's session (mozLz4: "mozLz40\0", the size, one LZ4 block). Running, it rewrites recovery.jsonlz4 every 15 s; closed, sessionstore.jsonlz4 holds what comes back.</summary>
    static JsonDocument? Session(string prof, bool running)
    {
        var names = running ? new[] { @"sessionstore-backups\recovery.jsonlz4", @"sessionstore-backups\recovery.baklz4" } : new[] { "sessionstore.jsonlz4", @"sessionstore-backups\recovery.jsonlz4" };
        foreach (var n in names)
            try
            {
                var f = Path.Combine(prof, n);
                if (!File.Exists(f)) continue;
                byte[] b;
                using (var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) { b = new byte[fs.Length]; fs.ReadExactly(b); }
                if (b.Length < 12 || System.Text.Encoding.ASCII.GetString(b, 0, 7) != "mozLz40") continue;
                return JsonDocument.Parse(Lz4(b.AsSpan(12), BitConverter.ToInt32(b, 8)));
            }
            catch (Exception e) { Log.Write($"firefox session {n} unreadable: {e.Message}"); }
        return null;
    }

    /// <summary>LZ4 block decoding: literals, then a match copied from earlier output (it may overlap itself)</summary>
    internal static byte[] Lz4(ReadOnlySpan<byte> s, int size)
    {
        var o = new byte[size]; int i = 0, n = 0;
        while (i < s.Length)
        {
            int tok = s[i++], len = tok >> 4; byte x;
            if (len == 15) do len += x = s[i++]; while (x == 255);
            s.Slice(i, len).CopyTo(o.AsSpan(n)); i += len; n += len;
            if (i >= s.Length) break;
            int off = s[i] | s[i + 1] << 8, m = tok & 15; i += 2;
            if (m == 15) do m += x = s[i++]; while (x == 255);
            for (m += 4; m > 0; m--, n++) o[n] = o[n - off];
        }
        return o;
    }

    static IEnumerable<Library.Item> Tabs(JsonDocument? s, bool running, Dictionary<string, string?> hostArt, Dictionary<string, string> t)
    {
        tabs = new();
        if (s == null || !s.RootElement.TryGetProperty("windows", out var wins)) yield break;
        var open = new List<object>(); var closed = new List<object>(); var seen = new HashSet<string>();
        long last = 0, closedLast = 0; int wi = 0;
        string? Art(JsonElement tab, string url) => tab.TryGetProperty("image", out var im) && im.ValueKind == JsonValueKind.String && DataIcon(im.GetString()!) is { } a ? a : hostArt.GetValueOrDefault(Host(url));
        static (string url, string title)? Shown(JsonElement st)   // the page a tab shows now: entries[index - 1]
        {
            if (!st.TryGetProperty("entries", out var en) || en.GetArrayLength() == 0) return null;
            var k = st.TryGetProperty("index", out var ix) ? Math.Clamp(ix.GetInt32(), 1, en.GetArrayLength()) - 1 : en.GetArrayLength() - 1;
            var e = en[k]; var url = e.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
            return (url, e.TryGetProperty("title", out var ti) ? ti.GetString() ?? "" : "");
        }
        foreach (var w in wins.EnumerateArray())
        {
            if (!w.TryGetProperty("tabs", out var ts)) continue;
            var sel = w.TryGetProperty("selected", out var se) ? se.GetInt32() - 1 : 0;
            var all = ts.EnumerateArray().ToList();
            var winTitle = sel >= 0 && sel < all.Count && Shown(all[sel]) is { } st ? Clean(st.title, st.url) : "";
            int pos = 0;
            for (int k = 0; k < all.Count; k++)
            {
                var tab = all[k];
                if (tab.TryGetProperty("hidden", out var hd) && hd.GetBoolean()) continue;   // hidden by an extension: not in the tab strip either
                if (Shown(tab) is not { } p) { pos++; continue; }
                var at = tab.TryGetProperty("lastAccessed", out var la) ? la.GetInt64() / 1000 : 0; last = Math.Max(last, at);
                var id = running ? $"tab:{wi}:{pos}" : "web:" + p.url;
                if (running) tabs[id] = (new(p.url, p.title), winTitle, pos); else if (Web_(p.url)) t[id] = p.url;
                if (open.Count < 200) open.Add(new { id, title = Clean(p.title, p.url), url = p.url, host = Host(p.url), art = Art(tab, p.url), at, pinned = tab.TryGetProperty("pinned", out var pn) && pn.GetBoolean(), cur = k == sel, win = wi });
                pos++;
            }
            if (w.TryGetProperty("_closedTabs", out var ct))
                foreach (var c in ct.EnumerateArray())
                    if (c.TryGetProperty("state", out var st2) && Shown(st2) is { } p && Web_(p.url) && seen.Add(p.url) && closed.Count < 40)
                    {
                        t["web:" + p.url] = p.url;
                        var at = c.TryGetProperty("closedAt", out var ca) ? ca.GetInt64() / 1000 : 0; closedLast = Math.Max(closedLast, at);
                        closed.Add(new { id = "web:" + p.url, title = Clean(p.title, p.url), url = p.url, host = Host(p.url), art = Art(c, p.url), at });
                    }
            wi++;
        }
        yield return new("webplace:tabs", "Open tabs", B.name, last, null, new { kind = "tabs", running, windows = wi, tabs = open });
        yield return new("webplace:closed", "Recently closed", B.name, closedLast, null, new { kind = "closed", tabs = closed });
    }

    /// <summary>A tab's icon as the session keeps it (a data: url) saved once by its content</summary>
    static string? DataIcon(string src)
    {
        var m = Regex.Match(src, @"^data:image/([a-z+.-]+);base64,(.+)$", RegexOptions.Singleline);
        if (!m.Success) return null;
        try { return Icons.Save(Light(Convert.FromBase64String(m.Groups[2].Value)), m.Groups[1].Value switch { "svg+xml" => ".svg", "x-icon" or "vnd.microsoft.icon" => ".ico", "jpeg" => ".jpg", var e => "." + e }); }
        catch { return null; }
    }

    /// <summary>Switches Firefox to an open tab through UI Automation (Firefox's tab strip selects the tab itself: no keys are typed into
    /// Firefox, so nothing can land on the page). Tab found by its title, the window's own first; else by its place in the strip.
    /// Firefox closed, or no tab matches: the page opens in a new tab instead.</summary>
    public static bool Switch(string id)
    {
        if (!tabs.TryGetValue(id, out var x)) { Log.Write($"tab switch refused: unknown {id}"); return false; }
        var wins = new List<IntPtr>();
        EnumWindows((h, _) => { if (IsWindowVisible(h) && ClassOf(h) == "MozillaWindowClass" && GetWindow(h, GW_OWNER) == IntPtr.Zero && TitleOf(h) != "") wins.Add(h); return true; }, IntPtr.Zero);
        if (wins.Count == 0) { Open(x.tab.url); Log.Write($"tab {id}: Firefox not open, opened the page"); return true; }
        new Thread(() =>
        {
            try
            {
                var home = wins.FirstOrDefault(h => x.winTitle != "" && TitleOf(h).Contains(x.winTitle));
                if (home == IntPtr.Zero && wins.Count == 1) home = wins[0];
                var want = Clean(x.tab.title, x.tab.url);
                (IntPtr w, AutomationElement t)? hit = null, byPos = null;
                foreach (var h in wins.OrderBy(h => h == home ? 0 : 1))
                {
                    var strip = AutomationElement.FromHandle(h).FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "tabbrowser-tabs"));
                    if (strip == null) continue;
                    var all = strip.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem)).Cast<AutomationElement>().ToList();
                    var same = all.Select((t, i) => (t, i)).Where(p => Clean(p.t.Current.Name, "") == want).ToList();
                    if (same.Count > 0) { hit = (h, (same.FirstOrDefault(p => h == home && p.i == x.pos).t ?? same[0].t)); break; }
                    if (h == home && x.pos < all.Count) byPos = (h, all[x.pos]);
                }
                if ((hit ?? byPos) is not { } go) { Open(x.tab.url); Log.Write($"tab {id}: no tab named \"{want}\", opened the page"); return; }
                ((SelectionItemPattern)go.t.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
                Thread.Sleep(150);   // the menu steps back first
                if (IsIconic(go.w)) ShowWindow(go.w, 9 /*SW_RESTORE*/);
                ForceForeground(go.w);
                Log.Write($"tab {id}: selected \"{go.t.Current.Name}\"{(hit == null ? " (by place, title changed)" : "")}");
            }
            catch (Exception e) { Log.Write($"tab {id}: switch failed: {e.Message}"); }
        }) { IsBackground = true }.Start();
        return true;
    }

    // ---- the places database: sites, history, bookmarks ----

    static (List<Library.Item> items, Dictionary<string, string> ids, Dictionary<string, string?> hostArt) Places(string prof)
    {
        var db = Path.Combine(prof, "places.sqlite");
        var key = string.Join("|", new[] { db, db + "-wal" }.Select(f => File.Exists(f) ? $"{File.GetLastWriteTimeUtc(f).Ticks}:{new FileInfo(f).Length}" : "-"));
        if (cache is { } c0 && c0.key == key) return (c0.items, c0.ids, c0.hostArt);
        var copy = Path.Combine(Path.GetTempPath(), "console-desktop-places");
        Directory.CreateDirectory(copy);
        try
        {
            foreach (var f in new[] { db, Path.Combine(prof, "favicons.sqlite") })
                foreach (var ext in new[] { "", "-wal" })
                    if (File.Exists(f + ext))
                    {
                        using var src = new FileStream(f + ext, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        using var dst = File.Create(Path.Combine(copy, Path.GetFileName(f) + ext));
                        src.CopyTo(dst);
                    }
            var ids = new Dictionary<string, string>(); var hostArt = new Dictionary<string, string?>();
            string Id(string url) { ids["web:" + url] = url; return "web:" + url; }
            using var p = new SqliteConnection($"Data Source={Path.Combine(copy, "places.sqlite")};Pooling=False"); p.Open();
            SqliteConnection? fav = null;
            if (File.Exists(Path.Combine(copy, "favicons.sqlite"))) { fav = new SqliteConnection($"Data Source={Path.Combine(copy, "favicons.sqlite")};Pooling=False"); fav.Open(); }
            string? Icon(string url) => fav != null && Favicon(fav, url) is { } b ? Icons.Save("web:" + url, Light(b)) : null;
            string? HostIcon(string url) { var h = Host(url); if (!hostArt.TryGetValue(h, out var a)) hostArt[h] = a = Icon(url); return a; }
            List<object[]> Q(string sql, params (string, object)[] ps)
            {
                using var cmd = p.CreateCommand(); cmd.CommandText = sql;
                foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
                using var r = cmd.ExecuteReader(); var rows = new List<object[]>();
                while (r.Read()) { var row = new object[r.FieldCount]; r.GetValues(row); rows.Add(row); }
                return rows;
            }
            static string S(object o) => o as string ?? "";
            static long L(object o) => o is long v ? v : 0;
            long Us(DateTime d) => Unix(d) * 1_000_000;
            var today = Us(DateTime.Today); var week = Us(DateTime.Today.AddDays(-6));

            // sites: Firefox's own ranking of origins; www. and bare host are one site
            var sites = new List<Library.Item>();
            var groups = new List<(string host, List<long> origins, string root)>();
            foreach (var r in Q("SELECT id, prefix, host FROM moz_origins WHERE prefix IN ('https://', 'http://') AND frecency > 0 ORDER BY frecency DESC LIMIT 120"))
            {
                var h = Regex.Replace(S(r[2]), @"^www\.", "");
                if (h is "localhost" || Regex.IsMatch(h, @"^[\d.]+$|^\[")) continue;   // the desktop's own sign-in pages, routers
                var g = groups.FindIndex(x => x.host == h);
                if (g >= 0) groups[g].origins.Add(L(r[0])); else if (groups.Count < 40) groups.Add((h, new() { L(r[0]) }, S(r[1]) + S(r[2]) + "/"));
            }
            foreach (var (host, origins, root) in groups)
            {
                var o = string.Join(",", origins);
                var top = Q($"SELECT url, title FROM moz_places WHERE origin_id IN ({o}) AND hidden = 0 AND title <> '' ORDER BY frecency DESC LIMIT 40");
                if (top.Count == 0) continue;   // only ever a redirect (gmail.com -> mail.google.com)
                if (sites.Count == 24) break;
                var sum = Q($"SELECT SUM(visit_count), MAX(last_visit_date), (SELECT COUNT(*) FROM moz_historyvisits v JOIN moz_places q ON q.id = v.place_id WHERE q.origin_id IN ({o}) AND v.visit_date > $w), " +
                            $"(SELECT MIN(v.visit_date) FROM moz_historyvisits v JOIN moz_places q ON q.id = v.place_id WHERE q.origin_id IN ({o})) FROM moz_places WHERE origin_id IN ({o})", ("$w", week))[0];
                var pages = Q($"SELECT url, title, last_visit_date FROM moz_places WHERE origin_id IN ({o}) AND hidden = 0 AND title <> '' AND last_visit_date > 0 ORDER BY last_visit_date DESC LIMIT 5")
                    .Select(x => new { id = Id(S(x[0])), title = Clean(S(x[1]), S(x[0])), at = L(x[2]) / 1_000_000 }).ToList();
                var art = Icon(root) ?? (top.Count > 0 ? Icon(S(top[0][0])) : null);
                hostArt[host] = art;
                sites.Add(new(Id(root), SiteName(host, top.Select(x => Clean(S(x[1]), S(x[0])))), host, L(sum[1]) / 1_000_000, art,
                    new { kind = "site", host, visits = L(sum[0]), week = L(sum[2]), first = L(sum[3]) / 1_000_000, pages }));
            }

            // history: each page once, at its latest visit (links, typed, bookmarks; not redirects, reloads, frames or downloads)
            var hist = Q("SELECT p.url, p.title, MAX(v.visit_date) AS d FROM moz_historyvisits v JOIN moz_places p ON p.id = v.place_id " +
                         "WHERE v.visit_date > $since AND v.visit_type IN (1, 2, 3, 8) AND p.hidden = 0 GROUP BY p.id ORDER BY d DESC LIMIT 150", ("$since", Us(DateTime.Today.AddDays(-30))))
                .Where(x => Web_(S(x[0]))).Select(x => new { id = Id(S(x[0])), title = Clean(S(x[1]), S(x[0])), url = S(x[0]), host = Host(S(x[0])), art = HostIcon(S(x[0])), at = L(x[2]) / 1_000_000 }).ToList();
            var histToday = L(Q("SELECT COUNT(DISTINCT place_id) FROM moz_historyvisits WHERE visit_date > $d AND visit_type IN (1, 2, 3, 8)", ("$d", today))[0][0]);

            // bookmarks: the toolbar's own, then the menu and Other bookmarks as folders
            var rows = Q("SELECT b.id, b.parent, b.type, b.title, p.url, b.dateAdded FROM moz_bookmarks b LEFT JOIN moz_places p ON p.id = b.fk ORDER BY b.parent, b.position");
            var kids = rows.GroupBy(r => L(r[1])).ToDictionary(g => g.Key, g => g.ToList());
            long Root(string guid) => Q("SELECT id FROM moz_bookmarks WHERE guid = $g", ("$g", guid)) is { Count: > 0 } r ? L(r[0][0]) : -1;
            int marks = 0;
            List<object> Tree(long parent, int depth) => depth > 12 ? new() : kids.GetValueOrDefault(parent, new()).Select(r =>
                L(r[2]) == 2 ? new { title = S(r[3]) == "" ? "Folder" : S(r[3]), kids = Tree(L(r[0]), depth + 1) }
                : L(r[2]) == 1 && Web_(S(r[4])) ? (object)new { id = Id(S(r[4])), title = Clean(S(r[3]), S(r[4])), url = S(r[4]), host = Host(S(r[4])), art = Icon(S(r[4])) ?? HostIcon(S(r[4])), at = L(r[5]) / 1_000_000, n = ++marks } : null)
                .Where(x => x != null).Cast<object>().ToList();
            var tree = Tree(Root("toolbar_____"), 0);
            foreach (var (g, name) in new[] { ("menu________", "Bookmarks menu"), ("unfiled_____", "Other bookmarks"), ("mobile______", "Mobile bookmarks") })
                if (Tree(Root(g), 0) is { Count: > 0 } sub) tree.Add(new { title = name, kids = sub });
            fav?.Dispose();

            var items = new List<Library.Item>
            {
                new("webplace:history", "History", B.name, hist.Count > 0 ? hist[0].at : 0, null, new { kind = "history", today = histToday, pages = hist }),
                new("webplace:bookmarks", "Bookmarks", B.name, 0, null, new { kind = "bookmarks", count = marks, tree }),
            };
            items.AddRange(sites);
            cache = (key, items, ids, hostArt);
            return (items, ids, hostArt);
        }
        finally { try { Directory.Delete(copy, true); } catch { } }
    }

    /// <summary>The site's own name from its page titles: a part of a title that is the host's name ("(5) YouTube", "Home / X"), else the part most titles end with
    /// ("… · GitHub"), else a short one-part title, else the host. ponytail: a title heuristic; Firefox's site_name column is empty here.</summary>
    internal static string SiteName(string host, IEnumerable<string> titles)
    {
        var parts = titles.Select(x => Regex.Split(x, @"\s+[-–—|/·:•»]\s+").Select(s => s.Trim()).Where(s => s.Length > 0).ToArray()).Where(a => a.Length > 0).ToList();
        var labels = host.Split('.'); var label = labels.Length >= 2 ? labels[^2] : host;
        static string Sq(string s) => Regex.Replace(s.ToLowerInvariant(), "[^a-z0-9]", "");
        var all = parts.SelectMany(a => a).Where(s => s.Length <= 30).ToList();
        if (all.FirstOrDefault(s => Sq(s) == Sq(label)) is { } exact) return exact;
        if (parts.Where(a => a.Length > 1).GroupBy(a => a[^1]).Where(g => g.Count() >= 2 && g.Key.Length <= 30).OrderByDescending(g => g.Count()).FirstOrDefault() is { } common) return common.Key;
        if (label.Length >= 4 && all.FirstOrDefault(s => Sq(s).Contains(Sq(label))) is { } has) return has;
        if (parts.FirstOrDefault() is { Length: 1 } one && one[0].Length <= 24) return one[0];
        return host;
    }

    /// <summary>An SVG favicon's dark-mode style taken out (GitHub's turns white when the system is dark): every icon sits on a white plate</summary>
    static byte[] Light(byte[] b)
    {
        if (b.Length > 200_000) return b;
        var x = System.Text.Encoding.UTF8.GetString(b);
        if (!x.Contains("<svg") || !x.Contains("prefers-color-scheme")) return b;
        return System.Text.Encoding.UTF8.GetBytes(Regex.Replace(x, @"@media\s*\(\s*prefers-color-scheme\s*:\s*dark\s*\)\s*\{(?:[^{}]*\{[^{}]*\})*[^{}]*\}", ""));
    }

    /// <summary>The biggest icon Firefox stored for this page, else the site's root icon (favicon.ico of the same host).</summary>
    static byte[]? Favicon(SqliteConnection c, string url)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT i.data FROM moz_pages_w_icons p JOIN moz_icons_to_pages ip ON ip.page_id = p.id JOIN moz_icons i ON i.id = ip.icon_id " +
                          "WHERE p.page_url = $u AND i.data IS NOT NULL ORDER BY i.width DESC LIMIT 1";
        cmd.Parameters.AddWithValue("$u", url);
        if (cmd.ExecuteScalar() is byte[] b) return b;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return null;
        cmd.CommandText = "SELECT data FROM moz_icons WHERE root = 1 AND data IS NOT NULL AND (icon_url LIKE $h1 OR icon_url LIKE $h2) ORDER BY width DESC LIMIT 1";
        cmd.Parameters.Clear();
        cmd.Parameters.AddWithValue("$h1", $"{u.Scheme}://{u.Host}/%");
        cmd.Parameters.AddWithValue("$h2", $"{u.Scheme}://www.{u.Host.Replace("www.", "")}/%");
        return cmd.ExecuteScalar() as byte[];
    }
}
