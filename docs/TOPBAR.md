# Top bar

A thin always-on-top strip at the top edge of each monitor (mac-like menu bar), plus one popover
window per monitor for volume, the calendar, the NNA menu and Control Center. See
`docs/ARCHITECTURE.md` for the general window/WebView2 conventions this reuses (per-monitor DPI,
`SetVirtualHostNameToFolderMapping`-free static serving via `Api.MapStatic`, the `--headless`
constraint that no WPF window of any kind exists in that mode).

## Files

```
topbar/index.html, bar.css, bar.js, modules.js   the bar itself (one page per monitor)
topbar/mock-api.js                                dev/test-only fetch stub, active under ?mock=1
topbar/popup/index.html, popup.css, popup.js      the four popover pages (?module=...)
src/NNA.Wallpaper/TopBar/TopBarWindow.xaml(.cs)    the bar's WPF window (AppBar, WebView2)
src/NNA.Wallpaper/TopBar/TopBarManager.cs          creates bars per monitor, owns the popovers
src/NNA.Wallpaper/TopBar/PopupWindow.xaml(.cs)     one popover's WPF window
```

`ui/tokens.css` + `ui/components.css` + `ui/components.js` (`window.NNAUI`) — see
`docs/DESIGN-SYSTEM.md` — are used by the popover pages (`select`, `toggle`, `slider`, `menu`,
`dialog`). The bar strip itself (`topbar/index.html`) does not load `ui/components.js`: it predates
the component library and stays a minimal ES5 page; it does pull in `ui/tokens.css` transitively
through `wallpaper/nna-brand.css`'s `@import`, which is where its own CSS variables come from.

## Windows

**TopBarWindow** (unchanged shape from before this stage): `WindowStyle=None`, `AllowsTransparency
=False`, `Topmost=True`, `ShowActivated=False`, `WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE` — it never
takes keyboard focus, registered as a Windows AppBar (`SHAppBarMessage`) so maximized windows start
below it. New in this stage: it listens for `CoreWebView2.WebMessageReceived` and re-raises
`{type:'popup', module, anchorX, anchorW}` as the C# event `PopupRequested`.

**PopupWindow** (new): also `WindowStyle=None`, `AllowsTransparency=False`, `Topmost=True`,
`WS_EX_TOOLWINDOW` — but *without* `WS_EX_NOACTIVATE` and with `ShowActivated` left at its default
(`true`), because sliders/keyboard/Esc inside a popover need real focus. It closes itself on
`Deactivated` (click elsewhere) and on a `{type:'close'}` page message (Esc), guarded the same way
as `InputWindow.SafeClose`/`TopBarWindow`'s own `_closing` flag: set the flag first, never close
twice, swallow `InvalidOperationException` from a second `Close()`.

Content: same shared `CoreWebView2Environment` user-data root as the bar
(`Paths.WebView2UserDataDir`), with suffix `-topbar-popup` (the bar itself uses `-topbar`) — a
separate WebView2 profile so the two surfaces do not fight over one profile lock. Navigates to
`/topbar/popup/?module=<id>&monitor=<monitorId>&anchor=<anchorCenterXPhysical>`.

