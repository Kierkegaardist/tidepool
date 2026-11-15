using System.Runtime.InteropServices;

namespace ConsoleDesktop;

/// <summary>The desktop's one Alt+Tab entry, "Tidepool" (user, 15 Nov: the two scene windows listed themselves; now they are tool windows).
/// An invisible window (opacity 0, off-screen) whose Alt+Tab picture is the main screen's page; switching to it minimizes every window, so the desktop shows.</summary>
sealed class Switcher : Form
{
    readonly Action shown; readonly Func<Task<Bitmap?>> shot;

    public Switcher(Action shown, Func<Task<Bitmap?>> shot)
    {
        this.shown = shown; this.shot = shot;
        Text = "Tidepool"; FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = true;   // taskbar-eligible = listed in Alt+Tab (the taskbar itself is hidden)
        StartPosition = FormStartPosition.Manual; Bounds = new Rectangle(-20000, -20000, 1, 1); Opacity = 0;
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { }
        Activated += (_, _) => shown();
        // kept at the bottom of the z-order: Windows activates the next window down when one is minimized or closed, and that must never be this one
        // while anything else is up (it would minimize them all). Only Alt+Tab, or nothing left, reaches it.
        Deactivate += (_, _) => Bottom();
        HandleCreated += (_, _) => { int on = 1; DwmSetWindowAttribute(Handle, 7 /*FORCE_ICONIC_REPRESENTATION*/, ref on, 4); DwmSetWindowAttribute(Handle, 10 /*HAS_ICONIC_BITMAP*/, ref on, 4); };
        Show(); Bottom();
    }

    void Bottom() => SetWindowPos(Handle, (IntPtr)1 /*HWND_BOTTOM*/, 0, 0, 0, 0, 0x13 /*NOSIZE|NOMOVE|NOACTIVATE*/);

    protected override bool ShowWithoutActivation => true;

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0323 /*WM_DWMSENDICONICTHUMBNAIL*/) { var l = m.LParam.ToInt64(); _ = Thumb((int)(l >> 16) & 0xFFFF, (int)l & 0xFFFF); return; }
        base.WndProc(ref m);
    }

    /// <summary>Alt+Tab asks for a picture up to w x h: the page as it is now, fitted (DWM takes the answer whenever it comes).</summary>
    async Task Thumb(int w, int h)
    {
        try
        {
            using var src = await shot(); if (src == null || w < 1 || h < 1) return;
            double k = Math.Min((double)w / src.Width, (double)h / src.Height);
            using var b = new Bitmap(Math.Max(1, (int)(src.Width * k)), Math.Max(1, (int)(src.Height * k)), System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(b)) { g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic; g.DrawImage(src, 0, 0, b.Width, b.Height); }
            var hb = b.GetHbitmap(Color.FromArgb(0));
            try { DwmSetIconicThumbnail(Handle, hb, 0); } finally { DeleteObject(hb); }
        }
        catch (Exception e) { Log.Write("alt-tab picture failed: " + e.Message); }
    }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);
    [DllImport("dwmapi.dll")] static extern int DwmSetIconicThumbnail(IntPtr h, IntPtr bmp, int flags);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int hgt, uint flags);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);
}
