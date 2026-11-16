/* Layout core: the Tidepool v4 engine (sun, phases, scenes, Winamp) shared by every layout page.
   A page puts gadgets where it wants with <div data-g="name">; the markup below is filled in.
   Gadgets a page leaves out are skipped silently. Optional hook: window.onRender({now, sun, ev, ph, sel}). */
const IC = n => `assets/icons/${n}`;
const GADGETS = {
  clock: `<div class="clock"><div class="dial"><div class="inner"></div></div>
    <div class="num" style="left:116px;top:38px">12</div><div class="num" style="left:192px;top:116px">3</div><div class="num" style="left:40px;top:116px">9</div>
    <div class="brand">TIDEPOOL</div><div class="sub"><div class="ring" id="ring"><div class="ptr" id="ptr"></div><div class="cap"></div></div></div>
    <div class="sub-lbl" id="phname">Midday</div><div class="hand hr" id="hh"></div><div class="hand mn" id="hm"></div><div class="hand sc" id="hs"></div><div class="hub"></div><div class="dome"></div></div>`,
  plaque: `<div class="plaque engrave"><span class="screw" style="left:5px;top:5px"></span><span class="screw" style="right:5px;top:5px;--r:-20deg"></span><span class="screw" style="left:5px;bottom:5px;--r:70deg"></span><span class="screw" style="right:5px;bottom:5px"></span>
    <b id="nextev"></b><div class="row"><span id="rise"></span><span id="set"></span><span id="dlen"></span></div><div class="row"><span id="place"></span><span id="elev"></span></div></div>`,
  winamp: `<div class="wa-gadget"><div class="wa" id="wa"></div><div class="wa-skins" id="waskins"></div></div>`,
  gauges: `<div class="gauges"><div class="gauge"><div class="gface"><div class="mask"></div><span><b>34</b>CPU %</span></div><div class="needle" id="n1"></div><div class="hub"></div><div class="dome"></div></div>
    <div class="gauge"><div class="gface"><div class="mask"></div><span><b>61°</b>GPU</span></div><div class="needle" id="n2"></div><div class="hub"></div><div class="dome"></div></div>
    <div class="leds"><span class="led"></span>Disk OK<br>C: 41 GB<br>Steam 4 min</div></div>`,
  scenes: `<div class="scenes"><div class="sframe" id="sframe"><div class="cap"><b id="sname"></b><span id="snext"></span></div></div><div class="srow" id="srow"></div></div>`,
  launcher: [["music", "Music"], ["studio", "Studio"], ["games", "Games"], ["web", "Web"], ["claude", "Claude"]]
    .map(([k, n]) => `<div class="slab" data-k="${k}"><img class="ic" src="${IC(k + ".png")}" alt="">${n}</div>`).join(""),
  caption: `<b id="capt"></b><p id="capsub"></p>`,
  claude: `<div class="titlebar"><span class="t">Claude</span><span class="sp"></span><span class="cbtn">▁</span><span class="cbtn">▢</span><span class="cbtn x">✕</span></div>
    <div class="client"><header><div class="avatar"><div><img src="${IC("claude.png")}" alt=""></div></div><div><b>Claude</b><small id="csub"></small></div><span class="status"></span></header>
    <div class="msgs"><div class="m ai" id="greet">…</div><div class="m me">can the background change sometimes, with a grass one too? and use one of my winamp skins</div>
    <div class="m ai">Done. Five real photo scenes now take turns, one per day, swapping at solar midnight. The radio is your own PS2 Winamp skin; try XBOX or Silent Hill.<div><span class="chip">Undo</span><span class="chip">Show me the hill</span></div></div></div>
    <div class="compose"><div class="field">Tell Claude what to change…</div><div class="send">Send</div></div></div>`,
  orb: `<div class="orb"><img src="${IC("orb.svg")}" alt=""></div>`,
  tasks: ["web", "games", "studio", "music", "claude", "folder"].map((k, i) => `<div class="tb${i === 2 || i === 5 ? "" : " run"}"><img src="${IC(k + ".png")}" alt=""></div>`).join(""),
  tray: `<div class="tray"><span class="ti"><img src="${IC("download.png")}" alt=""><span id="note"></span></span><span class="ti"><img src="${IC("volume.png")}" alt="">64%</span><div class="clk"><b id="clk"></b><small id="cdate"></small></div></div>`,
  hatch: `<span class="hatch"><img src="${IC("desktop.png")}" alt="">Classic desktop</span>`,
};
GADGETS.bar = GADGETS.orb + GADGETS.tasks + `<div class="sp"></div>` + GADGETS.tray + GADGETS.hatch;
document.querySelectorAll("[data-g]").forEach(el => el.insertAdjacentHTML("beforeend", el.dataset.g.split(" ").map(g => GADGETS[g]).join("")));
document.getElementById("screen").insertAdjacentHTML("afterbegin", `<div id="scenes"></div>`);
document.getElementById("screen").insertAdjacentHTML("beforeend", `<div id="scrub"><div><b>Time preview</b> · <span id="sread"></span></div>
  <input type="range" id="smin" min="0" max="1439" step="1"><div class="r">date <input type="date" id="sdate"> lat <input type="number" id="slat" step="0.01"> lon <input type="number" id="slon" step="0.01">
  <button id="slive">Live</button><button id="splay">Play day</button><span>[ ] = ±1 h · 1–5 scene, 0 auto</span></div></div>`);

const SETTINGS = { lat: 59.91, lon: 10.75, place: "Oslo" };   // the one location setting

/* SUN-BEGIN: offline sun position (NOAA-style low-precision formulas, about ±1 min for sunrise/sunset) */
const RAD = Math.PI / 180;
function sunAt(date, lat, lon) {
  const n = date.getTime() / 86400000 + 2440587.5 - 2451545.0;          // days since J2000
  const L = (280.460 + 0.9856474 * n) % 360, g = ((357.528 + 0.9856003 * n) % 360) * RAD;
  const lam = (L + 1.915 * Math.sin(g) + 0.020 * Math.sin(2 * g)) * RAD;  // ecliptic longitude
  const eps = (23.439 - 0.0000004 * n) * RAD;
  const dec = Math.asin(Math.sin(eps) * Math.sin(lam));
  const ra = Math.atan2(Math.cos(eps) * Math.sin(lam), Math.cos(lam)) / RAD;
  const gmst = 280.46061837 + 360.98564736629 * n;
  let H = ((gmst + lon - ra) % 360 + 540) % 360 - 180;                     // hour angle, -180..180, 0 = solar noon
  const elev = Math.asin(Math.sin(lat * RAD) * Math.sin(dec) + Math.cos(lat * RAD) * Math.cos(dec) * Math.cos(H * RAD)) / RAD;
  const d = dec / RAD;
  const cosHs = (Math.sin(-0.833 * RAD) - Math.sin(lat * RAD) * Math.sin(dec)) / (Math.cos(lat * RAD) * Math.cos(dec));
  const Hs = Math.acos(Math.max(-1, Math.min(1, cosHs))) / RAD;                 // hour angle of sunset (0 = never rises, 180 = never sets)
  return { elev, H, Hs, maxE: 90 - Math.abs(lat - d), minE: Math.abs(lat + d) - 90 };
}
// Events of the local calendar day: scan in 2-minute steps, interpolate crossings. Missing events stay null (polar day/night).
function dayEvents(day, lat, lon) {
  const t0 = new Date(day.getFullYear(), day.getMonth(), day.getDate()).getTime();
  const ev = { rise: null, set: null, dawn: null, dusk: null, noon: null, samples: [] };
  let prev = null, best = -99;
  for (let m = 0; m <= 1440; m += 2) {
    const t = t0 + m * 60000, e = sunAt(new Date(t), lat, lon).elev;
    ev.samples.push(e);
    if (e > best) { best = e; ev.noon = new Date(t); }
    if (prev) {
      const cross = a => (prev.e - a) * (e - a) <= 0 && prev.e !== e ? new Date(prev.t + (t - prev.t) * (a - prev.e) / (e - prev.e)) : null;
      const up = e > prev.e, r = cross(-0.833), c = cross(-6);
      if (r) up ? (ev.rise ??= r) : (ev.set = r);
      if (c) up ? (ev.dawn ??= c) : (ev.dusk = c);
    }
    prev = { t, e };
  }
  return ev;
}
/* Phase coordinate s on a cycle 0..8: 0 late night, 1 dawn, 2 morning, 3 midday, 4 afternoon, 5 golden hour, 6 dusk, 7 night, 8 = 0.
   Twilight follows absolute sun height, night turns late in the 2 h before solar midnight; day phases are relative to today's highest sun, damped in winter. */
