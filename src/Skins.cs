using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ConsoleDesktop;

/// <summary>Winamp skins the user adds (Settings › Winamp › Add a skin, or a .wsz dropped on the desktop): the same install as
/// tools/skin-hunt/install.ps1 does for the built-in ones. The .wsz (a zip) is unpacked, its bitmaps saved as PNG unchanged, viscolor.txt read
/// for the visualizer, region.txt and pledit.txt kept, and any bitmap the skin leaves out is copied from a built-in skin (Winamp falls back
/// to its base skin the same way). They live in web/assets/skins/u-*/ and are listed in web/assets/skins/user.json, which the page merges
/// into window.SKINS. The built-in ones (skins.js) are never touched.</summary>
static class Skins
{
    static string Dir => Path.Combine(Paths.Web, "assets", "skins");
    static string UserFile => Path.Combine(Dir, "user.json");
    static readonly string[] Need = { "main", "titlebar", "cbuttons", "playpaus", "posbar", "shufrep", "text", "volume", "monoster", "numbers" };
    const string Fallback = "vista";   // a built-in skin with every bitmap

    /// <summary>Installs one .wsz (a file path, or its bytes when it was dropped on the page). Returns the entry the page adds to SKINS.</summary>
    public static JsonObject Add(string? path, byte[]? data, string? fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName ?? path ?? "Skin").Replace('_', ' ').Trim();
        if (name.Length == 0) name = "Skin";
        var id = "u-" + new string(name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        if (id.Length > 40) id = id[..40];
        var list = Load();
        for (int n = 2; list.ContainsKey(id); n++) id = id.TrimEnd("0123456789-".ToCharArray()) + "-" + n;   // the same name twice: keep both
        var outDir = Path.Combine(Dir, id);
        Directory.CreateDirectory(outDir);
        try
        {
            using var zip = data != null ? new ZipArchive(new MemoryStream(data)) : ZipFile.OpenRead(path!);
            int bitmaps = 0;
            foreach (var e in zip.Entries)
            {
                if (e.Length == 0 || e.Length > 8_000_000) continue;
                var n = Path.GetFileName(e.FullName).ToLowerInvariant();
                if (n.EndsWith(".bmp"))
                {
                    using var s = e.Open(); using var ms = new MemoryStream(); s.CopyTo(ms); ms.Position = 0;
                    using var img = Image.FromStream(ms);
                    img.Save(Path.Combine(outDir, Path.ChangeExtension(n, ".png")), System.Drawing.Imaging.ImageFormat.Png); bitmaps++;
                }
                else if (n is "viscolor.txt" or "region.txt" or "pledit.txt") e.ExtractToFile(Path.Combine(outDir, n), true);
            }
            if (bitmaps == 0 || !File.Exists(Path.Combine(outDir, "main.png"))) throw new InvalidDataException("that is not a classic Winamp skin (no main.bmp inside; modern .wal skins don't work here)");
            foreach (var b in Need)   // what the skin leaves out comes from a built-in one
            {
                string to = Path.Combine(outDir, b + ".png"), from = Path.Combine(Dir, Fallback, b + ".png");
                if (!File.Exists(to) && File.Exists(from)) File.Copy(from, to);
            }
            var vis = new JsonArray();
            var vc = Path.Combine(outDir, "viscolor.txt");
            if (File.Exists(vc))
                foreach (var l in File.ReadLines(vc))
                    if (System.Text.RegularExpressions.Regex.Match(l, @"^\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)") is { Success: true } m) vis.Add($"rgb({m.Groups[1]},{m.Groups[2]},{m.Groups[3]})");
            if (vis.Count == 0) foreach (var c in new[] { "rgb(0,0,0)", "rgb(24,33,41)" }.Concat(Enumerable.Range(0, 16).Select(i => $"rgb({239 - i * 8},{49 + i * 12},16)")).Concat(Enumerable.Repeat("rgb(150,150,150)", 6))) vis.Add(c);   // Winamp's own default colours, roughly
            var entry = new JsonObject
            {
                ["name"] = name, ["label"] = new string(name.ToUpperInvariant().Where(char.IsLetterOrDigit).Take(6).ToArray()) is { Length: > 0 } lb ? lb : "SKIN",
                ["nums"] = File.Exists(Path.Combine(outDir, "nums_ex.png")) ? "nums_ex.png" : "numbers.png",
                ["balance"] = File.Exists(Path.Combine(outDir, "balance.png")) ? "balance.png" : "volume.png",
                ["vis"] = vis, ["user"] = true, ["added"] = DateTimeOffset.Now.ToUnixTimeSeconds(),
            };
            list[id] = entry; Save(list);
            Log.Write($"skins: added {id} ({bitmaps} bitmaps)");
            return new JsonObject { ["id"] = id, ["skin"] = entry.DeepClone() };
        }
        catch { try { Directory.Delete(outDir, true); } catch { } throw; }
    }

    public static void Remove(string id)
    {
        if (!id.StartsWith("u-") || id.Contains("..") || id.Contains('/') || id.Contains('\\')) throw new ArgumentException("only skins you added can be removed");
        var list = Load(); list.Remove(id); Save(list);
        try { Directory.Delete(Path.Combine(Dir, id), true); } catch { }
        Log.Write("skins: removed " + id);
    }

    static JsonObject Load() { try { return File.Exists(UserFile) ? JsonNode.Parse(File.ReadAllText(UserFile)) as JsonObject ?? new() : new(); } catch { return new(); } }
    static void Save(JsonObject o) => File.WriteAllText(UserFile, o.ToJsonString(new JsonSerializerOptions(JsonSerializerOptions.Default) { WriteIndented = true }));
}
