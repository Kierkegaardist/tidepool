using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace ConsoleDesktop;

/// <summary>Icon files for the rows, cached in app/.icons and served to the page as https://icons.local/&lt;file&gt;.</summary>
static class Icons
{
    public static string Dir { get { var d = Path.Combine(Paths.App, ".icons"); Directory.CreateDirectory(d); return d; } }
    const string Url = "https://icons.local/";

    static string Hash(string s) => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(s.ToLowerInvariant())))[..16];

    /// <summary>Bytes saved once under a name made from their own content (album art for the heard list: one file per cover); returns its icons.local url.
    /// Named by content, not by album: a player can send the last song's cover with the new song's title, and a name per album kept that wrong cover for good.</summary>
    public static string Save(byte[] data, string ext)
    {
        var name = "art-" + Convert.ToHexString(SHA1.HashData(data))[..16] + ext; var file = Path.Combine(Dir, name);
        if (!File.Exists(file)) File.WriteAllBytes(file, data);
        return Url + name;
    }

    /// <summary>The Windows shell icon for a file at 128 px with its transparency (what Explorer shows). One per file type, except programs and shortcuts, which have their own.</summary>
    public static string? Shell(string path)
    {
        var item = path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase);   // a shell address (Store apps: shell:AppsFolder\<app id>), not a file
        if (!item) path = Path.GetFullPath(path);   // the shell refuses mixed slashes (Epic manifests: "...\rocketleague\Binaries/Win64/Launcher.exe")
        var ext = item ? "" : Path.GetExtension(path).ToLowerInvariant();
        var name = "shell2-" + (ext is ".exe" or ".lnk" or ".url" or ".ico" or "" ? Hash(path) : ext.TrimStart('.')) + ".png";
        var file = Path.Combine(Dir, name);
        if (File.Exists(file)) return Url + name;
        try
        {
            SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out var f);
            f.GetImage(new SIZE { cx = 128, cy = 128 }, 0x4 /*SIIGBF_ICONONLY*/, out var hbm);
            try { using var bmp = WithAlpha(hbm); using var cut = Trim(bmp); cut.Save(file, ImageFormat.Png); }
            finally { DeleteObject(hbm); Marshal.ReleaseComObject(f); }
            return Url + name;
        }
        catch (Exception e) { Log.Write($"icon for {Path.GetFileName(path)} failed: {e.Message}"); return null; }
    }

    /// <summary>A program that only ships a 32 px icon comes back as a speck in the middle of the 128 px canvas: cut it to its visible pixels (plus a little air) so the page scales it up like the rest.</summary>
    static Bitmap Trim(Bitmap b)
    {
        int x0 = b.Width, y0 = b.Height, x1 = -1, y1 = -1;
        var d = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        var px = new byte[d.Stride * d.Height]; Marshal.Copy(d.Scan0, px, 0, px.Length); b.UnlockBits(d);
        for (int y = 0; y < b.Height; y++) for (int x = 0; x < b.Width; x++) if (px[y * d.Stride + x * 4 + 3] > 8) { x0 = Math.Min(x0, x); x1 = Math.Max(x1, x); y0 = Math.Min(y0, y); y1 = Math.Max(y1, y); }
        var side = Math.Max(x1 - x0, y1 - y0) + 1;
        if (x1 < 0 || side > b.Width * 0.6) return new Bitmap(b);   // empty, or already big enough: as it is
        side += side / 8; int cx = (x0 + x1) / 2, cy = (y0 + y1) / 2;   // square, centred on the icon, 1/16 air each side
        return b.Clone(new Rectangle(Math.Clamp(cx - side / 2, 0, b.Width - side), Math.Clamp(cy - side / 2, 0, b.Height - side), side, side), b.PixelFormat);
    }

    /// <summary>Image.FromHbitmap drops the alpha channel; copy the 32-bit pixels as they are.</summary>
    static Bitmap WithAlpha(IntPtr hbm)
    {
        using var tmp = Image.FromHbitmap(hbm);
        var b = new Bitmap(tmp.Width, tmp.Height, PixelFormat.Format32bppPArgb);   // the shell hands premultiplied alpha
        var d = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
        GetBitmapBits(hbm, d.Stride * d.Height, d.Scan0);
        b.UnlockBits(d);
        return b;
    }

    /// <summary>Saves an icon from Firefox's favicon store (bytes as stored: PNG, ICO, SVG...) and returns its url.</summary>
    public static string Save(string key, byte[] data)
    {
        string ext = data.Length > 4 && data[0] == 0x89 && data[1] == 'P' ? "png" : data.Length > 4 && data[0] == 0 && data[1] == 0 && data[2] == 1 ? "ico"
            : Encoding.ASCII.GetString(data, 0, Math.Min(256, data.Length)).Contains("<svg") ? "svg" : data.Length > 2 && data[0] == 0xFF && data[1] == 0xD8 ? "jpg" : "png";
        var name = "web-" + Hash(key) + "." + ext;
        var file = Path.Combine(Dir, name);
        if (!File.Exists(file)) File.WriteAllBytes(file, data);
        return Url + name;
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItemImageFactory { void GetImage(SIZE size, int flags, out IntPtr hbm); }
    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    static extern void SHCreateItemFromParsingName(string path, IntPtr bc, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IShellItemImageFactory item);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);
    [DllImport("gdi32.dll")] static extern int GetBitmapBits(IntPtr h, int count, IntPtr bits);
}