const lerpSeg = (x, x0, x1, y0, y1) => y0 + (y1 - y0) * Math.min(1, Math.max(0, (x - x0) / (x1 - x0)));
function phaseCoord(sun) {
  const { elev: e, maxE, H } = sun;
  const p = maxE > 3 ? Math.min(1, Math.max(0, (e - 3) / (maxE - 3))) * Math.min(1, maxE / 20) : 0;
  const sR = e <= -3 ? lerpSeg(e, -18, -3, 0, 1) : e < 3 ? lerpSeg(e, -3, 3, 1, 1.5) : 1.5 + 1.5 * p;
  const sS = e >= 3 ? 5 - 2 * p : e > -3 ? lerpSeg(e, 3, -3, 5, 6) : e > -12 ? lerpSeg(e, -3, -12, 6, 7) : 7 + lerpSeg(H, 150, 180, 0, 1);   // night turns to late night in the 2 h before solar midnight
  const w = Math.min(1, Math.max(0, 0.5 + Math.sin(H * RAD) * 3));     // 0 = morning side, 1 = evening side; crossfades ±40 min around noon and midnight
  return { sR, sS, w };
}
/* SUN-END */

/* Defaults for PHASES and SCENES: live/theme.json overrides them at start (applyTheme in index.html). Restyle there, not here. */
/*PHASES-BEGIN*/
const PHASES = [
 {"id":"late_night","label":"Late night","base":"dark","scene":{"skyTop":"#030814","skyMid":"#07162B","horizon":"#0C2A3A","water":"#03211F","waterDeep":"#010B0C","hill":"#0B3326","hillDeep":"#041A12","sun":"#FFD7A0","glow":"#3FE0D0","cloud":"#1A2A3A","cloudA":0.35,"stars":1,"bio":1,"caustic":0.04,"bubbles":0.25,"fireflies":0.9,"motion":0.4},"glass":{"rgb":[184,255,224],"a":0.1,"edge":0.3},"tc":{"window":"#020E0A","panel":"#071A17","surface":"#0B2A24","surface_hover":"#103630","surface_active":"#16453D","outline":"#1F5A4F","text":"#DFFBF3","secondary":"#9FD8C8","dim":"#6E9E92","accent":"#3FE0D0","accent_hover":"#66E8DB","on_accent":"#02130F","danger":"#FF6B6B","warning":"#E8FF8A"},"ui":{"select":"studio","greet":"It's late. Sunrise is at {rise}.","track":"Lantern Fish (sleep mix)","note":"Downloads wait for morning"},"grade":{"b":0.22,"s":0.6,"c":1.15,"hue":0,"skyA":0.9,"light":"#0E3A3C","lightA":0.6,"waterA":0.7,"rays":0,"city":0.85}},
 {"id":"dawn","label":"Dawn","base":"light","scene":{"skyTop":"#2B3A78","skyMid":"#B98AC9","horizon":"#FFB4A2","water":"#4E6E9E","waterDeep":"#1E2F55","hill":"#5E8F6A","hillDeep":"#2F5A45","sun":"#FFD1B0","glow":"#FF9FB0","cloud":"#F7C6D0","cloudA":0.85,"stars":0.2,"bio":0.15,"caustic":0.12,"bubbles":0.6,"fireflies":0.15,"motion":0.6},"glass":{"rgb":[255,240,245],"a":0.32,"edge":0.7},"tc":{"window":"#FFF1F0","panel":"#FBE3EA","surface":"#FFFFFF","surface_hover":"#FFF0F3","surface_active":"#F6D6E2","outline":"#E3B7C9","text":"#2A2350","secondary":"#5A4E86","dim":"#8E84B0","accent":"#E86FA0","accent_hover":"#F087B1","on_accent":"#FFFFFF","danger":"#D9455F","warning":"#E89B3C"},"ui":{"select":"music","greet":"Good morning. The sun comes up at {rise}; today has {daylen} of daylight.","track":"First Light","note":"Quiet hours are over"},"grade":{"b":0.78,"s":0.9,"c":1,"hue":0,"skyA":0.7,"light":"#E6A2C6","lightA":0.42,"waterA":0.4,"rays":0.12,"city":0.3}},
 {"id":"morning","label":"Morning","base":"light","scene":{"skyTop":"#4FB8FF","skyMid":"#A8E4FF","horizon":"#F2FBFF","water":"#19A3D1","waterDeep":"#00628F","hill":"#8CE068","hillDeep":"#3AA548","sun":"#FFF6D8","glow":"#FFF2B0","cloud":"#FFFFFF","cloudA":0.9,"stars":0,"bio":0,"caustic":0.3,"bubbles":1,"fireflies":0,"motion":1},"glass":{"rgb":[255,255,255],"a":0.3,"edge":0.75},"tc":{"window":"#EEFAFF","panel":"#DDF4FF","surface":"#FFFFFF","surface_hover":"#EEF9FF","surface_active":"#CBEFFF","outline":"#A3DCF5","text":"#06324A","secondary":"#2C6280","dim":"#6B97B0","accent":"#00B4E6","accent_hover":"#22C2EE","on_accent":"#FFFFFF","danger":"#E5484D","warning":"#F5A524"},"ui":{"select":"web","greet":"Morning. Sunset today is at {set}.","track":"Bubble Dream","note":"2 updates installed overnight"},"grade":{"b":1,"s":1.05,"c":1,"hue":-4,"skyA":0.12,"light":"#FFF4E0","lightA":0.12,"waterA":0.08,"rays":0.35,"city":0}},
 {"id":"midday","label":"Midday","base":"light","scene":{"skyTop":"#2E9BFF","skyMid":"#7FD0FF","horizon":"#E3F7FF","water":"#0096C7","waterDeep":"#004E7A","hill":"#7ED957","hillDeep":"#2E9E3E","sun":"#FFFFFF","glow":"#FFF8D0","cloud":"#FFFFFF","cloudA":0.95,"stars":0,"bio":0,"caustic":0.38,"bubbles":1,"fireflies":0,"motion":1.1},"glass":{"rgb":[255,255,255],"a":0.28,"edge":0.8},"tc":{"window":"#E9FAFF","panel":"#D3F1FF","surface":"#FFFFFF","surface_hover":"#EAF8FF","surface_active":"#C6ECFF","outline":"#9BD8F2","text":"#06324A","secondary":"#2C6280","dim":"#6B97B0","accent":"#00A8E8","accent_hover":"#22B8F0","on_accent":"#FFFFFF","danger":"#E5484D","warning":"#F5A524"},"ui":{"select":"games","greet":"Midday. The sun is as high as it gets today. Sunset at {set}.","track":"Bubble Dream (2003 mix)","note":"Steam · 2.1 GB · 4 min left"},"grade":{"b":1.05,"s":1.12,"c":1.04,"hue":0,"skyA":0,"light":"#FFFFFF","lightA":0,"waterA":0,"rays":0.5,"city":0}},
 {"id":"afternoon","label":"Afternoon","base":"light","scene":{"skyTop":"#4AA8F0","skyMid":"#A6DAF5","horizon":"#F5F1DC","water":"#1792B8","waterDeep":"#075478","hill":"#8BCF55","hillDeep":"#3D8F35","sun":"#FFF3C4","glow":"#FFE59A","cloud":"#FFFBEE","cloudA":0.9,"stars":0,"bio":0,"caustic":0.32,"bubbles":0.9,"fireflies":0,"motion":0.9},"glass":{"rgb":[250,255,248],"a":0.3,"edge":0.75},"tc":{"window":"#F4FAF7","panel":"#E6F3EE","surface":"#FFFFFF","surface_hover":"#F1F9F6","surface_active":"#D3EDE4","outline":"#A9D6C6","text":"#0A3442","secondary":"#2F6474","dim":"#6F98A4","accent":"#19A974","accent_hover":"#2DBB86","on_accent":"#FFFFFF","danger":"#E5484D","warning":"#F5A524"},"ui":{"select":"games","greet":"Afternoon. Golden hour starts at {golden}. Want some music for it?","track":"Lazy Current","note":"Everything is up to date"},"grade":{"b":1,"s":1.06,"c":1.02,"hue":0,"skyA":0.15,"light":"#FFEFC8","lightA":0.2,"waterA":0.1,"rays":0.4,"city":0}},
 {"id":"golden","label":"Golden hour","base":"light","scene":{"skyTop":"#3E6FB8","skyMid":"#F2B36B","horizon":"#FFD98A","water":"#C9844A","waterDeep":"#3E3F6A","hill":"#9AA83E","hillDeep":"#4E6A2A","sun":"#FFE7A0","glow":"#FFB347","cloud":"#FFD0A0","cloudA":0.95,"stars":0,"bio":0,"caustic":0.25,"bubbles":0.7,"fireflies":0.1,"motion":0.7},"glass":{"rgb":[255,236,205],"a":0.32,"edge":0.7},"tc":{"window":"#FFF6E8","panel":"#FCE9CF","surface":"#FFFDF8","surface_hover":"#FFF3E0","surface_active":"#F7DDB5","outline":"#E5BE87","text":"#3A2410","secondary":"#6E4A26","dim":"#9E7E5C","accent":"#FF8A1E","accent_hover":"#FF9E45","on_accent":"#FFFFFF","danger":"#D9452B","warning":"#E8A100"},"ui":{"select":"music","greet":"Golden hour. Sunset at {set}; the light turns amber now.","track":"Amber Lake","note":"Everything is up to date"},"grade":{"b":0.95,"s":1.18,"c":1.05,"hue":-8,"skyA":0.62,"light":"#FFB060","lightA":0.45,"waterA":0.3,"rays":0.3,"city":0.05}},
 {"id":"dusk","label":"Dusk","base":"dark","scene":{"skyTop":"#1C2659","skyMid":"#6A4C9C","horizon":"#F08A6B","water":"#3B3F7A","waterDeep":"#141A3E","hill":"#2F5A4E","hillDeep":"#15302B","sun":"#FF9A7A","glow":"#FF7A8A","cloud":"#B07AA8","cloudA":0.8,"stars":0.35,"bio":0.3,"caustic":0.1,"bubbles":0.5,"fireflies":0.5,"motion":0.6},"glass":{"rgb":[220,200,255],"a":0.16,"edge":0.45},"tc":{"window":"#120F2A","panel":"#1B1840","surface":"#262256","surface_hover":"#2F2A66","surface_active":"#3A347A","outline":"#4A4290","text":"#F3E9FF","secondary":"#C7B5E8","dim":"#9286B8","accent":"#FF8FB1","accent_hover":"#FFA6C1","on_accent":"#1B0F2A","danger":"#FF6B6B","warning":"#FFC46B"},"ui":{"select":"studio","greet":"Dusk. The glass has gone to evening. Sunrise tomorrow is at {rise}.","track":"Violet Shore","note":"1 download queued"},"grade":{"b":0.56,"s":0.85,"c":1.05,"hue":0,"skyA":0.8,"light":"#7A5AB0","lightA":0.52,"waterA":0.55,"rays":0,"city":0.7}},
 {"id":"night","label":"Night","base":"dark","scene":{"skyTop":"#06102A","skyMid":"#10245A","horizon":"#2A3F7A","water":"#0A2A4A","waterDeep":"#031426","hill":"#123A36","hillDeep":"#07201C","sun":"#FFD7A0","glow":"#6FA8FF","cloud":"#2C3E66","cloudA":0.5,"stars":0.85,"bio":0.65,"caustic":0.06,"bubbles":0.35,"fireflies":0.8,"motion":0.5},"glass":{"rgb":[160,190,255],"a":0.13,"edge":0.35},"tc":{"window":"#060E22","panel":"#0B1833","surface":"#12244A","surface_hover":"#1A2F5A","surface_active":"#223C6E","outline":"#2D4A80","text":"#E3ECFF","secondary":"#A9BCE6","dim":"#7489B5","accent":"#6FC3FF","accent_hover":"#8FD2FF","on_accent":"#04112A","danger":"#FF6B7A","warning":"#FFD27A"},"ui":{"select":"games","greet":"Good evening. Sunrise tomorrow is around {rise}.","track":"Moon Pool","note":"3 friends online"},"grade":{"b":0.32,"s":0.7,"c":1.1,"hue":0,"skyA":0.85,"light":"#2A3F80","lightA":0.58,"waterA":0.6,"rays":0,"city":1}}
];
/*PHASES-END*/

