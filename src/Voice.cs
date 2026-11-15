using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Windows.Devices.Enumeration;
using Windows.Media.Devices;

namespace ConsoleDesktop;

/// <summary>The mic buttons (search boxes, the Claude panel): records the default mic and local Whisper (whisper.cpp, Cfg.Whisper) types it live.
/// Windows' online dictation returned no words on this PC (13 Nov 2026), its offline engine was too inaccurate.
/// Live text: while you talk the open phrase is re-read about once a second ("partial", its last words can still change); a short pause
/// (CommitPause) locks that phrase in. A listen ends on a click, after the page's pause length once speech was heard (0 = only on click),
/// or after NoSpeech (or the pause, if longer) without any. Events "voice": { state: "listening" | "partial" | "end" | "error", text, id }.</summary>
sealed class Voice
{
    static readonly string Dir = Cfg.Whisper?.models ?? Path.Combine(Cfg.Extras, "whisper"), Cli = Cfg.Whisper?.cli ?? Path.Combine(Dir, "whisper-cli.exe"), Model = Path.Combine(Dir, "ggml-base.en.bin");   // config.json "tools.whisper" or app\extras\whisper (Cfg.Whisper)   // ggml-small.en-q5_1: better casing, 3 s instead of 1.2 s
    const int Rate = 16000;   // what Whisper reads: 16 kHz mono 16-bit; Windows converts the mic to it (AUTOCONVERTPCM)
    static readonly TimeSpan NoSpeech = TimeSpan.FromSeconds(8), Longest = TimeSpan.FromMinutes(5), CommitPause = TimeSpan.FromSeconds(0.8);
    const int LongestPhrase = 25 * Rate, PartialEvery = Rate * 7 / 10;   // a phrase is cut at 25 s (Whisper reads 30 s windows); re-read once 0.7 s more audio came
    const float MinSpeech = 0.02f, OverFloor = 2.5f;   // calibration: speech = a peak above both this and 2.5x the room noise (quietest 50 ms of the first 0.3 s; HyperX: 0.0005)

    readonly Action<string, object?> send;
    volatile bool on, stopAsked;
    int listens;   // each listen's events carry its number ("id")
    TimeSpan quiet;   // how long a pause ends a listen; zero = only a click does
    internal static string? TestFile;   // tests: play this 16 kHz mono wav in real time instead of the mic (Program --voice <wav>)

    public Voice(Action<string, object?> send) { this.send = send; }

    public void Start(double quietSeconds = 5)
    {
        if (on) return;
        on = true; stopAsked = false; quiet = TimeSpan.FromSeconds(Math.Max(0, quietSeconds));
        var id = ++listens;
        new Thread(() => Listen(id)) { IsBackground = true }.Start();
    }

    public void Stop() { stopAsked = true; }

