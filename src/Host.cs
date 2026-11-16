using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using static ConsoleDesktop.Native;

namespace ConsoleDesktop;

/// <summary>The layer: one WebView2 window per screen at the desktop layer, the taskbar kept hidden, the pause rule, live reload, the bridge.</summary>
sealed class Host : ApplicationContext
{
    const string Page = "https://app.local/index.html";   // app/web (Alyosha's host page)
    static readonly (double lat, double lon, string name) Place = Cfg.Place;   // config.json "place" (Setup), else a guess from the time zone
    static double Lat => Place.lat; static double Lon => Place.lon;
    static readonly string Bridge = new StreamReader(Assembly.GetExecutingAssembly().GetManifestResourceStream("bridge.js")!).ReadToEnd();

    readonly List<Scene> scenes = new();
    readonly System.Windows.Forms.Timer tick = new() { Interval = 500 }, liveDebounce = new() { Interval = 250 };
    readonly System.Windows.Forms.Timer updateTimer = new() { Interval = 60_000 }; Update.Found? update;   // Update.cs
    readonly MsgWindow ping = new("ConsoleDesktopHost");   // the watchdog pings this; it answers only if our UI thread is alive
    readonly FileSystemWatcher liveWatch;   // app/web/live: the page re-reads theme.json + live.css itself on a "live" event
    readonly Dictionary<uint, string> exeByPid = new();
    readonly Dictionary<uint, string?> artByPid = new();
    readonly Input input;   // desktop clicks -> page, PS button -> menu (own thread)
    readonly Media media; Media.Playing? nowPlaying;
    readonly IChat claude;   // the chat panel: the user's Claude Code, or any chat API (config.json "chat", ApiChat.cs)
    readonly Spotify spotify;
    readonly Voice voice;       // the mics in search and the Claude panel: local Whisper (Voice.cs)   // the Music tab's Spotify: Spotifast's cache, plus the Web API once connected
    readonly Overlay overlay;   // "Hey Marina": what was heard and the reply, over whatever app is in front (Overlay.cs)
    readonly MsgWindow menuKey;   // Win+Esc: lift the menu out of the desktop layer
    readonly Switcher switcher;   // the one Alt+Tab entry (Switcher.cs)
    readonly Spectrum spectrum;   // what the PC plays, as bands (Spectrum.cs): runs while an uncovered page asks for it
    Dictionary<string, object> lists = new(); readonly Dictionary<string, string> listJson = new();   // games, apps, files, studio, web
    Scene? lifted;
    internal IntPtr startFg;   // the user's window at start: WebView2 grabs focus while loading, we hand it back
    string lastWindows = "";
    DateTime lastSun = DateTime.MinValue;
    List<Notes.Note> notes = new(); string notesJson = ""; int noteTick;

    public Host()
    {
        Log.Write("host start");
        Directory.CreateDirectory(Paths.Live);
        startFg = GetForegroundWindow();
        var layer = Shell.DesktopLayer();
        Log.Write($"desktop layer: {ClassOf(layer)} 0x{layer:X} (icons in it: {FindWindowEx(layer, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero})");
        var (screens, mainAt) = Arrange();
        int partner = mainAt + 1 < screens.Length ? mainAt + 1 : mainAt - 1;   // the screen beside the main one (the right one if there is one) takes the clock and Winamp
        for (int i = 0; i < screens.Length; i++) scenes.Add(new Scene(this, screens[i], i, layer) { Slot = i - mainAt, Role = i == mainAt ? "main" : i == partner ? "partner" : "extra" });
        Log.Write("screens: " + string.Join(", ", scenes.Select(c => $"{c.Screen.DeviceName} {c.Screen.Bounds.Width}x{c.Screen.Bounds.Height} {c.Role} {c.Slot:+0;-0;0}")));
        screenSig = ScreenSig();
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) => scenes[0].BeginInvoke(ScreensChanged);
        overlay = new Overlay(Env.Value);
        var main = scenes.First(s => s.Role == "main");
        switcher = new Switcher(ShowDesktop, main.Shot);
        Shell.HideShell();

