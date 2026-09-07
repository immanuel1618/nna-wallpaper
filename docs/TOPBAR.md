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
src/NNA.Wallpaper/TopBar/CompositionInput.cs       stage 8C: composition-hosting + mouse forwarding
                                                    shared by TopBarWindow and Dock/DockWindow
tests/TopBarPreview/                               stage 8C: live click-through probe (see Testing)
```

`ui/tokens.css` + `ui/components.css` + `ui/components.js` (`window.NNAUI`): see
`docs/DESIGN-SYSTEM.md`: are used by the popover pages (`select`, `toggle`, `slider`, `menu`,
`dialog`). The bar strip itself (`topbar/index.html`) does not load `ui/components.js`: it predates
the component library and stays a minimal ES5 page; it does pull in `ui/tokens.css` transitively
through `wallpaper/nna-brand.css`'s `@import`, which is where its own CSS variables come from.

## Windows

**TopBarWindow**: `WindowStyle=None`, `AllowsTransparency=False`, `Topmost=True`,
`ShowActivated=False`, `WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE`: it never takes keyboard focus,
registered as a Windows AppBar (`SHAppBarMessage`) so maximized windows start below it. It listens
for `CoreWebView2.WebMessageReceived` and re-raises `{type:'popup', module, anchorX, anchorW}` as
the C# event `PopupRequested`. Since stage 8C it is hosted through `CompositionInput`/
`CompositionHost` instead of the WPF `Microsoft.Web.WebView2.Wpf.WebView2` control: see "Хостинг и
фокус" below.

**PopupWindow** (new): also `WindowStyle=None`, `AllowsTransparency=False`, `Topmost=True`,
`WS_EX_TOOLWINDOW`: but *without* `WS_EX_NOACTIVATE` and with `ShowActivated` left at its default
(`true`), because sliders/keyboard/Esc inside a popover need real focus. It closes itself on
`Deactivated` (click elsewhere) and on a `{type:'close'}` page message (Esc), guarded the same way
as `InputWindow.SafeClose`/`TopBarWindow`'s own `_closing` flag: set the flag first, never close
twice, swallow `InvalidOperationException` from a second `Close()`.

Content: same shared `CoreWebView2Environment` user-data root as the bar
(`Paths.WebView2UserDataDir`), with suffix `-topbar-popup` (the bar itself uses `-topbar`): a
separate WebView2 profile so the two surfaces do not fight over one profile lock. Navigates to
`/topbar/popup/?module=<id>&monitor=<monitorId>&anchor=<anchorCenterXPhysical>`.

**TopBarManager.TogglePopup(bar, module, anchorXCss, anchorWCss)**: one `PopupWindow` per monitor,
tracked in `_popups: Dictionary<monitorId, PopupWindow>`:
- a different module already open on that monitor → close it, open the new one;
- the *same* module already open → close it and stop (this is the "click the same module again to
  close it" behaviour);
- otherwise → open it.

Physical-pixel math: `anchorXCss`/`anchorWCss` arrive from the page in CSS px relative to the bar's
own client area (i.e. DIU at that monitor's scale, since this app is per-monitor-DPI-aware and
WebView2 already renders at that scale: same assumption `TopBarWindow.Place()` makes). They are
converted once, in `TogglePopup`, to a physical screen X:
`monitor.Left + round((anchorX + anchorW/2) * monitor.Scale)`, and the popover's top Y is
`monitor.Top + bar.HeightPx` (also physical: `HeightPx` already is). `PopupWindow` does not
position or size itself until the page reports a size (see below): it starts off-screen
(`Left=Top=-32000`) so nothing flashes at the wrong place while WebView2 is still loading.

## Хостинг и фокус (stage 8C)

Two field bugs from the owner's live desk (0.3.1, `monitors:"primary"`, `height:25`) drove this
stage: (1) clicking the clock did not open the calendar popover, no trace in `app.log`; (2) clicking
anywhere on the bar made `NNA Wallpaper Top Bar` the foreground window (`GetForegroundWindow()`),
stealing focus from whatever the owner had active, even though the window carries
`WS_EX_NOACTIVATE`.

**Popover not opening: root cause.** The C#↔JS wiring itself (`WebMessageReceived` subscribed
before `Navigate`, `WebMessageAsJson` not `AsString`, `ctx.openPopup(moduleId)` → `TB.openPopup`
passing the anchor element, `TogglePopup`'s try/catch already logging on failure) was already
correct: every one of the brief's hypotheses (а)–(д) checks out clean on inspection, and adding the
logging below and driving the exact same code end-to-end through `tests/TopBarPreview` opens the
popover every time (see Testing). The actual cause is a **click-target/config-layout collision**,
confirmed by reading the owner's live `/config` (read-only `GET`, port 1618): their saved
`topbar.modules` has `date` *and* `clock` sharing the same `"center"` side:
`[…, {id:"date",side:"center"}, {id:"clock",side:"center"}, …]`. `.tb-center{flex:0 0 auto}` is
horizontally centred as a *zone*, but with two modules inside it the zone's own midpoint falls
wherever their combined width places it: `date` (`"ПН · 07 СЕН"`, ~11 characters) is wider than
`clock` (`"23:17"`, 5 characters), so the true centre pixel of a 3440px-wide monitor (the owner's
click target, screen x=1720: exactly `3440/2`) lands inside `date`'s own box, not `clock`'s. `date`
posts `/app/settings?tab=layout` (fire-and-forget, no popup, nothing logged): which is exactly the
observed symptom: no popup, and (before this stage's logging existed) nothing in `app.log` either.
This is config data, not code, and out of this stage's scope to change (`settings/*` is off-limits
and the owner's live `app.json` was never written to: see Testing); `tests/TopBarPreview`'s own bar
config keeps `clock` alone on `"center"` so its click target is unambiguous. Diagnostic logging was
still added throughout the chain since the brief asked for it and it is useful regardless:
`TopBarWindow.OnWebMessage` logs every message received (`"top bar: web message " + json`) and any
JSON-parse failure; `TopBarManager.TogglePopup` logs `"popup: toggle <module> on <monitor>"`;
`PopupWindow` logs `"popup: init <module> on <monitor> anchor=<x>"` and, on every `{type:'size'}`
message, `"popup: shown at <x>,<y> <w>x<h> (<module>)"`.

**Focus steal: root cause and fix.** The WPF `Microsoft.Web.WebView2.Wpf.WebView2` control hosts
Chromium in *windowed* mode, which creates its own `Chrome_WidgetWin_1` child HWND to receive mouse
input. `WS_EX_NOACTIVATE` on the owner window only vetoes the *default* click-to-activate path
(`WM_MOUSEACTIVATE` → `MA_NOACTIVATE`); Chromium's own input handling calls `SetFocus` on its child
HWND on pointer-down regardless (for IME/accessibility), and giving a child window keyboard focus
forces its top-level owner active no matter what ex-style the owner carries: that is the bug.
`TopBarWindow` and `Dock/DockWindow` (hosting only: `DockManager.cs` untouched) no longer use the
WPF WebView2 control at all: `TopBar/CompositionInput.cs` hosts WebView2 through
`Engine/CompositionHost.cs` (the same DirectComposition device/target/visual plumbing the wallpaper
windows already use, reused as-is) straight onto the window's own top-level HWND. Composition
hosting has no Chromium-owned HWND, so there is nothing left that can call `SetFocus`/steal
activation: `CompositionInput.Handle(msg, wParam, lParam, out result)` is called from each window's
own `WndProc` and forwards `WM_MOUSEMOVE`/`WM_LBUTTONDOWN`/`WM_LBUTTONUP`/`WM_MBUTTONDOWN`/
`WM_MBUTTONUP`/`WM_MOUSEWHEEL` to `CoreWebView2CompositionController.SendMouseInput` (client
coordinates throughout; `WM_MOUSEWHEEL`'s screen coordinates are converted via `ScreenToClient`
first: unlike every other `WM_MOUSE*` message, wheel lParam is screen-relative), arms
`TrackMouseEvent(TME_LEAVE)` on first move so a real `WM_MOUSELEAVE` produces exactly one
`CoreWebView2MouseEventKind.Leave`, answers `WM_SETCURSOR` with `SetCursor` from the controller's own
`CursorChanged`/`Cursor` (so clickable modules still show a hand cursor), and answers
`WM_MOUSEACTIVATE` with `MA_NOACTIVATE` explicitly: belt-and-braces alongside the window's own
`WS_EX_NOACTIVATE`, though composition hosting alone already removes the actual mechanism that broke
it. Right-click is intentionally not forwarded (same convention as `Engine/InputBridge.cs` for the
wallpaper windows); neither the bar nor the dock take keyboard input, so none is forwarded either.
`PopupWindow` is unchanged (still windowed WPF `WebView2`, still `WS_EX_TOOLWINDOW` without
`WS_EX_NOACTIVATE`): it is a normal focusable window on purpose (sliders, Esc need real focus), and
closes itself on `Deactivated`/`{type:'close'}` same as before; it was only renamed
(`Title="NNA Wallpaper Popup"`, was `"NNA Wallpaper Top Bar Popup"`) so `tests/TopBarPreview` can
`FindWindow` it.

Composition hosting also gets working WebView2 transparency for free (`DefaultBackgroundColor =
Color.Transparent`, alpha 0: composition hosting supports a truly transparent background; windowed
hosting does not): for `DockWindow` this does not change the known blur-covers-the-whole-rectangle
seam (`docs/DOCK.md`), only the focus-stealing behaviour; the window is still sized exactly to
content as before.

## Window ↔ page messages

| Direction | Message | Meaning |
|---|---|---|
| bar page → host | `{type:'popup', module, anchorX, anchorW}` | Open/toggle a popover under this bar element. `module` is the *popup* module id (`volume`, `calendar`, `nna`, `control`): not always the same as the topbar module id that sent it (the `clock` topbar module opens the `calendar` popup). Sent by `TB.openPopup(moduleId, anchorEl)` in `topbar/bar.js`, wired into each clickable module's `ctx.openPopup(moduleId)`. |
| host → bar page | `{type:'config'}` | Reload the page (existing, unchanged). |
| host → bar page | `{type:'pause'}` | Stop/start module timers (existing, unchanged). |
| popup page → host | `{type:'size', width, height}` | Sent after first render and on every `ResizeObserver` change on `#ppRoot`. `width`/`height` are CSS px (== DIU here). `PopupWindow.Reposition` sizes/positions the window from this: the C# side never measures or guesses. |
| popup page → host | `{type:'close'}` | Sent on `Escape` (`document.addEventListener('keydown', ...)` in `topbar/popup/popup.js`, capture phase). Triggers `PopupWindow.SafeClose()`. |

Neither page posts anything else to the host; `{type:'volume-wheel', ...}` from the original brief
was dropped in favour of handling the mouse wheel/middle-click directly in `topbar/modules.js`'s
`volume` module (`wheel` → `PUT /audio/volume` step 2, middle click/`auxclick` → toggle mute):
no host round-trip needed for that.

## Modules (`topbar/modules.js`)

New: `volume`, `network`, `battery`, `layout`, `control`, `nna` (replaces `brand`). `clock` gained a
click handler (opens the `calendar` popover); it was previously inert.

- **volume**: hand-drawn speaker glyph (own SVG paths, no icon set), 3 wave levels + a mute
  cross-out. Live via the WS `audio-changed` event (no polling). Wheel over the module = ±2 volume
  (`PUT /audio/volume`), middle-click = toggle mute. Click = open the `volume` popover.
- **network**: wifi (signal-scaled arcs) / ethernet / offline glyph. `ctx.tooltip(...)` shows the
  network name on hover (a small custom tooltip in `bar.css`/`TB.tooltip`, not `NNAUI.tooltip`:
  the bar strip does not load `ui/components.js`, see above). Polled every 30s, no popover.
- **battery**: capsule with a proportional fill rect; hidden via `ctx.setVisible(false)` when
  `present:false`. Polled every 30s, plus the WS `battery-changed` event.
- **layout**: `RU`/`EN`, 10px mono (`.tb-layout` in `bar.css`). Polled every 30s (there is no
  push event for keyboard layout).
- **control**: "two sliders" glyph. Click = open the `control` (Control Center) popover.
- **nna**: replaces `brand`: the NNA1618 cluster mark as an inline SVG path (copied verbatim from
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
v1 default (`brand, date, clock, planner, media, weather, stats`): that file was intentionally not
touched here. `topbar/bar.js`'s `normalizeModules()` upgrades whatever list `/config` returns
(including that v1 default, an already-saved user config, or a taskbar preset's `topBar.modules`)
before mounting:
1. `brand` → `nna` in place (same `side`), only if the config does not already have an `nna` entry.
2. Any of `control, volume, network, battery, layout` missing from the list is inserted, in that
   order, immediately before the first existing `right`-side module (typically `planner`): giving
   the brief's requested default order: `control, volume, network, battery, layout, planner, media,
   weather, stats`.
3. An empty/missing module list still falls back to the full v2 default outright.

## Popover pages (`topbar/popup/`)

One `index.html` for all four; `popup.js` reads `?module=` and calls the matching `render*`
function into `#ppRoot` (`.pp-panel`). `?monitor=` and `?anchor=` are also on the URL (for
debugging/future use) but unused by the page itself: positioning is entirely host-side.

- **volume**: master `NNAUI.slider` + mute button (own SVG mute glyph, matches the bar icon);
  output device list (`PUT /audio/output {id}`); per-app mixer (`GET /audio/sessions`, each row a
  slider + mute + a 2px peak bar, `PUT /audio/session {id, volume?, muted?}`); microphone section
  (slider + mute + peak bar, `PUT /audio/mic`). Live via `audio-changed`/`mic-changed`;
  `session-changed` triggers a full `GET /audio/sessions` refetch (the event payload isn't assumed
  to carry the full session list).
- **calendar**: month grid, Monday-first, built from `GET /events` (`events[].date` → a Steel dot
  on that day) and `GET /planner/today` (`meetings[]`, dotted on *today* only: `/planner/today` is
  the only endpoint in the contract that returns meeting dates, so other days can't show meeting
  dots). Today is highlighted white (`box-shadow: inset 0 0 0 1px var(--white)`); the selected day
  drives the right-hand agenda list. Prev/next buttons change month; arrow keys move the selected
  day (and roll the month view when they cross a boundary).
- **nna**: `О программе` (`POST /app/settings?tab=about`), `Настройки` (`POST /app/settings`),
  pause/resume wallpaper (reads `GET /health`'s `monitors[].paused`, posts `/app/pause` or
  `/app/resume`), `Проверить обновления` (`POST /app/check-updates` → if `available`, the same row
  turns into `Обновить` → `POST /app/update`), separator, `Заблокировать экран` / `Сон` (straight to
  `POST /system/power {action}`), `Перезагрузка` / `Выключение` (styled `.is-danger`, gated behind
  `NNAUI.dialog` confirmation, then `POST /system/power {action, confirm:true}`).
- **control**: 2-column tile grid: volume slider (mirrors the bar's own audio state), brightness
  slider (`GET/PUT /system/brightness`, tile hidden when `supported:false`), "Не беспокоить"
  toggle (`GET/PUT /system/dnd`, hidden when unsupported), "Пауза обоев" toggle (`/health` +
  `/app/pause`/`/app/resume`), taskbar preset `NNAUI.select` (`GET /taskbar/presets` →
  `POST /taskbar/preset {id}`), and a `Настройки` button.

All four: `Esc` → `{type:'close'}`; size reported after render and on every `ResizeObserver` change
of `#ppRoot` (see the message table above). POST/PUT bodies go through `PP.post`/`PP.put`/
`PP.postJson`, all appending `?t=<token>` the same way `topbar/bar.js`'s `TB.post` does: the token
comes from the same `GET /config` every other page reads (`mock-api.js` does not fake `/config`: on
`?mock=1` it tries the real host first and only falls back to a stub if that fetch itself fails).

## Host API contract used here

Everything below is called with `PP.get`/`PP.put`/`PP.post`/`PP.postJson` exactly as specified for
this stage; none of it is implemented by this agent (out of scope: `HostServices.cs` is owned by a
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
everything else in that list: including the "existing" `/app/*` and `/taskbar/*` routes named in
the stage brief: is not registered in `HostServices.cs` as of this worktree snapshot (confirmed by
grep; `settings/taskbar-tab.js` even comments "every /taskbar/* call is expected to 404 until the
C# side" is done). Until it lands, `topbar/mock-api.js` (enabled by `?mock=1` on either page's URL)
answers every path above from an in-memory fixture so the popover pages are independently
developable and testable; it never touches `WS /events` (already real) or `/config` (tries the real
host first, only stubs on fetch failure) and does nothing at all when `?mock=1` is absent: the
production pages hit the real routes exclusively.

## Testing

`tests/topbar-probe.ps1 [-Port 1625] [-Data <tmp>] [-Exe <path>] [-ShotsDir <dir>]`:
1. `node --check` on `topbar/bar.js`, `topbar/modules.js`, `topbar/mock-api.js`,
   `topbar/popup/popup.js`.
2. Starts `NNA.Wallpaper.exe --headless --port <p> --data <tmp>` (never the owner's :1618
   instance), waits for `/health`.
3. `/topbar/` and `/topbar/popup/?module=volume&mock=1` respond 200 (plain static-file checks:
   `Api.MapStatic("/topbar/", ...)` already serves the new `popup/` subfolder, no host route
   change needed for this stage).
4. Headless Edge (`msedge --headless=new --window-size=420,600 --screenshot=...`) renders
   `/topbar/popup/?module=<volume|calendar|nna|control>&mock=1` into
   `H:\night-runs\nna-wallpaper-2\shots\stage8-popup-<module>.png`, each checked with
   `tests/png-mean.py ... 3` (not blank). The captured PNG is the full 420×600 window, not the
   popover's own intrinsic size: a real `PopupWindow` sizes itself exactly to the page's reported
   content (see the message table), so it never shows the extra blank space visible in these
   screenshots; that space is purely an artifact of giving Edge a fixed capture window.
5. Stops the instance by PID (never by process name: that would risk the owner's :1618 process).

### `tests/TopBarPreview` + `tests/topbar-live-probe.ps1` (stage 8C)

A real `TopBar/PopupWindow` needs an interactive desktop session and real WebView2 windows:
`--headless` cannot exercise any of this, which is exactly the gap stage 8B's script above left open
("Что не проверено"). `tests/TopBarPreview` (own `.csproj`, deliberately not in the `.sln`, same
shape as `tests/WindowPreview`) closes it: it builds a real, composition-hosted `TopBarWindow` +
`PopupWindow` pair and drives it with genuine `SendInput`.

- Pages are served **read-only** from the owner's already-running instance on :1618
  (`Paths.Resolve(null)`: the owner's real data dir, `HostContext` pointed at port 1618 as a
  client, no server of its own): only `GET`s (`/topbar/`, `/topbar/popup/`, `/config`), nothing is
  ever posted or saved, and the owner's process (whatever PID it currently has: it auto-updated to
  0.3.2 mid-stage; this stage's build never touched or restarted it) is left running throughout.
- Shows on the owner's real vertical monitor (`\\.\DISPLAY1`, 1440×2560, screen x −1440..0, y
  −603..1957: see `/health`): never where the owner's own bar renders (`monitors:"primary"`, i.e.
  the 3440×1440 monitor only): with `TopBarSettings.ReserveSpace=false` so it never registers as an
  AppBar there (the owner had real windows: VS Code: open on that monitor; AppBar registration
  would have resized them).
- Clicks two modules that open a popover: `clock` (bar centre) and `nna` (leftmost, first in the
  *left* zone): using each element's **live** `getBoundingClientRect()` (`TopBarWindow
  .ExecuteScriptAsync`, a small debug-only hook added for this), fetched fresh immediately before
  each click, not guessed from CSS/flex math or the bar's geometric centre (see "Хостинг и фокус"
  above for why that guess is unreliable: it is literally the field bug). `nna`, not `volume`: at
  1440px the owner's real right zone (`control, volume, network, battery, layout, media, weather,
  stats`: the owner's saved config plus what `normalizeModules()` injects) does not fit, and
  `.tb-right{overflow:hidden}` clips its earliest items (`volume` included) off-screen: confirmed
  live: `getBoundingClientRect()` still reports a plausible rect for a clipped element, but
  `elementFromPoint()` at that same point resolves to the zone's own empty trailing space, not the
  module. A pre-existing bar.js/CSS capacity issue on narrow monitors, unrelated to this stage's
  fix: `nna` sits in the *left* zone (`justify-content:flex-start`, packed at a small fixed offset
  from the bar's own edge) and is never affected by it.
- After each click: `FindWindow(null, "NNA Wallpaper Popup")` (renamed for exactly this: see
  "Хостинг и фокус") and `GetForegroundWindow()`'s title, printed as
  `RESULT <name>: popup=<bool> bar-stole-focus=<bool>` (`bar-stole-focus` = the foreground title was
  literally `"NNA Wallpaper Top Bar"`: the pre-fix symptom). One screenshot of the area under the
  bar (`H:\night-runs\nna-wallpaper-2\shots\stage8c-popup-live.png`) with the first (`calendar`)
  popover open.
- `tests/topbar-live-probe.ps1 [-Exe <path>] [-ShotsDir <dir>]` runs it and turns the `RESULT` lines
  (plus the screenshot's existence) into PASS/FAIL: via `Start-Process
  -RedirectStandardOutput/-RedirectStandardError` to files, not `&`/`2>&1`:
  `TopBarPreview.exe` is a `WinExe` (GUI subsystem) and Windows does not attach a console to it, so a
  plain `& $Exe` capture is silently empty even though the process runs fine (confirmed: `Start-
  Process`'s own `.ExitCode` was also unreliable for this: empty even after `HasExited=True` and
  `Refresh()`: so the script does not gate on it, only on the parsed `RESULT`/`PASS` text).

What is still not covered live: `Deactivated` actually closing a popover on a real user's click
elsewhere (the helper's own process could not reliably take/lose Win32 foreground ownership at all:
`GetForegroundWindow()` stayed on a third-party window throughout every run here, a `Start-Process`/
foreground-lock artifact of a freshly-started, non-interactively-used process, not a code issue: the
bar itself never became foreground even so, which is the thing actually being tested: so `Escape`
closing the popover was exercised but not asserted on), and dock hosting was not click-tested live at
all (no `DockPreview` helper: only `dotnet build`/`dotnet test` prove `DockWindow`'s composition
wiring compiles; it shares 100% of the mouse-forwarding code (`CompositionInput`) that the bar's live
run does exercise, but its own AppBar/content-sizing paths were not).
