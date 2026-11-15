/* cues.js: glue between concepts/polish/index.html and motion.js + sound.js. Page-specific on purpose.
   Every state change in the page goes through sync(), which rewrites #ui's class list and redraws the menu,
   so one observer on #ui sees all of them: mode (m-*), overlay (ov-*), cl-on, applying, guide-on, shade-on,
   wa-pause, plus the current category, item and unread count. Keys are only peeked at (Enter = select,
   Esc = back, arrows at the end of the list = edge), never consumed.
   LOAD ORDER: in <head>, after core.css, before the page's own script, so this keydown listener runs first:
     <script src="motion/sound.js"></script><script src="motion/motion.js"></script><script src="motion/cues.js"></script>
*/
(() => {
  // screen 2 mirroring screen 1 (body.s2) gets the same class changes a moment later: screen 1 already played them, so stay quiet (no double chime)
  const $ = id => document.getElementById(id), play = (n, o) => document.body?.classList.contains("s2") || window.Sound?.play(n, o), M = () => window.Motion;
  let key = "", keyAt = 0, hush = 0;
  const recent = (ms, ...ks) => performance.now() - keyAt < ms && ks.includes(key);
  const ovOf = c => [...c].find(x => x.startsWith("ov-")), modeOf = c => [...c].find(x => x.startsWith("m-"));

  addEventListener("keydown", e => {
    key = e.key; keyAt = performance.now();
    const ui = $("ui"); if (!ui) return;
    const c = ui.classList, ov = ovOf(c), mode = modeOf(c);
    if (e.key === "Enter" && mode !== "m-stock" && mode !== "m-idle" && ov !== "ov-hatch" && (mode !== "m-game" || c.contains("guide-on"))) play("select");
    // edge: an arrow that did not move the carousel
    if ((e.key === "ArrowLeft" || e.key === "ArrowRight") && mode === "m-desk" && !ov && !c.contains("cl-on")) {
      const row = $("row"), i = [...row.children].findIndex(x => x.classList.contains("cur"));
      setTimeout(() => { if ([...row.children].findIndex(x => x.classList.contains("cur")) === i) { play("edge"); M()?.bump(row, e.key === "ArrowRight" ? -1 : 1); } });
    }
    // moves inside overlays: switcher cards, search results, guide buttons (no class change to watch, so tick on the key)
    if ((ov === "ov-switcher" && /^(ArrowLeft|ArrowRight|Tab)$/.test(e.key)) || (ov === "ov-search" && /^(ArrowUp|ArrowDown)$/.test(e.key))
      || (mode === "m-game" && c.contains("guide-on") && /^Arrow(Left|Right|Up|Down)$/.test(e.key))) play("move", { axis: "x", i: 2 });   // guide rows are vertical (Alyosha, round 2)
    if (e.key === "Delete" && ov === "ov-switcher") { const d = document.querySelector("#swirow .card.cur.desk"); if (d) { play("error"); M()?.bump(d, 0); } }
  }, true);

  addEventListener("pointerdown", e => {
    if (!e.target.closest?.("#screen")) return;   // only clicks on the desktop itself count
    key = "click"; keyAt = performance.now();
    const t = e.target.closest?.(".item.cur,.btn,.chip,.gb,.res,.send,#states button,.card.cur");
    if (!t) return;
    window.Sound?.play("select");   // a real click on this screen: plays even on screen 2
    if (t.classList.contains("item")) { t.classList.add("tp-press"); setTimeout(() => t.classList.remove("tp-press"), 140); }
  }, true);

  function start() {
    const ui = $("ui"); if (!ui) return;
    const snap = () => ({
      cls: new Set(ui.classList), cat: $("catname")?.textContent,
      slab: [...document.querySelectorAll("#slabs .slab")].findIndex(x => x.classList.contains("on")),
      item: [...($("row")?.children || [])].findIndex(x => x.classList.contains("cur")),
      unread: +($("badge")?.textContent || 0),
    });
    let p = snap();
    const context = mode => window.Sound?.context({ gaming: mode === "m-game" || mode === "m-stock" });
    context(modeOf(p.cls));

    new MutationObserver(() => {
      const n = snap(), had = x => p.cls.has(x), has = x => n.cls.has(x);
      const pm = modeOf(p.cls), nm = modeOf(n.cls), po = ovOf(p.cls), no = ovOf(n.cls);
      const flip = (x, on, off) => { if (has(x) && !had(x)) on(); else if (!has(x) && had(x)) off(); };
      const closeOrSelect = () => { if (!recent(200, "Enter", "click")) play("close"); };

      if (pm !== nm) {
        hush = performance.now() + 150;                        // the menu resets on mode changes; no move ticks for that
        if (nm === "m-stock") { play("hatch"); context(nm); M()?.run("drain"); }
        else if (pm === "m-game" || pm === "m-stock") { context(nm); play("wake"); M()?.run("return"); }
        else if (pm === "m-idle") play("wake");
        else context(nm);                                      // into a game: enterGame's wrapper already played "game"
      }
      if (po !== no) no ? play("open") : closeOrSelect();
      flip("cl-on", () => play("open"), closeOrSelect);
      // the guide blade: its own chime, and while it is open over a game its sounds may play (Sound.context guide)
      flip("guide-on", () => { window.Sound?.context({ guide: true }); play("guide"); },
        () => { if (!recent(200, "Enter", "click")) play("guide", { on: false }); window.Sound?.context({ guide: false }); });
      flip("applying", () => play("sweep"), () => {});
      flip("shade-on", () => play("toggle", { on: false }), () => play("toggle", { on: true }));
      flip("wa-pause", () => play("toggle", { on: false }), () => play("toggle", { on: true }));
      if (n.unread > p.unread) play("notify");
      if (nm === "m-desk" && pm === nm && !no && !po && performance.now() > hush) {
        if (n.cat !== p.cat) recent(300, "Escape", "Home") ? play("back") : play("move", { axis: "y", i: n.slab });
        else if (n.item !== p.item) play("move", { axis: "x", i: n.item });
      }
      p = n;
    }).observe(ui, { attributes: true, attributeFilter: ["class"] });

    // a toast that nobody asked for (no Enter/click just before) is a notification
    const toast = $("toast");
    if (toast) new MutationObserver(() => { if (toast.classList.contains("on") && !toast._on && !recent(600, "Enter", "click")) play("notify"); toast._on = toast.classList.contains("on"); })
      .observe(toast, { attributes: true, attributeFilter: ["class"] });

    // phase drives the sound context: late night = quiet hours (silent), dusk/night = softer, darker timbre
    const ph = $("phase");
    const quietOn = () => { try { return localStorage.getItem("tp-quiet") !== "0"; } catch { return true; } };   // Settings › Sound › Quiet late at night
    const phase = () => { const t = (ph?.textContent || "").toLowerCase(); window.Sound?.context({ quiet: quietOn() && t.includes("late night"), night: /night|dusk/.test(t) }); };
    if (ph) { new MutationObserver(phase).observe(ph, { childList: true, characterData: true, subtree: true }); phase(); addEventListener("tp-quiet", phase); }

    // game launch: zoom into the item first, then the page's own enterGame runs (classic-script globals are patchable)
    if (typeof window.enterGame === "function") {
      const orig = window.enterGame;
      window.enterGame = n => { play("game"); M() ? M().run("game", () => orig(n)) : orig(n); };
    }
    if (new URLSearchParams(location.search).has("intro")) { play("wake"); M()?.run("intro"); }   // arriving from boot.html
  }
  document.readyState === "loading" ? addEventListener("DOMContentLoaded", start) : start();
})();
