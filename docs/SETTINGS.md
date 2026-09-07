# Settings files

Data folder: `%LOCALAPPDATA%\NNA Wallpaper\` (overridden with `--data <dir>` for portable use —
then it is `<dir>` next to the executable). All files are UTF-8 JSON, read at startup, on
`PUT /config` and on `--reload`; writes are atomic (temp file + rename).

## config/app.json

General settings. Fields (`AppSettings`):

```json
{
  "version": 1,
  "apiPort": 1618,
  "apiToken": "<generated on first run>",
  "language": "ru",
  "autostart": false,
  "updates": { "check": true, "channel": "stable" },
  "pauseOnFullscreen": true,
  "fpsCap": 30,
  "theme": {
    "preset": "nna1618",
    "palette": { "bgPage": "#050505", "bgSurface": "#000000", "fg": "#FFFFFF", "fgBody": "#C8C8C8",
                 "fgMuted": "#808080", "fgGhost": "#1D1D1D", "border": "#434343",
                 "glass": "rgba(139,139,139,0.03)" },
    "fonts": { "display": "Kharkiv Tone", "mono": "DM Mono" },
    "radius": 32, "gap": 24, "pad": 48, "blur": 14, "dim": 0.4
  },
  "audio": { "enabled": true, "device": null },
  "openWith": {},
  "openRoots": [],
  "graphs": {},
  "graphLimits": { "maxNodes": 100, "maxLinks": 450 },
  "weather": { "lat": 55.7558, "lon": 37.6173, "tz": "Europe/Moscow", "name": "MOSCOW" },
  "planner": {
    "supabaseUrl": "https://tvpmtjidonohpgwhyssm.supabase.co",
    "publishableKey": "<publishable, not secret; protected by row-level security>",
    "loginUrl": "https://planner.nna1618.com/desktop/login.html",
    "show": ["tasks", "meetings", "habits", "money", "briefing"]
  }
}
```

On a clean install `openWith` and `graphs` are empty and `openRoots` is an empty list; values with
machine-specific paths only appear after running `--import`. `audio.device` is `null` for the
default playback device's loopback capture.

## config/monitors.json

Per-monitor layouts.

```json
{
  "version": 1,
  "monitors": [
    {
      "id": "\\\\.\\DISPLAY2|3440x1440",
      "name": "Main 3440x1440",
      "enabled": true,
      "grid": { "cols": 3, "rows": 16, "colWeights": [2.2, 1, 2.2], "gap": 24, "pad": 48 },
      "blocks": [
        { "widget": "eq", "col": 1, "colSpan": 1, "row": 1, "rowSpan": 4 },
        { "widget": "photos", "col": 1, "colSpan": 1, "row": 5, "rowSpan": 12 },
        { "widget": "focus", "col": 2, "colSpan": 1, "row": 1, "rowSpan": 8 },
        { "widget": "weather", "col": 2, "colSpan": 1, "row": 9, "rowSpan": 8 },
        { "widget": "events", "col": 3, "colSpan": 1, "row": 1, "rowSpan": 4 },
        { "widget": "launch", "col": 3, "colSpan": 1, "row": 5, "rowSpan": 4,
          "settingsOverride": { "compact": true } },
        { "widget": "graph", "col": 3, "colSpan": 1, "row": 9, "rowSpan": 8 }
      ]
    },
    {
      "id": "\\\\.\\DISPLAY1|1440x2560",
      "name": "Vertical 1440x2560",
      "enabled": true,
      "grid": { "cols": 1, "rows": 4, "gap": 24, "pad": 48 },
      "blocks": [
        { "widget": "stats", "col": 1, "colSpan": 1, "row": 1, "rowSpan": 1 },
        { "widget": "planner", "col": 1, "colSpan": 1, "row": 2, "rowSpan": 1 },
        { "widget": "player", "col": 1, "colSpan": 1, "row": 3, "rowSpan": 1 },
        { "widget": "launch", "col": 1, "colSpan": 1, "row": 4, "rowSpan": 1 }
      ]
    }
  ]
}
```

A monitor's identity key is `"{DeviceName}|{Width}x{Height}"` rather than `\\.\DISPLAYn`, because
the latter is not stable across reboots. A monitor with no matching entry gets a default layout:
landscape monitors get the "Main" layout, portrait monitors get the "Vertical" layout.

## config/widgets/<id>.json

Keys follow the widget's manifest `settings[]`. Examples:

```json
// focus.json
{ "work": 25, "rest": 5, "longRest": 15, "sessionsBeforeLong": 4, "autoNext": false,
  "presets": [ { "id": "classic", "label": "25 / 5 x4", "work": 25, "rest": 5, "longRest": 15, "per": 4 } ] }
