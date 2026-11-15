using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace ConsoleDesktop;

/// <summary>Setup (web/setup.html): which tabs are on and what each talks to, saved to config.json (Config.cs).
/// Opened by the installer's first run (`ConsoleDesktop.exe --setup`) and from the desktop's Settings › Setup. Messages are plain JSON
/// { type, ... } both ways; the page has no other bridge. Saving from inside the desktop restarts it, since every part reads the file at start.</summary>
sealed class Setup : Form
{
    static Setup? one;
    readonly WebView2 view = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(11, 34, 51) };
    readonly bool standalone;
    readonly HttpClient http = new() { Timeout = TimeSpan.FromMinutes(20) };
    public bool Saved { get; private set; }
    public bool StartNow { get; private set; }

    /// <summary>From the desktop: one Setup window at a time, in front.</summary>
    public static void Show(CoreWebView2Environment env)
    {
        if (one == null || one.IsDisposed) { one = new Setup(env, false); one.Show(); }
        one.Activate();
    }

    public Setup(CoreWebView2Environment env, bool standalone)
    {
        this.standalone = standalone;
        Text = "Console Desktop Setup"; BackColor = Color.FromArgb(11, 34, 51); StartPosition = FormStartPosition.CenterScreen; TopMost = !standalone;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        float k = DeviceDpi / 96f; ClientSize = new Size((int)(980 * k), (int)(760 * k)); MinimumSize = new Size((int)(720 * k), (int)(560 * k));
        Controls.Add(view);
        _ = Init(env);
    }

    async Task Init(CoreWebView2Environment env)
    {
        try
        {
            await view.EnsureCoreWebView2Async(env);
            var w = view.CoreWebView2;
            w.Settings.AreDefaultContextMenusEnabled = false; w.Settings.IsStatusBarEnabled = false;
            w.SetVirtualHostNameToFolderMapping("app.local", Paths.Web, CoreWebView2HostResourceAccessKind.Allow);
            w.WebMessageReceived += (_, e) => { try { _ = On(JsonNode.Parse(e.WebMessageAsJson) as JsonObject ?? new()); } catch (Exception x) { Log.Write("setup message: " + x.Message); } };
            w.NewWindowRequested += (_, e) => { e.Handled = true; Library.OpenUrl(e.Uri); };   // help links open in the browser
            w.Navigate($"https://app.local/setup.html?{(standalone ? "first" : "desktop")}&{Environment.TickCount64}");
        }
        catch (Exception e) { Log.Write("setup: WebView2 failed: " + e.Message); MessageBox.Show("Setup needs Microsoft Edge WebView2.\n\n" + e.Message, Text); Close(); }
    }

    void Reply(object v) { try { BeginInvoke(() => view.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(v))); } catch { } }
    static string S(JsonObject m, string k) => m[k]?.GetValue<string>() ?? "";

    async Task On(JsonObject m)
    {
        var type = S(m, "type");
        try
        {
            switch (type)
            {
                case "get": Reply(new { type = "state", state = Cfg.ForSetup() }); break;
                case "save":
                    Cfg.Save(JsonSerializer.SerializeToElement(m["config"]));
                    if (m["key"] is JsonValue kv && kv.TryGetValue<string>(out var key)) Cfg.SetChatKey(key.Trim());   // null = keep the saved key
                    Cfg.SetStartup(m["startup"]?.GetValue<bool>() ?? false);
                    Saved = true; StartNow = m["start"]?.GetValue<bool>() ?? true;
                    Log.Write("setup: saved config.json");
                    Reply(new { type = "saved" });
                    BeginInvoke(() =>
                    {
                        Close();
                        if (!standalone) Restart();
                    });
                    break;
                case "cancel": BeginInvoke(Close); break;
                case "folder":
                    BeginInvoke(() =>
                    {
                        using var d = new FolderBrowserDialog { InitialDirectory = S(m, "at"), UseDescriptionForTitle = true, Description = S(m, "title") };
                        Reply(new { type = "folder", id = S(m, "id"), path = d.ShowDialog(this) == DialogResult.OK ? d.SelectedPath : null });
                    });
                    break;
                case "place": Reply(new { type = "places", items = await Geocode(S(m, "q")) }); break;
                case "models": Reply(new { type = "models", result = await Models(S(m, "api"), S(m, "url"), m["key"]?.GetValue<string>()) }); break;
                case "fetch": Reply(new { type = "fetched", tool = S(m, "tool"), error = await Fetch(S(m, "tool")) }); Reply(new { type = "state", state = Cfg.ForSetup() }); break;
                case "open": Library.OpenUrl(S(m, "url")); break;
                case "identify": BeginInvoke(() => Identify((m["names"] as JsonObject)?.ToDictionary(k => k.Key, k => k.Value?.GetValue<string>() ?? "") ?? new())); break;   // Screens: a big label on each screen
            }
        }
        catch (Exception e) { Log.Write($"setup {type}: {e.Message}"); Reply(new { type = "error", op = type, message = e.Message }); }
    }

    /// <summary>Setup › Screens › Identify: each screen shows its label (names: device name -> label, e.g. "2 · Main") for three seconds.</summary>
    static void Identify(Dictionary<string, string> names)
    {
        foreach (var scr in Screen.AllScreens)
        {
            var f = new Form { FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual, TopMost = true, ShowInTaskbar = false, BackColor = Color.FromArgb(8, 22, 34), Opacity = .92 };
            var b = scr.Bounds; f.Bounds = new Rectangle(b.X + b.Width / 2 - 260, b.Y + b.Height / 2 - 150, 520, 300);
            f.Controls.Add(new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.White, Font = new Font("Segoe UI Light", 44f),
                Text = names.TryGetValue(scr.DeviceName, out var t) && t != "" ? t : scr.DeviceName.TrimStart('\\', '.') });
            f.Show();
            var timer = new System.Windows.Forms.Timer { Interval = 3000 }; timer.Tick += (_, _) => { timer.Dispose(); f.Close(); }; timer.Start();
        }
    }

    /// <summary>The desktop again with the new config: the watchdog and host stop, the taskbar comes back for a moment, and a fresh start reads config.json.</summary>
    internal static void Restart()
    {
        try { Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--restart") { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory }); }
        catch (Exception e) { Log.Write("restart failed: " + e.Message); }
    }

    /// <summary>A city's coordinates for the sun (Open-Meteo's free geocoder; only the typed name is sent).</summary>
    async Task<object> Geocode(string q)
    {
        if (q.Trim().Length < 2) return Array.Empty<object>();
        var j = JsonNode.Parse(await http.GetStringAsync("https://geocoding-api.open-meteo.com/v1/search?count=6&format=json&name=" + Uri.EscapeDataString(q.Trim())));
        return (j?["results"] as JsonArray)?.Select(r => new
        {
            name = r?["name"]?.GetValue<string>(), country = r?["country"]?.GetValue<string>(), region = r?["admin1"]?.GetValue<string>(),
            lat = Math.Round(r?["latitude"]?.GetValue<double>() ?? 0, 2), lon = Math.Round(r?["longitude"]?.GetValue<double>() ?? 0, 2),
        }).ToArray() ?? (object)Array.Empty<object>();
    }

    /// <summary>The models an OpenAI-compatible server offers (GET {url}/models): it also proves the address and key work. The Claude API gets a fixed list.</summary>
    async Task<object> Models(string api, string url, string? key)
    {
        if (api == "anthropic") return new { ok = true, models = new[] { "claude-opus-5-5", "claude-sonnet-5-5", "claude-haiku-5-5", "claude-fable-5-2" } };
        key = string.IsNullOrEmpty(key) ? Cfg.ChatKey : key;
        using var req = new HttpRequestMessage(HttpMethod.Get, url.TrimEnd('/') + "/models");
        if (!string.IsNullOrEmpty(key)) req.Headers.Authorization = new("Bearer", key);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var res = await http.SendAsync(req, cts.Token);
        var text = await res.Content.ReadAsStringAsync(cts.Token);
        if (!res.IsSuccessStatusCode) { string? msg = null; try { msg = JsonNode.Parse(text)?["error"]?["message"]?.GetValue<string>(); } catch { } return new { ok = false, error = $"{(int)res.StatusCode}: {msg ?? res.ReasonPhrase}" }; }
        var ids = (JsonNode.Parse(text)?["data"] as JsonArray)?.Select(x => x?["id"]?.GetValue<string>()).OfType<string>().OrderBy(x => x).ToArray() ?? Array.Empty<string>();
        return new { ok = true, models = ids };
    }

    /// <summary>Downloads a helper into app\extras: yt-dlp (Video tab) or whisper.cpp + its English models (mics, "Hey ..." wake word). null = done, else the error.</summary>
    async Task<string?> Fetch(string tool)
    {
        Directory.CreateDirectory(Cfg.Extras);
        async Task Get(string url, string file)
        {
            var tmp = file + ".part";
            using (var res = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
            {
                res.EnsureSuccessStatusCode();
                var total = res.Content.Headers.ContentLength ?? 0; long got = 0, shown = 0;
                await using var src = await res.Content.ReadAsStreamAsync(); await using var dst = File.Create(tmp);
                var buf = new byte[1 << 16];
                for (int n; (n = await src.ReadAsync(buf)) > 0;)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n)); got += n;
                    if (got - shown > 2_000_000) { shown = got; Reply(new { type = "progress", tool, file = Path.GetFileName(file), got, total }); }
                }
            }
            File.Move(tmp, file, true);
        }
        try
        {
            if (tool == "yt-dlp") { await Get("https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe", Path.Combine(Cfg.Extras, "yt-dlp.exe")); return null; }
            if (tool == "whisper")
            {
                var dir = Path.Combine(Cfg.Extras, "whisper"); Directory.CreateDirectory(dir);
                // whisper.cpp's own Windows build (CPU); the newest release names its zip whisper-bin-x64.zip
                var zip = Path.Combine(dir, "whisper-bin-x64.zip");
                await Get("https://github.com/ggml-org/whisper.cpp/releases/latest/download/whisper-bin-x64.zip", zip);
                ZipFile.ExtractToDirectory(zip, dir, true); File.Delete(zip);
                foreach (var m in new[] { "base.en", "tiny.en" })
                    if (!File.Exists(Path.Combine(dir, $"ggml-{m}.bin"))) await Get($"https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-{m}.bin", Path.Combine(dir, $"ggml-{m}.bin"));
                return Cfg.Whisper == null ? "Downloaded, but whisper-cli.exe was not in the zip" : null;
            }
            return "Unknown helper: " + tool;
        }
        catch (Exception e) { Log.Write($"setup fetch {tool}: {e.Message}"); return e.Message; }
    }
}
