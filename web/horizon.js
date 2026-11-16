/* horizon.js: the horizon visualizer (user, 15 Nov: "a huge visualizer on the horizon, some of it obscured by trees, maybe a cloud").
   What the PC plays (host "spectrum" events: 32 bands 0..255 from src/Spectrum.cs, ~30/s) rises from behind the land of the scene photo as light
   far away: aurora curtains at night, soft light pillars at golden hour and dusk, pale shafts by day. Bass in the middle, highs out to the sides.
   Each scene has <div class="hviz"><canvas></div> (core.js sceneHTML) right after .sky, clipped by the same sky edge, so ridges and trees stand
   in front, and under .light, so the time-of-day grade tints it. The wrapper blends with `screen` (it can't inside .sky: .sky's mask isolates it),
   which keeps bright clouds and the sky title bright, so they read as in front. Colours come from the phase (--glow, --accent, --stars).
   Main screen + partner draw one shape across the bezel (XOFF, like the sun); extra screens draw their own copy. Lake and shore get a dim
   rippled reflection on the water (.hrefl). Silence: the bands fall, the light fades, the loop stops and the layer is hidden (costs nothing).
   Runs only when it can be seen (the Winamp visualizer's rule: not hidden, covered, frozen, under a full-screen overlay) and not in reduced motion.
   Settings › Scenes › Horizon visualizer: tp-hviz = off | subtle | full (default). ?vis=demo with no host draws a made-up spectrum. */