```

```json
// photos.json
{ "folder": "", "intervalMin": 10, "order": "sequential", "fadeMs": 1400, "zoom": true, "zoomScale": 1.08 }
```

```json
// eq.json
{ "bars": 56, "attack": 0.55, "release": 0.075, "gain": 1.7, "minHeight": 0.02 }
```

## launch.json, events.json

Same shape as the files produced by an older Wallpaper Engine + Python-helper setup, so
`--import` can bring them in unchanged; also editable through forms in the settings window.

## Settings window: shell + pages (v2)

`settings/index.html` is a macOS System Settings-style shell: a 233px sidebar (search box, then
one row per page, greek index α..κ + a small inline-SVG icon + label) on the left, and a page
header (greek + title + the save-status pill) plus a scrollable content area of "group cards" on
the right — the owner's chosen reference is macOS, the palette/type stay on tokens v3 (see
`docs/DESIGN-SYSTEM.md`). This replaced the original five hand-rolled tabs (α..ε rendered by one
`app.js`); the old tab shape is still reachable through `?tab=` compatibility aliases (below).

### File layout

| File | Role |
|---|---|
| `settings/index.html` | loads `ui/logic.js` + `ui/components.js` (classic scripts, so `window.NNAUI` exists) then `settings/shell.js` as an ES module. |
| `settings/shell.js` | boots (`GET /config/full`, `GET /widgets`), renders the sidebar + header, owns routing (`?tab=`, hash-free — page switches are in-memory) and the search box. |
| `settings/shell-logic.js` | pure functions behind the shell: `mapTabName`, `matchesQuery`, `filterSidebar`, `firstMatch` — no DOM, covered by `settings/tests/shell.test.mjs` under plain `node --test`. |
| `settings/api.js` | `api()`/`put()` (fetch wrapper + `X-Token`), `makeScheduler()` (debounced save), `setStatus(text, isError)` (status pill + error toast). Moved out of the old `app.js` so every page shares one copy. |
| `settings/dom.js` | `el()` (the same tiny DOM builder that used to be copy-pasted in `app.js`/`forms.js`/`taskbar-tab.js`), `groupCard(id, title, ...rows)`, `settingRow(title, help, control)` — the "card with rows" building blocks every new page uses. |
| `settings/icons.js` | inline-SVG line icons (16x16, 1.5px stroke, no icon sets) — one per sidebar page plus a few utility glyphs (search, drag handle, trash, folder, check). |
| `settings/i18n.js` | the ru/en dictionary + `makeT()`/`detectLang()` (unchanged shape) plus `SAVED_MARKERS` (both languages' "Saved" string, used by `api.js`'s `setStatus` to light the pill green without a separate flag). |
| `settings/forms.js`, `settings/taskbar-tab.js` | unchanged internals, reused by the pages below. |
| `settings/layout-editor.js` | the Layout page's controller (`mountLayoutTab`): per-monitor working state, undo/redo history, the throttled `/layout/preview` channel, Apply/Cancel/Reset — see "Layout: canvas, live preview, Apply/Cancel/Undo" below. |
| `settings/layout-model.js` | pure layout math shared with `settings/tests/layout-model.test.mjs` (`node --test`, no DOM): `clampToGrid`/`moveBlock`/`resizeBlock`/`validate`/`findOverlaps`/`findFreeSlot`/`addBlock`/`removeBlock`/`createHistory`/`defaultLayoutFor`. |
| `settings/layout-canvas.js` | the canvas: monitor minimap, the editable grid (drag/resize/select/delete, keyboard shortcuts), and the widget palette — presentational only, reports intents to `layout-editor.js` via a small `actions` callback set. |
| `settings/layout.css` | the Layout page's own stylesheet (`.lay-*` classes), loaded from `settings/index.html` after `design-system.css`. |
| `settings/pages/*.js` | one module per sidebar page (see the table below). |
| `settings/app.js` | now a one-line `import "./shell.js"` — kept only so `<script src="app.js">` still resolves if anything external still points at it. |

### Pages (sidebar order)

| # | Greek | Page key | Module | Notes |
|---|---|---|---|---|
| 1 | α | `layout` | `pages/layout.js` | thin wrapper owning this page's own ru/en dictionary (`i18n.js` was being edited by another agent this stage) that delegates to `layout-editor.js`'s `mountLayoutTab` — see "Layout: canvas, live preview, Apply/Cancel/Undo" below. |
| 2 | β | `blocks` | `pages/blocks.js` | widget/launch/events row list (icon + on/off toggle) on the left, `forms.js`-generated form on the right. The toggle flips `widgetSettings[id].enabled` (default true) — a forward-compatible flag, not read by any widget yet; full preview cards are a later stage. |
| 3 | γ | `appearance` | `pages/appearance.js` | dim/radius/gap/pad/blur/fps/pause — the old geometry-only γ tab, rebuilt on `NNAUI.slider`/`NNAUI.toggle`, plus a "font and accent" card (owner decision D6): `theme.fontScale` (90/100/110%, segmented) and `theme.accent` (`none`/`signal`/`chrome`, segmented) — read by the wallpaper page. |
| 4 | δ | `topbar` | `pages/topbar.js` | new page: our own always-on-top bar (`app.topBar`). Enable/monitors/height, style (segmented mode + color + opacity), auto-hide/reserve-space, and a drag-and-drop module editor across three zones (left/center/right, HTML5 DnD). Replaces the inline "top bar" section the old ζ tab used to render. |
| 5 | ε | `dock` | `pages/dock.js` | new page, `app.dock` (`DockSettings` in `AppSettings.cs`) — enable/monitors/size, magnify, auto-hide/reserve-space, folders/pinned/trash/running, style (segmented mode + palette-swatch color + opacity). Round-trips through `PUT /config` like every other page. |
| 6 | ζ | `taskbar` | `pages/taskbar.js` | the real Windows taskbar: preset picker (a collapsed "Advanced" card holds JSON import/export) + per-state styling + Windows toggles, moved in from `taskbar-tab.js` (now `mountTaskbarTab(el, {..., showTopBar:false})` — the top-bar section moved to its own page, item 4). Adds a "Windows taskbar mode" select (`normal` / `autohide` / `win-only`, `TaskbarWindowsSettings.Mode`) next to the existing tri-state toggles. Fully on the design system now: `NNAUI.select`/`toggle`/`slider`, `.ui-btn`, and `settings/icons.js` glyphs (`chevron-up/down`, `close`, `arrow-up/down`) instead of the old ▲/▼/✕ text symbols. |
| 7 | η | `cursor` | `pages/cursor.js` | the three brand cursor variants (`GET/POST /cursor/*`), preview cards built from the real `arrow.svg`/`hand.svg` copied to `settings/assets/cursors/<variant>/` (source: `brand/cursors/<variant>/*.svg`), size 32/48/64, apply/reset, backup status. |
| 8 | θ | `planner` | `pages/planner.js` | login status + what-to-show — old δ tab, carried over near-verbatim; redesign is a later stage. |
| 9 | ι | `general` | `pages/general.js` | autostart, language, API port, updates, **microphone pick** (new — `GET/PUT /audio/capture-device`, used by the planner voice block), import from folder, data/log folders. |
| 10 | κ | `about` | `pages/about.js` | new page: mark (`settings/assets/mark-white-64.png`, copied from `H:\brand\nna1618_mark_v2\png\nna1618_mark_white_64.png`), name, live version (`GET /health`), the `NNA1618 CLUSTER` signature, links (repository/releases/licenses/changelog), MIT note, "check updates" button. |

### Layout: canvas, live preview, Apply/Cancel/Undo (owner decision D16)

The Layout page (`pages/layout.js` + `layout-editor.js` + `layout-canvas.js` + `layout-model.js` +
`layout.css`) is a monitor/grid/block editor built on live, flicker-free preview instead of
autosave: every structural edit streams to the wallpaper via `POST /layout/preview` while you work,
and only **Apply** writes it to `monitors.json`.

- **Monitor source**: `liveMonitors` from `GET /config/full` is fetched once at shell boot and can
  go stale, so the page re-fetches `GET /health` on every visit for fresh `id/name/width/height/x/y`
  (same shape). `IHostApp.Monitors` is always empty under `--headless` (see `HeadlessHostApp.cs`),
  so when there is still no live monitor after that, the page synthesizes one per entry in
  `monitors.json` by parsing `"{name}|{w}x{h}"` out of its `id` and arranging them left to right —
  this is what makes the page (and `tests/layout-editor-probe.mjs`) usable/testable headless, and
  also keeps a temporarily-unplugged monitor's layout editable.
- **Canvas** (`layout-canvas.js`, presentational only): a minimap of every monitor in its real
  mutual position (hidden when there is only one), and a big editable grid for the selected
  monitor — thin `Slate` grid lines honoring `colWeights`/`gap`/`pad`, hatched `Steel` bars for a
  reserved top bar/dock (`app.topBar`/`app.dock`) when enabled, block cards (icon from
  `settings/icons.js` by the widget manifest's `icon` field, else the generic "blocks" tile —
  never a bare letter initial; name from the manifest's `title:{ru,en}`, falling back to `name`;
  delete button; a single bottom-right resize handle), a widget palette on the right (click adds via `layout-model.js`'s `addBlock`
  auto-placement), and keyboard shortcuts on the focused canvas (arrows move, Shift+arrows resize,
  Delete/Backspace removes, Ctrl+Z/Ctrl+Y undo/redo). Dragging shows a dashed "ghost" at the
  snapped target cell while the card itself follows the pointer. It never mutates model state or
  re-renders itself mid-gesture — see the `onBlockSelect` note in `layout-editor.js` below.
- **Model** (`layout-model.js`, pure, DOM-free, `node --test`-covered): `clampToGrid`, `moveBlock`,
  `resizeBlock`, `findOverlaps`/`validate`, `findFreeSlot`, `addBlock`, `removeBlock`, and
  `createHistory(limit=55)` — a small deep-cloning undo/redo stack.
- **Controller** (`layout-editor.js`): owns one working layout record per monitor (seeded from
  `monitors.json`, defaulted via `defaultLayoutFor` for a monitor with no saved entry), a
  `lastApplied` snapshot per monitor (the on-disk baseline), and a `layout-model` history per
  monitor. Every structural change is throttled (80ms, leading+trailing) to `POST /layout/preview
  {monitorId, blocks}` — nothing is sent on page open. **Apply** validates every monitor
  client-side first (disabled otherwise), then `PUT /config {monitors:{...}}` (a 400 shows the
  server's message via `onStatus` and leaves the working edit and its preview alone — no silent
  revert). **Cancel** restores the selected monitor to `lastApplied` and re-sends its preview
  immediately (not throttled) so the desktop snaps back. **Reset to default** asks
  `NNAUI.dialog` for confirmation, then loads `GET /config/defaults?monitor=<id>` (falling back to
  the same `defaultLayoutFor` the host's `LayoutResolver.cs` uses) into the working layout —
  still just a preview until Apply. Leaving the page (or closing the window) with unapplied edits
  re-previews every dirty monitor's `lastApplied` blocks so the desktop matches disk again — shell.js
  has no page-lifecycle hook, so this is done with a `MutationObserver` watching the page root for
  disconnection, plus a `beforeunload` listener as a backstop.
  - **Why `onBlockSelect` never re-renders**: it fires from inside the block/handle's own
    `pointerdown` handler in `layout-canvas.js`, *before* that handler calls
    `setPointerCapture`/wires its `pointermove`/`pointerup` listeners. A synchronous full canvas
    re-render at that point (`canvas.update()` rebuilds `.lay-grid`'s `innerHTML`) replaces the
    very DOM node the gesture is about to capture, which implicitly releases pointer capture per
    spec and silently breaks every drag/resize before it starts. The block/handle's own
    `pointerup` always ends in a `commit: true` `onBlockMove`/`onBlockResize` call, which does
    re-render — the `.sel` highlight lands right after release instead of before.
- **CSS note**: `.lay-actions` (Apply/Cancel/Undo/Redo/Reset) is a normal in-flow bottom bar, not
  `position: sticky` — a sticky element keeps its flow height but paints pinned to the viewport
  edge, which on this page's height can visually collide with the canvas once the page scrolls.
  Likewise `.lay-stage` aligns its grid `flex-start` (not `center`): a centered flex child taller
  than its (possibly short) container overflows symmetrically on both sides, and browsers cannot
  scroll to the part that overflowed *above*/*left of* the container's own box — `scrollIntoView`
  and manual `scrollTop` both silently no-op. `.lay-grid` uses `margin: auto` instead, which still
  centers it when it fits without any overflow-side effects.