    /// <summary>This thread records and finds pauses; a second one runs Whisper on phrases (locked in) and on the open phrase (partial).</summary>
    void Listen(int id)
    {
        void Say(string state, string text) => send("voice", new { state, text, id });
        var pcm = new List<short>();            // the whole listen; guarded by lock(pcm), as are the two below
        var locked = new Queue<(int from, int to)>();   // phrases ended by a pause, waiting for their final read
        int open = 0; bool openSpoken = false;  // the open phrase starts at pcm[open]; spoken = it holds speech
        bool recording = true; Exception? failed = null;
        var reader = new Thread(() =>
        {
            var said = ""; int lastPartial = 0;
            try
            {
                while (true)
                {
                    (int from, int to) job = default; bool final = false;
                    lock (pcm)
                    {
                        if (locked.Count > 0) { job = locked.Peek(); final = true; }
                        else if (recording && openSpoken && pcm.Count - lastPartial >= PartialEvery) { job = (open, pcm.Count); final = false; }
                        else if (!recording) break;
                    }
                    if (job.to == 0) { Thread.Sleep(100); continue; }
                    short[] clip; lock (pcm) clip = pcm.GetRange(job.from, job.to - job.from).ToArray();
                    var text = Transcribe(clip);
                    if (final) { lock (pcm) locked.Dequeue(); said = Join(said, text); Say("partial", said); }
                    else { lastPartial = job.to; if (on) Say("partial", Join(said, text)); }
                }
                Log.Write($"voice: whisper gave {said.Length} chars");
                Say("end", said);
            }
            catch (Exception e) { failed = e; }
        }) { IsBackground = true };
        try
        {
            using var mic = new Mic();
            Say("listening", "");
            reader.Start();
            var t = Stopwatch.StartNew(); TimeSpan lastLoud = TimeSpan.Zero; float floor = 1, top = 0; bool heard = false;
            var buf = new List<short>();
            while (!stopAsked && t.Elapsed < Longest && failed == null)
            {
                Thread.Sleep(50);
                buf.Clear(); mic.Read(buf);
                float peak = 0; foreach (var s in buf) peak = Math.Max(peak, Math.Abs(s / 32768f));
                top = Math.Max(top, peak);
                lock (pcm)
                {
                    pcm.AddRange(buf);
                    if (t.ElapsedMilliseconds < 300) { floor = Math.Min(floor, peak); continue; }   // the quietest 50 ms: the mic's start-up click is no room noise
                    if (peak > Math.Max(MinSpeech, floor * OverFloor)) { heard = openSpoken = true; lastLoud = t.Elapsed; }
                    if (openSpoken && (t.Elapsed - lastLoud > CommitPause || pcm.Count - open > LongestPhrase)) { locked.Enqueue((open, pcm.Count)); open = pcm.Count; openSpoken = false; }
                    else if (!openSpoken && pcm.Count - open > Rate) open = pcm.Count - Rate / 2;   // silence is never read: keep only its last 0.5 s as the next phrase's lead-in
                }
                if (quiet > TimeSpan.Zero && (heard ? t.Elapsed - lastLoud > quiet : t.Elapsed > (quiet > NoSpeech ? quiet : NoSpeech))) break;
            }
            Log.Write($"voice: {t.Elapsed.TotalSeconds:F1} s, heard {heard}, floor {floor:F4}, loudest {top:F3}, by {(stopAsked ? "click" : "silence")}");
        }
        catch (Exception e) { failed ??= e; }
        finally
        {
            lock (pcm) { if (openSpoken) locked.Enqueue((open, pcm.Count)); recording = false; }
            on = false;
        }
        if (reader.IsAlive) reader.Join();
        if (failed != null)
        {
            Log.Write("voice failed: " + failed.Message);
            Say("error", failed.Message);
        }
    }

    // ===== "Hey Marina" (Name): the always-on wake word (user, 14 Nov: "a Jarvis type thing") =====
    // Whisper only runs when someone talks, and then only on a phrase's first 3 s with tiny.en (0.7 s a check). After the wake word the
    // command is recorded until a 2 s pause and read once with base.en. Events "wake": { state: "wake" | "partial" | "heard" | "error", text } (partial = the ask so far, about once a second).
    // Paused while a mic button dictates (on) and while the page asks (Wake(false): it does while replies are read aloud); the first 0.3 s
    // after the mic opens is ignored (its click, the reply's echo). Wake(true, follow): for `follow` s any speech is an ask, no wake word needed
    // (the page opens this after a spoken reply); "idle" = the window closed unused.
    static readonly string WakeModel = Path.Combine(Dir, "ggml-tiny.en.bin");
    public const string Name = "Marina";   // the voice assistant's name: the wake word is "Hey " + Name (user, 15 Nov; was "Hey Claude"). The page has its own WAKE constant
    /// <summary>Who answers "Hey Marina", whatever runs the chat (Claude.cs Rules, ApiChat System). User, 15 Nov: keep the persona small.</summary>
    public const string Persona = "You are " + Name + ", the desktop's voice: a warm, playful but competent little goldfish who lives in the coral-reef bowl up in the top-left corner. " +
        "If asked who you are, say so. A light ocean touch now and then is welcome, but never at the cost of brevity or of getting the job done.";
    static readonly string Prompt = $"Hey {Name}.";   // nudges Whisper to spell the name, not "the marina" / "Maria"
    // "Hey Marina" and how tiny.en mishears it (concepts/mascot/wake-test.py), only at a phrase's start after hey/hi/ok/yo. Never "Maria" (a real
    // name: "hey Maria" is someone else). The bare name only as a call ("Marina, ..."), so "Marina Bay is..." or "the marina..." never wake it.
    static readonly Regex WakeWord = new(@"^\W*(?:(?:hey|hi|okay|ok|yo)\W+(?:marina|marinna|marena|mareena|merina|marine-?a|maureena|moreena|my\W?rina)|marina(?=\s*(?:[,.!?]|$)))\b\W*", RegexOptions.IgnoreCase);
    static readonly int CommandPause = 2 * Rate;   // ponytail: fixed 2 s (quick asks); the panel's dictation pause picker is for long dictation
    volatile bool wakeOn; Thread? waker;
    long followUntil;   // follow-up window (after a spoken reply): until this tick, any speech is an ask, no wake word needed; 0 = none
    public Action<float>? Level;   // each 50 ms while an ask records after the wake word: the mic's peak (the overlay's live meter)

