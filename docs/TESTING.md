# Testing

Three layers: xUnit tests (`dotnet test`), JavaScript unit tests (Node's built-in test runner),
and a set of manual/CI probe scripts under `tests/` that drive a running host or a live desktop.
None of the probes are wired into CI; they are run by hand while working on the feature they cover.

A known limitation of this environment: `node --test <folder>` does not work here: pass a single
file to `node --test` each time, not a directory.

## xUnit tests

```
dotnet test NNA.Wallpaper.sln -c Release
```

`src/NNA.Wallpaper.Tests/SmokeTests.cs` checks `HostInfo.AppName`/`HostInfo.Version` (semantic
version format, matching the assembly version).

## JavaScript unit tests

Run one file at a time:

```
node --test settings/tests/layout-model.test.mjs
node --test settings/tests/manifest.test.mjs
node --test settings/tests/shell.test.mjs
node --test settings/tests/taskbar-presets.test.mjs
node --test ui/tests/components.test.mjs
node --test ui/tests/palette.test.mjs
node --test ui/tests/tokens.test.mjs
node --test wallpaper/tests/layout-diff.test.mjs
node --test widgets/focus/tests/model.test.mjs
```

| File | Covers |
|---|---|
| `settings/tests/layout-model.test.mjs` | grid math for the layout editor: clamp, move, resize, overlap detection, undo/redo history |
| `settings/tests/manifest.test.mjs` | every built-in `widgets/<id>/widget.json` carries `description`/`icon`/`groups`, every `settings[]` entry belongs to a group with a bilingual label/help, `defaultSize` fits `minSize` |
| `settings/tests/shell.test.mjs` | settings sidebar routing: page-name mapping (including old pre-redesign tab names), search matching, deep-links from launcher/events into Blocks |
| `settings/tests/taskbar-presets.test.mjs` | every JSON file in `presets/taskbar` only uses known modes/sides/monitors/keys |
| `ui/tests/components.test.mjs` | popover placement, slider step, list navigation, typeahead (the shared `ui/` component logic) |
| `ui/tests/palette.test.mjs` | every hex color literal in `ui/**/*.css` and the named wallpaper/settings/topbar CSS files is one of the 18 approved brand palette colors |
| `ui/tests/tokens.test.mjs` | `ui/tokens.css` defines all v3 core palette colors |
| `wallpaper/tests/layout-diff.test.mjs` | diffing two block layouts (added/removed/moved) for live-patch updates |
| `widgets/focus/tests/model.test.mjs` | focus timer state machine (WORK/CHILL/CYCLES phase advance) |

`planner/test-widget-auth.mjs` is a separate, non-`node --test` script: it signs a Telegram Login
Widget payload by hand and exercises the `auth-telegram-widget` Supabase edge function directly
(`--dry-run` skips the network call). It reads secrets only from `H:\secrets` in memory and never
prints or logs them; it is not runnable outside a machine that has those secrets, and is not part
of this repository's automated build.

## Contract test (needs a headless instance)

```
NNA.Wallpaper.exe --headless --port <port>
powershell -File tests/api-contract.ps1 <port>
```

`--headless` starts the host (local API, sensors, etc.) with no wallpaper windows, which is what
makes it runnable in CI and without a desktop session. `api-contract.ps1` compares the JSON
**key names** (not values) of the running host's responses for `/config`, `/events`, `/graph`,
`/health`, `/launch/list`, `/media`, `/pins`, `/stats`, `/weather` and `/widgets` against the
reference snapshots in `tests/contract/*.json`; every key in the snapshot must still be present,
extra keys on the live side are fine.

## Probe scripts

Every probe below either starts its own `--headless` instance (never the owner's live one on
1618) or is explicitly documented as safe to run against a live instance; read a probe's own
header comment before running it against anything but a fresh `--data <tmp>` folder.

### Start their own headless host, need only a temp `--data` folder

| Script | Command | Covers |
|---|---|---|
| `tests/activate-probe.ps1` | `powershell -File tests\activate-probe.ps1 -Port 1621 [-Data <tmp>] [-Exe <path>]` | window service: click-to-raise vs. Shift-click-to-duplicate, minimize, close |
| `tests/audio-system-probe.ps1` | `powershell -File tests\audio-system-probe.ps1 -Port <p>` | `/audio/*`, `/system/*` GET routes, `+/-1` volume round-trip, negative cases (no token, foreign Origin); needs a host already running on `-Port` |
| `tests/cursor-probe.ps1` | `powershell -File tests\cursor-probe.ps1 -Port 1626 [-Data <tmp>] [-Exe <path>]` | `/cursor/status\|apply\|reset`, backup create-once, registry restore in a `finally` block |
| `tests/dock-probe.ps1` | `powershell -File tests\dock-probe.ps1 -Port 1627 [-Data <tmp>] [-Exe <path>] [-Edge <path>]` | `/dock/*` routes, auth/Origin guards, renders `dock/?monitor=main&mock=1` in headless Edge |
| `tests/hotkey-probe.ps1` | `powershell -File tests\hotkey-probe.ps1 -Port 1635 -Exe <path>` | `/planner/status.hotkey` shape and `/planner/capture-file` under `--headless` (no real `RegisterHotKey`/mic possible headless; see docs/PLANNER.md for the manual check) |
| `tests/layout-editor-probe.mjs` | `node tests/layout-editor-probe.mjs [port]` | end-to-end layout editor: drag, live preview, Cancel, Apply, delete, undo. Starts its own throwaway `--headless` host and **stops it again at the end of the run** |
| `tests/import-probe.ps1` | `powershell -File tests\import-probe.ps1 -Exe <path> [-Source <old-layout-folder>]` | `--import`, single-instance forwarding, `--exit` against a real (non-headless) `NNA.Wallpaper.exe`; writes only under `$env:TEMP` |
| `tests/planner-probe.ps1` | `powershell -File tests\planner-probe.ps1 -Port 1628 -Data <tmp> -Exe <path>` (or `-Voice tests\fixtures\voice-test.wav`) | every `/planner/*` route, with a self-signed Telegram Login Widget payload; secrets read from `H:\secrets` in memory only |

### CDP probes against a running host (start the host yourself first)

Drive a headless Microsoft Edge instance over the Chrome DevTools Protocol (raw WebSocket
JSON-RPC, no npm dependency). Start the host first with
`NNA.Wallpaper.exe --headless --port <port> --data <tmp>`, then run the script against that port.

| Script | Command | Covers |
|---|---|---|
| `tests/blocks-probe.mjs` | `node tests/blocks-probe.mjs <port>` | Blocks settings page: 10 preview cards, opening a block's detail page, breadcrumb/back, grouped settings |
| `tests/planner-page-probe.mjs` | `node tests/planner-page-probe.mjs <port> [msedge.exe path]` | Planner settings page: profile/TASKS/voice cards, logged-out state, slider changes reflected in `/config/full` |
| `tests/page-selfheal.mjs` | `node tests/page-selfheal.mjs <port>` | wallpaper page boot resilience on `/wallpaper/?monitor=vertical`: normal boot, injected latency, failed-then-retried requests, delayed planner login; all widgets must reach `ready` within their timeout without a manual reload |
| `tests/topbar-probe.ps1` | (see script; starts its own headless host) | top bar v2 popup pages under `?mock=1` (volume, calendar, NNA menu, Control Center), screenshot-not-blank check, `node --check` syntax pass over `topbar/*.js` |

### Need a live desktop session (cannot run headless)

| Script | Command | Covers |
|---|---|---|
| `tests/click-probe.ps1` | `powershell -File tests\click-probe.ps1 -Port 1619 [-X ..] [-Y ..] [-Monitor 0]` | a real `SendInput` click lands on the wallpaper window |
| `tests/fullscreen-probe.ps1` | `powershell -File tests\fullscreen-probe.ps1 -Port 1619 [-Monitor 0]` | opening a borderless fullscreen window pauses only that monitor |
| `tests/hover-probe.ps1` | `powershell -File tests\hover-probe.ps1 -Port <p> -X <x> -Y <y> [-Seconds 2] [-OutDir <dir>]` | composition hosting does not flicker `:hover` state on real cursor movement (the bug the classic `"window"` hosting mode had) |
| `tests/no-reload-probe.ps1` | `powershell -File tests\no-reload-probe.ps1 -Port <p>` | repeated config edits are live-patched over `/events`, never a full page reload, against a running (non-headless) host |
| `tests/screen-probe.ps1` | `powershell -File tests\screen-probe.ps1 [-Expect "#rrggbb"]` | samples on-screen pixel colour per monitor (baseline / regression check) |
| `tests/settings-window-probe.ps1` | `powershell -File tests\settings-window-probe.ps1 -Port <p> [-Token ..] [-Data ..]` | custom `BrandWindow` chrome (no system title bar, min/max/close work) via UI Automation, against a live settings window |
| `tests/taskbar-lock-probe.ps1` | `powershell -File tests\taskbar-lock-probe.ps1` (see script header) | `win-only` taskbar mode end to end; explicitly allowed to run against the owner's live instance on 1618 because a headless run creates no windows; always restores taskbar/top bar settings from the pre-test snapshot in a `finally` block |
| `tests/topbar-live-probe.ps1` | (see script; runs `tests/TopBarPreview`) | clicking a top bar module opens a real `PopupWindow`, and the bar itself never steals foreground focus |

### WebSocket smoke tests (need a running headless or live host)

| Script | Command | Covers |
|---|---|---|
| `tests/audio-ws.mjs` | `node tests/audio-ws.mjs <port>` | `/audio` WebSocket delivers a 512-byte (128 float32) binary spectrum frame within 3 s |
| `tests/events-ws.mjs` | `node tests/events-ws.mjs <port>` | `/events` hello handshake, `config-changed` broadcast on a `PUT /config`, foreign-Origin upgrade refused with 403, `/events/stats` counts |

### Standalone WPF/native dev helpers (not part of `NNA.Wallpaper.sln`)

Each opens a real window against an already-running instance (default port 1618) purely to see or
screenshot behavior that cannot be captured headlessly. Read-only against the target host.

| Project | Purpose |
|---|---|
| `tests/CompositionProbe` | standalone proof that `DCompositionCreateDevice2` + a WebView2 `CoreWebView2CompositionController` keeps `:hover` stable under `SendMouseInput`-only input, away from the desktop icon layer entirely |
| `tests/TopBarPreview` | shows a real, composition-hosted `TopBarWindow` + `PopupWindow` on the vertical monitor, reading pages from the already-running instance |
| `tests/WindowPreview` | opens the real `SettingsWindow` to see/screenshot the custom title bar chrome without a full app run |

### Other

| Script | Purpose |
|---|---|
| `tests/png-mean.py` | computes mean brightness of a PNG; used by other probes to detect a blank screenshot |
| `tests/stage4-gate.ps1` | one-off historical gate script comparing the C# host against the old Wallpaper Engine + Python helper setup; hard-codes paths from that comparison and is not meant to be run outside that machine |

## Fixtures

`tests/fixtures/layout-ok.json` / `layout-overlap.json`: sample layouts for layout-model tests.
`tests/fixtures/silence.webm` / `voice-test.wav`: audio fixtures for capture-pipeline probes (the
WAV is a generated 440 Hz sine, not real speech; transcribing it to "nothing parsed" is an
expected, passing outcome).
