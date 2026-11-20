using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using static ConsoleDesktop.Native;

namespace ConsoleDesktop;

/// <summary>Tidepool's own taskbar (web/dock.html; user, 18 Nov: "maybe we need our own tidepool taskbar", auto-hide, "movable so i can put it on the side").
/// A glass bar on one edge of one screen, hidden until the mouse rests on that edge (or while a full-screen window is in front: never). It slides
/// out, and back in half a second after the mouse leaves it. Drag its empty space anywhere and it snaps to the screen edge nearest the drop
/// (app\.dock.json). Holds: the menu orb, one icon per open window (click = front / minimize when it is in front, middle-click = close), the tray, the clock.
/// ponytail: a plain window (no per-pixel alpha, so square-ish rounded corners from a region, no see-through); DirectComposition like Overlay.cs
/// would give real glass but has to forward the mouse itself.</summary>
sealed class Dock : Form
{
    public Action? Menu;   // the orb: open / close Tidepool's menu
    public bool Pinned;    // tests: always out
    record Spot(string edge, string screen);
    static string StateFile => Path.Combine(Paths.App, ".dock.json");

    readonly WebView2 view = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(14, 26, 38) };
    readonly System.Windows.Forms.Timer poll = new() { Interval = 50 };
    string edge = "bottom", screenName = ""; int len = 400;   // len: the page's length along the edge (device px)
    double t; bool outWanted, moving, ready;   // t: 0 = in, 1 = out
    long edgeSince, awaySince; IntPtr fg; string lastJson = "";
    List<Host.Win> wins = new();
    float K => DeviceDpi / 96f;
    int Thick => (int)(60 * K);
    int Gap => (int)(6 * K);

    public Dock(Task<CoreWebView2Environment> env)
    {
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual; TopMost = true;
        Text = "Tidepool dock"; BackColor = view.DefaultBackgroundColor;
        try { var s = JsonSerializer.Deserialize<Spot>(File.ReadAllText(StateFile)); if (s != null) { edge = s.edge; screenName = s.screen; } } catch { }
        Controls.Add(view);
        CreateHandle();
        poll.Tick += (_, _) => Poll();
        poll.Start();
        Place(); Show();   // never hidden: all the way in, its region is empty (no pixels, no clicks); a hidden WebView2 came back blank for a moment
        _ = Init(env);
    }

    public IntPtr GiveBack;   // the window in front when Tidepool started (Host.startFg)
    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams { get { var p = base.CreateParams; p.ExStyle |= 0x08000000 /*NOACTIVATE*/ | 0x80 /*TOOLWINDOW*/ | 0x8 /*TOPMOST*/; return p; } }

    Screen Scr => Screen.AllScreens.FirstOrDefault(s => s.DeviceName == screenName) ?? Screen.PrimaryScreen!;
    bool Vertical => edge is "left" or "right";

    async Task Init(Task<CoreWebView2Environment> env)
    {
        try
        {
            await view.EnsureCoreWebView2Async(await env);
            var w = view.CoreWebView2;
            w.Settings.AreDefaultContextMenusEnabled = false; w.Settings.IsStatusBarEnabled = false; w.Settings.IsZoomControlEnabled = false;
            w.SetVirtualHostNameToFolderMapping("app.local", Paths.Web, CoreWebView2HostResourceAccessKind.Allow);
            w.SetVirtualHostNameToFolderMapping("icons.local", Icons.Dir, CoreWebView2HostResourceAccessKind.Allow);   // the programs' own icons (Host.TopWindows art)
            w.NavigationCompleted += (_, _) =>
            {
                ready = true; lastJson = ""; Tell();
                var f = GetForegroundWindow(); if ((f == Handle || IsChild(Handle, f)) && GiveBack != IntPtr.Zero) ForceForeground(GiveBack);   // WebView2 takes the focus while it loads, NOACTIVATE or not: the app you were in gets it back (as the scenes do)
            };
            w.WebMessageReceived += (_, e) => { try { From(JsonDocument.Parse(e.WebMessageAsJson).RootElement); } catch (Exception x) { Log.Write("dock: bad message: " + x.Message); } };
            w.Navigate("https://app.local/dock.html?" + Environment.TickCount64);
        }
        catch (Exception e) { Log.Write("dock: WebView2 failed: " + e.Message); }
    }

    void From(JsonElement m)
    {
        var op = m.GetProperty("op").GetString();
        IntPtr Id() => new(m.GetProperty("id").GetInt64());
        switch (op)
        {
            case "size": var n = (int)Math.Ceiling(m.GetProperty("n").GetDouble() * K); if (n != len) { len = n; Place(); } break;
            case "click":
                var h = Id();
                if (h == fg && !IsIconic(h)) ShowWindow(h, 6 /*SW_MINIMIZE*/);   // like the taskbar: the window in front goes down
                else { if (IsIconic(h)) ShowWindow(h, 9 /*SW_RESTORE*/); ForceForeground(h); }
                break;
            case "close": PostMessage(Id(), 0x0010 /*WM_CLOSE*/, IntPtr.Zero, IntPtr.Zero); break;
            case "menu": Menu?.Invoke(); break;
            case "tray": Shell.OpenTray(Cursor.Position, edge); break;
            case "drag": grab = Cursor.Position; at = Location; moving = true; poll.Interval = 16; break;   // followed in Poll until the button comes up (Windows' own WM_NCLBUTTONDOWN move loop never started from inside the view)
        }
    }

    /// <summary>The open windows (Host.TopWindows), from every cover check.</summary>
    public void Windows(List<Host.Win> list) { wins = list; Tell(); }

    void Tell()
    {
        if (!ready) return;
        var j = JsonSerializer.Serialize(new { wins = wins.Select(w => new { w.id, w.title, w.app, w.art, w.minimized }), fg = fg.ToInt64(), edge });
        if (j == lastJson) return; lastJson = j;
        try { view.CoreWebView2.PostWebMessageAsJson(j); } catch { }
    }

    // ---- where it sits ----
    Rectangle Out()   // the bar's spot when it is out
    {
        var b = Scr.Bounds; int l = Math.Min(len, (Vertical ? b.Height : b.Width) - 4 * Gap);
        return edge switch
        {
            "top" => new Rectangle(b.X + (b.Width - l) / 2, b.Top + Gap, l, Thick),
            "left" => new Rectangle(b.Left + Gap, b.Y + (b.Height - l) / 2, Thick, l),
            "right" => new Rectangle(b.Right - Gap - Thick, b.Y + (b.Height - l) / 2, Thick, l),
            _ => new Rectangle(b.X + (b.Width - l) / 2, b.Bottom - Gap - Thick, l, Thick),
        };
    }

    /// <summary>Puts the bar at t of the way out, clipped to its screen (sliding past a side edge must not show on the neighbouring screen).</summary>
    void Place()
    {
        if (moving) return;
        var r = Out(); int off = (int)Math.Round((1 - Ease(t)) * (Thick + Gap + 2));
        r.Offset(edge switch { "left" => -off, "right" => off, _ => 0 }, edge switch { "top" => -off, "bottom" => off, _ => 0 });
        Bounds = r;
        var vis = Rectangle.Intersect(r, Scr.Bounds); vis.Offset(-r.X, -r.Y);
        int rad = (int)(36 * K);   // the ellipse's size: 18 px corners, as in dock.html
        var shape = CreateRoundRectRgn(0, 0, r.Width + 1, r.Height + 1, rad, rad);
        var clip = CreateRectRgn(vis.Left, vis.Top, vis.Right, vis.Bottom);
        CombineRgn(shape, shape, clip, 1 /*RGN_AND*/); DeleteObject(clip);
        SetWindowRgn(Handle, shape, true);   // the window owns the region now
    }
    static double Ease(double x) => 1 - Math.Pow(1 - x, 3);

    // ---- auto-hide ----
    void Poll()
    {
        if (moving) { Drag(); return; }
        var c = Cursor.Position; var b = Scr.Bounds; long now = Environment.TickCount64;
        var f = GetForegroundWindow();
        if (f != IntPtr.Zero && f != Handle && f != fg) { fg = f; Tell(); }   // clicking the bar may still hand it focus: "in front" stays the user's window
        bool full = f != IntPtr.Zero && f != Handle && !IsZoomed(f) && GetWindowRect(f, out var fr) && fr.Left <= b.Left && fr.Top <= b.Top && fr.Right >= b.Right && fr.Bottom >= b.Bottom
            && ClassOf(f) is not ("Progman" or "WorkerW") && !TitleOf(f).StartsWith("ConsoleDesktop scene");   // a game or video full screen in front: the bar stays in (the lifted menu is not one)
        bool onEdge = b.Contains(c) && edge switch { "top" => c.Y <= b.Top, "left" => c.X <= b.Left, "right" => c.X >= b.Right - 1, _ => c.Y >= b.Bottom - 1 } && (GetAsyncKeyState(0x01) & 0x8000) == 0;   // not while dragging a window across
        var hot = Bounds; hot.Inflate(Gap * 2, Gap * 2);
        if (Pinned) outWanted = true;
        else if (!outWanted) { if (onEdge && !full) { if (edgeSince == 0) edgeSince = now; else if (now - edgeSince > 120) outWanted = true; } else edgeSince = 0; }   // a short rest on the edge: crossing to the next screen does not open it
        else if (full) outWanted = false;
        else if (t > 0 && hot.Contains(c) || onEdge || IsWindowVisible(FindWindow("NotifyIconOverflowWindow", null))) awaySince = 0;   // over it, or its tray flyout is open
        else if (awaySince == 0) awaySince = now;
        else if (now - awaySince > 500) { outWanted = false; awaySince = 0; edgeSince = 0; }

        double goal = outWanted ? 1 : 0;
        if (t == goal) { poll.Interval = 50; return; }
        t = Math.Clamp(t + (goal > t ? 1 : -1) * 0.016 / 0.16, 0, 1);   // 160 ms either way
        poll.Interval = 16; Place();
    }

    // ---- drag to another edge ----
    Point grab, at; bool dragged;
    void Drag()
    {
        var c = Cursor.Position;
        if ((GetAsyncKeyState(0x01) & 0x8000) != 0)   // still held: the bar follows the mouse (past a few px, so a click on the clock stays a click)
        {
            if (!dragged && Math.Abs(c.X - grab.X) + Math.Abs(c.Y - grab.Y) < 6 * K) return;
            dragged = true; SetWindowRgn(Handle, CreateRoundRectRgn(0, 0, Width + 1, Height + 1, (int)(36 * K), (int)(36 * K)), true);   // unclipped while it travels
            Location = new Point(at.X + c.X - grab.X, at.Y + c.Y - grab.Y); return;
        }
        moving = false; if (!dragged) return; dragged = false;
        var s = Screen.FromPoint(c); var b = s.Bounds;   // let go: the nearest edge of the screen it was dropped on
        var d = new[] { ("top", c.Y - b.Top), ("bottom", b.Bottom - c.Y), ("left", c.X - b.Left), ("right", b.Right - c.X) };
        edge = d.MinBy(x => x.Item2).Item1; screenName = s.DeviceName; t = 1; outWanted = true; awaySince = 0;
        try { File.WriteAllText(StateFile, JsonSerializer.Serialize(new Spot(edge, screenName))); } catch { }
        Log.Write($"dock: moved to the {edge} of {screenName}");
        Tell(); Place();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0021) { m.Result = (IntPtr)3; return; }   // WM_MOUSEACTIVATE -> MA_NOACTIVATE: clicks leave the user's window in front
        base.WndProc(ref m);
    }

    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] static extern int SetWindowRgn(IntPtr h, IntPtr rgn, bool redraw);
    [DllImport("gdi32.dll")] static extern IntPtr CreateRoundRectRgn(int l, int t, int r, int b, int w, int h);
    [DllImport("gdi32.dll")] static extern IntPtr CreateRectRgn(int l, int t, int r, int b);
    [DllImport("gdi32.dll")] static extern int CombineRgn(IntPtr dst, IntPtr a, IntPtr b, int mode);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);
}
