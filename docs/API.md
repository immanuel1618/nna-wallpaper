# Local API

The host listens on `http://127.0.0.1:<port>/` (default port `1618`), plus `localhost` as a
fallback bind. Every request must carry a `Host` header naming that loopback listener, and a
cross-origin request (browser `Origin` header) is refused unless it matches the same origin. GET
and HEAD requests need no token; every other method (POST, PUT, DELETE) must carry the app's API
token, either as the `X-Token` header or a `t` query parameter. WebSocket upgrades are accepted
before the token check runs, so `/audio` and `/events` need no token to connect.

Source of truth: `src/NNA.Wallpaper.Host/HostServices.cs`, `src/NNA.Wallpaper.Host/Services/*.cs`
(`Register(LocalApi)`), `src/NNA.Wallpaper.Host/Planner/PlannerService.cs`, and
`src/NNA.Wallpaper/App.xaml.cs` (`RegisterAppRoutes`, for routes that need the WPF shell rather
than the host library alone). This document only lists routes that exist in that code.

Legend: the **Token** column reads "no" for GET/HEAD (never needed) and "yes" for a POST/PUT/DELETE
route (the uniform token rule above applies; there are no exceptions to it in the code).

## Host core

| Route | Method | Token | Response |
|---|---|---|---|
| `/health` | GET | no | app name, version, uptime, per-monitor status, and a health snapshot (media/gpu/audio) |
| `/config` | GET | no | theme, layout and widget settings for one monitor (`?monitor=`) |
| `/app/exit` | POST | yes | `{ok:true}`, then stops the running instance |
| `/app/reload` | POST | yes | `{ok:true}`, reloads configuration and refreshes the wallpaper pages |
| `/test/event`, `/test/events`, `/test/log` | POST/GET | yes (POST) | test-only, registered only when the host runs with `--headless` or `--test-engine` |
| `/wallpaper/`, `/settings/`, `/topbar/`, `/presets/`, `/widgets/`, `/ui/` | GET | no | static files for each web root |

## Config

`src/NNA.Wallpaper.Host/Services/ConfigApiService.cs`

| Route | Method | Token | Response |
|---|---|---|---|
| `/config/full` | GET | no | everything the settings window renders on load |
| `/config/defaults` | GET | no | built-in default layout for a monitor |
| `/config` | PUT | yes | applies an edited configuration, broadcasts `config-changed` |
| `/config/open-folder` | POST | yes | opens Explorer on the data/log folder, `{ok:true}` |

## Stats, media, launcher

| Route | Method | Token | Response |
|---|---|---|---|
| `/stats` | GET | no | CPU (overall and per-core), memory, disks, network, uptime, GPU if `nvidia-smi` is available |
| `/media` | GET | no | current playback session (GSMTC), with a thumbnail data URL |
| `/media/toggle`, `/media/play`, `/media/pause`, `/media/next`, `/media/prev` | POST | yes | playback control, `{ok:bool}` |
| `/media/seek` | POST | yes | seeks to a position, `{ok:bool}` |
| `/launch/list` | GET | no | launcher groups and items from `launch.json` |
| `/launch/item`, `/launch/group` | POST | yes | launches one item or a whole group, `{ok:bool}` |
| `/icon/<id>.png` | GET | no | a cached launcher icon, extracted lazily |
| `/windows` | GET | no | top-level, user-facing windows (for the launcher/dock to raise instead of duplicating) |
| `/windows/activate`, `/windows/minimize`, `/windows/close` | POST | yes | acts on one window by handle, `{ok:bool}` |
| `/windows/icon` | GET | no | icon of one window's owning process |

## Widgets, graph, weather, events, pins, misc

| Route | Method | Token | Response |
|---|---|---|---|
| `/widgets` | GET | no | manifests of every installed widget (built-in and user), plus scan errors |
| `/widgets/<id>/preview.png` | GET | no | a live capture of that widget's placed cell, or the static `preview.png` shipped with it (`?live=0` forces the static image) |
| `/graph` | GET | no | a graphify `graph.json` for the graph widget, trimmed to configured node/link caps (`?src=`) |
| `/weather` | GET | no | current weather and forecast (Open-Meteo, 10-minute cache) |
| `/events` | GET | no | `events.json` contents (`events` and `daily` keys, plus `mtime`) |
| `/events` | WebSocket | no | live-update channel: `hello`, `config-changed`, `layout-preview` messages |
| `/events/stats` | GET | no | `{clients, sent}` counters for the live-update channel |
| `/layout/preview` | POST | yes | broadcasts a transient `layout-preview` message without touching disk |
| `/events/emit` | POST | yes | test-only: broadcasts an arbitrary JSON body |
| `/pins` | GET | no | list of photos for the photo widget (`?d=<widgetId>`) |
| `/pins/file/<widgetId>/<name>` | GET | no | serves one photo file byte-for-byte |
| `/open` | POST | yes | opens a path, restricted to configured root folders, `{ok:bool}` |
| `/edit` | POST | yes | opens the settings window on a given tab, `{ok:true}` |
| `/log/page` | POST | yes | wallpaper page/widget writes one line into `app.log` (2 KB body cap, 20 msg/s rate limit) |

## Audio and system

