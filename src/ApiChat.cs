using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic;
using Anthropic.Models.Messages;

namespace ConsoleDesktop;

/// <summary>What the chat panel talks to: the user's own Claude Code (Claude.cs: tools, files, full access) or a plain chat model through an API (ApiChat).</summary>
interface IChat
{
    void Ask(string text, JsonElement? images = null);
    void Info(); void Sessions(); void Resume(string id); void Files(string q);
    void NewChat(); void Cancel(); void Stop();
    void Set(string model, string effort, string mode = "");
}

/// <summary>The chat panel on any chat API (config.json "chat": provider "api"; plain chat, no tools, it can't touch the PC),
/// or on another agent's command line (provider "cli": chat.command, Codex by default; it works on the PC with its own sign-in and tools).
/// chat.api "openai" = any OpenAI-compatible endpoint at chat.url (OpenAI, OpenRouter, Gemini, Groq, Mistral, DeepSeek, xAI, Ollama, LM Studio...);
/// chat.api "anthropic" = the Claude API through Anthropic's SDK. The key is sealed to this Windows user (Cfg.ChatKey); local servers need none.
/// Chats are kept in app\chats\&lt;id&gt;.json, so past chats and resume work like the Claude Code panel's. Same events as Claude.cs (BRIDGE.md).</summary>
sealed class ApiChat : IChat
{
    readonly Action<string, object?> send;
    readonly HttpClient http = new() { Timeout = TimeSpan.FromMinutes(10) };
    static string Dir { get { var d = Path.Combine(Paths.App, "chats"); Directory.CreateDirectory(d); return d; } }
    static string Kind => Cfg.Chat == "cli" ? "cli" : Cfg.Get("chat.api") ?? "openai";
    /// <summary>The agent command line when chat.command is not set: Codex, full access like the Claude Code panel, the ask on stdin.</summary>
    public const string Codex = "codex exec --skip-git-repo-check --sandbox danger-full-access -";
    static string Url => (Cfg.Get("chat.url") ?? (Kind == "anthropic" ? "https://api.anthropic.com" : "https://api.openai.com/v1")).TrimEnd('/');
    static string System => Cfg.Get("chat.system") ?? (Kind == "cli"
        ? $"You are {Cfg.ChatName}, running from the chat panel of the user's console desktop on Windows, with full access to this PC: answer questions, run commands, work on their files, whatever they ask. "
          + $"The panel renders Markdown. To change how the desktop itself looks, edit the files in {Paths.Live} (README.md there says what each key does). Never kill or restart ConsoleDesktop: this panel runs inside it. "
        : $"You are {Cfg.ChatName}, the assistant in the chat panel of the user's console desktop on Windows. The panel renders Markdown. You cannot run commands or see the screen; say so if asked to act on the PC. ")
        + "A message starting with 🎙 was spoken (speech-to-text may mishear words): act first if it asks for something, then answer in one or two short plain sentences, they are read aloud. " + Voice.Persona;

    sealed class Msg { public string role = ""; public string text = ""; public List<(string type, string data)>? images; }
    readonly object gate = new();
    List<Msg> chat = new();
    string session = Guid.NewGuid().ToString(), model = Cfg.Get("chat.model") ?? (Kind == "cli" ? Cfg.ChatName : ""), effort = "medium";
    CancellationTokenSource? turn;
    long inTok, outTok;

    public ApiChat(Action<string, object?> send) { this.send = send; }

    public void Info() => send("claude-init", new { commands = Array.Empty<string>(), skills = Array.Empty<string>(), model, mcp = (object?)null, tools = 0, mode = "chat", session, api = true });

    /// <summary>The model picked in the panel (any id from config "chat.models", or the configured one); effort only matters to models that take it.</summary>
    public void Set(string m, string e, string md = "") { lock (gate) { if (m != "") model = m; if (e != "") effort = e; } Info(); }

    public void NewChat() { lock (gate) { turn?.Cancel(); chat = new(); session = Guid.NewGuid().ToString(); inTok = outTok = 0; } send("claude-new", null); }
    public void Cancel() { lock (gate) turn?.Cancel(); }
    public void Stop() => Cancel();
    public void Files(string q) => send("claude-files", new { q, files = Array.Empty<object>() });

    public void Sessions()
    {
        var list = new DirectoryInfo(Dir).GetFiles("*.json").OrderByDescending(f => f.LastWriteTimeUtc).Take(40).Select(f =>
        {
            try { var j = JsonNode.Parse(File.ReadAllText(f.FullName)); return (object)new { id = Path.GetFileNameWithoutExtension(f.Name), title = j?["title"]?.GetValue<string>() ?? "Chat", at = new DateTimeOffset(f.LastWriteTimeUtc).ToUnixTimeSeconds(), current = Path.GetFileNameWithoutExtension(f.Name) == session }; }
            catch { return null; }
        }).OfType<object>().ToList();
        send("claude-sessions", list);
    }

