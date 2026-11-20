using System.Runtime.InteropServices;
using System.Text;
using static ConsoleDesktop.Native;

namespace ConsoleDesktop;

/// <summary>Everything that touches Explorer: hide/show its taskbars, find the desktop layer, repaint the wallpaper.</summary>
static class Shell
{
    static List<IntPtr> Taskbars()
    {
        var list = new List<IntPtr>();
        EnumWindows((h, _) => { if (ClassOf(h) is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") list.Add(h); return true; }, IntPtr.Zero);
        return list;
    }

    /// <summary>The desktop icons: the FolderView list inside SHELLDLL_DefView (in Progman or a WorkerW).</summary>
    static IntPtr Icons()
    {
        var icons = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            var view = FindWindowEx(h, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (view != IntPtr.Zero) icons = FindWindowEx(view, IntPtr.Zero, "SysListView32", null);
            return icons == IntPtr.Zero;
        }, IntPtr.Zero);
        return icons;
    }

    /// <summary>The user's own Explorer "Show desktop icons" setting, so restore never shows icons they had hidden.</summary>
    static bool UserShowsIcons() => Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "HideIcons", 0) is not 1;

    /// <summary>Hides any visible Explorer taskbar and the desktop icons, and gives maximised windows the taskbar strip. Called on every host tick, so whatever Explorer re-shows (Start, Explorer restart) goes again.
    /// Window hiding only, never the saved setting: if everything dies, an Explorer restart brings both back.</summary>
    public static void HideShell()
    {
        foreach (var h in Taskbars()) if (IsWindowVisible(h)) ShowWindow(h, SW_HIDE);
        var icons = Icons(); if (icons != IntPtr.Zero && IsWindowVisible(icons)) ShowWindow(icons, SW_HIDE);
        foreach (var s in Screen.AllScreens) if (s.WorkingArea != s.Bounds) SetWorkArea(s.Bounds);
    }

    /// <summary>The tray (Windows' hidden-icons flyout): clicks the hidden taskbar's chevron, then moves the flyout from the top-left corner (where it lands
    /// with no taskbar) to just under the click, or beside it away from the dock's edge (Dock.cs). ponytail: no chevron when "always show all icons" is on; then nothing opens.</summary>
    public static void OpenTray(Point at, string edge = "top")
    {
        var chev = FindWindowEx(FindWindowEx(FindWindow("Shell_TrayWnd", null), IntPtr.Zero, "TrayNotifyWnd", null), IntPtr.Zero, "Button", null);
        if (chev == IntPtr.Zero) { Log.Write("tray: no chevron"); return; }
        PostMessage(chev, 0x00F5 /*BM_CLICK*/, IntPtr.Zero, IntPtr.Zero);
        Task.Run(async () =>
        {
            var o = IntPtr.Zero;
            for (int i = 0; i < 20 && !(IsWindowVisible(o = FindWindow("NotifyIconOverflowWindow", null))); i++) await Task.Delay(25);
            if (!IsWindowVisible(o)) return;
            GetWindowRect(o, out var r);
            var s = Screen.FromPoint(at).Bounds; int w = r.Right - r.Left, h = r.Bottom - r.Top, gap = 40;
            var (x, y) = edge switch { "bottom" => (at.X - w / 2, at.Y - gap - h), "left" => (at.X + gap, at.Y - h / 2), "right" => (at.X - gap - w, at.Y - h / 2), _ => (at.X - w / 2, at.Y + 24) };
            SetWindowPos(o, IntPtr.Zero, Math.Clamp(x, s.Left, s.Right - w), Math.Clamp(y, s.Top, s.Bottom - h), 0, 0, 0x0001 | 0x0004 | 0x0010 /*NOSIZE|NOZORDER|NOACTIVATE*/);
        });
    }

    public static void ShowShell()
    {
        foreach (var h in Taskbars()) ShowWindow(h, SW_SHOWNA);
        var icons = Icons(); if (icons != IntPtr.Zero && UserShowsIcons()) ShowWindow(icons, SW_SHOWNA);
        foreach (var s in Screen.AllScreens) SetWorkArea(StockWorkArea(s.Bounds));
    }

    /// <summary>Sets the work area of the screen that holds r; SPIF_SENDCHANGE makes maximised windows follow. Not saved (no SPIF_UPDATEINIFILE).</summary>
    static void SetWorkArea(Rectangle r)
    {
        var rc = new RECT { Left = r.Left, Top = r.Top, Right = r.Right, Bottom = r.Bottom };
        SystemParametersInfo(0x002F /*SPI_SETWORKAREA*/, 0, ref rc, 2 /*SPIF_SENDCHANGE*/);
    }

    /// <summary>The screen minus the strip each taskbar on it covers (taskbars must be shown first); the whole screen when the taskbar auto-hides.</summary>
    static Rectangle StockWorkArea(Rectangle screen)
    {
        var wa = screen;
        var abd = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>() };
        if (((long)SHAppBarMessage(4 /*ABM_GETSTATE*/, ref abd) & 1 /*ABS_AUTOHIDE*/) != 0) return wa;
        foreach (var h in Taskbars())
        {
            if (!GetWindowRect(h, out var t)) continue;
            var tb = Rectangle.FromLTRB(t.Left, t.Top, t.Right, t.Bottom);
            var hit = Rectangle.Intersect(tb, screen);
            if (hit.IsEmpty) continue;
            if (hit.Width >= screen.Width) { if (hit.Top <= screen.Top) wa = Rectangle.FromLTRB(wa.Left, hit.Bottom, wa.Right, wa.Bottom); else wa = Rectangle.FromLTRB(wa.Left, wa.Top, wa.Right, hit.Top); }
            else if (hit.Height >= screen.Height) { if (hit.Left <= screen.Left) wa = Rectangle.FromLTRB(hit.Right, wa.Top, wa.Right, wa.Bottom); else wa = Rectangle.FromLTRB(wa.Left, wa.Top, hit.Left, wa.Bottom); }
        }
        return wa;
    }