const V4 = "assets/";   // photos, icons and Winamp skins (copied from concepts/design/tidepool-v4)
const NIL = document.createElement("div");   // layouts leave out gadgets; writes to a missing id go here
const $ = id => document.getElementById(id) ?? NIL;
const q = new URLSearchParams(location.search), DEV = q.has("dev");   // ?dev: concept tools (state menu, time preview, test data states, self tests)
/* Two screens (Spill): each monitor runs its own page. Screen 2 = the right-hand one: its scenes are the same 360° photos turned one view to the right
   (assets/img2), and the sun's path runs across both. The host sets these from host.screen (index.html setScreen); ?screen=2 does it in a browser. */
// The screens (Host.Arrange, Setup › Screens): SCR 1 = the main screen (the menu), 2 = any other ("a second screen"). ROLE: main | partner (beside the
// main one: clock, Winamp, the row's tail) | extra (the scene only). SLOT: place from the main screen (-1 just left, 1 just right). PSIDE: the partner's side.
// A browser stands in with ?screen=2 (the partner, on the right) or ?screen=3 (an extra); setScreen() takes the host's word once it arrives.
let SCR = +(q.get("screen") ?? 1) >= 2 ? 2 : 1, DUAL = q.has("screen"), ROLE = q.get("screen") === "3" ? "extra" : SCR === 2 ? "partner" : "main", SLOT = SCR === 2 ? +(q.get("screen")) - 1 : 0, PSIDE = DUAL ? 1 : 0, XOFF = 1920 * SLOT;
const SCREEN2 = {
  hill: { img: "assets/img2/hill-r.jpg", mask: "assets/img2/hill-r-sky.png", ff: [120, 760, 1700, 280] },
  meadow: { img: "assets/img2/meadow-r.jpg", mask: "assets/img2/meadow-r-sky.png" },
  valley: { img: "assets/img2/valley-r.jpg", mask: "assets/img2/valley-r-sky.png" },
  shore: { img: "assets/img2/shore-r.jpg", mask: "assets/img2/shore-r-sky.png", water: 540 },
  lake: { flip: true } };   // Lake is one photo with no 360° source: screen 2 shows it mirrored, so the shoreline meets itself at the bezel
const loc = { lat: +(q.get("lat") ?? SETTINGS.lat), lon: +(q.get("lon") ?? SETTINGS.lon), place: q.get("place") ?? (q.has("lat") ? "here" : SETTINGS.place) };
if (window.host?.location) Object.assign(loc, window.host.location);   // the host knows where the PC is (app/BRIDGE.md)
let override = null, evCache = { key: "", ev: null };

const hex = h => [1, 3, 5].map(i => parseInt(h.slice(i, i + 2), 16));
const toHex = c => "#" + c.map(v => Math.round(v).toString(16).padStart(2, "0")).join("");
const INK = ["text", "secondary", "dim", "on_accent"];
function mix(a, b, f) {
  if (typeof a === "number") return a + (b - a) * f;
  if (typeof a === "string") return a[0] === "#" ? toHex(hex(a).map((v, i) => v + (hex(b)[i] - v) * f)) : (f < 0.5 ? a : b);
  if (Array.isArray(a)) return a.map((v, i) => mix(v, b[i], f));
  const o = {}; for (const k in a) o[k] = mix(a[k], b[k], f);
  if (a.base && b.base && a.base !== b.base) for (const k of INK) o.tc[k] = (f < 0.5 ? a : b).tc[k];   // the glass flips frosted/smoked with base; text blended across that flip sat mid-grey on both for an hour
  return o;
}
const atS = s => { const i = Math.floor(s) % 8, f = s - Math.floor(s); return mix(PHASES[i], PHASES[(i + 1) % 8], f); };
const hm = d => d ? d.toTimeString().slice(0, 5) : "none";
const dur = ms => { const m = Math.round(ms / 60000); return m < 60 ? `${m} min` : `${Math.floor(m / 60)} h ${String(m % 60).padStart(2, "0")} min`; };   // "16 min", not "0 h 16 min"

/* ---------- scenes: several real photos, each re-lit by the same phase grade ----------
   sky = clip path of the open sky (traced per photo), mask = optional per-pixel sky mask PNG next to core.js (made by skymask.py; for skies seen through trees), hz = horizon y (sun height reference), water = y where water starts,
   under = the camera is under the waterline (rays, bubbles), title = where the big category title sits in this sky ({x, b} = left, bottom px; default 188, 796): low enough that the ridge takes a bite, never the first letter, ff = firefly box at night [x, y, w, h], city = distant skyline overlay. */
