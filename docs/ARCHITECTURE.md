# Architecture

## Process

A single process, `NNA.Wallpaper.exe` (WPF, .NET 8, target `net8.0-windows10.0.19041.0`, x64,
self-contained in release builds). `Program.Main` calls `VelopackApp.Build().Run()` first (so the
installer's own invocations of the exe for install/update/uninstall hooks are handled and exit
early), then starts the WPF `App`.

The solution is split into:

```
src/NNA.Wallpaper/          WPF app: tray icon, single instance, CLI args, settings/login windows,
                             autostart, updates (Velopack), the wallpaper Engine
src/NNA.Wallpaper.Host/     class library: local HTTP/WebSocket API, sensors, media, launcher,
                             icons, graphs, weather, events, config, widget scan
src/NNA.Wallpaper.Tests/    xUnit tests
wallpaper/                  wallpaper page (HTML/CSS/JS) shared by every monitor window
settings/                   settings window page
widgets/<id>/               built-in widgets
planner/                    reference copies of the NNA Planner login page and edge function
                             (the source of truth is the NNA Planner repository)
```

Pages are loaded through WebView2's `SetVirtualHostNameToFolderMapping`, never through `file://`:
the wallpaper page is served from a virtual host mapped to the `wallpaper` folder, the settings
page from a virtual host mapped to `settings`, and each widget's `page`-kind entry from a virtual
host mapped to its own folder.

## Engine: rendering behind the desktop icons

One WPF window per monitor, embedded behind the desktop icons (`Progman`/`WorkerW`), each hosting
one WebView2 control:

1. `Progman = FindWindow("Progman", null)`.
2. Desktop mode is detected by window style, not by Windows version: if `Progman` has the extended
   style `WS_EX_NOREDIRECTIONBITMAP`, this is the newer desktop layout where `WorkerW` is a direct
   child of `Progman` (found with `FindWindowEx`, picking the `WorkerW` sibling that does **not**
   host `SHELLDLL_DefView`, since that one owns the icons). Otherwise, on the classic layout, the
   app sends `Progman` the internal message `0x052C` to make explorer spawn a top-level `WorkerW`,
   then finds the top-level window that hosts `SHELLDLL_DefView` and uses its next `WorkerW`
   sibling. If `WorkerW` does not appear immediately, the message is retried once.
3. Each wallpaper window is created as a popup, then reparented as a child of that `WorkerW` (or of
   `Progman` if no `WorkerW` exists) with `WS_CHILD` and `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW`,
   positioned with `MapWindowPoints` from desktop to parent coordinates, and pushed to the bottom
   of the Z-order.
4. A watchdog re-parents the wallpaper windows whenever `WorkerW` is recreated (explorer restart,
   theme change, waking from sleep) or the monitor layout changes.
5. Each window hosts one `CoreWebView2Controller` from a single shared `CoreWebView2Environment`
   (user data folder under `%LOCALAPPDATA%\NNA Wallpaper\WebView2`). Context menus, DevTools
   (unless `--devtools`), accelerator keys, zoom, pinch-zoom, swipe navigation, the built-in error
   page, autofill and password autosave are all disabled; external drops are rejected.
6. DPI: a WebView2 control does not automatically follow a monitor's own scale factor, so each
   window sets `RasterizationScale` from `GetDpiForMonitor` for the monitor it is on, and reacts to
   `WM_DPICHANGED` / display-settings-changed by recomputing it.
7. Input: mouse and keyboard reach the wallpaper windows because they are real (if invisible)
   windows in the desktop's window tree — no global hooks are used. Right-click still opens the
   normal desktop context menu. Typing into a widget's own input field is not done through the
   wallpaper window at all: it opens a small top-level WPF text-entry window that takes real
   keyboard focus and hands the typed text to the page through `PostWebMessageAsJson`. Voice input
   uses the page's own `MediaRecorder` and needs no window focus.
