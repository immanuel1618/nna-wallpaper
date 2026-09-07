# Widget SDK

A widget is a folder with a manifest. There are two kinds of entry point:

- **`module`** — a JavaScript function registered on the shared wallpaper page (used by every
  built-in widget).
- **`page`** — a self-contained `index.html`, rendered in a sandboxed `<iframe>` (for third-party
  widgets).

Both kinds appear the same way in the settings window: name, size, and a settings form generated
from the manifest.

## Folder layout

```
widgets/<id>/
  widget.json      manifest (required)
  index.html       for kind = page
  widget.js        for kind = module (registers window.NNA.widgets[<id>])
  icon.svg         list icon (optional)
  README.md        description (optional)
```

Built-in widgets live under `widgets/` next to the executable. User widgets live under
`%LOCALAPPDATA%\NNA Wallpaper\widgets`. The host scans both folders at startup and when the
settings window's widget list is refreshed.

## widget.json

```json
{
  "id": "weather",
  "name": "Weather + clocks",
  "version": "1.0.0",
  "author": "NNA1618",
  "entry": { "kind": "module", "path": "widget.js", "export": "weather" },
  "defaultSize": { "cols": 1, "rows": 8 },
  "minSize": { "cols": 1, "rows": 3 },
  "needs": ["weather"],
  "settings": [
    { "key": "refreshMin", "type": "number", "label": "Refresh, min", "default": 10, "min": 1, "max": 120 },
    { "key": "clocks", "type": "list", "label": "Clocks", "item": { "label": "string", "tz": "timezone" },
      "default": [{ "label": "MOSCOW", "tz": "Europe/Moscow" }] }
  ]
}
```

- `needs` — which host capabilities the widget uses: `stats`, `media`, `audio`, `launch`, `graph`,
  `open`, `pins`, `weather`, `events`, `planner`, `config`. The host only exposes these to the
  widget (for `page`-kind widgets, only these paths are allowed through the bridge — see below).
- `settings[].type`: `number`, `string`, `bool`, `select` (with `options[]`), `color`, `path`
  (file or folder), `timezone`, `list` (with an `item` schema), `text` (multi-line). The settings
  window renders a form from this description; values are stored in
  `config/widgets/<id>.json` and delivered to the widget on start and whenever they change.

## Built-in widgets shipped today

| id | name | entry | needs |
|---|---|---|---|
| `eq` | Audio equalizer | module | audio |
| `photos` | Photos | module | pins |
| `focus` | Focus timer | module | — |
| `weather` | Weather + clocks | module | weather |
| `events` | Events | module | events, config |
| `launch` | Launch | module | launch, config |
| `graph` | Graph | module | graph, open |
| `stats` | System | module | stats |
| `player` | Player | module | media |
| `planner` | NNA Planner | module | planner |

The `planner` widget currently renders a placeholder ("NNA PLANNER — SOON") pending the local API
routes described in [docs/PLANNER.md](PLANNER.md); its manifest and settings-window integration
are already in place.

## Lifecycle, kind = module

The wallpaper page loads `widget.js`, which registers a function
`window.NNA.widgets[id] = function (mount, ctx)` and returns an object
`{ root, destroy?(), onSettings?(settings), onResize?() }`. `ctx` is
`{ settings, monitor, block, helper: { get, post }, theme }`.

## Lifecycle, kind = page

```html
<iframe sandbox="allow-scripts" src="nna-widget://<id>/index.html">
```

The widget's folder is served through WebView2's virtual host mapping (so it has its own
origin, not `file://`). Inside the iframe, `wallpaper/nna-widget.js` (injected by the host)
provides a small bridge API — the widget never talks to the local API directly, and never sees
the API token:

```js
NNA.ready(ctx => { ... });       // ctx as above
NNA.get('/stats').then(...)      // bridge: postMessage -> wallpaper page -> host (only paths in needs)
NNA.post('/launch/item?id=steam')
NNA.on('ctx', ctx => ...)        // context pushed by the page
NNA.on('event-name', detail => ...)
NNA.toast('TEXT')
```

A third-party widget has no access to the file system, to host endpoints outside its manifest's
`needs`, or to the Planner session.

## Layout

A monitor's grid is `{ cols, rows, gap, pad }`; a placed block is
`{ widget, col, colSpan, row, rowSpan }`. The wallpaper page builds a CSS grid and places widgets
by `grid-area`. The settings window will not let a block shrink below its manifest's `minSize`.

Theme values arrive as CSS variables (`--bg-page`, `--fg`, `--fg-body`, `--fg-muted`, `--border`,
`--font-display`, `--font-mono`, `--radius`, `--dim`, matching `wallpaper/nna-brand.css`). A widget
should use these variables rather than its own colors, or it will look wrong when the theme
changes.