        menuKey = new MsgWindow("ConsoleDesktopMenu", m => { if (m.Msg == WM_HOTKEY) ToggleMenu("Win+Esc"); });
        if (!RegisterHotKey(menuKey.Handle, 1, 0x8 | 0x4000 /*MOD_WIN | MOD_NOREPEAT*/, 0x1B /*VK_ESCAPE*/)) Log.Write("WARNING: Win+Esc is taken; the menu opens from the PS button only");
        Rescan();
        media = new Media(p => scenes[0].BeginInvoke(() => { nowPlaying = p; Broadcast("nowPlaying", p); }), h => scenes[0].BeginInvoke(() => Broadcast("heard", h)));
        Action<string, object?> chatSend = (t, v) => scenes[0].BeginInvoke(() => Broadcast(t, v));
        claude = Cfg.Chat == "claude-code" ? new Claude(chatSend) : new ApiChat(chatSend);
        spotify = new Spotify((t, v) => scenes[0].BeginInvoke(() => Broadcast(t, v)));
        voice = new Voice((t, v) => { if (t == "wake") Tts.Warm(); scenes[0].BeginInvoke(() => Broadcast(t, v)); }) { Level = l => overlay.BeginInvoke(() => overlay.Level(l)) };   // "Hey Marina": load the read-aloud voice (Tts.cs) while the ask is still being said
        input = new Input(() => scenes[0].BeginInvoke(() => ToggleMenu("PS button")), keys => scenes[0].BeginInvoke(() => lifted?.Send("pad", keys)));
        PopOut.Back = (at, v) => { if (lifted == null) Lift(scenes.FirstOrDefault(s => s.Screen.Bounds.Contains(at))); lifted!.Send("yt-back", v); };   // the video window's ▭ Page view: back into the Video tab, on its screen
        spectrum = new Spectrum(v => { try { scenes[0].BeginInvoke(() => { foreach (var s in scenes) if (s.WantSpectrum && !s.Covered) s.Send("spectrum", v); }); } catch { } });   // the horizon visualizer (Spectrum.cs, web/horizon.js)

        WatchWindows();
        tick.Tick += (_, _) => Tick();
        tick.Start();
        updateTimer.Tick += async (_, _) => { updateTimer.Interval = 24 * 3600_000; if (await Update.Check() is { } f && f.version != update?.version) { update = f; Log.Write($"update: {f.version} available"); Broadcast("update", f); } };
        updateTimer.Start();   // first check a minute after start (out of the login rush), then daily

