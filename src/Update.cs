using System.Diagnostics;
using System.Text.Json;

namespace ConsoleDesktop;

/// <summary>Updates from GitHub releases (Kierkegaardist/tidepool): Check compares the newest release's tag with the installed version.txt;
/// Install downloads its ConsoleDesktop-Setup.exe and runs it quietly (install.ps1 -Update: replaces bin, keeps the user's files, starts the desktop again).
/// A dev copy (no version.txt beside web\) never checks.</summary>
static class Update
{
    const string Releases = "https://api.github.com/repos/Kierkegaardist/tidepool/releases?per_page=5";   // the list, not /latest: that skips pre-releases
    static readonly HttpClient http = new() { DefaultRequestHeaders = { { "User-Agent", "Tidepool-updater" } }, Timeout = TimeSpan.FromSeconds(30) };
    public record Found(string version, string url, string notes);

    public static Version? Installed => File.Exists(Path.Combine(Paths.App, "version.txt")) && Version.TryParse(File.ReadAllText(Path.Combine(Paths.App, "version.txt")).Trim(), out var v) ? v : null;

    public static async Task<Found?> Check()
    {
        if (Installed is not { } have) return null;
        try
        {
            using var doc = JsonDocument.Parse(await http.GetStringAsync(Releases));
            foreach (var r in doc.RootElement.EnumerateArray())
            {
                if (r.GetProperty("draft").GetBoolean()) continue;
                if (!Version.TryParse(r.GetProperty("tag_name").GetString()?.TrimStart('v'), out var v) || v <= have) return null;   // newest first
                var asset = r.GetProperty("assets").EnumerateArray().FirstOrDefault(a => a.GetProperty("name").GetString() == "ConsoleDesktop-Setup.exe");
                if (asset.ValueKind != JsonValueKind.Object) continue;
                return new(v.ToString(), asset.GetProperty("browser_download_url").GetString()!, r.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "");
            }
        }
        catch (Exception e) { Log.Write("update check: " + e.Message); }
        return null;
    }

    public static async Task Install(Found f)
    {
        var dir = Path.Combine(Path.GetTempPath(), "tidepool-update");
        Directory.CreateDirectory(dir);
        var exe = Path.Combine(dir, "ConsoleDesktop-Setup.exe");
        await File.WriteAllBytesAsync(exe, await http.GetByteArrayAsync(f.url));
        // install.ps1 stops this exe's processes with their whole tree, so the setup must not be our child: Explorer starts it for us.
        // IExpress: /Q = no dialogs, /C: = our own command instead of the built-in one. The .cmd deletes the download folder once the setup returns.
        var cmd = Path.Combine(dir, "run.cmd");
        File.WriteAllText(cmd, $"@echo off\r\n\"{exe}\" /Q /C:\"powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File install.ps1 -Update\"\r\ncd /d \"%TEMP%\"\r\nrd /s /q \"{dir}\"\r\n");
        Log.Write($"update: installing {f.version}");
        var vbs = Path.Combine(dir, "run.vbs");   // runs the .cmd with no console window
        File.WriteAllText(vbs, $"CreateObject(\"WScript.Shell\").Run \"cmd /c \"\"{cmd}\"\"\", 0, False\r\n");
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{vbs}\"") { UseShellExecute = false });
    }
}
