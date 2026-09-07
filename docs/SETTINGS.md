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

## Settings window tabs

The settings window (`settings/index.html`) has five tabs, labelled with Greek letters (α β γ δ ε)
in the design used by NNA Planner:

1. **Monitors and layout (α)** — list of monitors; for the selected one, a scaled grid where
   blocks are dragged and resized with the mouse (snapped to grid cells), "+ block" to add one
   from the widget list, delete, enable/disable a monitor, edit the grid (columns/rows/weights),
   "reset to default". Changes apply immediately (`PUT /config`); "undo" restores the previous
   snapshot.
2. **Blocks (β)** — widget list on the left, a form generated from the selected widget's manifest
   on the right; for the launcher: groups and items with a file picker and automatic icon
   extraction; for events: one-time dates and daily entries; for photos: folder and ordering.
3. **Appearance (γ)** — theme preset, palette, fonts, corner radius, blur, dim, FPS cap, pause
   under fullscreen apps.
4. **Planner (δ)** — login status, "Log in with Telegram" / "Log out", what to show, AI usage
   limit.
5. **General (ε)** — autostart, language (RU/EN), API port, updates (check now, channel), import
   from folder, open data folder, log, version, licenses.

Window rules: no emoji, uppercase DM Mono labels, dark theme, default size 1100x720, minimum
900x600.

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

## Вкладки окна настроек

Окно настроек (`settings/index.html`) — пять вкладок с греческими буквами (α β γ δ ε):

1. **Мониторы и раскладка (α)** — список мониторов; для выбранного — масштабная сетка, блоки
   таскаются и растягиваются мышью по ячейкам, кнопка «+ блок», удаление, включение/выключение
   монитора, редактирование сетки, «Сброс к умолчанию». Изменения применяются сразу (`PUT
   /config`); «Отменить» возвращает предыдущий снимок.
2. **Блоки (β)** — виджет слева, форма из его манифеста справа; для запуска — группы и пункты с
   выбором файла и автоизвлечением иконки; для событий — даты и ежедневные записи; для фото —
   папка и порядок показа.
3. **Внешний вид (γ)** — пресет темы, палитра, шрифты, скругление, размытие, затемнение, лимит
   FPS, пауза под полноэкранными окнами.
4. **Планировщик (δ)** — статус входа, «Войти через Telegram»/«Выйти», что показывать, лимит ИИ.
5. **Общие (ε)** — автозапуск, язык (RU/EN), порт API, обновления, импорт из папки, папка данных,
   лог, версия, лицензии.

Правила окна: без эмодзи, метки uppercase DM Mono, тёмная тема, размер по умолчанию 1100×720,
минимум 900×600.