const SCENES = [
 { id: "lake", name: "Lake", where: "Lake McDonald, Montana", img: V4 + "img/hero.jpg", hz: 385, water: 420, under: true, title: { b: 790 },
   sky: "M0,0 L1920,0 L1920,85 L1850,118 L1760,155 L1700,180 L1640,215 L1580,238 L1520,245 L1450,272 L1400,290 L1360,296 L1330,280 L1290,266 L1260,272 L1220,278 L1180,262 L1140,285 L1100,300 L1060,300 L1010,282 L960,267 L930,290 L900,310 L870,300 L830,292 L800,300 L760,318 L700,320 L640,312 L590,292 L540,272 L500,258 L460,272 L410,292 L370,300 L300,290 L230,292 L140,288 L60,272 L0,262 Z" },   // no skyline: a city on a wilderness lake read as a glitch (r6)
{ id: "hill", name: "Hill", where: "Spaichingen, Germany", img: V4 + "img/hill.jpg", hz: 565, ff: [380, 620, 1000, 380],
   mask: "hill-sky.png",
   sky: "M0,0 L1920,0 L1920,0 L1912,0 L1904,0 L1896,0 L1888,0 L1880,0 L1872,0 L1864,0 L1856,0 L1848,0 L1840,0 L1832,0 L1824,0 L1816,0 L1808,0 L1800,0 L1792,0 L1784,0 L1776,0 L1768,0 L1760,0 L1752,0 L1744,0 L1736,0 L1728,0 L1720,0 L1712,0 L1704,0 L1696,0 L1688,0 L1680,0 L1672,0 L1664,0 L1656,0 L1648,0 L1640,0 L1632,0 L1624,0 L1616,2 L1608,4 L1600,15 L1592,15 L1584,15 L1576,15 L1568,15 L1560,30 L1552,30 L1544,31 L1536,114 L1528,115 L1520,115 L1512,126 L1504,175 L1496,178 L1488,196 L1480,199 L1472,204 L1464,248 L1456,339 L1448,578 L1440,578 L1432,578 L1424,576 L1416,573 L1408,573 L1400,573 L1392,573 L1384,573 L1376,573 L1368,571 L1360,569 L1352,569 L1344,569 L1336,569 L1328,568 L1320,568 L1312,567 L1304,566 L1296,566 L1288,566 L1280,566 L1272,566 L1264,566 L1256,566 L1248,565 L1240,567 L1232,567 L1224,567 L1216,574 L1208,574 L1200,574 L1192,563 L1184,562 L1176,562 L1168,563 L1160,564 L1152,564 L1144,564 L1136,563 L1128,563 L1120,563 L1112,563 L1104,562 L1096,562 L1088,561 L1080,561 L1072,560 L1064,560 L1056,559 L1048,559 L1040,560 L1032,560 L1024,560 L1016,560 L1008,560 L1000,560 L992,560 L984,560 L976,560 L968,562 L960,562 L952,562 L944,560 L936,560 L928,560 L920,559 L912,559 L904,562 L896,563 L888,563 L880,563 L872,559 L864,556 L856,555 L848,555 L840,555 L832,555 L824,554 L816,554 L808,553 L800,553 L792,553 L784,553 L776,553 L768,552 L760,552 L752,551 L744,551 L736,551 L728,551 L720,550 L712,551 L704,551 L696,551 L688,551 L680,552 L672,552 L664,553 L656,555 L648,557 L640,558 L632,560 L624,561 L616,564 L608,564 L600,565 L592,565 L584,566 L576,567 L568,567 L560,570 L552,570 L544,571 L536,572 L528,574 L520,574 L512,576 L504,577 L496,580 L488,580 L480,580 L472,580 L464,579 L456,578 L448,578 L440,578 L432,578 L424,579 L416,580 L408,580 L400,580 L392,582 L384,582 L376,583 L368,583 L360,583 L352,583 L344,584 L336,584 L328,586 L320,587 L312,587 L304,587 L296,587 L288,588 L280,588 L272,589 L264,589 L256,595 L248,595 L240,596 L232,596 L224,596 L216,597 L208,599 L200,599 L192,600 L184,601 L176,602 L168,602 L160,602 L152,603 L144,606 L136,607 L128,608 L120,608 L112,608 L104,610 L96,611 L88,611 L80,611 L72,611 L64,609 L56,609 L48,609 L40,607 L32,607 L24,607 L16,606 L8,606 L0,606 Z" },
 { id: "meadow", name: "Meadow", where: "Gauteng, South Africa", img: V4 + "img/meadow.jpg", hz: 770, ff: [380, 800, 1000, 220],
   sky: "M0,0 L1920,0 L1920,548 L1912,548 L1904,549 L1896,552 L1888,553 L1880,553 L1872,553 L1864,553 L1856,553 L1848,559 L1840,561 L1832,562 L1824,563 L1816,584 L1808,584 L1800,584 L1792,584 L1784,584 L1776,584 L1768,584 L1760,584 L1752,586 L1744,588 L1736,598 L1728,605 L1720,706 L1712,706 L1704,706 L1696,704 L1688,701 L1680,701 L1672,704 L1664,708 L1656,714 L1648,714 L1640,716 L1632,716 L1624,716 L1616,718 L1608,721 L1600,736 L1592,739 L1584,743 L1576,745 L1568,747 L1560,747 L1552,747 L1544,748 L1536,748 L1528,748 L1520,748 L1512,749 L1504,750 L1496,751 L1488,751 L1480,751 L1472,749 L1464,746 L1456,746 L1448,746 L1440,747 L1432,752 L1424,757 L1416,768 L1408,768 L1400,775 L1392,776 L1384,776 L1376,777 L1368,777 L1360,777 L1352,781 L1344,785 L1336,786 L1328,787 L1320,787 L1312,786 L1304,787 L1296,787 L1288,790 L1280,791 L1272,791 L1264,791 L1256,794 L1248,794 L1240,794 L1232,794 L1224,794 L1216,792 L1208,790 L1200,790 L1192,790 L1184,791 L1176,793 L1168,794 L1160,795 L1152,795 L1144,795 L1136,795 L1128,789 L1120,789 L1112,787 L1104,784 L1096,779 L1088,779 L1080,779 L1072,793 L1064,795 L1056,796 L1048,805 L1040,806 L1032,806 L1024,805 L1016,805 L1008,805 L1000,805 L992,804 L984,804 L976,804 L968,803 L960,801 L952,801 L944,801 L936,801 L928,800 L920,800 L912,800 L904,796 L896,796 L888,796 L880,798 L872,798 L864,798 L856,798 L848,797 L840,796 L832,796 L824,795 L816,795 L808,795 L800,795 L792,795 L784,795 L776,794 L768,793 L760,792 L752,792 L744,792 L736,792 L728,791 L720,789 L712,789 L704,789 L696,789 L688,789 L680,788 L672,788 L664,788 L656,786 L648,786 L640,783 L632,780 L624,780 L616,780 L608,780 L600,780 L592,780 L584,780 L576,784 L568,786 L560,786 L552,786 L544,786 L536,786 L528,783 L520,780 L512,780 L504,780 L496,781 L488,781 L480,781 L472,781 L464,776 L456,776 L448,776 L440,776 L432,778 L424,778 L416,778 L408,776 L400,776 L392,776 L384,778 L376,779 L368,779 L360,779 L352,779 L344,773 L336,773 L328,715 L320,696 L312,696 L304,696 L296,696 L288,724 L280,724 L272,727 L264,773 L256,774 L248,774 L240,774 L232,773 L224,774 L216,774 L208,774 L200,775 L192,775 L184,775 L176,773 L168,773 L160,773 L152,771 L144,771 L136,771 L128,771 L120,771 L112,772 L104,774 L96,774 L88,774 L80,771 L72,767 L64,767 L56,767 L48,769 L40,772 L32,772 L24,772 L16,768 L8,768 L0,768 Z" },
 { id: "shore", name: "Shore", where: "Free State, South Africa", img: V4 + "img/shore.jpg", hz: 500, water: 566, title: { x: 470 }, glint: { x: 1056, top: 557.5 },   // the sun's shine, lifted out of the photo (blender/glint.py): it moves under the live sun (render)
   sky: "M0,0 L1920,0 L1920,488 L1912,488 L1904,488 L1896,488 L1888,488 L1880,488 L1872,488 L1864,488 L1856,488 L1848,488 L1840,488 L1832,488 L1824,488 L1816,488 L1808,488 L1800,488 L1792,488 L1784,488 L1776,488 L1768,488 L1760,488 L1752,488 L1744,488 L1736,488 L1728,488 L1720,488 L1712,488 L1704,488 L1696,488 L1688,488 L1680,488 L1672,488 L1664,488 L1656,488 L1648,488 L1640,488 L1632,488 L1624,488 L1616,488 L1608,488 L1600,488 L1592,488 L1584,488 L1576,488 L1568,488 L1560,488 L1552,488 L1544,488 L1536,488 L1528,488 L1520,488 L1512,488 L1504,488 L1496,488 L1488,488 L1480,488 L1472,488 L1464,488 L1456,488 L1448,488 L1440,488 L1432,488 L1424,488 L1416,488 L1408,488 L1400,488 L1392,488 L1384,488 L1376,488 L1368,488 L1360,488 L1352,488 L1344,488 L1336,488 L1328,488 L1320,488 L1312,488 L1304,488 L1296,488 L1288,488 L1280,488 L1272,488 L1264,488 L1256,488 L1248,488 L1240,488 L1232,488 L1224,488 L1216,488 L1208,488 L1200,488 L1192,488 L1184,488 L1176,488 L1168,488 L1160,488 L1152,488 L1144,488 L1136,488 L1128,488 L1120,488 L1112,488 L1104,488 L1096,488 L1088,488 L1080,488 L1072,488 L1064,488 L1056,488 L1048,488 L1040,488 L1032,488 L1024,488 L1016,488 L1008,488 L1000,488 L992,488 L984,488 L976,488 L968,488 L960,488 L952,488 L944,488 L936,488 L928,488 L920,488 L912,488 L904,488 L896,488 L888,488 L880,488 L872,488 L864,488 L856,488 L848,488 L840,488 L832,488 L824,488 L816,488 L808,458 L800,457 L792,449 L784,449 L776,430 L768,427 L760,425 L752,421 L744,416 L736,416 L728,412 L720,411 L712,389 L704,388 L696,384 L688,379 L680,369 L672,368 L664,368 L656,366 L648,363 L640,364 L632,366 L624,364 L616,364 L608,364 L600,358 L592,358 L584,358 L576,358 L568,364 L560,364 L552,357 L544,353 L536,353 L528,353 L520,348 L512,347 L504,345 L496,327 L488,325 L480,317 L472,313 L464,310 L456,303 L448,298 L440,258 L432,253 L424,252 L416,247 L408,234 L400,219 L392,208 L384,203 L376,203 L368,200 L360,198 L352,181 L344,179 L336,171 L328,165 L320,155 L312,149 L304,149 L296,146 L288,141 L280,101 L272,92 L264,87 L256,86 L248,79 L240,67 L232,63 L224,55 L216,48 L208,48 L200,32 L192,23 L184,19 L176,13 L168,7 L160,0 L152,0 L144,0 L136,0 L128,0 L120,0 L112,0 L104,0 L96,0 L88,0 L80,0 L72,0 L64,0 L56,0 L48,0 L40,0 L32,0 L24,0 L16,0 L8,0 L0,0 Z" },
 { id: "valley", name: "Valley", where: "Graubünden, Switzerland", img: V4 + "img/valley.jpg", hz: 575, ff: [380, 640, 1000, 380],
   sky: "M0,0 L1920,0 L1920,330 L1840,350 L1720,380 L1600,410 L1480,450 L1400,490 L1340,530 L1290,564 L1240,550 L1180,544 L1140,526 L1110,536 L1040,550 L980,564 L930,584 L860,570 L760,544 L660,540 L600,530 L520,500 L400,420 L240,330 L120,250 L40,210 L0,176 Z" },
];
const ROT = { mode: (q.get("rotate") ?? (() => { try { return localStorage.getItem("tp-rotate"); } catch { return null; } })()) === "4h" ? "4h" : "day", pin: null };   // Settings › Scenes   // day = new scene at solar midnight; 4h = every 4 solar hours
let pinId = q.get("scene"); try { pinId ??= localStorage.getItem("tp-scene"); } catch {}
ROT.pin = SCENES.findIndex(S => S.id === pinId); if (ROT.pin < 0) ROT.pin = null;
/* Sky edge: the traced clip path, or a per-pixel mask image where sky shows through trees. */
const CORE = new URL(".", document.currentScript.src).href;
const skyEdge = S => S.mask ? `-webkit-mask:url('${CORE + S.mask}') 0 0/1920px 1080px;mask:url('${CORE + S.mask}') 0 0/1920px 1080px` : `clip-path:path('${S.sky}')`;
/* Horizon visualizer (horizon.js), and since 15 Nov the sky layer too (the traced paths ran into the land; light showed it): a tight sky mask per photo (assets/hviz/<id>[-r].png, made from the photos by concepts/horizon/hvizmask.py, which also
   prints these baselines: the light's foot, just under the lowest ridge point in the middle of the view). */
