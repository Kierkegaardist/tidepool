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
  function sky(date = new Date(), lat = 59.91, lon = 10.75) {
    const sun = sunAt(date, lat, lon), { sR, sS, w } = phaseCoord(sun);
    const span = sun.Hs > 5 && sun.Hs < 175 ? sun.Hs : 90, xOf = H => cl(H / span, -1, 1);
    const mH = ((sun.H + 360) % 360) - 180;   // ponytail: moon placed opposite the sun like core.js; its phase is real, its path is not
    return { s: w > .5 ? sS : sR, sun: { elev: sun.elev, x: xOf(sun.H) }, moon: { age: moonAge(date), elev: -sun.elev * .8, x: xOf(mH) } };
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

  /* ---- Marina's bowl: the sea seen from inside (water, light shafts from the real sun or moon, a coral reef), then the glass over her ---- */
  function reef(g, x, y, r, hz, c, lit) {
    const P = (u, v) => [x + u * r, y + v * r], W = v => v * r;
    let sd = 5; const rn = () => (sd = (sd * 16807) % 2147483647) / 2147483647;   // fixed seed: the same reef every redraw
    const wg = g.createLinearGradient(0, hz, 0, y + r);
    wg.addColorStop(0, css(c.horizon)); wg.addColorStop(.12, css(c.water)); wg.addColorStop(1, css(c.waterDeep));
    g.fillStyle = wg; g.fillRect(x - r, hz, r * 2, y + r - hz);
    g.fillStyle = "rgba(255,255,255,.55)"; g.fillRect(x - r, hz - .4, r * 2, Math.max(.8, r * .02));   // the underside of the surface
    if (lit[2] > .03) for (let i = 0; i < 4; i++) {   // light shafts fanning down from where the sun (or moon) is
      const u0 = lit[0] + W(-.16 + i * .1), sp = W(.08 + i * .03), fall = (lit[0] - x) * -.35 + W(-.3 + i * .2);
      const sg = g.createLinearGradient(0, hz, 0, y + r * .8); sg.addColorStop(0, css(lit[1], .22 * lit[2])); sg.addColorStop(1, css(lit[1], 0));
      g.fillStyle = sg; g.beginPath(); g.moveTo(u0, hz); g.lineTo(u0 + W(.05), hz); g.lineTo(u0 + fall + sp, y + r * .8); g.lineTo(u0 + fall - sp * .3, y + r * .8); g.fill();
    }
    g.save(); g.translate(x, y + r); g.scale(1.5, 1.5); g.translate(-x, -(y + r));   // the reef drawn at 1.5x from the bottom: fills the lower half and runs up the glass
    g.lineCap = "round";
    const branch = (px, py, a, len, w, n, col, tip) => {   // branching coral / sea fan: tapering forks, light tips
      const qx = px + Math.cos(a) * len, qy = py + Math.sin(a) * len;
      g.strokeStyle = col; g.lineWidth = w; g.beginPath(); g.moveTo(px, py); g.quadraticCurveTo(px + Math.cos(a + .3) * len * .5, py + Math.sin(a + .3) * len * .5, qx, qy); g.stroke();
      if (n) for (let k = 0, m = 2 + (rn() > .6); k < m; k++) branch(qx, qy, a + (k - (m - 1) / 2) * (.45 + rn() * .3), len * (.62 + rn() * .2), w * .72, n - 1, col, tip);
      else { g.fillStyle = tip; g.beginPath(); g.arc(qx, qy, w * .75, 0, TAU); g.fill(); }
    };
    const dome = (u, v, rx, ry, c0, c1, line) => {   // brain / boulder coral: a lit dome with meandering grooves
      const [cx, cy] = P(u, v), dg = g.createRadialGradient(cx - W(rx) * .35, cy - W(ry) * .6, 0, cx, cy, W(rx) * 1.1);
      dg.addColorStop(0, c0); dg.addColorStop(1, c1); g.fillStyle = dg;
      g.beginPath(); g.ellipse(cx, cy, W(rx), W(ry), 0, Math.PI, 0); g.closePath(); g.fill();
      g.save(); g.clip(); g.strokeStyle = line; g.lineWidth = Math.max(.6, W(.012));
      for (let k = 0; k < 5; k++) { g.beginPath(); const yy = cy - W(ry) * (.15 + k * .17); g.moveTo(cx - W(rx), yy); for (let s = -1; s <= 1.01; s += .25) g.lineTo(cx + W(rx) * s, yy + W(.018) * Math.sin(s * 9 + k * 2)); g.stroke(); }
      g.restore();
    };
    // back row: sea fan, kelp, a tall orange branch
    branch(...P(-.66, .78), -1.75, W(.16), W(.022), 3, "rgba(150,96,196,.8)", "rgba(214,170,240,.9)");
    for (const [u, h, b] of [[.72, .5, -.08], [.8, .4, .12], [.64, .36, -.2]]) {
      const [bx, by] = P(u, .8); g.strokeStyle = "#3E8F4C"; g.lineWidth = W(.04);
      g.beginPath(); g.moveTo(bx, by); g.bezierCurveTo(bx + W(b), by - W(h) * .4, bx - W(b), by - W(h) * .7, bx + W(b) * .6, by - W(h)); g.stroke();
    }
    branch(...P(.4, .76), -1.5, W(.12), W(.035), 2, "#E0603A", "#FFC09A");
    branch(...P(.18, .8), -1.65, W(.11), W(.03), 3, "rgba(120,84,190,.85)", "rgba(200,170,255,.9)");
    branch(...P(-.16, .82), -1.45, W(.1), W(.03), 2, "#D9483A", "#FFB09A");
    branch(...P(.56, .82), -1.8, W(.09), W(.026), 2, "#E8B83A", "#FFF0A0");
    // the rock the reef grows on, and sand in front
    for (const [u, v, a, b] of [[-.5, .86, .36, .14], [.42, .88, .4, .15], [-.02, .9, .3, .1]]) {
      const [cx, cy] = P(u, v), rg = g.createRadialGradient(cx - W(a) * .3, cy - W(b) * .8, 0, cx, cy, W(a));
      rg.addColorStop(0, "#8A7470"); rg.addColorStop(1, "#3B2C34"); g.fillStyle = rg; g.beginPath(); g.ellipse(cx, cy, W(a), W(b), 0, 0, TAU); g.fill();
    }
    const sg = g.createLinearGradient(0, y + r * .86, 0, y + r); sg.addColorStop(0, "#E4D2A4"); sg.addColorStop(1, "#9C7F52");
    g.fillStyle = sg; g.beginPath(); g.ellipse(x, y + r * 1.04, r, r * .16, 0, 0, TAU); g.fill();
    // front row: domes, tube sponges, a small pink branch, and the anemone (Marina's home)
    dome(.12, .88, .15, .13, "#A9CF6A", "#4E7A2A", "rgba(40,70,20,.45)");
    dome(.5, .9, .11, .09, "#F0C46A", "#A8682A", "rgba(110,60,20,.45)");
    dome(-.72, .9, .1, .08, "#8FA8E0", "#3E5590", "rgba(30,40,90,.45)");
    for (const [u, h, col] of [[.27, .16, "#9A5CC8"], [.31, .11, "#B47CE0"], [.86, .12, "#E8C23A"]]) {
      const [bx, by] = P(u, .9); g.strokeStyle = col; g.lineWidth = W(.045); g.beginPath(); g.moveTo(bx, by); g.lineTo(bx + W(.01), by - W(h)); g.stroke();
      g.fillStyle = "rgba(30,10,40,.55)"; g.beginPath(); g.ellipse(bx + W(.01), by - W(h), W(.016), W(.008), 0, 0, TAU); g.fill();
    }
    branch(...P(-.05, .84), -1.35, W(.08), W(.022), 2, "#E58BB5", "#FFD0E4");
    const [ax, ay] = P(-.3, .8);
    g.fillStyle = "#B88A90"; g.beginPath(); g.ellipse(ax, ay, W(.1), W(.05), 0, 0, TAU); g.fill();
    for (let k = 0; k < 30; k++) {   // anemone tentacles: long, curling out and up
      const a = -Math.PI / 2 + (k / 29 - .5) * 2.6 + (rn() - .5) * .2, L = W(.17 + rn() * .1), bx = ax + Math.cos(a) * W(.05), by = ay - W(.01);
      const ex = bx + Math.cos(a) * L, ey = by + Math.sin(a) * L * .9, cx2 = bx + Math.cos(a + .5) * L * .6, cy2 = by + Math.sin(a + .5) * L * .6;
      const tg = g.createLinearGradient(bx, by, ex, ey); tg.addColorStop(0, "#C99AA2"); tg.addColorStop(.7, "#F1DDB6"); tg.addColorStop(1, "#FFF3D8");
      g.strokeStyle = tg; g.lineWidth = W(.026); g.beginPath(); g.moveTo(bx, by); g.quadraticCurveTo(cx2, cy2, ex, ey); g.stroke();
    }
    for (let k = 0; k < 14; k++) { const [px, py] = P(-.8 + rn() * 1.6, .95 + rn() * .04); g.fillStyle = ["#8C7B6A", "#C9B79A", "#6F6A63", "#D8CBB4"][k % 4]; g.beginPath(); g.ellipse(px, py, W(.025), W(.015), 0, 0, TAU); g.fill(); }
    g.restore();
    g.fillStyle = css(c.water, .12); g.fillRect(x - r, hz, r * 2, y + r - hz);   // a little water over everything, for depth
  }
  function glass(g, x, y, r, c, hz, st) {   // over Marina: night darkens the water, then the Crystal gloss and rim
    const top = y - r, night = cl((2 - st.sun.elev) / 14) * .55;
    g.save(); g.beginPath(); g.arc(x, y, r, 0, TAU); g.clip();
    if (night > .01) { g.fillStyle = css(c.waterDeep, night); g.fillRect(x - r, hz, r * 2, y + r - hz); }
    const dep = g.createRadialGradient(x, y, r * .62, x, y, r); dep.addColorStop(0, "rgba(0,20,40,0)"); dep.addColorStop(1, "rgba(0,20,40,.38)");
    g.fillStyle = dep; g.fillRect(x - r, top, r * 2, r * 2);
    const hl = g.createLinearGradient(0, top + r * .04, 0, y - r * .2); hl.addColorStop(0, "rgba(255,255,255,.7)"); hl.addColorStop(1, "rgba(255,255,255,0)");
    g.fillStyle = hl; g.beginPath(); g.ellipse(x, y - r * .58, r * .66, r * .36, 0, 0, TAU); g.fill();
    g.fillStyle = "rgba(255,255,255,.3)"; g.beginPath(); g.moveTo(x + r * .52, y - r * .62); g.quadraticCurveTo(x + r * .8, y - r * .35, x + r * .86, y - r * .05);
    g.lineTo(x + r * .76, y - r * .06); g.quadraticCurveTo(x + r * .7, y - r * .34, x + r * .46, y - r * .55); g.fill();   // a window reflection on the right
    g.restore();
    const rim = g.createLinearGradient(0, top, 0, y + r); rim.addColorStop(0, "rgba(255,255,255,.95)"); rim.addColorStop(.5, "rgba(255,255,255,.18)"); rim.addColorStop(1, css(c.glow, .8));
    g.strokeStyle = rim; g.lineWidth = Math.max(1, r * .024); g.beginPath(); g.arc(x, y, r - g.lineWidth / 2, 0, TAU); g.stroke();
  }
  // Marina: the same goldfish as the voice overlay (overlay.html), ids prefixed m-; ui.css .mswim swims her round the bowl
  const FISH = `<svg viewBox="6 16 42 32" class="mfish"><defs><path id="m-bd" d="M46 34C45.6 31.6 43.6 28.4 40.4 26.4 37.6 24.6 35.4 23.6 32.6 23.6c-5 0-8.8 3.6-11.4 7.8l-.4 3.2c2.4 4.8 6.8 7.8 11.6 7.8 4.2 0 7.6-1.4 10.2-3.8 1.6-1.4 2.8-2.8 3.4-4.6z"/><clipPath id="m-bodyc"><use href="#m-bd"/></clipPath><linearGradient id="m-skin" x1="0" y1="0" x2=".15" y2="1"><stop offset="0" stop-color="#A82A0C"/><stop offset=".28" stop-color="#DC5418"/><stop offset=".55" stop-color="#F28C2A"/><stop offset=".8" stop-color="#FDBA55"/><stop offset="1" stop-color="#FFE2A6"/></linearGradient><radialGradient id="m-sheen" cx="58%" cy="40%" r="50%"><stop offset="0" stop-color="#FFF4D6" stop-opacity=".55"/><stop offset=".5" stop-color="#FFE0A0" stop-opacity=".18"/><stop offset="1" stop-color="#FFE0A0" stop-opacity="0"/></radialGradient><pattern id="m-scl" width="2" height="1.5" patternUnits="userSpaceOnUse" patternTransform="rotate(-6)"> <path d="M0 1.5Q1 .1 2 1.5M-1 .75Q0 -.65 1 .75M1 .75Q2 -.65 3 .75" stroke="#7A2006" stroke-opacity=".2" stroke-width=".2" fill="none"/> <path d="M.3 1.2Q1 .5 1.7 1.2" stroke="#FFE6B0" stroke-opacity=".22" stroke-width=".14" fill="none"/> </pattern><linearGradient id="m-fin" x1="1" y1="0" x2="0" y2="0"><stop offset="0" stop-color="#E8661F" stop-opacity=".9"/><stop offset=".5" stop-color="#F6943E" stop-opacity=".55"/><stop offset="1" stop-color="#FFD3A0" stop-opacity=".18"/></linearGradient><linearGradient id="m-finv" x1="0" y1="1" x2="0" y2="0"><stop offset="0" stop-color="#D9561A" stop-opacity=".9"/><stop offset="1" stop-color="#FFC890" stop-opacity=".22"/></linearGradient><linearGradient id="m-finp" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#EE7A30" stop-opacity=".85"/><stop offset="1" stop-color="#FFD0A0" stop-opacity=".2"/></linearGradient><radialGradient id="m-iris" cx="45%" cy="40%" r="60%"><stop offset="0" stop-color="#F9DE8E"/><stop offset=".6" stop-color="#D79A35"/><stop offset="1" stop-color="#6E3A0E"/></radialGradient><linearGradient id="m-tshade" x1="0" y1="0" x2="1" y2="0"><stop offset="0" stop-color="#7A1E04" stop-opacity=".45"/><stop offset=".3" stop-color="#7A1E04" stop-opacity="0"/><stop offset=".85" stop-color="#7A1E04" stop-opacity="0"/><stop offset="1" stop-color="#7A1E04" stop-opacity=".2"/></linearGradient></defs><g class="fx"><g class="fb"> <g class="tail"> <path d="M21.2 32.2C18.4 29.6 15.2 25.6 10.6 22c-1-.7-2.2-.6-3-.2 1.2 1.6 1 3.2 2 4.6-.8 1.4.4 3.2-.2 4.8 3.2 1.6 7.2 2 11.6 2.2z" fill="url(#m-fin)"/> <path d="M21 33.4c-4.4.2-8.4.8-11.4 2.2.6 1.6-.6 3.4.2 5-1 1.6-.6 3.2-2 4.8 1.2.4 2.6.2 3.6-.6 4.2-3.4 7.2-7.2 9.8-10.2z" fill="url(#m-fin)"/> <path d="M20.6 32.8L8.4 22.4M20.6 33l-10.8-6.4M20.6 33.1L9.6 30.6M20.6 33.6L9.8 35.8M20.6 33.8l-10.6 6.6M20.6 34L8.6 45" stroke="#B9420F" stroke-opacity=".35" stroke-width=".22" stroke-linecap="round"/> <path d="M7.6 21.8c1.2 1.6 1 3.2 2 4.6-.8 1.4.4 3.2-.2 4.8M9.6 35.6c.6 1.6-.6 3.4.2 5-1 1.6-.6 3.2-2 4.8" stroke="#FFE6C8" stroke-opacity=".4" stroke-width=".3" fill="none"/> </g> <g class="dor"> <path d="M26.4 26c.6-3.6 2.4-6.8 5.2-8.4.6 1.8 2.2 3 3 4 1.2 1 2.8 2 3.8 3.2-3.8-1.2-8.2-.6-12 1.2z" fill="url(#m-finv)"/> <path d="M28 25.2l3.2-7M30 24.4l2.6-5.4M32 24l2.4-3.4M34 23.9l2.4-1.6" stroke="#A8380C" stroke-opacity=".35" stroke-width=".2" stroke-linecap="round"/> </g> <path d="M24.6 39.6c-1.4 1.8-3.2 3-5 3.4 2.8.8 5.6 0 7.6-1.8z" fill="url(#m-finp)"/> <path d="M31.6 42c-.8 2-2.2 3.6-3.8 4.4 2.6.2 4.8-1 6-3z" fill="url(#m-finp)"/> <use href="#m-bd" fill="url(#m-skin)"/> <path d="M20 20h19.6c-2.6 4-3 13.6-.4 24H20z" fill="url(#m-scl)" clip-path="url(#m-bodyc)"/> <use href="#m-bd" fill="url(#m-sheen)"/> <use href="#m-bd" fill="url(#m-tshade)"/> <path d="M23 29.4c2.8-3.4 6.2-5 9.6-5 3 0 5.6 1 7.8 2.6-2.6-1-5-1.4-7.8-1.2-3.4.2-6.6 1.6-9.6 3.6z" fill="#7E1C04" opacity=".35"/> <path d="M23.2 37.8c2.6 2.6 5.8 3.8 9.2 3.8 4 0 7.6-1.6 10.2-4.2" stroke="#FFF0C8" stroke-opacity=".45" stroke-width=".5" fill="none" stroke-linecap="round"/> <path d="M24.4 32.4c4.4.6 9 .5 13.4-.4" stroke="#7A2006" stroke-opacity=".22" stroke-width=".3" stroke-dasharray=".35 .5" fill="none"/> <path d="M39.6 27.4c-2 3.4-2 9.2-.2 12.6" stroke="#8A2A0A" stroke-opacity=".32" stroke-width=".4" fill="none" stroke-linecap="round"/> <path d="M40.2 27.8c-1.7 3.2-1.7 8.6 0 11.8" stroke="#FFE0A8" stroke-opacity=".25" stroke-width=".3" fill="none"/> <path class="pec" d="M36.4 35.4c-2 1-3.8 3.2-4 5.2 2-.4 4-2.2 5-4.2z" fill="url(#m-finp)"/> <g class="eye"> <circle cx="41.4" cy="30.4" r="1.75" fill="#3A1A08" opacity=".55"/> <circle cx="41.4" cy="30.4" r="1.5" fill="url(#m-iris)"/> <circle cx="41.5" cy="30.5" r=".82" fill="#06070A"/> <circle cx="41.1" cy="30" r=".26" fill="#fff" opacity=".9"/><circle cx="41.95" cy="31" r=".12" fill="#fff" opacity=".5"/> </g> <circle cx="44" cy="31" r=".26" fill="#6A1C06" opacity=".6"/> <path class="smile" d="M45 34.45l1-.3" stroke="#5A1806" stroke-width=".35" fill="none" stroke-linecap="round"/> <g class="talk"><ellipse class="mouth" cx="45.8" cy="34.1" rx=".7" ry=".9" fill="#3A0E04" stroke="#F49A5A" stroke-width=".35"/></g> </g></g></svg>`;

  function mount(el, { size = 64, now = () => new Date(), lat, lon, home = false } = {}) {
    const d = devicePixelRatio || 1, pad = size * .45;   // room for the halo
    const mk = () => { const cv = document.createElement("canvas"); cv.className = "orbc"; cv.width = cv.height = Math.round((size + pad * 2) * d);
      cv.style.cssText = `width:${size + pad * 2}px;height:${size + pad * 2}px;margin:${-pad}px;display:block;pointer-events:none` + (home ? ";position:absolute;left:0;top:0" : ""); return cv; };
    const cv = mk(), fr = home && mk();
    if (home) { const m = document.createElement("div"); m.className = "mswim"; m.innerHTML = `<div class="mbob">${FISH}</div>`; el.replaceChildren(cv, m, fr); el.classList.add("mhome"); }
    else el.replaceChildren(cv);
    const g = cv.getContext("2d"), redraw = (date = now()) => {
      const st = sky(date, lat, lon), o = size / 2 + pad;
      for (const [k, L] of home ? [[g, "back"], [fr.getContext("2d"), "front"]] : [[g]]) { k.setTransform(d, 0, 0, d, 0, 0); k.clearRect(0, 0, cv.width, cv.height); draw(k, o, o, size / 2, st, date / 1000, L); }
    };
    redraw(); setInterval(() => redraw(), 60000);   // live, but cheap: the sky moves once a minute, nothing animates in between
    return redraw;
  }

  window.Orb = { draw, sky, moonAge, mount, look };
})();
