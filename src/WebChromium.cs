using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace ConsoleDesktop;

/// <summary>The Web row for Chromium browsers (Chrome, Edge, Brave, Vivaldi, Opera): the same tiles and shapes as Firefox's (BRIDGE.md web),
/// from the profile's History and Favicons databases (read from a copy) and its Bookmarks file. Chromium keeps 90 days of visits.</summary>
static partial class Web
{
    static (string key, List<Library.Item> items, Dictionary<string, string> ids)? chromeCache;
    static long Chrome(long us) => us <= 0 ? 0 : us / 1_000_000 - 11_644_473_600;   // microseconds since 1601 -> unix seconds
    static long ChromeUs(DateTime d) => (Unix(d) + 11_644_473_600) * 1_000_000;

    static List<Library.Item> Chromium(string prof, Dictionary<string, string> t)
    {
        var files = new[] { "History", "Favicons", "Bookmarks" }.Select(f => Path.Combine(prof, f)).ToArray();
        var key = string.Join("|", files.Select(f => File.Exists(f) ? $"{File.GetLastWriteTimeUtc(f).Ticks}:{new FileInfo(f).Length}" : "-"));
        if (chromeCache is { } c0 && c0.key == key) { foreach (var (k, v) in c0.ids) t[k] = v; return c0.items; }
        var copy = Path.Combine(Path.GetTempPath(), "console-desktop-chromium");
        Directory.CreateDirectory(copy);
        try
        {
            foreach (var f in files.Take(2))
                foreach (var ext in new[] { "", "-wal", "-journal" })
                    if (File.Exists(f + ext))
                    {
                        using var src = new FileStream(f + ext, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        using var dst = File.Create(Path.Combine(copy, Path.GetFileName(f) + ext));
                        src.CopyTo(dst);
                    }
            var ids = new Dictionary<string, string>();
            string Id(string url) { ids["web:" + url] = url; return "web:" + url; }
            using var h = new SqliteConnection($"Data Source={Path.Combine(copy, "History")};Pooling=False"); h.Open();
            SqliteConnection? fav = null;
            if (File.Exists(Path.Combine(copy, "Favicons"))) { fav = new SqliteConnection($"Data Source={Path.Combine(copy, "Favicons")};Pooling=False"); fav.Open(); }
            List<object[]> Q(SqliteConnection c, string sql, params (string, object)[] ps)
            {
                using var cmd = c.CreateCommand(); cmd.CommandText = sql;
                foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
                using var r = cmd.ExecuteReader(); var rows = new List<object[]>();
                while (r.Read()) { var row = new object[r.FieldCount]; r.GetValues(row); rows.Add(row); }
                return rows;
            }
            static string S(object o) => o as string ?? "";
            static long L(object o) => o is long v ? v : 0;
            var hostArt = new Dictionary<string, string?>();
            string? Icon(string url)
            {
                if (fav == null) return null;
                var b = Q(fav, "SELECT b.image_data FROM icon_mapping m JOIN favicon_bitmaps b ON b.icon_id = m.icon_id WHERE m.page_url = $u AND b.image_data IS NOT NULL ORDER BY b.width DESC LIMIT 1", ("$u", url));
                return b.Count > 0 && b[0][0] is byte[] bytes ? Icons.Save("web:" + url, Light(bytes)) : null;
            }
            string? HostIcon(string url) { var k = Host(url); if (!hostArt.TryGetValue(k, out var a)) hostArt[k] = a = Icon(url); return a; }
            var week = ChromeUs(DateTime.Today.AddDays(-6)); var today = ChromeUs(DateTime.Today);

            // sites: the hosts with the most visits (www. and bare host are one site)
            var pages = Q(h, "SELECT u.url, u.title, u.visit_count, u.last_visit_time, MIN(v.visit_time), SUM(v.visit_time > $w) FROM urls u LEFT JOIN visits v ON v.url = u.id WHERE u.hidden = 0 GROUP BY u.id", ("$w", week))
                .Where(r => Web_(S(r[0]))).Select(r => (url: S(r[0]), title: S(r[1]), visits: L(r[2]), last: L(r[3]), first: L(r[4]), week: L(r[5]), host: Host(S(r[0]))))
                .Where(p => p.host != "" && p.host != "localhost" && !Regex.IsMatch(p.host, @"^[\d.]+$|^\[")).ToList();
            var sites = pages.GroupBy(p => p.host).OrderByDescending(g => g.Sum(p => p.visits)).Where(g => g.Any(p => p.title != "")).Take(24).Select(g =>
            {
                var titled = g.Where(p => p.title != "").ToList();
                var root = new Uri(g.OrderByDescending(p => p.visits).First().url).GetLeftPart(UriPartial.Authority) + "/";
                var art = Icon(root) ?? Icon(titled.OrderByDescending(p => p.visits).First().url);
                hostArt[g.Key] = art;
                return new Library.Item(Id(root), SiteName(g.Key, titled.OrderByDescending(p => p.visits).Take(40).Select(p => Clean(p.title, p.url))), g.Key, Chrome(g.Max(p => p.last)), art, new
                {
                    kind = "site", host = g.Key, visits = g.Sum(p => p.visits), week = g.Sum(p => p.week), first = Chrome(g.Where(p => p.first > 0).Select(p => p.first).DefaultIfEmpty(0).Min()),
                    pages = titled.OrderByDescending(p => p.last).Take(5).Select(p => new { id = Id(p.url), title = Clean(p.title, p.url), at = Chrome(p.last) }).ToList(),
                });
            }).ToList();

            // history: each page once, at its latest visit (links, typed, bookmarks; no reloads, frames, form posts)
            var hist = Q(h, "SELECT u.url, u.title, MAX(v.visit_time) AS d FROM visits v JOIN urls u ON u.id = v.url WHERE v.visit_time > $since AND (v.transition & 255) IN (0, 1, 2, 5, 9) AND u.hidden = 0 GROUP BY u.id ORDER BY d DESC LIMIT 150",
                    ("$since", ChromeUs(DateTime.Today.AddDays(-30))))
                .Where(x => Web_(S(x[0]))).Select(x => new { id = Id(S(x[0])), title = Clean(S(x[1]), S(x[0])), url = S(x[0]), host = Host(S(x[0])), art = HostIcon(S(x[0])), at = Chrome(L(x[2])) }).ToList();
            var histToday = L(Q(h, "SELECT COUNT(DISTINCT url) FROM visits WHERE visit_time > $d AND (transition & 255) IN (0, 1, 2, 5, 9)", ("$d", today))[0][0]);

            // bookmarks: the bar's own, then Other and Mobile as folders
            int marks = 0;
            List<object> Tree(JsonElement node, int depth) => depth > 12 || !node.TryGetProperty("children", out var kids) ? new() : kids.EnumerateArray().Select(k =>
            {
                var type = k.TryGetProperty("type", out var ty) ? ty.GetString() : "";
                var name = k.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : "";
                if (type == "folder") return new { title = name == "" ? "Folder" : name, kids = Tree(k, depth + 1) };
                var url = k.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                if (type != "url" || !Web_(url)) return null;
                var added = k.TryGetProperty("date_added", out var da) && long.TryParse(da.GetString(), out var dv) ? Chrome(dv) : 0;
                return (object)new { id = Id(url), title = Clean(name, url), url, host = Host(url), art = Icon(url) ?? HostIcon(url), at = added, n = ++marks };
            }).Where(x => x != null).Cast<object>().ToList();
            var tree = new List<object>();
            try
            {
                using var bm = JsonDocument.Parse(File.ReadAllText(files[2]));
                var roots = bm.RootElement.GetProperty("roots");
                if (roots.TryGetProperty("bookmark_bar", out var bar)) tree = Tree(bar, 0);
                foreach (var (g, name) in new[] { ("other", "Other bookmarks"), ("synced", "Mobile bookmarks") })
                    if (roots.TryGetProperty(g, out var r) && Tree(r, 0) is { Count: > 0 } sub) tree.Add(new { title = name, kids = sub });
            }
            catch (Exception e) when (e is not OutOfMemoryException) { if (File.Exists(files[2])) Log.Write($"{B.name} bookmarks unreadable: {e.Message}"); }
            fav?.Dispose();

            var items = new List<Library.Item>
            {
                new("webplace:history", "History", B.name, hist.Count > 0 ? hist[0].at : 0, null, new { kind = "history", today = histToday, pages = hist }),
                new("webplace:bookmarks", "Bookmarks", B.name, 0, null, new { kind = "bookmarks", count = marks, tree }),
            };
            items.AddRange(sites);
            chromeCache = (key, items, ids);
            foreach (var (k, v) in ids) t[k] = v;
            return items;
        }
        catch (Exception e) { Log.Write($"{B.name} history unreadable: {e.Message}"); return new(); }
        finally { SqliteConnection.ClearAllPools(); try { Directory.Delete(copy, true); } catch { } }
    }
}