    /// <summary>The WorkerW behind the desktop icons (the layer Lively draws in). Progman's 0x052C message makes Explorer create it.</summary>
    public static IntPtr DesktopLayer()
    {
        var progman = FindWindow("Progman", null);
        SendMessageTimeout(progman, 0x052C, IntPtr.Zero, IntPtr.Zero, 0, 1000, out _);
        var worker = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            if (FindWindowEx(h, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero) worker = FindWindowEx(IntPtr.Zero, h, "WorkerW", null);
            return true;
        }, IntPtr.Zero);
        return worker != IntPtr.Zero ? worker : progman;
    }

    /// <summary>Re-applies the current wallpaper (same file, not saved), so no frame of ours stays painted behind the icons.
    /// The saved path can point at a file that was moved since (user, 20 Nov: black desktop after every restore); then Explorer's own cached copy is used.</summary>
    public static void RepaintWallpaper()
    {
        var sb = new StringBuilder(1024);
        if (!SystemParametersInfo(0x0073 /*SPI_GETDESKWALLPAPER*/, (uint)sb.Capacity, sb, 0) || sb.Length == 0) return;   // a solid colour: nothing to repaint
        var path = sb.ToString();
        if (!File.Exists(path)) path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Themes\TranscodedWallpaper");
        if (File.Exists(path)) SystemParametersInfo(0x0014 /*SPI_SETDESKWALLPAPER*/, 0, new StringBuilder(path), 0);
        else Log.Write("wallpaper file missing, left as it is: " + sb);
    }

    /// <summary>Stock Windows: taskbars, desktop icons and work area back, wallpaper repainted. Safe to call any number of times.</summary>
    public static void Restore(string reason)
    {
        ShowShell();
        RepaintWallpaper();
        Log.Write($"stock Windows restored ({reason})");
    }
}

static class Log
{
    static readonly object gate = new();
    public static void Write(string line)
    {
        try { lock (gate) File.AppendAllText(Path.Combine(Paths.Logs, "console-desktop.log"), $"{DateTime.Now:HH:mm:ss} [{Environment.ProcessId}] {line}\n"); } catch { }
    }
}

static class Paths
{
    /// <summary>The app folder: the first folder above the exe that holds web\index.html (the project's app\ here, the install folder on another PC).
    /// Everything the desktop writes (config.json, logs, icon cache, browser profile) lives in it, so the installer puts it under the user's own AppData.</summary>
    public static readonly string App = FindApp();
    public static string Web => Path.Combine(App, "web");
    public static string Live => Path.Combine(Web, "live");
    public static string Logs { get { var d = Path.Combine(App, "logs"); Directory.CreateDirectory(d); return d; } }

    static string FindApp()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "web", "index.html"))) return d.FullName;
        throw new DirectoryNotFoundException("the app folder (with web/index.html) was not found above " + AppContext.BaseDirectory);
    }
}
