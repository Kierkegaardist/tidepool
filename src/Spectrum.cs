using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;

namespace ConsoleDesktop;

/// <summary>What the PC is playing, as 32 bands for the horizon visualizer (web/horizon.js) and the Winamp gadget's bars.
/// WASAPI loopback on the default output device (all sound: music, video, games, Marina's voice; by design). 1024-sample Hann FFT
/// into 32 log-spaced bands 40 Hz..16 kHz, fast attack / slow fall, slow automatic gain so quiet and loud volumes look alike.
/// Runs only while a page wants it (host.spectrum(on)) and its screen is not covered (Host.SpectrumCheck). Sends "spectrum" = int[32]
/// of 0..255 about 30 times a second; in silence it decays to zeros, sends one all-zero frame and then stays quiet until sound returns.</summary>
sealed class Spectrum
{
    public const int Bands = 32;
    const int N = 1024;
    readonly Action<int[]> send;
    readonly ManualResetEventSlim want = new(false);
    Thread? thread;

    public Spectrum(Action<int[]> send) { this.send = send; }

    public void Run(bool on)
    {
        if (on == want.IsSet) return;
        if (on) { want.Set(); if (thread == null) { thread = new Thread(Loop) { IsBackground = true, Name = "spectrum" }; thread.Start(); } }
        else want.Reset();
        Log.Write("spectrum " + (on ? "on" : "off"));
    }

    void Loop()
    {
        var ring = new float[N]; int at = 0;
        var win = new float[N]; for (int i = 0; i < N; i++) win[i] = 0.5f - 0.5f * MathF.Cos(2 * MathF.PI * i / (N - 1));
        var fft = new Complex[N]; var level = new float[Bands]; var outv = new int[Bands];
        float refDb = -40; bool quiet = true; Loopback? cap = null; long lastSound = 0, opened = 0, next = Environment.TickCount64;
        while (true)
        {
            if (!want.IsSet) { cap?.Dispose(); cap = null; Array.Clear(ring); Array.Clear(level); quiet = true; want.Wait(); }
            var t0 = Environment.TickCount64;
            try
            {
                if (cap == null) { cap = new Loopback(); opened = lastSound = t0; }
                int got = cap.Read(ring, ref at);
                if (got > 0) lastSound = t0;
                else if (t0 - lastSound > 4000 && t0 - opened > 4000) { cap.Dispose(); cap = null; }   // silent a while: reopen, which also follows a change of default output device
            }
            catch (Exception e) { Log.Write("spectrum: " + e.Message); try { cap?.Dispose(); } catch { } cap = null; Array.Clear(ring); Thread.Sleep(2000); continue; }

            for (int i = 0; i < N; i++) fft[i] = new Complex(ring[(at + i) % N] * win[i], 0);
            Fft(fft);
            float rate = cap?.Rate ?? 48000, bw = rate / N, top = -200;
            var db = new float[Bands];
            for (int b = 0; b < Bands; b++)
            {
                double lo = 40 * Math.Pow(400, b / (double)Bands), hi = 40 * Math.Pow(400, (b + 1) / (double)Bands);
                int k0 = Math.Max(1, (int)(lo / bw)), k1 = Math.Max(k0, Math.Min(N / 2 - 1, (int)Math.Ceiling(hi / bw)));
                double m = 0; for (int k = k0; k <= k1; k++) m = Math.Max(m, fft[k].Magnitude);
                float d = 20 * MathF.Log10((float)(m * 4 / N) + 1e-9f) + 3 * MathF.Log2((float)Math.Sqrt(lo * hi) / 1000);   // full-scale sine = 0 dB; +3 dB/octave so music's falling highs still show
                db[b] = d; top = Math.Max(top, d);
            }
            bool silent = top < -75;
            if (!silent) refDb = top > refDb ? refDb + (top - refDb) * 0.3f : Math.Max(-60, refDb - 0.12f);   // the automatic gain: catches up with loud fast, eases down ~3.6 dB/s
            bool any = false;
            for (int b = 0; b < Bands; b++)
            {
                float v = silent ? 0 : Math.Clamp((db[b] - (refDb - 42)) / 42, 0, 1);
                level[b] = v > level[b] ? level[b] + (v - level[b]) * 0.7f : level[b] * 0.86f;   // fast attack, slow fall
                if (level[b] < 0.01f) level[b] = 0;
                outv[b] = (int)(level[b] * 255); any |= outv[b] > 0;
            }
            if (any || !quiet) send((int[])outv.Clone());   // the last all-zero frame tells the pages it went quiet
            quiet = !any;
            next = Math.Max(next + 33, Environment.TickCount64 - 100); Thread.Sleep((int)Math.Max(1, next - Environment.TickCount64));   // a schedule, not a fixed sleep: Windows sleeps in 15.6 ms steps (a plain Sleep(33) gave 21 fps)
        }
    }

    static void Fft(Complex[] a)   // in-place radix-2
    {
        int n = a.Length;
        for (int i = 1, j = 0; i < n; i++) { int bit = n >> 1; for (; (j & bit) != 0; bit >>= 1) j ^= bit; j ^= bit; if (i < j) (a[i], a[j]) = (a[j], a[i]); }
        for (int len = 2; len <= n; len <<= 1)
        {
            var w = Complex.FromPolarCoordinates(1, -2 * Math.PI / len);
            for (int i = 0; i < n; i += len) { var u = Complex.One; for (int k = 0; k < len / 2; k++) { Complex x = a[i + k], y = a[i + k + len / 2] * u; a[i + k] = x + y; a[i + k + len / 2] = x - y; u *= w; } }
        }
    }

