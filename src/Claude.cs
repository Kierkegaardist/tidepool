using System.Diagnostics;
using System.Text.Json;

namespace ConsoleDesktop;

/// <summary>The Claude panel: the user's own Claude Code (`claude -p`, their sign-in, settings, hooks and MCP servers) as a general desktop
/// assistant with full access: every tool, permissions bypassed (the panel cannot answer permission prompts), working in chat.home (Home).
/// One claude is kept running between asks (stream-json in and out), so its hooks, MCP servers and sign-in are done before the user types.
/// Several chats can work at once: switching chats (New chat, Past chats) leaves a working one going in the background (user, 15 Nov).</summary>
sealed class Claude : IChat
{
    static readonly string RestartScript = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\tools\restart-host.ps1"));
    static readonly string Rules = "You are running in the chat panel of the user's console desktop (Tidepool), a narrow panel that renders your replies as Markdown. " +
        "You are their general Claude Code on this PC with full access: answer questions, run commands, work on their projects, whatever they ask. " +
        "If they ask to change how the desktop itself looks, edit the files in " + Paths.Live + " (README.md there says what each key does; the page reloads them by itself). " +
        // user, 15 Nov: typed asks run at the effort they picked and are done directly; only "Hey Marina" starts low and escalates
        "Effort: typed messages run at the effort the user picked in the panel, so do the work yourself; don't hand it to a subagent unless they ask for one. " +
        "Spoken messages (🎙) run at low effort: do quick things yourself straight away (questions, opening or controlling apps, music, small lookups and small edits). " +
        "When a spoken task needs real work (building or debugging code, research, many-file or careful changes), or they ask for more effort (\"think hard\", " +
        "\"high effort\", \"max effort\"), hand it to the Agent tool with its effort parameter set (high by default, xhigh or max when they ask for it), a short " +
        "description of the task, and run_in_background false, then report the result. This is the user's standing instruction, so setting effort on that Agent " +
        "call is explicitly allowed. Say in one short line that you are handing it off. " +
        // this chat runs inside the Tidepool host: killing it kills this chat and any agent it started
        "Never kill or restart ConsoleDesktop yourself, and tell any agent you start the same: to rebuild the desktop run " + RestartScript +
        ", which queues the restart for after this reply ends. " +
        "Voice: a message starting with 🎙 was spoken (\"Hey Marina ...\"; the speech-to-text may mishear words, so read it charitably) and your reply is read aloud: " +
        "act first, then answer in one or two short plain sentences, no tables, lists, code or links unless asked. " +
        // user, 15 Nov: Marina should passively remember what people tell her, when the chat has a memory
        "Memory: when the user mentions something personal or lasting in passing (a birthday, a preference, a name, a plan, something they like or dislike), " +
        "quietly save it to your persistent memory without being asked, and use it later where it fits. Don't announce every save; a light touch is fine. " + Voice.Persona;
    internal static string RestartFlag => Path.Combine(Path.GetDirectoryName(RestartScript)!, "..", "logs", "restart.pending");
    /// <summary>Where Claude Code works and the @ menu looks: config.json "chat.home" (Setup), else the user's own folder.</summary>
    internal static readonly string Home = Cfg.Get("chat.home") is { } h && Directory.Exists(h) ? h : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    static readonly HashSet<string> Models = new() { "opus", "sonnet", "haiku", "fable" }, Efforts = new() { "low", "medium", "high", "xhigh", "max" };

    readonly Action<string, object?> send;   // (event type, value) to the page
    readonly object gate = new();
    string model = "opus", effort = "low", mode = "bypassPermissions";   // effort: the panel's pick (the page sends it at load)
    static readonly HashSet<string> Modes = new() { "bypassPermissions", "plan" };

    /// <summary>One chat = one Claude Code session, with its own claude process while it is shown or working.
    /// The panel shows one (cur); the others keep working in the background and are dropped once their ask is done (resume brings them back).</summary>
    sealed class Chat
    {
        public string Id = Guid.NewGuid().ToString();
        public bool Used;                   // the session has had an ask: a restart resumes it
        public string Run = "low";          // the effort the claude runs at: the pick for typed asks, low for spoken ones (🎙). It stays put after an ask, so back-to-back voice asks don't reboot
        public string Booted = "";
        public Process? Warm;               // the claude waiting for (or working on) this chat's asks
        public Turn? Turn;                  // the ask in flight
        public object? Info;                // the last init message, trimmed: commands, model, MCP servers (claude-init)
        public (string session, string id)? Bridge;   // Remote Control: the phone session this chat's claude joined
    }
    readonly List<Chat> chats = new();      // cur, plus chats working in the background
    readonly HashSet<string> fresh = new(); // background chats that finished since they were last shown
    Chat cur;
    sealed class Turn { public string Reply = "", Error = ""; public bool Stopped; public System.Threading.Timer? Killer, Late; public int Extra; public long Ctx; public long Window; public double Cost; }