const HVZ = { lake: 327, "lake-r": 327, hill: 612, meadow: 808, shore: 500, valley: 587, "hill-r": 805, "meadow-r": 716, "valley-r": 365, "shore-r": 566 };
const HRX = { shore: 820 }, HRY = { shore: 580, "shore-r": 566 };   // the reflection's open water: from x (shore: trees and rocks to the left), from y (below the far hills)
const hvizEdge = k => `-webkit-mask:url('${CORE}assets/hviz/${k}.png') 0 0/1920px 1080px;mask:url('${CORE}assets/hviz/${k}.png') 0 0/1920px 1080px`;
function sceneHTML(S) {
  if (SCR === 2) S = { ...S, title: null, city: null, under: false, ...(SCREEN2[S.id] ?? {}) };
  if (S.flip) S = { ...S, sky: S.sky.replace(/(\d+),(\d+)/g, (m, x, y) => `${1920 - x},${y}`) };
  const bub = [[560, 34, 0, 16], [930, 20, -6, 16], [1180, 48, -10, 20], [760, 14, -3, 12], [1300, 24, -13, 16]]
    .map(([x, d, de, du]) => `<div class="bubble" style="left:${x}px;top:${1040 + d % 30}px;width:${d}px;height:${d}px;animation-delay:${de}s;animation-duration:${du}s"></div>`).join("");
  const hk = S.id + (SCR === 2 ? "-r" : ""), vz = HVZ[hk] ?? S.hz + 14;   // the horizon visualizer's own mask and baseline for this photo
  const ff = S.ff ? Array.from({ length: 22 }, (_, i) => `<i style="left:${S.ff[0] + (i * 397) % S.ff[2]}px;top:${S.ff[1] + (i * 211) % S.ff[3]}px;animation-delay:${-i * 1.3}s;animation-duration:${7 + i % 5}s"></i>`).join("") : "";
  return `<div class="scn" data-id="${S.id}">
  <div class="photo"><img class="hero" src="${S.img}"${S.flip ? ' style="transform:scaleX(-1)"' : ""} alt="">${S.glint ? `<div class="glint" style="-webkit-mask:url('${CORE}assets/glint/${hk}-water.png') 0 0/1920px 1080px;mask:url('${CORE}assets/glint/${hk}-water.png') 0 0/1920px 1080px"><img src="${CORE}assets/glint/${S.id}-glint.png" style="top:${S.glint.top}px" alt=""></div>` : ""}${S.city ? `<img class="city" src="${V4}img/skyline.png" style="left:${S.city.x}px;top:${S.city.y}px" alt="">` : ""}</div>
  <div class="sky" style="${HVZ[hk] ? hvizEdge(hk) : skyEdge(S)}"><div class="skydark"></div><div class="skytint"></div><div class="stars" style="height:${Math.max(S.hz, vz) + 20}px"></div><div class="sunglow"></div><div class="moon"></div><div class="sun"></div><div class="skyt" style="${S.title?.x ? `left:${S.title.x}px;` : ""}${S.title?.b ? `bottom:${S.title.b}px` : ""}"></div></div>
  <div class="hviz" style="${hvizEdge(hk)}"><canvas width="480" height="160" style="bottom:${1080 - vz}px;height:${Math.round(Math.max(280, vz * .6))}px"></canvas></div>${S.water != null ? `<div class="hrefl" data-x0="${HRX[hk] ?? 0}" style="top:${HRY[hk] ?? Math.max(vz, S.hz) + 4}px"><canvas width="480" height="40" style="height:90px"></canvas></div>` : ""}
  <div class="light"></div>
  ${S.city ? `<div class="citylights" style="left:${S.city.x}px;top:${S.city.y}px"></div>` + S.city.masts.map(([x, y], i) => `<div class="mast" style="left:${x}px;top:${y}px;animation-delay:${-i * .8}s"></div>`).join("") : ""}
  ${S.water != null ? `<div class="watertint" style="top:${S.water}px"></div><div class="bio" style="top:${S.water + (S.under ? 100 : 10)}px"></div>` : ""}
  ${S.under ? `<div class="rays" style="top:${S.water + 50}px"></div>` + bub : ""}
  ${S.ff ? `<div class="fireflies">${ff}</div>` : ""}</div>`;
}
$("scenes").innerHTML = SCENES.map(sceneHTML).join("");
$("srow").innerHTML = SCENES.map((S, i) => `<div class="st" data-i="${i}" title="${S.name}" style="background-image:url(${S.img})"></div>`).join("") + `<span class="auto" id="sauto">Auto</span>`;
const pinScene = i => { ROT.pin = i; try { i == null ? localStorage.removeItem("tp-scene") : localStorage.setItem("tp-scene", SCENES[i].id); } catch {} render(); };
document.querySelectorAll("#srow .st").forEach(el => el.onclick = () => pinScene(+el.dataset.i));
$("sauto").onclick = () => pinScene(null);
function sceneAt(now) {
  const solarH = now.getTime() / 3600000 + loc.lon / 15, step = ROT.mode === "4h" ? 4 : 24, slot = Math.floor(solarH / step);
  const auto = ((slot % SCENES.length) + SCENES.length) % SCENES.length;
  return { i: ROT.pin ?? auto, auto, next: (auto + 1) % SCENES.length, at: new Date(((slot + 1) * step - loc.lon / 15) * 3600000) };
}

