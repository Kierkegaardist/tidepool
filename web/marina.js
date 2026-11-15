/* marina.js: Marina without the AI (user, 15 Nov: "a good well rounded function bot without AI").
   localAsk(text) answers the plain asks itself, at once and for free; it returns the reply (a string, or a Promise of one), or null when
   the AI should answer. With no AI (none set up, offline, or it failed) she answers everything she can and says what she can do.
   What she knows: the time (also in other cities), the date, days until a holiday or date; timers, alarms, reminders, a stopwatch;
   the music (play, pause, skip, what's playing, play a song or artist); the volume; opening games, apps, sites, folders and desktop tabs,
   switching to and closing windows; web, YouTube and Wikipedia searches; lock, sleep, shut down, restart (asks first), screenshot,
   show the desktop; PC status (disks, memory, GPU, uptime); the weather (open-meteo, needs the internet); sums, percentages and unit
   conversions; coin, dice, random numbers, picking for you, jokes, fish facts; things to remember. Uses globals from index.html
   (S, H, CFG, HC, loc, allItems, activate, goCat, sync, toast, speak, CATS) at call time, so it loads before the page's script.
   State in localStorage "tp-marina": { notes: [{ t, at }], alarms: [{ at, kind, what }] }. Check: node web/tools/marina-check.mjs */
