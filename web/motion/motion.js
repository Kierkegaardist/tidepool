/* motion.js: the Tidepool motion language (track C, polish phase).
   Named easing curves and durations, the CSS for every state change in spec.md part 3, scene parallax,
   and the reduced-motion fallback. Written against the class names of concepts/polish/index.html.

   API (window.Motion):
     Motion.tokens            {ease, dur (ms), exit, stagger, ...}; also on :root as --ease-* and --dur-*
     Motion.run(name, fn)     scripted transitions: "game" (zoom into the item, then fn), "drain" (escape hatch),
                              "return" (back from a game or stock Windows), "intro" (first desktop after boot). Returns a promise.
     Motion.bump(el, dir)     rubber-band nudge: dir -1/1 = hit the end of a list, 0 = shake (refused)
     Motion.reduced(on)       force reduced motion on/off; no argument = read (default follows the OS setting, ?still forces it)
     Motion.parallax(on)      scene depth on/off (default on)
*/
(() => {
  const T = {
    ease: {
      glide: "cubic-bezier(.2,.9,.3,1)",      // the house curve: things that slide along a track (carousel, rail)
      surface: "cubic-bezier(.16,1,.3,1)",    // arriving: rises fast, settles long (panels, overlays, wake)
      spring: "cubic-bezier(.34,1.56,.64,1)", // a small overshoot: focus growing, toasts landing
      sink: "cubic-bezier(.55,0,.75,.2)",     // leaving: accelerates away (closing, idle, drain)
      drift: "cubic-bezier(.45,0,.55,1)",     // ambient: sine in-out (fades, the sky, the scene)
    },
    dur: { tick: 90, tap: 160, move: 240, overlay: 280, toast: 420, panel: 450, slide: 550, idle: 900, game: 900, hatch: 1100, apply: 1400, blade: 400, scene: 4000, boot: 6500 },
    exit: .7,        // closing takes 70 % of opening: arrivals are savoured, departures get out of the way
    stagger: 30,     // ms between siblings that enter together
    rise: 14,        // px an overlay travels as it appears
    blade: 560,      // px width of the guide blade (ui.css draws it; the slide distance reads this)
    depth: { item: 6, cat: 5, pointer: 8, ease: 180 },   // parallax px per item / per category / pointer edge, smoothing ms
  };
  const root = document.documentElement, q = new URLSearchParams(location.search);
  const vars = [...Object.entries(T.ease).map(([k, v]) => `--ease-${k}:${v}`), ...Object.entries(T.dur).map(([k, v]) => `--dur-${k}:${v}ms`),
    `--exit:${T.exit}`, `--stagger:${T.stagger}ms`, `--rise:${T.rise}px`, `--blade:${T.blade}px`].join(";");
  const X = n => `calc(var(--dur-${n})*var(--exit))`;   // exit duration of token n
  const live = ":root:not([data-motion=reduced]) .screen:not(.boot)";   // entrance animations: never on frame one, never when reduced
  const nth = (sel, n = 10) => Array.from({ length: n }, (_, i) => `${sel}:nth-child(${i + 1}){animation-delay:${i * T.stagger}ms}`).join("");

  const CSS = `
:root{${vars}}
/* rail: the focused slab grows with a spring; its icon pops */
.rail{transition:transform calc(var(--dur-idle)*.5) var(--ease-surface),opacity var(--dur-overlay) var(--ease-drift)}
.m-idle .rail{transition:transform var(--dur-idle) var(--ease-sink)}
/* Alyosha, round 3 (perf.md): the focused slab and tile grow with the scale property, not width/height, so a move is compositor-only */
.slab{transition:scale var(--dur-move) var(--ease-spring)}
${live} .slab.on .ic{animation:tp-pop var(--dur-move) var(--ease-spring)}
/* title + description: the new text rises into place (class set by Motion on text change) */
${live} .tp-swap{animation:tp-swap var(--dur-move) var(--ease-surface)}
/* carousel: slide on the track; a new category's items cascade in */
.row{transition:transform var(--dur-slide) var(--ease-glide),opacity calc(var(--dur-idle)*.5) var(--ease-surface)}
.row.tp-new{transition:none}
.item{transition:scale var(--dur-slide) var(--ease-glide),translate var(--dur-slide) var(--ease-glide),opacity var(--dur-slide) var(--ease-glide),transform var(--dur-tap) var(--ease-spring)}
.item::after{transition:opacity var(--dur-move) var(--ease-glide)}
${live} .row.tp-new .item{animation:tp-cascade var(--dur-slide) var(--ease-surface) backwards}
${nth(live + " .row.tp-new .item")}
${live} .item.cur img{animation:tp-float 2.5s var(--ease-drift) 2 alternate}   /* one float up and back, then rest: an endless loop kept every glass blur redrawing (perf.md) */
.item.tp-press{transform:scale(.94)}
/* idle: the menu sinks away slowly; waking is quick */
.cat,.desc,.legend,.pill{transition:opacity calc(var(--dur-idle)*.5) var(--ease-surface),transform calc(var(--dur-idle)*.5) var(--ease-surface)}
.m-idle .cat,.m-idle .row,.m-idle .desc,.m-idle .legend,.m-idle .pill{transition:opacity var(--dur-idle) var(--ease-drift)}
.clockline{transition:transform calc(var(--dur-idle)*.5) var(--ease-surface)}
.m-idle .clockline{transition:transform var(--dur-idle) var(--ease-drift)}
.idlehint{transition:opacity var(--dur-idle) var(--ease-drift)}
${live} .m-idle .idlehint{animation:tp-breathe 4s var(--ease-drift) 6 alternate}   /* breathes for 24 s, then rests: an endless loop kept the glass re-blurring (perf.md, Alyosha round 3) */
/* Claude panel: surfaces from the right, sinks back faster; each new message rises */
.cl{transition:right ${X("panel")} var(--ease-sink)}
.cl-on .cl{transition:right var(--dur-panel) var(--ease-surface)}
${live} .cl .m:last-child{animation:tp-rise var(--dur-overlay) var(--ease-surface)}
.applying::after{animation-duration:var(--dur-apply)!important;animation-timing-function:var(--ease-drift)!important}
/* toast: lands with a spring, leaves by sinking */
.toast{transition:transform ${X("toast")} var(--ease-sink),opacity ${X("toast")} var(--ease-sink)}
.toast.on{transition:transform var(--dur-toast) var(--ease-spring),opacity var(--dur-tap) linear}
/* overlays: dim fades, the overlay rises, closing is 70 % */
.dim{transition:opacity ${X("overlay")} var(--ease-drift)}
.ov-switcher .dim,.ov-search .dim,.ov-hatch .dim{transition:opacity var(--dur-overlay) var(--ease-drift)}
.ovl{transition:opacity ${X("overlay")} var(--ease-sink),transform ${X("overlay")} var(--ease-sink)}
.ov-switcher #swi,.ov-search #srch,.ov-hatch #hatch,.ov-notifs #notifs,.ov-states #states{transition:opacity var(--dur-overlay) var(--ease-surface),transform var(--dur-overlay) var(--ease-surface)}
#swi{transform:scale(.96)}#srch{transform:translateY(calc(var(--rise)*-1)) scale(.97)}#hatch{transform:scale(.94)}#notifs{transform:translateX(30px)}
${live} .ov-notifs .nt{animation:tp-rise var(--dur-overlay) var(--ease-surface) backwards}
${nth(live + " .ov-notifs .nt")}
.card{transition:scale var(--dur-move) var(--ease-spring),translate var(--dur-move) var(--ease-spring),opacity var(--dur-move) var(--ease-glide)}   /* Alyosha round 4: the card grows with scale, not width/height */
.res{transition:background var(--dur-tick) linear}
/* game guide (critique move 5): the full-height glass blade (geometry in ui.css) surfaces from the left edge and
   leaves faster; its sections follow it in, 60 ms apart. Transform + opacity only, so the GPU moves the glass. */
.ui.m-game .guide{transform:translateX(calc(-1 * var(--blade) - 40px));transition:transform ${X("blade")} var(--ease-sink)}
.ui.m-game.guide-on .guide{transform:none;transition:transform var(--dur-blade) var(--ease-surface)}
.m-game .guide>*{transition:opacity ${X("tap")} linear,transform ${X("tap")} var(--ease-sink)}
.m-game:not(.guide-on) .guide>*{opacity:0;transform:translateX(-28px)}
.m-game.guide-on .guide>*{transition:opacity var(--dur-move) var(--ease-drift),transform var(--dur-move) var(--ease-surface)}
${nth(".m-game.guide-on .guide>*", 6).replace(/animation-delay:(\d+)ms/g, (m, n) => `transition-delay:${120 + 2 * n}ms`)}
.m-game .guide .gb{transition:background var(--dur-tick) linear,box-shadow var(--dur-move) var(--ease-glide)}
${live} .m-game .game{animation:tp-fade var(--dur-panel) var(--ease-drift)}
/* scripted: game launch, drain, return, intro */
.tp-launch .item.cur{transform:scale(1.35);transition:transform var(--dur-game) var(--ease-sink)!important;z-index:2}
.tp-launch .ui>:not(.row):not(.rowclip),.tp-launch .item:not(.cur){opacity:0!important;transition:opacity calc(var(--dur-game)*.5) var(--ease-sink)!important}
.tp-flash{position:absolute;inset:0;z-index:60;pointer-events:none;opacity:0;background:radial-gradient(circle at 50% 45%,#fff,color-mix(in srgb,var(--accent,#00B4E6) 70%,#fff) 25%,#000 70%)}
.tp-launch .tp-flash{animation:tp-flash var(--dur-game) var(--ease-sink) forwards}
.tp-drain{position:absolute;inset:0;z-index:60;pointer-events:none;
  background:linear-gradient(180deg,rgba(255,255,255,.9) 0 3px,color-mix(in srgb,var(--accent,#00B4E6) 55%,#bfefff) 3px,color-mix(in srgb,var(--accent,#00B4E6) 80%,#003a55));
  animation:tp-drain var(--dur-hatch) var(--ease-sink) forwards}
${live}.tp-return .ui,${live}.tp-return #scenes{animation:tp-return calc(var(--dur-idle)*.8) var(--ease-surface)}
${live}.tp-intro .rail{animation:tp-in-left var(--dur-panel) var(--ease-surface) backwards}
${live}.tp-intro #slabs .slab{animation:tp-pop var(--dur-panel) var(--ease-spring) backwards}
${nth(live + ".tp-intro #slabs .slab", 9).replace(/(\d+)ms/g, (m, n) => 150 + 2 * n + "ms")}
${live}.tp-intro .cat,${live}.tp-intro .desc{animation:tp-swap var(--dur-slide) var(--ease-surface) .35s backwards}
${live}.tp-intro .item{animation:tp-cascade var(--dur-slide) var(--ease-surface) backwards}
${nth(live + ".tp-intro .item").replace(/(\d+)ms/g, (m, n) => 400 + 2 * n + "ms")}
${live}.tp-intro .clockline,${live}.tp-intro .pill,${live}.tp-intro .wa-dock,${live}.tp-intro .legend{animation:tp-fade var(--dur-slide) var(--ease-drift) .6s backwards}
.tp-bump{animation:tp-bump var(--dur-move) var(--ease-spring)}.tp-shake{animation:tp-shake calc(var(--dur-move)*1.4) var(--ease-drift)}
/* #scenes has no will-change (16 Nov): it froze the layer's raster at scale 1, so the parallax scale stretched a 1920 px bitmap (soft photos) */
@keyframes tp-pop{from{transform:scale(.82)}}
@keyframes tp-swap{from{opacity:0;transform:translateY(10px)}}
@keyframes tp-cascade{from{opacity:0;transform:translateY(34px) scale(.92)}}
@keyframes tp-rise{from{opacity:0;transform:translateY(var(--rise))}}
@keyframes tp-fade{from{opacity:0}}
@keyframes tp-float{to{transform:translateY(-6px)}}
@keyframes tp-breathe{to{opacity:.35}}
@keyframes tp-flash{0%{opacity:0}55%{opacity:.9}100%{opacity:1;background:#000}}
@keyframes tp-drain{from{clip-path:inset(0 0 0 0)}to{clip-path:inset(100% 0 0 0)}}
@keyframes tp-return{from{opacity:0;transform:scale(1.03);filter:blur(6px)}}
@keyframes tp-in-left{from{transform:translateX(-160px)}}
@keyframes tp-bump{40%{translate:calc(var(--dir,1)*14px) 0}}
@keyframes tp-shake{20%{translate:-8px 0}40%{translate:7px 0}60%{translate:-5px 0}80%{translate:3px 0}}
/* reduced motion: nothing travels. State changes become short cross-fades, ambience stops, progress still shows */
[data-motion=reduced] *,[data-motion=reduced] *::before,[data-motion=reduced] *::after{transition-property:opacity,background-color,color,box-shadow!important;transition-duration:var(--dur-tap)!important;transition-timing-function:linear!important;animation-play-state:paused!important}
[data-motion=reduced] .steps li.go::before,[data-motion=reduced] .dots::after{animation-play-state:running!important}
[data-motion=reduced] .applying::after,[data-motion=reduced] .tp-drain,[data-motion=reduced] .tp-flash{display:none}
`;

  /* ---- reduced motion ---- */
  const mq = matchMedia("(prefers-reduced-motion: reduce)");
  let forced = q.has("still") ? true : null;
  const setReduced = () => { root.dataset.motion = (forced ?? mq.matches) ? "reduced" : "full"; };
  mq.addEventListener?.("change", setReduced); setReduced();
  const reduced = () => root.dataset.motion === "reduced";

  /* ---- helpers ---- */
  const $ = id => document.getElementById(id), screen = () => $("screen") || document.body;
  const wait = ms => new Promise(r => setTimeout(r, ms));
  const replay = (el, cls) => { el.classList.remove(cls); void el.offsetWidth; el.classList.add(cls); };
  function bump(el, dir = 1) {
    if (!el || reduced()) return;
    el.style.setProperty("--dir", dir);
    const cls = dir ? "tp-bump" : "tp-shake";
    replay(el, cls); el.addEventListener("animationend", () => el.classList.remove(cls), { once: true });
  }
  let busy = false;
  async function run(name, fn = () => {}) {
    const s = screen();
    if (reduced() || busy) { fn(); return; }
    busy = true;
    try {
      if (name === "game") {
        const f = Object.assign(document.createElement("div"), { className: "tp-flash" });
        s.appendChild(f); s.classList.add("tp-launch"); s.style.pointerEvents = "none";
        await wait(T.dur.game); fn();
        s.classList.remove("tp-launch"); s.style.pointerEvents = ""; f.remove();
      } else if (name === "drain") {            // call right after the switch to stock Windows: the water drains off it
        fn(); const d = Object.assign(document.createElement("div"), { className: "tp-drain" });
        s.appendChild(d); await wait(T.dur.hatch); d.remove();
      } else if (name === "return" || name === "intro") {
        fn(); replay(s, "tp-" + name); await wait(name === "intro" ? 1600 : T.dur.idle); s.classList.remove("tp-" + name);
      } else fn();
    } finally { busy = false; }
  }

  /* ---- parallax: the scene drifts against the menu (depth) and toward the pointer; idle when settled ---- */
  let pOn = !q.has("noparallax"), px = 0, py = 0, cx = 0, cy = 0, mx = 0, my = 0, raf = 0, t0 = 0;
  function target() {
    const slabs = [...document.querySelectorAll("#slabs .slab")], items = [...($("row")?.children || [])];
    // Home is the orb now (no slab), so it is depth 0 and Games is 1 (Alyosha, round 2)
    const c = slabs.findIndex(e => e.classList.contains("on")) + 1, i = Math.max(0, items.findIndex(e => e.classList.contains("cur")));
    px = -Math.min(i, 6) * T.depth.item + mx * T.depth.pointer; py = -c * T.depth.cat + my * T.depth.pointer;
  }
  function frame(t) {
    const sc = $("scenes"), dt = Math.min(64, t - (t0 || t)); t0 = t;
    if (!sc || !pOn || reduced() || screen().classList.contains("gaming")) { raf = 0; if (sc) sc.style.transform = ""; return; }
    const k = 1 - Math.exp(-dt / T.depth.ease); cx += (px - cx) * k; cy += (py - cy) * k;
    const moving = Math.abs(px - cx) + Math.abs(py - cy) > .05;
    // +20 / 127/120: room so no edge shows. 127/120 makes the 1920x1080 stage exactly 2032x1143 (the photos' own size) with its corner at (-56, -31.5) + the shift,
    // so at rest the shift snaps to whole device pixels and every photo pixel lands on one screen pixel (16 Nov: 1.06 + sub-pixel rest blurred the sharp photos)
    const tx = moving ? cx : Math.round(cx), ty = moving ? cy + 20 : Math.round(cy + 19.5) + .5;
    sc.style.transform = `translate3d(${tx.toFixed(2)}px,${ty.toFixed(2)}px,0) scale(${127 / 120})`;
    raf = moving ? requestAnimationFrame(frame) : 0;   // settles to zero work
  }
  const kick = () => { target(); if (!raf) { t0 = 0; raf = requestAnimationFrame(frame); } };
  addEventListener("pointermove", e => { mx = e.clientX / innerWidth * 2 - 1; my = e.clientY / innerHeight * 2 - 1; kick(); }, { passive: true });

  /* ---- watch the page: text swaps, new carousel rows, focus changes (no edits to the page needed) ---- */
  function watch() {
    const st = document.createElement("style"); st.id = "tp-motion"; st.textContent = CSS; document.head.appendChild(st);   // after the page's own <style>, so these win
    for (const id of ["catname", "iname"]) {
      const el = $(id); if (!el) continue; let prev = el.textContent;
      new MutationObserver(() => { if (el.textContent !== prev) { prev = el.textContent; replay(el, "tp-swap"); } }).observe(el, { childList: true, characterData: true, subtree: true });
    }
    const row = $("row");
    if (row) new MutationObserver(ms => {
      if (ms.some(m => m.type === "childList")) { row.classList.add("tp-new"); requestAnimationFrame(() => requestAnimationFrame(() => row.classList.remove("tp-new"))); }
      kick();
    }).observe(row, { childList: true, subtree: true, attributes: true, attributeFilter: ["class"] });
    const slabs = $("slabs"); if (slabs) new MutationObserver(kick).observe(slabs, { subtree: true, attributes: true, attributeFilter: ["class"] });
    const sc = $("screen"); if (sc) new MutationObserver(kick).observe(sc, { attributes: true, attributeFilter: ["class"] });
    kick();
  }
  document.readyState === "loading" ? addEventListener("DOMContentLoaded", watch) : watch();

  window.Motion = {
    tokens: T, run, bump,
    reduced(on) { if (on === undefined) return reduced(); forced = on === null ? null : !!on; setReduced(); kick(); },
    parallax(on) { if (on === undefined) return pOn; pOn = !!on; kick(); },
  };
})();
