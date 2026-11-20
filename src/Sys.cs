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
            case "pvol": return PVol(null, null);                        // the music player's own volume (Cfg.Player), not the speakers': { vol, muted } or null when it has no sound session
            case "pvolset": return PVol(v / 100, null);
            case "pvolstep": return PVol(null, null, v / 100);
            case "pmute": return PVol(null, v != 0);
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

    /// <summary>Every sound session of the player's processes (an Electron player has a few) gets the same level; the first one is read back.</summary>
    static object? PVol(double? set, bool? mute, double step = 0)
    {
        var match = Cfg.Player.match; if (match.Length == 0) return null;
        using var e = Ep();
        var iid = typeof(IAudioSessionManager2).GUID;
        Marshal.ThrowExceptionForHR(e.Dev.Activate(ref iid, 23, IntPtr.Zero, out var o)); var mgr = (IAudioSessionManager2)o;
        object? r = null;
        try
        {
            Marshal.ThrowExceptionForHR(mgr.GetSessionEnumerator(out var en));
            en.GetCount(out var n);
            for (int i = 0; i < n; i++)
            {
                en.GetSession(i, out var c);
                try
                {
                    ((IAudioSessionControl2)c).GetProcessId(out var pid); string name;
                    try { using var p = Process.GetProcessById((int)pid); name = p.ProcessName.ToLowerInvariant(); } catch { continue; }
                    if (!match.Any(name.Contains)) continue;
                    var sv = (ISimpleAudioVolume)c; sv.GetMasterVolume(out var f);
                    if (r == null && step != 0) set = Math.Clamp(f + step, 0, 1);   // every session steps to the first one's level
                    if (set is { } x) { sv.SetMasterVolume((float)Math.Clamp(x, 0, 1), IntPtr.Zero); f = (float)Math.Clamp(x, 0, 1); }
                    if (mute is { } m) sv.SetMute(m, IntPtr.Zero);
                    sv.GetMute(out var mm);
                    r ??= new { vol = (int)Math.Round(f * 100), muted = mm };
                }
                finally { Marshal.ReleaseComObject(c); }
            }
            Marshal.ReleaseComObject(en);
        }
        finally { Marshal.ReleaseComObject(mgr); }
        return r;
    }

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
        public readonly IAudioEndpointVolume V; readonly object en, dev; public IMMDevice Dev => (IMMDevice)dev;
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
    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionManager2 { void _0(); void _1(); [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator e); }
    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionEnumerator { void GetCount(out int n); void GetSession(int i, [MarshalAs(UnmanagedType.IUnknown)] out object c); }
    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionControl2 { void _0(); void _1(); void _2(); void _3(); void _4(); void _5(); void _6(); void _7(); void _8(); void _9(); void _10(); void GetProcessId(out uint pid); }
    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ISimpleAudioVolume
    {
        void SetMasterVolume(float level, IntPtr ctx); void GetMasterVolume(out float level);
        void SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, IntPtr ctx); void GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumerator { }
}