## Adding a widget

1. Create `widgets/<id>/` (built-in) or `%LOCALAPPDATA%\NNA Wallpaper\widgets\<id>\` (user) with a
   `widget.json`.
2. Add `widget.js` (module) or `index.html` (page) per the entry kind.
3. Restart the app, or use "refresh widget list" in the settings window, then add the widget to a
   monitor's layout from the **Monitors and layout** tab.

---

# Widget SDK (RU)

Виджет — папка с манифестом. Два вида точки входа: **module** (функция JavaScript,
регистрируется на общей странице обоев — так устроены все встроенные виджеты) и **page**
(самостоятельный `index.html` в изолированном `iframe` — для сторонних виджетов). Оба вида
одинаково видны в окне настроек: имя, размер, форма настроек из манифеста.

## Структура папки

```
widgets/<id>/
  widget.json      манифест (обязателен)
  index.html       для kind = page
  widget.js        для kind = module (регистрирует window.NNA.widgets[<id>])
  icon.svg         значок в списке (опционально)
  README.md        описание (опционально)
```

Встроенные виджеты лежат в `widgets/` рядом с исполняемым файлом, пользовательские — в
`%LOCALAPPDATA%\NNA Wallpaper\widgets`. Хост сканирует обе папки при старте и по кнопке
обновления списка в окне настроек.

## widget.json

Поля: `id, name, version, author, entry {kind: module|page, path, export?}, defaultSize {cols,
rows}, minSize {cols, rows}, needs[], settings[]`.

`needs` — какие возможности хоста нужны виджету: `stats, media, audio, launch, graph, open, pins,
weather, events, planner, config`. Хост открывает виджету только их (для `page` — только эти пути
разрешены через мост). `settings[].type`: `number, string, bool, select (options[]), color, path,
timezone, list (со схемой item), text`. Окно настроек строит форму по этому описанию; значения
хранятся в `config/widgets/<id>.json`.

## Встроенные виджеты

`eq` (эквалайзер, needs: audio), `photos` (фото, needs: pins), `focus` (фокус-таймер, без needs),
`weather` (погода и часы, needs: weather), `events` (события, needs: events, config), `launch`
(запуск, needs: launch, config), `graph` (граф папок, needs: graph, open), `stats` (системные
показатели, needs: stats), `player` (медиаплеер, needs: media), `planner` (блок NNA Planner,
needs: planner — сейчас выводит заглушку «NNA PLANNER — SOON» до появления соответствующих точек
локального API, см. [docs/PLANNER.md](PLANNER.md)).

## Жизненный цикл kind = module

Страница обоев подключает `widget.js`; он регистрирует `window.NNA.widgets[id] = function (mount,
ctx)` и возвращает `{ root, destroy?(), onSettings?(settings), onResize?() }`. `ctx` = `{
settings, monitor, block, helper: {get, post}, theme }`.

## Жизненный цикл kind = page

`<iframe sandbox="allow-scripts" src="nna-widget://<id>/index.html">` — папка виджета отдаётся
через виртуальный хост WebView2. Внутри доступен мост `nna-widget.js` (кладётся хостом): виджет
никогда не обращается к локальному API напрямую и не видит токен. Методы: `NNA.ready(cb)`,
`NNA.get(path)`, `NNA.post(path)` (только пути из `needs`), `NNA.on(name, cb)`, `NNA.toast(text)`.
Сторонний виджет не имеет доступа к файловой системе, к чужим точкам хоста и к сессии
планировщика.

## Раскладка

Сетка монитора: `{cols, rows, gap, pad}`; блок: `{widget, col, colSpan, row, rowSpan}`. Страница
обоев строит CSS grid и расставляет виджеты по `grid-area`. Окно настроек не даёт сжать блок
меньше `minSize` из манифеста. Тема приходит CSS-переменными (`--bg-page, --fg, --fg-body,
--fg-muted, --border, --font-display, --font-mono, --radius, --dim`, как в `nna-brand.css`) —
виджет обязан использовать их, а не свои цвета.

## Как добавить виджет

1. Создать `widgets/<id>/` (встроенный) или `%LOCALAPPDATA%\NNA Wallpaper\widgets\<id>\`
   (пользовательский) с `widget.json`.
2. Добавить `widget.js` или `index.html` по типу точки входа.
3. Перезапустить приложение или обновить список виджетов в окне настроек, затем добавить виджет
   на раскладку монитора во вкладке «Мониторы и раскладка».