(() => {   // own scope: its top-level names (pick, dur...) clashed with index.html's and killed the page's script (15 Nov)
let LAST = "", PEND = null, WATCH = 0;
const MST = (() => { try { return JSON.parse(localStorage.getItem("tp-marina")) ?? {}; } catch { return {}; } })();
MST.notes ??= []; MST.alarms ??= [];
const msave = () => { try { localStorage.setItem("tp-marina", JSON.stringify(MST)); } catch {} };
const aiReady = () => CFG.modules.claude !== false && !!HC() && !S.offline;
const LOCAL_HELP = "Without the AI I can tell the time, the date and the weather, set timers, alarms and reminders, open games, apps and sites, " +
  "play and skip music, change the volume, search the web, do sums and conversions, check on the PC, lock it, take a screenshot, remember things, and tell a joke.";
const mpick = a => a[Math.floor(Math.random() * a.length)];
const plur = (n, w) => `${n} ${w}${n === 1 ? "" : "s"}`;
const num = n => String(+(+n).toPrecision(6)).replace(/\.?0+e/, "e");

/* ---- words: Whisper mostly writes digits, but not always ---- */
const NW = { zero: 0, one: 1, two: 2, three: 3, four: 4, five: 5, six: 6, seven: 7, eight: 8, nine: 9, ten: 10, eleven: 11, twelve: 12, thirteen: 13, fourteen: 14,
  fifteen: 15, sixteen: 16, seventeen: 17, eighteen: 18, nineteen: 19, twenty: 20, thirty: 30, forty: 40, fifty: 50, sixty: 60, seventy: 70, eighty: 80, ninety: 90 };
function digits(s) {   // "twenty five" -> "25", "one hundred and five" -> "105"
  const out = []; let cur = null, tot = 0, mult = false;
  const flush = () => { if (cur != null) out.push(String(tot + cur)); cur = null; tot = 0; };
  for (const w of s.split(" ")) {
    if (w in NW) { cur = (cur ?? 0) + NW[w]; mult = false; continue; }
    if (cur != null && w === "hundred") { cur *= 100; mult = true; continue; }
    if (cur != null && w === "thousand") { tot += cur * 1000; cur = 0; mult = true; continue; }
    if (cur != null && mult && w === "and") continue;   // "one hundred and five"; "one and ten" stays two numbers
    flush(); mult = false; out.push(w);
  }
  flush(); return out.join(" ").replace(/\b(this|that|next|last|the|other|another|pick|which|any|no|every) 1\b/g, "$1 one");   // "skip this one" is not a number
}
function norm(t) {
  let s = (" " + t.toLowerCase() + " ").replace(/[’‘]/g, "'").replace(/\b([ap])\.\s?m\b\.?/g, "$1m").replace(/[,!?;"“”]/g, " ").replace(/\.(?!\d)/g, " ").replace(/\s+/g, " ");
  s = s.replace(/\bhalf an hour\b/g, "30 minutes").replace(/\ba quarter of an hour\b|\ba quarter hour\b/g, "15 minutes").replace(/\b(a|an) (second|minute|hour|day)\b/g, "1 $2")
    .replace(/\band a half (second|minute|hour)s?\b/g, "½ $1s").replace(/\bnoon\b/g, "12 pm").replace(/\bmidnight\b/g, "12 am");
  s = digits(s.trim()).replace(/(\d+) ½/g, "$1.5").replace(/\b(\d+) (second|minute|hour)s? and a half\b/g, (_, n, u) => `${+n + .5} ${u}s`);
  for (let k = 0; k < 4; k++) s = s.replace(/^(please|can you|could you|would you|will you|marina|hey|hi marina|ok|okay|um|uh|so|and|now|just|quickly) /, "");
  s = s.replace(/ (please|for me|thanks|thank you|real quick|right now|quickly)$/, "").trim();
  for (const [a, b] of REPHRASE) s = s.replace(a, b);
  return s.replace(/\s+/g, " ").trim();
}
const UNITMS = { second: 1e3, sec: 1e3, minute: 6e4, min: 6e4, hour: 36e5, hr: 36e5, day: 864e5 };
function mdur(s) {   // every "N unit" in s, summed: "1 hour 30 minutes" -> 5.4e6; 0 = none
  let ms = 0; for (const m of s.matchAll(/(\d+(?:\.\d+)?) ?(second|sec|minute|min|hour|hr|day)s?\b/g)) ms += +m[1] * UNITMS[m[2]]; return ms;
}
function durText(ms) {
  const s = Math.round(ms / 1000), h = Math.floor(s / 3600), m = Math.floor(s % 3600 / 60), r = s % 60, p = [];
  if (h) p.push(plur(h, "hour")); if (m) p.push(plur(m, "minute")); if (r && h === 0 && m < 10) p.push(plur(r, "second"));
  return p.join(" ") || "a moment";
}
function clock(s) {   // "7:30 am", "at 19.45", "7 pm", "7 in the morning" -> the next Date at that time; null = none
  const m = s.match(/\b(?:at |for )(\d{1,2})(?:[:.](\d{2}))? ?(am|pm|in the morning|in the evening|at night|tonight)?\b/) ?? s.match(/\b(\d{1,2})(?:[:.](\d{2}))? ?(am|pm)\b/) ?? s.match(/\b(\d{1,2})[:.](\d{2})\b()/);
  if (!m) return null;
  let h = +m[1]; const mi = +(m[2] ?? 0), ap = m[3] ?? "";
  if (h > 23 || mi > 59) return null;
  if (/pm|evening|night/.test(ap) && h < 12) h += 12; if (ap === "am" && h === 12) h = 0;
  const d = new Date(); d.setHours(h, mi, 0, 0);
  if (!ap && h <= 12 && d <= new Date()) { const pmD = new Date(d); pmD.setHours(h + 12); if (h < 12 && pmD > new Date()) return pmD; }   // "at 7" said at 3 pm = 7 pm
  if (d <= new Date()) d.setDate(d.getDate() + 1);
  return d;
}
const hhmm = d => d.toLocaleTimeString([], { hour: "numeric", minute: "2-digit" });

/* ---- timers, alarms, reminders (kept across restarts) ---- */
const ARMED = new Map();
function arm(a) {
  if (ARMED.has(a)) return;
  const fire = () => { ARMED.delete(a); MST.alarms = MST.alarms.filter(x => x !== a); msave();
    const say = a.kind === "reminder" ? `Reminder: ${flip(a.what)}.` : a.kind === "alarm" ? `It's ${hhmm(new Date())}. ${a.what ? flip(a.what) + "." : "Time to get up!"}` : `Your ${a.what} timer is done.`;
    window.Sound?.play("notify"); toast("@mic.svg", a.kind === "timer" ? "Timer done" : a.kind === "alarm" ? "Alarm" : "Reminder", a.kind === "timer" ? a.what : flip(a.what) || hhmm(new Date()), true); speak(say); };
  const wait = a.at - Date.now();
  ARMED.set(a, wait > 2 ** 31 - 1 ? setTimeout(() => { ARMED.delete(a); arm(a); }, 2 ** 31 - 1) : setTimeout(fire, Math.max(0, wait)));
}
function addAlarm(at, kind, what) { const a = { at: +at, kind, what }; MST.alarms.push(a); msave(); arm(a); }
function rearm() {   // on load: what is still to come is armed again; what went off while the desktop was closed is dropped (a reminder older than 10 min too)
  MST.alarms = MST.alarms.filter(a => a.at > Date.now() - (a.kind === "reminder" ? 6e5 : 0)); msave(); MST.alarms.forEach(arm);
}
const flip = t => (t ?? "").replace(/\bmy\b/g, "your").replace(/\bi am\b|\bi'm\b/g, "you're").replace(/\bi\b/g, "you").replace(/\bme\b/g, "you").replace(/\bmine\b/g, "yours").replace(/\bmyself\b/g, "yourself");

/* ---- the time elsewhere, days until ---- */
const ZONES = { london: "Europe/London", england: "Europe/London", uk: "Europe/London", britain: "Europe/London", dublin: "Europe/Dublin", ireland: "Europe/Dublin", lisbon: "Europe/Lisbon",
  paris: "Europe/Paris", france: "Europe/Paris", berlin: "Europe/Berlin", germany: "Europe/Berlin", amsterdam: "Europe/Amsterdam", madrid: "Europe/Madrid", spain: "Europe/Madrid", rome: "Europe/Rome", italy: "Europe/Rome",
  oslo: "Europe/Oslo", norway: "Europe/Oslo", stockholm: "Europe/Stockholm", sweden: "Europe/Stockholm", copenhagen: "Europe/Copenhagen", denmark: "Europe/Copenhagen", helsinki: "Europe/Helsinki", finland: "Europe/Helsinki",
  warsaw: "Europe/Warsaw", poland: "Europe/Warsaw", prague: "Europe/Prague", vienna: "Europe/Vienna", athens: "Europe/Athens", greece: "Europe/Athens", kyiv: "Europe/Kyiv", kiev: "Europe/Kyiv", ukraine: "Europe/Kyiv",
  istanbul: "Europe/Istanbul", turkey: "Europe/Istanbul", moscow: "Europe/Moscow", russia: "Europe/Moscow", cairo: "Africa/Cairo", egypt: "Africa/Cairo", johannesburg: "Africa/Johannesburg", "south africa": "Africa/Johannesburg",
  dubai: "Asia/Dubai", india: "Asia/Kolkata", delhi: "Asia/Kolkata", "new delhi": "Asia/Kolkata", mumbai: "Asia/Kolkata", bangkok: "Asia/Bangkok", thailand: "Asia/Bangkok", singapore: "Asia/Singapore", jakarta: "Asia/Jakarta",
  "hong kong": "Asia/Hong_Kong", beijing: "Asia/Shanghai", shanghai: "Asia/Shanghai", china: "Asia/Shanghai", taipei: "Asia/Taipei", taiwan: "Asia/Taipei", manila: "Asia/Manila", seoul: "Asia/Seoul", korea: "Asia/Seoul",
  "south korea": "Asia/Seoul", tokyo: "Asia/Tokyo", japan: "Asia/Tokyo", sydney: "Australia/Sydney", melbourne: "Australia/Melbourne", australia: "Australia/Sydney", auckland: "Pacific/Auckland", "new zealand": "Pacific/Auckland",
  honolulu: "Pacific/Honolulu", hawaii: "Pacific/Honolulu", "los angeles": "America/Los_Angeles", la: "America/Los_Angeles", california: "America/Los_Angeles", "san francisco": "America/Los_Angeles", seattle: "America/Los_Angeles",
  vancouver: "America/Vancouver", denver: "America/Denver", chicago: "America/Chicago", texas: "America/Chicago", "new york": "America/New_York", "new york city": "America/New_York", nyc: "America/New_York",
  toronto: "America/Toronto", canada: "America/Toronto", "mexico city": "America/Mexico_City", mexico: "America/Mexico_City", "sao paulo": "America/Sao_Paulo", brazil: "America/Sao_Paulo", "buenos aires": "America/Argentina/Buenos_Aires",
  utc: "UTC", gmt: "UTC" };
const MONTHS = ["january", "february", "march", "april", "may", "june", "july", "august", "september", "october", "november", "december"];
function easter(y) { const a = y % 19, b = Math.floor(y / 100), c = y % 100, d = Math.floor(b / 4), e = b % 4, f = Math.floor((b + 8) / 25), g = Math.floor((b - f + 1) / 3), h = (19 * a + b - d - g + 15) % 30,
  i = Math.floor(c / 4), k = c % 4, l = (32 + 2 * e + 2 * i - h - k) % 7, m = Math.floor((a + 11 * h + 22 * l) / 451); return [Math.floor((h + l - 7 * m + 114) / 31) - 1, (h + l - 7 * m + 114) % 31 + 1]; }
function dayOf(what) {   // [month 0-11, day] of a holiday or a "december 24" / "24th of december"; null = unknown
  const HOL = { christmas: [11, 25], "christmas day": [11, 25], "christmas eve": [11, 24], "new year": [0, 1], "new years": [0, 1], "new year's": [0, 1], "new year's day": [0, 1], "new years day": [0, 1],
    "new year's eve": [11, 31], "new years eve": [11, 31], halloween: [9, 31], "valentine's day": [1, 14], "valentines day": [1, 14], valentines: [1, 14], "valentine's": [1, 14], "st patrick's day": [2, 17] };
  if (HOL[what]) return HOL[what];
  if (what === "easter") return "easter";
  let m = what.match(/^(\w+) (\d{1,2})(?:st|nd|rd|th)?$/); if (m && MONTHS.includes(m[1])) return [MONTHS.indexOf(m[1]), +m[2]];
  m = what.match(/^(\d{1,2})(?:st|nd|rd|th)? (?:of )?(\w+)$/); if (m && MONTHS.includes(m[2])) return [MONTHS.indexOf(m[2]), +m[1]];
  return null;
}

/* ---- sums and conversions ---- */
function calc(s) {
  let m = s.match(/(-?\d+(?:\.\d+)?) ?(?:percent|%) of (-?\d+(?:\.\d+)?)/); if (m) return `${m[1]} percent of ${m[2]} is ${num(m[1] * m[2] / 100)}.`;
  m = s.match(/square root of (\d+(?:\.\d+)?)/); if (m) return `The square root of ${m[1]} is ${num(Math.sqrt(m[1]))}.`;
  const e = s.replace(/^(what's|what is|whats|calculate|compute|how much is|solve|work out)\s+/, "").replace(/ (equal|equals|is)$/, "")
    .replace(/\bmultiplied by\b|\btimes\b|×|\bx\b/g, "*").replace(/\bdivided by\b|\bover\b|÷/g, "/").replace(/\bplus\b/g, "+").replace(/\bminus\b|\btake away\b/g, "-")
    .replace(/\bto the power of\b|\bto the\b/g, "^").replace(/\bsquared\b/g, "^2").replace(/\bcubed\b/g, "^3").replace(/\s+/g, " ").trim();
  if (!/^[\d\s.+\-*/()^]+$/.test(e) || !/\d\s*[-+*/^]\s*[\d(]/.test(e)) return null;
  try { const v = Function(`"use strict"; return (${e.replace(/\^/g, "**")});`)(); return Number.isFinite(v) ? `That's ${num(v)}.` : "That doesn't come out to a number."; } catch { return null; }   // only digits and + - * / ^ ( ) reach here
}
const UNITS = {};
[["length", { millimeter: .001, mm: .001, centimeter: .01, cm: .01, meter: 1, metre: 1, m: 1, kilometer: 1000, kilometre: 1000, km: 1000, inch: .0254, inches: .0254, in: .0254, foot: .3048, feet: .3048, ft: .3048, yard: .9144, mile: 1609.344 }],
 ["mass", { milligram: 1e-6, mg: 1e-6, gram: .001, g: .001, kilogram: 1, kilo: 1, kg: 1, pound: .45359237, lb: .45359237, lbs: .45359237, ounce: .028349523, oz: .028349523, stone: 6.35029318, ton: 1000, tonne: 1000 }],
 ["volume", { milliliter: .001, millilitre: .001, ml: .001, liter: 1, litre: 1, l: 1, cup: .2365882, pint: .473176, quart: .946353, gallon: 3.785411, "fluid ounce": .0295735, tablespoon: .0147868, teaspoon: .00492892 }],
 ["speed", { "kilometer per hour": 1 / 3.6, "kilometers per hour": 1 / 3.6, "km/h": 1 / 3.6, kmh: 1 / 3.6, "kilometres per hour": 1 / 3.6, "mile per hour": .44704, "miles per hour": .44704, mph: .44704, "meter per second": 1, "meters per second": 1, "m/s": 1, knot: .514444 }],
 ["data", { byte: 1, kilobyte: 1e3, kb: 1e3, megabyte: 1e6, mb: 1e6, gigabyte: 1e9, gb: 1e9, terabyte: 1e12, tb: 1e12 }],
 ["time", { second: 1, minute: 60, hour: 3600, day: 86400, week: 604800, year: 31557600 }]].forEach(([d, u]) => Object.entries(u).forEach(([k, f]) => UNITS[k] = [d, f]));
["celsius", "c", "centigrade", "fahrenheit", "f", "kelvin", "k"].forEach(k => UNITS[k] = ["temp", k[0]]);
const unit = w => { w = w.replace(/^(degrees?|a|an|the) /, "").trim(); return UNITS[w] ? [w, UNITS[w]] : UNITS[w.replace(/e?s$/, "")] ? [w, UNITS[w.replace(/e?s$/, "")]] : UNITS[w.replace(/s$/, "")] ? [w, UNITS[w.replace(/s$/, "")]] : null; };
function convert(s) {
  let m = s.match(/^(?:convert |what's |what is |how much is |how many \w+ is )?(-?\d+(?:\.\d+)?) ?([a-z/ ]+?) (?:to|in|into|as) ([a-z/ ]+)$/), n, a, b;
  if (m) { n = +m[1]; a = unit(m[2]); b = unit(m[3]); }
  else if ((m = s.match(/^how many ([a-z/ ]+?) (?:are )?(?:there )?in (?:(-?\d+(?:\.\d+)?) )?([a-z/ ]+)$/))) { n = +(m[2] ?? 1); a = unit(m[3]); b = unit(m[1]); }
  if (!a || !b || a[1][0] !== b[1][0]) return null;
  let v;
  if (a[1][0] === "temp") { const c = a[1][1] === "c" ? n : a[1][1] === "f" ? (n - 32) * 5 / 9 : n - 273.15; v = b[1][1] === "c" ? c : b[1][1] === "f" ? c * 9 / 5 + 32 : c + 273.15; }
  else v = n * a[1][1] / b[1][1];
  return `${num(n)} ${a[0]} is ${num(v)} ${b[0]}.`;
}

/* ---- matching games, apps, sites, folders and open windows by name ---- */
const nm = s => (s ?? "").toLowerCase().replace(/[^\p{L}\p{N} ]+/gu, " ").replace(/\s+/g, " ").trim();
function best(q, list, name) {   // the entry whose name fits q best; null below half its words
  q = nm(q).replace(/^(the|my|a|an) /, ""); if (!q) return null;
  const qw = q.split(" "); let top = null, bs = 0;
  for (const x of list) {
    const n = nm(name(x)); if (!n) continue;
    const nw = n.split(" ");
    let s = n === q ? 3 : n.length > 2 && (n.startsWith(q + " ") || q.startsWith(n + " ") || n.startsWith(q)) ? 2 : qw.filter(w => nw.includes(w)).length / Math.max(qw.length, nw.length);
    if (x.page) s *= .9;   // a bookmark or visited page loses a tie to the app of that name
    if (s > bs) { bs = s; top = x; }
  }
  return bs >= .5 ? top : null;
}
const launchable = () => allItems().filter(it => it.act === "launch" || it.act === "game" || it.dir);
const findItem = q => best(q, launchable(), it => it.n);
const findWin = q => best(q, S.wins ?? [], w => w[0] + " " + w[1]) ?? best(q, S.wins ?? [], w => w[0]);
const SITES = { youtube: "https://www.youtube.com", gmail: "https://mail.google.com", email: "https://mail.google.com", mail: "https://mail.google.com", google: "https://www.google.com",
  reddit: "https://www.reddit.com", twitter: "https://x.com", x: "https://x.com", github: "https://github.com", netflix: "https://www.netflix.com", twitch: "https://www.twitch.tv",
  wikipedia: "https://en.wikipedia.org", maps: "https://maps.google.com", "google maps": "https://maps.google.com", "google drive": "https://drive.google.com", drive: "https://drive.google.com",
  "google docs": "https://docs.google.com", calendar: "https://calendar.google.com", "google calendar": "https://calendar.google.com", neocities: "https://neocities.org/dashboard",
  "my site": "https://bonerequiem.neocities.org", "my shrine": "https://bonerequiem.neocities.org", claude: "https://claude.ai", chatgpt: "https://chatgpt.com", amazon: "https://www.amazon.com", ebay: "https://www.ebay.com" };
const openUrl = u => { if (H?.openUrl) H.openUrl(u); else window.open(u, "_blank"); };
const CATWORD = { games: "games", game: "games", music: "music", videos: "video", video: "video", youtube: "video", studio: "studio", web: "web", internet: "web", files: "files", folders: "files", settings: "settings", home: "home" };

/* ---- the PC and the weather ---- */
const sys = (op, v) => H?.sys ? H.sys(op, v) : Promise.resolve(null);
const gbText = g => g >= 1000 ? `${num((g / 1000).toFixed(1))} terabytes` : `${Math.round(g)} gigabytes`;
async function mstats(s) {
  const r = await sys("stats"); if (!r) return "I can't see the PC's stats from here.";
  const out = [], all = !/disk|drive|space|storage|gpu|graphics|hot|temp|memory|ram|uptime|been on/.test(s);
  if (all || /disk|drive|space|storage/.test(s)) out.push(r.disks.map(d => `${d.d} has ${gbText(d.free)} free`).join(", ") + ".");
  if (all || /memory|ram/.test(s)) out.push(`Memory is ${r.memUsed}% used, of ${Math.round(r.memTotal)} gigabytes.`);
  if (all || /gpu|graphics|hot|temp/.test(s)) { const [t, u] = (r.gpu ?? "").split(",").map(x => +x); out.push(r.gpu ? `The graphics card is at ${t} degrees and ${u}% busy${t >= 85 ? ", that's hot" : ""}.` : "I can't read the graphics card."); }
  if (all || /uptime|been on/.test(s)) out.push(`The PC has been on for ${durText(r.upHours * 36e5)}.`);
  return out.join(" ");
}
const WMO = { 0: "clear", 1: "mostly clear", 2: "partly cloudy", 3: "overcast", 45: "foggy", 48: "foggy with frost", 51: "light drizzle", 53: "drizzle", 55: "heavy drizzle", 56: "freezing drizzle", 57: "freezing drizzle",
  61: "light rain", 63: "rain", 65: "heavy rain", 66: "freezing rain", 67: "freezing rain", 71: "light snow", 73: "snow", 75: "heavy snow", 77: "snow grains", 80: "rain showers", 81: "rain showers", 82: "heavy rain showers",
  85: "snow showers", 86: "heavy snow showers", 95: "thunderstorms", 96: "thunderstorms with hail", 99: "thunderstorms with hail" };
async function weather(s) {
  if (S.offline) return "We're offline, so I can't check the weather.";
  let w;
  try {
    const u = `https://api.open-meteo.com/v1/forecast?latitude=${loc.lat.toFixed(2)}&longitude=${loc.lon.toFixed(2)}&current=temperature_2m,apparent_temperature,weather_code,wind_speed_10m` +
      `&daily=weather_code,temperature_2m_max,temperature_2m_min,precipitation_probability_max,sunrise,sunset&timezone=auto&forecast_days=2`;
    const r = await fetch(u, { signal: AbortSignal.timeout(8000) }); if (!r.ok) throw 0; w = await r.json();
  } catch { return "I couldn't reach the weather service just now."; }
  const d = /tomorrow/.test(s) ? 1 : 0, D = w.daily, c = w.current, R = Math.round, rain = D.precipitation_probability_max[d] ?? 0, when = d ? "Tomorrow" : "Today";
  if (/sunrise|sun rise|sun come up/.test(s)) return `The sun rises at ${hhmm(new Date(D.sunrise[d]))}${d ? " tomorrow" : ""}.`;
  if (/sunset|sun set|sun go down|get dark/.test(s)) return `The sun sets at ${hhmm(new Date(D.sunset[d]))}${d ? " tomorrow" : ""}.`;
  if (/\b(rain|umbrella|snow)\b/.test(s)) { const snow = /snow/.test(s), code = D.weather_code[d], snowy = code >= 71 && code <= 86 && code !== 80 && code !== 81 && code !== 82;
    return snow ? (snowy ? `Yes, ${WMO[code]} ${when.toLowerCase()}.` : `No snow expected ${when.toLowerCase()}.`) : `${when}: ${rain}% chance of rain${rain >= 50 ? ", so take an umbrella" : rain >= 25 ? ", maybe bring an umbrella" : ""}.`; }
  const day = `high ${R(D.temperature_2m_max[d])}, low ${R(D.temperature_2m_min[d])}, ${rain}% chance of rain.`;
  if (d) return `Tomorrow in ${loc.place}: ${WMO[D.weather_code[1]] ?? "mixed"}, ${day}`;
  return `It's ${R(c.temperature_2m)} degrees and ${WMO[c.weather_code] ?? "mixed"} in ${loc.place}${Math.abs(c.apparent_temperature - c.temperature_2m) >= 2 ? `, feels like ${R(c.apparent_temperature)}` : ""}. Today: ${day}`;
}

/* ---- fun ---- */
const JOKES = ["Why are fish so smart? They live in schools.", "What do you call a fish with no eyes? A fsh.", "Why don't fish play basketball? They're scared of the net.",
  "How does the ocean say hello? It waves.", "Why did the crab never share? Because he's shellfish.", "What do you call a fish in a bow tie? Sofishticated.",
  "What did the fish say when it swam into a wall? Dam.", "Why do programmers prefer dark mode? Because light attracts bugs.", "I'd tell you a UDP joke, but you might not get it.",
  "Why did the scarecrow win an award? He was outstanding in his field.", "What's a fish's favourite instrument? The bass.", "Why was the computer cold? It left its Windows open.",
  "Why did the goldfish blush? It saw the bottom of the sea.", "What do sea monsters eat? Fish and ships."];
const FACTS = ["Goldfish can remember things for months, not three seconds. I remember you, for one.", "Goldfish have no stomach. Food goes straight through, so we're always peckish.",
  "Goldfish see more colours than people do, ultraviolet included.", "An octopus has three hearts and blue blood.", "Sea otters hold hands while they sleep so they don't drift apart.",
  "Seahorse dads carry the babies.", "Coral is an animal, not a plant.", "Clownfish are all born male, and the boss of the group can turn female.",
  "Greenland sharks can live for well over 250 years.", "A starfish has no brain and no blood.", "A group of jellyfish is called a smack.",
  "At least half the oxygen you breathe comes from the ocean, mostly from tiny plankton."];

/* ---- saying it another way: casual speech and Whisper's usual slips, rewritten to the plain asks below (in order) ---- */
const REPHRASE = [
  [/\bwanna\b/g, "want to"], [/\bgonna\b/g, "going to"], [/\bgimme\b/g, "give me"], [/\blemme\b/g, "let me"], [/\bwhats\b/g, "what's"], [/\bhows\b/g, "how's"], [/\bwhat is\b/g, "what's"], [/\bhow is\b/g, "how's"],
  [/^(marine|mariner|maria|marena|corina)\b ?/, ""],                                                    // the wake word misheard, left at the front
  [/^(i want you to|i need you to|i'd like you to|i would like you to|i want to|i'd like to|i would like to|i need to|let's|let me|go ahead and|would you mind|be a dear and|is it possible to|are you able to|you can|try to|try and|help me) /, ""],
  [/^(do you know|can you tell me|could you tell me|tell me|i want to know|i'd like to know|any idea|do you have any idea|i wonder) /, "what's "],
  [/^what's (what's|what|how's|how|when's|when|where's|where|who's|who|which|if|whether) /, "$1 "],
  [/^what's (a |another |some )?(jokes?|fun fact|fact|something)\b/, "tell me $1$2"],                    // "tell me a joke" turned into "what's a joke" above
  [/\bwhat time it is\b/, "what time is it"], [/\bwhat day it is\b/, "what day is it"], [/\bwhat the time is\b/, "what's the time"], [/\bwhat the weather is( like)?\b/, "what's the weather"],
  [/\bset a time for\b/, "set a timer for"], [/\bset the time for\b/, "set a timer for"], [/\btimer of\b/, "timer for"],
  [/^(put on|throw on|chuck on|stick on|queue up|blast|listen to|i want to hear|hear|play me|bang on) /, "play "], [/^(kill|cut|stop) (the )?(music|song|track|tunes|spotify)$/, "pause"], [/^which /, "what "],
  [/^(fire up|boot up|load up|start up|spin up|get me into|bring up|pull up|go into|launch|run) /, "open "],
  [/^(make it |turn it |crank it |pump it |bump it )?(louder|up a bit|up some|up)$/, "volume up"], [/^(crank|pump|bump) (it|the music|the sound|the volume) up\b.*/, "volume up"],
  [/^(make it |turn it )?(quieter|softer|down a bit|down some|down)$/, "volume down"], [/^(it's |that's |this is )?(way )?too loud$/, "volume down"], [/^(it's |that's )?too (quiet|soft)$/, "volume up"],
  [/^(change|switch) (the )?(song|track)$|^(play )?(another|a different) (song|track|one)$|^i don't like this (song|track|one)$/, "next song"],
  [/^(play )?(that|the) (song|track) again$|^replay (that|the last) (song|track)$/, "previous song"],
];

/* ---- a near match: when no rule fits, the ask is compared with example phrasings of each plain ask; the closest one wins, if
   enough of its words were said and enough of what was said is in it (stricter while the AI is there to take what she misses) ---- */
const STOP = new Set("a an the is are am was it its i me my mine you your yours to please of for on in at this that these those what's what can could would will do does did how how's be just some any so um uh oh hey marina now right currently really very bit little there here we us our take get give tell have got let want need like know kind sort maybe possibly mind".split(" "));
const SYN = { louder: "up", raise: "up", increase: "up", higher: "up", boost: "up", crank: "up", pump: "up", quieter: "down", softer: "down", lower: "down", decrease: "down", reduce: "down",
  sound: "volume", audio: "volume", loud: "volume", noise: "volume", song: "music", songs: "music", track: "music", tune: "music", tunes: "music", spotify: "music",
  pc: "computer", laptop: "computer", machine: "computer", rig: "computer", system: "computer", windows: "computer", forecast: "weather", outside: "weather", umbrella: "rain", raining: "rain", rainy: "rain",
  graphics: "gpu", card: "gpu", ram: "memory", storage: "space", room: "space", drive: "disk", drives: "disk", hard: "disk", ssd: "disk", photo: "screenshot", picture: "screenshot", snap: "screenshot", grab: "screenshot",
  capture: "screenshot", reboot: "restart", hibernate: "sleep", kill: "stop", halt: "stop", skip: "next", resume: "play", unpause: "play", continue: "play", funny: "joke", laugh: "joke", jokes: "joke" };
const stem = w => w.length > 5 && w.endsWith("ing") ? w.slice(0, -3) : w.length > 4 && w.endsWith("ed") ? w.slice(0, -2) : w.length > 3 && w.endsWith("s") && !w.endsWith("ss") ? w.slice(0, -1) : w;
const toks = s => [...new Set(s.split(/\s+/).map(w => w.replace(/'s$/, "")).map(w => SYN[w] ?? w).filter(w => w && !STOP.has(w)).map(w => SYN[stem(w)] ?? stem(w)))];
const NEAR = {
  "what's the time": ["what time is it", "time please", "current time", "the time", "do you have the time", "what hour is it", "how late is it", "clock", "time now", "time check"],
  "what's the date": ["today's date", "what day is it", "which day is today", "what day of the week is it", "date today", "what date is it", "the date", "day today"],
  "what week is it": ["week number", "which week are we in", "current week"], "what year is it": ["which year is it", "current year"],
  "volume up": ["turn it up", "make it louder", "turn up the volume", "increase the volume", "raise the volume", "i can't hear it", "more volume", "volume higher", "louder please", "turn the music up"],
  "volume down": ["turn it down", "make it quieter", "too loud", "lower the volume", "decrease the volume", "less volume", "keep it down", "volume lower", "turn the music down"],
  "mute": ["mute the volume", "silence", "no volume", "turn the volume off", "volume off"], "unmute": ["volume back on", "turn the volume on", "unmute it", "volume on"],
  "what's the volume": ["how loud is it", "volume level", "what's the volume at", "current volume"],
  "pause": ["pause the music", "stop the music", "hold the music", "stop playing", "music off", "pause playback", "stop it", "pause it"],
  "play music": ["play the music", "play music again", "start the music", "music on", "play the music again", "put the music back on", "keep playing", "play again"],
  "next song": ["next", "skip it", "next music", "play the next one", "skip ahead", "next one please"],
  "previous song": ["go back", "previous", "last music", "play the last one", "go back one music", "back one"],
  "what's playing": ["what music is this", "who sings this", "name of this music", "what am i listening to", "who is this artist", "music name", "what's on", "what is this music called", "current music"],
  "what's the weather": ["how's the weather", "weather today", "is it cold weather", "what's it like weather", "how warm is it weather", "temperature weather", "do i need a jacket", "is it nice weather", "weather", "how cold is it", "how warm is it", "is it cold", "is it warm"],
  "what's the weather tomorrow": ["weather tomorrow", "tomorrow's weather", "will it be cold tomorrow", "how's tomorrow looking", "tomorrow weather"],
  "will it rain": ["is it going to rain", "rain today", "chance of rain", "is it rain", "should i take rain", "will it rain today", "any rain"],
  "when's sunset": ["when does it get dark", "what time is sunset", "when does the sun go down", "sunset"], "when's sunrise": ["when does the sun come up", "what time is sunrise", "when does it get light", "sunrise"],
  "lock the computer": ["lock computer", "lock the screen", "lock it up", "lock"], "take a screenshot": ["screenshot", "screenshot the screen", "print screen", "screenshot this"],
  "show the desktop": ["minimize everything", "hide all windows", "show desktop", "minimize all", "go to desktop"],
  "how's my computer": ["computer status", "computer stats", "computer health", "check computer", "computer performance", "how's computer doing", "computer info"],
  "how much disk space": ["free space", "space left", "how full is disk", "disk space", "space on disk", "how much space", "disk full"],
  "gpu temperature": ["how hot is gpu", "gpu temp", "is gpu overheating", "how hot is computer", "is computer overheating", "gpu heat", "gpu usage"],
  "memory usage": ["how much memory am i using", "memory use", "how much memory is free", "memory used", "memory"],
  "shut down the computer": ["turn off the computer", "power off", "shut the computer down", "switch off the computer", "turn the computer off", "shutdown"],
  "restart the computer": ["restart", "restart computer", "restart my computer"], "put the computer to sleep": ["sleep mode", "computer sleep", "make the computer sleep", "sleep computer"],
  "tell me a joke": ["joke", "something funny", "make me joke", "say something joke", "got a joke", "know a joke", "cheer me up", "another joke", "tell joke"],
  "tell me a fun fact": ["fun fact", "teach me something", "random fact", "something interesting", "something cool", "did you know", "tell me a fact", "fact"],
  "flip a coin": ["heads or tails", "toss a coin", "coin toss", "coin flip", "flip coin"], "roll a dice": ["roll a die", "throw a dice", "dice roll", "roll the dice", "roll dice", "throw the dice"],
  "random number": ["give me a random number", "pick a number", "any number"],
  "what can you do": ["help", "what are your commands", "how do i use you", "what do you know", "your features", "what can i ask you", "what can i say", "commands", "options"],
  "what do you remember": ["read my notes", "my notes", "what did i tell you", "show my notes", "what have i told you", "notes"],
  "how long is left": ["how much time is left", "timer status", "time remaining", "how long on the timer", "what timers do i have", "when does my timer end", "time left", "check the timer"],
  "cancel the timer": ["stop the timer", "delete the timer", "never mind the timer", "remove the timer", "timer off", "turn off the timer"],
  "cancel the alarm": ["stop the alarm", "delete the alarm", "turn off the alarm", "alarm off", "remove the alarm", "no alarm"],
  "cancel the reminder": ["stop the reminder", "delete the reminder", "remove the reminder"],
  "start a stopwatch": ["stopwatch", "start timing", "time me"], "stop the stopwatch": ["stop timing", "how long has it been", "stopwatch time"],
  "what should i play": ["suggest a game", "i'm bored", "recommend a game", "what game should i play", "pick a game", "give me a game"],
  "say that again": ["repeat", "what was that", "i didn't catch that", "sorry what", "come again", "one more time", "repeat that", "say again"],
  "who are you": ["what are you", "what's your name", "introduce yourself", "your name"], "how are you": ["how are you doing", "how's it going", "you okay", "how do you feel", "how have you been", "what's up"],
  "good night": ["night", "nighty night", "sleep well", "i'm going to bed"], "thank you": ["thanks", "thanks a lot", "cheers", "much appreciated", "nice one", "good job", "great job"],
  "hello": ["hi", "hey", "hiya", "good evening", "good afternoon", "yo", "greetings"],
};
let NEARX = null;   // built on first use: norm() needs REPHRASE, which is defined above
function near(s, strict) {   // the plain ask that s is closest to, or null
  NEARX ??= Object.entries(NEAR).flatMap(([c, ex]) => [c, ...ex].map(e => [c, toks(norm(e))]));
  const q = toks(s); if (!q.length || q.length > 7) return null;
  let top = null, bs = 0;
  for (const [c, e] of NEARX) {
    if (!e.length) continue;
    const hit = e.filter(w => q.includes(w)).length, cover = hit / e.length, prec = hit / q.length, sc = cover * prec;
    if (cover >= (strict ? .75 : .6) && prec >= (strict ? .75 : .5) && sc > bs) { bs = sc; top = c; }
  }
  return top;
}
const miss = (tag, t) => { try { sys("miss", `[${tag}] ${t}`); } catch {} };   // logs\marina-misses.txt: what she did not get, to teach her

/* ---- the asks ---- */
function localAsk(t, depth = 0) {
  const s = norm(t), ai = aiReady(), now = new Date(), playing = S.music && !S.paused;
  let m;
  if (!s) return null;

  // a yes or no to the last thing she asked (shut down, restart, sleep, forget)
  if (PEND && Date.now() < PEND.until) {
    const p = PEND; PEND = null;
    if (/^(yes|yeah|yep|yup|sure|do it|go ahead|confirm|ok|okay|please do|affirmative)\b/.test(s)) { p.run(); return p.say; }
    if (/^(no|nope|nah|cancel|don't|do not|never mind|stop)\b/.test(s)) return "Okay, I won't.";
  }
  PEND = null;
  if (/^(repeat( that)?|say (that|it) again|what did you (just )?say|come again|pardon|sorry what)$/.test(s)) return globalThis.LAST_SAID || "I haven't said anything yet.";

  // the time and the date
  if ((m = s.match(/\btime(?: is it)? in ([a-z ]+)$/)) && ZONES[m[1].replace(/^the /, "")]) return `It's ${new Date().toLocaleTimeString([], { hour: "numeric", minute: "2-digit", timeZone: ZONES[m[1].replace(/^the /, "")] })} in ${m[1].replace(/\b\w/g, c => c.toUpperCase())}.`;
  if (/\bwhat('s| is) the time\b|\bwhat time is it\b|^time$|^the time$|\bwhat's the time now\b|\bgot the time\b/.test(s)) return `It's ${hhmm(now)}.`;
  if (/\bwhat('s| is) (the )?(date|day)\b|\bwhat day is it\b|\btoday'?s date\b|\bwhat('s| is) today\b/.test(s)) return `It's ${now.toLocaleDateString([], { weekday: "long", month: "long", day: "numeric" })}.`;
  if (/\bwhat year is it\b/.test(s)) return `It's ${now.getFullYear()}.`;
  if (/\bwhat (week|week number) is it\b|\bwhich week is it\b/.test(s)) { const d = new Date(Date.UTC(now.getFullYear(), now.getMonth(), now.getDate())); d.setUTCDate(d.getUTCDate() + 4 - (d.getUTCDay() || 7)); return `It's week ${Math.ceil(((d - Date.UTC(d.getUTCFullYear(), 0, 1)) / 864e5 + 1) / 7)}.`; }
  if ((m = s.match(/\bhow (?:many days|long|many sleeps) (?:is it )?(?:until|till|til|to|before) ([a-z0-9' ]+?)$/))) {
    let w = dayOf(m[1].replace(/^(the|it's|its) /, "")); if (!w) return ai ? null : `I don't know when ${m[1]} is.`;
    const y = now.getFullYear(), at = yy => { const [mo, da] = w === "easter" ? easter(yy) : w; return new Date(yy, mo, da); }, today = new Date(y, now.getMonth(), now.getDate());
    let d = at(y); if (d < today) d = at(y + 1);
    const n = Math.round((d - today) / 864e5);
    return n === 0 ? `That's today!` : n === 1 ? `That's tomorrow!` : `${n} days, it's on ${d.toLocaleDateString([], { weekday: "long", month: "long", day: "numeric" })}.`;
  }

  // timers, alarms, reminders, the stopwatch
  if (/\b(cancel|stop|delete|clear|remove|turn off) (?:the |my |all |all the |all my )?(timers?|alarms?|reminders?)\b/.test(s)) {
    const kind = s.match(/timer|alarm|reminder/)[0], gone = MST.alarms.filter(a => a.kind === kind);
    gone.forEach(a => { clearTimeout(ARMED.get(a)); ARMED.delete(a); }); MST.alarms = MST.alarms.filter(a => a.kind !== kind); msave();
    return gone.length ? `Cancelled ${gone.length === 1 ? "your " + kind : "all " + plur(gone.length, kind)}.` : `There's no ${kind} set.`;
  }
  if (/\bhow (much|long) (time )?(is )?left\b|\bwhat (timers|alarms|reminders)\b|\bany (timers|alarms|reminders)\b|\bmy (timers|alarms|reminders)\b|\bwhen('s| is) my (alarm|timer|reminder)\b/.test(s)) {
    const L = [...MST.alarms].sort((a, b) => a.at - b.at);
    if (!L.length) return "Nothing's set.";
    return L.map(a => a.kind === "timer" ? `The ${a.what} timer has ${durText(a.at - Date.now())} left.` : a.kind === "alarm" ? `An alarm at ${hhmm(new Date(a.at))}.` : `A reminder at ${hhmm(new Date(a.at))} to ${flip(a.what)}.`).join(" ");
  }
  if (/^remind me\b/.test(s)) {
    let what = "", when = "";
    if ((m = s.match(/^remind me (in \d.+?|at \d.+?|tomorrow at \d.+?) (?:to|that|about) (.+)$/))) [when, what] = [m[1], m[2]];
    else if ((m = s.match(/^remind me (?:to |that |about )?(.+?) (in \d.*|at \d.*|tomorrow at \d.*)$/))) [what, when] = [m[1], m[2]];
    const at = !what ? 0 : when.startsWith("in ") ? (mdur(when) ? Date.now() + mdur(when) : 0) : +(clock(" " + when.replace(/^tomorrow /, "")) ?? 0);
    if (!at) return "When should I remind you? Say something like: remind me in 10 minutes to check the oven.";
    addAlarm(at, "reminder", what);
    return when.startsWith("in ") ? `Okay, in ${durText(at - Date.now())} I'll remind you to ${flip(what)}.` : `Okay, at ${hhmm(new Date(at))} I'll remind you to ${flip(what)}.`;
  }
  if (/\b(alarm|wake me)\b/.test(s) && !/\btimer\b/.test(s)) {
    const at = /\bin \d/.test(s) && mdur(s) ? new Date(Date.now() + mdur(s)) : clock(s);
    if (!at) return "For what time? Say something like: set an alarm for 7:30 am.";
    addAlarm(at, "alarm", "");
    return `Alarm set for ${hhmm(at)}${at.getDate() !== now.getDate() ? " tomorrow" : ""}, that's in ${durText(at - Date.now())}.`;
  }
  if (/\btimer\b|^count ?down\b/.test(s)) {
    const ms = mdur(s); if (!ms) return "How long? Say something like: set a timer for 5 minutes.";
    const what = durText(ms); addAlarm(Date.now() + ms, "timer", what);
    return `Timer set for ${what}.`;
  }
  if (/\bstopwatch\b/.test(s)) {
    if (/\b(start|begin|go)\b/.test(s) || !WATCH) { WATCH = Date.now(); return "Stopwatch started."; }
    const e = Date.now() - WATCH; if (/\b(stop|end|pause|reset)\b/.test(s)) WATCH = 0;
    return `${durText(e)}${WATCH ? " so far" : ""}.`;
  }

  // volume
  if (/\b(un ?mute|turn (the )?sound (back )?on)\b/.test(s)) return sys("mute", 0).then(r => r ? `Sound's back on, at ${r.vol}.` : "I couldn't reach the volume.");
  if (/\bmute\b|\bturn (the )?sound off\b|\bsilence\b/.test(s)) return sys("mute", 1).then(r => r ? "Muted." : "I couldn't reach the volume.");
  if (/\bvolume\b|\blouder\b|\bquieter\b|\bsofter\b|\bturn (it|the music|the sound|this) (up|down)\b|\bturn (up|down) (the )?(music|sound|volume)\b/.test(s)) {
    const n = /\b(max|maximum|full|all the way up)\b/.test(s) ? 100 : /\b(min|minimum|zero)\b/.test(s) ? 0 : +(s.match(/(\d+)(?: ?%| percent)?/)?.[1] ?? NaN), by = /\bby \d/.test(s);
    const go = /\b(up|louder|raise|increase|higher)\b/.test(s) ? 1 : /\b(down|quieter|softer|lower|decrease|reduce)\b/.test(s) ? -1 : 0;
    const p = go ? sys("volstep", go * (by && n ? n : 10)) : !isNaN(n) ? sys("volset", n) : sys("vol");
    return p.then(r => r ? (r.muted ? `Volume ${r.vol}, but it's muted.` : `Volume ${r.vol}.`) : "I couldn't reach the volume.");
  }

  // music
  if (/^(pause|stop)( the| this)?( music| song| track| spotify)?$|^(pause|stop) (it|playing|that)$/.test(s)) { if (playing) H?.media?.("playpause"); return playing ? "Paused." : "Nothing's playing."; }
  if (/^(play|resume|unpause|continue|start)( the| some| my)? (music|song|songs|spotify|tunes)$|^(resume|unpause|keep playing)$|^play$/.test(s)) { if (!playing) H?.media?.("playpause"); return playing ? "It's already playing." : "Playing."; }
  if (/^(next|skip)( the| this)?( song| track| one| it)?$|^skip (this|it|that)$|^next please$/.test(s)) { H?.media?.("next"); return "Skipped."; }
  if (/^(previous|last|go back|back)( the)?( song| track| one)?$|^play the (last|previous) (song|track|one)( again)?$/.test(s)) { H?.media?.("prev"); return "Going back."; }
  if (/\bwhat('s| is) (this|the|that) (song|track)\b|\bwhat('s| is) playing\b|\bwho (sings|is singing|made) (this|that)\b|\bwhat song is (this|that|playing)\b|\bwhat am i listening to\b/.test(s)) {
    const np = H?.nowPlaying; return np?.title ? `That's ${np.title}${np.artist ? " by " + np.artist : ""}${np.playing ? "" : ", paused"}.` : "Nothing's playing right now.";
  }

  // the PC
  if (/^(lock|lock (the |my )?(pc|computer|screen|it))$|\block my (pc|computer)\b/.test(s)) { sys("lock"); return "Locking up."; }
  if (/\bscreen ?shot\b|\bscreen capture\b|\bcapture the screen\b/.test(s)) { setTimeout(() => sys("shot"), 1200); return "Screenshot in a second. It goes in your Screenshots folder."; }
  if (/\b(show (me )?the desktop|minimi[sz]e (everything|all|all windows))\b/.test(s)) { sys("desk"); return "There's your desktop."; }
  if ((m = s.match(/\b(shut ?down|turn off|power off|restart|reboot|put (the )?(pc|computer) to sleep|go to sleep|sleep mode)\b/)) && /\b(pc|computer|shut ?down|restart|reboot|sleep|power off)\b/.test(s) && !/\b(music|song|track|game|level|video|timer|alarm)\b/.test(s)) {
    const op = /shut|turn off|power/.test(m[1]) ? "shutdown" : /restart|reboot/.test(m[1]) ? "restart" : "sleep";
    PEND = { until: Date.now() + 20000, run: () => setTimeout(() => sys(op), 1500), say: op === "sleep" ? "Good night, PC." : op === "restart" ? "Restarting in a few seconds." : "Shutting down in a few seconds. Bye!" };
    return `${op === "shutdown" ? "Shut down" : op === "restart" ? "Restart" : "Put"} the PC${op === "sleep" ? " to sleep" : ""}? Say yes to go ahead.`;
  }
  if (/\b(system|pc|computer) (status|stats|health|info)\b|\bhow('s| is) (my |the )?(pc|computer)( doing)?\b|\b(disk|drive|storage) space\b|\bspace (left|free)\b|\bhow much space\b|\b(gpu|graphics card) (temp|temperature|usage|load)\b|\bhow hot is (my |the )?(pc|gpu|graphics card|computer)\b|\b(memory|ram) (usage|use|used)\b|\bhow much (memory|ram)\b|\buptime\b|\bhow long has (my |the )?(pc|computer) been on\b/.test(s)) return mstats(s);

  // the weather
  if (/\bweather\b|\bforecast\b|\btemperature (outside|today|tomorrow)\b|\b(is it|will it|is it going to|gonna) (rain|snow)\b|\bhow (cold|warm|hot) is it( outside)?\b|\bdo i need an umbrella\b|\bsun ?(rise|set)\b|\bwhen does the sun (come up|go down|set|rise)\b|\bwhen does it get dark\b/.test(s)) return weather(s);

  // searching
  let site, q;
  if ((m = s.match(/^(?:search|look up|find)(?: on)? (youtube|wikipedia|reddit|google|spotify)(?: for)? (.+)$/))) [site, q] = [m[1], m[2]];
  else if ((m = s.match(/^(?:search(?: for)?|look up|find|google) (.+?) on (youtube|wikipedia|reddit|google|spotify)$/))) [site, q] = [m[2], m[1]];
  if (site) {
    const e = encodeURIComponent(q);
    if (site === "spotify") { H?.spotify?.best?.(q, "track"); return `Playing ${q}.`; }
    openUrl({ youtube: `https://www.youtube.com/results?search_query=${e}`, wikipedia: `https://en.wikipedia.org/w/index.php?search=${e}`, reddit: `https://www.reddit.com/search/?q=${e}`, google: `https://www.google.com/search?q=${e}` }[site]);
    return `Searching ${site[0].toUpperCase() + site.slice(1)} for ${q}.`;
  }
  if ((m = s.match(/^(?:google|search(?: the web| the internet| google)?(?: for)?|look up) (.+)$/))) { openUrl("https://www.google.com/search?q=" + encodeURIComponent(m[1])); return `Searching for ${m[1]}.`; }

  // sums, conversions
  if ((m = calc(s))) return m;
  if ((m = convert(s))) return m;

  // fun
  if (/\b(flip|toss) a coin\b|\bheads or tails\b|\bcoin (flip|toss)\b/.test(s)) return mpick(["Heads!", "Tails!"]);
  if ((m = s.match(/\broll (?:a |an |the |one |(\d+) )?(?:dice|die|d(\d+))\b/) ?? s.match(/\broll (\d+)?d(\d+)\b/))) {
    const k = Math.min(+(m[1] ?? 1) || 1, 20), sides = +(m[2] ?? 6) || 6, r = Array.from({ length: k }, () => 1 + Math.floor(Math.random() * sides));
    return k === 1 ? `You rolled a ${r[0]}.` : `You rolled ${r.join(", ")}. That's ${r.reduce((a, b) => a + b)}.`;
  }
  if ((m = s.match(/\b(?:random number|pick a number|number) (?:between|from) (-?\d+) (?:and|to) (-?\d+)\b/))) { const [a, b] = [+m[1], +m[2]].sort((x, y) => x - y); return `${a + Math.floor(Math.random() * (b - a + 1))}.`; }
  if (/\b(random number|pick a number)\b/.test(s)) return `${1 + Math.floor(Math.random() * 100)}.`;
  if (/\brock paper scissors\b/.test(s)) return mpick(["Rock!", "Paper!", "Scissors!"]);
  if ((m = s.match(/^(?:pick|choose|decide)(?: between| one)?:? (.+) or (.+)$/) ?? s.match(/^should i (.+) or (.+)$/))) return `${mpick([m[1], m[2]]).replace(/^(i |to )/, "")}. Definitely.`;
  if (/\bmagic (8|eight) ball\b/.test(s) || (!ai && /^should i\b|^will i\b|^is it a good idea\b/.test(s))) return mpick(["Yes, go for it.", "Hmm, I'd say no.", "Ask me again after a snack.", "Definitely.", "Not today.", "The bubbles say yes.", "The bubbles are unclear. Try again."]);
  if (/\b(tell me a |another |know any |got any )?jokes?\b|\bmake me laugh\b|\bsay something funny\b/.test(s)) return mpick(JOKES);
  if (/\b(fun|random|a|another|cool|interesting|fish|ocean) fact\b|^tell me something( interesting| cool| fun)?$|^fact$/.test(s)) return mpick(FACTS);

  // remembering things
  if ((m = s.match(/^(?:remember|note|make a note|take a note|write down|don't let me forget)(?: that| to)?:? (.+)$/))) { MST.notes.push({ t: m[1], at: Date.now() }); msave(); return `Got it, I'll remember ${flip(m[1])}.`; }
  if (/\bwhat (do you remember|did i (tell|ask) you to remember|have i told you)\b|\b(read|show) (me )?my notes\b|\bwhat are my notes\b|\bwhat did you remember\b/.test(s)) return MST.notes.length ? "You told me: " + MST.notes.map(n => flip(n.t)).join(". ") + "." : "You haven't asked me to remember anything yet.";
  if (/^forget (everything|all|all my notes|what i told you)$/.test(s)) { PEND = { until: Date.now() + 20000, run: () => { MST.notes = []; msave(); }, say: "Done, my mind's a blank bowl." }; return `Forget all ${plur(MST.notes.length, "note")}? Say yes to go ahead.`; }
  if (/^forget (that|the last( one| thing)?)$/.test(s)) { const n = MST.notes.pop(); msave(); return n ? `Forgot that ${flip(n.t)}.` : "There's nothing to forget."; }
  if ((m = s.match(/^where (?:is|are|did i (?:put|leave)|'s) (?:my |the )?(.+)$/) ?? s.match(/^where's (?:my |the )?(.+)$/))) {
    const k = nm(m[1]).split(" ").filter(w => w.length > 2), hit = MST.notes.filter(n => k.some(w => nm(n.t).includes(w))).pop();
    if (hit) return `You told me ${flip(hit.t)}.`; if (!ai) return `You didn't tell me where ${m[1]} ${/s$/.test(m[1]) ? "are" : "is"}.`;
  }

  // the desktop's own tabs, windows, and opening things
  if ((m = s.match(/^(?:go to|show(?: me)?|take me to|switch to|open)(?: my| the)? (games|game|music|videos|video|studio|web|internet|files|folders|settings|home)(?: tab| page| row)?$/))) {
    goCat(CATWORD[m[1]]); sync(); H?.openMenu?.(); return `Here's ${CATS[CATWORD[m[1]]]?.[0] ?? m[1]}.`;
  }
  if ((m = s.match(/^(?:switch to|go to|show(?: me)?|bring up|focus|back to|go back to)(?: the| my)? (.+?)(?: window)?$/))) { const w = findWin(m[1]); if (w) { H?.focus?.(w[3]); return `Here's ${w[0]}.`; } }
  if ((m = s.match(/^(?:close|quit|exit|kill)(?: the| my)? (.+?)(?: window| app)?$/))) {
    const w = findWin(m[1]); if (w && H?.close) { H.close(w[3]); return `Closing ${w[0]}.`; }
    if (!ai) return `I can't find a window called ${m[1]}.`;
  }
  if (/\b(play|start|launch|open) (a |some )?random game\b|\bpick a game( for me)?\b|\bwhat should i play\b/.test(s)) {
    const g = launchable().filter(it => it.cat === "Games" && it.act); if (!g.length) return "I don't see any games.";
    const it = mpick(g); if (/what should i play|pick a game/.test(s)) return `How about ${it.n}?`; activate(it); return `Let's go: ${it.n}.`;
  }
  if ((m = s.match(/^(?:open|launch|start|run|play|load|boot|fire)(?: up)? (.+)$/))) {
    const q = m[1].replace(/ (for me|now|up)$/, "");
    const it = /^play\b/.test(s) ? best(q, launchable().filter(x => !x.page), x => x.n) : findItem(q);   // "play": a game or a song, never a page from the history
    if (it) { activate(it); return `${/^(steam|epic|gog|uplay|origin|battlenet):/.test(it.id ?? "") || it.act === "game" || it.cat === "Games" ? "Starting" : "Opening"} ${it.n}.`; }
    const site = SITES[nm(q).replace(/^(the|my) /, "").replace(/ (website|site|page)$/, "").replace(/ dot com$/, "")]; if (site) { openUrl(site); return `Opening ${q}.`; }
    if (/^(?:play|put on)$/.test(s.split(" ")[0]) && H?.spotify?.best && !/^(a |the )?game$/.test(q)) {
      let k = "track", w = q.replace(/^(some|the song|song|the track|track)\b ?/, "");
      if (/^(the )?album\b/.test(w)) { k = "album"; w = w.replace(/^(the )?album ?/, ""); } else if (/^(the )?playlist\b/.test(w)) { k = "playlist"; w = w.replace(/^(the )?playlist ?/, ""); }
      else if (/^(some |music by |songs by |stuff by )/.test(q) || /^(some) /.test(m[1])) k = "artist";
      w = w.replace(/^(music|songs|stuff) by /, "").replace(/ by /, " ").replace(/ on spotify$/, "");
      if (w) { H.spotify.best(w, k); return `Playing ${q.replace(/ on spotify$/, "")}.`; }
    }
    if (!ai) return `I couldn't find ${q}.`;
  }

  if (!depth) { const n = near(s, ai); if (n) return localAsk(n, 1); }   // said another way
  if (ai) { if (!depth) miss("ai", t); return null; }
  // small talk: only without the AI (it does this better)
  if (/\bwhat can you do\b|^help( me)?$|\bwhat do you do\b|\bhow do (i|you) use you\b/.test(s)) return LOCAL_HELP;
  if (/\bwho are you\b|\bwhat are you\b|\bwhat('s| is) your name\b/.test(s)) return "I'm Marina, the little goldfish in the bowl up in the corner. Blub.";
  if (/\bhow are you\b|\bhow('s| is) it going\b|\bhow are things\b|\bhow do you feel\b/.test(s)) return mpick(["Swimming along nicely, thanks!", "Wet, as always. Happy, too.", "Doing great, the water's just right."]);
  if (/^(good night|night night|goodnight)\b/.test(s)) return "Good night! Sleep well.";
  if (/^(good morning)\b/.test(s)) return `Good morning! It's ${hhmm(now)}.`;
  if (/^(hi|hello|hey|yo|hiya|howdy|good (evening|afternoon))\b/.test(s)) return mpick(["Hi! What can I do?", "Hello! Blub.", "Hey there!"]);
  if (/^(thanks|thank you|cheers|ty|nice|great|awesome|cool|perfect|good job|well done)\b/.test(s)) return mpick(["Any time!", "Happy to help.", "Blub, you're welcome!"]);
  if (/\bi love you\b|\byou'?re (cute|the best|great|awesome)\b|\bgood (fish|girl|marina)\b/.test(s)) return mpick(["Aw, I'm blushing. Can fish blush?", "Right back at you!"]);
  if (/\bare you there\b|\bcan you hear me\b|^marina$/.test(s)) return "I'm here!";
  if (/\bare you (an? )?(ai|real|alive|robot)\b|\bwhere('s| is) (the |your )?(ai|brain)\b/.test(s)) return S.offline ? "We're offline, so right now I'm just a simple fish with a few tricks." : "My big brain isn't here right now, so I'm a simple fish with a few tricks.";
  if (!depth) miss("no ai", t);
  return (S.offline ? "We're offline, so my big brain is out of reach. " : "My big brain isn't here right now. ") + "Ask me what I can do.";
}
(document.readyState === "loading" ? addEventListener("DOMContentLoaded", rearm) : rearm());
globalThis.localAsk = localAsk; globalThis.Marina = { localAsk, norm, MST, flip, msave, LOCAL_HELP };   // settings.js: Settings › Marina
})();
