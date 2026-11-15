using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace ConsoleDesktop;

/// <summary>Read-aloud voice: Kokoro 82M (kokoro-onnx, app\tts\say.py in its own venv), kept running once started (~470 MB, 2 s to load).
/// Say() gives a WAV as a data: url, or null when Kokoro is missing or fails (the page then falls back to speechSynthesis).
/// ponytail: never unloaded; stop it after some idle minutes if the RAM is ever missed.</summary>
static class Tts
{
    static readonly string Dir = Path.Combine(Paths.App, "tts"), Py = Path.Combine(Dir, @"venv\Scripts\python.exe");
    static readonly object gate = new();
    static Process? p;

    public static void Warm() => Task.Run(() => { lock (gate) Start(); });

    static bool Start()
    {
        if (p is { HasExited: false }) return true;
        if (!File.Exists(Py)) return false;
        var psi = new ProcessStartInfo(Py) { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true, StandardInputEncoding = new UTF8Encoding(false) };
        psi.ArgumentList.Add("-I"); psi.ArgumentList.Add(Path.Combine(Dir, "say.py"));
        p = Process.Start(psi)!;   // exits by itself when we do: its stdin closes
        if (p.StandardOutput.ReadLine() == "READY") { Log.Write("tts: kokoro ready"); return true; }
        Log.Write("tts: kokoro did not start"); p = null; return false;
    }

    public static string? Say(string text, string voice, double speed)
    {
        lock (gate)
        {
            try
            {
                if (!Start()) return null;
                p!.StandardInput.WriteLine(JsonSerializer.Serialize(new { text, voice, speed }));
                var o = p.StandardOutput.ReadLine();
                if (o == null || o.StartsWith("ERR")) { Log.Write("tts: " + (o ?? "kokoro exited")); return null; }
                return "data:audio/wav;base64," + o;
            }
            catch (Exception e) { Log.Write("tts failed: " + e.Message); try { p?.Kill(true); } catch { } p = null; return null; }
        }
    }
}