    public Claude(Action<string, object?> send) { this.send = send; lock (gate) { cur = new Chat(); chats.Add(cur); Boot(cur); } watch = new System.Threading.Timer(_ => Watch(), null, 10_000, 10_000); }

    /// <summary>claude.exe (native installer, winget, PATH), else an npm install's own entry point (its bundled claude.exe, or cli.js run with node),
    /// found next to the claude.cmd npm puts on PATH. The .cmd itself is never run: cmd.exe mangles the multi-line arguments.</summary>
    public static string? Exe
    {
        get
        {
            var path = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries).Select(d => d.Trim()).ToList();
            var npm = path.Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm")).Where(d => File.Exists(Path.Combine(d, "claude.cmd")))
                .Select(d => Path.Combine(d, @"node_modules\@anthropic-ai\claude-code"));
            return new[] { Environment.GetEnvironmentVariable("USERPROFILE") + @"\.local\bin\claude.exe" }.Concat(path.Select(d => Path.Combine(d, "claude.exe")))
                .Concat(npm.SelectMany(p => new[] { Path.Combine(p, @"bin\claude.exe"), Path.Combine(p, "cli.js") })).FirstOrDefault(File.Exists);
        }
    }

    string Key(Chat c) => $"{model}/{c.Run}/{mode}/{c.Id}/{c.Used}";

    /// <summary>A chat's event: to the page while it is the one shown; background chats only change their dot in Past chats (claude-busy).</summary>
    void Send(Chat c, string type, object? v) { bool shown; lock (gate) shown = c == cur; if (shown) send(type, v); }

    public bool Working { get { lock (gate) return chats.Any(x => x.Turn != null); } }

    void Busy()   // under gate: which chats are working, which finished unseen
    {
        var busy = chats.Where(c => c.Turn != null).Select(c => c.Id).ToArray();
        send("claude-busy", new { current = cur.Id, busy, fresh = fresh.ToArray() });
    }

    /// <summary>Shows chat c in the panel. The one shown before keeps going if it is working, else its claude stops (resume starts it again).</summary>
    void Show(Chat c)   // under gate
    {
        var old = cur; cur = c; fresh.Remove(c.Id);
        if (!chats.Contains(c)) chats.Add(c);
        if (old != c && old.Turn == null) Drop(old);
    }
    void Drop(Chat c) { chats.Remove(c); var p = c.Warm; c.Warm = null; Kill(p); }   // under gate

    public void NewChat()
    {
        lock (gate) { if (cur.Used || cur.Turn != null) Show(new Chat { Run = effort }); if (cur.Turn == null && cur.Booted != Key(cur)) Boot(cur); Busy(); }
        send("claude-new", null); Log.Write("claude: new chat");
    }

    /// <summary>The model and effort for the next ask (the page keeps the choice). Reboots the shown chat's waiting claude on the same session.</summary>
    public void Set(string m, string e, string md = "")
    {
        if (!Models.Contains(m) || !Efforts.Contains(e)) return;
        if (!Modes.Contains(md)) md = mode;
        lock (gate) { if (m == model && e == effort && md == mode) return; model = m; effort = cur.Run = e; mode = md; if (cur.Turn == null) Boot(cur); }
        Log.Write($"claude: {m}, {e} effort, {md}");
    }

    public void Info() { object? i; lock (gate) i = cur.Info; send("claude-init", i); }

    /// <summary>The chats Claude Code kept for the home folder (the panel's and VS Code's), newest first: { id, title, at, current, busy, fresh }.</summary>
    public void Sessions()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects", ProjectKey(Home));
        string curId; HashSet<string> busy, done;
        lock (gate) { curId = cur.Id; busy = chats.Where(c => c.Turn != null).Select(c => c.Id).ToHashSet(); done = fresh.ToHashSet(); }
        var list = !Directory.Exists(dir) ? new List<object>() : new DirectoryInfo(dir).GetFiles("*.jsonl").OrderByDescending(f => f.LastWriteTimeUtc).Take(40)
            .Select(f => (f, id: Path.GetFileNameWithoutExtension(f.Name), title: Title(f.FullName))).Where(x => x.title != null)
            .Select(x => (object)new { x.id, x.title, at = new DateTimeOffset(x.f.LastWriteTimeUtc).ToUnixTimeSeconds(), current = x.id == curId, busy = busy.Contains(x.id), fresh = done.Contains(x.id) }).ToList();
        send("claude-sessions", list);
    }
    static string ProjectKey(string cwd) => System.Text.RegularExpressions.Regex.Replace(cwd, "[^A-Za-z0-9]", "-");
    static string Transcript(string id) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects", ProjectKey(Home), id + ".jsonl");

    static IEnumerable<JsonElement> Lines(string file)
    {
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var rd = new StreamReader(fs);
        for (string? l; (l = rd.ReadLine()) != null;) { JsonElement e; try { e = JsonDocument.Parse(l).RootElement.Clone(); } catch { continue; } yield return e; }
    }
    static string? UserText(JsonElement m)
    {
        if (!m.TryGetProperty("message", out var msg) || !msg.TryGetProperty("content", out var c)) return null;
        var t = c.ValueKind == JsonValueKind.String ? c.GetString() : c.ValueKind == JsonValueKind.Array ? string.Join("\n", c.EnumerateArray().Where(b => b.TryGetProperty("type", out var ty) && ty.GetString() == "text").Select(b => b.GetProperty("text").GetString())) : null;
        return string.IsNullOrWhiteSpace(t) || t.TrimStart().StartsWith("<") || t.StartsWith("Caveat:") ? null : t.Trim();   // hook output, command echoes and caveats are not the user's words
    }
    static string? Title(string file)
    {
        try { foreach (var m in Lines(file).Take(400)) if (m.TryGetProperty("type", out var ty) && ty.GetString() == "user" && UserText(m) is { } t) return t.Length > 90 ? t[..87] + "…" : t; } catch { }
        return null;
    }

    /// <summary>Shows an earlier chat: the page gets its last messages back (claude-history), and the next ask resumes it.
    /// A chat still working in the background comes back mid-ask: the page then gets claude-start (attach) and the rest of the ask live.</summary>
    public void Resume(string id)
    {
        var file = Transcript(id);
        Chat c; bool working;
        lock (gate)
        {
            c = chats.FirstOrDefault(x => x.Id == id) ?? new Chat { Id = id, Used = true, Run = effort };
            if (c.Warm == null && (!Guid.TryParse(id, out _) || !File.Exists(file))) { send("claude-error", new { message = "That chat is gone." }); return; }
            Show(c); working = c.Turn != null;
            if (!working && (c.Warm == null || c.Warm.HasExited || c.Booted != Key(c))) Boot(c);
            Busy();
        }
        var msgs = new List<object>();
        if (File.Exists(file))
            foreach (var m in Lines(file))
            {
                var ty = m.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (m.TryGetProperty("isSidechain", out var sc) && sc.ValueKind == JsonValueKind.True) continue;
                if (ty == "user" && UserText(m) is { } u) msgs.Add(new { role = "me", text = u });
                else if (ty == "assistant" && m.TryGetProperty("message", out var am) && am.TryGetProperty("content", out var ac) && ac.ValueKind == JsonValueKind.Array)
                    foreach (var b in ac.EnumerateArray())
                    {
                        var bt = b.TryGetProperty("type", out var x) ? x.GetString() : null;
                        if (bt == "text" && b.GetProperty("text").GetString() is { Length: > 0 } tx) msgs.Add(new { role = "ai", text = tx });
                        else if (bt == "tool_use") msgs.Add(new { role = "tool", name = b.GetProperty("name").GetString(), file = Detail(b.GetProperty("input")) });
                    }
            }
        Log.Write($"claude: showing {id} ({msgs.Count} messages{(working ? ", working" : "")})");
        send("claude-history", new { id, messages = msgs.TakeLast(120), working });
        object? info; lock (gate) info = c.Info; if (info != null) send("claude-init", info);
        if (working) Send(c, "claude-start", new { text = "", followUp = true, attach = true });
    }

    /// <summary>@-mentions: files and folders under the home folder whose name holds the words typed (a quick walk, 5 levels, build output and packages skipped).</summary>
    public void Files(string q)
    {
        var words = q.ToLowerInvariant().Replace('\\', '/').Split(new[] { ' ', '/' }, StringSplitOptions.RemoveEmptyEntries);
        var hits = new List<(int rank, string path)>(); int seen = 0;
        void Walk(string dir, int depth)
        {
            if (seen > 60_000 || hits.Count > 400) return;
            IEnumerable<string> kids; try { kids = Directory.EnumerateFileSystemEntries(dir); } catch { return; }
            foreach (var k in kids)
            {
                seen++; var name = Path.GetFileName(k); var rel = Path.GetRelativePath(Home, k).Replace('\\', '/'); var lo = rel.ToLowerInvariant();
                if (words.Length > 0 && words.All(lo.Contains)) hits.Add((Path.GetFileName(lo).StartsWith(words[^1]) ? 0 : lo.EndsWith(words[^1]) ? 1 : 2, rel));
                if (depth < 5 && Directory.Exists(k) && !name.StartsWith(".") && !Skip.Contains(name.ToLowerInvariant())) Walk(k, depth + 1);
            }
        }
        if (words.Length > 0) Walk(Home, 0);
        send("claude-files", new { q, files = hits.OrderBy(h => h.rank).ThenBy(h => h.path.Length).Take(12).Select(h => new { path = h.path, dir = Directory.Exists(Path.Combine(Home, h.path)) }) });
    }
    static readonly HashSet<string> Skip = new() { "node_modules", "bin", "obj", "dist", "build", "target", "__pycache__", "venv", ".venv", "library", "temp", "cache", "packages", "assets", "import" };

    public void Cancel() { lock (gate) { if (cur.Turn == null) return; cur.Turn.Stopped = true; Kill(cur.Warm); } }   // its reader ends the turn and boots a fresh one

    /// <summary>Host exit: kill every chat's claude without a reboot.</summary>
    public void Stop() { lock (gate) foreach (var c in chats) { var p = c.Warm; c.Warm = null; Kill(p); } }

    static void Kill(Process? p) { try { p?.Kill(true); } catch { } }

    void Boot(Chat c)   // under gate
    {
        var old = c.Warm; c.Warm = null; Kill(old);   // detached first, so its exit is not read as an ask's end
        if (Exe is not { } exe) return;
        var js = exe.EndsWith(".js", StringComparison.OrdinalIgnoreCase);
        if (js && Cfg.Tool("node") == null) { Log.Write("claude: npm install found but node.exe is not on PATH"); return; }
        var psi = new ProcessStartInfo(js ? Cfg.Tool("node")! : exe) { WorkingDirectory = Home, UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, StandardOutputEncoding = System.Text.Encoding.UTF8, StandardInputEncoding = new System.Text.UTF8Encoding(false) };
        if (js) psi.ArgumentList.Add(exe);
        foreach (var a in new[] { "-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose", "--include-partial-messages",
                     "--permission-mode", mode, "--append-system-prompt", Rules, "--model", model, "--effort", c.Run,
                     c.Used ? "--resume" : "--session-id", c.Id }) psi.ArgumentList.Add(a);
        psi.Environment.Remove("AGENT_ID");     // set when another agent started the host: its SessionStart hook then opens a VS Code window
        psi.Environment.Remove("CLAUDECODE");
        var err = new System.Text.StringBuilder();
        Process p;
        try { p = Process.Start(psi)!; } catch (Exception e) { Log.Write("claude: could not start: " + e.Message); return; }
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (err) err.AppendLine(e.Data); };
        p.BeginErrorReadLine();
        c.Warm = p; c.Booted = Key(c); acct = Account() ?? acct;
        RemoteOn(p, c.Bridge?.session == c.Id ? c.Bridge?.id : null);
        new Thread(() => Read(c, p, err)) { IsBackground = true, Name = "claude" }.Start();
        Log.Write($"claude: waiting ({model}, {c.Run}{(c.Used ? ", resumed" : "")}, {chats.Count} chat{(chats.Count == 1 ? "" : "s")} open)");
    }

    public void Ask(string text, JsonElement? images = null)
    {
        var pics = images is { ValueKind: JsonValueKind.Array } im ? im.EnumerateArray().Select(x => (type: x.GetProperty("type").GetString() ?? "", data: x.GetProperty("data").GetString() ?? ""))
            .Where(x => x.type is "image/png" or "image/jpeg" or "image/gif" or "image/webp" && x.data.Length > 0).Take(8).ToList() : new();
        if (string.IsNullOrWhiteSpace(text) && pics.Count == 0) return;
        if (string.IsNullOrWhiteSpace(text)) text = "(see the picture)";
        lock (gate)
        {
            var c = cur;
            if (c.Turn != null && c.Warm is { HasExited: false })   // typed while it works: straight into the running turn, like VS Code (Claude Code reads it at its next step,
            {                                                         // or answers it as a follow-up turn right after; see "result" below)
                try { Write(c.Warm, Content(text, pics)); c.Turn.Extra++; Log.Write($"claude: slipped into the running ask, {text.Length} chars"); }
                catch (Exception e) { send("claude-error", new { message = "Could not add it: " + e.Message }); }
                return;
            }
            if (c.Turn != null) { send("claude-error", new { message = "Still working on the last ask." }); return; }
            if (Exe == null) { send("claude-error", new { message = "Claude Code is not installed on this PC." }); return; }
            c.Run = text.StartsWith("🎙") ? "low" : effort;   // a different effort = a reboot on the same session (a few seconds)
            if (c.Warm == null || c.Warm.HasExited || c.Booted != Key(c)) Boot(c);
            if (c.Warm == null) { send("claude-error", new { message = "Claude Code would not start." }); return; }
            bool followUp = c.Used;
            var t = Begin(c);
            Log.Write($"claude: ask ({(followUp ? "follow-up" : "new chat")}, {model}, {c.Run}) {text.Length} chars");
            send("claude-start", new { text, followUp, images = pics.Count });
            try { Write(c.Warm, Content(text, pics)); }
            catch (Exception e) { t.Error = e.Message; Kill(c.Warm); }   // its reader ends the turn
        }
    }

    static object Content(string text, List<(string type, string data)> pics) => pics.Count == 0 ? text   // pasted pictures go in as image blocks
        : pics.Select(x => (object)new { type = "image", source = new { type = "base64", media_type = x.type, data = x.data } }).Append(new { type = "text", text }).ToArray();
    static void Write(Process p, object content) { p.StandardInput.WriteLine(JsonSerializer.Serialize(new { type = "user", message = new { role = "user", content } })); p.StandardInput.Flush(); }

    Turn Begin(Chat c)   // under gate: a new ask holds the session (a later restart resumes it) and is stopped after 30 minutes
    {
        var t = c.Turn = new Turn();
        c.Used = true; c.Booted = Key(c);
        t.Killer = new System.Threading.Timer(_ => { lock (gate) if (c.Turn == t) { t.Error = "Took longer than 30 minutes, stopped."; Kill(c.Warm); } }, null, 1_800_000, Timeout.Infinite);
        Busy();
        return t;
    }

    // Remote Control: every claude the panel starts joins the user's Claude app as "Tidepool", so the chat can go on from the phone.
    // Reattaching keeps the same phone session across reboots of the same chat (effort/model changes, crashes).

    // The account switcher swaps the login under a running claude (it reloads credentials live), but the phone link stays on the old account.
    // Every 10 s: if the account changed, drop the link and make a new one on the new account, in the same process, so a reply in flight goes on.
    static readonly string GlobalJson = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json");
    string? acct;
    System.Threading.Timer? watch;
    static string? Account()
    {
        try { using var d = JsonDocument.Parse(File.ReadAllText(GlobalJson)); return d.RootElement.GetProperty("oauthAccount").GetProperty("accountUuid").GetString(); }
        catch { return null; }   // mid-write: next tick
    }
    void Watch()
    {
        var a = Account(); if (a == null) return;
        lock (gate)
        {
            if (acct == null || a == acct) { acct = a; return; }
            acct = a;
            foreach (var c in chats)
            {
                c.Bridge = null;
                if (c.Warm is not { HasExited: false } p) continue;   // the next boot links on the new account
                Log.Write("claude: account switched, moving the phone link to it");
                try { p.StandardInput.WriteLine(JsonSerializer.Serialize(new { type = "control_request", request_id = "rc-off", request = new { subtype = "remote_control", enabled = false } })); p.StandardInput.Flush(); }
                catch (Exception e) { Log.Write("claude: remote control off failed: " + e.Message); }   // its reply (rc-off) links again
            }
        }
    }

    void RemoteOn(Process p, string? reattach)   // under gate
    {
        var req = new Dictionary<string, object> { ["subtype"] = "remote_control", ["enabled"] = true, ["name"] = "Tidepool" };
        if (reattach != null) req["reattach_session_id"] = reattach;
        try { p.StandardInput.WriteLine(JsonSerializer.Serialize(new { type = "control_request", request_id = reattach != null ? "rc-re" : "rc", request = req })); p.StandardInput.Flush(); }
        catch (Exception e) { Log.Write("claude: remote control request failed: " + e.Message); }
    }

    void RemoteReply(Chat c, Process p, JsonElement r)
    {
        var id = r.TryGetProperty("request_id", out var x) ? x.GetString() : null;
        if (id is not ("rc" or "rc-re" or "rc-off")) return;
        lock (gate)
        {
            if (c.Warm != p) return;
            if (id == "rc-off") { RemoteOn(p, null); return; }   // the old account's link is down: a new one on the current account (enable before this would hand back the old link)
            if (r.TryGetProperty("subtype", out var s) && s.GetString() == "success" && r.TryGetProperty("response", out var b) && b.TryGetProperty("bridge_session_id", out var bs) && bs.GetString() is { } bid)
            { c.Bridge = (c.Id, bid); Log.Write($"claude: remote control on ({bid})"); }
            else { Log.Write("claude: remote control refused: " + r.GetRawText()); if (id == "rc-re") { c.Bridge = null; RemoteOn(p, null); } else RemoteRetry(c, p); }   // the old phone session is gone: start a new one
        }
    }

    // Offline at login, or the link dropped for good: keep trying, so the phone always has this chat while the PC is on.
    void RemoteRetry(Chat c, Process p) => Task.Delay(30_000).ContinueWith(_ => { lock (gate) if (c.Warm == p && !p.HasExited) RemoteOn(p, c.Bridge?.session == c.Id ? c.Bridge?.id : null); });

    /// <summary>A message typed on the phone started a turn: the panel shows it like its own ask.</summary>
    void RemoteAsk(Chat c, Process p)
    {
        lock (gate) { if (c.Warm != p || c.Turn != null) return; Begin(c); }
        var file = Transcript(c.Id);
        Send(c, "claude-start", new { text = "", followUp = true, remote = true });
        // the words come from the transcript, which claude writes a while after the turn starts; its uuid is not the command_uuid, so: the newest typed message
        var since = DateTimeOffset.UtcNow.AddSeconds(-30);
        bool Fresh(JsonElement m) => m.TryGetProperty("timestamp", out var ts) && DateTimeOffset.TryParse(ts.GetString(), out var at) && at > since;
        new Thread(() =>
        {
            string? text = null;
            for (int i = 0; i < 40 && text == null; i++)
            {
                try { if (File.Exists(file)) text = Lines(file).Reverse().Take(300).Where(m => m.TryGetProperty("type", out var ty) && ty.GetString() == "user" && Fresh(m)).Select(UserText).FirstOrDefault(x => x != null); } catch (Exception e) { if (i == 0) Log.Write("claude: phone text: " + e.Message); }
                if (text == null) Thread.Sleep(500);
            }
            Log.Write($"claude: phone ask {(text?.Length ?? 0)} chars");
            Send(c, "claude-remote-text", new { text = text ?? "(a message from your phone)" });
        }) { IsBackground = true, Name = "phone-text" }.Start();
    }

    void Read(Chat c, Process p, System.Text.StringBuilder err)
    {
        try
        {
            string? line;
            while ((line = p.StandardOutput.ReadLine()) != null)
            {
                Turn? t; lock (gate) t = c.Warm == p ? c.Turn : null;
                JsonElement m;
                try { m = JsonDocument.Parse(line).RootElement; } catch { continue; }
                var type = m.GetProperty("type").GetString();
                if (type == "system" && m.TryGetProperty("subtype", out var st) && st.GetString() == "init")
                {
                    bool more = false; if (t != null) lock (gate) if (t.Late != null) { t.Late.Dispose(); t.Late = null; t.Extra--; more = true; }
                    if (more) Send(c, "claude-delta", new { text = "\n\n" });   // a message slipped in after the last step: its follow-up turn goes on in the same reply
                    Init(c, m); continue;
                }
                if (type == "control_response" && m.TryGetProperty("response", out var cr)) { RemoteReply(c, p, cr); continue; }
                if (type == "system" && m.TryGetProperty("subtype", out var bs) && bs.GetString() == "bridge_state" && m.TryGetProperty("state", out var bst) && bst.GetString() == "failed")
                { Log.Write("claude: remote control failed: " + line); RemoteRetry(c, p); continue; }
                if (type == "command_lifecycle" && t == null && m.TryGetProperty("state", out var cs) && cs.GetString() == "started")   // a turn nobody here asked for: sent from the phone
                { RemoteAsk(c, p); continue; }
                if (t == null) continue;   // boot chatter (hooks), or a process already replaced
                var sub = m.TryGetProperty("parent_tool_use_id", out var pt) && pt.ValueKind == JsonValueKind.String;   // a subagent's own steps: the panel shows the Task step only
                switch (m.GetProperty("type").GetString())
                {
                    case "stream_event" when m.GetProperty("event") is var ev && ev.GetProperty("type").GetString() == "content_block_delta" && ev.GetProperty("delta") is var d:
                        var dt = d.GetProperty("type").GetString();
                        if (dt == "text_delta") Send(c, "claude-delta", new { text = d.GetProperty("text").GetString() });
                        else if (dt == "thinking_delta") Send(c, "claude-think", new { text = d.GetProperty("thinking").GetString() });
                        break;
                    case "assistant":   // a subagent's steps show too (sub = true): a long handoff otherwise looked frozen and got stopped
                        foreach (var x in m.GetProperty("message").GetProperty("content").EnumerateArray())
                            if (x.GetProperty("type").GetString() == "tool_use")
                                Send(c, "claude-tool", new { id = x.GetProperty("id").GetString(), name = (sub ? "↳ " : "") + x.GetProperty("name").GetString(), file = Detail(x.GetProperty("input")), input = Trim(x.GetProperty("input")), sub });
                        if (!sub && m.GetProperty("message").TryGetProperty("usage", out var u))   // how full the context is now: everything this call read
                            t.Ctx = new[] { "input_tokens", "cache_read_input_tokens", "cache_creation_input_tokens" }.Sum(k => u.TryGetProperty(k, out var v) && v.TryGetInt64(out var n) ? n : 0);
                        break;
                    case "user" when m.GetProperty("message").TryGetProperty("content", out var uc) && uc.ValueKind == JsonValueKind.Array:
                        foreach (var x in uc.EnumerateArray())
                            if (x.TryGetProperty("type", out var ct) && ct.GetString() == "tool_result")
                            {
                                var body = x.TryGetProperty("content", out var cc) ? cc.ValueKind == JsonValueKind.String ? cc.GetString() : cc.ValueKind == JsonValueKind.Array ? string.Join("\n", cc.EnumerateArray().Where(b => b.TryGetProperty("text", out _)).Select(b => b.GetProperty("text").GetString())) : "" : "";
                                Send(c, "claude-result", new { id = x.GetProperty("tool_use_id").GetString(), error = x.TryGetProperty("is_error", out var ie2) && ie2.ValueKind == JsonValueKind.True, text = body is { Length: > 3000 } ? body[..3000] + "\n…" : body });
                            }
                        break;
                    case "result":
                        var rr = m.TryGetProperty("result", out var r) ? r.GetString() ?? "" : "";
                        t.Reply = t.Reply == "" ? rr : rr == "" ? t.Reply : t.Reply + "\n\n" + rr;
                        if (m.TryGetProperty("is_error", out var ie) && ie.GetBoolean()) t.Error = t.Reply == "" ? "Claude Code reported an error." : t.Reply;
                        if (m.TryGetProperty("total_cost_usd", out var tc) && tc.TryGetDouble(out var cost)) t.Cost = cost;
                        if (m.TryGetProperty("modelUsage", out var mu) && mu.ValueKind == JsonValueKind.Object)
                            foreach (var x in mu.EnumerateObject()) if (x.Value.TryGetProperty("contextWindow", out var cw) && cw.TryGetInt64(out var w)) t.Window = Math.Max(t.Window, w);
                        lock (gate) if (t.Extra > 0 && t.Error == "" && !t.Stopped)   // something was slipped in: if Claude Code had not read it yet, a follow-up turn starts now
                            { t.Late?.Dispose(); var tt = t; t.Late = new System.Threading.Timer(_ => Finish(c, tt), null, 2500, Timeout.Infinite); break; }
                        Finish(c, t);
                        break;
                }
            }
            p.WaitForExit();
        }
        catch { }
        string why; lock (err) why = FirstLine(err.ToString()) ?? $"Claude Code stopped (code {(p.HasExited ? p.ExitCode : -1)}).";
        Turn? left;
        lock (gate)
        {
            if (c.Warm != p) return;   // replaced or dropped on purpose
            c.Warm = null; left = c.Turn;
            if (left == null && why.Contains("No conversation found")) { c.Id = Guid.NewGuid().ToString(); c.Used = false; }   // the resumed chat is gone: the next ask starts fresh
        }
        if (left != null) { if (left.Error == "") left.Error = why; Finish(c, left); }
        else Log.Write("claude: waiting claude ended: " + why);
    }

    void Finish(Chat c, Turn t)
    {
        bool shown, anyBusy;
        lock (gate)
        {
            if (c.Turn != t) return; c.Turn = null; t.Killer?.Dispose(); t.Late?.Dispose();
            if (t.Error.Contains("No conversation found")) { c.Id = Guid.NewGuid().ToString(); c.Used = false; }
            shown = c == cur;
            if (shown) { if (c.Warm == null || c.Warm.HasExited || c.Booted != Key(c)) Boot(c); }   // ready again for the next ask (after Stop, a crash, or a model change mid-ask)
            else { Drop(c); fresh.Add(c.Id); }   // a background chat is done: its claude goes, Past chats marks it new
            anyBusy = chats.Any(x => x.Turn != null);
            Busy();
        }
        if (t.Stopped) t.Error = "Stopped.";
        Log.Write($"claude: done{(shown ? "" : " (in the background)")}{(t.Error != "" ? ", error: " + t.Error : "")}");
        if (shown && t.Error != "") send("claude-error", new { message = t.Error });
        else if (shown) send("claude-done", new { text = t.Reply, usage = new { ctx = t.Ctx, window = t.Window, cost = t.Cost } });
        if (!anyBusy && File.Exists(RestartFlag))   // a rebuild asked for during a reply: run it once no chat is working (the restart ends them all)
        {
            try { File.Delete(RestartFlag); } catch { }
            Log.Write("claude: running the queued host restart");
            try { Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{RestartScript}\" -Now") { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden }); } catch (Exception e) { Log.Write("claude: queued restart failed: " + e.Message); }
        }
    }

    /// <summary>The init message, trimmed for the panel: slash commands (built-in and the user's skills), model, MCP servers.</summary>
    void Init(Chat c, JsonElement m)
    {
        string[] Names(string k) => m.TryGetProperty(k, out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : x.TryGetProperty("name", out var n) ? n.GetString() : null).OfType<string>().ToArray() : Array.Empty<string>();
        var info = new
        {
            commands = Names("slash_commands"), skills = Names("skills"), model = m.TryGetProperty("model", out var mo) ? mo.GetString() : null,
            mcp = m.TryGetProperty("mcp_servers", out var ms) && ms.ValueKind == JsonValueKind.Array ? ms.EnumerateArray().Select(x => new { name = x.TryGetProperty("name", out var n) ? n.GetString() : "", status = x.TryGetProperty("status", out var s2) ? s2.GetString() : "" }).ToArray() : null,
            tools = Names("tools").Length, mode = m.TryGetProperty("permissionMode", out var pm) ? pm.GetString() : mode, session = m.TryGetProperty("session_id", out var si) ? si.GetString() : c.Id,
        };
        lock (gate) c.Info = info;
        Send(c, "claude-init", info);
    }

    /// <summary>A tool's input for the panel's step details (diffs, commands, todos), long strings cut.</summary>
    static object Trim(JsonElement input)
    {
        var o = new Dictionary<string, object?>();
        foreach (var p in input.EnumerateObject())
            o[p.Name] = p.Value.ValueKind == JsonValueKind.String ? (p.Value.GetString() is { Length: > 4000 } s ? s[..4000] + "\n…" : p.Value.GetString())
                : p.Name is "todos" or "edits" ? JsonSerializer.Deserialize<object>(p.Value.GetRawText()) : p.Value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False ? p.Value.ToString() : null;
        return o;
    }

    /// <summary>What a tool call is about, in a few words: the file, the command, the search, the page.</summary>
    static string? Detail(JsonElement input)
    {
        foreach (var k in new[] { "file_path", "notebook_path", "command", "pattern", "query", "url", "description", "prompt" })
            if (input.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s)
                return k.EndsWith("path") ? Path.GetFileName(s) : s.Length > 90 ? s[..87] + "…" : s;
        return null;
    }

    static string? FirstLine(string s) => s.Split('\n').Select(x => x.Trim()).FirstOrDefault(x => x != "");
}