- **Probe**: `tests/layout-editor-probe.mjs [port]` (default 1633) starts its own throwaway
  `--headless` host (own `--data` tmp dir seeded from a copy of `%LOCALAPPDATA%\NNA Wallpaper\config\`
  plus a deterministic two-monitor `monitors.json` fixture — the owner's real vertical monitor is
  single-column, which makes "drag one cell right" meaningless), drives it over CDP in headless
  Edge at a forced 1100x720 viewport (`Emulation.setDeviceMetricsOverride` — `--headless=new`'s
  `--window-size` is not reliable for the actual content viewport), and checks: drag STATS one
  cell right (`/events/stats.sent` grows within ~400ms, DOM moves), Cancel (DOM reverts), drag +
  Apply (`/config/full` persists, a `monitors.json.bak-*` appears), delete (DOM removes), Ctrl+Z
  (DOM restores). Screenshots go to `H:\night-runs\nna-wallpaper-2\shots\stage4-layout-*.png`.
  Stops its own host by PID; never touches the owner's live instance on 1618.

### `?tab=` compatibility

`settings/shell-logic.js`'s `mapTabName(tab)` resolves both the current page keys (`layout`,
`blocks`, ..., `about`) and every value the old five-tab shell understood: the pre-redesign greek
keys (`alpha`→layout, `beta`→blocks, `gamma`→appearance, `delta`→planner, `epsilon`→general,
`zeta`→taskbar) and the even older aliases (`launch`/`events`→blocks with a sub-selection,
`config`/`nna-config`/`general`→general, `theme`→appearance). Anything unrecognised falls back to
`layout`. The WPF `SettingsWindow` keeps loading `/settings/?token=&tab=&lang=` unchanged.

### How to add a page

1. Create `settings/pages/<name>.js` exporting `render(container, ctx)` and, if it should be
   found by search, `keywords(lang)` (a flat array of ru/en strings — group titles, notable field
   names). `ctx` = `{ t, lang, api, put, config, widgets, selection, setSelection, setLang,
   onStatus, fetchDefaults }` — `config`/`widgets` are the live objects from `GET /config/full` /
   `GET /widgets`, shared across every page for the life of the window (mutate them in place after
   a successful save so switching pages shows fresh data without a reload).
2. Build the page body from `groupCard(id, titleText, ...rows)` and `settingRow(titleText,
   helpText, control)` (`settings/dom.js`) — `id` on `groupCard` is what the search highlighter
   matches against. Prefer `window.NNAUI.select/toggle/slider/segmented` for controls; wrap
   `select`/`slider` in `settingRow` (not directly in a bare flex row) — both set `width:100%` on
   themselves, and `settingRow`'s `.row-control` column is what caps that width so it doesn't
   swallow the row's label. Wrap the page body in `.page-narrow` or `.page-wide` (design-system.css
   sets both to `max-width: 760px` — one content width across every page, System Settings-style;
   the layout (α) canvas is the deliberate exception, see `layout.css`'s `.lay-page`). A surface
   color needs a palette, not a picker: use `paletteSwatchField(mount, t, hexValue, onChange)`
   (`dom.js`) rather than `<input type="color">`. A collapsed-by-default card (JSON import/export,
   advanced settings) is `collapsibleCard(id, titleText, ...rows)`.
3. Register the page in `settings/shell.js`'s `PAGES` array (icon in `settings/icons.js`'s
   `ICONS`, nav label key `nav_<name>` in `i18n.js`), and add the key to
   `settings/shell-logic.js`'s `mapTabName` passthrough list.
4. Add any new i18n keys to `settings/i18n.js` (ru + en). No build step — the page is picked up on
   next reload of `/settings/`.

### Search

The sidebar's search box filters the nav list live: `settings/shell-logic.js`'s `filterSidebar`
matches the query (case/whitespace-insensitive substring) against each page's nav label and its
`keywords(lang)`; non-matching rows get `.dim` (still clickable, just faded) rather than being
removed, so the list doesn't jump around while typing. Enter opens the first match
(`firstMatch`). On the open page, `shell.js` also scans the rendered `.group-title` elements and
adds `.match` (a Signal-colored left border) to any group whose title contains the query — a
light-weight, DOM-scan approach rather than routing every page's groups through a shared
metadata structure.

### Dock and taskbar mode: now persisted

`AppSettings.Dock` (`DockSettings`) and `TaskbarWindowsSettings.Mode` both landed in this branch's
`AppSettings.cs`, so `pages/dock.js` and the taskbar page's "Windows taskbar mode" select round-trip
through `PUT /config`/`GET /config/full` like every other field — no more "not persisted yet" hint.

### Palette-only surface colors (no color pickers)

Every surface-color row (`pages/topbar.js`, `pages/dock.js`, `pages/taskbar.js`'s per-state cards,
`forms.js`, `pages/blocks.js`'s widget `color` settings) uses `dom.js`'s `paletteSwatchField`
instead of a native `<input type="color">`: a `NNAUI.segmented` restricted to the four neutral
palette tones (Base/Surface/Slate/Steel — see `docs/DESIGN-SYSTEM.md`), each item prefixed with a
small color swatch. The stored value is still a plain hex string; only the picker UI changed.

### Signal is a mark, not a color for text or a full border

Per the brand rule (Signal only as a dot/serif/underline, never body text or a whole shape's
outline): error/match/conflict states now keep their frame neutral (Slate/Line) and their text
Ash, with Signal reduced to a small dot or a 2px left notch — see `.hdr-status.err`, `.group.match`,
`.toast`, `.lay-block.bad`, `.lay-palette-item.no-room` in `design-system.css`/`layout.css`.

Window rules (unchanged): no emoji, no exclamation marks, no em/en dashes, uppercase mono labels
where the old tabs used them, dark theme, default size 1100x720, minimum 900x600. Enforced for
every settings page's string literals by `settings/tests/voice.test.mjs` (`node --test`).

---

# Файлы настроек (RU)

Папка данных: `%LOCALAPPDATA%\NNA Wallpaper\` (переопределяется `--data <папка>` для portable —
тогда это `<папка>` рядом с exe). Все файлы — JSON в UTF-8, читаются при старте, при `PUT /config`
и по `--reload`; запись атомарная (временный файл + переименование).

## config/app.json

Общие настройки — поля показаны в английской части этого файла (класс `AppSettings`). На чистой
установке `openWith` и `graphs` пустые, `openRoots` — пустой список; значения с путями конкретной
машины появляются только после `--import`.

## config/monitors.json

Раскладки по мониторам: `grid {cols, rows, colWeights?, gap, pad}` и список `blocks[]`
(`{widget, col, colSpan, row, rowSpan, settingsOverride?}`). Идентификатор монитора —
`"{DeviceName}|{Width}x{Height}"` (не `\\.\DISPLAYn`, так как это имя не стабильно между
перезагрузками). Монитор без записи получает раскладку по умолчанию: альбомный — как «Main»,
портретный — как «Vertical».

## config/widgets/<id>.json

Ключи по `settings[]` манифеста виджета — примеры для `focus`, `photos`, `eq` приведены в
английской части.

## launch.json, events.json

Формат совпадает с файлами старой связки Wallpaper Engine + Python-помощник, поэтому `--import`
переносит их без изменений; также редактируются формами в окне настроек.

## Окно настроек: сайдбар и страницы (v2)

`settings/index.html` теперь построено как macOS System Settings: слева сайдбар 233px (поиск,
затем по строке на страницу — греческий индекс α..κ, inline-SVG иконка, подпись), справа —
заголовок страницы (греческая буква + название + плашка статуса сохранения) и прокручиваемая
область карточек-групп. Ориентир — macOS, палитра и шрифты только из токенов v3 (см.
`docs/DESIGN-SYSTEM.md`). Заменяет прежние пять вкладок (α..ε, все в одном `app.js`); старые
`?tab=` значения по-прежнему работают через алиасы (см. ниже).

### Структура файлов

`settings/shell.js` — загрузка (`GET /config/full`, `GET /widgets`), сайдбар, заголовок,
маршрутизация и поиск. `settings/shell-logic.js` — чистые функции без DOM (`mapTabName`,
`matchesQuery`, `filterSidebar`, `firstMatch`), покрыты `settings/tests/shell.test.mjs` под
`node --test`. `settings/api.js` — `api()`/`put()`, `makeScheduler()` (отложенное сохранение),
`setStatus(text, isError)` (плашка статуса + тост об ошибке) — вынесены из старого `app.js`, чтобы
каждая страница не копировала их заново. `settings/dom.js` — `el()` (тот же построитель DOM, что
раньше был скопирован в `app.js`/`forms.js`/`taskbar-tab.js`), `groupCard`, `settingRow` — базовые
блоки «карточка с группой строк», на которых построены новые страницы. `settings/icons.js` —
inline-SVG линейные иконки (16×16, 1.5px), без готовых наборов. `settings/i18n.js` — тот же
словарь ru/en плюс `SAVED_MARKERS` (строки «Сохранено»/«Saved» для подсветки плашки зелёным).
`settings/forms.js`, `settings/taskbar-tab.js` — без изменений логики. `settings/layout-editor.js` —
контроллер страницы «Раскладка» (состояние по монитору, история отмены, троттлинг
`/layout/preview`, Применить/Отменить/Сброс). `settings/layout-model.js` — чистая математика
раскладки, покрыта `settings/tests/layout-model.test.mjs`. `settings/layout-canvas.js` — холст
(мониторы, сетка, перетаскивание/растягивание, палитра виджетов), только представление. Детали —
в разделе «Раскладка: холст, живой предпросмотр, Применить/Отменить/Отмена» ниже.
`settings/pages/*.js` — по одному модулю на страницу сайдбара.
`settings/app.js` — теперь `import "./shell.js"` в одну строку, оставлен для обратной
совместимости.

### Страницы (порядок в сайдбаре)

1. **Раскладка (α)** — `pages/layout.js`, тонкая обёртка (свой словарь ru/en, т.к. `i18n.js`
   параллельно правит другой агент), делегирует `layout-editor.js`. Подробности — ниже.
2. **Блоки (β)** — `pages/blocks.js`, список виджетов/запуска/событий строками (иконка +
   переключатель) слева, форма справа. Переключатель — `widgetSettings[id].enabled` (по умолчанию
   включён), на перспективу; ни один виджет пока его не читает.
3. **Внешний вид (γ)** — `pages/appearance.js`, геометрия/производительность на
   `NNAUI.slider`/`NNAUI.toggle`.
4. **Верхняя строка (δ)** — новая страница `pages/topbar.js`: включение, мониторы, высота, стиль
   (сегменты режима + цвет + непрозрачность), автоскрытие, резерв места, редактор модулей
   drag-and-drop по трём зонам (слева/центр/справа).
5. **Док (ε)** — новая страница `pages/dock.js`, `app.dock` (см. «Док: пока не сохраняется» ниже).
6. **Панель задач (ζ)** — `pages/taskbar.js`, перенос `taskbar-tab.js` (пресет + панель Windows;
   секция «верхняя строка» из него убрана — теперь отдельная страница δ) плюс новый select режима
   панели Windows (обычная / автоскрытие / только по Win).
7. **Курсор (η)** — новая страница `pages/cursor.js`, три варианта, превью из настоящих
   `arrow.svg`/`hand.svg` (скопированы в `settings/assets/cursors/<variant>/` из
   `brand/cursors/<variant>/*.svg`), размер 32/48/64, применить/сбросить.
8. **Планировщик (θ)** — `pages/planner.js`, перенос без изменений.
9. **Общие (ι)** — `pages/general.js`, плюс новый выбор микрофона (`GET/PUT
   /audio/capture-device`).
10. **О программе (κ)** — новая страница `pages/about.js`: знак (`settings/assets/mark-white-64.png`,
    копия `H:\brand\nna1618_mark_v2\png\nna1618_mark_white_64.png`), версия из `/health`, подпись
    `NNA1618 CLUSTER`, ссылки, MIT.

### Раскладка: холст, живой предпросмотр, Применить/Отменить/Отмена (решение владельца D16)

Страница «Раскладка» (`pages/layout.js` + `layout-editor.js` + `layout-canvas.js` +
`layout-model.js` + `layout.css`) — редактор мониторов/сетки/блоков на живом предпросмотре без
автосохранения: каждое структурное изменение течёт на обои через `POST /layout/preview` сразу же,
на диск пишет только кнопка **Применить**.

- **Источник мониторов**: `liveMonitors` из `GET /config/full` получен один раз при загрузке окна
  и может устареть, поэтому страница при каждом открытии дополнительно опрашивает `GET /health` за
  свежими `id/name/width/height/x/y`. `IHostApp.Monitors` всегда пуст под `--headless` (см.
  `HeadlessHostApp.cs`) — если живых мониторов всё равно нет, страница собирает их из
  `monitors.json`, разбирая `id` вида `"{имя}|{ш}x{в}"` и расставляя слева направо; это и делает
  страницу тестируемой без реального экрана (`tests/layout-editor-probe.mjs`), а заодно позволяет
  редактировать раскладку временно отключённого монитора.
- **Холст** (`layout-canvas.js`, только представление): миниатюра всех мониторов в реальном
  взаимном расположении (скрыта при одном мониторе), крупная редактируемая сетка выбранного —
  тонкие линии Slate с учётом `colWeights`/`gap`/`pad`, штриховка Steel для резерва верхней
  строки/дока (`app.topBar`/`app.dock`), карточки блоков (иконка из `settings/icons.js` по id
  виджета, иначе первая буква; кнопка удаления; одна ручка изменения размера в правом нижнем
  углу), палитра виджетов справа (клик добавляет через авто-размещение `layout-model.js`
  `addBlock`), клавиатура на сфокусированном холсте (стрелки — двигать, Shift+стрелки — менять
  размер, Delete/Backspace — удалить, Ctrl+Z/Ctrl+Y — отмена/повтор). Перетаскивание рисует
  штриховую тень-цель в снапнутой ячейке, сама карточка следует за курсором. Холст никогда не
  меняет модель и не перерисовывает себя посреди жеста — см. заметку про `onBlockSelect` ниже.
- **Модель** (`layout-model.js`, чистые функции, без DOM, покрыта `node --test`): `clampToGrid`,
  `moveBlock`, `resizeBlock`, `findOverlaps`/`validate`, `findFreeSlot`, `addBlock`, `removeBlock`,
  `createHistory(limit=55)` — небольшой стек отмены/повтора с глубоким клонированием снимков.
- **Контроллер** (`layout-editor.js`): держит рабочую раскладку на каждый монитор (из
  `monitors.json`, для монитора без записи — `defaultLayoutFor`), снимок `lastApplied` на монитор
  (то, что реально на диске) и историю `layout-model` на монитор. Любое структурное изменение идёт
  с троттлингом (80 мс, по переднему и заднему фронту) в `POST /layout/preview {monitorId,
  blocks}` — при открытии страницы ничего не шлётся. **Применить** сначала проверяет валидность
  всех мониторов на клиенте (иначе кнопка недоступна), затем `PUT /config {monitors:{...}}`
  (при 400 текст ошибки показывается через `onStatus`, рабочее состояние и его предпросмотр не
  сбрасываются). **Отменить** возвращает выбранный монитор к `lastApplied` и сразу (без
  троттлинга) шлёт его предпросмотр, чтобы стол вернулся немедленно. **Сброс к умолчанию** просит
  подтверждение через `NNAUI.dialog`, затем грузит `GET /config/defaults?monitor=<id>` (при ошибке
  — тот же `defaultLayoutFor`, что и `LayoutResolver.cs` на хосте) в рабочую раскладку — тоже
  только предпросмотр, до нажатия Применить. Уход со страницы (или закрытие окна) с неприменёнными
  правками возвращает предпросмотр каждого «грязного» монитора к его `lastApplied` — у `shell.js`
  нет хука жизненного цикла страницы, поэтому это сделано через `MutationObserver` за отключением
  корня страницы от документа плюс `beforeunload` как страховка.
  - **Почему `onBlockSelect` никогда не перерисовывает**: он вызывается изнутри обработчика
    `pointerdown` самого блока/ручки в `layout-canvas.js`, ДО того как этот обработчик вызовет
    `setPointerCapture` и подключит свои `pointermove`/`pointerup`. Синхронная полная перерисовка
    холста в этот момент (`canvas.update()` перестраивает `innerHTML` у `.lay-grid`) заменяет тот
    самый DOM-узел, на который жест только собирается захватить указатель — по спецификации это
    неявно снимает захват указателя и молча ломает любое перетаскивание/изменение размера ещё до
    его начала. Собственный `pointerup` блока/ручки всегда заканчивается вызовом
    `onBlockMove`/`onBlockResize` с `commit: true`, который и перерисовывает — подсветка `.sel`
    появляется сразу после отпускания, а не до него.
- **Заметка про CSS**: `.lay-actions` (Применить/Отменить/Отмена последнего/Повторить/Сброс) —
  обычная панель в потоке документа, не `position: sticky` — липкий элемент сохраняет высоту в
  потоке, но рисуется прижатым к краю окна просмотра, из-за чего на высоте этой страницы он мог
  визуально наложиться на холст при прокрутке. Аналогично `.lay-stage` выравнивает сетку по
  `flex-start`, а не по центру: отцентрированный flex-элемент выше своего (возможно невысокого)
  контейнера переполняет его симметрично с обеих сторон, а браузер не может прокрутить к части,
  вышедшей ЗА пределы контейнера сверху/слева — `scrollIntoView` и ручной `scrollTop` в этом случае
  молча ничего не делают. `.lay-grid` вместо этого использует `margin: auto` — по-прежнему
  центрируется, когда помещается, без побочных эффектов переполнения.
- **Проба**: `tests/layout-editor-probe.mjs [port]` (по умолчанию 1633) поднимает свой временный
  `--headless`-хост (свой `--data`, копия `%LOCALAPPDATA%\NNA Wallpaper\config\` плюс
  детерминированная заглушка `monitors.json` на два монитора — у настоящего вертикального монитора
  владельца всего одна колонка, «сдвинуть на ячейку вправо» там бессмысленно), водит его по CDP в
  headless Edge с принудительным вьюпортом 1100×720 (`Emulation.setDeviceMetricsOverride` —
  `--window-size` у `--headless=new` не всегда даёт нужный вьюпорт контента), проверяет:
  перетаскивание STATS на ячейку вправо (`/events/stats.sent` растёт за ~400 мс, блок сдвигается в
  DOM), Отменить (блок возвращается), перетаскивание + Применить (`/config/full` меняется,
  появляется `monitors.json.bak-*`), удаление (блок исчезает), Ctrl+Z (блок возвращается).
  Скриншоты — в `H:\night-runs\nna-wallpaper-2\shots\stage4-layout-*.png`. Останавливает свой хост
  по PID, экземпляр владельца на 1618 не трогает.

### Совместимость `?tab=`

`mapTabName` в `settings/shell-logic.js` понимает и новые ключи страниц, и старые греческие имена
дореформенных вкладок (`alpha`→layout, `beta`→blocks, `gamma`→appearance, `delta`→planner,
`epsilon`→general, `zeta`→taskbar), и более старые алиасы (`launch`/`events`→blocks с
под-выбором, `config`/`nna-config`/`general`→general, `theme`→appearance). Неизвестное значение —
`layout`. WPF `SettingsWindow` по-прежнему открывает `/settings/?token=&tab=&lang=` без изменений.

### Как добавить страницу

Создать `settings/pages/<name>.js` с `render(container, ctx)` и (для поиска) `keywords(lang)`;
собрать вёрстку из `groupCard`/`settingRow` (`settings/dom.js`), для `select`/`slider` оборачивать
именно в `settingRow` — оба компонента ставят себе `width:100%`, и колонка `.row-control` в
`settingRow` — это то, что ограничивает эту ширину, а не съедает подпись строки. Добавить страницу
в массив `PAGES` в `shell.js`, иконку в `settings/icons.js`, ключи `nav_<name>` в `i18n.js`.

### Поиск

Поле поиска фильтрует список сайдбара вживую (`filterSidebar`: подстрока без учёта регистра по
названию страницы и её `keywords(lang)`); несовпавшие строки получают `.dim`, а не исчезают.
Enter открывает первое совпадение. На открытой странице `shell.js` дополнительно подсвечивает
(`.match`, левая засечка Signal) карточки, чей заголовок (`.group-title`) содержит запрос —
простое сканирование DOM после рендера страницы, без отдельной схемы метаданных по группам.

### Док: пока не сохраняется

`pages/dock.js` рисуется всегда (дефолты, если `app.dock` отсутствует) и шлёт `PUT /config
{app:{dock:{...}}}` на каждое изменение — запрос отвечает `{ok:true}`, но ничего не сохраняется:
в этой ветке `AppSettings` ещё не имеет свойства `Dock` (появится в параллельной ветке), а
`ConfigApiService.PutConfig` десериализует патч `app` в `AppSettings` через `System.Text.Json` с
поведением по умолчанию — неизвестные поля молча игнорируются (см. `Json.cs`, там нет
`UnmappedMemberHandling.Disallow`). Страница показывает об этом подсказку, когда `app.dock`
отсутствует в `GET /config/full`. Тот же исход у нового select режима панели Windows
(`taskbar.windows.mode`) — у `TaskbarWindowsSettings` тоже пока нет поля `Mode`. Обе страницы
специально написаны заранее под будущие поля — когда они появятся на C#-стороне, правки JS не
нужны.

Правила окна (без изменений): без эмодзи, без восклицательных знаков, без длинных тире, метки
uppercase моно там, где были раньше, тёмная тема, размер по умолчанию 1100×720, минимум 900×600.
