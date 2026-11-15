using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;
using static ConsoleDesktop.Native;

namespace ConsoleDesktop;

/// <summary>The "Hey Marina" overlay (web/overlay.html): a small window at the primary screen's top-left, above every app, that never takes
/// focus or clicks. The page's host.overlay(state, said, reply) lands here; the overlay page says "gone" once it has faded out, and the window hides.
/// Real per-pixel alpha (soft shadows, see-through glass): WebView2 renders into a DirectComposition visual on a window with no redirection
/// surface (overlay redesign, 14 Nov; the old colour-key version is in concepts/polish/overlay/old).</summary>
sealed class Overlay : Form
{
    CoreWebView2CompositionController? view;
    IDCompositionDevice? dcomp; IDCompositionTarget? target; IDCompositionVisual? root;   // held so they live as long as the window
    string? pending; bool ready;

    public Overlay(Task<CoreWebView2Environment> env)
    {
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual; TopMost = true;
        Text = "ConsoleDesktop overlay";
        var (scr, mainAt) = Host.Arrange(); var b = scr[mainAt].Bounds; Location = b.Location;   // the main screen's top-left (Setup › Screens), not always Windows' primary
        CreateHandle();
        SetLayeredWindowAttributes(Handle, 0, 255, 2 /*LWA_ALPHA*/);   // a layered window shows nothing until this; layered + TRANSPARENT = clicks go through
        float k = DeviceDpi / 96f;
        Bounds = new Rectangle(b.X, b.Y, (int)(560 * k), (int)(400 * k));   // Marina's bowl at the rail orb's spot + her text bubble beside it, the rest is room for glow and shadow
        _ = Init(env);
    }

    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get { var p = base.CreateParams; p.ExStyle |= 0x08000000 /*NOACTIVATE*/ | 0x80 /*TOOLWINDOW*/ | 0x8 /*TOPMOST*/ | 0x20 /*TRANSPARENT*/ | 0x80000 /*LAYERED*/ | 0x00200000 /*NOREDIRECTIONBITMAP*/; return p; }
    }
    protected override void OnPaintBackground(PaintEventArgs e) { }   // no GDI surface to paint

    async Task Init(Task<CoreWebView2Environment> env)
    {
        try
        {
            var iid = typeof(IDCompositionDevice).GUID;
            Marshal.ThrowExceptionForHR(DCompositionCreateDevice2(IntPtr.Zero, ref iid, out var dev));
            dcomp = (IDCompositionDevice)dev;
            Marshal.ThrowExceptionForHR(dcomp.CreateTargetForHwnd(Handle, true, out target));
            Marshal.ThrowExceptionForHR(dcomp.CreateVisual(out root));
            Marshal.ThrowExceptionForHR(target.SetRoot(root));
            view = await (await env).CreateCoreWebView2CompositionControllerAsync(Handle);
            view.DefaultBackgroundColor = Color.Transparent;
            view.RootVisualTarget = root;
            view.Bounds = new Rectangle(Point.Empty, ClientSize);
            view.IsVisible = Visible;   // hidden, the browser's own input window (see ClickThrough) must be hidden too, or it eats the rail's clicks
            Marshal.ThrowExceptionForHR(dcomp.Commit());
            var w = view.CoreWebView2;
            w.Settings.AreDefaultContextMenusEnabled = false; w.Settings.IsStatusBarEnabled = false; w.Settings.IsZoomControlEnabled = false;
            w.SetVirtualHostNameToFolderMapping("app.local", Paths.Web, CoreWebView2HostResourceAccessKind.Allow);
            w.WebMessageReceived += (_, _) => { view.IsVisible = false; Hide(); };   // "gone": faded out
            w.NavigationCompleted += (_, _) => { ready = true; if (pending != null) w.PostWebMessageAsJson(pending); pending = null; };
            var pl = Cfg.Place; var ic = System.Globalization.CultureInfo.InvariantCulture;
            w.Navigate($"https://app.local/overlay.html?lat={pl.lat.ToString(ic)}&lon={pl.lon.ToString(ic)}&t={Environment.TickCount64}");   // never yesterday's cached page; lat/lon = the bowl's sky (Marina's bowl, overlay.html)
        }
        catch (Exception e) { Log.Write("overlay: WebView2 failed: " + e.Message); }
    }

    /// <summary>A state from the page: { state, said, reply } as JSON. Shows the window without activating it.</summary>
    public void Set(string json)
    {
        if (!Visible) Show();
        if (view != null) view.IsVisible = true;
        if (ready) view!.CoreWebView2.PostWebMessageAsJson(json); else pending = json;
        ClickThrough();
    }

    /// <summary>Visual hosting gives WebView2 its own top-level input window (Chrome_WidgetWin_1 in msedgewebview2.exe) at the overlay's
    /// bounds, not click-through: it ate clicks on the rail's Games/Music/Studio below the card and hid the desktop from Input.OnDesktop
    /// (bug 14 Nov; and again 15 Nov from startup until the first "Hey Marina", while it sat there hidden-but-visible: now view.IsVisible follows the window). Found by browser pid + exact bounds (so the scenes' select popups stay clickable) and given WS_EX_TRANSPARENT.</summary>
    void ClickThrough()
    {
        if (view == null) return;
        uint pid; try { pid = (uint)view.CoreWebView2.BrowserProcessId; } catch { return; }
        var b = Bounds;
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out var p);
            if (p != pid || !GetWindowRect(h, out var r) || r.Left != b.Left || r.Top != b.Top || r.Right != b.Right || r.Bottom != b.Bottom) return true;
            var ex = GetWindowLong(h, GWL_EXSTYLE);
            if ((ex & 0x20) == 0) SetWindowLong(h, GWL_EXSTYLE, ex | 0x20 /*TRANSPARENT*/ | 0x80000 /*LAYERED: TRANSPARENT alone does not pass clicks*/);
            return true;
        }, IntPtr.Zero);
    }

    /// <summary>The mic's loudest sample in the last 50 ms (0..1) while a "Hey Marina" ask records: the page's live meter.</summary>
    public void Level(float peak) { if (ready && Visible) view!.CoreWebView2.PostWebMessageAsJson($"{{\"level\":{peak.ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture)}}}"); }

    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int index, int value);
    [DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);
    [DllImport("dcomp.dll")] static extern int DCompositionCreateDevice2(IntPtr renderingDevice, ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object device);
    [ComImport, Guid("C37EA93A-E7AA-450D-B16F-9746CB0407F3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDCompositionDevice
    {
        [PreserveSig] int Commit(); void _WaitForCommitCompletion(); void _GetFrameStatistics();
        [PreserveSig] int CreateTargetForHwnd(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool topmost, out IDCompositionTarget target);
        [PreserveSig] int CreateVisual(out IDCompositionVisual visual);
    }
    [ComImport, Guid("eacdd04c-117e-4e17-88f4-d1b12b0e3d89"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDCompositionTarget { [PreserveSig] int SetRoot(IDCompositionVisual visual); }
    [ComImport, Guid("4d93059d-097b-4651-9a60-f0f25116e2f3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDCompositionVisual { }
}