    public void Resume(string id)
    {
        var file = Path.Combine(Dir, id + ".json");
        if (!Guid.TryParse(id, out _) || !File.Exists(file)) { send("claude-error", new { message = "That chat is gone." }); return; }
        var msgs = (JsonNode.Parse(File.ReadAllText(file))?["messages"] as JsonArray)?.Select(m => new Msg { role = m?["role"]?.GetValue<string>() ?? "", text = m?["text"]?.GetValue<string>() ?? "" }).Where(m => m.role is "user" or "assistant").ToList() ?? new();
        lock (gate) { turn?.Cancel(); chat = msgs; session = id; }
        send("claude-history", new { id, messages = msgs.TakeLast(120).Select(m => new { role = m.role == "user" ? "me" : "ai", m.text }) });
    }

    void Save()
    {
        try
        {
            List<Msg> copy; lock (gate) copy = chat.ToList();
            var title = copy.FirstOrDefault(m => m.role == "user")?.text ?? "Chat"; if (title.Length > 90) title = title[..87] + "…";
            File.WriteAllText(Path.Combine(Dir, session + ".json"), JsonSerializer.Serialize(new { title, model, messages = copy.Select(m => new { m.role, m.text }) }));
        }
        catch (Exception e) { Log.Write("chat not saved: " + e.Message); }
    }

    public void Ask(string text, JsonElement? images = null)
    {
        CancellationTokenSource cts;
        lock (gate)
        {
            if (turn != null) { send("claude-error", new { message = "Still working on the last ask." }); return; }
            var pics = images is { ValueKind: JsonValueKind.Array } a ? a.EnumerateArray().Select(i => (i.GetProperty("type").GetString() ?? "image/png", i.GetProperty("data").GetString() ?? "")).ToList() : null;
            chat.Add(new Msg { role = "user", text = text, images = pics is { Count: > 0 } ? pics : null });
            turn = cts = new CancellationTokenSource();
        }
        send("claude-start", new { text, followUp = chat.Count > 1, images = images is { ValueKind: JsonValueKind.Array } im ? im.GetArrayLength() : 0 });
        _ = Task.Run(async () =>
        {
            var reply = new StringBuilder(); string? error = null;
            void Delta(string d) { reply.Append(d); send("claude-delta", new { text = d }); }
            try
            {
                if (model == "") throw new InvalidOperationException("No model is set · pick one in Setup › Chat");
                if (Kind == "cli") await Cli(Delta, cts.Token);
                else if (Kind == "anthropic") await Anthropic(Delta, cts.Token); else await OpenAi(Delta, s => send("claude-think", new { text = s }), cts.Token);
            }
            catch (OperationCanceledException) { error = reply.Length > 0 ? null : "Stopped."; }
            catch (Exception e) { error = e.Message; Log.Write($"chat ({Kind} {model}): {e.Message}"); }
            lock (gate)
            {
                turn = null;
                if (reply.Length > 0) chat.Add(new Msg { role = "assistant", text = reply.ToString() });
                else if (chat.Count > 0 && chat[^1].role == "user") chat.RemoveAt(chat.Count - 1);   // a failed ask leaves no half turn behind (two user turns in a row are refused by some APIs)
            }
            if (reply.Length > 0) Save();
            if (error != null && reply.Length == 0) send("claude-error", new { message = error, changed = Array.Empty<string>() });
            else send("claude-done", new { text = reply.ToString(), usage = new { ctx = inTok + outTok, window = 0, cost = 0 } });
        });
    }

    List<Msg> History() { lock (gate) return chat.ToList(); }

