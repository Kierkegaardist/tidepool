using System.IO.Compression;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ConsoleDesktop;

/// <summary>A DAW the Studio tab can show. ext: its project files (first = the main one); dirs: where its projects usually live under Documents/Music
/// (config.json "studio.projects" wins); installed: a sign it is on this PC.</summary>
record Daw(string id, string name, string[] ext, string[] dirs, Func<bool> installed);

/// <summary>The Studio tab and Music's "Your tracks", for whichever DAW config.json "studio.daw" names (Cfg.Daw): one tile per project folder with
/// its versions, bounced renders and backups, plus what the project file says about itself where it can be read (FL .flp, Ableton .als, REAPER .rpp).
/// Ids keep the "flp:" prefix for every DAW's project (the page's contract, BRIDGE.md), whatever the file type.</summary>
static class Studio
{
    static string Docs => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    static string Music => Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
    static string Pf => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    static bool AnyDir(string root, string pattern) { try { return Directory.Exists(root) && Directory.EnumerateDirectories(root, pattern).Any(); } catch { return false; } }

    public static readonly Daw[] Daws =
    {
        new("fl", "FL Studio", new[] { ".flp" }, new[] { @"Image-Line\FL Studio\Projects" }, () => Registry.CurrentUser.OpenSubKey(@"Software\Image-Line") != null),
        new("ableton", "Ableton Live", new[] { ".als" }, new[] { "Ableton", @"Ableton\Projects", "Ableton Projects" }, () => Directory.Exists(@"C:\ProgramData\Ableton")),
        new("reaper", "REAPER", new[] { ".rpp" }, new[] { "REAPER Media", "REAPER Projects" }, () => File.Exists(Path.Combine(Pf, @"REAPER (x64)\reaper.exe")) || File.Exists(Path.Combine(Pf, @"REAPER\reaper.exe")) || Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "REAPER"))),
        new("bitwig", "Bitwig Studio", new[] { ".bwproject" }, new[] { @"Bitwig Studio\Projects", "Bitwig Studio" }, () => AnyDir(Pf, "Bitwig Studio*")),
        new("cubase", "Cubase", new[] { ".cpr" }, new[] { "Cubase Projects", "Steinberg" }, () => AnyDir(Path.Combine(Pf, "Steinberg"), "Cubase*")),
        new("studioone", "Studio One", new[] { ".song" }, new[] { @"Studio One\Songs" }, () => AnyDir(Path.Combine(Pf, "PreSonus"), "Studio One*")),
        new("logic", "Logic Pro", new[] { ".logicx" }, Array.Empty<string>(), () => false),   // macOS only; listed so a shared config.json still reads
    };

    /// <summary>Where this DAW's projects live: config.json "studio.projects", else the first of its usual folders that exists.</summary>
    public static string? ProjectsDir(Daw d) =>
        Cfg.Get("studio.projects") is { } p && Directory.Exists(p) ? Places.Real(p)
        : d.dirs.SelectMany(x => new[] { Path.Combine(Docs, x), Path.Combine(Music, x) }).Where(Directory.Exists).Select(Places.Real).FirstOrDefault();

    static readonly string[] Audio = { ".wav", ".mp3", ".ogg", ".flac", ".aif", ".aiff" };
    static long Unix(DateTime t) => new DateTimeOffset(t.ToUniversalTime()).ToUnixTimeSeconds();
    static bool IsProject(Daw d, string f) => d.ext.Contains(Path.GetExtension(f).ToLowerInvariant());
    static bool IsBackupDir(string dir) => Regex.IsMatch(Path.GetFileName(dir), @"^(Backup|Backups|Autosave|Auto-Saves|\.backup)$", RegexOptions.IgnoreCase);

    /// <summary>Project files the DAW itself lists as recent (FL's registry MRU, REAPER's ini), else the newest under the projects folder.</summary>
    static IEnumerable<string> Recent(Daw d)
    {
        if (d.id == "fl")
        {
            using var il = Registry.CurrentUser.OpenSubKey(@"Software\Image-Line");
            var fl = il?.GetSubKeyNames().Where(n => n.StartsWith("FL Studio ")).OrderByDescending(n => int.TryParse(n[10..], out var v) ? v : 0).FirstOrDefault();
            using var mru = fl == null ? null : il!.OpenSubKey(fl + @"\MRU");
            var list = (mru?.GetValueNames() ?? Array.Empty<string>()).OrderBy(n => int.TryParse(n, out var v) ? v : 99).Select(n => mru!.GetValue(n) as string).OfType<string>().Where(File.Exists).ToList();
            if (list.Count > 0) return list;
        }
        if (d.id == "reaper" && Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"REAPER\reaper.ini") is var ini && File.Exists(ini))
        {
            var list = Regex.Matches(File.ReadAllText(ini), @"^recent\d+=(.+)$", RegexOptions.Multiline).Select(m => m.Groups[1].Value.Trim()).Where(File.Exists).Reverse().ToList();   // recent01 is the oldest
            if (list.Count > 0) return list;
        }
        return Files(d).OrderByDescending(f => f.LastWriteTimeUtc).Take(60).Select(f => f.FullName);
    }

    /// <summary>Every project file under the projects folder (backup folders skipped).</summary>
    static IEnumerable<FileInfo> Files(Daw d)
    {
        if (ProjectsDir(d) is not { } root) return Enumerable.Empty<FileInfo>();
        var opt = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        return d.ext.SelectMany(x => new DirectoryInfo(root).EnumerateFiles("*" + x, opt)).Where(f => !IsBackupDir(f.DirectoryName!));
    }

    /// <summary>A folder's bounced renders, newest first. FL: audio named after one of its project files (FL's export name: "song_2.flp" -> "song_2.wav");
    /// the rest of the audio in an FL folder is the project's samples. Other DAWs keep recordings in subfolders (Samples\, Audio Files\, Media\),
    /// so audio right in the project folder counts as a render.</summary>
    static IEnumerable<FileInfo> Renders(Daw d, string dir, List<FileInfo> projects)
    {
        var stems = projects.Select(f => Path.GetFileNameWithoutExtension(f.Name)).ToList();
        return new DirectoryInfo(dir).GetFiles().Where(f => Audio.Contains(f.Extension.ToLowerInvariant()) && (d.id != "fl" || stems.Any(st => f.Name.StartsWith(st, StringComparison.OrdinalIgnoreCase))))
            .OrderByDescending(f => f.LastWriteTime);
    }

    /// <summary>The Studio row: one tile per project folder (its versions, renders, the DAW's backups) and what the newest project file says.</summary>
    public static List<Library.Item> Projects(Dictionary<string, string> t)
    {
        var list = new List<Library.Item>();
        if (Cfg.Daw is not { } d) return list;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Recent(d))
        {
            var dir = Places.Real(Path.GetDirectoryName(path)!);
            var projects = d.ext.SelectMany(x => Directory.GetFiles(dir, "*" + x)).Select(f => new FileInfo(f)).OrderByDescending(f => f.LastWriteTime).ToList();
            var grouped = projects.Count <= 6;   // a folder of one song's versions; a folder full of unrelated projects keeps one tile per file
            if (!seen.Add(grouped ? dir : path)) continue;
            if (!grouped) projects = new() { new FileInfo(path) };
            object Entry(FileInfo f, string kind) { var eid = (kind == "render" ? "file:" : "flp:") + f.FullName; t[eid] = f.FullName; return new { id = eid, name = kind == "render" ? f.Name : Path.GetFileNameWithoutExtension(f.Name), modified = Unix(f.LastWriteTime), size = f.Length }; }
            var renders = Renders(d, dir, projects).Take(12).ToList();
            var backups = !grouped ? new() : Directory.GetDirectories(dir).Where(IsBackupDir).SelectMany(b => new DirectoryInfo(b).GetFiles()).Where(f => IsProject(d, f.Name) || f.Name.EndsWith(".rpp-bak", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => f.LastWriteTime).Take(6).ToList();
            var newest = projects[0];
            var id = "flp:" + newest.FullName; t[id] = newest.FullName; t["dir:" + dir] = dir;
            list.Add(new(id, grouped ? Path.GetFileName(dir) : Path.GetFileNameWithoutExtension(path), Path.GetFileName(dir), Unix(newest.LastWriteTime), Icons.Shell(newest.FullName), new
            {
                project = Read(newest.FullName), folder = "dir:" + dir,
                versions = projects.Select(f => Entry(f, "version")).ToList(), renders = renders.Select(f => Entry(f, "render")).ToList(), backups = backups.Select(f => Entry(f, "backup")).ToList(),
            }));
            if (list.Count >= 60) break;
        }
        return list;
    }

    /// <summary>The Music row's "Your tracks": every song bounced in the projects folder, one per project folder (its newest render), newest first.</summary>
    public static List<Library.Item> Tracks(Dictionary<string, string> t)
    {
        var list = new List<Library.Item>(); var sw = System.Diagnostics.Stopwatch.StartNew();
        if (Cfg.Daw is not { } d) return list;
        foreach (var g in Files(d).GroupBy(f => f.DirectoryName!, StringComparer.OrdinalIgnoreCase))
        {
            var projects = g.OrderByDescending(f => f.LastWriteTime).ToList();
            var r = Renders(d, g.Key, projects).ToList(); if (r.Count == 0) continue;
            foreach (var f in r) t["file:" + f.FullName] = f.FullName;
            t["flp:" + projects[0].FullName] = projects[0].FullName;
            list.Add(new("file:" + r[0].FullName, Path.GetFileNameWithoutExtension(r[0].Name), Path.GetFileName(g.Key), Unix(r[0].LastWriteTime), null, new
            {
                kind = "track", size = r[0].Length, project = "flp:" + projects[0].FullName, saved = Unix(projects[0].LastWriteTime), flp = Read(projects[0].FullName),
                takes = r.Skip(1).Take(8).Select(f => new { id = "file:" + f.FullName, name = Path.GetFileNameWithoutExtension(f.Name), modified = Unix(f.LastWriteTime), size = f.Length }).ToList(),
            }));
        }
        Log.Write($"tracks: {list.Count} in {sw.ElapsedMilliseconds} ms");
        return list.OrderByDescending(x => x.modified).ToList();
    }

    // ---------- what a project file says about itself (cached by file time: the menu rescans on every open) ----------
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime at, object? info)> seen = new();
    /// <summary>{ app, bpm, fl (version), channels, patterns, plugins, created, minutes }; fields a format does not keep are left out. null = unreadable.</summary>
    public static object? Read(string path) => seen.TryGetValue(path, out var c) && c.at == File.GetLastWriteTimeUtc(path) ? c.info
        : (seen[path] = (File.GetLastWriteTimeUtc(path), Path.GetExtension(path).ToLowerInvariant() switch { ".flp" => Places.ReadFlp(path), ".als" => Als(path), ".rpp" => Rpp(path), _ => null })).info;

    static string[] TopPlugins(IEnumerable<string> names) => names.Where(x => x != "").GroupBy(x => x, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).Select(g => g.Key).Take(10).ToArray();

    /// <summary>Ableton Live set: gzipped XML. Tempo = the master track's Tempo/Manual, version from the root's Creator, tracks, plugin names.</summary>
    static object? Als(string path)
    {
        try
        {
            using var gz = new GZipStream(File.OpenRead(path), CompressionMode.Decompress);
            var x = new StreamReader(gz).ReadToEnd();
            var bpm = Regex.Match(x, @"<Tempo>.*?<Manual Value=""([\d.]+)""", RegexOptions.Singleline) is { Success: true } m && double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var b) ? b : 0;
            var ver = Regex.Match(x, @"Creator=""Ableton Live ([\d.]+)") is { Success: true } v ? string.Join(".", v.Groups[1].Value.Split('.').Take(2)) : "";
            var tracks = Regex.Matches(x, @"<(Audio|Midi)Track ").Count;
            var plugins = TopPlugins(Regex.Matches(x, @"<PlugName Value=""([^""]+)""|<Name Value=""([^""]+)"" />\s*<(?:Vst3?)PluginInfo").Select(p => p.Groups[1].Success ? p.Groups[1].Value : p.Groups[2].Value)
                .Concat(Regex.Matches(x, @"<(Operator|Simpler|Sampler|Wavetable|Drift|Analog|Collision|Tension|Electric|Reverb|Delay|Compressor2|Eq8|GlueCompressor|AutoFilter|Saturator)\b").Select(p => p.Groups[1].Value)));
            return new { app = "Ableton Live " + ver, bpm = Math.Round(bpm, 2), channels = tracks, plugins };
        }
        catch (Exception e) { Log.Write($"als {Path.GetFileName(path)} unreadable: {e.Message}"); return null; }
    }

    /// <summary>REAPER project: plain text. TEMPO line, the version on the first line, tracks, plugin names from &lt;VST / &lt;CLAP / &lt;JS lines.</summary>
    static object? Rpp(string path)
    {
        try
        {
            var x = File.ReadAllText(path);
            var bpm = Regex.Match(x, @"^\s*TEMPO ([\d.]+)", RegexOptions.Multiline) is { Success: true } m && double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var b) ? b : 0;
            var ver = Regex.Match(x, @"<REAPER_PROJECT [\d.]+ ""([\d.]+)") is { Success: true } v ? v.Groups[1].Value : "";
            var plugins = TopPlugins(Regex.Matches(x, @"<(?:VST|CLAP|AU)i? ""(?:\w+i?: )?([^""(]+)").Select(p => p.Groups[1].Value.Trim()).Concat(Regex.Matches(x, @"<JS ""?([^"" ]+)").Select(p => Path.GetFileName(p.Groups[1].Value))));
            return new { app = "REAPER " + ver, bpm = Math.Round(bpm, 2), channels = Regex.Matches(x, @"^\s*<TRACK\b", RegexOptions.Multiline).Count, plugins };
        }
        catch (Exception e) { Log.Write($"rpp {Path.GetFileName(path)} unreadable: {e.Message}"); return null; }
    }
}