        liveWatch = new FileSystemWatcher(Paths.Live) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName, EnableRaisingEvents = true };
        FileSystemEventHandler changed = (_, e) => { if (!e.Name!.StartsWith(".")) scenes[0].BeginInvoke(() => { liveDebounce.Stop(); liveDebounce.Start(); }); };
        liveWatch.Changed += changed; liveWatch.Created += changed; liveWatch.Renamed += (s, e) => changed(s, e);
        liveDebounce.Tick += (_, _) => { liveDebounce.Stop(); LiveChanged(); };
    }

    void Tick()
    {
        if (File.Exists(Path.Combine(Paths.Logs, "hang.now"))) { File.Delete(Path.Combine(Paths.Logs, "hang.now")); Log.Write("hang test: UI thread sleeps 20 s"); Thread.Sleep(20000); }
        Shell.HideShell();
        var wins = CheckCovers();
        var json = JsonSerializer.Serialize(wins);
        if (json != lastWindows) { lastWindows = json; Broadcast("windows", wins); }
        if (DateTime.UtcNow - lastSun > TimeSpan.FromSeconds(60)) { lastSun = DateTime.UtcNow; Broadcast("sun", SunNow()); }
        if (++noteTick % 4 == 0 && Notes.ReadIfChanged() is { } n)   // every 2 s, and only when the store's file changed
        {
            var j = JsonSerializer.Serialize(n);
            if (j != notesJson) { notesJson = j; notes = n; Broadcast("notifications", n); }
        }
    }

    List<Win> CheckCovers()
    {
        var wins = TopWindows();
        foreach (var s in scenes) s.SetCovered(wins.FirstOrDefault(w => !w.minimized && Covers(w, s.Screen.Bounds)) ?? PopOut.Cover(s.Screen.Bounds));   // our own video window filling a screen counts too
        input.Targets = scenes.Select(s => new Input.Target(s.Screen.Bounds, s.Handle, !s.Covered)).ToArray();
        SpectrumCheck();
        return wins;
    }

    /// <summary>Minimize, restore, maximize, a drag ending, a window moving: check the covers now (coalesced to one check per message-loop pass).</summary>
    void WatchWindows()
    {
        bool queued = false;
        winHook = (_, ev, h, obj, child, _, _) =>
        {
            if (queued || (ev == 0x800B /*LOCATIONCHANGE*/ && (obj != 0 || child != 0 || GetParent(h) != IntPtr.Zero))) return;
            queued = true; scenes[0].BeginInvoke(() => { queued = false; CheckCovers(); });
        };
        SetWinEventHook(0x000B, 0x0017, IntPtr.Zero, winHook, 0, 0, 0);   // MOVESIZEEND .. MINIMIZEEND (out of context: runs on this UI thread)
        SetWinEventHook(0x800B, 0x800B, IntPtr.Zero, winHook, 0, 0, 0);
    }
    WinEventProc? winHook;   // kept alive: the hook holds only a pointer to it

    /// <summary>Alt+Tab to "Tidepool": every window over the desktop goes down (without activating anything else), like Win+D.</summary>
    void ShowDesktop()
    {
        var up = TopWindows().Where(w => !w.minimized).ToList();
        foreach (var w in up) ShowWindow(new IntPtr(w.id), 7 /*SW_SHOWMINNOACTIVE*/);
        Log.Write($"alt-tab to Tidepool: {up.Count} window(s) minimized");
        CheckCovers();
    }

    /// <summary>Lively's rule: a maximised window on that screen, or any window covering the whole screen (full screen, borderless games).</summary>
    static bool Covers(Win w, Rectangle screen)
    {
        var r = Rectangle.FromLTRB(w.rect.Left, w.rect.Top, w.rect.Right, w.rect.Bottom);
        if (w.maximized) return screen.Contains(r.X + r.Width / 2, r.Y + r.Height / 2);
        return r.Contains(screen);
    }

    public record Win(long id, string title, string app, bool maximized, bool minimized, string? art) { [System.Text.Json.Serialization.JsonIgnore] public RECT rect; }

    /// <summary>The switcher's list: visible, unowned, not tool windows, not cloaked, titled, not ours, not the shell.</summary>
    List<Win> TopWindows()
    {
        var list = new List<Win>();
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || GetWindow(h, GW_OWNER) != IntPtr.Zero || (GetWindowLong(h, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0 || IsCloaked(h)) return true;
            var title = TitleOf(h);
            if (title.Length == 0 || ClassOf(h) is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return true;
            GetWindowThreadProcessId(h, out uint pid);
            if (pid == Environment.ProcessId) return true;
            if (!exeByPid.TryGetValue(pid, out var exe)) { try { exe = Process.GetProcessById((int)pid).ProcessName.ToLowerInvariant(); } catch { exe = "?"; } exeByPid[pid] = exe; }
            if (!artByPid.TryGetValue(pid, out var art)) { try { art = Process.GetProcessById((int)pid).MainModule?.FileName is { } path ? Icons.Shell(path) : null; } catch { art = null; } artByPid[pid] = art; }   // the program's own icon (null for elevated apps we cannot open)
            GetWindowRect(h, out var r);
            list.Add(new Win(h.ToInt64(), title, exe, IsZoomed(h), IsIconic(h), art) { rect = r });
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>The screens Tidepool runs on, left to right as config.json "screens" arranges them (Setup › Screens), and which one is the main
    /// one (the menu's). Not set: Windows' own left-to-right order and its main display. "off" screens keep the plain Windows wallpaper.
    /// Every screen but the main one is a "second screen": the one beside it takes the spill (clock, Winamp, the row's tail), the rest show the scene.</summary>
    internal static (Screen[] on, int mainAt) Arrange()
    {
        var all = Screen.AllScreens;
        var order = (Cfg.Node("screens.order") as System.Text.Json.Nodes.JsonArray)?.Select(n => n?.GetValue<string>()).ToList() ?? new();
        var off = ((Cfg.Node("screens.off") as System.Text.Json.Nodes.JsonArray)?.Select(n => n?.GetValue<string>()) ?? Enumerable.Empty<string?>()).ToHashSet();
        var sorted = all.OrderBy(s => { int i = order.IndexOf(s.DeviceName); return i < 0 ? int.MaxValue : i; }).ThenBy(s => s.Bounds.X).ThenBy(s => s.Bounds.Y).ToArray();   // arranged ones first, new ones after by position
        var on = sorted.Where(s => !off.Contains(s.DeviceName)).ToArray();
        if (on.Length == 0) on = new[] { Screen.PrimaryScreen ?? all[0] };   // every screen switched off: the main one still runs
        var want = Cfg.Get("screens.main");
        int mainAt = Array.FindIndex(on, s => s.DeviceName == want); if (mainAt < 0) mainAt = Array.FindIndex(on, s => s.Primary); if (mainAt < 0) mainAt = 0;
        return (on, mainAt);
    }

    /// <summary>A monitor plugged in, unplugged or rearranged in Windows: the desktop restarts on the new set (resolution and scale changes alone don't).</summary>
    string screenSig = "";
    static string ScreenSig() => string.Join("|", Screen.AllScreens.OrderBy(s => s.DeviceName).Select(s => $"{s.DeviceName}@{s.Bounds.X},{s.Bounds.Y}{(s.Primary ? "*" : "")}"));
    void ScreensChanged()
    {
        var now = ScreenSig(); if (now == screenSig) return;
        Log.Write($"screens changed ({screenSig} -> {now}): restarting"); screenSig = now; Setup.Restart();
    }

    static object ScreenInfo(Scene c) => new
    {
        index = c.Index, primary = c.Screen.Primary, role = c.Role, slot = c.Slot, x = c.Screen.Bounds.X, y = c.Screen.Bounds.Y,
        width = c.Screen.Bounds.Width, height = c.Screen.Bounds.Height, hz = RefreshRate(c.Screen.DeviceName),
    };

    object SunNow() => new { elevation = Math.Round(Sun.Elevation(DateTime.UtcNow, Lat, Lon), 2), at = DateTime.Now.ToString("o") };

    void LiveChanged() { Broadcast("live", null); Log.Write("live/ changed: page told to re-read it"); }

    /// <summary>Calls from the page: focus or close a window from the switcher, or leave to stock Windows.</summary>
    internal void FromPage(Scene s, JsonElement m)
    {
        var type = m.GetProperty("type").GetString();
        IntPtr Id() => new(m.GetProperty("id").GetInt64());
        switch (type)
        {
            case "ready": SendInitial(s); break;
            case "focus": var h = Id(); if (IsIconic(h)) ShowWindow(h, 9 /*SW_RESTORE*/); ForceForeground(h); break;   // Force: a desktop click went to Explorer, not us, so a plain SetForegroundWindow is refused
            case "launch":
                var id = m.GetProperty("id").GetString() ?? "";
                if (Library.Launch(id)) { if (lifted != null) { lifted.NoStepBack(); Drop(); } }   // the launched thing takes the front; the menu steps back
                break;
            case "snapshot": s.Snapshot(m.TryGetProperty("blur", out var b) && b.TryGetDouble(out var bv) ? bv : 12); break;
            case "claude-ask": claude.Ask(m.GetProperty("text").GetString() ?? "", m.TryGetProperty("images", out var imgs) ? imgs : null); break;
            case "claude-info": claude.Info(); break;
            case "claude-sessions": _ = Task.Run(claude.Sessions); break;
            case "claude-resume": var rid = m.GetProperty("id").GetString() ?? ""; _ = Task.Run(() => claude.Resume(rid)); break;
            case "claude-files": var fq = m.GetProperty("q").GetString() ?? ""; _ = Task.Run(() => claude.Files(fq)); break;
            case "voiceSettings": Process.Start(new ProcessStartInfo("ms-settings:privacy-speech") { UseShellExecute = true }); if (lifted != null) { lifted.NoStepBack(); Drop(); } break;   // the mic needs Windows' online speech recognition on
            case "voice" or "wake" when !Cfg.On("voice"): break;   // Setup: Voice off, or whisper.cpp not installed
            case "voice": if (m.GetProperty("on").GetBoolean()) voice.Start(m.TryGetProperty("quiet", out var vq) && vq.ValueKind == System.Text.Json.JsonValueKind.Number ? vq.GetDouble() : 5); else voice.Stop(); break;
            case "overlay": overlay.Set(m.GetRawText()); break;
            case "spectrum": s.WantSpectrum = m.TryGetProperty("on", out var spo) && spo.ValueKind == System.Text.Json.JsonValueKind.True; SpectrumCheck(); break;   // horizon.js: the visualizer is on and this scene can be seen
            case "wake": voice.Wake(m.GetProperty("on").GetBoolean(), m.TryGetProperty("follow", out var wf) && wf.ValueKind == System.Text.Json.JsonValueKind.Number ? wf.GetDouble() : 0); break;
            case "tts":   // read-aloud through Kokoro (Tts.cs); answered to the asking page only, url null = use speechSynthesis
                var tq = m.GetRawText();
                _ = Task.Run(() =>
                {
                    // id is read here, not in the BeginInvoke: the JsonDocument is disposed by then (14 Nov crash, ObjectDisposedException on the UI thread)
                    int tid = 0; string? url = null;
                    try
                    {
                        using var tj = JsonDocument.Parse(tq); var r = tj.RootElement;
                        tid = r.GetProperty("id").GetInt32();
                        url = Tts.Say(r.GetProperty("text").GetString() ?? "", r.TryGetProperty("voice", out var tv) ? tv.GetString() ?? "af_heart" : "af_heart", r.TryGetProperty("speed", out var ts) && ts.TryGetDouble(out var tsv) ? tsv : 1);
                    }
                    catch (Exception e) { Log.Write("tts request failed: " + e.Message); }
                    try { s.BeginInvoke(() => { try { s.Send("tts", new { id = tid, url }); } catch (Exception e) { Log.Write("tts reply failed: " + e.Message); } }); } catch { }   // scene gone
                });
                break;
            case "listMics": _ = voice.Mics(); break;
            case "setMic": voice.SetMic(m.GetProperty("id").GetString() ?? ""); break;
            case "claude-new": claude.NewChat(); break;
            case "claude-cancel": claude.Cancel(); break;
            case "spotify": spotify.Do(m); break;
            case "yt": YouTube.Do(m, (t, v) => { try { s.BeginInvoke(() => s.Send(t, v)); } catch { } }); break;   // the Video tab; answered to the asking page only
            case "claude-set": claude.Set(m.GetProperty("model").GetString() ?? "", m.GetProperty("effort").GetString() ?? "", m.TryGetProperty("mode", out var md) ? md.GetString() ?? "" : ""); break;
            case "skins":   // Settings › Winamp (Skins.cs): pick = a file dialog, add = a dropped .wsz's bytes, remove = one the user added; answered as "skin-result"
                {
                    var op = m.GetProperty("op").GetString() ?? "";
                    void reply(object v) { try { s.Send("skin-result", v); } catch { } }
                    try
                    {
                        if (op == "remove") { var skid = m.GetProperty("id").GetString() ?? ""; Skins.Remove(skid); reply(new { op, ok = true, id = skid }); }
                        else if (op == "add") reply(new { op, ok = true, got = Skins.Add(null, Convert.FromBase64String(m.GetProperty("data").GetString() ?? ""), m.GetProperty("name").GetString()) });
                        else if (op == "pick")
                        {
                            using var d = new OpenFileDialog { Title = "Add Winamp skins", Filter = "Winamp skins (*.wsz;*.zip)|*.wsz;*.zip", Multiselect = true };
                            if (d.ShowDialog(s) == DialogResult.OK)
                                foreach (var f in d.FileNames)
                                    try { reply(new { op = "add", ok = true, got = Skins.Add(f, null, f) }); }
                                    catch (Exception e) { reply(new { op = "add", ok = false, name = Path.GetFileName(f), error = e.Message }); }
                        }
                    }
                    catch (Exception e) { Log.Write("skins " + op + " failed: " + e.Message); reply(new { op, ok = false, error = e.Message }); }
                }
                break;
            case "sys":   // Marina's PC commands (Sys.cs); answered to the asking page only: { id, r }
                var sop = m.GetProperty("op").GetString() ?? ""; var hasV = m.TryGetProperty("v", out var svv); var sv = hasV && svv.ValueKind == System.Text.Json.JsonValueKind.Number ? svv.GetDouble() : 0; var stx = hasV && svv.ValueKind == System.Text.Json.JsonValueKind.String ? svv.GetString() ?? "" : ""; var sid = m.TryGetProperty("id", out var sidv) ? sidv.GetInt32() : 0;
                _ = Task.Run(() => { object? r = null; try { r = Sys.Do(sop, sv, stx); } catch (Exception e) { Log.Write("sys " + sop + " failed: " + e.Message); } try { s.BeginInvoke(() => s.Send("sys", new { id = sid, r })); } catch { } });
                break;
            case "media": media.Do(m.GetProperty("action").GetString() ?? "", m.TryGetProperty("at", out var at) && at.TryGetDouble(out var atv) ? atv : 0); break;
            case "replay":   // a heard song again, named by its time in the host's own list, so the page never says what runs
                var hat = m.GetProperty("at").GetInt64();
                if (media.HeardList().FirstOrDefault(x => x.at == hat) is { } hh && Media.ReplayWords(hh, m.GetProperty("kind").GetString() ?? "track") is { } words) Spotify.Best(words, m.GetProperty("kind").GetString() ?? "track");
                break;
            case "reveal": if (Library.Reveal(m.GetProperty("id").GetString() ?? "") && lifted != null) { lifted.NoStepBack(); Drop(); } break;   // Explorer takes the front
            case "openUrl": Library.OpenUrl(m.GetProperty("url").GetString() ?? ""); if (lifted != null) { lifted.NoStepBack(); Drop(); } break;
            case "browse":   // a folder's entries for the Files column and preview card (Places.Browse); answered to the asking page only
                var bid = m.GetProperty("id").GetString() ?? "";
                var th = new Thread(() => { var r = Places.Browse(bid); s.BeginInvoke(() => s.Send("folder", r)); }) { IsBackground = true };
                th.SetApartmentState(ApartmentState.STA); th.Start();   // STA: the shell's icon extractors answer E_PENDING on pool threads
                break;
            case "openMenu": if (lifted == null) Lift(m.TryGetProperty("here", out var here) && here.GetBoolean() && !s.Covered ? s : null); break;   // here: the asking page (a click on screen 2's bell opens the menu there)
            case "closeMenu": CloseMenu(); break;
            case "setup": if (lifted != null) { lifted.NoStepBack(); Drop(); } _ = Env.Value.ContinueWith(t => scenes[0].BeginInvoke(() => Setup.Show(t.Result)), TaskScheduler.Default); break;   // Settings › Setup
            case "update-install" when update != null: var uf = update; _ = Task.Run(async () => { try { await Update.Install(uf); } catch (Exception e) { Log.Write("update: " + e.Message); Broadcast("update-failed", e.Message); } }); break;
            case "close": PostMessage(Id(), 0x0010 /*WM_CLOSE*/, IntPtr.Zero, IntPtr.Zero); break;
            case "exitToStock":
                // The watchdog does the leaving (kills us, restores, exits); without one, restore and quit ourselves.
                Log.Write("page asked for stock Windows");
                var dog = FindWindowEx(HWND_MESSAGE, IntPtr.Zero, null, "ConsoleDesktopWatchdog");
                if (dog != IntPtr.Zero) PostMessage(dog, Watchdog.WM_STOCK, IntPtr.Zero, IntPtr.Zero); else ExitThread();
                break;
        }
    }

    void Broadcast(string type, object? value) { foreach (var s in scenes) s.Send(type, value); }
    void SpectrumCheck() => spectrum?.Run(scenes.Any(s => s.WantSpectrum && !s.Covered));   // capture only while a page that wants it can be seen (null: a cover check before the constructor got that far)

    /// <summary>Everything a freshly loaded page needs.</summary>
    internal void SendInitial(Scene s)
    {
        var all = scenes.Select(ScreenInfo).ToArray();
        s.Send("screens", all);          // every monitor, so one scene can run across all of them
        s.Send("screen", all[s.Index]);  // the one this page is on
        s.Send("location", new { lat = Lat, lon = Lon, place = Place.name });
        s.Send("sun", SunNow());
        if (update != null) s.Send("update", update);
        s.Send("windows", TopWindows());
        s.Send("covered", s.Covered);
        s.Send("nowPlaying", nowPlaying); s.Send("heard", media.HeardList()); s.Send("notifications", notes); foreach (var (k, v) in lists) s.Send(k, v);
        s.Send("menu", new { open = lifted == s });
        _ = Task.Run(() => spotify.SendState(true));
    }

    /// <summary>Everything launchable, scanned off the UI thread; the page hears only about lists that changed.</summary>
    void Rescan() { var th = new Thread(ScanNow) { IsBackground = true }; th.SetApartmentState(ApartmentState.STA); th.Start(); }   // STA: first-time shell icons answer E_PENDING on pool threads
    void ScanNow()
    {
        var found = Library.Scan();
        scenes[0].BeginInvoke(() =>
        {
            foreach (var (k, v) in found)
            {
                var j = JsonSerializer.Serialize(v);
                if (listJson.GetValueOrDefault(k) != j) { listJson[k] = j; Broadcast(k, v); }
            }
            lists = found;
            Log.Write("library: " + string.Join(", ", found.Select(x => $"{((System.Collections.ICollection)x.Value).Count} {x.Key}")));
        });
    }

    internal void CloseMenu() { if (lifted != null) Drop(); }

    /// <summary>The main screen, unless windows cover it (a game, a maximised app) and another screen is free: then the menu opens there and leaves the game alone.</summary>
    Scene MenuScene
    {
        get { var main = scenes.First(s => s.Role == "main"); return !main.Covered ? main : scenes.FirstOrDefault(s => s.Role == "partner" && !s.Covered) ?? main; }   // only the partner can stand in: the extra screens have no menu
    }

    void ToggleMenu(string by) { Log.Write($"menu toggle ({by})"); if (lifted == null) Lift(); else Drop(); }

    /// <summary>Menu open: the primary screen's page leaves the desktop layer and becomes a focused full-screen window, so keyboard, mouse and pad reach it.</summary>
    void Lift(Scene? at = null)
    {
        lifted = at is { Role: not "extra" } ? at : MenuScene; input.MenuOpen = true;   // a click on an extra screen opens the menu where it lives
        lifted.Lift();
        lifted.Send("menu", new { open = true });
        Rescan(); _ = Task.Run(() => spotify.SendState());   // Spotifast's cache may have new playlists or likes
    }

    /// <summary>Menu closed: the page goes back behind everything.</summary>
    void Drop()
    {
        var s = lifted!; lifted = null; input.MenuOpen = false;
        s.Drop();
        s.Send("menu", new { open = false });
    }

    protected override void ExitThreadCore()
    {
        tick.Stop();
        UnregisterHotKey(menuKey.Handle, 1); input.Stop(); claude.Stop();
        Shell.Restore("host exit");
        base.ExitThreadCore();
    }

    // One browser environment for all screens (two with different options on one profile folder fail with 0x8007139F).
    // Note: all views live in one WorkerW spanning every monitor, so Chromium paces every view to the main screen's vsync;
    // Chromium's --frame-throttle-fps did not change that (tested), so bridge.js paces requestAnimationFrame per screen instead.
    static Task? cacheCleared;
    static Task ClearCacheOnce(CoreWebView2 w) => cacheCleared ??= w.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.DiskCache | CoreWebView2BrowsingDataKinds.CacheStorage);   // both scenes share one profile: once per start

    bool browserGone;
    /// <summary>WebView2's browser process exited (15 Nov: the desktop stayed blank). Restart the desktop now, or after the chat's reply if one is working.</summary>
    internal void BrowserGone()
    {
        if (browserGone) return; browserGone = true;
        if (claude is Claude c && c.Working) { File.Create(Claude.RestartFlag).Dispose(); Log.Write("WebView2 browser exited: restart queued until the chat's reply ends"); return; }
        Log.Write("WebView2 browser exited: restarting the desktop");
        try { Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--restart") { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory }); } catch (Exception e) { Log.Write("restart failed: " + e.Message); }
    }

    internal static readonly Lazy<Task<CoreWebView2Environment>> Env = new(() => CoreWebView2Environment.CreateAsync(null, Path.Combine(Paths.App, ".wv2"),
        new CoreWebView2EnvironmentOptions("--autoplay-policy=no-user-gesture-required" + (Environment.GetEnvironmentVariable("CD_DEBUG_PORT") is { } port ? $" --remote-debugging-port={port}" : ""))));   // the pad's keys are synthetic: no "user gesture", so render previews would never play

    internal sealed class Scene : Form
    {
        readonly Host host;
        SafeView view = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.Black };
        bool built;   // the handle existed once: a later one means Windows rebuilt the window (layer reset) and the old view is dead

        /// <summary>A WebView2 whose controller died with its window: resizing it threw and took the host down.</summary>
        sealed class SafeView : WebView2
        {
            protected override void OnSizeChanged(EventArgs e) { try { base.OnSizeChanged(e); } catch (InvalidOperationException) { } }
            protected override void OnVisibleChanged(EventArgs e) { try { base.OnVisibleChanged(e); } catch (InvalidOperationException) { } }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (!built) { built = true; return; }
            Log.Write($"scene {Index}: window rebuilt, making a new view");
            BeginInvoke(() =>
            {
                var old = view; view = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.Black, Visible = old.Visible };
                Controls.Remove(old); try { old.Dispose(); } catch { }
                Controls.Add(view); _ = InitView();
            });
        }
        public readonly Screen Screen;
        public readonly int Index;
        public bool Covered { get; private set; }
        public bool WantSpectrum;   // its page asked for "spectrum" events (host.spectrum)
        bool up;   // lifted out of the desktop layer as the menu
        long liftedAt; IntPtr before; bool stepBack = true;   // window that had focus before the menu; given back on close unless the user moved on
        readonly IntPtr layer;

        /// <summary>Its place left to right from the main screen (0; -1 = just left of it) and its part: main | partner | extra (Host.Arrange).</summary>
        public int Slot { get; init; }
        public string Role { get; init; } = "main";
        public Scene(Host host, Screen screen, int index, IntPtr layer)
        {
            this.host = host; Screen = screen; Index = index; this.layer = layer;
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual;
            BackColor = Color.Black; Text = $"ConsoleDesktop scene {index}";
            Controls.Add(view);
            CreateHandle();
            // Child of the desktop layer; its coordinates start at the virtual screen's top-left.
            Visible = true;
            Drop();
            Deactivate += (_, _) => { if (up && Environment.TickCount64 - liftedAt > 800) { Log.Write($"scene {Index}: menu lost focus to \"{TitleOf(GetForegroundWindow())}\""); stepBack = false; BeginInvoke(host.CloseMenu); } };   // clicked or alt-tabbed away: the menu steps back
            _ = InitView();
        }

        async Task InitView()
        {
            try
            {
                int hz = RefreshRate(Screen.DeviceName);
                await view.EnsureCoreWebView2Async(await Env.Value);
                var w = view.CoreWebView2;
                w.Settings.AreDefaultContextMenusEnabled = false; w.Settings.IsStatusBarEnabled = false; w.Settings.IsZoomControlEnabled = false;
                await ClearCacheOnce(w);   // app/web is local files: never run yesterday's cached core.js after an update
                w.SetVirtualHostNameToFolderMapping("app.local", Paths.Web, CoreWebView2HostResourceAccessKind.Allow);
                w.SetVirtualHostNameToFolderMapping("icons.local", Icons.Dir, CoreWebView2HostResourceAccessKind.Allow);   // file, project and site icons
                if (Library.SteamArtDir is { } art) w.SetVirtualHostNameToFolderMapping("steam.local", art, CoreWebView2HostResourceAccessKind.Allow);   // game art
                foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable))   // previews in Files and Studio: pictures, renders, text
                    w.SetVirtualHostNameToFolderMapping($"drive-{char.ToLowerInvariant(d.Name[0])}.local", d.Name, CoreWebView2HostResourceAccessKind.Allow);
                await w.AddScriptToExecuteOnDocumentCreatedAsync(Bridge);
                await w.AddScriptToExecuteOnDocumentCreatedAsync("window.TP_CONFIG = " + JsonSerializer.Serialize(Cfg.ForPage()) + ";");   // what Setup picked: tabs, names, chat (index.html CFG)
                w.WebMessageReceived += (_, e) => { try { host.FromPage(this, JsonDocument.Parse(e.WebMessageAsJson).RootElement); } catch (Exception x) { Log.Write("bad page message: " + x.Message); } };
                w.ProcessFailed += (_, e) => { Log.Write($"scene {Index}: web process failed ({e.ProcessFailedKind})"); if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited) host.BrowserGone(); else w.Reload(); };   // a dead browser can't reload: every view (scenes, overlay) is gone
                w.NavigationCompleted += (_, _) => { var fg = GetForegroundWindow(); if (!up && (fg == Handle || GetParent(fg) == Handle || IsChild(Handle, fg)) && host.startFg != IntPtr.Zero) ForceForeground(host.startFg); };
                w.Navigate(Page);
                Log.Write($"scene {Index}: {Screen.DeviceName} {Screen.Bounds} {hz} Hz, loading {Page}");
            }
            catch (Exception e) { Log.Write($"scene {Index}: WebView2 failed: {e}"); }
        }

        /// <summary>Out of the desktop layer: a top-level window over this screen, in front and focused.</summary>
        public void Lift()
        {
            up = true; liftedAt = Environment.TickCount64; before = GetForegroundWindow(); stepBack = true;
            SetParent(Handle, IntPtr.Zero);
            SetWindowPos(Handle, HWND_TOP, Screen.Bounds.X, Screen.Bounds.Y, Screen.Bounds.Width, Screen.Bounds.Height, 0x0040 /*SHOWWINDOW*/);
            view.Visible = true;
            ForceForeground(Handle);
            Activate(); view.Focus();
            Log.Write($"scene {Index}: menu open (lifted out of the desktop layer)");
        }

        /// <summary>Back into the desktop layer (child of the WorkerW; its coordinates start at the virtual screen's top-left).</summary>
        public void Drop()
        {
            bool was = up; up = false;
            SetParent(Handle, layer);
            var v = SystemInformation.VirtualScreen;
            SetWindowPos(Handle, IntPtr.Zero, Screen.Bounds.X - v.X, Screen.Bounds.Y - v.Y, Screen.Bounds.Width, Screen.Bounds.Height, 0x0040 /*SHOWWINDOW*/);
            if (was && stepBack && before != IntPtr.Zero && IsWindowVisible(before)) ForceForeground(before);
            if (was) Log.Write($"scene {Index}: menu closed (back in the desktop layer)");
        }

        protected override bool ShowWithoutActivation => true;   // never take focus at start; only the menu does
        protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ExStyle |= 0x80 /*WS_EX_TOOLWINDOW*/; return cp; } }   // never in Alt+Tab, even lifted as the menu: "Tidepool" (Switcher) is the one entry

        /// <summary>The page as it is now, full size (the Alt+Tab picture).</summary>
        public async Task<Bitmap?> Shot()
        {
            try { using var png = new MemoryStream(); await view.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, png); png.Position = 0; using var b = new Bitmap(png); return new Bitmap(b); }   // copied: a Bitmap read from a stream needs that stream for life
            catch { return null; }
        }

        /// <summary>For glass without live backdrop blur: this page's current frame, blurred, as a small JPEG data url ("snapshot" event).
        /// The blur is a downscale to about 1/blur then a smooth upscale to half size (the browser stretches it to full).</summary>
        public async void Snapshot(double blur)
        {
            object? reply = null;
            try
            {
                var t0 = Environment.TickCount64;
                using var png = new MemoryStream();
                await view.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, png);
                png.Position = 0;
                using var full = new Bitmap(png);
                int k = Math.Clamp((int)Math.Round(blur), 2, 64), w = full.Width / 2, h = full.Height / 2;
                using var tiny = Resample(full, Math.Max(1, full.Width / k), Math.Max(1, full.Height / k));
                using var soft = Resample(tiny, w, h);
                using var jpg = new MemoryStream();
                soft.Save(jpg, System.Drawing.Imaging.ImageFormat.Jpeg);
                reply = new { url = "data:image/jpeg;base64," + Convert.ToBase64String(jpg.ToArray()), width = w, height = h, ms = Environment.TickCount64 - t0 };
            }
            catch (Exception e) { Log.Write($"scene {Index}: snapshot failed: {e.Message}"); }
            Send("snapshot", reply);
        }

        static Bitmap Resample(Image src, int w, int h)
        {
            var b = new Bitmap(w, h);
            using var g = Graphics.FromImage(b);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            using var wrap = new System.Drawing.Imaging.ImageAttributes(); wrap.SetWrapMode(System.Drawing.Drawing2D.WrapMode.TileFlipXY);   // no dark rim at the edges
            g.DrawImage(src, new Rectangle(0, 0, w, h), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, wrap);
            return b;
        }

        public void NoStepBack() => stepBack = false;   // a launched app takes the front, not the window from before

        public void Send(string type, object? value)
        {
            try { view.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(new { type, value })); } catch { }
        }

        /// <summary>Covered: the page freezes itself (animations paused, nothing redraws), but the view stays shown, so a window moved,
        /// restored or minimized reveals the desktop at once instead of black while WebView2 wakes up (user, 15 Nov).</summary>
        public void SetCovered(Win? by)
        {
            bool now = by != null;
            if (now == Covered) return;
            Covered = now;
            Log.Write(now ? $"scene {Index}: covered by {by!.app} \"{by.title}\" -> paused" : $"scene {Index}: uncovered -> running");
            Send("covered", now);
        }
    }
}