    // ---------- OpenAI-compatible: POST {url}/chat/completions, stream: true, server-sent "data:" lines ----------
    async Task OpenAi(Action<string> delta, Action<string> think, CancellationToken ct)
    {
        var msgs = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = System } };
        foreach (var m in History())
            msgs.Add(new JsonObject
            {
                ["role"] = m.role,
                ["content"] = m.images == null ? m.text : new JsonArray(m.images.Select(i => (JsonNode)new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = $"data:{i.type};base64,{i.data}" } })
                    .Prepend(new JsonObject { ["type"] = "text", ["text"] = m.text }).ToArray()),
            });
        var body = new JsonObject { ["model"] = model, ["messages"] = msgs, ["stream"] = true, ["stream_options"] = new JsonObject { ["include_usage"] = true } };
        using var req = new HttpRequestMessage(HttpMethod.Post, Url + "/chat/completions") { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        if (Cfg.ChatKey is { } key) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!res.IsSuccessStatusCode)
        {
            var err = await res.Content.ReadAsStringAsync(ct);
            string? msg = null; try { msg = JsonNode.Parse(err)?["error"]?["message"]?.GetValue<string>(); } catch { }
            throw new HttpRequestException($"{(int)res.StatusCode}: {msg ?? (err.Length > 200 ? err[..200] : err)}");
        }
        using var rd = new StreamReader(await res.Content.ReadAsStreamAsync(ct));
        for (string? line; (line = await rd.ReadLineAsync(ct)) != null;)
        {
            if (!line.StartsWith("data:")) continue;
            var data = line[5..].Trim(); if (data == "[DONE]") break;
            JsonNode? j; try { j = JsonNode.Parse(data); } catch { continue; }
            var d = j?["choices"]?[0]?["delta"];
            if ((d?["reasoning_content"] ?? d?["reasoning"])?.GetValue<string>() is { Length: > 0 } r) think(r);   // DeepSeek / OpenRouter / Ollama thinking models
            if (d?["content"]?.GetValue<string>() is { Length: > 0 } c) delta(c);
            if (j?["usage"] is { } u) { inTok = u["prompt_tokens"]?.GetValue<long>() ?? inTok; outTok = u["completion_tokens"]?.GetValue<long>() ?? outTok; }
        }
    }

    // ---------- an agent's command line (chat.command, else Codex): the ask goes in on stdin, what it prints is the reply ----------
    // Through cmd.exe so npm's .cmd shims (gemini, aider...) run too; it works in chat.home like Claude Code. Images are not passed on.
    // ponytail: each ask replays the chat so far as one prompt, which any CLI understands; use a CLI's own resume (codex exec resume) if long chats get slow.
    async Task Cli(Action<string> delta, CancellationToken ct)
    {
        var h = History(); var prompt = new StringBuilder(System).Append("\n\n");
        if (h.Count > 1)
        {
            prompt.Append("The conversation so far:\n\n");
            foreach (var m in h.SkipLast(1)) prompt.Append(m.role == "user" ? "User: " : "You: ").Append(m.text).Append("\n\n");
            prompt.Append("Their new message:\n");
        }
        prompt.Append(h[^1].text);
        var cmd = Cfg.Get("chat.command") ?? Codex;
        var psi = new ProcessStartInfo("cmd.exe", $"/d /s /c \"{cmd}\"") { WorkingDirectory = Claude.Home, UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, StandardInputEncoding = new UTF8Encoding(false) };
        using var p = Process.Start(psi)!;
        var err = new StringBuilder(); p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (err) err.AppendLine(e.Data); }; p.BeginErrorReadLine();
        using var stop = ct.Register(() => { try { p.Kill(true); } catch { } });
        await p.StandardInput.WriteAsync(prompt); p.StandardInput.Close();
        var buf = new char[512]; bool any = false;
        for (int n; (n = await p.StandardOutput.ReadAsync(buf, ct)) > 0;) { delta(new string(buf, 0, n)); any = true; }
        await p.WaitForExitAsync(ct);
        if (p.ExitCode != 0 && !any)
        {
            string why; lock (err) why = err.ToString().Split('\n').Select(x => x.Trim()).LastOrDefault(x => x != "") ?? "";
            throw new InvalidOperationException($"{cmd.Split(' ')[0]} stopped (code {p.ExitCode}){(why != "" ? ": " + why : "")}");
        }
    }

    // ---------- the Claude API (Anthropic's C# SDK), streamed ----------
    async Task Anthropic(Action<string> delta, CancellationToken ct)
    {
        var client = new AnthropicClient { ApiKey = Cfg.ChatKey ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"), BaseUrl = Url };
        var msgs = new List<MessageParam>();
        foreach (var m in History())
        {
            if (m.role == "user")
            {
                var blocks = new List<ContentBlockParam>();
                foreach (var (type, data) in m.images ?? new()) blocks.Add(new ImageBlockParam { Source = new Base64ImageSource { MediaType = type, Data = data } });
                blocks.Add(new TextBlockParam { Text = m.text });
                msgs.Add(new UserMessageParam { Content = blocks });
            }
            else msgs.Add(new AssistantMessageParam { Content = m.text });
        }
        var p = new MessageCreateParams
        {
            Model = model, MaxTokens = 64000, System = System, Messages = msgs,
            OutputConfig = new OutputConfig { Effort = effort switch { "low" => Effort.Low, "high" => Effort.High, "xhigh" => Effort.Xhigh, "max" => Effort.Max, _ => Effort.Medium } },
        };
        await foreach (var ev in client.Messages.CreateStreaming(p, ct))
        {
            if (ev.TryPickContentBlockDelta(out var d) && d.Delta.TryPickText(out var t)) delta(t.Text);
            else if (ev.TryPickDelta(out var md))
            {
                outTok = md.Usage.OutputTokens;
                if (md.Delta.StopReason?.Raw() == "refusal") delta("\n\n*The model declined this one.*");
            }
            else if (ev.TryPickStart(out var st)) inTok = st.Message.Usage.InputTokens;
        }
    }
}