(() => {
  const N = 32, CW = 480, CH = 160, FPS_MS = 1000 / 30;
  const cur = new Float32Array(N), tgt = new Float32Array(N);
  let amp = 0, loud = 0, got = 0, cap = null, raf = 0, last = 0, want = null, live = null, sprite = null, spriteKey = "";
  const LS = k => { try { return localStorage.getItem(k); } catch { return null; } };
  const H = { mode: LS("tp-hviz") ?? "full" };   // off | subtle | full
  const demo = !window.host && new URLSearchParams(location.search).get("vis") === "demo";

  /* ---- data in ---- */
  function feed(v) {
    if (!Array.isArray(v)) return;
    let any = false;
    for (let i = 0; i < N; i++) { tgt[i] = (v[i] ?? 0) / 255; any ||= tgt[i] > 0; }
    got = performance.now();
    if (any) { loud = got; start(); }
  }
  addEventListener("host", e => { if (e.detail?.type === "spectrum") feed(e.detail.value); });
  if (demo) setInterval(() => {   // a made-up song: a kick every 0.47 s, a wandering mid, sparkly highs
    const t = Date.now() / 1000, kick = Math.exp(-((t % .47) / .12)), v = [];
    for (let i = 0; i < N; i++) v.push(Math.round(255 * Math.min(1, Math.max(0, (i < 6 ? .6 + .4 * kick : .85 - i * .016) * (.7 + .3 * Math.sin(t * 1.7 + i * .55) * Math.sin(t * .9 - i * .21)) + (i > 20 ? .1 * Math.sin(t * 9 + i) : 0)))));
    feed(v);
  }, 33);

  /* ---- may it run? (asked twice a second; the host captures only while some page says yes) ---- */
  const scr = () => document.getElementById("screen"), ui = () => document.getElementById("ui");
  const allowed = () => H.mode !== "off" && !window.Motion?.reduced?.() && !document.hidden && !scr()?.matches(".covered,.frozen")
    && !/ov-(switcher|search|hatch|welcome)/.test(ui()?.className ?? "") && !!document.querySelector(".scn.on")?.offsetParent;   // not .hviz itself: it is display:none until it draws
  const waWants = () => typeof waShown === "function" && waShown() && !WA.visOff;   // the Winamp gadget's bars (core.js vis) use the same bands
  function check() {
    const ok = allowed(), c = ok || waWants();
    if (ok !== want) { want = ok; if (!ok) stop(); }
    if (c !== cap) { cap = c; window.host?.spectrum?.(c); }
  }
  setInterval(check, 500); addEventListener("visibilitychange", check);

  /* ---- the light's colours, from the phase the page is in (render() writes these on #screen) ---- */
  const rgb = h => { h = (h || "#ffffff").trim(); return h[0] === "#" ? [1, 3, 5].map(i => parseInt(h.slice(i, i + 2), 16)) : [255, 255, 255]; };
  function look() {
    const st = scr()?.style, v = k => st?.getPropertyValue(k) ?? "";
    const night = Math.min(1, Math.max(0, (+v("--stars") || 0) / .85)), gb = +v("--gb") || 1;
    const glow = rgb(v("--glow")), acc = rgb(v("--accent"));
    const warm = Math.min(1, Math.max(0, (glow[0] - glow[2]) / 120)) * Math.min(1, Math.max(0, (1.04 - gb) / .25));   // golden hour and dusk: a warm glow on a sky that is no longer midday-bright
    return { night, warm, day: Math.max(0, 1 - night - warm), glow, acc };
  }
  /* the palettes: real aurora washes between hues along the arc (green core, red/magenta tops, violet and pink fringes). Each time of day has
     its own four {foot, mid, top} washes, blended by the phase weights; columns drift between them (user, 16 Nov: "too monocoloured"). */
  const PAL = {
    night: [[[90,255,150],[60,230,140],[230,60,110]], [[70,240,200],[60,170,220],[150,80,240]], [[120,255,120],[200,90,220],[255,70,150]], [[110,140,255],[150,90,255],[255,90,170]]],
    dawn:  [[[255,200,170],[255,140,170],[180,110,230]], [[255,225,150],[255,170,120],[255,110,150]], [[200,255,210],[150,220,255],[200,140,255]], [[255,170,210],[230,120,220],[140,110,255]]],
    dusk:  [[[255,190,110],[255,120,90],[200,70,160]], [[255,150,170],[230,90,170],[120,70,220]], [[255,220,130],[255,160,80],[230,80,90]], [[240,120,200],[170,90,230],[80,90,220]]],
    day:   [[[230,250,255],[170,220,255],[140,180,255]], [[255,245,220],[200,240,235],[150,210,240]], [[235,225,255],[190,190,255],[150,160,255]], [[220,255,240],[160,235,225],[130,200,250]]],
  };
  const K = 4;
  function makeSprite(L) {   // an atlas of K vertical strips (one per wash), bottom bright to top clear; stretched per column (no per-frame gradients)
    const dawn = new Date().getHours() < 12, key = L.glow + "|" + L.night.toFixed(2) + "|" + L.warm.toFixed(2) + "|" + dawn;
    if (key === spriteKey) return sprite;
    spriteKey = key; sprite ??= document.createElement("canvas"); sprite.width = 4 * K; sprite.height = 128;
    const g = sprite.getContext("2d"), c = (a, k) => `rgba(${a.map(Math.round)},${k})`, warmP = dawn ? PAL.dawn : PAL.dusk;
    g.clearRect(0, 0, 4 * K, 128);
    for (let k = 0; k < K; k++) {
      const mix = j => [0, 1, 2].map(i => {   // phase weights, plus a fifth of the scene's own glow so the light still belongs to the picture
        const v = (PAL.night[k][j][i] * L.night + warmP[k][j][i] * L.warm + PAL.day[k][j][i] * L.day) / (L.night + L.warm + L.day || 1);
        return v * .8 + L.glow[i] * .2;
      });
      const foot = mix(0), mid = mix(1), top = mix(2), pale = foot.map(x => x + (255 - x) * .25 * L.warm);   // golden hour's pillars burn whiter at the foot
      const gr = g.createLinearGradient(0, 0, 0, 128);
      gr.addColorStop(0, c(top, 0)); gr.addColorStop(.22, c(top, .45)); gr.addColorStop(.5, c(mid, .9)); gr.addColorStop(.82, c(foot, 1)); gr.addColorStop(1, c(pale, .9));   // most of the foot is behind the ridge: the body has to carry
      g.fillStyle = gr; g.fillRect(4 * k, 0, 4, 128);
    }
    return sprite;
  }

  /* ---- drawing ---- */
  function target() {   // the canvas of the scene on show (scenes are rebuilt when the screen layout changes)
    if (live?.c.isConnected && live.c.closest(".scn.on")) return live;
    if (live) live.w.classList.remove("live");
    const w = document.querySelector(".scn.on .hviz"), c = w?.querySelector("canvas");
    live = c ? { w, c, g: c.getContext("2d"), r: w.parentElement.querySelector(".hrefl canvas") } : null;
    if (live) live.rg = live.r?.getContext("2d");
    return live;
  }
  function start() { if (!raf && want) raf = requestAnimationFrame(frame); }
  function stop() {
    cancelAnimationFrame(raf); raf = 0;
    if (live) { live.g.clearRect(0, 0, CW, CH); live.rg?.clearRect(0, 0, CW, live.r.height); live.w.classList.remove("live"); live.r?.parentElement.classList.remove("live"); }
  }
  let L = null, lookAt = 0;
  function frame(now) {
    raf = requestAnimationFrame(frame);
    if (now - last < FPS_MS - 2) return;   // 30 fps cap
    last = now;
    const T = target(); if (!T) return;
    const quiet = now - loud > 300;
    amp = quiet ? Math.max(0, amp - .022) : Math.min(1, amp + .06);   // in over ~0.5 s; out over ~1.5 s after the bands have fallen
    let e = 0; for (let i = 0; i < N; i++) { cur[i] += (tgt[i] - cur[i]) * .45; e += cur[i]; }
    if (amp <= 0 && e < .02) { stop(); return; }
    if (now - lookAt > 1000 || !L) { L = look(); lookAt = now; }
    T.w.classList.add("live"); T.r?.parentElement.classList.add("live");
    draw(T, L, Date.now() / 1000);
  }

  function level(u) {   // u = -1..1 across the whole shape: bass at 0, highs at the ends; smooth between bands
    const f = Math.min(1, Math.abs(u)) ** .8 * (N - 1), i = Math.floor(f), k = f - i, s = k * k * (3 - 2 * k);
    return cur[i] * (1 - s) + cur[Math.min(N - 1, i + 1)] * s;
  }
  function draw(T, L, t) {
    const g = T.g, wide = DUAL && ROLE !== "extra", W = wide ? 3840 : 1920, x0 = wide ? XOFF : 0;   // world: main + partner as one sky
    const str = (H.mode === "subtle" ? .55 : 1) * amp, S = makeSprite(L);
    g.clearRect(0, 0, CW, CH); g.globalCompositeOperation = "lighter";
    const bass = (cur[0] + cur[1] + cur[2] + cur[3]) / 4;
    // the dome: a broad glow behind the land that breathes with the bass (an ellipse that fades out inside the canvas: no edge)
    const cx = (W / 2 - x0) / 4, gc = L.glow, da = Math.min(1, (.22 + .45 * bass) * str * (.55 + .6 * L.night + .5 * L.warm));
    const dome = g.createRadialGradient(0, 0, 0, 0, 0, CH * .95);
    dome.addColorStop(0, `rgba(${gc[0]},${gc[1]},${gc[2]},${da})`); dome.addColorStop(.5, `rgba(${gc[0]},${gc[1]},${gc[2]},${da * .35})`); dome.addColorStop(1, `rgba(${gc[0]},${gc[1]},${gc[2]},0)`);
    g.save(); g.translate(cx, CH); g.scale(2.6 * W / 1920, 1); g.fillStyle = dome; g.fillRect(-CH, -CH, CH * 2, CH); g.restore();
    // the columns: aurora folds at night, separate pillars at golden hour and dusk, faint shafts by day
    const step = 2, pil = L.warm, a0 = str * (L.night * 2.4 + L.warm * 2 + L.day * .7);
    for (let x = 0; x < CW; x += step) {   // whole columns side by side: overlapping or sub-pixel strips left bright seams
      const wx = x0 + x * 4 + L.night * 12 * Math.sin((x0 + x * 4) * .003 + t * .5), u = (wx - W / 2) / (W / 2), env = Math.max(0, 1 - Math.abs(u) ** 2.4);   // at night the curtains lean a little
      if (env <= 0) continue;
      const v = level(u) ** .75;
      const fold = .5 + .5 * Math.sin(wx * .0042 + t * .35 + 2.2 * Math.sin(wx * .0011 - t * .17));              // slow curtains
      const ray = .86 + .14 * Math.sin(wx * .019 + t * .9) * Math.sin(wx * .0071 - t * .6);                      // soft striation
      const pillar = Math.max(0, Math.sin(wx * .0155 + Math.sin(t * .2) * .6)) ** 6;                              // light towers
      const mixw = L.night + pil + L.day || 1;
      const a = a0 * env * (L.night * (.35 + .65 * fold) * ray + pil * (.08 + .92 * pillar) + L.day * (.4 + .6 * fold)) / mixw;
      const h = CH * Math.min(1, (.14 + .95 * v) * (.75 + .25 * env)) * ((pil * (.5 + .6 * pillar) + L.day * .8 + L.night) / mixw);
      if (a < .004 || h < 1) continue;
      const al = Math.min(1, a * (.45 + .55 * v)), y = Math.round(CH - h - 3 * Math.sin(wx * .0025 + t * .3));
      const p = (.5 + .5 * Math.sin(wx * .0016 + t * .06 + 1.6 * Math.sin(wx * .00055 - t * .045))) * (K - 1), k0 = Math.floor(p), f = p - k0;   // the wash drifts slowly along the arc
      g.globalAlpha = al * (1 - f); g.drawImage(S, 4 * k0, 0, 4, 128, x, y, step, CH + 6 - y);
      if (f > .02) { g.globalAlpha = al * f; g.drawImage(S, 4 * Math.min(K - 1, k0 + 1), 0, 4, 128, x, y, step, CH + 6 - y); }
    }
    g.globalAlpha = 1; g.globalCompositeOperation = "source-over";
    if (T.rg) {   // the water's copy: upside down and squashed, each row shifted by a ripple
      const R = T.r, rh = R.height;
      T.rg.clearRect(0, 0, CW, rh);
      for (let y = 0; y < rh; y += 2) T.rg.drawImage(T.c, 0, CH - 1 - y * (CH / rh) * 1.6, CW, 2, Math.sin(y * .45 + t * 2.2) * (1 + y / rh * 3), y, CW, 2);
      const x0 = +(R.parentElement.dataset.x0 || 0) / 4;
      if (x0 > 0) { const f = T.rg.createLinearGradient(x0, 0, x0 + 45, 0); f.addColorStop(0, "rgba(0,0,0,0)"); f.addColorStop(1, "#000"); T.rg.globalCompositeOperation = "destination-in"; T.rg.fillStyle = f; T.rg.fillRect(0, 0, CW, rh); T.rg.globalCompositeOperation = "source-over"; }   // only on open water
    }
  }

  /* ---- Settings › Scenes ---- */
  window.Horizon = {
    mode: () => H.mode,
    set(m) { H.mode = m; try { localStorage.setItem("tp-hviz", m); } catch {} want = null; check(); },
    feed,
    bands: tgt, fresh: () => performance.now() - got < 600,   // the latest bands (0..1), for Winamp's bars
  };
})();