    public void Wake(bool want, double follow = 0) { followUntil = want && follow > 0 ? Environment.TickCount64 + (long)(follow * 1000) : 0; wakeOn = want; if (want && waker == null) { waker = new Thread(WakeLoop) { IsBackground = true, Name = "wake" }; waker.Start(); } }

    void WakeLoop()
    {
        Mic? mic = null; var pcm = new List<short>(); var buf = new List<short>();
        float floor = 0.01f; int segFrom = -1, lastLoud = int.MinValue / 2, cmdFrom = -1, checkFrom = 0, cmdPause = CommandPause;   // indices into pcm; cmdFrom >= 0: the wake word was heard there, the command is recording
        Task<bool>? check = null; Task? partial = null; int partialAt = 0; long micAt = 0;   // live text while the ask records: one base.en read at a time, each 0.7 s more audio
        while (true)
        {
            if (!wakeOn || on) { mic?.Dispose(); mic = null; pcm.Clear(); segFrom = cmdFrom = -1; check = null; Thread.Sleep(200); continue; }
            try { if (mic == null) { mic = new Mic(); micAt = Environment.TickCount64; } Thread.Sleep(50); buf.Clear(); mic.Read(buf); }
            catch (Exception e) { Log.Write("wake: mic: " + e.Message); try { mic?.Dispose(); } catch { } mic = null; Thread.Sleep(3000); continue; }   // unplugged, or the default mic changed: open it again
            float peak = 0; foreach (var x in buf) peak = Math.Max(peak, Math.Abs(x / 32768f));
            floor = Math.Min(Math.Max(peak, 0.0002f), floor * 1.01f);   // room noise: the quietest recent 50 ms, drifting up ~20 %/s when it gets louder
            pcm.AddRange(buf);
            if (peak > Math.Max(MinSpeech, floor * OverFloor) && Environment.TickCount64 - micAt > 300) { lastLoud = pcm.Count; if (segFrom < 0) segFrom = Math.Max(0, pcm.Count - buf.Count - Rate * 3 / 10); }
            int quietFor = pcm.Count - lastLoud;
            if (followUntil > 0 && cmdFrom < 0)   // follow-up window: speech starts an ask straight away; silence closes it ("idle")
            {
                if (segFrom >= 0) { followUntil = 0; cmdFrom = partialAt = segFrom; segFrom = -1; check = null; cmdPause = CommandPause; Log.Write("wake: follow-up"); send("wake", new { state = "wake", text = "", follow = true }); }
                else if (Environment.TickCount64 > followUntil) { followUntil = 0; send("wake", new { state = "idle", text = "" }); }
            }
            if (cmdFrom >= 0)
            {
                Level?.Invoke(peak);
                if (quietFor < cmdPause && pcm.Count - cmdFrom < 30 * Rate)
                {
                    if (partial is null or { IsCompleted: true } && pcm.Count - partialAt >= PartialEvery && lastLoud > partialAt)   // only when there was speech since the last read
                    {
                        var open = pcm.GetRange(cmdFrom, pcm.Count - cmdFrom).ToArray(); partialAt = pcm.Count;
                        partial = Task.Run(() => { var t = WakeWord.Replace(Transcribe(open, Model, Prompt), "").Trim(); if (t.Length > 0) send("wake", new { state = "partial", text = char.ToUpper(t[0]) + t[1..] }); });
                    }
                    continue;
                }
                var clip = pcm.GetRange(cmdFrom, pcm.Count - cmdFrom).ToArray(); cmdFrom = segFrom = -1; pcm.Clear(); lastLoud = int.MinValue / 2;
                new Thread(() =>
                {
                    try
                    {
                        var heard = WakeWord.Replace(Transcribe(clip, Model, Prompt), "").Trim();
                        if (Regex.IsMatch(heard, @"^(thanks? (you )?for watching|thank you|you|bye)\W*$", RegexOptions.IgnoreCase)) heard = "";   // what Whisper says to silence
                        if (heard.Length > 0) heard = char.ToUpper(heard[0]) + heard[1..];
                        Log.Write($"wake: heard {heard.Length} chars"); send("wake", new { state = "heard", text = heard });
                    }
                    catch (Exception e) { Log.Write("wake failed: " + e.Message); send("wake", new { state = "error", text = e.Message }); }
                }) { IsBackground = true }.Start();
                continue;
            }
            if (check is { IsCompleted: true })
            {
                if (check.Status == TaskStatus.RanToCompletion && check.Result)
                {
                    cmdFrom = partialAt = checkFrom; segFrom = -1;
                    cmdPause = lastLoud - checkFrom < Rate * 3 / 2 && quietFor < 3 * Rate ? 5 * Rate : CommandPause;   // just "hey Marina" (a short phrase, nothing since): up to 5 s to start the ask
                    Log.Write("wake: hey " + Name.ToLower()); send("wake", new { state = "wake", text = "" });
                }
                check = null;
            }
            if (segFrom >= 0 && (quietFor > Rate * 8 / 10 || pcm.Count - segFrom > 15 * Rate))   // a phrase ended: was it to Marina?
            {
                if (lastLoud - segFrom > Rate / 4 && check == null)
                {
                    var clip = pcm.GetRange(segFrom, Math.Min(pcm.Count - segFrom, 3 * Rate)).ToArray(); checkFrom = segFrom;
                    check = Task.Run(() => WakeWord.IsMatch(Transcribe(clip, WakeModel, Prompt)));
                }
                segFrom = -1;
            }
            if (segFrom < 0 && check == null && cmdFrom < 0 && pcm.Count > Rate) { int drop = pcm.Count - Rate / 2; pcm.RemoveRange(0, drop); lastLoud -= drop; }   // idle: keep the last 0.5 s as lead-in
        }
    }

