# Changelog

All notable changes to NNA Wallpaper are documented here.

## [Unreleased]

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
