# Dock (stage 9)

A mac-like dock at the bottom edge of a monitor, meant to replace the Windows taskbar visually
(the owner will hide the system taskbar itself in a later stage — this stage only builds the
dock window and its API, `AppSettings.Dock.Enabled` defaults to `false`). Modelled directly on
`TopBar/TopBarManager.cs` + `TopBar/TopBarWindow.xaml(.cs)`: one always-on-top WebView2 window per
monitor (or the primary only), created by `Dock/DockManager.cs`, hosting `dock/index.html`.

## Settings (`AppSettings.Dock`, `src/NNA.Wallpaper.Host/Config/AppSettings.cs`)

```json
"dock": {
  "enabled": false,
  "monitors": "all",
  "size": 55,
  "magnify": true,
  "magnifyMax": 1.6,
  "autoHide": false,
  "reserveSpace": false,
  "folders": ["%USERPROFILE%\\Downloads", "%USERPROFILE%\\Desktop"],
  "showTrash": true,
  "showRunning": true,
  "pinned": [],
  "style": { "mode": "acrylic", "color": "#0B0B0B", "opacity": 0.6 }
}
```

`size` follows the Fibonacci icon row used elsewhere (34/55/89). `pinned` holds launch.json item
ids in display order; **empty means "derive automatically"**, resolved by `DockService` on every
`GET /dock/items`:

1. launch.json items with `"dock": true` (a new, dock-only flag; `LaunchService` does not read it
   and is unaffected), in launch.json's own order;
2. else the first 8 items of a launch.json group whose id is `"work"` (case-insensitive);
3. else the first 8 items overall.

There is no settings-window UI for the dock yet — edit `config/app.json` directly, or apply the
`mac` taskbar preset (`presets/taskbar/mac.json` now carries a `"dock": {"enabled": true, ...}`
block). **Known gap**: `settings/taskbar-tab.js` (off-limits for this stage) only reads
`data.taskbar` and `data.topBar` from a preset — it silently ignores `data.dock`, so applying the
"Mac-like" preset from the settings window does not yet turn the dock on by itself; the JSON key is
in place for whichever stage wires up that tab.

## Routes (`src/NNA.Wallpaper.Host/Services/DockService.cs`)

Registered by one line in `HostServices.RegisterServices` (`Add(new Services.DockService(_ctx))`);
`DockService.Register` maps its own static root (`/dock/` → `dock/index.html`, `dock.js`,
`dock.css`) so no other file needed a second change.

| Route | Notes |
|---|---|
| `GET /dock/items` | `{items:[...], settings:{...}}` — merged pinned + running apps, one separator, configured folders, trash. 500ms cache. `settings` exists here (not in `GET /config`, which this stage does not touch) so `dock.js` has size/magnify/style without a `HostServices` change. |
| `POST /dock/pin` `{launchId}` | Adds to `Dock.Pinned`, saves `app.json`. |
| `POST /dock/unpin` `{launchId}` | Removes from `Dock.Pinned`. |
| `POST /dock/reorder` `{ids:[]}` | Replaces `Dock.Pinned` wholesale. |
| `GET /dock/trash` | `{ok, empty, items, bytes}` via `SHQueryRecycleBinW`. |
| `POST /dock/trash/open` | `explorer.exe shell:RecycleBinFolder`. |
| `POST /dock/trash/empty` `{confirm:true}` | `SHEmptyRecycleBinW` (no confirm → `400`); the page's own `NNAUI.dialog` is the actual confirmation UI. |
| `GET /dock/folder?path=` | `{entries:[{name,path,isDir,icon}]}`, newest 21 first. **Only ever lists one of the configured `Dock.Folders`** — this is a `GET` route and `LocalApi` does not require a token on `GET`, so it must not become "list any directory". |
| `POST /dock/folder/open` `{path}` | Opens one configured folder in Explorer. A dedicated route rather than reusing `POST /open` (`OpenService`), because that one is gated by `AppSettings.OpenRoots`, which is empty on a clean install — a folder the owner explicitly put in `Dock.Folders` should not additionally need `OpenRoots`. Individual **files** inside the fan still open through `POST /open`, so they do inherit the `OpenRoots` gate; on a clean install that call will 403 until the owner adds Downloads/Desktop to `OpenRoots`. |
| `GET /dock/file-icon?path=` | PNG via `IconExtractor.TryExtract`, cached under `data/icons/file-<sha1>.png`. Same "configured folders only" restriction as `/dock/folder`. |

