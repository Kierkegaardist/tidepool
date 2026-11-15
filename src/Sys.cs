using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ConsoleDesktop;

/// <summary>Marina's plain PC commands (index.html localAsk, user 15 Nov: "a well rounded function bot without AI"): the volume, lock,
/// sleep, shut down, a screenshot, and a status line (disks, memory, GPU, uptime). Do() returns what the page reads back.</summary>
static class Sys
{
    public static object? Do(string op, double v, string text = "")
    {
        switch (op)
        {
            case "vol": return Vol();                                     // { vol 0..100, muted }
            case "volset": using (var e = Ep()) e.V.SetMasterVolumeLevelScalar((float)Math.Clamp(v / 100, 0, 1), IntPtr.Zero); return Vol();
            case "volstep": using (var e = Ep()) { e.V.GetMasterVolumeLevelScalar(out var f); e.V.SetMasterVolumeLevelScalar((float)Math.Clamp(f + v / 100, 0, 1), IntPtr.Zero); } return Vol();
            case "mute": using (var e = Ep()) e.V.SetMute(v != 0, IntPtr.Zero); return Vol();
            case "lock": LockWorkStation(); return null;
            case "sleep": SetSuspendState(false, false, false); return null;
            case "shutdown": Process.Start(new ProcessStartInfo("shutdown", "/s /t 5") { CreateNoWindow = true, UseShellExecute = false }); return null;
            case "restart": Process.Start(new ProcessStartInfo("shutdown", "/r /t 5") { CreateNoWindow = true, UseShellExecute = false }); return null;
            case "shot": Keys(0x5B, 0x2C); return null;                    // Win+PrtScn: Windows saves it in the Screenshots folder (Pictures\Skjermbilder here)
            case "desk": Keys(0x5B, 0x44); return null;                    // Win+D: show the desktop
            case "stats": return Stats();
            case "miss": File.AppendAllText(Path.Combine(Paths.Logs, "marina-misses.txt"), $"{DateTime.Now:yyyy-MM-dd HH:mm}\t{text.Replace('\t', ' ').Replace('\n', ' ')}\n"); return null;   // asks Marina did not get (marina.js), to learn new phrasings from
        }
        return null;
    }

    static object Vol() { using var e = Ep(); e.V.GetMasterVolumeLevelScalar(out var f); e.V.GetMute(out var m); return new { vol = (int)Math.Round(f * 100), muted = m }; }

    static object Stats()
    {
        var mem = new MEMORYSTATUSEX { len = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() }; GlobalMemoryStatusEx(ref mem);
        var disks = DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).Select(d => new { d = d.Name[..1], free = d.AvailableFreeSpace / 1e9, size = d.TotalSize / 1e9 }).ToArray();
        string? gpu = null;
        try
        {
            using var p = Process.Start(new ProcessStartInfo("nvidia-smi", "--query-gpu=temperature.gpu,utilization.gpu --format=csv,noheader,nounits") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true })!;
            gpu = p.StandardOutput.ReadToEnd().Trim(); if (!p.WaitForExit(3000)) p.Kill();
        }
        catch { }   // no NVIDIA card or driver tools: no GPU line
        return new { disks, memUsed = mem.load, memTotal = mem.total / 1e9, gpu, upHours = Environment.TickCount64 / 36e5 };
    }

    // the default speakers' volume (IAudioEndpointVolume); the enumerator and device are released with it
    sealed class Endpoint : IDisposable
    {
        public readonly IAudioEndpointVolume V; readonly object en, dev;
        public Endpoint()
        {
            en = new MMDeviceEnumerator();
            Marshal.ThrowExceptionForHR(((IMMDeviceEnumerator)en).GetDefaultAudioEndpoint(0 /*render*/, 1 /*multimedia*/, out var d)); dev = d;
            var iid = typeof(IAudioEndpointVolume).GUID;
            Marshal.ThrowExceptionForHR(d.Activate(ref iid, 23 /*CLSCTX_ALL*/, IntPtr.Zero, out var o)); V = (IAudioEndpointVolume)o;
        }
        public void Dispose() { Marshal.ReleaseComObject(V); Marshal.ReleaseComObject(dev); Marshal.ReleaseComObject(en); }
    }
    static Endpoint Ep() => new();

    static void Keys(params byte[] vk)
    {
        foreach (var k in vk) keybd_event(k, 0, 0, UIntPtr.Zero);
        foreach (var k in vk.Reverse()) keybd_event(k, 0, 2 /*KEYUP*/, UIntPtr.Zero);
    }

    [DllImport("user32.dll")] static extern bool LockWorkStation();
    [DllImport("powrprof.dll")] static extern bool SetSuspendState(bool hibernate, bool force, bool noWake);
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);
    [StructLayout(LayoutKind.Sequential)] struct MEMORYSTATUSEX { public uint len, load; public ulong total, avail, totalPage, availPage, totalVirtual, availVirtual, availExt; }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator { void _0(); [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice dev); }
    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice { [PreserveSig] int Activate(ref Guid iid, int ctx, IntPtr p, [MarshalAs(UnmanagedType.IUnknown)] out object o); }
    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioEndpointVolume
    {
        void _0(); void _1(); void _2(); void _3();   // Register/UnregisterControlChangeNotify, GetChannelCount, SetMasterVolumeLevel
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, IntPtr ctx);
        void _5();                                     // GetMasterVolumeLevel
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        void _7(); void _8(); void _9(); void _10();   // the per-channel ones
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, IntPtr ctx);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumerator { }
}
