using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace ConsoleDesktop;

/// <summary>config.json in the app folder: which tabs are on and which app or service each one talks to (music player, browser, DAW, chat).
/// A missing file or key is filled in from what is installed, so a fresh install works before Setup is opened.
/// Setup (web/setup.html, `ConsoleDesktop.exe --setup`) writes the file; the host reads it once at start.</summary>
static class Cfg
{
    public static string FilePath => Environment.GetEnvironmentVariable("TIDEPOOL_CONFIG") is { Length: > 0 } f ? f : Path.Combine(Paths.App, "config.json");   // the env var: tests, so they never touch the real one
    public static bool Exists => File.Exists(FilePath);
    static JsonObject root = Load();

    static JsonObject Load()
    {
        try { return Exists ? JsonNode.Parse(File.ReadAllText(FilePath)) as JsonObject ?? new() : new(); }
        catch (Exception e) { Log.Write("config.json unreadable, going by what is installed: " + e.Message); return new(); }
    }

    internal static JsonNode? Node(string path) { JsonNode? n = root; foreach (var k in path.Split('.')) n = (n as JsonObject)?[k]; return n; }
    /// <summary>"music.player" -> its text in config.json, null when missing or empty.</summary>
    public static string? Get(string path) => Node(path) is JsonValue v && v.TryGetValue<string>(out var s) && s.Trim() != "" ? s.Trim() : null;
    public static double? Num(string path) => Node(path) is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;
    static bool Flag(string path, bool def) => Node(path) is JsonValue v && v.TryGetValue<bool>(out var b) ? b : def;

    static string Local => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    static string Roaming => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    // ---------- what each tab talks to ----------
    public static Media.Player Player => player ??= Media.Find(Get("music.player") ?? Media.Players.FirstOrDefault(p => p.installed())?.id ?? "any", Get("music.name"));
    public static Web.Browser Browser => browser ??= Web.Browsers.FirstOrDefault(b => b.id == Get("web.browser")) ?? DefaultBrowser();
    public static Daw? Daw => daw ??= Get("studio.daw") is { } id ? Studio.Daws.FirstOrDefault(d => d.id == id) : Studio.Daws.FirstOrDefault(d => d.installed());
    /// <summary>"claude-code" (the user's own Claude Code, full access), "cli" (another agent's command line, chat.command, Codex by default),
    /// "api" (any OpenAI-compatible endpoint or the Claude API, plain chat) or "none". Unset: whichever agent is installed, Claude Code first.</summary>
    public static string Chat => Get("chat.provider") ?? (Claude.Exe != null ? "claude-code" : HasCodex ? "cli" : "none");
    public static string ChatName => Get("chat.name") ?? (Chat == "claude-code" ? "Claude" : Chat == "cli" && Get("chat.command") == null ? "Codex" : "Assistant");
    /// <summary>OpenAI's Codex CLI on PATH: its own installer (codex.exe) or npm (codex.cmd).</summary>
    static bool HasCodex => Tool("codex") != null || (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries).Any(d => File.Exists(Path.Combine(d.Trim(), "codex.cmd")));
    static Media.Player? player; static Web.Browser? browser; static Daw? daw;

