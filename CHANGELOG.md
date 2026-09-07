# Changelog

All notable changes to NNA Wallpaper are documented here.

## [Unreleased]

## [0.3.5] - 2026-09-08

### Changed
- Wallpaper block titles and service labels follow the app language (`wallpaper/i18n.js`, manifest
  titles); switching the language updates the desktop without a reload.
- One control system in the settings: legacy `.btn`/`.field` styles removed, radii from tokens,
  block typography on the type scale, plain-language names for accent and palette swatches.
- SYSTEM block: value next to its label; empty LAUNCH shows a hint; empty layout page shows one message.
- Popups: text columns truncate with an ellipsis, sliders keep a fixed width; rounded through DWM.
- Backend notes are localized by code.

### Security
- `X-Frame-Options: DENY` on every response; login page sends no referrer.

### Fixed
- Popup web messages and hotkey handlers never throw out of their callbacks.
- Dragging inside the top bar or dock no longer sends a Leave to the page.
- Event broadcasts go to clients in parallel; a slow client does not delay the others.

## [0.3.4] - 2026-09-08

### Security
- The API token is returned by `GET /config` only to same-origin requests (`Sec-Fetch-Site`); sensitive
  GET routes refuse cross-site requests; every response carries `X-Content-Type-Options: nosniff`.
- Planner login is `POST` only; the login callback requires a one-time `state` issued by the app.
- Push-to-talk capture stops at 60 s (safety timer at 65 s).

### Fixed
- Pointer capture is reset when wallpaper windows are re-attached or paused (mouse could die after an
  explorer restart with a button held).
- Top bar and dock re-register their reserved area after explorer restarts; no more AppBar ping-pong
  between the two; DPI changes handled; page-reported sizes clamped to the monitor.
- Live updates: one send at a time per WebSocket client, clients are no longer dropped by a burst.
- Dragging inside the top bar or dock keeps mouse capture; right click reaches the dock page.
- Settings shell: a page requested at start is no longer overwritten by the default page.

### Changed
- Settings on the design system everywhere: taskbar page rebuilt (no native controls), palette
  swatches instead of color pickers, font size and accent knobs, Russian block titles, hints and
  units, one content width, no em dashes or text glyphs.
- Wallpaper surfaces: palette-only colors including inline styles (test covers JS/HTML), Signal used
  as a dot or notch instead of text, SYSTEM block laid out as a 2x2 grid with disks below,
  popup windows rounded.
- Cursors: readable pointing hand; sets are monochrome (line keeps one Signal dot on the arrow and hand).
- README rewritten (EN + RU) with API and testing guides; docs without em dashes.

## [0.3.3] - 2026-09-08

### Added
- Layout editor: monitors in real proportions, visible grid, drag, resize, delete, widget palette,
  live preview on the desktop, Apply / Cancel, undo and redo, keyboard.
- Blocks page: cards with previews (live capture from the desktop or a static picture), block pages
  with grouped settings and plain-language help; manifests carry `description`, `icon`, `groups`, `help`.
- Planner page: profile with Telegram avatar and name, day and week stats, TASKS sections, voice
  settings; push-to-talk hotkey (`Ctrl+Shift+Space` by default) records through the host and sends to
  the planner; recording indicator in the top bar.
- Top bar and dock are composition-hosted: clicking them no longer steals focus from the active app;
  the cursor follows the page. Popup chain logged.

### Fixed
- `player` and `stats` manifests declared a minimum size larger than the default.

## [0.3.2] - 2026-09-07

### Added
- Settings window rebuilt as a macOS-like shell: sidebar with search, pages Layout, Blocks, Appearance,
  Top bar, Dock, Taskbar, Cursor, Planner, General, About.
- Dock (`app.dock`, off by default; the `mac` preset enables it): pinned and running apps with
  magnification, labels, bounce, separator, folder fans, trash; `/dock/*` routes.
- Taskbar mode `win-only` (`taskbar.windows.mode`): the Windows taskbar stays hidden and does not slide
  out on hover; the Win key shows it together with Start. Low-level keyboard hook watches Win only,
  never logs keys, removed on pause and exit.
- Page observability: `POST /log/page`, widget `ctx.ready()/ctx.fail()`, automatic remount of a widget
  that did not render within 8 s, request retries.
- App icon and tray icon from the brand mark, background pack (`wallpaper/backgrounds`), README banner,
  preset previews.

### Fixed
- HEAD requests wrote a response body and could leave a keep-alive connection stuck, which showed up
  as an empty LAUNCH block and a stale TASKS login screen after start.

## [0.3.1] - 2026-09-07

### Added
- Composition hosting for the wallpaper windows (`engine.hosting`, default `composition`): mouse goes
  through WebView2 `SendMouseInput`, so hover no longer flickers behind the desktop icons.
- Windows service: `GET /windows`, `POST /windows/activate|minimize|close`, `GET /windows/icon`.
  Launch raises the existing window; `newInstance` (Shift-click) opens a new one.
- Audio control: `/audio/volume`, `/audio/outputs`, `/audio/output`, `/audio/sessions`, `/audio/session`,
  `/audio/mic`; capture device pick `/audio/devices`, `/audio/capture-device`.
- System info: `/system/network`, `/system/battery`, `/system/layout`, `/system/brightness` (DDC/CI),
  `/system/power`.
- Top bar v2: popovers (volume mixer, calendar, NNA menu, control center), modules network, battery,
  keyboard layout, volume; wheel over the volume icon changes volume, middle click mutes.
