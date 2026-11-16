using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace ConsoleDesktop;

/// <summary>The Video tab's player windows (web/pop.html), meant to move like a browser tab (user, 15 Nov: "put on a video and flick it to the
/// right screen ... prop it up wherever"). Each is a plain sizable window, so Windows drags, resizes, snaps and maximizes it, and on top of that:
///  - one window per video (up to <see cref="Most"/>, the oldest closes); the same video again just comes forward;
///  - a new one opens where the last one was left (screen, size, maximized; app\.popout.json);
///  - flick: let go of the title bar mid-throw toward another screen and it glides there and fills it; thrown up, it fills its own screen;
///  - the page can open it filling a given screen (`to`: the other screen, or the one it was flicked toward in the menu);
///  - in the window: F / double-click = full screen on its screen, S = to the next screen, 1-9 / the screen picker = to that screen, T = on top or not,
///    ▭ Page view = back into the Video tab (watch page, Up next) at the same second, Ctrl+W = close.
/// It stays on top only while floating and pinned (picture in picture). Filling a screen or full screen it is part of that screen's desktop
/// (user, 16 Nov: "more your desktop than a window above it"), so any window can cover it. A pop-out that fills a screen counts as covering it (Host.CheckCovers), so that screen's desktop pauses like under any window.
/// The page hands it the stream urls it already has (host.yt.pop), so it starts without another yt-dlp run.</summary>
sealed class PopOut : Form
{
    const int Most = 4;
    /// <summary>▭ Page view: (the point the window was on, { id, t }); Host lifts the menu there and the page plays it in the Video tab.</summary>
    public static Action<Point, object>? Back;
    static readonly List<PopOut> all = new();
    static string StateFile => Path.Combine(Paths.App, ".popout.json");
    record Place(int x, int y, int w, int h, bool max, bool pin);
    static Place? saved = LoadPlace();