    static Web.Browser DefaultBrowser()
    {
        // Windows' own choice for http links, then any browser that has a profile here
        var prog = (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\Shell\Associations\UrlAssociations\http\UserChoice", "ProgId", null) as string ?? "").ToLowerInvariant();
        return Web.Browsers.FirstOrDefault(b => prog != "" && b.progId.Any(prog.StartsWith) && b.Root != null) ?? Web.Browsers.FirstOrDefault(b => b.Root != null) ?? Web.Browsers[0];
    }

    /// <summary>A tab is on unless config.json switches it off, or the thing it needs is not on this PC.</summary>
    public static bool On(string module) => Flag("modules." + module, true) && module switch
    {
        "studio" => Daw != null,
        "video" => Tool("yt-dlp") != null,
        "voice" => Whisper != null,
        "claude" => Chat != "none",
        _ => true,
    };

    // ---------- helper programs (config "tools.<name>", then app\extras, then PATH) ----------
    public static string Extras => Path.Combine(Paths.App, "extras");
    public static string? Tool(string name)
    {
        var exe = name + ".exe";
        var hits = new[] { Get("tools." + name), Path.Combine(Extras, exe) }.Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries).Select(d => Path.Combine(d.Trim(), exe)));
        return hits.FirstOrDefault(p => p != null && File.Exists(p));
    }
    /// <summary>whisper.cpp for the mics: (whisper-cli.exe, the folder with the ggml-*.bin models), null when it is not installed.</summary>
    public static (string cli, string models)? Whisper
    {
        get
        {
            foreach (var d in new[] { Get("tools.whisper"), Path.Combine(Extras, "whisper") })
            {
                if (d == null || !Directory.Exists(d)) continue;
                var cli = new[] { @"bin\Release\whisper-cli.exe", @"Release\whisper-cli.exe", "whisper-cli.exe" }.Select(p => Path.Combine(d, p)).FirstOrDefault(File.Exists);
                if (cli != null && File.Exists(Path.Combine(d, "ggml-base.en.bin"))) return (cli, d);
            }
            return null;
        }
    }

    // ---------- where the sun is ----------
    /// <summary>The place in config.json, else a guess from the clock's time zone (15° of longitude per hour, mid latitude): the sky only needs roughly the right sunrise.
    /// ponytail: Setup's city search sets the real place; no Windows location permission asked.</summary>
    public static (double lat, double lon, string name) Place =>
        Num("place.lat") is { } la && Num("place.lon") is { } lo ? (la, lo, Get("place.name") ?? "Here")
        : (45, Math.Clamp(TimeZoneInfo.Local.BaseUtcOffset.TotalHours * 15, -180, 180), "Your time zone");

    // ---------- the chat's API key, sealed to this Windows user ----------
    static string KeyFile => Path.Combine(Paths.App, ".chatkey");
    public static string? ChatKey { get { try { return File.Exists(KeyFile) && Spotify.Dpapi(File.ReadAllBytes(KeyFile), false) is { } b ? System.Text.Encoding.UTF8.GetString(b) : null; } catch { return null; } } }
    public static void SetChatKey(string key) { if (key == "") { try { File.Delete(KeyFile); } catch { } } else File.WriteAllBytes(KeyFile, Spotify.Dpapi(System.Text.Encoding.UTF8.GetBytes(key), true)!); }

    // ---------- for the page and for Setup ----------
    static readonly string[] Modules = { "games", "music", "video", "studio", "web", "files", "claude", "voice" };

    /// <summary>What the page needs to know before its scripts run (bridge: window.TP_CONFIG): which tabs exist and what to call things.</summary>
    public static object ForPage() => new
    {
        modules = Modules.ToDictionary(m => m, On),
        names = new { player = Player.name, browser = Browser.name, daw = Daw?.name ?? "your DAW", ext = Daw?.ext[0] ?? "", ai = ChatName },
        music = new { player = Player.id, rich = Player.id is "spotifast" or "spotify" },
        chat = new { provider = Chat, api = Get("chat.api") ?? "openai", model = Get("chat.model"), models = (Node("chat.models") as JsonArray)?.Select(m => m?.GetValue<string>()).Where(m => m != null) },
    };

    /// <summary>Setup's form: the saved file, what would be picked without it, and every choice with whether it is installed here.</summary>
    public static object ForSetup() => new
    {
        first = !Exists, saved = root,
        now = new { player = Player.id, browser = Browser.id, daw = Daw?.id, chat = Chat, chatName = ChatName, place = new { Place.lat, Place.lon, Place.name }, hasKey = ChatKey != null,
                    projects = Daw == null ? null : Studio.ProjectsDir(Daw), home = Claude.Home, codex = ApiChat.Codex, modules = Modules.ToDictionary(m => m, m => Flag("modules." + m, true)), startup = File.Exists(StartupLink) },
        players = Media.Players.Select(p => new { p.id, p.name, installed = p.installed() }),
        browsers = Web.Browsers.Select(b => new { b.id, b.name, installed = b.Root != null }),
        daws = Studio.Daws.Select(d => new { d.id, d.name, installed = d.installed() }),
        screens = Screen.AllScreens.Select(s => new { id = s.DeviceName, s.Primary, x = s.Bounds.X, y = s.Bounds.Y, w = s.Bounds.Width, h = s.Bounds.Height }),   // Setup › Screens
        tools = new { claude = Claude.Exe != null, codex = HasCodex, ytdlp = Tool("yt-dlp") != null, js = Tool("node") != null || Tool("deno") != null, whisper = Whisper != null },
    };

    /// <summary>Setup's Save: the whole file replaces the old one (written beside it first, so a crash never leaves half a file).</summary>
    public static void Save(JsonElement config)
    {
        var tmp = FilePath + ".new";
        File.WriteAllText(tmp, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, FilePath, true);
        root = Load(); player = null; browser = null; daw = null;
    }

    // ---------- start with Windows ----------
    static string StartupLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Console Desktop.lnk");
    public static void SetStartup(bool on)
    {
        try
        {
            if (!on) { File.Delete(StartupLink); return; }
            dynamic sh = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
            var l = sh.CreateShortcut(StartupLink); l.TargetPath = Environment.ProcessPath; l.WorkingDirectory = AppContext.BaseDirectory; l.Save();
        }
        catch (Exception e) { Log.Write("startup shortcut: " + e.Message); }
    }
}