- Cursors: three brand cursor sets (mark, line, mono), `/cursor/status|apply|reset` with registry backup.
- Brand title bar for the settings and login windows (WindowChrome, dark DWM, snap layouts),
  remembered window size.
- Voice capture in TASKS: microphone pick, level ring, recording timer, click outside cancels,
  recognised text with a 5 second undo (`POST /planner/undo`).
- Planner login returns first name, username and photo (server function v2).

### Changed
- Focus timer model: WORK / CHILL / CYCLES, phases advance automatically, done after the last cycle.
- Appearance tab: single brand theme; palette editor removed.

### Fixed
- Focus tool buttons rendered dark text on a transparent background when active.
- Appearance tab duplicated its fields on every re-render.
- Licenses link pointed to the wrong GitHub owner.
- Launch icons keep a specific transition instead of `all`.

## [0.3.0] - 2026-09-07

### Added
- Design system v3 (`ui/`): brand palette tokens, Roboto Flex + JetBrains Mono (SIL OFL, bundled),
  type roles and Fibonacci spacing, own UI components (select, toggle, slider, segmented, popover,
  menu, tooltip, dialog, scrollbars) shared by the wallpaper page, settings and top bar.
- Live updates channel: WebSocket `/events` (`config-changed`, `layout-preview`), `POST /layout/preview`,
  `GET /events/stats`. Settings changes reach the wallpaper page as messages; only the affected block
  is re-rendered. Widget lifecycle (`ctx.setInterval/setTimeout/raf/on/onDispose`).

### Changed
- The wallpaper page is no longer reloaded on every settings change (`app.liveUpdates`, default true);
  a full reload happens only when the set of monitors changes.
- One brand theme: the page no longer applies `theme.palette`/`theme.fonts`; old font names in
  `app.json` are migrated to the v3 pair.
- Kharkiv Tone and DM Mono removed.

### Fixed
- CI smoke test compared the version with a stale literal.

## [0.2.0] - 2026-09-07

### Added
- Settings tab "Taskbar": Windows taskbar toggles (centered icons, hide Search / Task View /
  Widgets / clock, small size, transparency, auto-hide) with a backup and one-click restore,
  per-state accent styles (clear / blur / acrylic / opaque), explorer restart.
- Top bar: an always-on-top strip at the top edge of each monitor (mac-like menu bar) with
  configurable modules (brand, date, clock, weather, stats, media, planner), reserved work area,
  auto-hide, per-monitor placement. The wallpaper grid keeps its blocks below the bar.
- Presets (windows, mac, clear, night, minimal), user presets, JSON export/import.
- Local API: `GET /taskbar/status`, `POST /taskbar/apply`, `POST /taskbar/reset`,
  `POST /taskbar/restart-explorer`, `GET/POST /taskbar/presets`, `GET /taskbar/preset?id=`.

### Known limitation
- Windows 11 builds with the XAML taskbar (24H2 and later) paint their own background, so the
  accent transparency of the Windows taskbar may have no visible effect there; toggles and the
  top bar work regardless.

## [0.1.5] - 2026-09-07

### Changed
- Maintenance release used to verify the direct-feed update path.

## [0.1.4] - 2026-09-07

### Changed
- Updates are fetched from the direct release download URL first (no GitHub API rate limit); the API source is only a fallback.

## [0.1.3] - 2026-09-07

### Changed
- Maintenance release used to verify the in-app update path (tray and `POST /app/update`).

## [0.1.2] - 2026-09-07

### Fixed
- The text input window could crash the whole application when it was closed while already
  closing (Deactivated fired during Close). Now closes once; unhandled UI exceptions are logged
  instead of terminating the wallpaper.

## [0.1.1] - 2026-09-07

### Changed
- NNA Planner block redesigned to match the other blocks: task counter, briefing, meetings,
  habit chips, money, add button and voice button.
- Text input window opens next to the block on the right monitor (monitor lookup made tolerant).
- Tray "Check for updates" now checks, downloads and applies updates through Velopack; new
  `POST /app/update` route.
- Local API refuses requests whose `Host` header does not name the loopback listener.
- `/test/*` endpoints are registered only in `--headless` and `--test-engine` runs.
- Graph widget source labels come from its settings instead of being hard-coded.

### Fixed
- Pause/resume race on fast window switches.

## [0.1.0] - 2026-09-07

First public release.

### Added
- Wallpaper engine: one WebView2 window per monitor, embedded behind the desktop icons, with
  per-monitor DPI handling, input forwarding, pause under fullscreen apps, and a watchdog that
  survives explorer restarts and display changes.
- Local host on `127.0.0.1`: system stats, media control, app launcher with icon extraction,
  folder graphs, weather, events, photo folders, config delivery, and a live WASAPI audio
  spectrum over WebSocket.
- Ten built-in widgets: audio equalizer, photos, focus timer, weather and clocks, events,
  launcher, folder graph, system stats, media player, and the NNA Planner block.
- Settings window with mouse-driven layout editing (drag/resize blocks on a grid), per-widget
  settings forms, appearance, planner and general tabs.
- NNA Planner integration: desktop login through a Telegram Login Widget, a DPAPI-protected
  local session, the `auth-telegram-widget` Supabase edge function, and local API routes serving
  today's tasks, meetings, habits, money and a morning briefing, plus text/voice capture.
- Packaging with Velopack: signed-free `Setup.exe` and portable `.zip` from one build, with
  self-update from GitHub Releases.
