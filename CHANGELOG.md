# Changelog

All notable changes to NNA Wallpaper are documented here.

## [Unreleased]

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
