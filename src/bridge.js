// window.host: the bridge between the console-desktop host (C#) and the page; contract in app/BRIDGE.md. Injected before the page's scripts.
(() => {
  const listeners = {}; let ttsN = 0;
  const send = msg => window.chrome.webview.postMessage(msg);
  const host = window.host = {
    covered: false, screen: null, screens: [], location: null, sun: null, windows: [],
    games: [], apps: [], nowPlaying: null, heard: [], tracks: [], notifications: [], menu: { open: false },
    focus: id => send({ type: "focus", id }),
    launch: id => send({ type: "launch", id }),
    media: (action, at) => send({ type: "media", action, at }),   // "playpause", "next", "prev", or "seek" with at = seconds
    replay: (at, kind = "track") => send({ type: "replay", at, kind }),   // a host.heard entry (by its at) again through Spotifast: kind "track", "album" or "artist"
    reveal: id => send({ type: "reveal", id }),          // a file / project / shortcut selected in Explorer
    openUrl: url => send({ type: "openUrl", url }),      // http(s) in the default browser
    browse: id => send({ type: "browse", id }),          // a folder's entries ("dir:<path>"), answered by a "folder" event (not "browse": events land on host.<type>)
    // Glass without live blur: hide your UI, wait two frames, then await host.snapshot(12) -> { url, width, height } (null on failure).
    snapshot: (blur = 12) => new Promise(res => { const f = v => { (listeners.snapshot || []).splice((listeners.snapshot || []).indexOf(f), 1); res(v); }; host.on("snapshot", f); send({ type: "snapshot", blur }); }),
    claude: {   // the Claude panel: events claude-start, claude-delta, claude-tool, claude-done, claude-error, claude-new, claude-busy (BRIDGE.md)
      ask: (text, images) => send({ type: "claude-ask", text, images }),   // images: [{ type: "image/png", data: base64 }]
      info: () => send({ type: "claude-info" }),                          // -> claude-init { commands, skills, model, mcp, tools, mode, session }
      sessions: () => send({ type: "claude-sessions" }),                  // -> claude-sessions [{ id, title, at, current, busy, fresh }]
      resume: id => send({ type: "claude-resume", id }),                  // -> claude-history { id, messages }
      files: q => send({ type: "claude-files", q }),                      // -> claude-files { q, files: [{ path, dir }] }
      newChat: () => send({ type: "claude-new" }),
      cancel: () => send({ type: "claude-cancel" }),
      set: (model, effort, mode) => send({ type: "claude-set", model, effort, mode }),   // mode: bypassPermissions | plan
    },
    // The Music tab's Spotify (BRIDGE.md): answers come as sp-state, sp-search, sp-list, sp-done, sp-now events
    spotify: {
      state: () => send({ type: "spotify", op: "state" }),
      connect: () => send({ type: "spotify", op: "connect" }),
      disconnect: () => send({ type: "spotify", op: "disconnect" }),
      search: q => send({ type: "spotify", op: "search", q }),
      list: uri => send({ type: "spotify", op: "list", uri }),                    // a playlist's / album's songs, an artist's albums, "spotifast:liked-songs"
      play: (uri, context = "") => send({ type: "spotify", op: "play", uri, context }),
      queue: (uri, name = "") => send({ type: "spotify", op: "queue", uri, name }),
      like: (uri, name = "") => send({ type: "spotify", op: "like", uri, name }),
      save: (uri, name = "") => send({ type: "spotify", op: "save", uri, name }),   // an album into "Your albums"
      add: (id, uri, name = "") => send({ type: "spotify", op: "add", id, uri, name }),   // a playlist id, a track uri
      now: () => send({ type: "spotify", op: "now" }),
      best: (q, kind = "track") => send({ type: "spotify", op: "best", q, kind }),   // signed out: Spotifast plays its best match
    },
    // The Video tab (src/YouTube.cs, yt-dlp): answers come as yt-search, yt-stream, yt-list and yt-feed events, to this page only (BRIDGE.md)
    yt: {
      search: (q, o = {}) => send({ type: "yt", op: "search", q, sort: o.sort ?? "", kind: o.kind ?? "", len: o.len ?? "" }),   // sort: date | views | rating; kind: video | channel | playlist; len: short | medium | long
      stream: id => send({ type: "yt", op: "stream", id }),   // also a prefetch: the host keeps the answer for an hour
      list: (kind, id, sort = "") => send({ type: "yt", op: "list", kind, id, sort }),   // kind: channel (UC id) | playlist (PL id) | mix (a video id: its "Up next")
      feed: ids => send({ type: "yt", op: "feed", ids }),   // the favourite channels' newest uploads (their RSS), newest first
      pop: v => send({ type: "yt", op: "pop", ...v }),   // { id, title, video, audio, t, vol, embed, to? }: a player window of its own (src/PopOut.cs, web/pop.html), one per video; to = { x, y } of a screen to open it filling
    },
    spectrum: on => send({ type: "spectrum", on: !!on }),   // the horizon visualizer wants "spectrum" events (int[32], 0..255, ~30/s; one all-zero frame when it goes quiet): on while it is on and this screen is seen (src/Spectrum.cs)
    voice: (on, quiet) => send({ type: "voice", on: !!on, quiet }),   // quiet = seconds of pause that end a listen, 0 = only voice(false)
    wake: (on, follow = 0) => send({ type: "wake", on: !!on, follow: +follow || 0 }),   // the always-on "Hey Marina" listener on/off (off while replies are read aloud); follow = s of follow-up window (no wake word), "idle" when it closes unused; "wake" events { state: wake | partial | heard | error, text }
    overlay: (state, said, reply) => send({ type: "overlay", state, said, reply }),   // the "Hey Marina" overlay over all apps (web/overlay.html): state listening | thinking | speaking | done (fades a few s later)
    // Read-aloud voice (Kokoro, src/Tts.cs): await host.tts(text, voice = "af_heart", speed = 1) -> a WAV data: url, null if Kokoro failed
    installUpdate: () => send({ type: "update-install" }),   // Update.cs: host.update = { version, url, notes } once a newer release is out; "update-failed" carries the error
    skinfile: { pick: () => send({ type: "skins", op: "pick" }), add: (name, data) => send({ type: "skins", op: "add", name, data }), remove: id => send({ type: "skins", op: "remove", id }) },   // Skins.cs; answers "skin-result" { op, ok, got: { id, skin }, error }
    sys: (op, v = 0) => new Promise(res => { const id = ++ttsN, f = x => { if (x.id !== id) return; (listeners.sys || []).splice((listeners.sys || []).indexOf(f), 1); res(x.r); }; host.on("sys", f); send({ type: "sys", op, v, id }); }),   // Sys.cs: vol, volset, volstep, mute, lock, sleep, shutdown, restart, shot, stats
    tts: (text, voice, speed) => new Promise(res => { const id = ++ttsN, f = v => { if (v.id !== id) return; (listeners.tts || []).splice((listeners.tts || []).indexOf(f), 1); res(v.url); }; host.on("tts", f); send({ type: "tts", id, text, voice, speed }); }),
    listMics: () => send({ type: "listMics" }),     // answered by a "mics" event (kept as host.mics): [{ id, name, on }] (on = Windows' default mic, the one speech listens through)
    setMic: id => send({ type: "setMic", id }),     // makes that mic Windows' default; a fresh "mics" event follows
    voiceSettings: () => send({ type: "voiceSettings" }), winSettings: () => send({ type: "voiceSettings", page: "home" }),   // opens Windows Settings › Privacy › Speech   // Windows speech recognition: "voice" events { state: listening | partial | end | error, text, privacy? }
    openMenu: (here) => send({ type: "openMenu", here: !!here }),   // here: on this page's screen (a click on it), not the usual menu screen
    closeMenu: () => send({ type: "closeMenu" }),
    tray: () => send({ type: "tray" }),   // Windows' tray icons (hidden-icons flyout), opened under the mouse
    shellMenu: () => send({ type: "shellMenu" }),   // Explorer's own desktop menu (View, Sort by, New, Display settings…), under the mouse
    setup: () => send({ type: "setup" }),   // Setup's window (config.json); saving restarts the desktop
    close: id => send({ type: "close", id }),
    exitToStock: () => send({ type: "exitToStock" }),
    on(type, f) { (listeners[type] ||= []).push(f); },
  };
  // Chromium paces every view to the main screen's vsync (144 Hz here). On a slower screen, run the page's
  // requestAnimationFrame callbacks at that screen's own rate: frames are let through on an accumulating schedule
  // (60 from 144 = alternating gaps of 2 and 3 ticks), and every callback of a let-through frame runs together.
  const raf = window.requestAnimationFrame.bind(window), caf = window.cancelAnimationFrame.bind(window), ids = new Map();
  let gap = 0, next = 0, on = -1, seq = 0;
  window.requestAnimationFrame = cb => {
    const id = ++seq;
    const tick = t => {
      if (gap && t !== on) {
        if (t < next - 1) { ids.set(id, raf(tick)); return; }
        on = t; next = t - next > gap ? t + gap : next + gap;
      }
      ids.delete(id); cb(t);
    };
    ids.set(id, raf(tick));
    return id;
  };
  window.cancelAnimationFrame = id => { caf(ids.get(id)); ids.delete(id); };
  const pace = () => { const s = host.screen, top = Math.max(...host.screens.map(x => x.hz || 0)); gap = s && s.hz && s.hz < top ? 1000 / s.hz : 0; };

  // Parked (hidden while covered) and shown again: two frames later the host takes the still picture off (Host.Scene.Drawn). Real rAF, unpaced.
  document.addEventListener("visibilitychange", () => { if (!document.hidden) raf(() => raf(() => setTimeout(() => send({ type: "drawn" }), 30))); });
  window.chrome.webview.addEventListener("message", e => {
    const { type, value } = e.data;
    if (type !== "live" && type !== "snapshot" && type !== "tts" && type !== "sys" && type !== "spectrum" && type !== "skin-result" && type !== "voice" && type !== "wake" && !type.startsWith("claude-")) host[type] = value;
    if (type === "screen" || type === "screens") pace();
    for (const f of listeners[type] || []) { try { f(value); } catch (err) { console.error(err); } }
    window.dispatchEvent(new CustomEvent("host", { detail: { type, value } }));
  });
  // Menu: Escape closes it unless the page handled it (preventDefault, e.g. to go back a level). Pad B becomes that same Escape, minus the closing.
  // Checked after every listener has run (the bridge's own listener is registered first), so the page's preventDefault() counts. Pad B arrives here too.
  // Pad ○ never closes it (e.pad): mashed ○ used to drop the menu, and the pad then had nothing to drive until PS; PS is the pad's open/close.
  addEventListener("keydown", e => { if (e.key === "Escape" && !e.pad) setTimeout(() => { if (!e.defaultPrevented && host.menu.open) host.closeMenu(); }); });
  // While the menu is open the pad (DualSense, standard mapping) drives the page as keys, so one key handler covers both:
  // ✕ Enter, ○ Escape, □ Delete, △ F2 (Claude), Create Tab (windows), Options F3 (notifications), L1/R1 PageUp/PageDown, L2/R2 [ ] (sort),
  // D-pad and left stick arrows (held: repeat after 400 ms, every 110 ms). The PS button (16) is the host's: it toggles the menu.
  const PADKEYS = { 0: "Enter", 1: "Escape", 2: "Delete", 3: "F2", 4: "PageUp", 5: "PageDown", 6: "[", 7: "]", 8: "Tab", 9: "F3", 12: "ArrowUp", 13: "ArrowDown", 14: "ArrowLeft", 15: "ArrowRight" };   // L2 / R2 = [ ] = sort
  const REPEATS = new Set(["ArrowUp", "ArrowDown", "ArrowLeft", "ArrowRight", "PageUp", "PageDown"]);
  let padTimer = 0, held = {}, hostPad = [];   // hostPad: keys the host decoded from the DualSense's own reports (WebView2's Gamepad API does not list it)
  host.on("pad", keys => { hostPad = keys || []; });
  const press = key => { const e = new KeyboardEvent("keydown", { key, code: key, bubbles: true, cancelable: true }); e.pad = true; (document.activeElement || document.body).dispatchEvent(e); };
  host.on("menu", m => {
    clearInterval(padTimer); held = null; hostPad = [];   // null: whatever is down when the menu opens (the press that opened it) waits for a release
    if (m.open) padTimer = setInterval(() => {
      const now = performance.now(), down = new Set(hostPad);
      for (const p of navigator.getGamepads()) {
        if (!p) continue;
        for (const i in PADKEYS) if (p.buttons[i]?.pressed) down.add(PADKEYS[i]);
        const [x = 0, y = 0] = p.axes;
        if (Math.abs(x) > 0.5 && Math.abs(x) > Math.abs(y)) down.add(x > 0 ? "ArrowRight" : "ArrowLeft");
        else if (Math.abs(y) > 0.5) down.add(y > 0 ? "ArrowDown" : "ArrowUp");
      }
      if (!held) { held = {}; for (const k of down) held[k] = Infinity; }
      for (const k in held) if (!down.has(k)) delete held[k];
      for (const k of down) {
        if (!(k in held)) { held[k] = now + 400; press(k); }
        else if (REPEATS.has(k) && now >= held[k]) { held[k] = now + 110; press(k); }
      }
    }, 16);
  });
  send({ type: "ready" });
})();