| Route | Method | Token | Response |
|---|---|---|---|
| `/audio` | WebSocket | no | ~30 frames/second of 64-band spectrum data per channel, for the equalizer widget |
| `/audio/volume` | GET/PUT | yes (PUT) | output volume and mute state |
| `/audio/outputs` | GET | no | list of render (output) devices |
| `/audio/output` | PUT | yes | switches the default render device, `{ok:bool}` |
| `/audio/sessions` | GET | no | per-app volume mixer sessions |
| `/audio/session` | PUT | yes | sets one session's volume/mute, `{ok:bool}` |
| `/audio/session-icon` | GET | no | icon for one mixer session's process |
| `/audio/mic` | GET/PUT | yes (PUT) | microphone volume and mute state |
| `/audio/devices` | GET | no | render and capture (microphone) device labels |
| `/audio/capture-device` | GET/PUT | yes (PUT) | preferred microphone (by friendly name, matched against browser device labels) |
| `/system/network` | GET | no | current network adapter/connection info |
| `/system/battery` | GET | no | battery charge and status |
| `/system/layout` | GET | no | current keyboard layout |
| `/system/brightness` | GET/PUT | yes (PUT) | monitor brightness over DDC/CI |
| `/system/dnd` | GET/PUT | no / yes | do-not-disturb state; unsupported on this host (`PUT` answers `400`) |
| `/system/power` | POST | yes | sleep/restart/shut down, requires `confirm` in the body |

## Cursors, dock, taskbar and top bar

| Route | Method | Token | Response |
|---|---|---|---|
| `/cursor/status` | GET | no | which brand cursor scheme (if any) is installed, and whether a backup exists |
| `/cursor/apply` | POST | yes | installs a cursor scheme, backing up the original once, `{ok:bool}` |
| `/cursor/reset` | POST | yes | restores the backed-up scheme, `{ok:false, error:"no backup"}` if none is left |
| `/dock/` | GET | no | static files for `dock/index.html` and friends |
| `/dock/items` | GET | no | pinned and running apps, folders, trash, merged with open windows |
| `/dock/pin`, `/dock/unpin`, `/dock/reorder` | POST | yes | edits the pinned set, `{ok:bool}` |
| `/dock/trash` | GET | no | `{ok, empty, items, bytes}` for the Recycle Bin |
| `/dock/trash/open`, `/dock/trash/empty` | POST | yes | opens or empties the Recycle Bin, `{ok:bool}` |
| `/dock/folder` | GET | no | entries of one configured folder (the "fan" preview) |
| `/dock/folder/open` | POST | yes | opens that folder in Explorer, `{ok:true}` |
| `/dock/file-icon` | GET | no | icon for one file in a dock folder preview |
| `/taskbar/status` | GET | no | Windows taskbar and top bar enabled/applied state |
| `/taskbar/apply` | POST | yes | reloads config and re-applies taskbar + top bar, `{ok:true}` |
| `/taskbar/reset` | POST | yes | restores Windows taskbar settings from the backup, `{ok:bool, message}` |
| `/taskbar/restart-explorer` | POST | yes | restarts `explorer.exe`, `{ok:true}` |
| `/taskbar/presets` | GET | no | list of taskbar+top bar presets |
| `/taskbar/presets` | POST | yes | saves a user preset, `{ok:true, id}` |
| `/taskbar/preset` | GET | no | one preset by `?id=` |

## NNA Planner

`src/NNA.Wallpaper.Host/Planner/PlannerService.cs`

| Route | Method | Token | Response |
|---|---|---|---|
| `/planner/status` | GET | no | login state, quota, push-to-talk hotkey registration state |
| `/planner/today` | GET | no | today's/overdue tasks, meetings, habits, money, briefing (cached 30 s) |
| `/planner/done` | POST | yes | marks a task done, `{ok:bool}` |
| `/planner/habit` | POST | yes | checks off a habit, `{ok:bool}` |
| `/planner/capture` | POST | yes | adds an entry by text or voice (base64 audio, 12 MB cap) |
| `/planner/login` | GET | no | opens the Telegram Login Widget window |
| `/planner/logout` | POST | yes | clears the local session, `{ok:true}` |
| `/planner/callback` | GET | no | receives the Telegram Login Widget redirect (no token: called by the login page itself, before a session exists) |
| `/planner/input` | POST | yes | opens the top-level text-entry window (InputWindow) at a widget's screen position |
| `/planner/test-delete` | POST | yes | test-only: deletes a captured test entry |
| `/planner/undo` | POST | yes | undoes the last capture within its 5-second window |
| `/planner/profile` | GET | no | signed-in user's name, username, photo |
| `/planner/avatar` | GET | no | cached Telegram avatar image |
| `/planner/stats` | GET | no | day/week planner stats |
| `/planner/capture-file` | POST | yes | test-only: runs a WAV file through the same capture path as a hotkey release |

See [PLANNER.md](PLANNER.md) for the full login flow and data model.

## App shell (WPF)

`src/NNA.Wallpaper/App.xaml.cs` (`RegisterAppRoutes`): routes that need the running WPF shell
(opening real windows, pausing the live engine) rather than the host library alone.

| Route | Method | Token | Response |
|---|---|---|---|
| `/app/settings` | POST | yes | opens the settings window, optionally on `?tab=`, `{ok:true}` |
| `/app/pause`, `/app/resume` | POST | yes | pauses/resumes wallpaper rendering, `{ok:bool}` |
| `/app/check-updates` | POST | yes | checks GitHub Releases for an update |
| `/app/update` | POST | yes | downloads and applies an update through Velopack |
| `/app/login` | POST | yes | opens the Planner login window, `{ok:true}` |

## Response shape stability

Contract tests (`tests/api-contract.ps1` against `tests/contract/*.json`) check that the JSON
**keys** of `/config`, `/events`, `/graph`, `/health`, `/launch/list`, `/media`, `/pins`,
`/stats`, `/weather` and `/widgets` stay backward compatible: every key in the reference snapshot
must still be present in the live response, extra keys are fine. See [TESTING.md](TESTING.md) for
how to run it.
