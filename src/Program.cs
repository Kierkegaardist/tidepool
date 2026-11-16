using System.Diagnostics;
using Microsoft.Web.WebView2.Core;
using static ConsoleDesktop.Native;

namespace ConsoleDesktop;

// ConsoleDesktop.exe            watchdog: holds the escape-hatch hotkey, starts the host, restores stock Windows if the host dies or hangs
// ConsoleDesktop.exe --host     the layer itself (started by the watchdog; never run it alone)
// ConsoleDesktop.exe --stock    escape hatch: stop everything, taskbar back
// ConsoleDesktop.exe --setup    Setup (web/setup.html): tabs, music player, browser, DAW, chat; also runs by itself when config.json is missing
// ConsoleDesktop.exe --restart  stop, then start again with the current config.json (Setup's Save from inside the desktop)
// ConsoleDesktop.exe --selftest sun maths check (exit code 0 = pass)
// ConsoleDesktop.exe --web [tab id]   the Web row to logs/web-test.json (tests; no window)
// ConsoleDesktop.exe --spotify <op> [json]   one Spotify call, its events written to logs/spotify-test.txt (tests; no window)
// ConsoleDesktop.exe --youtube "<query>"     a YouTube search + the first result's stream to logs/youtube-test.json (tests; no window)
static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        // Started once from a VS Code / Claude terminal, the desktop inherits ELECTRON_RUN_AS_NODE=1 and hands it to everything it launches,
        // so VS Code (and any Electron app) starts as bare Node and quits at once. Nothing here needs it.
        Environment.SetEnvironmentVariable("ELECTRON_RUN_AS_NODE", null);
        var mode = args.FirstOrDefault() ?? "";
        if (mode == "--selftest") return Sun.SelfTest() & Library.SelfTest() ? 0 : 1;
        if (mode == "--spotify")
        {
            var outFile = Path.Combine(Paths.Logs, "spotify-test.txt"); File.WriteAllText(outFile, "");
            var sp = new Spotify((t, v) => File.AppendAllText(outFile, t + " " + System.Text.Json.JsonSerializer.Serialize(v) + Environment.NewLine));
            var msg = System.Text.Json.JsonDocument.Parse(args.Length > 2 ? args[2] : "{}").RootElement.Clone();
            var o = new Dictionary<string, object?> { ["op"] = args[1] }; foreach (var p in msg.EnumerateObject()) o[p.Name] = p.Value.GetString();
            sp.Do(System.Text.Json.JsonSerializer.SerializeToElement(o));
            Thread.Sleep(args[1] == "connect" ? 200_000 : 12_000); return 0;
        }
        if (mode == "--voice")   // tests: listen 6 s (or play a wav), events to logs/voice-test.txt
        {
            var outFile = Path.Combine(Paths.Logs, "voice-test.txt"); File.WriteAllText(outFile, "");
            var v = new Voice((t, val) => File.AppendAllText(outFile, $"{DateTime.Now:ss.f} {t} {System.Text.Json.JsonSerializer.Serialize(val)}{Environment.NewLine}"));
            if (args.Length > 1) { Voice.TestFile = args[1]; v.Start(4); while (!File.ReadAllText(outFile).Contains("\"end\"") && !File.ReadAllText(outFile).Contains("\"error\"")) Thread.Sleep(200); return 0; }   // --voice <16 kHz wav>: through to the end, 4 s pause ends it
            v.Start(); Thread.Sleep(6000); v.Stop(); Thread.Sleep(1500); return 0;
        }
        if (mode == "--wake" && args.Length > 1)   // tests: play a 16 kHz wav through the wake word, events to logs/wake-test.txt (the wav is followed by 10 s of silence)
        {
            var outFile = Path.Combine(Paths.Logs, "wake-test.txt"); File.WriteAllText(outFile, "");
            var v = new Voice((t, val) => File.AppendAllText(outFile, $"{DateTime.Now:ss.f} {t} {System.Text.Json.JsonSerializer.Serialize(val)}{Environment.NewLine}"));
            Voice.TestFile = args[1]; v.Wake(true);
            var until = DateTime.Now.AddSeconds(20); while (DateTime.Now < until && !File.ReadAllText(outFile).Contains("\"heard\"")) Thread.Sleep(200);
            return 0;
        }
        if (mode == "--tts" && args.Length > 1)   // tests: Kokoro reads args[1] (voice args[2]) to logs/tts-test.wav, timings in the log
        {
            var url = Tts.Say(args[1], args.Length > 2 ? args[2] : "af_heart", 1);
            if (url != null) File.WriteAllBytes(Path.Combine(Paths.Logs, "tts-test.wav"), Convert.FromBase64String(url[(url.IndexOf(',') + 1)..]));
            return url != null ? 0 : 1;
        }
        if (mode == "--spectrum") return Spectrum.Test();   // tests: FFT check, then 5 s of what the PC plays as bands to logs/spectrum-test.txt
        if (mode == "--youtube" && args.Length > 1) return YouTube.Test(args[1]);   // tests: a search + the first result's stream urls to logs/youtube-test.json (exit 0 = a stream came back)
        if (mode == "--web")   // tests: the Web row twice (the second from the cache) to logs/web-test.json, timings in the log; --web <tab id> switches to that tab
        {
            var t = new Dictionary<string, string>(); var sw = System.Diagnostics.Stopwatch.StartNew();
            var a = Web.Scan(t); var cold = sw.ElapsedMilliseconds; sw.Restart(); Web.Scan(t);
            Log.Write($"web test: {a.Count} tiles, {t.Count} ids, {cold} ms cold, {sw.ElapsedMilliseconds} ms cached");
            File.WriteAllText(Path.Combine(Paths.Logs, "web-test.json"), System.Text.Json.JsonSerializer.Serialize(a, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            if (args.Length > 1) { Web.Switch(args[1]); Thread.Sleep(1500); }
            return 0;
        }
        if (mode == "--overlay")   // tests: the "Hey Marina" overlay alone, through listening, thinking, speaking, done, until it fades and hides (own WebView2 profile, deleted after)
        {
            ApplicationConfiguration.Initialize();
            var udf = Path.Combine(Paths.App, "obj-webtest", "overlay-wv2");
            var o = new Overlay(Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(null, udf));
            var steps = new Queue<string>(new[] { """{"state":"listening"}""", """{"state":"thinking","said":"open spotify and play my liked songs"}""",
                """{"state":"speaking","reply":"Playing your Liked Songs on Spotify now. Want it shuffled?"}""", """{"state":"done"}""" });
            var t = new System.Windows.Forms.Timer { Interval = 3000 }; int n = 0;
            t.Tick += (_, _) => { if (steps.Count > 0) o.Set(steps.Dequeue()); else if (!o.Visible || ++n > 5) Application.ExitThread(); };
            t.Start();   // first state after 3 s: the overlay must load while hidden, as in the host
            var clock = System.Diagnostics.Stopwatch.StartNew(); var rnd = new Random(7);
            var lv = new System.Windows.Forms.Timer { Interval = 50 };   // a made-up voice for the live meter while it listens (3-6 s): syllables of rising and falling peaks
            lv.Tick += (_, _) => { var s = clock.Elapsed.TotalSeconds; if (s > 3.3 && s < 6) o.Level((float)(0.02 + 0.3 * Math.Pow(Math.Abs(Math.Sin(s * 7.5)), 2) * rnd.NextDouble())); };
            lv.Start();
            Application.Run();
            o.Dispose();
            for (int i = 0; i < 10 && Directory.Exists(udf); i++) { try { Directory.Delete(udf, true); } catch { Thread.Sleep(500); } }
            return 0;
        }
        if (mode == "--stock") { StopOthers(); Shell.Restore("stock shortcut"); return 0; }
        if (mode == "--restart") { StopOthers(); Shell.Restore("restart after Setup"); Thread.Sleep(1000); mode = ""; }   // then a normal start below reads the new config.json

        ApplicationConfiguration.Initialize();
        if (mode == "--setup" || !Cfg.Exists && mode == "")   // the installer's first run, or a start before Setup ever ran
        {
            var udf = Path.Combine(Paths.App, ".wv2");
            var f = new Setup(CoreWebView2Environment.CreateAsync(null, udf).GetAwaiter().GetResult(), true);
            Application.Run(f);
            if (!f.Saved || !f.StartNow || Process.GetProcessesByName("ConsoleDesktop").Length > 1) return 0;
            mode = "";   // saved with "start the desktop": on to a normal start
        }
        if (mode == "--host")
        {
            // Whatever happens to the host, the taskbar comes back.
            AppDomain.CurrentDomain.UnhandledException += (_, e) => { Log.Write("host crash: " + e.ExceptionObject); Shell.Restore("host crash"); };
            Application.ThreadException += (_, e) => { Log.Write("host UI exception: " + e.Exception); Shell.Restore("host crash"); Environment.Exit(3); };
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Shell.ShowShell();
            Application.Run(new Host());
            return 0;
        }
        if (Process.GetProcessesByName("ConsoleDesktop").Length > 1) { Log.Write("already running; second start ignored"); return 0; }
        // At login (Startup shortcut) Explorer may still be building the desktop: wait up to 60 s for it, then let it settle.
        for (int i = 0; i < 120 && (FindWindow("Progman", null) == IntPtr.Zero || FindWindow("Shell_TrayWnd", null) == IntPtr.Zero); i++) Thread.Sleep(500);
        if (Environment.TickCount64 < 5 * 60_000) Thread.Sleep(5000);   // started within 5 min of boot
        Application.Run(new Watchdog());
        return 0;
    }

    /// <summary>Stops the other processes of this same exe (host + watchdog), never another installed or dev copy.</summary>
    static void StopOthers()
    {
        foreach (var p in Process.GetProcessesByName("ConsoleDesktop"))
            if (p.Id != Environment.ProcessId) try { if (string.Equals(p.MainModule?.FileName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)) { p.Kill(true); p.WaitForExit(3000); } } catch { }
    }
}

