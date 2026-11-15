using Microsoft.Data.Sqlite;
using Microsoft.Win32;

namespace ConsoleDesktop;

/// <summary>The non-game rows: recent files (Windows' Recent folder), recent FL Studio projects (the Web row is Web.cs).
/// Each adds its ids to the launch table: files and projects open with their own app.</summary>
static class Places
{
    static readonly Type? WshShell = Type.GetTypeFromProgID("WScript.Shell");

    /// <summary>Where a path really is: user folders here are junctions elsewhere, and the page's drive mappings (Host.cs) do not follow a junction off their drive.</summary>
    public static string Real(string path)
    {
        using var h = CreateFile(path, 0, 7, IntPtr.Zero, 3 /*OPEN_EXISTING*/, 0x02000000 /*BACKUP_SEMANTICS: folders too*/, IntPtr.Zero);
        if (h.IsInvalid) return path;
        var b = new System.Text.StringBuilder(1024);
        var n = GetFinalPathNameByHandle(h, b, 1024, 0);
        if (n == 0 || n >= 1024) return path;
        var r = b.ToString();
        return r.StartsWith(@"\\?\UNC\") ? @"\\" + r[8..] : r.StartsWith(@"\\?\") ? r[4..] : r;
    }
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint disposition, uint flags, IntPtr template);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern uint GetFinalPathNameByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle h, System.Text.StringBuilder buf, uint len, uint flags);

    static long Unix(DateTime t) => new DateTimeOffset(t.ToUniversalTime()).ToUnixTimeSeconds();
    static bool Shown(FileSystemInfo f) => (f.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0;

    /// <summary>The Files row: your places (Projects, Downloads, Desktop, Documents, Pictures, Music, Videos), the drives with their free space,
    /// then the last 30 files Windows recorded as opened (shortcuts in %APPDATA%\Microsoft\Windows\Recent; folders and missing files left out).</summary>
    public static List<Library.Item> Files(Dictionary<string, string> t)
    {
        var list = new List<Library.Item>();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var extra = (Cfg.Node("files.places") as System.Text.Json.Nodes.JsonArray)?.Select(p => (name: p?["name"]?.GetValue<string>() ?? "", path: p?["path"]?.GetValue<string>() ?? ""))
            .Where(p => p.name != "" && Directory.Exists(p.path)) ?? Enumerable.Empty<(string, string)>();   // config.json "files.places": your own folders first (Setup)
        var places = extra.Concat(new (string name, string path)[] { ("Downloads", Path.Combine(home, "Downloads")), ("Desktop", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)),
            ("Documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)), ("Pictures", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)),
            ("Music", Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)), ("Videos", Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)) }).ToArray();
        var day = DateTime.Today;   // "new today" = since midnight, like the page's dates
        foreach (var (name, link) in places)
        {
            var path = link == "" ? "" : Real(link);
            if (path == "" || !Directory.Exists(path) || list.Any(x => x.id == "dir:" + path)) continue;
            int items = 0, fresh = 0;
            try { foreach (var f in new DirectoryInfo(path).EnumerateFileSystemInfos().Where(Shown)) { items++; if (f.LastWriteTime > day) fresh++; } } catch { }
            t["dir:" + path] = path;
            list.Add(new("dir:" + path, name, path, Unix(Directory.GetLastWriteTime(path)), Icons.Shell(path), new { kind = "place", items, fresh }));
        }
        foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable))
        {
            var letter = d.Name.TrimEnd('\\');
            t["dir:" + d.Name] = d.Name;
            list.Add(new("dir:" + d.Name, $"{(d.VolumeLabel == "" ? d.DriveType == DriveType.Removable ? "USB drive" : "Local disk" : d.VolumeLabel)} ({letter})", d.Name, 0, Icons.Shell(d.Name),
                new { kind = "drive", free = d.AvailableFreeSpace, total = d.TotalSize }));
        }
        var dir = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
        dynamic shell = Activator.CreateInstance(WshShell!)!;
        int recent = 0;
        foreach (var lnk in new DirectoryInfo(dir).GetFiles("*.lnk").OrderByDescending(f => f.LastWriteTimeUtc))
        {
            string target;
            try { target = (string)shell.CreateShortcut(lnk.FullName).TargetPath; } catch { continue; }
            if (target == "" || !File.Exists(target)) continue;   // folders, removed files, unplugged drives
            target = Real(target);
            var id = "file:" + target;
            if (t.ContainsKey(id)) continue;
            t[id] = target;
            list.Add(new(id, Path.GetFileName(target), Path.GetDirectoryName(target) ?? "", Unix(lnk.LastWriteTime), Icons.Shell(target), new { kind = "file", size = new FileInfo(target).Length }));
            if (++recent == 30) break;
        }
        return list;
    }

    /// <summary>One folder's entries, newest first (400 at most), for the Files column and the preview card. Any existing folder: the page browses like Explorer does.</summary>
    public static object Browse(string id)
    {
        try
        {
            var dir = new DirectoryInfo(id.StartsWith("dir:") ? Real(id[4..]) : "?");
            if (!dir.Exists) return new { id, missing = true };
            var all = dir.EnumerateFileSystemInfos().Where(Shown).ToList();
            var day = DateTime.Today;
            var folder = Icons.Shell(Paths.Web);   // one plain folder's icon for every subfolder: per-folder icons would mean one shell call per folder
            var entries = all.OrderByDescending(f => f.LastWriteTime).Take(400).Select(f => new
            {
                id = (f is DirectoryInfo ? "dir:" : "file:") + f.FullName, name = f.Name, dir = f is DirectoryInfo,
                size = f is FileInfo fi ? fi.Length : 0, modified = Unix(f.LastWriteTime), art = f is FileInfo ? Icons.Shell(f.FullName) : folder,
            }).ToList();
            return new { id, name = dir.Parent == null ? dir.FullName.TrimEnd('\\') : dir.Name, path = dir.FullName,
                total = all.Count, dirs = all.Count(f => f is DirectoryInfo), fresh = all.Count(f => f.LastWriteTime > day), entries };
        }
        catch (Exception e) { return new { id, denied = true, error = e.Message }; }   // a protected folder (C:\System Volume Information...)
    }

    /// <summary>What an .flp says about itself: tempo, FL version, channels, patterns, the plugins on it, when it was started and how long it has been worked on.
    /// FLP = "FLhd" header, then "FLdt" events: id below 64 one byte, below 128 two, below 192 four, else a 7-bit length and that many bytes (PyFLP documents the ids).</summary>
    internal static object? ReadFlp(string path)
    {
        try
        {
            var d = File.ReadAllBytes(path);
            if (d.Length < 22 || System.Text.Encoding.ASCII.GetString(d, 0, 4) != "FLhd") return null;
            int p = 8 + BitConverter.ToInt32(d, 4) + 8, channels = 0;
            double bpm = 0, minutes = 0; long created = 0; string version = "";
            var patterns = new HashSet<int>(); var names = new List<string>(); var samples = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (p < d.Length)
            {
                int e = d[p++];
                if (e < 64) { p++; continue; }
                if (e < 128) { if (p + 2 > d.Length) break; if (e == 64) channels++; if (e == 65) patterns.Add(BitConverter.ToUInt16(d, p)); p += 2; continue; }   // 64 new channel, 65 new pattern
                if (e < 192) { if (p + 4 > d.Length) break; if (e == 156) bpm = BitConverter.ToUInt32(d, p) / 1000.0; p += 4; continue; }   // 156 tempo x 1000
                int n = 0, s = 0; byte b;
                do { b = d[p++]; n |= (b & 0x7F) << s; s += 7; } while ((b & 0x80) != 0 && p < d.Length);
                if (p + n > d.Length) break;
                string Txt() => System.Text.Encoding.Unicode.GetString(d, p, n & ~1).TrimEnd('\0');
                if (e == 199) version = System.Text.Encoding.ASCII.GetString(d, p, n).TrimEnd('\0');   // "24.1.1.4285"
                else if (e == 196) samples.Add(Path.GetFileNameWithoutExtension(Txt()));   // a sampler channel's file
                else if (e == 203) names.Add(Txt());   // a channel's or effect's plugin name
                else if (e == 237 && n >= 16) { created = Unix(DateTime.FromOADate(BitConverter.ToDouble(d, p))); minutes = BitConverter.ToDouble(d, p + 8) * 1440; }   // started (Delphi date) + time worked on it (days)
                p += n;
            }
            var plugins = names.Where(x => x != "" && !samples.Contains(x) && !System.Text.RegularExpressions.Regex.IsMatch(x, @"^Pattern \d|#\d+$| - ")).GroupBy(x => x, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count()).Select(g => g.Key).Take(10).ToArray();   // sample channels are named after their files: those are sounds, not plugins; "Insert 2 - Volume" is an automation clip
            return new { app = "FL Studio " + string.Join(".", version.Split('.').Take(2)), bpm = Math.Round(bpm, 2), fl = string.Join(".", version.Split('.').Take(2)), channels, patterns = patterns.Count, plugins, created, minutes = Math.Round(minutes) };
        }
        catch (Exception e) { Log.Write($"flp {Path.GetFileName(path)} unreadable: {e.Message}"); return null; }
    }
}