**TopBarManager.TogglePopup(bar, module, anchorXCss, anchorWCss)** — one `PopupWindow` per monitor,
tracked in `_popups: Dictionary<monitorId, PopupWindow>`:
- a different module already open on that monitor → close it, open the new one;
- the *same* module already open → close it and stop (this is the "click the same module again to
  close it" behaviour);
- otherwise → open it.

Physical-pixel math: `anchorXCss`/`anchorWCss` arrive from the page in CSS px relative to the bar's
own client area (i.e. DIU at that monitor's scale, since this app is per-monitor-DPI-aware and
WebView2 already renders at that scale — same assumption `TopBarWindow.Place()` makes). They are
converted once, in `TogglePopup`, to a physical screen X:
`monitor.Left + round((anchorX + anchorW/2) * monitor.Scale)`, and the popover's top Y is
`monitor.Top + bar.HeightPx` (also physical — `HeightPx` already is). `PopupWindow` does not
position or size itself until the page reports a size (see below): it starts off-screen
(`Left=Top=-32000`) so nothing flashes at the wrong place while WebView2 is still loading.

## Window ↔ page messages

| Direction | Message | Meaning |
|---|---|---|
| bar page → host | `{type:'popup', module, anchorX, anchorW}` | Open/toggle a popover under this bar element. `module` is the *popup* module id (`volume`, `calendar`, `nna`, `control`) — not always the same as the topbar module id that sent it (the `clock` topbar module opens the `calendar` popup). Sent by `TB.openPopup(moduleId, anchorEl)` in `topbar/bar.js`, wired into each clickable module's `ctx.openPopup(moduleId)`. |
| host → bar page | `{type:'config'}` | Reload the page (existing, unchanged). |
| host → bar page | `{type:'pause'}` | Stop/start module timers (existing, unchanged). |
| popup page → host | `{type:'size', width, height}` | Sent after first render and on every `ResizeObserver` change on `#ppRoot`. `width`/`height` are CSS px (== DIU here). `PopupWindow.Reposition` sizes/positions the window from this — the C# side never measures or guesses. |
| popup page → host | `{type:'close'}` | Sent on `Escape` (`document.addEventListener('keydown', ...)` in `topbar/popup/popup.js`, capture phase). Triggers `PopupWindow.SafeClose()`. |

Neither page posts anything else to the host; `{type:'volume-wheel', ...}` from the original brief
was dropped in favour of handling the mouse wheel/middle-click directly in `topbar/modules.js`'s
`volume` module (`wheel` → `PUT /audio/volume` step 2, middle click/`auxclick` → toggle mute) —
no host round-trip needed for that.

## Modules (`topbar/modules.js`)

New: `volume`, `network`, `battery`, `layout`, `control`, `nna` (replaces `brand`). `clock` gained a
click handler (opens the `calendar` popover); it was previously inert.

- **volume** — hand-drawn speaker glyph (own SVG paths, no icon set), 3 wave levels + a mute
  cross-out. Live via the WS `audio-changed` event (no polling). Wheel over the module = ±2 volume
  (`PUT /audio/volume`), middle-click = toggle mute. Click = open the `volume` popover.
- **network** — wifi (signal-scaled arcs) / ethernet / offline glyph. `ctx.tooltip(...)` shows the
  network name on hover (a small custom tooltip in `bar.css`/`TB.tooltip`, not `NNAUI.tooltip` —
  the bar strip does not load `ui/components.js`, see above). Polled every 30s, no popover.
- **battery** — capsule with a proportional fill rect; hidden via `ctx.setVisible(false)` when
  `present:false`. Polled every 30s, plus the WS `battery-changed` event.
- **layout** — `RU`/`EN`, 10px mono (`.tb-layout` in `bar.css`). Polled every 30s (there is no
  push event for keyboard layout).
- **control** — "two sliders" glyph. Click = open the `control` (Control Center) popover.
- **nna** — replaces `brand`: the NNA1618 cluster mark as an inline SVG path (copied verbatim from
  `H:\brand\nna1618_mark_v2\nna1618_mark_white.svg`'s single `<path d="...">`, recoloured via
  `fill: currentColor` instead of the baked-in `#FFFFFF`), `height: 13px` (`.tb-nna svg` in
  `bar.css`). Click = open the `nna` menu popover.

Live events are distributed from one shared `WS /events` connection opened once in `topbar/bar.js`
(`connectEvents()`), the same endpoint `wallpaper/layout.js` already uses. `TB.on(type, fn)` /
`ctx.on(type, fn)` subscribe; `config-changed` is still handled centrally (`location.reload()`),
everything else (`audio-changed`, `mic-changed`, `session-changed`, `network-changed`,
`battery-changed`) is handed to whichever module subscribed to it.

### Default module list / v1 → v2 config migration

`TopBarSettings.Modules` in `AppSettings.cs` (owned by another agent in this stage) still ships the
v1 default (`brand, date, clock, planner, media, weather, stats`) — that file was intentionally not
touched here. `topbar/bar.js`'s `normalizeModules()` upgrades whatever list `/config` returns
(including that v1 default, an already-saved user config, or a taskbar preset's `topBar.modules`)
before mounting:
1. `brand` → `nna` in place (same `side`), only if the config does not already have an `nna` entry.
2. Any of `control, volume, network, battery, layout` missing from the list is inserted, in that
   order, immediately before the first existing `right`-side module (typically `planner`) — giving
   the brief's requested default order: `control, volume, network, battery, layout, planner, media,
   weather, stats`.
3. An empty/missing module list still falls back to the full v2 default outright.

## Popover pages (`topbar/popup/`)

One `index.html` for all four; `popup.js` reads `?module=` and calls the matching `render*`
function into `#ppRoot` (`.pp-panel`). `?monitor=` and `?anchor=` are also on the URL (for
debugging/future use) but unused by the page itself — positioning is entirely host-side.

- **volume** — master `NNAUI.slider` + mute button (own SVG mute glyph, matches the bar icon);
  output device list (`PUT /audio/output {id}`); per-app mixer (`GET /audio/sessions`, each row a
  slider + mute + a 2px peak bar, `PUT /audio/session {id, volume?, muted?}`); microphone section
  (slider + mute + peak bar, `PUT /audio/mic`). Live via `audio-changed`/`mic-changed`;
  `session-changed` triggers a full `GET /audio/sessions` refetch (the event payload isn't assumed
  to carry the full session list).
- **calendar** — month grid, Monday-first, built from `GET /events` (`events[].date` → a Steel dot
  on that day) and `GET /planner/today` (`meetings[]`, dotted on *today* only — `/planner/today` is
  the only endpoint in the contract that returns meeting dates, so other days can't show meeting
  dots). Today is highlighted white (`box-shadow: inset 0 0 0 1px var(--white)`); the selected day
  drives the right-hand agenda list. Prev/next buttons change month; arrow keys move the selected
  day (and roll the month view when they cross a boundary).
- **nna** — `О программе` (`POST /app/settings?tab=about`), `Настройки` (`POST /app/settings`),
  pause/resume wallpaper (reads `GET /health`'s `monitors[].paused`, posts `/app/pause` or
  `/app/resume`), `Проверить обновления` (`POST /app/check-updates` → if `available`, the same row
  turns into `Обновить` → `POST /app/update`), separator, `Заблокировать экран` / `Сон` (straight to
  `POST /system/power {action}`), `Перезагрузка` / `Выключение` (styled `.is-danger`, gated behind
  `NNAUI.dialog` confirmation, then `POST /system/power {action, confirm:true}`).
- **control** — 2-column tile grid: volume slider (mirrors the bar's own audio state), brightness
  slider (`GET/PUT /system/brightness`, tile hidden when `supported:false`), "Не беспокоить"
  toggle (`GET/PUT /system/dnd`, hidden when unsupported), "Пауза обоев" toggle (`/health` +
  `/app/pause`/`/app/resume`), taskbar preset `NNAUI.select` (`GET /taskbar/presets` →
  `POST /taskbar/preset {id}`), and a `Настройки` button.

All four: `Esc` → `{type:'close'}`; size reported after render and on every `ResizeObserver` change
of `#ppRoot` (see the message table above). POST/PUT bodies go through `PP.post`/`PP.put`/
`PP.postJson`, all appending `?t=<token>` the same way `topbar/bar.js`'s `TB.post` does — the token
comes from the same `GET /config` every other page reads (`mock-api.js` does not fake `/config`: on
`?mock=1` it tries the real host first and only falls back to a stub if that fetch itself fails).

## Host API contract used here

Everything below is called with `PP.get`/`PP.put`/`PP.post`/`PP.postJson` exactly as specified for
this stage; none of it is implemented by this agent (out of scope — `HostServices.cs` is owned by a
parallel agent) and none of it exists yet in this worktree as of this stage:

```
GET/PUT  /audio/volume     {volume:0..100, muted, device:{id,name}}      PUT body {volume?, muted?}
GET      /audio/outputs    {devices:[{id,name,default}]}
PUT      /audio/output     {id}
GET      /audio/sessions   {sessions:[{id,pid,name,icon,volume,muted,peak,system}]}
PUT      /audio/session    {id, volume?, muted?}
GET/PUT  /audio/mic        {volume, muted, device:{id,name}, peak}       PUT body {volume?, muted?}
GET      /system/network   {up, kind:"wifi"|"ethernet"|"none", name, signal, ipv4}
GET      /system/battery   {present, percent, charging, remainingMin}
GET      /system/layout    {lang, hkl}
GET/PUT  /system/brightness {supported, monitors:[{id,name,value}]}      PUT body {id?, value}
GET/PUT  /system/dnd       {supported, on}                               PUT body {on}
POST     /system/power     {action:"lock"|"sleep"|"restart"|"shutdown", confirm?}   (JSON body, not query)
WS       /events           audio-changed, mic-changed, session-changed, network-changed,
                            battery-changed, config-changed (existing endpoint, new message types)
POST     /app/settings?tab=, /app/pause, /app/resume
POST     /app/check-updates → {ok, installed, current, available, message}
POST     /app/update
GET      /health, GET /events (calendar file), GET /planner/today
GET      /taskbar/presets  {builtin:[{id,file,name:{ru,en}}], user:[...]}
GET      /taskbar/status   {note}
POST     /taskbar/preset   {id}
```

`GET /events` and `GET /planner/today` already exist (`EventsService`, `Planner/PlannerService`);
everything else in that list — including the "existing" `/app/*` and `/taskbar/*` routes named in
the stage brief — is not registered in `HostServices.cs` as of this worktree snapshot (confirmed by
grep; `settings/taskbar-tab.js` even comments "every /taskbar/* call is expected to 404 until the
C# side" is done). Until it lands, `topbar/mock-api.js` (enabled by `?mock=1` on either page's URL)
answers every path above from an in-memory fixture so the popover pages are independently
developable and testable; it never touches `WS /events` (already real) or `/config` (tries the real
host first, only stubs on fetch failure) and does nothing at all when `?mock=1` is absent — the
production pages hit the real routes exclusively.

## Testing

`tests/topbar-probe.ps1 [-Port 1625] [-Data <tmp>] [-Exe <path>] [-ShotsDir <dir>]`:
1. `node --check` on `topbar/bar.js`, `topbar/modules.js`, `topbar/mock-api.js`,
   `topbar/popup/popup.js`.
2. Starts `NNA.Wallpaper.exe --headless --port <p> --data <tmp>` (never the owner's :1618
   instance), waits for `/health`.
3. `/topbar/` and `/topbar/popup/?module=volume&mock=1` respond 200 (plain static-file checks —
   `Api.MapStatic("/topbar/", ...)` already serves the new `popup/` subfolder, no host route
   change needed for this stage).
4. Headless Edge (`msedge --headless=new --window-size=420,600 --screenshot=...`) renders
   `/topbar/popup/?module=<volume|calendar|nna|control>&mock=1` into
   `H:\night-runs\nna-wallpaper-2\shots\stage8-popup-<module>.png`, each checked with
   `tests/png-mean.py ... 3` (not blank). The captured PNG is the full 420×600 window, not the
   popover's own intrinsic size — a real `PopupWindow` sizes itself exactly to the page's reported
   content (see the message table), so it never shows the extra blank space visible in these
   screenshots; that space is purely an artifact of giving Edge a fixed capture window.
5. Stops the instance by PID (never by process name — that would risk the owner's :1618 process).

### Что не проверено (what this script cannot cover)

A real `TopBar/PopupWindow` needs an interactive desktop session and a real WebView2 host process;
`--headless` (`HeadlessHostApp`) creates neither a `TopBarWindow` nor a `PopupWindow` — there is
nothing to click, position, or screenshot through that mode. What *is* verified instead:
`dotnet build NNA.Wallpaper.sln -c Release` (0 warnings/errors) proves `TopBarWindow.PopupRequested`,
`TopBarManager.TogglePopup`/`_popups` tracking, and `PopupWindow` (`Reposition`, `SafeClose`,
`WebMessageReceived` handling) all compile and wire together correctly; the popover *pages*
(`topbar/popup/*`) are verified to actually render, under the `?mock=1` contract, via the headless
Edge screenshots above. Live window behaviour that was not exercised by any of this: `Deactivated`
actually closing the window when a real user clicks elsewhere, the AppBar-anchored bar actually
receiving the `{type:'popup'}` postMessage and `TogglePopup` actually creating/positioning/showing a
`PopupWindow` HWND next to it, and the popover actually taking keyboard focus (sliders/Esc) on a
real desktop.
