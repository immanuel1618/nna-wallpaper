# Changelog

All notable changes to NNA Wallpaper are documented here.

## [Unreleased]

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