    /// <summary>The default render endpoint captured in loopback, in its own mix format (usually 32-bit float, 48 kHz), mixed down to mono.</summary>
    sealed class Loopback : IDisposable
    {
        readonly IAudioClient client; readonly IAudioCaptureClient capture;
        readonly int channels, bits; readonly bool isFloat;
        public readonly int Rate;
        public Loopback()
        {
            var en = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            Marshal.ThrowExceptionForHR(en.GetDefaultAudioEndpoint(0, 0, out var dev));   // eRender, eConsole
            var iid = typeof(IAudioClient).GUID;
            Marshal.ThrowExceptionForHR(dev.Activate(ref iid, 23, IntPtr.Zero, out var o));
            client = (IAudioClient)o;
            Marshal.ThrowExceptionForHR(client.GetMixFormat(out var fmt));
            try
            {
                int tag = (ushort)Marshal.ReadInt16(fmt, 0); channels = Marshal.ReadInt16(fmt, 2); Rate = Marshal.ReadInt32(fmt, 4); bits = Marshal.ReadInt16(fmt, 14);
                if (tag == 0xFFFE) tag = (ushort)Marshal.ReadInt16(fmt, 24);   // WAVEFORMATEXTENSIBLE: the SubFormat GUID starts with the format tag
                isFloat = tag == 3;
                if (!isFloat && bits != 16 && bits != 32) throw new NotSupportedException($"mix format {tag}/{bits} bit");
                Marshal.ThrowExceptionForHR(client.Initialize(0, 0x00020000 /*LOOPBACK*/, 2_000_000, 0, fmt, IntPtr.Zero));   // shared, 200 ms buffer
            }
            finally { Marshal.FreeCoTaskMem(fmt); }
            var cid = typeof(IAudioCaptureClient).GUID;
            Marshal.ThrowExceptionForHR(client.GetService(ref cid, out var c));
            capture = (IAudioCaptureClient)c;
            Marshal.ThrowExceptionForHR(client.Start());
            Log.Write($"spectrum: loopback {Rate} Hz, {channels} ch, {bits} bit {(isFloat ? "float" : "pcm")}");
        }

        /// <summary>Every waiting packet into the ring (mono); returns the frames read. No packets = silence (loopback sends none then).</summary>
        public int Read(float[] ring, ref int at)
        {
            int total = 0;
            while (capture.GetNextPacketSize(out var n) == 0 && n > 0)
            {
                Marshal.ThrowExceptionForHR(capture.GetBuffer(out var data, out var frames, out var flags, out _, out _));
                bool silentFlag = (flags & 2) != 0;
                for (int f = 0; f < frames; f++)
                {
                    float s = 0;
                    if (!silentFlag)
                        for (int ch = 0; ch < channels; ch++)
                        {
                            int off = (f * channels + ch) * (bits / 8);
                            s += isFloat ? BitConverter.Int32BitsToSingle(Marshal.ReadInt32(data, off)) : bits == 16 ? Marshal.ReadInt16(data, off) / 32768f : Marshal.ReadInt32(data, off) / 2147483648f;
                        }
                    ring[at] = s / channels; at = (at + 1) % ring.Length;
                }
                capture.ReleaseBuffer(frames);
                if (!silentFlag) total += frames;
            }
            return total;
        }

        public void Dispose() { try { client.Stop(); } catch { } Marshal.ReleaseComObject(capture); Marshal.ReleaseComObject(client); }
    }

    // Own copies of the core-audio interfaces (Voice.cs keeps its own private ones); only the slots used are typed.
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator { void _0(); [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice dev); }
    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice { [PreserveSig] int Activate(ref Guid iid, int ctx, IntPtr p, [MarshalAs(UnmanagedType.IUnknown)] out object o); }
    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioClient
    {
        [PreserveSig] int Initialize(int mode, int flags, long buffer, long period, IntPtr fmt, IntPtr session);
        void _1(); void _2(); void _3(); void _4();
        [PreserveSig] int GetMixFormat(out IntPtr fmt);
        void _6();
        [PreserveSig] int Start(); [PreserveSig] int Stop(); void _9(); void _10();
        [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object o);
    }
    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out int frames, out int flags, out long devicePosition, out long qpc);
        [PreserveSig] int ReleaseBuffer(int frames);
        [PreserveSig] int GetNextPacketSize(out int frames);
    }
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumerator { }

    /// <summary>ConsoleDesktop.exe --spectrum: the FFT puts a 1 kHz sine in the 1 kHz bin (exit 1 if not), then 5 s of real loopback frames
    /// to logs/spectrum-test.txt (play something first).</summary>
    internal static int Test()
    {
        var a = new Complex[N]; for (int i = 0; i < N; i++) a[i] = new Complex(Math.Sin(2 * Math.PI * 1000 * i / 48000.0), 0);
        Fft(a);
        int peak = 1; for (int k = 1; k < N / 2; k++) if (a[k].Magnitude > a[peak].Magnitude) peak = k;
        var outFile = Path.Combine(Paths.Logs, "spectrum-test.txt");
        File.WriteAllText(outFile, $"fft: 1 kHz sine -> bin {peak} = {peak * 48000.0 / N:F0} Hz{Environment.NewLine}");
        if (Math.Abs(peak * 48000.0 / N - 1000) > 48000.0 / N) return 1;
        int frames = 0; var sw = Stopwatch.StartNew();
        var sp = new Spectrum(v => { frames++; File.AppendAllText(outFile, $"{sw.ElapsedMilliseconds,5} {string.Join(" ", v.Select(x => x.ToString().PadLeft(3)))}{Environment.NewLine}"); });
        sp.Run(true); Thread.Sleep(5000); sp.Run(false);
        File.AppendAllText(outFile, $"{frames} frames in 5 s{Environment.NewLine}");
        return 0;
    }
}