    static string Join(string a, string b) => a.Length == 0 ? b : b.Length == 0 ? a : a + " " + b;

    /// <summary>One clip through whisper-cli. ponytail: a fresh process per read (about 0.5 s of model loading each time);
    /// whisper-server keeps the model loaded if the live text ever lags too far behind.</summary>
    static string Transcribe(short[] clip, string? model = null, string? prompt = null)
    {
        model ??= Model;
        if (!File.Exists(Cli) || !File.Exists(model)) throw new FileNotFoundException("Whisper is missing from " + Dir);
        var wav = Path.Combine(Path.GetTempPath(), $"console-desktop-voice-{Guid.NewGuid():N}.wav");
        try
        {
            using (var w = new BinaryWriter(File.Create(wav)))
            {
                int bytes = clip.Length * 2;
                w.Write("RIFF"u8); w.Write(36 + bytes); w.Write("WAVEfmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
                w.Write(Rate); w.Write(Rate * 2); w.Write((short)2); w.Write((short)16); w.Write("data"u8); w.Write(bytes);
                foreach (var s in clip) w.Write(s);
            }
            var psi = new ProcessStartInfo(Cli) { RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, UseShellExecute = false, StandardOutputEncoding = Encoding.UTF8 };
            foreach (var a in new[] { "-m", model, "-f", wav, "-nt", "-np", "-l", "en", "-t", Math.Min(8, Environment.ProcessorCount).ToString() }) psi.ArgumentList.Add(a);
            if (prompt != null) { psi.ArgumentList.Add("--prompt"); psi.ArgumentList.Add(prompt); }   // nudges Whisper to spell the wake name (Prompt)
            using var p = Process.Start(psi)!;
            p.ErrorDataReceived += (_, _) => { }; p.BeginErrorReadLine();   // drain it so whisper never blocks on a full pipe
            var o = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(30000)) { p.Kill(); throw new TimeoutException("Whisper took over 30 s"); }
            return Regex.Replace(Regex.Replace(o, @"\[[^\]]*\]|\([^)]*\)", " "), @"\s+", " ").Trim();   // drop [BLANK_AUDIO], (music) and line breaks
        }
        finally { try { File.Delete(wav); } catch { } }
    }

