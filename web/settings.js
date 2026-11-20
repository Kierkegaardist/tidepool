/* settings.js: the Settings tab (user, 15 Nov: "a proper settings ... fitting", with skins). Each tile opens a column, PS3 style
   (S.col = { set: page, i }): Enter changes a value or opens, ←/→ step it, Del removes (a skin you added). A row is
   { n, d, ic, cover, run(), adj(dir), del(), on }. Values live in localStorage (tp-*) and apply at once; nothing restarts.
   Winamp skins you add (a .wsz picked here or dropped anywhere on the desktop) are installed by the host (src/Skins.cs) into
   assets/skins/u-(name)/ and listed in assets/skins/user.json, merged into window.SKINS below. Uses globals of index.html/core.js at call time. */
const LS = { get: (k, d) => { try { return localStorage.getItem(k) ?? d; } catch { return d; } }, set: (k, v) => { try { localStorage.setItem(k, v); } catch {} } };
const abs = p => new URL(p, location.href).href;   // I() treats a bare path as an icon name
const onOff = (on, what = "") => `${on ? "On" : "Off"}${what ? " · " + what : ""}`;
const step = (list, cur, dir) => list[(list.indexOf(cur) + dir + list.length) % list.length];
const VOICES = [["zf_xiaoyi", "Xiaoyi (soft)"], ["af_heart", "Heart (warm)"], ["af_bella", "Bella (bright)"], ["af_nicole", "Nicole (whisper)"], ["bf_emma", "Emma (British)"], ["am_puck", "Puck (playful)"], ["bm_george", "George (British)"]];
const HVIZ = ["off", "subtle", "full"];
const AMBIENTS = [15, 20, 30, "full"];   // Settings › Scenes › Ambient motion (motion.js ambient clock)
const SPEEDS = [0.9, 1, 1.05, 1.15, 1.3], FOLLOWS = [0, 5, 8, 12, 20], SHADES = ["auto", "open", "closed"];
const SHADE_TXT = { auto: "Collapsed except on Music", open: "Always open", closed: "Always collapsed" };

/* ---- the tiles ---- */
function settingsItems() {
  const sk = Object.keys(SKINS), mine = sk.filter(k => SKINS[k].user).length, sc = H?.screens ?? [];
  return [
    ...(H?.update ? [{ n: `Install update ${H.update.version}`, d: "Tidepool restarts in a minute · your settings and chats stay", ic: "@gear.svg", run: () => { H.installUpdate(); toast("@gear.svg", "Updating Tidepool…", "Downloading " + H.update.version); }, id: "set:update" }] : []),
    { n: "Winamp", d: `Wearing ${SKINS[WA.id]?.name ?? WA.id} · ${plural(sk.length, "skin")}${mine ? `, ${mine} yours` : ""}`, ic: "@skin.svg", set: "winamp", id: "set:winamp" },
    { n: "Scenes", d: ROT.pin == null ? `Follows the sun · now ${SCENES[sceneAt(new Date()).i].name}` : `Pinned to ${SCENES[ROT.pin].name}`, ic: "@scene", set: "scenes", id: "set:scenes" },
    { n: "Sound", d: window.Sound?.mute() ? "Desktop sounds off" : `Desktop sounds at ${Math.round((window.Sound?.volume() ?? .6) * 100)}%`, ic: "@speaker2.svg", set: "sound", id: "set:sound" },
    { n: "Marina", d: `“Hey Marina” ${S.wake ? "on" : "off"} · ${VOICES.find(v => v[0] === TTS_VOICE)?.[1].split(" ")[0] ?? TTS_VOICE}'s voice`, ic: "@mic.svg", set: "marina", id: "set:marina" },
    { n: "Screens", d: sc.length > 1 ? `${plural(sc.length, "screen")} · main one: screen ${sc.findIndex(x => x.role === "main") + 1}` : "One screen", ic: "@window.svg", set: "screens", id: "set:screens" },
    { n: "Location and sun", d: `${loc.place} · ${loc.lat.toFixed(2)}° N, ${loc.lon.toFixed(2)}° E`, ic: "@pin.svg", act: "setup", id: "set:place" },
    { n: "Setup", d: `${NM.player} · ${NM.browser} · ${CFG.modules.studio === false ? "no studio" : NM.daw} · ${NM.ai}`, ic: "@gear.svg", act: "setup", id: "set:setup" },
    { n: "Windows Settings", d: "Opens the Windows Settings app", ic: "@gear.svg", act: "winset", id: "set:win" },
    { n: "Classic desktop", d: "Back to stock Windows", ic: "@window.svg", act: "hatch", id: "set:hatch" },
  ];
}
const SETPAGES = { winamp: "Winamp", scenes: "Scenes", sound: "Sound", marina: "Marina", screens: "Screens" };

