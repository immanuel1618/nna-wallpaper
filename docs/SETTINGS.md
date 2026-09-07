# Settings files

Data folder: `%LOCALAPPDATA%\NNA Wallpaper\` (overridden with `--data <dir>` for portable use —
then it is `<dir>` next to the executable). All files are UTF-8 JSON, read at startup, on
`PUT /config` and on `--reload`; writes are atomic (temp file + rename).

## config/app.json

General settings. Fields (`AppSettings`):

```json
{
  "version": 1,
  "apiPort": 1618,
  "apiToken": "<generated on first run>",
  "language": "ru",
  "autostart": false,
  "updates": { "check": true, "channel": "stable" },
  "pauseOnFullscreen": true,
  "fpsCap": 30,
  "theme": {
    "preset": "nna1618",
    "palette": { "bgPage": "#050505", "bgSurface": "#000000", "fg": "#FFFFFF", "fgBody": "#C8C8C8",
                 "fgMuted": "#808080", "fgGhost": "#1D1D1D", "border": "#434343",
                 "glass": "rgba(139,139,139,0.03)" },
    "fonts": { "display": "Kharkiv Tone", "mono": "DM Mono" },
    "radius": 32, "gap": 24, "pad": 48, "blur": 14, "dim": 0.4
  },
  "audio": { "enabled": true, "device": null },
  "openWith": {},
  "openRoots": [],
  "graphs": {},
  "graphLimits": { "maxNodes": 100, "maxLinks": 450 },
  "weather": { "lat": 55.7558, "lon": 37.6173, "tz": "Europe/Moscow", "name": "MOSCOW" },
  "planner": {
    "supabaseUrl": "https://tvpmtjidonohpgwhyssm.supabase.co",
    "publishableKey": "<publishable, not secret; protected by row-level security>",
    "loginUrl": "https://planner.nna1618.com/desktop/login.html",
    "show": ["tasks", "meetings", "habits", "money", "briefing"]
  }
}
```

On a clean install `openWith` and `graphs` are empty and `openRoots` is an empty list; values with
machine-specific paths only appear after running `--import`. `audio.device` is `null` for the
default playback device's loopback capture.

## config/monitors.json

Per-monitor layouts.

```json
{
  "version": 1,
  "monitors": [
    {
      "id": "\\\\.\\DISPLAY2|3440x1440",
      "name": "Main 3440x1440",
      "enabled": true,
      "grid": { "cols": 3, "rows": 16, "colWeights": [2.2, 1, 2.2], "gap": 24, "pad": 48 },
      "blocks": [
        { "widget": "eq", "col": 1, "colSpan": 1, "row": 1, "rowSpan": 4 },
        { "widget": "photos", "col": 1, "colSpan": 1, "row": 5, "rowSpan": 12 },
        { "widget": "focus", "col": 2, "colSpan": 1, "row": 1, "rowSpan": 8 },
        { "widget": "weather", "col": 2, "colSpan": 1, "row": 9, "rowSpan": 8 },
        { "widget": "events", "col": 3, "colSpan": 1, "row": 1, "rowSpan": 4 },
        { "widget": "launch", "col": 3, "colSpan": 1, "row": 5, "rowSpan": 4,
          "settingsOverride": { "compact": true } },
        { "widget": "graph", "col": 3, "colSpan": 1, "row": 9, "rowSpan": 8 }
      ]
    },
    {
      "id": "\\\\.\\DISPLAY1|1440x2560",
      "name": "Vertical 1440x2560",
      "enabled": true,
      "grid": { "cols": 1, "rows": 4, "gap": 24, "pad": 48 },
      "blocks": [
        { "widget": "stats", "col": 1, "colSpan": 1, "row": 1, "rowSpan": 1 },
        { "widget": "planner", "col": 1, "colSpan": 1, "row": 2, "rowSpan": 1 },
        { "widget": "player", "col": 1, "colSpan": 1, "row": 3, "rowSpan": 1 },
        { "widget": "launch", "col": 1, "colSpan": 1, "row": 4, "rowSpan": 1 }
      ]
    }
  ]
}
```

A monitor's identity key is `"{DeviceName}|{Width}x{Height}"` rather than `\\.\DISPLAYn`, because
the latter is not stable across reboots. A monitor with no matching entry gets a default layout:
landscape monitors get the "Main" layout, portrait monitors get the "Vertical" layout.

## config/widgets/<id>.json

Keys follow the widget's manifest `settings[]`. Examples:

```json
// focus.json
{ "work": 25, "rest": 5, "longRest": 15, "sessionsBeforeLong": 4, "autoNext": false,
  "presets": [ { "id": "classic", "label": "25 / 5 x4", "work": 25, "rest": 5, "longRest": 15, "per": 4 } ] }
