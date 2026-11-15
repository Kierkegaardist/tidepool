/* YouTube's split streams kept in step, shared by the menu's player (index.html) and the pop-out (pop.html). Above 360p YouTube serves the
   picture and the sound apart: the <video> v leads, the audio a follows its play, pause, seek and stalls, and is nudged 3 % faster or slower
   when it drifts. split() says whether a is in use for the current video. */
function avSync(v, a, split) {
  let held = false;
  v.addEventListener("playing", () => { if (split()) { if (Math.abs(a.currentTime - v.currentTime) > .4) a.currentTime = v.currentTime; a.play().catch(() => {}); } });   // only seek the audio when it is really off: every audio seek fires "waiting", which held the picture, whose resume fired "playing" again = a pause/play loop
  v.addEventListener("pause", () => a.pause());
  v.addEventListener("waiting", () => a.pause());
  v.addEventListener("seeking", () => { if (split()) a.pause(); });   // a drag on the timeline seeks many times: the audio waits it out instead of seeking (and holding the picture) on each one
  v.addEventListener("seeked", () => { if (!split() || v.seeking) return; a.currentTime = v.currentTime; if (!v.paused) a.play().catch(() => {}); });
  a.addEventListener("waiting", () => { if (!v.paused && !v.seeking && split()) { held = true; v.pause(); } });   // the audio ran dry: hold the picture too
  a.addEventListener("canplay", () => { if (held) { held = false; v.play().catch(() => {}); } });
  setInterval(() => {
    if (!split() || v.paused || a.paused || v.seeking) return;
    const d = a.currentTime - v.currentTime;
    if (Math.abs(d) > .4) a.currentTime = v.currentTime; else a.playbackRate = v.playbackRate * (Math.abs(d) < .04 ? 1 : d > 0 ? .97 : 1.03);
  }, 250);
}
