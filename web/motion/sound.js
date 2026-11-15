/* sound.js: the Tidepool UI sound set (track C, polish phase).
   Every sound is synthesised live with WebAudio: sine/triangle oscillators, filtered noise and a reverb whose
   impulse is generated in code. No samples, no third-party audio, nothing AI-made.

   Signature: struck glass (inharmonic bell partials) + water droplets (sine "plips" whose pitch rises, as a
   real bubble's does), all in E major pentatonic, with a watery reverb. The boot motif E5 B5 G#5 C#6 is the
   product's sonic logo; select quotes its first two notes, notify its last two, shutdown plays it backwards.

   API (window.Sound):
     Sound.play(name, opts)   names in Sound.names; opts: {axis:"x"|"y", i:index} for move, {on:bool} for toggle, {pan:-1..1}
     Sound.volume(v)          0..1, persisted; no argument = read
     Sound.mute(on)           persisted; no argument = read
     Sound.context({gaming, guide, app, quiet, night})   silence and timbre rules, see motion.md part 3 (guide = the guide blade is open over a game)
     Sound.ctx()              the AudioContext (boot.html syncs visuals to it)
*/
(() => {
  const PAUSE_APPS = ["FL64.exe", "FL.exe"];   // foreground apps that silence the UI (spec part 4: desktop.json pause list)
  const mtof = m => 440 * 2 ** ((m - 69) / 12);
  const PENTA = [76, 78, 80, 83, 85, 88, 90, 92, 95];   // E5 F#5 G#5 B5 C#6 E6 F#6 G#6 B6
  const MOTIF = [76, 83, 80, 85];                      // E5 B5 G#5 C#6
  const saved = (() => { try { return JSON.parse(localStorage.getItem("tidepool.sound")) || {}; } catch { return {}; } })();
  const st = { vol: saved.vol ?? 0.6, muted: !!saved.muted, gaming: false, guide: false, app: "", quiet: false, night: false };
  const save = () => { try { localStorage.setItem("tidepool.sound", JSON.stringify({ vol: st.vol, muted: st.muted })); } catch {} };
  let ctx, out, bus, tone, wet, noise;
  const last = {};

  function init() {
    if (ctx) return ctx;
    ctx = new (window.AudioContext || window.webkitAudioContext)();
    const comp = ctx.createDynamicsCompressor();          // glue + safety: stacked sounds never clip
    comp.threshold.value = -14; comp.ratio.value = 4; comp.attack.value = .003; comp.release.value = .2;
    out = ctx.createGain(); tone = ctx.createBiquadFilter(); bus = ctx.createGain();
    tone.type = "lowpass"; tone.frequency.value = 18000;
    bus.connect(tone); tone.connect(out); out.connect(comp); comp.connect(ctx.destination);
    // reverb: 2.8 s stereo impulse of decaying noise, high-passed so the tail stays glassy, not muddy
    const len = ctx.sampleRate * 2.8, ir = ctx.createBuffer(2, len, ctx.sampleRate);
    for (let c = 0; c < 2; c++) { const d = ir.getChannelData(c); for (let i = 0; i < len; i++) d[i] = (Math.random() * 2 - 1) * (1 - i / len) ** 3.2; }
    const conv = ctx.createConvolver(), hp = ctx.createBiquadFilter();
    conv.buffer = ir; hp.type = "highpass"; hp.frequency.value = 350;
    wet = ctx.createGain(); wet.gain.value = .55; wet.connect(hp); hp.connect(conv); conv.connect(tone);
    noise = ctx.createBuffer(1, ctx.sampleRate * 2, ctx.sampleRate);
    const nd = noise.getChannelData(0); for (let i = 0; i < nd.length; i++) nd[i] = Math.random() * 2 - 1;
    apply();
    return ctx;
  }
  function apply() {
    if (!ctx) return;
    out.gain.setTargetAtTime(st.vol * (st.night ? .7 : 1), ctx.currentTime, .05);
    tone.frequency.setTargetAtTime(st.night ? 3800 : 18000, ctx.currentTime, .3);   // night: softer, darker glass
  }

  /* ---- building blocks ---- */
  // a voice strip: input gain -> panner -> dry bus (+ reverb send)
  function strip(pan = 0, send = .3) {
    const g = ctx.createGain(), p = ctx.createStereoPanner();
    p.pan.value = Math.max(-1, Math.min(1, pan)); g.connect(p); p.connect(bus);
    if (send) { const s = ctx.createGain(); s.gain.value = send; p.connect(s); s.connect(wet); }
    return { in: g, pan: p.pan };
  }
  function env(param, t, a, peak, d) { param.setValueAtTime(.0001, t); param.linearRampToValueAtTime(peak, t + a); param.exponentialRampToValueAtTime(.0001, t + a + d); }
  function osc(type, f, t, dur, dest) { const o = ctx.createOscillator(); o.type = type; o.frequency.setValueAtTime(f, t); o.connect(dest); o.start(t); o.stop(t + dur + .05); return o; }

  // struck glass: partial ratios of a free bar, upper partials die first
  function bell(t, m, { gain = .15, dur = 1.2, pan = 0, send = .35, bright = 1 } = {}) {
    const f = mtof(m), s = strip(pan, send);
    for (const [r, a, k] of [[1, 1, 1], [2.756, .42 * bright, .5], [5.404, .2 * bright, .28], [8.933, .08 * bright, .16]]) {
      if (f * r > 15000) continue;
      const g = ctx.createGain(); g.connect(s.in); env(g.gain, t, .003, gain * a, dur * k); osc("sine", f * r, t, dur * k, g);
    }
  }
  // water droplet: a sine whose pitch glides (up = plip, down = plop)
  function drop(t, f0, f1, { gain = .2, dur = .08, pan = 0, send = .3 } = {}) {
    const s = strip(pan, send), g = ctx.createGain(); g.connect(s.in); env(g.gain, t, .002, gain, dur);
    osc("sine", f0, t, dur, g).frequency.exponentialRampToValueAtTime(f1, t + dur * .85);
  }
  // swish: band-passed noise sweeping f0 -> f1, optionally panning p0 -> p1
  function swish(t, f0, f1, { gain = .1, dur = .3, q = 1.2, p0 = 0, p1 = p0, send = .25 } = {}) {
    const s = strip(p0, send), src = ctx.createBufferSource(), bp = ctx.createBiquadFilter(), g = ctx.createGain();
    src.buffer = noise; bp.type = "bandpass"; bp.Q.value = q;
    bp.frequency.setValueAtTime(f0, t); bp.frequency.exponentialRampToValueAtTime(f1, t + dur);
    g.gain.setValueAtTime(.0001, t); g.gain.linearRampToValueAtTime(gain, t + dur * .45); g.gain.exponentialRampToValueAtTime(.0001, t + dur);
    s.pan.setValueAtTime(p0, t); s.pan.linearRampToValueAtTime(p1, t + dur);
    src.connect(bp); bp.connect(g); g.connect(s.in); src.start(t, Math.random()); src.stop(t + dur + .05);
  }
  // pad: detuned saw/triangle pairs under an opening low-pass (boot and shutdown only)
  function pad(t, notes, { gain = .05, a = 1.5, hold = 1.5, r = 2.5, c0 = 250, c1 = 2800 } = {}) {
    const lp = ctx.createBiquadFilter(), g = ctx.createGain(), s = strip(0, .7), end = a + hold + r;
    lp.type = "lowpass"; lp.Q.value = .8; lp.frequency.setValueAtTime(c0, t); lp.frequency.exponentialRampToValueAtTime(c1, t + a + hold * .5);
    g.gain.setValueAtTime(.0001, t); g.gain.linearRampToValueAtTime(gain, t + a); g.gain.setValueAtTime(gain, t + a + hold); g.gain.exponentialRampToValueAtTime(.0001, t + end);
    lp.connect(g); g.connect(s.in);
    notes.forEach((m, i) => [-7, 7].forEach(c => { osc(i % 2 ? "triangle" : "sawtooth", mtof(m), t, end, lp).detune.value = c; }));
  }
  // a rising (or falling) stream of bubbles
  function bubbles(t, n, span, f0, f1, gain = .07) {
    for (let i = 0; i < n; i++) {
      const k = i / n, f = f0 * (f1 / f0) ** k * (.85 + Math.random() * .3);
      drop(t + k * span + Math.random() * .05, f, f * 1.7, { gain: gain * (.6 + Math.random() * .6), dur: .04 + Math.random() * .04, pan: Math.random() * 1.4 - .7, send: .5 });
    }
  }

  /* ---- the set: every UI sound. t = start time; keep these short, the reverb carries the tail ---- */
  const SOUNDS = {
    // rail (y) = rounder and an octave down; carousel (x) = a high plip that climbs with the item index
    move: (t, { axis = "x", i = 0, rep = 1 } = {}) => {
      const m = PENTA[Math.max(0, Math.min(i, PENTA.length - 1))] - (axis === "y" ? 12 : 0), f = mtof(m);   // i = -1 on Home (the orb is not a slab)
      drop(t, f, f * (axis === "y" ? 1.3 : 1.55), { gain: .22 * rep, dur: axis === "y" ? .09 : .06, send: .2 });
      bell(t, m + 12, { gain: .04 * rep, dur: .3, send: .15 });
    },
    select: t => { drop(t, 900, 1700, { gain: .16, dur: .07 }); bell(t + .015, MOTIF[0], { gain: .14, dur: 1.1 }); bell(t + .05, MOTIF[1], { gain: .11, dur: 1.1, pan: .2 }); bell(t + .05, 95, { gain: .025, dur: .6 }); },
    back: t => { drop(t, 1400, 650, { gain: .2, dur: .09 }); bell(t + .02, 71, { gain: .13, dur: .6, pan: -.15 }); },
    open: t => { swish(t, 400, 3500, { gain: .08, dur: .32 }); [76, 80, 83].forEach((m, k) => bell(t + .08 + k * .07, m, { gain: .065, dur: .8, pan: .25 })); },
    close: t => { swish(t, 3000, 350, { gain: .07, dur: .28 }); bell(t + .04, 83, { gain: .055, dur: .5 }); bell(t + .11, 76, { gain: .065, dur: .6 }); },
    toggle: (t, { on = true } = {}) => { const [a, b] = on ? [80, 85] : [85, 80]; drop(t, 700, on ? 1300 : 500, { gain: .14, dur: .05 }); bell(t, a, { gain: .08, dur: .35, send: .15 }); bell(t + .06, b, { gain: .08, dur: .4, send: .15 }); },
    // the notification centre sits on the right, so notify comes from the right
    notify: t => { drop(t, 1200, 2100, { gain: .09, dur: .06, pan: .35 }); bell(t + .02, MOTIF[2], { gain: .14, dur: 1.4, pan: .35 }); bell(t + .2, MOTIF[3], { gain: .14, dur: 1.9, pan: .35 }); },
    // error: glass under water. Low, muffled, out of the scale; never a buzzer
    error: t => { drop(t, 320, 180, { gain: .16, dur: .1, send: .1 }); bell(t, 58, { gain: .14, dur: .35, bright: .25, send: .1 }); bell(t + .13, 57, { gain: .14, dur: .45, bright: .25, send: .1 }); },
    edge: t => { drop(t, 260, 190, { gain: .24, dur: .06, send: .1 }); },
    // "Hey Marina": a soft water-drop bloop, a glint of glass, a few bubbles rising (concepts/mascot/CONCEPT.md)
    marina: t => { drop(t, 330, 820, { gain: .22, dur: .13, send: .45 }); drop(t + .1, 560, 1300, { gain: .12, dur: .08, send: .5 }); bell(t + .12, 88, { gain: .045, dur: 1.1 }); bubbles(t + .18, 4, .4, 1000, 2000, .035); },
    wake: t => { swish(t, 600, 4500, { gain: .05, dur: .6, send: .5 }); bell(t + .1, 88, { gain: .05, dur: 1.2 }); bell(t + .25, 95, { gain: .03, dur: 1.2 }); },
    // Claude's change lands: a sweep left to right with sparkles riding it (matches the accent sweep)
    sweep: t => { swish(t, 2000, 7000, { gain: .06, dur: 1.3, q: 4, p0: -1, p1: 1, send: .4 }); [88, 92, 95, 100].forEach((m, k) => bell(t + .2 + k * .25, m, { gain: .035, dur: .8, pan: -.8 + k * .5 })); },
    // game start: one swell, then hush (the game owns the speakers from here)
    game: t => { swish(t, 200, 5000, { gain: .09, dur: .75, q: .7, send: .5 }); [76, 83, 88].forEach(m => bell(t + .05, m, { gain: .06, dur: 1.4 })); drop(t + .7, 500, 120, { gain: .14, dur: .25, send: .5 }); },
    // the guide blade (critique move 5): one short glass "shing" from the left, where the blade comes from.
    // A swish that pans in with the blade, then E6 and B6 (the motif's opening fifth, an octave up) and a high sparkle. 0.6 s.
    // {on:false} = the blade going away: the swish back out, no bells.
    guide: (t, { on = true } = {}) => {
      if (!on) return swish(t, 2600, 500, { gain: .05, dur: .22, p0: -.2, p1: -.9, send: .2 });
      swish(t, 900, 6000, { gain: .06, dur: .26, q: 1.6, p0: -.9, p1: -.25, send: .3 });
      bell(t + .12, 88, { gain: .1, dur: .9, pan: -.45, send: .45 }); bell(t + .17, 95, { gain: .075, dur: .8, pan: -.35, send: .45 });
      bell(t + .21, 100, { gain: .025, dur: .5, pan: -.25, send: .5 });
    },
    // escape hatch: the water drains away (falling gurgle + the motif's tail going down)
    hatch: t => { swish(t, 2500, 160, { gain: .1, dur: 1.1, q: 2, send: .3 }); bubbles(t + .1, 9, .8, 1400, 400, .05); [85, 80, 76].forEach((m, k) => bell(t + .1 + k * .22, m, { gain: .06, dur: .9 })); },
    boot: t => {
      // 0.0-1.6  deep water: sub swell + dark rumble
      { const s = strip(0, .2), g = ctx.createGain(); g.connect(s.in); g.gain.setValueAtTime(.0001, t); g.gain.linearRampToValueAtTime(.18, t + 1.4); g.gain.exponentialRampToValueAtTime(.0001, t + 5.5); osc("sine", mtof(40), t, 5.5, g); osc("sine", mtof(52), t, 5.5, g); }
      swish(t, 120, 500, { gain: .07, dur: 2.2, q: .6, send: .4 });
      // 0.6  the droplet that starts everything (the visual drop lands here)
      drop(t + .6, 1500, 2600, { gain: .22, dur: .09, send: 1.2 });
      // 0.7-4.5  the pad opens like light reaching down through water: E major add9
      pad(t + .7, [52, 59, 66, 68, 71], { gain: .045, a: 1.8, hold: 1.6, r: 3.2, c0: 220, c1: 3200 });
      // 1.2-2.6  bubbles rise with the orb
      bubbles(t + 1.2, 16, 1.4, 500, 1700, .06);
      // 2.6-3.65  the motif, one note per wordmark beat, spread across the stereo field
      MOTIF.forEach((m, k) => bell(t + 2.6 + k * .35, m, { gain: .15, dur: 2.2, pan: -.45 + k * .3, send: .55 }));
      // 4.0  bloom: the full chord plus a shimmer of high glass
      [76, 80, 83, 88].forEach((m, k) => bell(t + 4.0 + k * .02, m, { gain: .1, dur: 3.2, pan: (k - 1.5) * .25, send: .7 }));
      [100, 104, 107].forEach((m, k) => bell(t + 4.1 + k * .09, m, { gain: .03, dur: 1.6, pan: .5 - k * .5, send: .8 }));
      swish(t + 3.9, 3000, 9000, { gain: .04, dur: 1.8, q: 3, p0: -.6, p1: .6, send: .7 });
    },
    shutdown: t => {
      [...MOTIF].reverse().forEach((m, k) => bell(t + k * .42, m - 12 * (k === 3), { gain: .12, dur: 2, pan: .45 - k * .3, send: .6 }));
      pad(t, [64, 71, 76], { gain: .035, a: .3, hold: .6, r: 2.2, c0: 2400, c1: 300 });
      swish(t + .2, 3000, 140, { gain: .06, dur: 2, q: .8, send: .5 });
    },
  };

  // over a game everything is silent, except the guide: the player called it, so the blade and its buttons speak
  const GUIDE = ["guide", "move", "select", "back"];
  function silent(name) {
    return st.muted || !st.vol || (st.gaming && !(st.guide && GUIDE.includes(name))) || st.quiet || PAUSE_APPS.includes(st.app) || document.hidden;
  }
  function play(name, opts = {}) {
    if (!SOUNDS[name] || silent(name)) return false;
    init();
    if (ctx.state === "suspended") ctx.resume();
    const now = ctx.currentTime, gap = now - (last[name] ?? -1);
    if (gap < .035) return false;                                   // a burst of the same cue plays once
    if (name === "move" && gap < .12) opts = { ...opts, rep: .65 };  // held key: quieter repeats
    last[name] = now;
    SOUNDS[name](now + .005, opts);
    return true;
  }
  // autoplay policy: the context may start suspended until the first gesture. The build disables the policy
  // in WebView2 (motion.md part 5), so the boot chime plays with no gesture there.
  const unlock = () => { init(); ctx.resume(); removeEventListener("pointerdown", unlock, true); removeEventListener("keydown", unlock, true); };
  addEventListener("pointerdown", unlock, true); addEventListener("keydown", unlock, true);

  window.Sound = {
    names: Object.keys(SOUNDS), motif: MOTIF, play,
    volume(v) { if (v === undefined) return st.vol; st.vol = Math.max(0, Math.min(1, +v)); save(); apply(); },
    mute(on) { if (on === undefined) return st.muted; st.muted = !!on; save(); },
    context(c) { Object.assign(st, c); apply(); return { ...st, silent: silent() }; },
    ctx: init,
  };
})();