    readonly WebView2 view = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.Black };
    string? pending, id; bool ready, pin = saved?.pin ?? true, full;
    Rectangle beforeFull; bool beforeFullMax;

    /// <summary>{ id, title, video, audio, t, vol, embed, to? ({ x, y } of a screen: open filling that screen) } from the page (BRIDGE.md host.yt.pop).</summary>
    public static void Open(string json)
    {
        string? vid = null; Screen? to = null;
        try
        {
            using var d = JsonDocument.Parse(json); var r = d.RootElement; vid = r.GetProperty("id").GetString();
            if (r.TryGetProperty("to", out var t) && t.ValueKind == JsonValueKind.Object) to = Screen.FromPoint(new Point(t.GetProperty("x").GetInt32() + 8, t.GetProperty("y").GetInt32() + 8));
        }
        catch { }
        var p = all.FirstOrDefault(x => x.id == vid && !x.IsDisposed);
        if (p == null)
        {
            while (all.Count >= Most) all[0].Close();
            p = new PopOut { id = vid };
            all.Add(p);
        }
        p.Play(json);
        if (to != null) p.Fill(to, glide: p.Visible && p.ScreenName() != to.DeviceName);
    }

    /// <summary>A pop-out filling this screen (maximized on it, or full screen over it), as a window the desktop can count as a cover.</summary>
    public static Host.Win? Cover(Rectangle screen)
    {
        var p = all.FirstOrDefault(x => x.Visible && !x.IsDisposed && x.WindowState != FormWindowState.Minimized && (x.full || x.WindowState == FormWindowState.Maximized)
            && screen.Contains(x.Bounds.X + x.Bounds.Width / 2, x.Bounds.Y + x.Bounds.Height / 2));
        return p == null ? null : new Host.Win(p.Handle.ToInt64(), p.Text, "video", true, false, null) { rect = new Native.RECT { Left = p.Bounds.Left, Top = p.Bounds.Top, Right = p.Bounds.Right, Bottom = p.Bounds.Bottom } };
    }

    PopOut()
    {
        Text = "Video"; BackColor = Color.Black; ShowInTaskbar = true; StartPosition = FormStartPosition.Manual;
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        Controls.Add(view);
        CreateHandle();
        int dark = 1; DwmSetWindowAttribute(Handle, 20 /*DWMWA_USE_IMMERSIVE_DARK_MODE*/, ref dark, 4);
        float k = DeviceDpi / 96f;
        MinimumSize = new Size((int)(240 * k), (int)(160 * k));
        var s = saved; var r = s == null ? Rectangle.Empty : new Rectangle(s.x, s.y, s.w, s.h);
        if (r.IsEmpty || !Screen.AllScreens.Any(x => x.WorkingArea.IntersectsWith(r)))   // first time, or that screen is gone: bottom right of the main screen
        {
            var wa = Screen.PrimaryScreen!.WorkingArea; var sz = new Size((int)(640 * k), (int)(360 * k) + SystemInformation.CaptionHeight);
            r = new Rectangle(wa.Right - sz.Width - (int)(32 * k), wa.Bottom - sz.Height - (int)(32 * k), sz.Width, sz.Height);
        }
        int n = all.Count(x => !x.IsDisposed && x.Visible); r.Offset(-(int)(28 * k) * n, -(int)(28 * k) * n);   // another one already there: a step up and left of it
        Bounds = r;
        if (s?.max == true) WindowState = FormWindowState.Maximized;
        Resize += (_, _) => OnTop();
        FormClosing += (_, _) => Save();
        FormClosed += (_, _) => all.Remove(this);
        OnTop();
        _ = Init();
    }

    protected override bool ShowWithoutActivation => true;   // the menu keeps the keys; a click on the window gives them to it

    async Task Init()
    {
        try
        {
            await view.EnsureCoreWebView2Async(await Host.Env.Value);
            var w = view.CoreWebView2;
            w.Settings.AreDefaultContextMenusEnabled = false; w.Settings.IsStatusBarEnabled = false; w.Settings.IsZoomControlEnabled = false;
            w.SetVirtualHostNameToFolderMapping("app.local", Paths.Web, CoreWebView2HostResourceAccessKind.Allow);
            w.DocumentTitleChanged += (_, _) => Text = w.DocumentTitle;
            w.ContainsFullScreenElementChanged += (_, _) => Full(w.ContainsFullScreenElement);   // YouTube's own player (embed) asked for full screen: the window goes full screen with it
            w.NavigationCompleted += (_, _) => { ready = true; if (pending != null) w.PostWebMessageAsJson(pending); pending = null; Tell(); };
            w.WebMessageReceived += (_, e) =>
            {
                string op = ""; int i = -1; double t = 0;
                try
                {
                    using var d = JsonDocument.Parse(e.WebMessageAsJson); var r = d.RootElement; op = r.GetProperty("op").GetString() ?? "";
                    if (r.TryGetProperty("i", out var iv)) i = iv.GetInt32();
                    if (r.TryGetProperty("t", out var tv)) t = tv.GetDouble();
                }
                catch { }
                switch (op)
                {
                    case "full": Full(!full); break;
                    case "unfull": if (full) Full(false); break;
                    case "next": Next(); break;
                    case "to": if (i >= 0 && i < Screens.Length) MoveTo(Screens[i]); break;
                    case "back": var at = new Point(Bounds.X + Bounds.Width / 2, Bounds.Y + Bounds.Height / 2); Close(); Back?.Invoke(at, new { id, t }); break;
                    case "pin": pin = !pin; OnTop(); Save(); break;
                    case "close": Close(); break;
                }
            };
            w.Navigate("https://app.local/pop.html?" + Environment.TickCount64);
        }
        catch (Exception e) { Log.Write("pop-out: WebView2 failed: " + e.Message); }
    }

    void Play(string json)
    {
        if (ready) view.CoreWebView2.PostWebMessageAsJson(json); else pending = json;
        if (!Visible) Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        if (all.Count > 1) BringToFront();
    }

    string ScreenName() => Screen.FromControl(this).DeviceName;
    /// <summary>On top only while floating and pinned (T); filling a screen or full screen it sits in that screen's desktop, coverable like any window.</summary>
    void OnTop() { TopMost = !full && pin && WindowState == FormWindowState.Normal; Tell(); }
    void Tell()
    {
        if (!ready) return;
        var ss = Screens; int scr = Array.FindIndex(ss, s => s.DeviceName == ScreenName());
        try { view.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { state = new { pin, full, max = WindowState == FormWindowState.Maximized, scr, n = ss.Length } })); } catch { }
    }

    // ---- moving between screens ----
    static Screen[] Screens => Screen.AllScreens.OrderBy(s => s.Bounds.X).ThenBy(s => s.Bounds.Y).ToArray();
    static Screen? Side(Screen from, int dir) =>   // the nearest screen to the right (dir 1) or left (-1) of this one
        Screen.AllScreens.Where(s => dir > 0 ? s.Bounds.Left >= from.Bounds.Right - 8 : s.Bounds.Right <= from.Bounds.Left + 8).OrderBy(s => Math.Abs(s.Bounds.X - from.Bounds.X)).FirstOrDefault();

    /// <summary>Fill that screen (maximized there), gliding over to it.</summary>
    void Fill(Screen to, bool glide = true)
    {
        if (full) { Full(false); }
        if (WindowState == FormWindowState.Maximized) { WindowState = FormWindowState.Normal; glide = false; }
        void done() { WindowState = FormWindowState.Maximized; OnTop(); Save(); }
        if (glide && Visible) Glide(to.WorkingArea, done); else { Bounds = Centered(to.WorkingArea, Size); done(); }
    }

    /// <summary>S: to the next screen.</summary>
    void Next()
    {
        var ss = Screens; if (ss.Length < 2) return;
        MoveTo(ss[(Array.FindIndex(ss, s => s.DeviceName == ScreenName()) + 1) % ss.Length]);
    }

    /// <summary>The screen picker / 1-9: to that screen, as it is (full screen stays full screen, filling stays filling, floating keeps its spot).</summary>
    void MoveTo(Screen to)
    {
        var here = Screen.FromControl(this); if (here.DeviceName == to.DeviceName) return;
        if (full) { Bounds = to.Bounds; Save(); return; }
        if (WindowState == FormWindowState.Maximized) { Fill(to); return; }
        var r = Bounds; r.Offset(to.WorkingArea.X - here.WorkingArea.X, to.WorkingArea.Y - here.WorkingArea.Y);
        r.Intersect(to.WorkingArea); if (r.Width < MinimumSize.Width || r.Height < MinimumSize.Height) r = Centered(to.WorkingArea, Size);
        Glide(r, Save);
    }

    /// <summary>F / double-click: full screen over its whole screen (taskbar too), and back to where it was.</summary>
    void Full(bool on)
    {
        if (on == full) return;
        if (on)
        {
            beforeFullMax = WindowState == FormWindowState.Maximized; beforeFull = beforeFullMax ? RestoreBounds : Bounds;
            var scr = Screen.FromControl(this).Bounds; full = true;
            WindowState = FormWindowState.Normal; FormBorderStyle = FormBorderStyle.None; Bounds = scr;
        }
        else
        {
            full = false; FormBorderStyle = FormBorderStyle.Sizable; Bounds = beforeFull;
            if (beforeFullMax) WindowState = FormWindowState.Maximized;
        }
        OnTop();
    }

    static Rectangle Centered(Rectangle area, Size s)
    {
        var w = Math.Min(s.Width, area.Width); var h = Math.Min(s.Height, area.Height);
        return new Rectangle(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h);
    }

    int glideN;
    async void Glide(Rectangle to, Action? then)   // ease-out over 220 ms; a newer glide or a drag stops this one
    {
        var my = ++glideN; var from = Bounds; long t0 = Environment.TickCount64;
        while (my == glideN)
        {
            double k = Math.Min(1, (Environment.TickCount64 - t0) / 220.0), e = 1 - Math.Pow(1 - k, 3);
            Bounds = new Rectangle((int)(from.X + (to.X - from.X) * e), (int)(from.Y + (to.Y - from.Y) * e), (int)(from.Width + (to.Width - from.Width) * e), (int)(from.Height + (to.Height - from.Height) * e));
            if (k >= 1) { then?.Invoke(); return; }
            await Task.Delay(10);
        }
    }

    // ---- the flick: where the title bar was in the last tenth of a second before letting go ----
    readonly List<(long t, int x, int y)> trail = new(); bool sizing; Screen? dragFrom;
    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case 0x0231: trail.Clear(); sizing = false; glideN++; dragFrom = Screen.FromControl(this); break;   // WM_ENTERSIZEMOVE
            case 0x0214: sizing = true; break;                                                                    // WM_SIZING: a resize, not a throw
            case 0x0216:                                                                                          // WM_MOVING
                var r = Marshal.PtrToStructure<Native.RECT>(m.LParam); trail.Add((Environment.TickCount64, r.Left, r.Top)); if (trail.Count > 16) trail.RemoveAt(0); break;
            case 0x0232:                                                                                          // WM_EXITSIZEMOVE
                base.WndProc(ref m);
                if (!sizing && !Flick()) Save(); else if (sizing) Save();
                return;
        }
        base.WndProc(ref m);
    }

    bool Flick()
    {
        if (trail.Count < 3 || WindowState != FormWindowState.Normal || full || dragFrom == null) return false;   // Windows snapped or maximized it: leave that
        var last = trail[^1]; long now = Environment.TickCount64;
        if (now - last.t > 80) return false;                                    // it stopped before being let go: a plain drag
        var first = trail.First(p => last.t - p.t <= 100); double dt = last.t - first.t; if (dt < 12) return false;
        double vx = (last.x - first.x) / dt, vy = (last.y - first.y) / dt;   // px per ms
        float k = DeviceDpi / 96f;
        if (Math.Abs(vx) > 1.4 * k && Math.Abs(vx) > Math.Abs(vy) * 1.3 && Side(dragFrom, Math.Sign(vx)) is { } to) { Fill(to); return true; }
        if (vy < -1.6 * k && Math.Abs(vy) > Math.Abs(vx) * 1.3) { Fill(Screen.FromControl(this)); return true; }   // thrown up: fill this screen
        return false;
    }

    // ---- where it was left ----
    void Save()
    {
        Tell();   // the screen picker follows where it went
        if (full || WindowState == FormWindowState.Minimized) { if (full && saved != null) saved = saved with { pin = pin }; return; }
        var max = WindowState == FormWindowState.Maximized; var r = max ? RestoreBounds : Bounds;
        if (max) { var wa = Screen.FromControl(this).WorkingArea; r = Centered(wa, r.Size); }   // restore it on the screen it filled
        saved = new Place(r.X, r.Y, r.Width, r.Height, max, pin);
        try { File.WriteAllText(StateFile, JsonSerializer.Serialize(saved)); } catch { }
    }
    static Place? LoadPlace() { try { return File.Exists(StateFile) ? JsonSerializer.Deserialize<Place>(File.ReadAllText(StateFile)) : null; } catch { return null; } }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);
}
