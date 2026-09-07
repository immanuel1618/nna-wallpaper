# Contributing

## Building

Requirements: Windows 10/11, the .NET SDK pinned in [`global.json`](global.json) (currently
`9.0.300`), and Node.js for the JavaScript unit tests.

```
dotnet restore NNA.Wallpaper.sln
dotnet build NNA.Wallpaper.sln -c Release
```

## Testing

```
dotnet test NNA.Wallpaper.sln -c Release
node --test settings/tests/*.test.mjs
```

The local API's contract is checked separately, against a running instance:

```
NNA.Wallpaper.exe --headless --port <port>
powershell -File tests/api-contract.ps1 <port>
```

`--headless` starts the host (local API, sensors, etc.) without creating any wallpaper windows,
which is what makes this runnable in CI and without a desktop session. `tests/api-contract.ps1`
compares the JSON shape (key names, not values) of the running host's responses against the
reference snapshots under `tests/contract/*.json`. Other scripts under `tests/` (`audio-ws.mjs`,
`click-probe.ps1`, `fullscreen-probe.ps1`, `import-probe.ps1`, `screen-probe.ps1`, `png-mean.py`)
are manual/local probes for specific engine behavior (audio streaming, input forwarding, pause
under fullscreen apps, `--import`, screenshot comparison) and are not part of the automated CI
build.

## Repository layout

```
src/NNA.Wallpaper/            WPF app: engine, tray, CLI args, settings/login windows
src/NNA.Wallpaper.Host/       local API, sensors, media, launcher, planner, config (library)
src/NNA.Wallpaper.Tests/      xUnit tests
wallpaper/                    wallpaper page shared by every monitor window
settings/                     settings window page
widgets/<id>/                 built-in widgets
planner/                      reference copies of the NNA Planner desktop-login pieces
docs/                         architecture, widget SDK, settings schema, planner integration
tests/                        contract tests and manual probe scripts
build/                        packaging script (build/pack.ps1)
.github/workflows/            build.yml (CI), release.yml (tagged releases)
```

## Rules

- **MIT-compatible dependencies only.** Every dependency must be MIT, BSD, Apache-2.0, MS-PL or
  similarly permissive. No GPL (or other copyleft) code or package is used or copied — see
  [THIRD-PARTY.md](THIRD-PARTY.md) for the current list.
- **No secrets in the repository.** Nothing that looks like a token, API key, or personal path
  should ever be committed; configuration with machine-specific values is produced only by
  `--import` or by the running app itself, never checked in.
- **Don't break existing widgets.** A widget's public contract (`widget.json` shape, the `ctx`
  object passed to `module` widgets, the `nna-widget.js` bridge for `page` widgets) is what
  third-party widgets rely on; changes to it should be backward compatible or clearly documented
  as breaking in [CHANGELOG.md](CHANGELOG.md).
- Keep pull requests focused; update [docs/](docs/) alongside any change that affects the local
  API, the widget manifest format, or the settings file formats.

## Adding a widget

See [docs/WIDGET-SDK.md](docs/WIDGET-SDK.md) for the full manifest reference. In short: create
`widgets/<id>/widget.json` plus a `widget.js` (rendered inside the wallpaper page) or `index.html`
(rendered in a sandboxed iframe), list the host capabilities it needs in `needs`, describe its
configurable fields in `settings`, then add it to a monitor's layout from the settings window.

## Reporting issues

Use the issue templates under `.github/ISSUE_TEMPLATE/`: bug report, feature request, or widget
request.