```

```json
// photos.json
{ "folder": "", "intervalMin": 10, "order": "sequential", "fadeMs": 1400, "zoom": true, "zoomScale": 1.08 }
```

```json
// eq.json
{ "bars": 56, "attack": 0.55, "release": 0.075, "gain": 1.7, "minHeight": 0.02 }
```

## launch.json, events.json

Same shape as the files produced by an older Wallpaper Engine + Python-helper setup, so
`--import` can bring them in unchanged; also editable through forms in the settings window.

## Settings window: shell + pages (v2)

`settings/index.html` is a macOS System Settings-style shell: a 233px sidebar (search box, then
one row per page, greek index α..κ + a small inline-SVG icon + label) on the left, and a page
header (greek + title + the save-status pill) plus a scrollable content area of "group cards" on
the right — the owner's chosen reference is macOS, the palette/type stay on tokens v3 (see
`docs/DESIGN-SYSTEM.md`). This replaced the original five hand-rolled tabs (α..ε rendered by one
`app.js`); the old tab shape is still reachable through `?tab=` compatibility aliases (below).

### File layout

| File | Role |
|---|---|
| `settings/index.html` | loads `ui/logic.js` + `ui/components.js` (classic scripts, so `window.NNAUI` exists) then `settings/shell.js` as an ES module. |
| `settings/shell.js` | boots (`GET /config/full`, `GET /widgets`), renders the sidebar + header, owns routing (`?tab=`, hash-free — page switches are in-memory) and the search box. |
| `settings/shell-logic.js` | pure functions behind the shell: `mapTabName`, `matchesQuery`, `filterSidebar`, `firstMatch` — no DOM, covered by `settings/tests/shell.test.mjs` under plain `node --test`. |
| `settings/api.js` | `api()`/`put()` (fetch wrapper + `X-Token`), `makeScheduler()` (debounced save), `setStatus(text, isError)` (status pill + error toast). Moved out of the old `app.js` so every page shares one copy. |
| `settings/dom.js` | `el()` (the same tiny DOM builder that used to be copy-pasted in `app.js`/`forms.js`/`taskbar-tab.js`), `groupCard(id, title, ...rows)`, `settingRow(title, help, control)` — the "card with rows" building blocks every new page uses. |
| `settings/icons.js` | inline-SVG line icons (16x16, 1.5px stroke, no icon sets) — one per sidebar page plus a few utility glyphs (search, drag handle, trash, folder, check). |
| `settings/i18n.js` | the ru/en dictionary + `makeT()`/`detectLang()` (unchanged shape) plus `SAVED_MARKERS` (both languages' "Saved" string, used by `api.js`'s `setStatus` to light the pill green without a separate flag). |
| `settings/forms.js`, `settings/taskbar-tab.js`, `settings/layout-editor.js`, `settings/layout-model.js` | unchanged internals, reused by the pages below. `layout-editor.js`/`layout-model.js` belong to the next stage (block-card redesign) and were left untouched. |
| `settings/pages/*.js` | one module per sidebar page (see the table below). |
| `settings/app.js` | now a one-line `import "./shell.js"` — kept only so `<script src="app.js">` still resolves if anything external still points at it. |

### Pages (sidebar order)

| # | Greek | Page key | Module | Notes |
|---|---|---|---|---|
| 1 | α | `layout` | `pages/layout.js` | thin wrapper around `layout-editor.js`'s `mountLayoutTab` — unchanged, owned by the next stage. |
| 2 | β | `blocks` | `pages/blocks.js` | widget/launch/events row list (icon + on/off toggle) on the left, `forms.js`-generated form on the right. The toggle flips `widgetSettings[id].enabled` (default true) — a forward-compatible flag, not read by any widget yet; full preview cards are a later stage. |
| 3 | γ | `appearance` | `pages/appearance.js` | dim/radius/gap/pad/blur/fps/pause — the old geometry-only γ tab, rebuilt on `NNAUI.slider`/`NNAUI.toggle`. |
| 4 | δ | `topbar` | `pages/topbar.js` | new page: our own always-on-top bar (`app.topBar`). Enable/monitors/height, style (segmented mode + color + opacity), auto-hide/reserve-space, and a drag-and-drop module editor across three zones (left/center/right, HTML5 DnD). Replaces the inline "top bar" section the old ζ tab used to render. |
| 5 | ε | `dock` | `pages/dock.js` | new page, `app.dock` (`DockSettings` — **not a real `AppSettings` field in this branch**, see "Dock: not persisted" below). |
| 6 | ζ | `taskbar` | `pages/taskbar.js` | the real Windows taskbar: preset picker + per-state styling + Windows toggles, moved in from `taskbar-tab.js` (now `mountTaskbarTab(el, {..., showTopBar:false})` — the top-bar section moved to its own page, item 4). Adds a "Windows taskbar mode" select (`normal` / `autohide` / `win-only`) next to the existing tri-state toggles. |
| 7 | η | `cursor` | `pages/cursor.js` | the three brand cursor variants (`GET/POST /cursor/*`), preview cards built from the real `arrow.svg`/`hand.svg` copied to `settings/assets/cursors/<variant>/` (source: `brand/cursors/<variant>/*.svg`), size 32/48/64, apply/reset, backup status. |
| 8 | θ | `planner` | `pages/planner.js` | login status + what-to-show — old δ tab, carried over near-verbatim; redesign is a later stage. |
| 9 | ι | `general` | `pages/general.js` | autostart, language, API port, updates, **microphone pick** (new — `GET/PUT /audio/capture-device`, used by the planner voice block), import from folder, data/log folders. |
| 10 | κ | `about` | `pages/about.js` | new page: mark (`settings/assets/mark-white-64.png`, copied from `H:\brand\nna1618_mark_v2\png\nna1618_mark_white_64.png`), name, live version (`GET /health`), the `NNA1618 CLUSTER` signature, links (repository/releases/licenses/changelog), MIT note, "check updates" button. |

### `?tab=` compatibility

`settings/shell-logic.js`'s `mapTabName(tab)` resolves both the current page keys (`layout`,
`blocks`, ..., `about`) and every value the old five-tab shell understood: the pre-redesign greek
keys (`alpha`→layout, `beta`→blocks, `gamma`→appearance, `delta`→planner, `epsilon`→general,
`zeta`→taskbar) and the even older aliases (`launch`/`events`→blocks with a sub-selection,
`config`/`nna-config`/`general`→general, `theme`→appearance). Anything unrecognised falls back to
`layout`. The WPF `SettingsWindow` keeps loading `/settings/?token=&tab=&lang=` unchanged.

### How to add a page

1. Create `settings/pages/<name>.js` exporting `render(container, ctx)` and, if it should be
   found by search, `keywords(lang)` (a flat array of ru/en strings — group titles, notable field
   names). `ctx` = `{ t, lang, api, put, config, widgets, selection, setSelection, setLang,
   onStatus, fetchDefaults }` — `config`/`widgets` are the live objects from `GET /config/full` /
   `GET /widgets`, shared across every page for the life of the window (mutate them in place after
   a successful save so switching pages shows fresh data without a reload).
2. Build the page body from `groupCard(id, titleText, ...rows)` and `settingRow(titleText,
   helpText, control)` (`settings/dom.js`) — `id` on `groupCard` is what the search highlighter
   matches against. Prefer `window.NNAUI.select/toggle/slider/segmented` for controls; wrap
   `select`/`slider` in `settingRow` (not directly in a bare flex row) — both set `width:100%` on
   themselves, and `settingRow`'s `.row-control` column is what caps that width so it doesn't
   swallow the row's label.
3. Register the page in `settings/shell.js`'s `PAGES` array (icon in `settings/icons.js`'s
   `ICONS`, nav label key `nav_<name>` in `i18n.js`), and add the key to
   `settings/shell-logic.js`'s `mapTabName` passthrough list.
4. Add any new i18n keys to `settings/i18n.js` (ru + en). No build step — the page is picked up on
   next reload of `/settings/`.

### Search

The sidebar's search box filters the nav list live: `settings/shell-logic.js`'s `filterSidebar`
matches the query (case/whitespace-insensitive substring) against each page's nav label and its
`keywords(lang)`; non-matching rows get `.dim` (still clickable, just faded) rather than being
removed, so the list doesn't jump around while typing. Enter opens the first match
(`firstMatch`). On the open page, `shell.js` also scans the rendered `.group-title` elements and
adds `.match` (a Signal-colored left border) to any group whose title contains the query — a
light-weight, DOM-scan approach rather than routing every page's groups through a shared
metadata structure.

### Dock: not persisted yet

`pages/dock.js` always renders (with defaults when `app.dock` is absent) and calls `PUT /config
{app:{dock:{...}}}` on every change — the request succeeds (`{ok:true}`), but nothing is actually
saved: `AppSettings` has no `Dock` property in this branch yet (it ships from a parallel branch),
and `ConfigApiService.PutConfig` deserializes the merged `app` patch straight into `AppSettings`
with `System.Text.Json`'s default behavior of silently ignoring unknown members (see `Json.cs` —
no `UnmappedMemberHandling.Disallow`). The page shows a hint line saying so when `app.dock` is
absent from `GET /config/full`. The taskbar page's new "Windows taskbar mode" select
(`taskbar.windows.mode`) has the same fate for the same reason (`TaskbarWindowsSettings` has no
`Mode` property yet) — both are wired ahead of their C# fields on purpose, so the pages need no
further JS changes once those fields land.

Window rules (unchanged): no emoji, no exclamation marks, no em/en dashes, uppercase mono labels
where the old tabs used them, dark theme, default size 1100x720, minimum 900x600.

---

# Файлы настроек (RU)

Папка данных: `%LOCALAPPDATA%\NNA Wallpaper\` (переопределяется `--data <папка>` для portable —
тогда это `<папка>` рядом с exe). Все файлы — JSON в UTF-8, читаются при старте, при `PUT /config`
и по `--reload`; запись атомарная (временный файл + переименование).

## config/app.json

Общие настройки — поля показаны в английской части этого файла (класс `AppSettings`). На чистой
установке `openWith` и `graphs` пустые, `openRoots` — пустой список; значения с путями конкретной
машины появляются только после `--import`.

## config/monitors.json

Раскладки по мониторам: `grid {cols, rows, colWeights?, gap, pad}` и список `blocks[]`
(`{widget, col, colSpan, row, rowSpan, settingsOverride?}`). Идентификатор монитора —
`"{DeviceName}|{Width}x{Height}"` (не `\\.\DISPLAYn`, так как это имя не стабильно между
перезагрузками). Монитор без записи получает раскладку по умолчанию: альбомный — как «Main»,
портретный — как «Vertical».

## config/widgets/<id>.json

Ключи по `settings[]` манифеста виджета — примеры для `focus`, `photos`, `eq` приведены в
английской части.

## launch.json, events.json

Формат совпадает с файлами старой связки Wallpaper Engine + Python-помощник, поэтому `--import`
переносит их без изменений; также редактируются формами в окне настроек.

## Окно настроек: сайдбар и страницы (v2)

`settings/index.html` теперь построено как macOS System Settings: слева сайдбар 233px (поиск,
затем по строке на страницу — греческий индекс α..κ, inline-SVG иконка, подпись), справа —
заголовок страницы (греческая буква + название + плашка статуса сохранения) и прокручиваемая
область карточек-групп. Ориентир — macOS, палитра и шрифты только из токенов v3 (см.
`docs/DESIGN-SYSTEM.md`). Заменяет прежние пять вкладок (α..ε, все в одном `app.js`); старые
`?tab=` значения по-прежнему работают через алиасы (см. ниже).

### Структура файлов

`settings/shell.js` — загрузка (`GET /config/full`, `GET /widgets`), сайдбар, заголовок,
маршрутизация и поиск. `settings/shell-logic.js` — чистые функции без DOM (`mapTabName`,
`matchesQuery`, `filterSidebar`, `firstMatch`), покрыты `settings/tests/shell.test.mjs` под
`node --test`. `settings/api.js` — `api()`/`put()`, `makeScheduler()` (отложенное сохранение),
`setStatus(text, isError)` (плашка статуса + тост об ошибке) — вынесены из старого `app.js`, чтобы
каждая страница не копировала их заново. `settings/dom.js` — `el()` (тот же построитель DOM, что
раньше был скопирован в `app.js`/`forms.js`/`taskbar-tab.js`), `groupCard`, `settingRow` — базовые
блоки «карточка с группой строк», на которых построены новые страницы. `settings/icons.js` —
inline-SVG линейные иконки (16×16, 1.5px), без готовых наборов. `settings/i18n.js` — тот же
словарь ru/en плюс `SAVED_MARKERS` (строки «Сохранено»/«Saved» для подсветки плашки зелёным).
`settings/forms.js`, `settings/taskbar-tab.js`, `settings/layout-editor.js`,
`settings/layout-model.js` — без изменений логики (последние два — зона следующего этапа,
редизайн карточек блоков). `settings/pages/*.js` — по одному модулю на страницу сайдбара.
`settings/app.js` — теперь `import "./shell.js"` в одну строку, оставлен для обратной
совместимости.

### Страницы (порядок в сайдбаре)

1. **Раскладка (α)** — `pages/layout.js`, обёртка над `layout-editor.js` без изменений.
2. **Блоки (β)** — `pages/blocks.js`, список виджетов/запуска/событий строками (иконка +
   переключатель) слева, форма справа. Переключатель — `widgetSettings[id].enabled` (по умолчанию
   включён), на перспективу; ни один виджет пока его не читает.
3. **Внешний вид (γ)** — `pages/appearance.js`, геометрия/производительность на
   `NNAUI.slider`/`NNAUI.toggle`.
4. **Верхняя строка (δ)** — новая страница `pages/topbar.js`: включение, мониторы, высота, стиль
   (сегменты режима + цвет + непрозрачность), автоскрытие, резерв места, редактор модулей
   drag-and-drop по трём зонам (слева/центр/справа).
5. **Док (ε)** — новая страница `pages/dock.js`, `app.dock` (см. «Док: пока не сохраняется» ниже).
6. **Панель задач (ζ)** — `pages/taskbar.js`, перенос `taskbar-tab.js` (пресет + панель Windows;
   секция «верхняя строка» из него убрана — теперь отдельная страница δ) плюс новый select режима
   панели Windows (обычная / автоскрытие / только по Win).
7. **Курсор (η)** — новая страница `pages/cursor.js`, три варианта, превью из настоящих
   `arrow.svg`/`hand.svg` (скопированы в `settings/assets/cursors/<variant>/` из
   `brand/cursors/<variant>/*.svg`), размер 32/48/64, применить/сбросить.
8. **Планировщик (θ)** — `pages/planner.js`, перенос без изменений.
9. **Общие (ι)** — `pages/general.js`, плюс новый выбор микрофона (`GET/PUT
   /audio/capture-device`).
10. **О программе (κ)** — новая страница `pages/about.js`: знак (`settings/assets/mark-white-64.png`,
    копия `H:\brand\nna1618_mark_v2\png\nna1618_mark_white_64.png`), версия из `/health`, подпись
    `NNA1618 CLUSTER`, ссылки, MIT.

### Совместимость `?tab=`

`mapTabName` в `settings/shell-logic.js` понимает и новые ключи страниц, и старые греческие имена
дореформенных вкладок (`alpha`→layout, `beta`→blocks, `gamma`→appearance, `delta`→planner,
`epsilon`→general, `zeta`→taskbar), и более старые алиасы (`launch`/`events`→blocks с
под-выбором, `config`/`nna-config`/`general`→general, `theme`→appearance). Неизвестное значение —
`layout`. WPF `SettingsWindow` по-прежнему открывает `/settings/?token=&tab=&lang=` без изменений.

### Как добавить страницу

Создать `settings/pages/<name>.js` с `render(container, ctx)` и (для поиска) `keywords(lang)`;
собрать вёрстку из `groupCard`/`settingRow` (`settings/dom.js`), для `select`/`slider` оборачивать
именно в `settingRow` — оба компонента ставят себе `width:100%`, и колонка `.row-control` в
`settingRow` — это то, что ограничивает эту ширину, а не съедает подпись строки. Добавить страницу
в массив `PAGES` в `shell.js`, иконку в `settings/icons.js`, ключи `nav_<name>` в `i18n.js`.

### Поиск

Поле поиска фильтрует список сайдбара вживую (`filterSidebar`: подстрока без учёта регистра по
названию страницы и её `keywords(lang)`); несовпавшие строки получают `.dim`, а не исчезают.
Enter открывает первое совпадение. На открытой странице `shell.js` дополнительно подсвечивает
(`.match`, левая засечка Signal) карточки, чей заголовок (`.group-title`) содержит запрос —
простое сканирование DOM после рендера страницы, без отдельной схемы метаданных по группам.

### Док: пока не сохраняется

`pages/dock.js` рисуется всегда (дефолты, если `app.dock` отсутствует) и шлёт `PUT /config
{app:{dock:{...}}}` на каждое изменение — запрос отвечает `{ok:true}`, но ничего не сохраняется:
в этой ветке `AppSettings` ещё не имеет свойства `Dock` (появится в параллельной ветке), а
`ConfigApiService.PutConfig` десериализует патч `app` в `AppSettings` через `System.Text.Json` с
поведением по умолчанию — неизвестные поля молча игнорируются (см. `Json.cs`, там нет
`UnmappedMemberHandling.Disallow`). Страница показывает об этом подсказку, когда `app.dock`
отсутствует в `GET /config/full`. Тот же исход у нового select режима панели Windows
(`taskbar.windows.mode`) — у `TaskbarWindowsSettings` тоже пока нет поля `Mode`. Обе страницы
специально написаны заранее под будущие поля — когда они появятся на C#-стороне, правки JS не
нужны.

Правила окна (без изменений): без эмодзи, без восклицательных знаков, без длинных тире, метки
uppercase моно там, где были раньше, тёмная тема, размер по умолчанию 1100×720, минимум 900×600.