Two small additive changes elsewhere support these routes without touching restricted files:

- `WindowsService.Snapshot()` and `WindowsService.FindAllForItem(aumid, cmd)` (returns every
  matching window, not just the first — the dock's window list/right-click menu needs all of
  them, `LaunchService`'s raise-or-launch flow only ever needed one).
- `EventsService.Current` + `BroadcastAsync` made `public` (was `private`) — `DockService` polls
  `WindowsService.Snapshot()` every second and, only when the open-window set actually changed,
  pushes `{"type":"dock-changed"}` over the existing `/events` WebSocket. `dock.js` also reacts to
  `{"type":"config-changed","what":"app"}` (already broadcast by `ConfigStore.SaveApp`) so a pin/
  unpin or a settings edit refreshes the page without a full window recreation.

`SHQueryRecycleBin`/`SHEmptyRecycleBin` are declared as plain `[DllImport]`s inside `DockService`
rather than through `NativeMethods.txt`/CsWin32: CsWin32 refuses to generate them for this
AnyCPU-by-default host project ("PInvoke005: only available when targeting a specific CPU
architecture"), and widening the project's `PlatformTarget` for two routes seemed like more blast
radius than one manual P/Invoke pair (the same pattern `TaskbarStyler` already uses for
`SetWindowCompositionAttribute`).

## Window (`src/NNA.Wallpaper/Dock/DockWindow.xaml(.cs)`, `DockManager.cs`)

One `DockWindow` per monitor, `WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE` (a click on the dock never
steals focus from the foreground app — WebView2's child HWND still receives the click regardless
of `NOACTIVATE` on the top-level owner), topmost, optional `ABE_BOTTOM` AppBar registration when
`ReserveSpace` is on (off by default — a floating dock is not meant to reserve screen space the
way the taskbar/top bar do). Hidden under fullscreen apps and, when `AutoHide` is on, unless the
cursor is near the bottom edge — same `FullscreenWatcher` + `DispatcherTimer` tick as
`TopBarManager`.

**Sizing**: the window is exactly content-sized, not full monitor width. `dock.js` measures its
own wrapper (`#dkWrap`, which already includes a 13px pad on every side) after every render and
posts `{"type":"size","width":...,"height":...}` over `window.chrome.webview.postMessage`;
`DockWindow.OnWebMessage` reads it and calls `ApplyContentSize`, which resizes+repositions via
`SetWindowPos` in physical px (`Monitor` is a physical-pixel rect; CSS px from the page equal WPF
DIUs at the window's own per-monitor DPI, so only the physical-px `SetWindowPos` call needs the
`Monitor.Scale` conversion — the WPF `Width`/`Height` do not). Height is *not* re-measured on
every hover-magnify frame — see Transparency/limits below for why.

**Transparency**: WebView2's own background transparency only composes correctly with
DirectComposition hosting, not the WPF child-HWND `Microsoft.Web.WebView2.Wpf.WebView2` control
used here (same control `TopBarWindow` uses), so true window transparency was not attempted. The
window keeps an **opaque** `Background` (the configured `Style.Color`, default Base `#0B0B0B`)
and is sized to exactly the page's content box; the page renders the rounded, translucent bar
(`rgba(22,22,22,.82)` + `backdrop-filter: blur`) inside a transparent 13px pad, so the plain
rectangle around the pill blends into the Base-coloured wallpaper behind it rather than showing as
a visible box. This is the approach the stage brief calls "honest": it is an approximation, not
real see-through transparency, and it has one visible seam — when `Style.Mode` is `acrylic`/
`blur`/`clear`, `TaskbarStyler.ApplyAccent` composites the whole window rectangle (same call
`TopBarWindow` uses), including the 13px pad around the rounded bar, so that pad also gets the
blur material instead of staying a plain Base rectangle. It was not "fixed" further because doing
so needs DirectComposition hosting, which is a materially bigger change than this stage's scope.

## Page (`dock/index.html`, `dock/dock.js`, `dock/dock.css`)

ES5, no build step, same conventions as `topbar/bar.js`/`bar.css`. Loads `/ui/tokens.css` +
`/ui/components.css` + `/ui/logic.js` + `/ui/components.js` (for `NNAUI.menu`/`.popover`/
`.dialog`) plus its own `dock.css`, which is now also scanned by `ui/tests/palette.test.mjs`
(added to that test's file list) and uses no hex literals at all, only the shared CSS variables
and `rgba()` of the same palette values, matching `topbar/bar.css`'s own approach.

- **Magnify**: on `mousemove` over the bar, each icon's `.dk-icon-img` gets
  `transform: scale(s)` (`transform-origin: bottom center`) where `s` follows a cosine falloff
  over a 3-icon radius, capped at `magnifyMax` — the usual macOS dock formula. Only `transform`
  transitions (120ms) are used, never `transition: all`. **Limitation**: unlike real macOS, the
  bar's own width is *not* re-flowed while icons grow — the window is sized once at rest (icon
  count × base size) and its **height** carries a fixed headroom (`size * magnifyMax + 21px` for
  the label) so vertical growth never needs a live window resize; horizontal growth can overlap a
  neighbour slightly instead of pushing it aside. Resizing the native window on every
  `mousemove` frame would be expensive and visibly jittery, so this trade-off was deliberate.
- **Label above icon**: `.dk-icon-tip`, shown by a plain CSS `:hover` rule (not
  `NNAUI.tooltip`, which positions via `document.body` and a 400ms delay — the dock wants it
  immediate and anchored exactly above the icon).
- **Jump on launch**: `.dk-icon.is-jumping` (a 3-bounce `translateY` keyframe animation, 1.1s) is
  added only when a click actually starts a *new* process (`item.running` was false), not when it
  merely raises an already-open window — matching real dock behaviour.
- **Running dot**: `.dk-icon-dot`, Steel when any window is open, White when one of them is the
  foreground window (`item.foreground`).
- **Folders as a fan**: `NNAUI.popover` anchored on the folder icon, filled from
  `GET /dock/folder?path=`, up to 21 entries; each click opens the file (`POST /open`, subject to
  `OpenRoots`, see above) or drills into a subfolder (`POST /dock/folder/open`).
- **Trash**: two hand-drawn inline SVGs (empty/full silhouette built from `<path>` primitives, no
  icon font/set), left-click opens the Recycle Bin, right-click menu adds "Empty" behind
  `NNAUI.dialog` confirmation.
- **Context menu**: `NNAUI.menu` — window list (click to activate) + Minimize + Close for a
  running app, Pin/Unpin for a pinned/launchable one, Open for a folder, Open/Empty for the trash.
- **Live updates**: subscribes to `/events` (WebSocket) for `dock-changed` and
  `config-changed{what:"app"|"launch"}`, re-fetches `GET /dock/items` and re-renders.
- **`?mock=1`**: renders 8 placeholder icons (monogram squares, no network calls at all) — used by
  `tests/dock-probe.ps1`'s headless-Edge screenshot so the check does not depend on the live
  desktop's window/launch state.

## Testing

`tests/dock-probe.ps1 -Port 1627` — headless host (`--headless --port 1627 --data <tmp>`) with a
one-item `launch.json` fixture (`"dock": true`), then: `GET /dock/items` shape (≥1 `app`,
`separator`, 2×`folder`, `trash`), `GET /dock/trash` shape, `GET /dock/folder?path=<Downloads>`,
`POST /dock/pin` without a token (401/403), a foreign `Origin` header (403 — `LocalApi`'s origin
guard is global, exercised here on `GET /dock/items`), `POST /dock/pin` with the token, and a
headless-Edge screenshot of `/dock/?monitor=main&mock=1` checked with `tests/png-mean.py` (all 13
checks pass; screenshot saved to `H:\night-runs\nna-wallpaper-2\shots\stage9-dock-page.png`).

**Not verified**: an actual live `DockWindow` (AppBar reservation, magnify/jump animation,
fullscreen/auto-hide hiding, real per-monitor placement) — this worktree must not start the
engine or touch the owner's running instance on 1618. `DockManager.Apply()` is written to be a
no-op whenever `Dock.Enabled` is false (the shipped default), so it is safe to construct even
without the engine; `dotnet build`/`dotnet test` cover that it compiles and the smoke test suite
stays green, but the window has not been seen on a real desktop.