8. Pause: every wallpaper window is paused (WebView2's `TrySuspendAsync`, otherwise resumed) when a
   fullscreen or presentation-mode app is detected in front of it, or the session is locked. The
   app also enforces an FPS cap by telling each page how often to render.

## Host: local API

`HostServices` wires one `LocalApi` (an `HttpListener` on `127.0.0.1`, with WebSocket support) and
registers every feature service's routes on it. CORS is open (`*`, since only local pages call it).
Every request other than `GET`/`HEAD` must carry the app's API token as the `X-Token` header or a
`t` query parameter; the token is generated on first run and stored in `app.json`.

### Local API — routes that exist today

| Route | Method | Service | Notes |
|---|---|---|---|
| `/health` | GET | host | app/version info, per-monitor status |
| `/config` | GET | host | theme, layout and widget settings the wallpaper page needs for one monitor (`?monitor=`) |
| `/config/full` | GET | ConfigApiService | everything the settings window renders |
| `/config/defaults` | GET | ConfigApiService | built-in default layout for a monitor |
| `/config` | PUT | ConfigApiService | apply an edited configuration |
| `/config/open-folder` | POST | ConfigApiService | open Explorer on the data/log folder |
| `/stats` | GET | StatsService | CPU (per core and total), memory, disks, network, GPU (if `nvidia-smi` is available), uptime |
| `/media` | GET | MediaService | current playback session (GSMTC), with a thumbnail data URL |
| `/media/toggle`, `/media/play`, `/media/pause`, `/media/next`, `/media/prev` | POST | MediaService | playback control |
| `/media/seek` | POST | MediaService | seek to a position |
| `/launch/list` | GET | LaunchService | launcher groups and items |
| `/launch/item`, `/launch/group` | POST | LaunchService | launch one item or a whole group |
| `/icon/<id>.png` | GET | LaunchService | cached launcher icon, extracted lazily |
| `/graph` | GET | GraphService | a folder graph (graphify output) for the graph widget |
| `/weather` | GET | WeatherService | current weather and forecast (Open-Meteo) |
| `/events` | GET | EventsService | one-time and daily events |
| `/pins` | GET | PinsService | list of photos for the photo widget |
| `/open` | POST | OpenService | open a path, restricted to configured root folders |
| `/edit` | POST | OpenService | opens the settings window on a given tab |
| `/widgets` | GET | WidgetsService | manifests of every installed widget (built-in and user) |
| `/audio` | WebSocket | AudioService | ~30 frames/second of 64-band spectrum data per channel, for the equalizer widget |
| `/app/exit` | POST | host | stop the running instance |
| `/app/reload` | POST | host | reload configuration and refresh wallpaper pages |
| `/test/event`, `/test/events`, `/test/log` | POST/GET | host | test-only endpoints used by the contract tests (`--headless`) |
| `/wallpaper/`, `/settings/`, `/widgets/` | GET | host | static file serving for the wallpaper page, settings page and widget folders |

The NNA Planner endpoints described in [docs/PLANNER.md](PLANNER.md) (`/planner/status`,
`/planner/today`, `/planner/done`, `/planner/habit`, `/planner/capture`, `/planner/login`,
`/planner/logout`, `/planner/callback`) are part of the designed contract that the settings page
and the Planner widget already call, but are not yet registered on the host in this build — the
desktop Planner block currently renders as a placeholder. See [docs/PLANNER.md](PLANNER.md) for
the full design.

### Sensors and integrations

- CPU, memory, disk and network counters come from Win32/`.NET` APIs; GPU stats come from
  `nvidia-smi` when present and are omitted otherwise.
- Media info comes from `GlobalSystemMediaTransportControlsSessionManager` (GSMTC); the equalizer's
  spectrum comes from a WASAPI loopback capture of the default render device, processed with a
  1024-point FFT into 64 log-spaced bands per channel between 40 Hz and 16 kHz.
- Weather comes from the Open-Meteo API (no API key required).
- Launching items uses `ShellExecute`, `Process.Start` or the Windows application-activation API
  for packaged (Store) apps, matching the item's configured launch method; icons are extracted and
  cached as PNGs for files, folders and packaged apps alike.

## Web: the wallpaper page

`wallpaper/index.html?monitor=<id>` loads `layout.js`, which reads `GET /config?monitor=` for the
theme (applied as CSS variables), the monitor's grid and block placement, and each placed widget's
settings. A `module`-kind widget is mounted directly into the page; a `page`-kind widget is mounted
in a sandboxed `<iframe>` and talks to the host only through a `postMessage` bridge (see
[docs/WIDGET-SDK.md](WIDGET-SDK.md)). The `/audio` WebSocket feed is turned into a `weAudio` event
on the page so the equalizer widget's logic did not need to change when it moved off Wallpaper
Engine.

## Distribution

Releases are built and packaged with [Velopack](https://velopack.io):

```
dotnet publish src/NNA.Wallpaper/NNA.Wallpaper.csproj -c Release -r win-x64 --self-contained -o publish
vpk pack --packId NNA.Wallpaper --packVersion <version> --packDir publish \
         --mainExe NNA.Wallpaper.exe --packTitle "NNA Wallpaper" --packAuthors NNA1618 \
         --icon assets/app.ico --outputDir Releases
```

(`build/pack.ps1` wraps these steps, installs the pinned `vpk` tool version, and verifies the
expected artifacts exist afterward.) The result under `Releases/` is
`NNA.Wallpaper-win-Setup.exe`, a full package, and `NNA.Wallpaper-win-Portable.zip` — all from the
same publish output. Publishing a GitHub Release is done with `vpk upload github --publish`, which
uploads `releases.win.json` alongside the assets so `Velopack.UpdateManager` (configured with a
`GithubSource` pointing at this repository) can find updates. The client checks once a day and on
demand from the tray menu / settings window.

The GitHub Actions workflow `.github/workflows/build.yml` builds and tests the solution on every
push to `main` and on every pull request. `.github/workflows/release.yml` builds, packages and
(for a tag push, or a manual run with `dry_run` set to false) publishes a release when a `v*` tag
is pushed.

Builds are not code-signed; see the SmartScreen note in the [README](../README.md#smartscreen-warning).

## Security boundaries

- The local API listens on `127.0.0.1` only.
- Every non-`GET` request needs the API token.
- `/open` is restricted to a configured allow-list of root folders.
- `page`-kind widgets run in a sandboxed iframe with no access to the file system, to host
  endpoints outside their manifest's `needs`, or to the Planner session.
- The Planner session file is protected with Windows DPAPI for the current user; logs never
  contain tokens.
- No telemetry, no administrator rights required.

## Third-party packages

See [THIRD-PARTY.md](../THIRD-PARTY.md) for the full list and licenses.

---

# Архитектура (RU)

## Процесс

Один процесс `NNA.Wallpaper.exe` (WPF, .NET 8, `net8.0-windows10.0.19041.0`, x64, self-contained в
релизе). `Program.Main` сначала вызывает `VelopackApp.Build().Run()` (чтобы корректно обработать
служебные вызовы exe со стороны установщика — установка/обновление/удаление), затем запускает WPF
`App`. Части решения: `src/NNA.Wallpaper` (WPF-приложение: трей, единственный экземпляр, аргументы
командной строки, окна настроек и входа, автозапуск, обновления через Velopack, движок обоев),
`src/NNA.Wallpaper.Host` (библиотека: локальный HTTP/WebSocket API, датчики, медиа, запуск,
иконки, графы, погода, события, конфиг, сканирование виджетов), `src/NNA.Wallpaper.Tests`
(тесты xUnit), `wallpaper/` (страница обоев), `settings/` (окно настроек), `widgets/<id>/`
(встроенные виджеты), `planner/` (справочные копии страницы входа и edge-функции планировщика —
источник истины остаётся в репозитории NNA Planner).

Страницы грузятся через `SetVirtualHostNameToFolderMapping`, без `file://`.

## Движок: окно за иконками рабочего стола

По одному окну WPF на монитор, встроенному за иконками (`Progman`/`WorkerW`), в каждом — контрол
WebView2:

1. `Progman = FindWindow("Progman", null)`.
2. Режим рабочего стола определяется по стилю окна, а не по версии Windows: если у `Progman` есть
   расширенный стиль `WS_EX_NOREDIRECTIONBITMAP` — это новый рабочий стол, где `WorkerW` является
   прямым дочерним окном `Progman` (ищется перебором дочерних `WorkerW`, из которых выбирается тот,
   что **не** содержит `SHELLDLL_DefView` — то окно держит иконки). Иначе, в классическом варианте,
   приложение посылает `Progman` внутреннее сообщение `0x052C`, чтобы explorer создал отдельный
   `WorkerW`, затем находит окно с `SHELLDLL_DefView` и берёт соседний с ним `WorkerW`. Если
   `WorkerW` не появился сразу, сообщение отправляется повторно.
3. Каждое окно обоев создаётся как popup, затем переводится в дочернее того `WorkerW` (или
   `Progman`, если `WorkerW` не найден) со стилями `WS_CHILD` и `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW`,
   координаты пересчитываются `MapWindowPoints`, окно опускается в конец Z-порядка.
4. Наблюдатель переустанавливает родителя при пересоздании `WorkerW` (перезапуск explorer, смена
   темы, выход из сна) или изменении набора мониторов.
5. В каждом окне — один `CoreWebView2Controller` из общего `CoreWebView2Environment` (данные —
   `%LOCALAPPDATA%\NNA Wallpaper\WebView2`). Контекстное меню, DevTools (кроме `--devtools`),
   акселераторы, зум, pinch-zoom, свайп-навигация, встроенная страница ошибок, автозаполнение и
   сохранение паролей отключены; внешний drop запрещён.
6. DPI: контрол WebView2 не следует масштабу монитора автоматически, поэтому масштаб
   (`RasterizationScale`) выставляется явно по `GetDpiForMonitor` и пересчитывается при
   `WM_DPICHANGED`/смене конфигурации экранов.
7. Ввод: мышь и клавиатура доходят до окон обоев напрямую, без глобальных хуков, так как это
   настоящие (хоть и невидимые) окна в дереве рабочего стола. Правый клик по-прежнему открывает
   штатное меню рабочего стола. Набор текста в поле виджета не идёт через окно обоев: всплывает
   отдельное маленькое top-level окно WPF, забирает фокус клавиатуры и передаёт строку странице
   через `PostWebMessageAsJson`. Голосовой ввод использует `MediaRecorder` самой страницы и фокуса
   не требует.
8. Пауза: окна обоев приостанавливаются (`TrySuspendAsync` WebView2, иначе возобновляются) при
   полноэкранном приложении или презентационном режиме поверх них, а также при блокировке сессии.
   Дополнительно применяется ограничение FPS.

## Хост: локальный API

`HostServices` создаёт один `LocalApi` (`HttpListener` на `127.0.0.1` с поддержкой WebSocket) и
регистрирует на нём маршруты каждого сервиса. CORS открыт (`*` — обращаются только локальные
страницы). Любой запрос, кроме `GET`/`HEAD`, должен нести токен API (`X-Token` или `?t=`); токен
генерируется при первом запуске и хранится в `app.json`.

### Реально существующие маршруты локального API

Полный список — в английской части этого файла (таблица «Local API — routes that exist today»).
Кратко: `/health`, `/config` (GET/PUT), `/config/full`, `/config/defaults`, `/config/open-folder`,
`/stats`, `/media*`, `/launch/*`, `/icon/<id>.png`, `/graph`, `/weather`, `/events`, `/pins`,
`/open`, `/edit`, `/widgets`, `/audio` (WebSocket), `/app/exit`, `/app/reload`,
`/test/*` (только для тестов), статика `/wallpaper/`, `/settings/`, `/widgets/`.

Маршруты планировщика (`/planner/status`, `/planner/today`, `/planner/done`, `/planner/habit`,
`/planner/capture`, `/planner/login`, `/planner/logout`, `/planner/callback`) — часть
спроектированного контракта (см. [docs/PLANNER.md](PLANNER.md)), который уже вызывают окно
настроек и виджет планировщика, но который ещё не зарегистрирован на хосте в этой сборке: блок
планировщика на рабочем столе сейчас выводит заглушку.

## Распространение

Релизы собираются и упаковываются [Velopack](https://velopack.io) (`build/pack.ps1`):
`dotnet publish` self-contained win-x64 → `vpk pack` → `Releases/NNA.Wallpaper-win-Setup.exe`,
полный пакет и `NNA.Wallpaper-win-Portable.zip` из одной сборки. Публикация — `vpk upload github
--publish`, который заодно кладёт `releases.win.json` для `Velopack.UpdateManager` (источник —
`GithubSource` на этот репозиторий); проверка обновлений — раз в сутки и по кнопке.
`.github/workflows/build.yml` собирает и тестирует при каждом push в `main` и в pull request;
`.github/workflows/release.yml` собирает, упаковывает и (при пуше тега `v*` либо ручном запуске с
`dry_run=false`) публикует релиз. Сборки не подписаны сертификатом — см. предупреждение о
SmartScreen в [README](../README.md#предупреждение-smartscreen).

## Границы безопасности

API только на `127.0.0.1`; любой запрос кроме GET требует токена; `/open` ограничен списком
разрешённых корневых папок; виджеты вида `page` работают в песочнице iframe без доступа к файловой
системе, к чужим точкам хоста и к сессии планировщика; файл сессии планировщика защищён DPAPI
текущего пользователя; в логах токенов нет; телеметрии нет; права администратора не требуются.
