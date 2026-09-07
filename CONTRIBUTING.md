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

`node --test <folder>` does not work in this repository's environment: run one file at a time, as
listed above (or as `node --test <dir>/*.test.mjs` on a shell that expands globs).

Everything else under `tests/` (`*.ps1`, `*.mjs`, plus the small standalone `.csproj` dev helpers)
is a manual or CI probe against a running instance, not part of `dotnet test`. The full list,
what each one covers, and what it needs (a headless instance started with `--headless --port <p>
--data <tmp>`, or a live desktop) is in [docs/TESTING.md](docs/TESTING.md).

## Repository layout

```
src/NNA.Wallpaper/            WPF app: engine, tray, CLI args, settings/login windows
src/NNA.Wallpaper.Host/       local API, sensors, media, launcher, planner, config (library)
src/NNA.Wallpaper.Tests/      xUnit tests
wallpaper/                    wallpaper page shared by every monitor window
settings/                     settings window: shell.js (sidebar/routing) + pages/<name>.js (one
                               file per sidebar page: layout, blocks, appearance, topbar, dock,
                               taskbar, cursor, planner, general, about)
ui/                            shared design-system components (tokens.css, components.css/js) used
                               by the wallpaper page, settings and the top bar
topbar/                       top bar strip and its popovers; modules.js holds one factory per
                               top bar module (brand, date, clock, weather, stats, media, planner...)
dock/                          the mac-like dock window
widgets/<id>/                 built-in widgets
planner/                      reference copies of the NNA Planner desktop-login pieces
docs/                         architecture, widget SDK, settings schema, planner integration, API
                               reference, testing guide
tests/                        contract test and manual probe scripts
build/                        packaging and asset scripts (build/pack.ps1, build/cursors.py, ...)
.github/workflows/            build.yml (CI), release.yml (tagged releases)
```

## Rules

- **MIT-compatible dependencies only.** Every dependency must be MIT, BSD, Apache-2.0, MS-PL or
  similarly permissive. No GPL (or other copyleft) code or package is used or copied; see
  [THIRD-PARTY.md](THIRD-PARTY.md) for the current list.
- **No secrets in the repository.** Nothing that looks like a token, API key, or personal path
  should ever be committed; configuration with machine-specific values is produced only by
  `--import` or by the running app itself, never checked in.
- **Don't break existing widgets.** A widget's public contract (`widget.json` shape, the `ctx`
  object passed to `module` widgets, the `nna-widget.js` bridge for `page` widgets) is what
  third-party widgets rely on; changes to it should be backward compatible or clearly documented
  as breaking in [CHANGELOG.md](CHANGELOG.md).
- **Palette discipline.** Every color used in `ui/`, the wallpaper page, settings, the top bar and
  the dock must be one of the 18 hex values from the NNA1618 v3 brand palette, defined in
  `ui/tokens.css`. `node --test ui/tests/palette.test.mjs` scans every CSS file under `ui/` plus
  the named wallpaper/settings/topbar/dock stylesheets for stray hex colors and fails on anything
  off-palette; run it after touching any `.css` file.
- **Brand voice for interface strings.** User-facing text (settings labels/help, widget copy,
  README, docs) states things plainly in the present tense, uses "we"/"мы" rather than "I", and
  avoids exclamation marks, emoji and em dashes (use a period, comma, colon or parentheses
  instead). This applies to both the English and Russian copy.
- Keep pull requests focused; update [docs/](docs/) alongside any change that affects the local
  API ([docs/API.md](docs/API.md)), the widget manifest format
  ([docs/WIDGET-SDK.md](docs/WIDGET-SDK.md)), or the settings file formats
  ([docs/SETTINGS.md](docs/SETTINGS.md)).

## Adding a widget

See [docs/WIDGET-SDK.md](docs/WIDGET-SDK.md) for the full manifest reference. In short: create
`widgets/<id>/widget.json` (with `description`, `icon`, `groups` and `settings[]` entries carrying
a bilingual `label`/`help`, since the Blocks settings page groups fields by manifest) plus a
`widget.js` (rendered inside the wallpaper page, gets a `ctx` with `ready`/`fail`/`setInterval`/
`onDispose` and friends) or an `index.html` (rendered in a sandboxed iframe, talks to the host
through the `nna-widget.js` `postMessage` bridge). List the host capabilities the widget needs in
`needs`, then add it to a monitor's layout from the settings window's Layout page.

## Adding a settings page

Each sidebar page is its own file under `settings/pages/<name>.js`, mounted by `settings/shell.js`
(`settings/shell-logic.js` holds the pure routing/search functions covered by
`settings/tests/shell.test.mjs`, with no DOM). A new page needs an entry in the sidebar list, a
`mount<Name>Tab(el, ctx)`-style function building its DOM with the shared `ui/` components
(`ui/components.js`: select, toggle, slider, segmented, popover, menu, tooltip, dialog) and
`settings/dom.js` helpers (`el()`, `groupCard()`, `settingRow()`), and, if it edits `app.json`,
reads its state from `GET /config/full` and writes it back through `PUT /config`.

## Adding a top bar module

`topbar/modules.js` holds one factory per module:
`window.TopBarModules.<id> = function (el, ctx) { ...; return { start, stop }; }`. `el` is the
module's container element; `ctx.get`/`ctx.post` are the fetch helpers `topbar/bar.js` provides
(GET needs no token, POST sends `?t=<token>`); `ctx.setVisible(bool)` shows or hides the module.
Register the new id in the top bar's module list (`app.json`'s `topBar` config, or a preset under
`presets/taskbar/`) to place it on the bar.

## Reporting issues

Use the issue templates under `.github/ISSUE_TEMPLATE/`: bug report, feature request, or widget
request.