/* ---- the rows ---- */
function setRows(c) {
  if (c.set === "winamp") {
    const ids = Object.keys(SKINS).sort((a, b) => (SKINS[a].user ? 1 : 0) - (SKINS[b].user ? 1 : 0)), shade = LS.get("tp-wa-shade", "closed");
    return [
      ...ids.map(id => ({ n: SKINS[id].name, d: `${id === WA.id ? "✓ Wearing it · " : ""}${SKINS[id].user ? "Added by you · Del removes it" : "Built in"}`, ic: abs(`${V4}skins/${id}/main.png`), cover: 1, wide: 1, on: id === WA.id,
        run: () => { buildWinamp(id); toast("@skin.svg", "Winamp skin", SKINS[id].name); }, del: SKINS[id].user ? () => skinRemove(id) : null })),
      { n: "Add a skin…", d: "Pick .wsz files · or drop one anywhere on the desktop", ic: "@skin.svg", run: () => H?.skinfile ? H.skinfile.pick() : toast("@skin.svg", "Adding skins needs the desktop", "") },
      { n: "Find more skins", d: "The Winamp Skin Museum, 80,000 classic skins · opens in your browser", ic: "web.png", run: () => H?.openUrl ? H.openUrl("https://skins.webamp.org/") : open("https://skins.webamp.org/") },
      { n: "Collapsed or open", d: `${SHADE_TXT[shade]} · ←→ or Enter changes it`, ic: "@skin.svg", run: () => setShade(step(SHADES, shade, 1)), adj: d => setShade(step(SHADES, shade, d)) },
      { n: "Visualizer", d: onOff(!WA.visOff, "the little spectrum bars"), ic: "@skin.svg", run: () => { WA.visOff = !WA.visOff; LS.set("tp-wa-vis", WA.visOff ? "0" : "1"); } },
      { n: "A new skin every day", d: onOff(LS.get("tp-skinday") === "1", "wears the next one each morning"), ic: "@skin.svg", run: () => { LS.set("tp-skinday", LS.get("tp-skinday") === "1" ? "0" : "1"); skinOfDay(true); } },
    ];
  }
  if (c.set === "scenes") return [
    { n: "Follow the sun", d: `${ROT.pin == null ? "✓ " : ""}A new scene each ${ROT.mode === "4h" ? "four hours" : "day"}, lit by the real sun`, ic: "@scene", run: () => pinScene(null) },
    { n: "How often it changes", d: `${ROT.mode === "4h" ? "Every four hours" : "Once a day, at solar midnight"} · Enter changes it`, ic: "@scene", run: () => { ROT.mode = ROT.mode === "4h" ? "day" : "4h"; LS.set("tp-rotate", ROT.mode); render(); } },
    { n: "Horizon visualizer", d: `${{ off: "Off", subtle: "Subtle", full: "Full" }[window.Horizon?.mode() ?? "full"]} · music rises as light behind the land · ←→ or Enter changes it`, ic: "@scene",
      run: () => Horizon.set(step(HVIZ, Horizon.mode(), 1)), adj: d => Horizon.set(step(HVIZ, Horizon.mode(), d)) },
    { n: "Ambient motion", d: `${(r => r === "full" ? "Full rate · the smoothest, about 3× the graphics power" : `${r} updates a second${r === 15 ? " · lightest on power" : ""}`)(window.Motion?.ambient() ?? 15)} · fireflies, bubbles, Marina's swim · ←→ or Enter changes it`, ic: "@scene",
      run: () => Motion.ambient(step(AMBIENTS, Motion.ambient(), 1)), adj: d => Motion.ambient(step(AMBIENTS, Motion.ambient(), d)) },
    { n: "Rest while you work", d: onOff(LS.get("tp-rest", "1") !== "0", LS.get("tp-rest", "1") !== "0" ? "the scene holds still while an app is in front · saves power" : "the scene keeps moving behind your apps"), ic: "@scene",
      run: () => { LS.set("tp-rest", LS.get("tp-rest", "1") === "0" ? "1" : "0"); dispatchEvent(new Event("tp-rest")); } },
    ...SCENES.map((x, i) => ({ n: x.name, d: `${ROT.pin === i ? "✓ Pinned · " : ""}${x.where}`, ic: abs(x.img), cover: 1, wide: 1, run: () => pinScene(ROT.pin === i ? null : i) })),
  ];
  if (c.set === "sound") {
    const Snd = window.Sound, v = Math.round((Snd?.volume() ?? .6) * 10) * 10, vol = n => { Snd?.volume(Math.max(0, Math.min(100, n)) / 100); Snd?.play("move", { axis: "x", i: Math.round(n / 20) }); };
    return [
      { n: "Desktop sounds", d: onOff(!Snd?.mute(), "the chimes when you move and pick"), ic: "@speaker2.svg", run: () => { Snd?.mute(!Snd.mute()); if (!Snd?.mute()) Snd?.play("select"); } },
      { n: "Volume", d: `${v}% · ←→ change it`, ic: "@speaker2.svg", run: () => vol(v >= 100 ? 10 : v + 10), adj: d => vol(v + d * 10) },
      { n: "Quiet late at night", d: onOff(LS.get("tp-quiet") !== "0", "no sounds after midnight"), ic: "@moon.svg", run: () => { LS.set("tp-quiet", LS.get("tp-quiet") === "0" ? "1" : "0"); dispatchEvent(new Event("tp-quiet")); } },
      { n: "Try it", d: "Plays the wake-up chime", ic: "@speaker2.svg", run: () => Snd?.play("wake") },
    ];
  }
  if (c.set === "marina") {
    const vn = VOICES.find(x => x[0] === TTS_VOICE)?.[1] ?? TTS_VOICE, voice = d => { const i = (VOICES.findIndex(x => x[0] === TTS_VOICE) + d + VOICES.length) % VOICES.length; TTS_VOICE = VOICES[i][0]; LS.set("tp-voice", TTS_VOICE); speak("Hi, I'm Marina. Blub!"); };
    const speed = d => { TTS_SPEED = step(SPEEDS, TTS_SPEED, d); LS.set("tp-tts-speed", TTS_SPEED); speak("This is how fast I talk."); };
    const follow = d => { FOLLOW_S = step(FOLLOWS, FOLLOW_S, d); LS.set("tp-follow", FOLLOW_S); };
    return [
      { n: "“Hey Marina”", d: onOff(S.wake, "she listens for her name"), ic: "@mic.svg", run: () => wakeToggle() },
      { n: "Read replies aloud", d: onOff(S.talk, "in the chat panel too, not just spoken asks"), ic: "@speaker2.svg", run: () => talkToggle() },
      { n: "Voice", d: `${vn} · ←→ or Enter tries the next`, ic: "@mic.svg", run: () => voice(1), adj: voice },
      { n: "Talking speed", d: `${TTS_SPEED}× · ←→ changes it`, ic: "@speaker2.svg", run: () => speed(1), adj: speed },
      { n: "Keep listening after she answers", d: `${FOLLOW_S ? FOLLOW_S + " seconds, for a follow-up without her name" : "Off"} · ←→ changes it`, ic: "@mic.svg", run: () => follow(1), adj: follow },
      { n: "What she can do without the AI", d: "Timers, music, the weather, sums, opening games… · Enter hears it", ic: "orb.svg", run: () => speak(Marina.LOCAL_HELP) },
      { n: "Her own notes", d: `${plural(Marina.MST.notes.length, "thing")} you asked her to remember · Del forgets them`, ic: "@mic.svg", run: () => speak(Marina.MST.notes.length ? "You told me: " + Marina.MST.notes.map(x => Marina.flip(x.t)).join(". ") : "You haven't asked me to remember anything yet."), del: () => { Marina.MST.notes = []; Marina.msave(); toast("@mic.svg", "Marina forgot her notes", ""); } },
    ];
  }
  if (c.set === "mvol") {   // the Music tab's volume column: the player's own level (also the Winamp slider) and the PC's
    const p = PV.p, m = PV.m;
    return [
      { n: `${NM.player ?? "Player"} volume`, d: p ? `${p.vol}% · ←→ change it · the Winamp slider too` : "The player makes no sound yet · start a song", ic: "@speaker2.svg", run: () => pvol("pvolstep", 10), adj: d => pvol("pvolstep", d * 10) },
      { n: p?.muted ? "Unmute the player" : "Mute the player", d: "Only the music, not the PC", ic: "@speaker2.svg", run: () => pvol("pmute", p?.muted ? 0 : 1) },
      { n: "PC volume", d: m ? `${m.vol}%${m.muted ? " · muted" : ""} · ←→ change it` : "…", ic: "@speaker2.svg", run: () => pvol("volstep", 10), adj: d => pvol("volstep", d * 10) },
      { n: m?.muted ? "Unmute the PC" : "Mute the PC", d: "Everything", ic: "@speaker2.svg", run: () => pvol("mute", m?.muted ? 0 : 1) },
    ];
  }
  if (c.set === "screens") {
    const sc = H?.screens ?? [], role = { main: "Main · the menu", partner: "Beside it · clock, Winamp, notifications", extra: "The live scene" };
    return [
      ...(sc.length ? sc : [{ role: "main", width: innerWidth, height: innerHeight }]).map((x, i) => ({ n: `Screen ${i + 1} · ${role[x.role] ?? "Main"}`, d: `${x.width} × ${x.height}${x.primary ? " · Windows' main display" : ""}${x.hz ? ` · ${x.hz} Hz` : ""}`, ic: "@window.svg", info: 1 })),
      { n: "Arrange screens…", d: "Order, the main one, which ones Tidepool runs on · in Setup", ic: "@gear.svg", run: () => activate({ act: "setup" }) },
      { n: "Video windows", d: "Open where you left the last one · flick one by its title bar to send it to another screen", ic: "@video.svg", info: 1 },
    ];
  }
  return null;
}