/// <summary>A separate small process, so the hotkey and the restore still work when the host hangs or crashes.</summary>
sealed class Watchdog : ApplicationContext
{
    const int HotkeyId = 1;
    public const int WM_STOCK = 0x8001;   // WM_APP + 1, posted by the host when the page calls host.exitToStock()
    readonly Process host;
    readonly MsgWindow hotkeyWindow;
    readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    readonly DateTime started = DateTime.Now;
    int misses;

    public Watchdog()
    {
        hotkeyWindow = new MsgWindow("ConsoleDesktopWatchdog", m => { if (m.Msg == WM_HOTKEY) Stop("hotkey Ctrl+Alt+Win+S"); else if (m.Msg == WM_STOCK) Stop("the page asked (exitToStock)"); });
        // Ctrl+Alt+Win+S (MOD_ALT 1 | MOD_CONTROL 2 | MOD_WIN 8 | MOD_NOREPEAT 0x4000)
        if (!RegisterHotKey(hotkeyWindow.Handle, HotkeyId, 0x1 | 0x2 | 0x8 | 0x4000, (uint)Keys.S)) Log.Write("WARNING: hotkey Ctrl+Alt+Win+S is taken; use the Stock Windows shortcut");
        host = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--host") { UseShellExecute = false })!;
        Log.Write($"watchdog start, host pid {host.Id}");
        timer.Tick += (_, _) => Check();
        timer.Start();
    }

    void Check()
    {
        if (host.HasExited) { Stop($"host exited (code {host.ExitCode})"); return; }
        var ping = FindWindowEx(HWND_MESSAGE, IntPtr.Zero, null, "ConsoleDesktopHost");
        if (ping == IntPtr.Zero) { if (DateTime.Now - started > TimeSpan.FromSeconds(30)) Stop("host never came up"); return; }
        bool alive = SendMessageTimeout(ping, WM_NULL, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, 1500, out _) != IntPtr.Zero;
        misses = alive ? 0 : misses + 1;
        if (misses >= 3) Stop("host not responding for 3 checks");
    }

    void Stop(string reason)
    {
        timer.Stop();
        try { if (!host.HasExited) { host.Kill(true); host.WaitForExit(3000); } } catch { }
        Shell.Restore(reason);
        UnregisterHotKey(hotkeyWindow.Handle, HotkeyId);
        ExitThread();
    }
}