    /// <summary>The default mic through WASAPI shared mode, converted by Windows to 16 kHz mono 16-bit.</summary>
    sealed class Mic : IDisposable
    {
        readonly IAudioClient client = null!; readonly IAudioCaptureClient capture = null!;
        readonly short[]? test; readonly Stopwatch testClock = new(); int testAt;
        public Mic()
        {
            if (TestFile != null) { var b = File.ReadAllBytes(TestFile); test = new short[(b.Length - 44) / 2]; Buffer.BlockCopy(b, 44, test, 0, test.Length * 2); testClock.Start(); return; }
            var en = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            Marshal.ThrowExceptionForHR(en.GetDefaultAudioEndpoint(1, 0, out var dev));   // eCapture, eConsole
            var iid = typeof(IAudioClient).GUID;
            Marshal.ThrowExceptionForHR(dev.Activate(ref iid, 23, IntPtr.Zero, out var o));
            client = (IAudioClient)o;
            var fmt = new WaveFormat { tag = 1, channels = 1, rate = Rate, bytesPerSec = Rate * 2, align = 2, bits = 16 };
            Marshal.ThrowExceptionForHR(client.Initialize(0, unchecked((int)0x88000000), 5_000_000, 0, ref fmt, IntPtr.Zero));   // shared; AUTOCONVERTPCM | SRC_DEFAULT_QUALITY; 0.5 s buffer
            var cid = typeof(IAudioCaptureClient).GUID;
            Marshal.ThrowExceptionForHR(client.GetService(ref cid, out var c));
            capture = (IAudioCaptureClient)c;
            Marshal.ThrowExceptionForHR(client.Start());
        }
        public void Read(List<short> into)
        {
            if (test != null) { int to = (int)Math.Min(test.Length + Rate * 10L, testClock.ElapsedMilliseconds * Rate / 1000); for (; testAt < to; testAt++) into.Add(testAt < test.Length ? test[testAt] : (short)0); return; }   // then 10 s of silence
            while (capture.GetNextPacketSize(out var n) == 0 && n > 0)
            {
                Marshal.ThrowExceptionForHR(capture.GetBuffer(out var data, out var frames, out var flags, out _, out _));
                if ((flags & 2) != 0) into.AddRange(new short[frames]);   // AUDCLNT_BUFFERFLAGS_SILENT
                else { var s = new short[frames]; Marshal.Copy(data, s, 0, frames); into.AddRange(s); }
                capture.ReleaseBuffer(frames);
            }
        }
        public void Dispose() { if (test != null) return; client.Stop(); Marshal.ReleaseComObject(capture); Marshal.ReleaseComObject(client); }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)] struct WaveFormat { public short tag, channels; public int rate, bytesPerSec; public short align, bits, extra; }
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator { void _0(); [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice dev); }
    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice { [PreserveSig] int Activate(ref Guid iid, int ctx, IntPtr p, [MarshalAs(UnmanagedType.IUnknown)] out object o); }
    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioClient
    {
        [PreserveSig] int Initialize(int mode, int flags, long buffer, long period, ref WaveFormat fmt, IntPtr session);
        void _1(); void _2(); void _3(); void _4(); void _5(); void _6();
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

    /// <summary>The panel's mic picker. Recording always uses the default mic, so the list marks that one
    /// and picking sets it (for every role, as Sound settings does). Event "mics": [{ id, name, on }].</summary>
    public async Task Mics()
    {
        try
        {
            var def = MediaDevice.GetDefaultAudioCaptureId(AudioDeviceRole.Default);
            var all = await DeviceInformation.FindAllAsync(MediaDevice.GetAudioCaptureSelector());
            send("mics", all.Select(d => new { id = d.Id, name = d.Name, on = d.Id == def }).ToArray());
        }
        catch (Exception e) { Log.Write("mics failed: " + e.Message); }
    }

    public void SetMic(string id)
    {
        try
        {
            var mm = Regex.Match(id, @"MMDEVAPI#(.+?)#", RegexOptions.IgnoreCase).Groups[1].Value;   // \\?\SWD#MMDEVAPI#{0.0.1.00000000}.{guid}#{...} -> the core-audio id
            var p = (IPolicyConfig)new PolicyConfigClient();
            for (int role = 0; role < 3; role++) Marshal.ThrowExceptionForHR(p.SetDefaultEndpoint(mm, role));
            Log.Write("mic set: " + mm);
        }
        catch (Exception e) { Log.Write("mic set failed: " + e.Message); }
        _ = Mics();   // the page shows what Windows really has now
    }

    // Undocumented but stable since Vista; what every "set default device" tool uses. Only SetDefaultEndpoint is called, the rest hold vtable slots.
    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPolicyConfig { void _0(); void _1(); void _2(); void _3(); void _4(); void _5(); void _6(); void _7(); void _8(); void _9();
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role); }
    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")] class PolicyConfigClient { }
}
