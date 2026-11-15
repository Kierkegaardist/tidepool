# live/ — what Claude edits to restyle Tidepool

The page reads `theme.json` at start and again whenever the host reports a change in this folder (no page reload). `live.css` loads after `ui.css`, so its rules win.
If `theme.json` is broken JSON, the page keeps its current look and logs a warning. Opened as a plain file (file://) the page can't read it and uses the built-in defaults in `core.js` / `index.html`.

## theme.json

| key | what | notes |
|---|---|---|
| `vars` | CSS custom properties set on `.ui` | type scale `--f-cap` … `--f-display` (CSS `font` shorthand), glass `--glass-top`, `--glass-bottom`, `--glass-blur`, `--glass-edge`; any other `--name` used in ui.css works too (e.g. `--accent`) |
| `rail` | rail categories, in order: `{ id: [name, icon, items] }` | `home` must stay (its items are built live). item = `[name, description, { ic, act, run }]`; icon `x.png` = `assets/icons/x.png`, `@x.svg` = `icons/x.svg` (paths from app/web). `{p}` in a description = the phase word ("tonight") |
| `scenes` | which photo scenes, in order | merged by `id` over core.js (the traced sky outline stays there). `img` path, `hz` horizon y, `water` y, `title` {x, b} for the big sky title, `ff` firefly box, `mask` sky mask PNG |
| `phases` | the 8 sun phases, merged by `id` | `scene` colours, `grade` (photo brightness/saturation/contrast/hue and layer strengths), `tc` CSS colours, `glass`, `ui` (greeting, note, track, `select` = the rail id the sun suggests). Ids and order are fixed |

Partial entries are fine: `{"phases":[{"id":"night","tc":{"accent":"#C4553A"}}]}` changes one colour and keeps the rest.