/* ---- keys inside a Settings column: ←/→ step a value; Del removes. true = handled ---- */
function setKey(k, r) {
  if ((k === "ArrowLeft" || k === "ArrowRight") && r?.adj) { r.adj(k === "ArrowRight" ? 1 : -1); return true; }
  if (k === "Delete" && r?.del) { r.del(); return true; }
  return false;
}

/* ---- Winamp: collapsed by default or not, the visualizer, a skin a day, adding and removing skins ---- */
function setShade(v) { LS.set("tp-wa-shade", v); S.shade = null; sync(); }
WA.visOff = LS.get("tp-wa-vis") === "0";
function skinOfDay(now) {   // "A new skin every day": the next skin in the list each new day (local date)
  if (LS.get("tp-skinday") !== "1") return;
  const day = new Date().toDateString(); if (!now && LS.get("tp-skinday-at") === day) return;
  const ids = Object.keys(SKINS); LS.set("tp-skinday-at", day); buildWinamp(ids[(ids.indexOf(WA.id) + 1) % ids.length]);
}
setInterval(() => skinOfDay(false), 60e3);
function skinRemove(id) { H?.skinfile?.remove(id); }
fetch(V4 + "skins/user.json", { cache: "no-store" }).then(r => r.ok ? r.json() : {}).then(u => {   // skins you added (Skins.cs)
  for (const [id, v] of Object.entries(u ?? {})) SKINS[id] = { ...v, user: true };
  const want = LS.get("tp-skin"); if (want && SKINS[want] && WA.id !== want) buildWinamp(want);   // core.js started before this list arrived
  skinOfDay(false); if (S.col?.set) drawCol();
}).catch(() => skinOfDay(false));
window.host?.on?.("skin-result", v => {
  if (v.op === "add" && v.ok) { const { id, skin } = v.got; SKINS[id] = { ...skin, user: true }; buildWinamp(id); toast(abs(`${V4}skins/${id}/main.png`), "Skin added", `${skin.name} · wearing it now`); }
  else if (v.op === "remove" && v.ok) { const was = WA.id === v.id; delete SKINS[v.id]; if (was) buildWinamp(SKINS.aero ? "aero" : Object.keys(SKINS)[0]); toast("@skin.svg", "Skin removed", ""); }
  else if (!v.ok) toast("@skin.svg", v.op === "remove" ? "Couldn't remove it" : `Couldn't add ${v.name ?? "that skin"}`, v.error ?? "");
  if (S.col?.set) drawCol(); sync();
});
/* a .wsz dropped anywhere on the desktop goes in (the menu is up, so the desktop's own window takes the drop) */
addEventListener("dragover", e => { if ([...(e.dataTransfer?.items ?? [])].some(i => i.kind === "file")) e.preventDefault(); });
addEventListener("drop", e => {
  const fs = [...(e.dataTransfer?.files ?? [])].filter(f => /\.(wsz|zip)$/i.test(f.name)); if (!e.dataTransfer?.files?.length) return;
  e.preventDefault(); if (!fs.length) { toast("@skin.svg", "Only Winamp skins can be dropped here", ".wsz files"); return; }
  if (!H?.skinfile) { toast("@skin.svg", "Adding skins needs the desktop", ""); return; }
  for (const f of fs) { const rd = new FileReader(); rd.onload = () => H.skinfile.add(f.name, String(rd.result).split(",")[1]); rd.readAsDataURL(f); toast("@skin.svg", "Adding a skin…", f.name); }
});

/* ---- player + PC volume (Music › Volume, Winamp slider): PV.p = the player's { vol, muted }, PV.m = the PC's ---- */
const PV = { p: null, m: null };
function pvRead() { const h = window.host?.sys; if (!h) return; Promise.all([h("pvol"), h("vol")]).then(([p, m]) => { PV.p = p; PV.m = m; if (p) waVol(p.vol / 100); drawCol(); sync(); }); }
function pvol(op, v) { window.host?.sys?.(op, v)?.then(r => { if (!r) return; if (op[0] === "p") { PV.p = r; waVol(r.vol / 100); } else PV.m = r; drawCol(); sync(); }); }
setTimeout(pvRead, 1500); setInterval(() => { if (S.cat === "music") pvRead(); }, 5000);