/* ---------- music gadget: a Winamp 2 main window drawn from the skin's own bitmaps (standard Winamp sprite map) ---------- */
const WA = { id: null, t0: Date.now() - 83000, track: "" };
const FONT = {};
'abcdefghijklmnopqrstuvwxyz"@'.split("").forEach((c, i) => FONT[c] = [i, 0]); FONT[" "] = [30, 0];
"0123456789….:()-'!_+\\/[]^&%,=$#".split("").forEach((c, i) => FONT[c] = [i, 1]);
"åöä?*".split("").forEach((c, i) => FONT[c] = [i, 2]);
function waSprite(file, sx, sy, w, h, x, y, parent = $("wa")) {
  const i = document.createElement("i");
  i.style.cssText = `left:${x}px;top:${y}px;width:${w}px;height:${h}px;background-image:url(${V4}skins/${WA.id}/${file});background-position:${-sx}px ${-sy}px`;
  parent.appendChild(i); return i;
}
const waText = (str, x, y, parent) => [...str.toLowerCase()].forEach((c, k) => { const [cx, cy] = FONT[c] ?? FONT[" "]; waSprite("text.png", cx * 5, cy * 6, 5, 6, x + k * 5, y, parent); });
/* region.txt: a shaped skin lists the polygons Winamp keeps ([Normal] NumPoints + PointList); everything else is cut away (else magenta corners show) */
function regionPath(t, part = "normal") {   // region.txt's [Normal] (the big window) or [WindowShade] (collapsed) as a clip-path
  const sec = t.split(/^\s*\[/m).find(x => x.toLowerCase().startsWith(part + "]")) ?? "";
  const n = (sec.match(/NumPoints\s*=\s*([\d,\s]+)/i)?.[1] ?? "").split(",").map(Number).filter(x => x > 0);
  const i = sec.search(/PointList\s*=/i), p = i < 0 ? [] : (sec.slice(i).replace(/^\s*;.*$/mg, "").replace(/PointList\s*=/i, "").match(/-?\d+/g) ?? []).map(Number);
  if (!n.length || p.length < 6) return "";
  let d = "", k = 0;
  for (const c of n) { const pts = []; for (let j = 0; j < c && k + 1 < p.length; j++, k += 2) pts.push(p[k] + " " + p[k + 1]); if (pts.length > 2) d += "M" + pts.join("L") + "Z"; }
  return d ? `path("${d}")` : "";
}
function buildWinamp(id) {
  WA.id = id; const S = SKINS[id]; $("wa").innerHTML = ""; WA.track = ""; $("wa").style.clipPath = $("shade").style.clipPath = "";
  fetch(`${V4}skins/${id}/region.txt`).then(r => r.ok ? r.text() : "").then(t => { if (WA.id === id) { $("wa").style.clipPath = regionPath(t); $("shade").style.clipPath = regionPath(t, "windowshade"); } }).catch(() => {});
  waSprite("main.png", 0, 0, 275, 116, 0, 0);
  waSprite("titlebar.png", 27, 0, 275, 14, 0, 0);                                        // active title bar
  [[0, 0, 6, "menu", "Next skin"], [9, 0, 244, "min", "Collapse"], [0, 18, 254, "shade", "Collapse (F4)"], [18, 0, 264, "close", "Stop"]].forEach(([sx, sy, x, k, tip]) => {
    const b = waSprite("titlebar.png", sx, sy, 9, 9, x, 3); b.dataset.wt = k; b.title = tip; b.style.cursor = "pointer"; });   // Winamp's own title-bar buttons (waDo)
  waSprite("titlebar.png", 304, 0, 8, 43, 10, 22);                                       // clutter bar
  waSprite("playpaus.png", 0, 0, 9, 9, 26, 28); waSprite("playpaus.png", 39, 0, 3, 9, 24, 28);
  waSprite("monoster.png", 29, 12, 27, 12, 212, 41); waSprite("monoster.png", 0, 0, 29, 12, 239, 41);
  waText("320", 111, 43); waText("44", 156, 43);
  WA.digits = [48, 60, 78, 90].map(x => waSprite(S.nums, 0, 0, 9, 13, x, 26));
  WA.volBg = waSprite("volume.png", 0, 0, 68, 13, 107, 57); WA.volKnob = waSprite("volume.png", 15, 422, 14, 11, 107, 58);   // the system volume (host.sys); drag or click it
  WA.volBg.dataset.wt = WA.volKnob.dataset.wt = "vol"; WA.volBg.style.cursor = WA.volKnob.style.cursor = "pointer"; waVol(WA.vol ?? .64);
  window.host?.sys?.("vol")?.then(r => { if (r) waVol(r.vol / 100); });
  waSprite(S.balance, 9, 0, 38, 13, 177, 57); waSprite(S.balance, 15, 422, 14, 11, 189, 58);
  waSprite("shufrep.png", 0, 61, 23, 12, 219, 58); waSprite("shufrep.png", 23, 61, 23, 12, 242, 58);
  WA.pos = waSprite("posbar.png", 0, 0, 248, 10, 16, 72); WA.thumb = waSprite("posbar.png", 248, 0, 29, 10, 16, 72);
  [[0, 23, 16], [23, 23, 39], [46, 23, 62], [69, 23, 85], [92, 22, 108]].forEach(([sx, w, x], i) => { const b = waSprite("cbuttons.png", sx, 0, w, 18, x, 88); b.dataset.wb = i; b.style.cursor = "pointer"; });   // prev, play, pause, stop, next: index.html waButton
  const ej = waSprite("cbuttons.png", 114, 0, 22, 16, 136, 89); ej.dataset.wt = "eject"; ej.title = "The Music tab"; ej.style.cursor = "pointer";
  waSprite("shufrep.png", 28, 0, 47, 15, 164, 89); waSprite("shufrep.png", 0, 30, 28, 15, 210, 89);   // shuffle off, repeat on
  WA.mq = document.createElement("div"); WA.mq.style.cssText = "position:absolute;left:111px;top:27px;width:154px;height:6px;overflow:hidden"; $("wa").appendChild(WA.mq);
  const c = document.createElement("canvas"); c.width = 76; c.height = 16; $("wa").appendChild(c); WA.vis = c.getContext("2d");
  document.querySelectorAll("#waskins button").forEach(b => b.classList.toggle("on", b.dataset.s === id));
  try { localStorage.setItem("tp-skin", id); } catch {}
  if (WA.lastTrack) tickWinamp(WA.lastTrack);
}
function waTime() {   // the host's position (pos at `at`, counting on while playing); a player that says none: no made-up time (known false)
  const np = window.host?.nowPlaying, real = !!window.host, known = real && np?.dur > 0;
  const len = known ? np.dur : 221, el = known ? Math.min(len, np.playing ? np.pos + (Date.now() - np.at) / 1000 : np.pos) : ((Date.now() - WA.t0) / 1000) % len;
  return { np, real, known, len, el };
}
function waVol(v) { WA.vol = v = Math.max(0, Math.min(1, v)); if (!WA.volBg) return; WA.volBg.style.backgroundPosition = `0 ${-Math.round(v * 27) * 15}px`; WA.volKnob.style.left = 107 + Math.round(v * 54) + "px"; }
function tickWinamp(track) {
  WA.lastTrack = track;
  const { np, real, known, len, el } = waTime(), mm = Math.floor(el / 60), ss = Math.floor(el % 60);
  [Math.floor(mm / 10) % 10, mm % 10, Math.floor(ss / 10), ss % 10].forEach((d, i) => { WA.digits[i].style.backgroundPosition = `${-d * 9}px 0`; WA.digits[i].style.visibility = real && !known ? "hidden" : ""; });
  WA.thumb.style.left = (real && !known ? 16 : 16 + Math.round(el / len * 219)) + "px";
  track = known ? `${track} (${Math.floor(len / 60)}:${String(Math.floor(len % 60)).padStart(2, "0")})` : track;   // Winamp's own title line ends with the song's length
  if (WA.track !== track) {
    WA.track = track; WA.mq.innerHTML = ""; WA.mqText = real ? `${np?.app ? np.app[0].toUpperCase() + np.app.slice(1) + " - " : ""}${track}  ***  ` : `1. Spotifast - ${track} (3:41)  ***  `; WA.mqPos = 0;
    WA.mqIn = document.createElement("div"); WA.mqIn.style.cssText = "position:absolute;left:0;top:0;height:6px"; WA.mq.appendChild(WA.mqIn);
    waText(WA.mqText + WA.mqText, 0, 0, WA.mqIn);
  }
}
// Winamp only draws while it is on screen: not when covered (game, stock Windows), collapsed, or with nothing playing (perf.md)
const waShown = () => WA.vis && WA.vis.canvas.offsetParent && !document.hidden && !$("screen").matches(".covered,.frozen")
  && !/ov-(switcher|search|hatch|welcome)/.test($("ui").className);   // under the full-screen dim its redraws would re-blur the whole screen
const waShadeShown = () => WA.shMqIn?.isConnected && $("shade").offsetParent && !document.hidden && !$("screen").matches(".covered,.frozen");
setInterval(() => {   // transform: no layout
  const big = WA.mqIn && waShown(), small = waShadeShown(); if (!big && !small) return;
  WA.mqPos = (WA.mqPos + 5) % (WA.mqText.length * 5);
  if (big) WA.mqIn.style.transform = `translateX(${-WA.mqPos}px)`; if (small) WA.shMqIn.style.transform = `translateX(${-WA.mqPos}px)`;
}, 220);
(function vis() {                                            // spectrum bars in the skin's own viscolor.txt colours
  const g = WA.vis, V = SKINS[WA.id]?.vis, t = performance.now() / 1000;
  if (g && V && waShown() && WA.visOff) { g.fillStyle = V[0]; g.fillRect(0, 0, 76, 16); }   // Settings › Winamp › Visualizer off
  else if (g && V && waShown()) {
    g.fillStyle = V[0]; g.fillRect(0, 0, 76, 16);
    const HB = window.Horizon, real = !!window.host;   // with the host: what the PC plays (Spectrum.cs via horizon.js), flat in silence; a plain browser keeps the made-up bars
    for (let b = 0; b < 19; b++) {
      const h = real ? (HB?.fresh() ? Math.round(Math.min(16, HB.bands[Math.round(b * 31 / 18)] * 16)) : 0)
        : Math.round(Math.max(1, Math.min(16, (0.55 + 0.45 * Math.sin(t * 2.1 + b * 0.8)) * (0.5 + 0.5 * Math.abs(Math.sin(t * 5.3 + b * 2.7))) * (17 - b * 0.35))));
      for (let y = 16 - h; y < 16; y++) { g.fillStyle = V[2 + y] ?? V[V.length - 1]; g.fillRect(b * 4, y, 3, 1); }
    }
  }
  setTimeout(() => requestAnimationFrame(vis), 100);   // 10 fps (perf.md: was 25). Each redraw re-blurs every glass panel on screen, so it is the idle GPU cost
})();
$("waskins").innerHTML = Object.keys(SKINS).map(k => `<button data-s="${k}" title="${SKINS[k].name}">${SKINS[k].label ?? k.toUpperCase()}</button>`).join("");   // labels come from skins.js (tools/skin-hunt/install.ps1)
document.querySelectorAll("#waskins button").forEach(b => b.onclick = () => buildWinamp(b.dataset.s));
let skin0 = q.get("skin"); try { skin0 ??= localStorage.getItem("tp-skin"); } catch {}
buildWinamp(SKINS[skin0] ? skin0 : SKINS.aero ? "aero" : Object.keys(SKINS)[0]);

/* Where the sun (or moon, at hour angle H) sits in scene S on this screen: x runs sunrise -> sunset across the open middle (two screens: sunrise on the
   main screen, noon just past the bezel, sunset on screen 2), y = 7 px per degree above the photo's horizon. */
function sunPos(S, H, elev, Hs) {
  const f = H / (Hs > 5 && Hs < 175 ? Hs : 90);
  const x = DUAL ? Math.max(250, Math.min(3640, 2200 + f * 1500)) - XOFF : Math.max(430, Math.min(1340, 880 + f * 480));
  return [x, S.hz - 55 - elev * 7];
}

function render() {
  const now = override ?? new Date();
  const sun = sunAt(now, loc.lat, loc.lon);
  const key = now.toDateString() + loc.lat + loc.lon;
  if (evCache.key !== key) evCache = { key, ev: dayEvents(now, loc.lat, loc.lon) };
  const ev = evCache.ev;
  const { sR, sS, w } = phaseCoord(sun);
  const look = mix(atS(sR), atS(sS), w);
  const s = w > 0.5 ? sS : sR, ph = PHASES[Math.round(s) % 8];

  // photo grade + palette -> CSS variables
  const st = $("screen").style, g = look.grade, rc = render.c ??= {};
  const r = { setProperty: (k, v) => { v = String(v); if (rc[k] !== v) { rc[k] = v; st.setProperty(k, v); } } };   // write a variable only when it changed: every write restyles the whole screen
  r.setProperty("--gb", g.b); r.setProperty("--gs", g.s); r.setProperty("--gc", g.c); r.setProperty("--gh", g.hue + "deg");
  for (const k of ["skyA", "lightA", "waterA", "rays", "city"]) r.setProperty("--" + k, g[k]);
  r.setProperty("--light", g.light);
  for (const k of ["skyTop", "skyMid", "horizon", "water", "waterDeep", "glow", "stars", "bio"]) r.setProperty("--" + k, look.scene[k]);
  r.setProperty("--lamp", Math.max(0, 1 - g.b) * 0.9);
  for (const [k, v] of Object.entries(look.tc)) r.setProperty("--" + k, v);

  // scene: which photo is up (crossfade), and the scenes gadget
  const sc = sceneAt(now), cur = SCENES[sc.i];
  document.querySelectorAll(".scn").forEach((el, i) => el.classList.toggle("on", i === sc.i));
  $("sframe").style.backgroundImage = `url(${cur.img})`; $("sname").textContent = cur.name + " · " + cur.where;
  $("snext").textContent = ROT.pin != null ? "Pinned · press Auto to rotate" : `Next: ${SCENES[sc.next].name} at ${hm(sc.at)}`;
  document.querySelectorAll("#srow .st").forEach((el, i) => { el.classList.toggle("on", i === sc.i); el.classList.toggle("pin", i === ROT.pin); });
  $("sauto").classList.toggle("on", ROT.pin == null);
  r.setProperty("--ff", look.scene.fireflies);
  // ambient layers that are invisible at this phase are taken out, so nothing ticks for them (perf.md: idle cost)
  const qc = $("scenes").classList;
  qc.toggle("q-ff", look.scene.fireflies < .03); qc.toggle("q-bio", look.scene.bio < .03); qc.toggle("q-rays", g.rays < .03); qc.toggle("q-city", g.city < .03);

  // sun and moon (sunPos; the idle arc in index.html uses the same mapping)
  const mo0 = window.Orb ? Orb.moonAt(now, loc.lat, loc.lon) : { H: ((sun.H + 360) % 360) - 180, elev: -sun.elev * 0.8, Hs: sun.Hs };   // the real moon (orb.js); no Orb: the old stand-in, opposite the sun
  document.querySelectorAll(".scn").forEach((el, i) => {
    const place = (e2, H, e, Hs = sun.Hs) => { const [x, y] = sunPos(SCENES[i], H, e, Hs); e2.style.left = x + "px"; e2.style.top = y + "px"; };
    const [sn, gl, mo] = [".sun", ".sunglow", ".moon"].map(c => el.querySelector(c));
    place(sn, sun.H, sun.elev); place(gl, sun.H, sun.elev); place(mo, mo0.H, mo0.elev, mo0.Hs);   // x runs moonrise -> moonset like the sun's day
    sn.style.opacity = sun.elev > -1 ? 1 : 0;
    gl.style.opacity = Math.max(0, Math.min(0.9, (sun.elev + 8) / 12)) * (sun.elev > 25 ? 0.3 : 1);
    mo.style.opacity = Math.max(0, Math.min(1, (-sun.elev - 2) / 6)) * Math.max(0, Math.min(1, (mo0.elev + 1) / 4));   // dark sky and the moon above the horizon
    let lit = 1;
    if (window.Orb) {   // the moon's real phase (orb.js, mean synodic month), so the sky and the orb agree. ponytail: an inset shadow, not a true terminator ellipse
      const age = Orb.moonAge(now); lit = (1 - Math.cos(age * 2 * Math.PI)) / 2; const d = Math.round(56 * (1 - lit)) * (age < .5 ? 1 : -1);
      mo.style.boxShadow = `inset ${d}px 0 0 0 rgba(6,12,28,.94),0 0 40px 12px rgba(200,220,255,${(.3 * lit).toFixed(2)})`;
      mo.style.opacity = (+mo.style.opacity * Math.min(1, lit * 5)).toFixed(3);   // a thin crescent fades out toward new moon, like the real one (a dark disc read as a hole in the sky)
    }
    const gt = SCENES[i].glint && el.querySelector(".glint img");   // the shine on the water sits under whichever light is up: sun by day, a faint moon path at night
    if (gt) { const day = sun.elev > -1, src = day ? sn : mo;
      gt.style.transform = `translateX(${(parseFloat(src.style.left) - SCENES[i].glint.x).toFixed(0)}px)`;
      const low = day ? Math.max(0, Math.min(1, (12 - sun.elev) / 12)) : 0;   // a low sun lays the brightest path, and a golden one (it faded out here before: invisible by the time the sun reached screen 2)
      gt.parentNode.style.opacity = day ? Math.min(1, (sun.elev + 1) / 1.5).toFixed(2) : (+mo.style.opacity * (.15 + .3 * lit)).toFixed(2);   // moonlight: a full moon lays a real path, a crescent only a hint
      gt.parentNode.style.filter = low ? `brightness(${(1 + .9 * low).toFixed(2)}) sepia(${(.45 * low).toFixed(2)}) saturate(${(1 + low).toFixed(2)})` : ""; }
  });

  // clock: hands + day/night sub-dial from today's real sun
  const h = now.getHours(), m = now.getMinutes(), sec = now.getSeconds();
  $("hh").style.transform = `rotate(${(h % 12) * 30 + m / 2}deg)`; $("hm").style.transform = `rotate(${m * 6}deg)`; $("hs").style.transform = `rotate(${sec * 6}deg)`;
  const col = e => e > 0 ? "#FFD75E" : e > -6 ? "#F28C6B" : e > -12 ? "#5A5FA8" : "#141A3A";
  const stops = ev.samples.filter((_, i) => i % 5 === 0).map((e, i, a) => `${col(e)} ${(i / a.length * 100).toFixed(1)}% ${((i + 1) / a.length * 100).toFixed(1)}%`);
  $("ring").style.background = `conic-gradient(${stops.join(",")})`;
  $("ptr").style.transform = `rotate(${(h * 60 + m) / 4}deg)`;

  // text
  const golden = ev.set ? new Date(ev.set.getTime() - 50 * 60000) : null;
  const fill = t => t.replace("{rise}", hm(ev.rise)).replace("{set}", hm(ev.set)).replace("{golden}", hm(golden))
                     .replace("{daylen}", ev.rise && ev.set ? dur(ev.set - ev.rise) : (sun.maxE > 0 ? "24 h" : "no"));
  $("phname").textContent = ph.label;
  $("place").textContent = loc.place;
  $("rise").textContent = "↑ " + hm(ev.rise); $("set").textContent = "↓ " + hm(ev.set);
  $("dlen").textContent = ev.rise && ev.set ? dur(ev.set - ev.rise) : (sun.maxE > 0 ? "sun all day" : "sun stays down");
  $("elev").textContent = "sun " + sun.elev.toFixed(1) + "°";
  const upcoming = [["First light", ev.dawn], ["Sunrise", ev.rise], ["Sunset", ev.set], ["Nightfall", ev.dusk]].filter(([, t]) => t && t > now);   // civil twilight (-6°): not "Dawn"/"Dusk", which the phase tag already shows by then ("Dusk · Oslo" next to "Dusk in 29 min")
  if (!upcoming.length) {   // after dusk: name tomorrow's sunrise instead of a vague "night"
    const t = new Date(now.getTime() + 86400000), k2 = t.toDateString() + loc.lat + loc.lon;
    if (evCache.k2 !== k2) { evCache.k2 = k2; evCache.ev2 = dayEvents(t, loc.lat, loc.lon); }
  }
  $("nextev").textContent = upcoming.length ? `${upcoming[0][0]} in ${dur(upcoming[0][1] - now)}` : evCache.ev2.rise ? `Sunrise at ${hm(evCache.ev2.rise)}` : "No sunrise tomorrow";
  $("greet").textContent = fill(ph.ui.greet);
  tickWinamp(ph.ui.track);
  $("note").textContent = fill(ph.ui.note);
  $("csub").textContent = ph.label + " · lit by the real sun";
  $("clk").textContent = hm(now); $("cdate").textContent = now.toDateString().slice(0, 10);
  const labels = { music: ["Music", "Spotifast · playlists for the " + ph.label.toLowerCase()], studio: ["Studio", "FL Studio · last project: night drive v4.flp"],
    games: ["Games", "Steam · Battle.net · Epic · Emulators · Skyrim (modded)"], web: ["Web", "Firefox · news, mail, YouTube"], claude: ["Claude", "Ask for any change"] };
  document.querySelectorAll("[data-k]").forEach(t => t.classList.toggle("sel", t.dataset.k === ph.ui.select));
  $("capt").textContent = labels[ph.ui.select][0]; $("capsub").textContent = labels[ph.ui.select][1];
  $("trk").textContent = ph.ui.track; $("phase").textContent = ph.label;
  window.onRender?.({ now, sun, ev, ph, sel: ph.ui.select });
  $("sread").textContent = `${now.toDateString()} ${hm(now)} · ${ph.label} · sun ${sun.elev.toFixed(1)}° · s ${sR.toFixed(2)}/${sS.toFixed(2)} w ${w.toFixed(2)}`;
  if (!override) $("smin").value = h * 60 + m;
}
// static bits: gauge needles (mock values)
$("n1").style.transform = "rotate(" + (-135 + 0.34 * 270) + "deg)"; $("n2").style.transform = "rotate(" + (-135 + 0.61 * 270) + "deg)";

// ---------- preview scrubber ----------
function setOverride(dateStr, minutes) { const [y, mo, d] = dateStr.split("-").map(Number); override = new Date(y, mo - 1, d, 0, minutes); render(); }
const today = () => { const d = override ?? new Date(); return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, "0")}-${String(d.getDate()).padStart(2, "0")}`; };
$("sdate").value = q.get("date") ?? today(); $("slat").value = loc.lat; $("slon").value = loc.lon;
$("smin").oninput = () => setOverride($("sdate").value, +$("smin").value);
$("sdate").onchange = () => setOverride($("sdate").value, +$("smin").value);
$("slat").onchange = $("slon").onchange = () => { loc.lat = +$("slat").value; loc.lon = +$("slon").value; loc.place = "here"; render(); };
let play = null;
$("slive").onclick = () => { override = null; clearInterval(play); render(); };
const startPlay = () => { clearInterval(play); let mm = 0; play = setInterval(() => { mm = (mm + 3) % 1440; $("smin").value = mm; setOverride($("sdate").value, mm); }, 100); };
$("splay").onclick = startPlay;
if (DEV) addEventListener("keydown", e => {
  if (e.key === "t" || e.key === "T") $("scrub").classList.toggle("on");
  if (/^[0-9]$/.test(e.key) && +e.key <= SCENES.length) pinScene(e.key === "0" ? null : +e.key - 1);   // 1-5 pin a scene, 0 = auto
  if (e.key === "[" || e.key === "]") { const v = (+$("smin").value + (e.key === "]" ? 60 : 1380)) % 1440; $("smin").value = v; setOverride($("sdate").value, v); }
});
if (DEV && q.has("t")) { const [hh, mi] = q.get("t").split(":").map(Number); setOverride($("sdate").value, hh * 60 + mi); }
else if (DEV && q.has("date")) setOverride($("sdate").value, new Date().getHours() * 60 + new Date().getMinutes());
if (DEV && q.has("play")) { $("scrub").classList.add("on"); startPlay(); }

function fit() {   // any screen shape: the stage is 1920x1080 scaled to fit, then grown to the real edges (UI pinned right/bottom follows); the scene scales to cover
  const z = Math.min(innerWidth / 1920, innerHeight / 1080), W = innerWidth / z, H = innerHeight / z, st = $("screen").style;
  st.transform = `scale(${z})`; st.width = W + "px"; st.height = H + "px";
  const s = Math.max(W / 1920, H / 1080);   // scaled about its centre (the parallax origin), placed so its top-left stays on the screen's: a wide screen crops grass, a tall one the right edge, never the sky title
  st.setProperty("--cov", s); st.setProperty("--covx", 960 * (s - 1) + "px"); st.setProperty("--covy", 540 * (s - 1) + "px");
}
addEventListener("resize", fit); fit();
render(); setInterval(() => { if (!override && !window.HOSTCOVERED) render(); }, 1000);   // HOSTCOVERED: the host says a window covers this screen (bridge in index.html)

function selfTest() {
  const near = (d, hhmm) => Math.abs(d.getHours() * 60 + d.getMinutes() - (+hhmm.slice(0, 2) * 60 + +hhmm.slice(3))) <= 6;
  const jun = dayEvents(new Date(2026, 5, 21), 59.91, 10.75), dec = dayEvents(new Date(2026, 11, 21), 59.91, 10.75);
  const tro = dayEvents(new Date(2026, 11, 21), 69.65, 18.96);
  console.assert(near(jun.rise, "03:54") && near(jun.set, "22:44"), "Oslo midsummer", hm(jun.rise), hm(jun.set));
  console.assert(near(dec.rise, "09:18") && near(dec.set, "15:12"), "Oslo midwinter", hm(dec.rise), hm(dec.set));
  console.assert(tro.rise === null && tro.set === null, "Tromsø polar night has no sunrise");
  console.assert(sunAt(new Date(2026, 5, 21, 1, 20), 59.91, 10.75).minE > -12, "Oslo midsummer never reaches full night");
  console.log("selftest done", hm(jun.rise), hm(jun.set), hm(dec.rise), hm(dec.set));
}
if (DEV && q.has("selftest")) selfTest();
// Place everything without transitions on load (carousels, scene crossfade); they animate from then on.
$("screen").classList.add("boot");
if (!q.has("shot")) requestAnimationFrame(() => requestAnimationFrame(() => $("screen").classList.remove("boot")));   // ?shot: screenshots keep transitions off
