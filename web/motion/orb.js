/* orb.js: the Tidepool orb, a small live window onto the real sky (critique move 1, polish track C round 2).
   Inside the glass: the current phase's sky gradient, the sun on it by day, the moon in its real phase at night,
   stars, a strip of sea with the light's reflection, then the Crystal gloss on top. Canvas 2D, all code-drawn.

   API (window.Orb):
     Orb.draw(g, x, y, r, st, t)   draw into a 2D context. st = {s, sun:{elev, x}, moon:{age, elev, x}}; t = seconds (ripples, twinkle)
                                   s = phase coordinate 0..8 (core.js phaseCoord), elev in degrees, x = -1 (rise side) .. 1 (set side)
     Orb.sky(date, lat, lon)       the real state for a moment and place (Oslo by default)
     Orb.moonAge(date)             0 = new, .25 = first quarter, .5 = full, .75 = last quarter
     Orb.mount(el, {size, now, lat, lon})   put a live orb canvas in el; redraws every minute. Returns redraw(date).
                                   On the polish page: Orb.mount($("orbw"), {size: 64, now: () => override ?? new Date()})
*/
(() => {
  const TAU = Math.PI * 2, RAD = Math.PI / 180;
  const cl = (v, a = 0, b = 1) => Math.max(a, Math.min(b, v));

  /* ---- phase colours: live from core.js's PHASES when the page has it, else this copy ---- */
  const KEYS = ["skyTop", "skyMid", "horizon", "water", "waterDeep", "sun", "glow", "stars"];
  // ponytail: copy of PHASES[*].scene (core.js, 2026-11-12) for pages without core.js (boot.html); the build reads one phases module
  const COPY = [["#030814","#07162B","#0C2A3A","#03211F","#010B0C","#FFD7A0","#3FE0D0",1],["#2B3A78","#B98AC9","#FFB4A2","#4E6E9E","#1E2F55","#FFD1B0","#FF9FB0",.2],["#4FB8FF","#A8E4FF","#F2FBFF","#19A3D1","#00628F","#FFF6D8","#FFF2B0",0],["#2E9BFF","#7FD0FF","#E3F7FF","#0096C7","#004E7A","#FFFFFF","#FFF8D0",0],["#4AA8F0","#A6DAF5","#F5F1DC","#1792B8","#075478","#FFF3C4","#FFE59A",0],["#3E6FB8","#F2B36B","#FFD98A","#C9844A","#3E3F6A","#FFE7A0","#FFB347",0],["#1C2659","#6A4C9C","#F08A6B","#3B3F7A","#141A3E","#FF9A7A","#FF7A8A",.35],["#06102A","#10245A","#2A3F7A","#0A2A4A","#031426","#FFD7A0","#6FA8FF",.85]];
  const hex = h => [1, 3, 5].map(i => parseInt(h.slice(i, i + 2), 16));
  let TBL = null;
  function look(s) {
    TBL ??= (typeof PHASES !== "undefined" ? PHASES.map(p => KEYS.map(k => p.scene[k])) : COPY).map(r => r.map(v => typeof v === "string" ? hex(v) : v));
    s = ((s % 8) + 8) % 8;
    const i = Math.floor(s), f = s - i, a = TBL[i], b = TBL[(i + 1) % 8], o = {};
    KEYS.forEach((k, j) => { o[k] = Array.isArray(a[j]) ? a[j].map((v, n) => v + (b[j][n] - v) * f) : a[j] + (b[j] - a[j]) * f; });
    return o;
  }
  const css = (c, a = 1) => `rgba(${c.map(Math.round)},${a})`;

  /* ---- the real sky: core.js's sun model when present, else the same maths ---- */
  const sunAt = window.sunAt || function (date, lat, lon) {
    const n = date.getTime() / 86400000 + 2440587.5 - 2451545.0;
    const L = (280.460 + 0.9856474 * n) % 360, g = ((357.528 + 0.9856003 * n) % 360) * RAD;
    const lam = (L + 1.915 * Math.sin(g) + 0.020 * Math.sin(2 * g)) * RAD, eps = (23.439 - 0.0000004 * n) * RAD;
    const dec = Math.asin(Math.sin(eps) * Math.sin(lam)), ra = Math.atan2(Math.cos(eps) * Math.sin(lam), Math.cos(lam)) / RAD;
    const H = ((280.46061837 + 360.98564736629 * n + lon - ra) % 360 + 540) % 360 - 180;
    const elev = Math.asin(Math.sin(lat * RAD) * Math.sin(dec) + Math.cos(lat * RAD) * Math.cos(dec) * Math.cos(H * RAD)) / RAD;
    const cosHs = (Math.sin(-0.833 * RAD) - Math.sin(lat * RAD) * Math.sin(dec)) / (Math.cos(lat * RAD) * Math.cos(dec));
    return { elev, H, Hs: Math.acos(cl(cosHs, -1, 1)) / RAD, maxE: 90 - Math.abs(lat - dec / RAD) };
  };
  const seg = (x, x0, x1, y0, y1) => y0 + (y1 - y0) * cl((x - x0) / (x1 - x0));
  const phaseCoord = window.phaseCoord || function ({ elev: e, maxE, H }) {
    const p = maxE > 3 ? cl((e - 3) / (maxE - 3)) * Math.min(1, maxE / 20) : 0;
    const sR = e <= -3 ? seg(e, -18, -3, 0, 1) : e < 3 ? seg(e, -3, 3, 1, 1.5) : 1.5 + 1.5 * p;
    const sS = e >= 3 ? 5 - 2 * p : e > -3 ? seg(e, 3, -3, 5, 6) : e > -12 ? seg(e, -3, -12, 6, 7) : 7 + seg(H, 150, 180, 0, 1);
    return { sR, sS, w: cl(0.5 + Math.sin(H * RAD) * 3) };
  };
  const moonAge = d => ((((d - Date.UTC(2000, 0, 6, 18, 14)) / 864e5 / 29.530588853) % 1) + 1) % 1;   // mean synodic month from a known new moon
  /* the real moon's place: low-precision lunar theory (main terms only, ~0.5 deg; ponytail: add Meeus' full series if a rise time ever needs to be exact to the minute)
     -> { elev, H (hour angle, -180..180, 0 = due south), Hs (hour angle of moonset, as sunAt's Hs) } */
  function moonAt(date, lat, lon) {
    const n = date.getTime() / 86400000 + 2440587.5 - 2451545.0;
    const L = 218.316 + 13.176396 * n, M = (134.963 + 13.064993 * n) * RAD, F = (93.272 + 13.229350 * n) * RAD;
    const l = (L + 6.289 * Math.sin(M)) * RAD, b = 5.128 * Math.sin(F) * RAD, e = 23.4397 * RAD;   // ecliptic longitude / latitude
    const ra = Math.atan2(Math.sin(l) * Math.cos(e) - Math.tan(b) * Math.sin(e), Math.cos(l)) / RAD;
    const dec = Math.asin(Math.sin(b) * Math.cos(e) + Math.cos(b) * Math.sin(e) * Math.sin(l));
    const H = ((280.46061837 + 360.98564736629 * n + lon - ra) % 360 + 540) % 360 - 180;
    const geo = Math.asin(Math.sin(lat * RAD) * Math.sin(dec) + Math.cos(lat * RAD) * Math.cos(dec) * Math.cos(H * RAD)) / RAD;
    const cosHs = (Math.sin(0.125 * RAD) - Math.sin(lat * RAD) * Math.sin(dec)) / (Math.cos(lat * RAD) * Math.cos(dec));   // +0.125 deg: parallax minus refraction, the moon's rise altitude
    return { elev: geo - 0.95 * Math.cos(geo * RAD), H, Hs: Math.acos(cl(cosHs, -1, 1)) / RAD };   // seen from the ground, parallax drops it up to ~1 deg
  }
  function sky(date = new Date(), lat = 59.91, lon = 10.75) {
    const sun = sunAt(date, lat, lon), { sR, sS, w } = phaseCoord(sun);
    const span = sun.Hs > 5 && sun.Hs < 175 ? sun.Hs : 90, xOf = H => cl(H / span, -1, 1);
    const mo = moonAt(date, lat, lon), mspan = mo.Hs > 5 && mo.Hs < 175 ? mo.Hs : 90;
    return { s: w > .5 ? sS : sR, sun: { elev: sun.elev, x: xOf(sun.H) }, moon: { age: moonAge(date), elev: mo.elev, x: cl(mo.H / mspan, -1, 1) } };
  }

  /* ---- drawing ---- */
  let seed = 11; const rnd = () => (seed = (seed * 16807) % 2147483647) / 2147483647;
  const STARS = Array.from({ length: 26 }, () => [rnd() * 2 - 1, rnd(), .4 + rnd() * .6]);
  function moon(g, mx, my, mr, age, a) {
    const k = Math.cos(age * TAU), waxing = age < .5;   // k: 1 new .. -1 full
    const gl = g.createRadialGradient(mx, my, mr * .5, mx, my, mr * 4); gl.addColorStop(0, `rgba(190,210,255,${.35 * a * (1 - k) / 2})`); gl.addColorStop(1, "rgba(190,210,255,0)");
    g.fillStyle = gl; g.fillRect(mx - mr * 4, my - mr * 4, mr * 8, mr * 8);
    g.fillStyle = `rgba(120,140,190,${.22 * a})`; g.beginPath(); g.arc(mx, my, mr, 0, TAU); g.fill();   // earthshine on the dark part
    // lit part: the bright half-disc, closed by the terminator ellipse (northern-hemisphere view)
    g.fillStyle = `rgba(246,243,228,${a})`; g.beginPath();
    g.arc(mx, my, mr, -Math.PI / 2, Math.PI / 2, !waxing);
    g.ellipse(mx, my, mr * Math.abs(k), mr, 0, Math.PI / 2, -Math.PI / 2, waxing ? k > 0 : k < 0);
    g.fill();
  }
  function draw(g, x, y, r, st, t = 0, layer) {   // layer: undefined = the whole orb; "back" / "front" = Marina's bowl (sky + reef under her, glass over her)
    const c = look(st.s), hz = layer ? y - r * .28 : y + r * .22, top = y - r;
    if (layer === "front") return glass(g, x, y, r, c, hz, st);
    const yOf = e => hz - cl(e, -14, 60) / 60 * r * (layer ? .45 : .57);   // the top of the sky stays clear of the gloss
    const sunA = cl((st.sun.elev + 7) / 7), sx = x + st.sun.x * r * .55, sy = yOf(st.sun.elev);
    const moonA = cl((-st.sun.elev - 2) / 6) * cl((st.moon.elev + 4) / 4), mx = x + st.moon.x * r * .55, my = yOf(st.moon.elev);
    g.save();
    // halo: the orb lights what is around it with its own sky
    const halo = g.createRadialGradient(x, y, r * .85, x, y, r * 1.8); halo.addColorStop(0, css(c.glow, .32)); halo.addColorStop(1, css(c.glow, 0));
    g.fillStyle = halo; g.fillRect(x - r * 1.9, y - r * 1.9, r * 3.8, r * 3.8);
    g.save(); g.beginPath(); g.arc(x, y, r, 0, TAU); g.clip();
    // sky
    const sk = g.createLinearGradient(0, top, 0, hz); sk.addColorStop(0, css(c.skyTop)); sk.addColorStop(.62, css(c.skyMid)); sk.addColorStop(1, css(c.horizon));
    g.fillStyle = sk; g.fillRect(x - r, top, r * 2, hz - top + 1);
    if (c.stars > .02) for (const [u, v, b] of STARS) {
      const a = c.stars * b * (.75 + .25 * Math.sin(t * 2.3 + u * 40)), s = Math.max(.7, r * .016);
      g.fillStyle = `rgba(255,255,255,${a})`; g.fillRect(x + u * r, top + v * (hz - top) * .92, s, s);
    }
    if (moonA > 0) moon(g, mx, my, r * .12, st.moon.age, moonA);
    if (sunA > 0) {
      const gl = g.createRadialGradient(sx, sy, 0, sx, sy, r * 1.1); gl.addColorStop(0, css(c.glow, .75 * sunA)); gl.addColorStop(.35, css(c.glow, .25 * sunA)); gl.addColorStop(1, css(c.glow, 0));
      g.fillStyle = gl; g.fillRect(x - r, top, r * 2, r * 2);
      const dg = g.createRadialGradient(sx, sy, 0, sx, sy, r * .15); dg.addColorStop(0, `rgba(255,255,255,${sunA})`); dg.addColorStop(.55, css(c.sun, sunA)); dg.addColorStop(1, css(c.sun, sunA * .9));
      g.fillStyle = dg; g.beginPath(); g.arc(sx, sy, r * .15, 0, TAU); g.fill();
    }
    if (layer) { reef(g, x, y, r, hz, c, sunA > moonA ? [sx, c.sun, sunA] : [mx, [235, 240, 255], moonA * .5]); g.restore(); g.restore(); return; }
    // the sea: below the horizon, with the light laid on it as broken strokes
    const wg = g.createLinearGradient(0, hz, 0, y + r); wg.addColorStop(0, css(c.water)); wg.addColorStop(1, css(c.waterDeep));
    g.fillStyle = wg; g.fillRect(x - r, hz, r * 2, y + r - hz);
    g.fillStyle = css(c.horizon, .85); g.fillRect(x - r, hz - .5, r * 2, Math.max(1, r * .012));
    const lit = sunA > moonA ? [sx, c.sun, sunA * cl(1 - st.sun.elev / 70)] : [mx, [235, 240, 255], moonA * .8];
    if (lit[2] > .02) for (let i = 0; i < 9; i++) {
      const k = i / 9, ly = hz + 2 + k * k * (y + r - hz) * .9, w = r * (.05 + k * .22) * (.6 + .4 * Math.sin(t * 3.1 + i * 2.1));
      g.fillStyle = css(lit[1], lit[2] * (1 - k) * .8); g.fillRect(lit[0] - w, ly, w * 2, Math.max(.8, r * .018));
    }
    // Crystal gloss: depth at the edge, a caustic glow at the bottom, the big highlight on top
    const dep = g.createRadialGradient(x, y, r * .62, x, y, r); dep.addColorStop(0, "rgba(0,20,40,0)"); dep.addColorStop(1, "rgba(0,20,40,.38)");
    g.fillStyle = dep; g.fillRect(x - r, top, r * 2, r * 2);
    const cau = g.createRadialGradient(x, y + r * .95, 0, x, y + r * .95, r * .75); cau.addColorStop(0, css(c.horizon, .45)); cau.addColorStop(1, css(c.horizon, 0));
    g.fillStyle = cau; g.fillRect(x - r, y, r * 2, r);
    const hl = g.createLinearGradient(0, top + r * .04, 0, y - r * .08); hl.addColorStop(0, "rgba(255,255,255,.85)"); hl.addColorStop(1, "rgba(255,255,255,0)");
    g.fillStyle = hl; g.beginPath(); g.ellipse(x, y - r * .5, r * .7, r * .43, 0, 0, TAU); g.fill();
    g.restore();
    const rim = g.createLinearGradient(0, top, 0, y + r); rim.addColorStop(0, "rgba(255,255,255,.95)"); rim.addColorStop(.5, "rgba(255,255,255,.18)"); rim.addColorStop(1, css(c.glow, .8));
    g.strokeStyle = rim; g.lineWidth = Math.max(1, r * .024); g.beginPath(); g.arc(x, y, r - g.lineWidth / 2, 0, TAU); g.stroke();
    g.restore();
  }

  /* ---- Marina's bowl, Frutiger Aero style: a bright, saturated reef (photo cutouts) in glossy water, caustic light, bubbles, the real sun or moon's shafts ---- */
  const FLOOR = new Image(), onFloor = [];   // concepts/mascot/reef/build.py composes it from three reef photos (assets/CREDITS.md); pages using Orb sit in web/
  FLOOR.onload = () => onFloor.splice(0).forEach(f => f()); FLOOR.src = "assets/marina/floor.webp";
  const vivid = (c, k = .45) => { const m = (c[0] + c[1] + c[2]) / 3; return c.map(v => cl(m + (v - m) * (1 + k), 0, 255)); };   // Aero water: the phase colour, more saturated
  const BUBS = Array.from({ length: 9 }, (_, i) => [((i * .37) % 1) * 1.5 - .75, .1 + ((i * .61) % 1) * .8, .018 + ((i * .29) % 1) * .03]);
  function reef(g, x, y, r, hz, c, lit) {
    const W = v => v * r, bot = y + r, wa = vivid(c.water), wd = vivid(c.waterDeep, .3);
    const wg = g.createLinearGradient(0, hz, 0, bot);
    wg.addColorStop(0, css(vivid(c.horizon, .2))); wg.addColorStop(.1, css(wa)); wg.addColorStop(1, css(wd));
    g.fillStyle = wg; g.fillRect(x - r, hz, r * 2, bot - hz);
    g.save(); g.beginPath(); g.rect(x - r, hz, r * 2, bot - hz); g.clip();
    // caustics: soft bright ripples on the water near the surface
    g.lineWidth = Math.max(.5, W(.008)); g.lineCap = "round";
    for (let k = 0; k < 5; k++) {
      const yy = hz + W(.06 + k * .07); g.strokeStyle = `rgba(255,255,255,${.12 - k * .02})`; g.beginPath();
      for (let s = -1; s <= 1.001; s += .05) { const px = x + s * r, py = yy + W(.018) * Math.sin(s * 11 + k * 1.7); s <= -1 ? g.moveTo(px, py) : g.lineTo(px, py); }
      g.stroke();
    }
    if (lit[2] > .03) for (let i = 0; i < 4; i++) {   // light shafts from where the sun (or moon) is, behind the reef
      const u0 = lit[0] + W(-.16 + i * .1), sp = W(.08 + i * .03), fall = (lit[0] - x) * -.35 + W(-.3 + i * .2);
      const sg = g.createLinearGradient(0, hz, 0, y + r * .8); sg.addColorStop(0, css(lit[1], .3 * lit[2])); sg.addColorStop(1, css(lit[1], 0));
      g.fillStyle = sg; g.beginPath(); g.moveTo(u0, hz); g.lineTo(u0 + W(.05), hz); g.lineTo(u0 + fall + sp, y + r * .8); g.lineTo(u0 + fall - sp * .3, y + r * .8); g.fill();
    }
    if (FLOOR.complete && FLOOR.naturalWidth) {   // the reef fills the lower part and climbs to just under the surface at the back
      const w = W(2.3), h = w * FLOOR.naturalHeight / FLOOR.naturalWidth;
      g.imageSmoothingQuality = "high";   // 900 px shrunk to ~100-300: the default (low) skipped pixels and the reef glittered (redrawn once a minute, so free)
      g.drawImage(FLOOR, x - w / 2, bot + W(.04) - h, w, h);
    }
    const hz2 = g.createLinearGradient(0, hz, 0, bot); hz2.addColorStop(0, css(wa, .12)); hz2.addColorStop(.5, css(wa, .03)); hz2.addColorStop(1, css(wd, 0));
    g.fillStyle = hz2; g.fillRect(x - r, hz, r * 2, bot - hz);   // only a breath of water over the reef: Aero stays crisp
    for (const [u, v, s] of BUBS) {   // glossy bubbles: clear body, bright rim, a white glint
      const bx = x + u * r, by = hz + v * (bot - hz) * .85, br = W(s);
      g.strokeStyle = "rgba(255,255,255,.55)"; g.lineWidth = Math.max(.5, br * .18); g.beginPath(); g.arc(bx, by, br, 0, TAU); g.stroke();
      g.fillStyle = "rgba(255,255,255,.12)"; g.fill();
      g.fillStyle = "rgba(255,255,255,.9)"; g.beginPath(); g.arc(bx - br * .35, by - br * .35, br * .28, 0, TAU); g.fill();
    }
    g.restore();
    g.fillStyle = "rgba(255,255,255,.7)"; g.fillRect(x - r, hz - .4, r * 2, Math.max(.8, r * .022));   // the underside of the surface, bright
  }
  function glass(g, x, y, r, c, hz, st) {   // over Marina: night darkens the water, then the Crystal gloss and rim
    const top = y - r, night = cl((2 - st.sun.elev) / 14) * .42;   // night dims the bowl but the reef keeps some colour
    g.save(); g.beginPath(); g.arc(x, y, r, 0, TAU); g.clip();
    if (night > .01) { g.fillStyle = css(c.waterDeep, night); g.fillRect(x - r, hz, r * 2, y + r - hz); }
    const dep = g.createRadialGradient(x, y, r * .62, x, y, r); dep.addColorStop(0, "rgba(0,20,40,0)"); dep.addColorStop(1, "rgba(0,20,40,.38)");
    g.fillStyle = dep; g.fillRect(x - r, top, r * 2, r * 2);
    const up = g.createRadialGradient(x, y + r * 1.05, 0, x, y + r * 1.05, r * .9); up.addColorStop(0, "rgba(160,240,255,.14)"); up.addColorStop(1, "rgba(160,240,255,0)");
    g.fillStyle = up; g.fillRect(x - r, y, r * 2, r);   // Aero glow welling up from the bottom of the glass
    const hl = g.createLinearGradient(0, top + r * .04, 0, y - r * .2); hl.addColorStop(0, "rgba(255,255,255,.88)"); hl.addColorStop(1, "rgba(255,255,255,0)");
    g.fillStyle = hl; g.beginPath(); g.ellipse(x, y - r * .58, r * .66, r * .36, 0, 0, TAU); g.fill();
    g.fillStyle = "rgba(255,255,255,.3)"; g.beginPath(); g.moveTo(x + r * .52, y - r * .62); g.quadraticCurveTo(x + r * .8, y - r * .35, x + r * .86, y - r * .05);
    g.lineTo(x + r * .76, y - r * .06); g.quadraticCurveTo(x + r * .7, y - r * .34, x + r * .46, y - r * .55); g.fill();   // a window reflection on the right
    g.restore();
    const rim = g.createLinearGradient(0, top, 0, y + r); rim.addColorStop(0, "rgba(255,255,255,.95)"); rim.addColorStop(.5, "rgba(255,255,255,.18)"); rim.addColorStop(1, css(c.glow, .8));
    g.strokeStyle = rim; g.lineWidth = Math.max(1, r * .024); g.beginPath(); g.arc(x, y, r - g.lineWidth / 2, 0, TAU); g.stroke();
  }
  // Marina: the same goldfish as the voice overlay (overlay.html), ids prefixed m-; ui.css .mswim swims her round the bowl
  // Marina is a real goldfish: photo layers cut by concepts/mascot/photo/build.py (public-domain photo, see assets/CREDITS.md). Tail swings at the peduncle; the mouth gulps while she talks.
  const FISH = `<svg viewBox="6 16 42 32" class="mfish"><defs><radialGradient id="m-gulp"><stop offset="0" stop-color="#1A0402"/><stop offset=".6" stop-color="#3A0C04"/><stop offset="1" stop-color="#9A2A10" stop-opacity="0"/></radialGradient></defs><g class="fx"><g class="fb"><g class="tail"><image href="assets/marina/tail.webp" x="6.2" y="21.76" width="14.5" height="15.82"/></g><image class="mbody" href="assets/marina/body.webp" x="6" y="21.43" width="42" height="21.13"/><g class="talk"><ellipse class="mouth" cx="47.35" cy="30.4" rx=".55" ry=".7" fill="url(#m-gulp)"/></g></g></g></svg>`;

  function mount(el, { size = 64, now = () => new Date(), lat, lon, home = false } = {}) {
    const d = devicePixelRatio || 1, pad = size * .45;   // room for the halo
    const mk = () => { const cv = document.createElement("canvas"); cv.className = "orbc"; cv.width = cv.height = Math.round((size + pad * 2) * d);
      cv.style.cssText = `width:${size + pad * 2}px;height:${size + pad * 2}px;margin:${-pad}px;display:block;pointer-events:none` + (home ? ";position:absolute;left:0;top:0" : ""); return cv; };
    const cv = mk(), fr = home && mk();
    if (home) { const m = document.createElement("div"); m.className = "mswim"; m.innerHTML = `<div class="mbob">${FISH}</div>`; const bubs = [1, 2, 3].map(() => Object.assign(document.createElement("i"), { className: "mbub" })); el.replaceChildren(cv, ...bubs, m, fr); el.classList.add("mhome"); }
    else el.replaceChildren(cv);
    const g = cv.getContext("2d"), redraw = (date = now()) => {
      const st = sky(date, lat, lon), o = size / 2 + pad;
      for (const [k, L] of home ? [[g, "back"], [fr.getContext("2d"), "front"]] : [[g]]) { k.setTransform(d, 0, 0, d, 0, 0); k.clearRect(0, 0, cv.width, cv.height); draw(k, o, o, size / 2, st, date / 1000, L); }
    };
    redraw(); if (home && !FLOOR.complete) onFloor.push(() => redraw()); setInterval(() => redraw(), 60000);   // live, but cheap: the sky moves once a minute, nothing animates in between
    return redraw;
  }

  window.Orb = { draw, sky, moonAge, moonAt, mount, look };
})();
