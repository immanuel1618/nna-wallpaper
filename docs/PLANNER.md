# NNA Planner integration

NNA Wallpaper can show a desktop block connected to [NNA Planner](https://planner.nna1618.com),
a separate task/habit/money-tracking service reachable through a Telegram bot and a web app. This
document describes the login flow and the data the desktop block uses. The source of truth for
the planner service itself (database schema, edge functions, web app) lives in the NNA Planner
repository; this repository only ships reference copies of the pieces that are specific to the
desktop login (`planner/edge/auth-telegram-widget/index.ts`, `planner/site/login.html`,
`planner/test-widget-auth.mjs`).

The login window, the settings window's Planner tab, and the desktop `planner` widget all use the
local API routes described below, backed by `PlannerService` on the host.

## 1. Login: a Telegram Login Widget, not a Mini App

The desktop app has no Telegram Mini App context (no `initData`), so it uses the classic
[Telegram Login Widget](https://core.telegram.org/widgets/login) instead:

1. The app opens an in-app window on `https://planner.nna1618.com/desktop/login.html?port=<api port>`.
2. That page embeds the Telegram Login Widget script for the planner's bot. Telegram requires the
   widget's domain to be registered against the bot (`/setdomain` in BotFather): without that,
   the widget shows "Bot domain invalid".
3. On success, the page's `onTelegramAuth(user)` callback builds a query string from the fields
   Telegram returns (`id, first_name, last_name?, username?, photo_url?, auth_date, hash`) and
   redirects to `http://127.0.0.1:<port>/planner/callback?<query>`: the port comes from the login
   page's own URL.
4. The host's local `GET /planner/callback` handler is expected to forward that payload to the
   `auth-telegram-widget` edge function, store the resulting session, and close the login window.

Telegram has since moved this widget to a "legacy" status and introduced a newer OIDC-based login
through `oauth.telegram.org`; the legacy widget remains functional (no announced shutdown date) and
is what v0.1 uses. The OIDC widget opens a separate `oauth.telegram.org` popup, which a WebView2
host window would need to let open as a normal (non-embedded) window.

## 2. Server-side verification (`auth-telegram-widget`)

A dedicated Supabase edge function (reference copy:
`planner/edge/auth-telegram-widget/index.ts`) verifies the Telegram Login Widget payload using the
algorithm from Telegram's own documentation, which differs from Mini App `initData` verification:

1. Build `data_check_string`: every field except `hash`, sorted by key, joined as `key=value` with
   `\n`.
2. `secret = SHA256(bot_token)`: a **raw digest**, not an HMAC (unlike `initData` verification).
3. `expected = HMAC_SHA256(key=secret, msg=data_check_string)`, compared to the received `hash` in
   constant time.
4. `auth_date` must be no older than 24 hours.
5. On success, the function creates or reuses a profile for that Telegram user and mints a session
   in the same shape used elsewhere in NNA Planner: `{ access_token, refresh_token, expires_at,
   profile }`.
6. Any verification failure returns `401` with no details (details go to the function's own logs
   only).

This function only adds a new entry point; it does not modify any existing edge function, schema,
row-level-security policy, or plan/quota logic.

## 3. What the host does with the session

- The app stores `access_token`, `refresh_token`, `expires_at` and `profile` in
  `%LOCALAPPDATA%\NNA Wallpaper\planner-session.json`, encrypted with Windows DPAPI for the
  current user. This file is never committed to a repository and is excluded from logs.
- Shortly before `expires_at`, the host refreshes the token against Supabase's auth endpoint; a
  refresh failure surfaces as "please log in again".
- All calls to Supabase are made by the host process itself, not by any page: the local API never
  exposes the access token to a widget.

## 4. Local API surface

| Route | Method | Purpose |
|---|---|---|
| `/planner/status` | GET | current login state and profile |
| `/planner/today` | GET | today's and overdue tasks, upcoming meetings, habits, money spent today, a morning briefing (cached for 30 seconds) |
| `/planner/done` | POST | mark a task done / not done |
| `/planner/habit` | POST | mark a habit checked / unchecked for today |
| `/planner/capture` | POST | add an entry by text or voice, forwarded to the same capture endpoint the Telegram bot uses |
| `/planner/input` | POST | open the top-level text-entry window (InputWindow) at a given on-screen position |
| `/planner/login` | GET | open the login window |
| `/planner/logout` | POST | clear the local session |
| `/planner/callback` | GET | receives the redirect from the login page after a successful Telegram sign-in (no API token required: this is the one unauthenticated write path, and it only accepts the widget's signed fields, which the edge function verifies) |
| `/planner/undo` | POST | undo a whole capture batch (`?batch=<batch_id>`), used by the block's "cancel" button right after a capture |
| `/planner/test-delete` | POST | test-only: delete a captured test entry |
| `/planner/profile` | GET | settings page profile card: `{loggedIn, display_name, first_name, username, avatar, tier, timezone, lang, expires_at, sections}` |
| `/planner/avatar` | GET | the cached Telegram avatar JPEG, or 404 if none |
| `/planner/stats` | GET | `?range=day\|week`: small stat tiles for the settings page, see §6 below for the exact definition of every field |
| `/planner/capture-file` | POST | test-only (`TestEndpoints`): `{"path": "<wav file>"}`: runs a WAV file through the exact same capture pipeline a hotkey release uses (`PlannerService.CaptureVoiceAsync`), for headless probing without a real microphone or a real global hotkey |

"Today" and "overdue" are computed by the host using the user's own profile timezone, not UTC. A
background timer refreshes the access token about a minute before other work would need it.

## 5. Desktop block

Once signed in, the `planner` widget shows: a short morning briefing, tasks due today and
overdue, the next few meetings, habits (checked/unchecked today), and money spent today by
category. Clicking a task or habit toggles it. Clicking the text field calls `/planner/input`,
which opens a small top-level input window at that position (a wallpaper window cannot reliably
keep keyboard focus); a microphone button records voice with the page's own `MediaRecorder` and
sends it through `/planner/capture` the same way. Which of these sections are shown is controlled
from the settings window's Planner tab (`app.json`'s `planner.show` list).

## 6. Profile, avatar and stats (stage 6)

`auth-telegram-widget` v2 returns `first_name`, `username`, `photo_url` (an `https://t.me/...`
URL) in `profile`, alongside the original `display_name`, `lang`, `timezone`, `tier`, `sections`.
The host:

- Saves all of these fields in `planner-session.json` (`PlannerProfile`, see
  `src/NNA.Wallpaper.Host/Planner/PlannerModels.cs`).
- Downloads the avatar **once** to `<data>\cache\avatar.jpg` right after a successful login (and
  again after any refresh that happens to enrich a pre-v2 session: see below), refusing any URL
  that is not `https://t.me/...`. `GET /planner/avatar` serves that file, or 404 if there is none
  (a profile with no photo, or the one-time download hasn't run/succeeded yet: the settings page
  falls back to initials).
- A session saved before this stage's fields existed keeps working: plain GoTrue token refresh
  does not normally carry a `profile` node, so there is nothing to backfill from in practice today
 : `PlannerSession.ApplyRefresh` opportunistically merges one in *if* a future refresh response
  ever starts including it, without clobbering already-known fields on a partial echo. Until then,
  such a session just shows initials instead of an avatar, which is the documented fallback, not a
  bug.

`GET /planner/stats?range=day|week` fields, and exactly where each one comes from (PostgREST
queries against the same `entries` columns `PlannerClient` already uses elsewhere: **not**
`rpc/summary_for`, whose actual response shape this client has no verified reason to depend on):

| Field | Definition |
|---|---|
| `tasks.total` / `tasks.done` | `total` = open tasks due before the end of the range (includes overdue) **plus** tasks completed within the range; `done` = the completed-within-range count alone. |
| `habits.total` / `habits.done` | `total` = all open (ongoing) habits; `done` = habits with at least one `meta.checks` entry inside the range window. |
| `habits.streak` | The longest current run of consecutive days (ending today) present in any single habit's `meta.checks`: a real computation from that column, not an invented one. |
| `meetings` | Count of meetings starting between now and the end of the range. |
| `money.sum` | `income_minor - spent_minor` for entries within the range, in minor units (same unit `/planner/today`'s `money.spent_minor`/`income_minor` already use). |
| `money.currency` | **Not sourced from the backend**: always `"RUB"`. No query this client makes anywhere selects a currency column, so this is a fixed default, not real data; documented here rather than silently assumed. |

## 7. TASKS block settings and voice / push-to-talk (stage 6)

The settings window's Planner page (`settings/pages/planner.js`) adds, beyond the login
status/section toggles that already existed:

- **Profile card**: avatar (or initials) + name + `@username` + tier + session expiry, "log out",
  "open web planner" (`https://planner.nna1618.com`) and "open the bot"
  (`https://t.me/<planner.botUsername>`, default `NNAplanner_bot`: see `H:\secrets\tg-bot-nna-planner.md`).
- **Stats**: today/week stat tiles (§6).
- **TASKS block**: the existing section toggles, plus a max-tasks slider (`planner.maxTasks`,
  3-13, default 8) and a refresh-interval segmented control (`planner.refreshSec`, 30/60/120s,
  default 60).
- **Voice**: the microphone picker (same `GET`/`PUT /audio/capture-device` preference the
  wallpaper block's own mic button already reads: one choice drives both), a hotkey-capture field
  (`planner.hotkey`, default `Ctrl+Shift+Space`: click the button, then press a combination; Esc
  cancels), a "show recognized text before sending" toggle (`planner.showRecognizedText`), and a
  "test microphone" button (3s level meter via `getUserMedia` in the settings window itself:
  `SettingsWindow.xaml.cs` grants the Microphone permission request for that WebView2 instance).

**Push-to-talk** (`src/NNA.Wallpaper/Hotkeys.cs`): registers `planner.hotkey` with `RegisterHotKey`
on a hidden message-only window, `MOD_NOREPEAT` so one physical press fires `WM_HOTKEY` once.
`WM_HOTKEY` carries no key-up, so "still held" is polled with `GetAsyncKeyState` every 50ms (a
`DispatcherTimer`) until it releases. On press: `VoiceCaptureService` (host-side, NAudio
`WasapiCapture` on the configured/default microphone, 16kHz mono float resampled to 16-bit PCM,
capped at 60s) starts recording; on release it stops and hands the WAV to
`PlannerService.CaptureVoiceAsync` (via the `PlannerService.Current` static, the same
`EventsService.Current`-style registry pattern already used elsewhere), which sends it through the
existing `PlannerClient.CaptureAsync` and broadcasts **the exact same**
`{"type":"planner","event":"captured","ok":...,"entries":...,"batch_id":...,"error":...}` shape
`widgets/planner/widget.js`'s `onHostMessage`/`handleCaptureResult` already listen for from a
browser-side mic capture: so a hotkey capture shows up in the TASKS block's recognized-text/undo
panel identically. A `{"type":"voice-rec","on":true|false}` broadcast (`EventsService`) drives a
`rec` top bar module (`topbar/modules.js`): a Signal-red dot plus mm:ss: while recording; that
module is not in `bar.js`'s `DEFAULT_MODULES` (out of this stage's file scope), so add
`{"id":"rec","side":"right"}` to `topbar.modules` (or the top bar settings page's module editor)
to show it.

`--test-audio <wav path>` (an unrecognized CLI flag, so `CliArgs.Unknown` carries it through
unmodified) swaps the microphone for a fixed WAV file: one hotkey press sends it immediately, no
hold-to-record wait: for a manual check of the whole round trip without a working mic.

`GET /planner/status` gained a `hotkey: {registered, error}` field (present even logged out):
`registered=false` with an error string means `RegisterHotKey` failed (almost always another app
already holding the same combination) or the spec did not parse; both are logged, not thrown.
Hotkeys.cs is not wired to `WallpaperEngine`'s user-pause (that would need a pause-changed event
the engine does not currently raise: left out of scope for this stage): it stays registered for
the app's lifetime and is only released by `Hotkeys.Dispose()` on exit.

**What is stored locally, beyond the existing session file**: `<data>\cache\avatar.jpg` (deleted
on logout). Nothing else new touches disk.

**What this stage could not verify headless** (`tests/hotkey-probe.ps1` only proves the two things
a `--headless` run: no WPF, no real global hotkey, no microphone: actually can): a real
press-and-hold of the configured combination, actual `RegisterHotKey` conflict handling against
another running app, and the WASAPI capture path against a physical microphone. A human check on
the owner's live install after release is still needed for those.

## Security notes

- No planner secrets are ever stored in this repository; the bot token used for widget-signature
  verification lives only in the planner service's own environment.
- The Supabase URL and publishable key used by the desktop app are not secrets: they are meant to
  be public, with row-level security enforcing access control server-side.
- The planner session file lives only on the local machine, encrypted with DPAPI; nothing about
  the planner login is proxied through the local API's other (unauthenticated `GET`) routes.

---

# Интеграция с NNA Planner (RU)

NNA Wallpaper умеет показывать на рабочем столе блок, подключённый к
[NNA Planner](https://planner.nna1618.com): отдельному сервису задач, привычек и учёта денег с
Telegram-ботом и веб-приложением. Здесь описан вход и данные, которые использует блок на рабочем
столе. Источник истины по самому планировщику (схема БД, edge-функции, веб-приложение): отдельный
репозиторий NNA Planner; в этом репозитории только справочные копии части, специфичной для
десктопного входа (`planner/edge/auth-telegram-widget/index.ts`, `planner/site/login.html`,
`planner/test-widget-auth.mjs`).

Окно входа, вкладка «Планировщик» в настройках и виджет `planner` используют точки локального
API, описанные ниже; на хосте их обслуживает `PlannerService`.

## 1. Вход: Telegram Login Widget, а не Mini App

У десктопного приложения нет контекста Telegram Mini App (`initData`), поэтому используется
классический [Telegram Login Widget](https://core.telegram.org/widgets/login):

1. Приложение открывает окно на `https://planner.nna1618.com/desktop/login.html?port=<порт API>`.
2. Страница подключает скрипт виджета для бота планировщика. Telegram требует, чтобы домен виджета
   был привязан к боту (`/setdomain` в BotFather): без этого виджет покажет «Bot domain invalid».
3. При успехе `onTelegramAuth(user)` собирает query-строку из полей, которые вернул Telegram
   (`id, first_name, last_name?, username?, photo_url?, auth_date, hash`), и переходит на
   `http://127.0.0.1:<порт>/planner/callback?<query>`: порт берётся из адреса самой страницы.
4. Локальный обработчик `GET /planner/callback` должен передать эти данные в edge-функцию
   `auth-telegram-widget`, сохранить полученную сессию и закрыть окно входа.

Telegram перевёл этот виджет в статус «legacy» и ввёл новый вход через OIDC
(`oauth.telegram.org`); дата отключения legacy-виджета не объявлена, и v0.1 использует именно его.

## 2. Проверка на сервере (`auth-telegram-widget`)

Отдельная edge-функция Supabase (справочная копия:
`planner/edge/auth-telegram-widget/index.ts`) проверяет подпись Telegram Login Widget по
официальному алгоритму, который отличается от проверки `initData` Mini App: секрет: сырой
SHA-256 от токена бота (не HMAC), затем HMAC-SHA256 по строке отсортированных полей, сравнение
постоянного времени, `auth_date` не старше 24 часов. При успехе выдаётся сессия в том же формате,
что и у остальных способов входа в NNA Planner. Эта функция только добавляет новую точку входа и
не меняет существующие edge-функции, схему БД, RLS или тарифы.

## 3. Что делает хост с сессией

Хост хранит `access_token, refresh_token, expires_at, profile` в
`%LOCALAPPDATA%\NNA Wallpaper\planner-session.json`, зашифрованными DPAPI для текущего
пользователя; файл никогда не попадает в репозиторий и не пишется в лог. Обновление токена: за
несколько минут до истечения; при ошибке: статус «войти заново». Все обращения к Supabase делает
сам хост, ни одна страница не видит access-токен напрямую.

## 4. Точки локального API

`GET /planner/status`, `GET /planner/today` (кеш 30 секунд), `POST /planner/done`,
`POST /planner/habit`, `POST /planner/capture`, `POST /planner/input` (открывает окно ввода
InputWindow в заданной точке экрана), `GET /planner/login`, `POST /planner/logout`,
`GET /planner/callback` (принимает редирект от Telegram, без токена: проверку делает edge-функция
по подписи), `POST /planner/undo` (отменяет всю пачку записей одного захвата, `?batch=<batch_id>`,
для кнопки «ОТМЕНИТЬ» в блоке), `POST /planner/test-delete` (только для тестов). «Сегодня» и «просрочено» хост
считает по часовому поясу профиля пользователя, не по UTC; фоновый таймер обновляет токен доступа
заранее.

## 5. Блок на рабочем столе

После входа виджет `planner` показывает короткий утренний брифинг, задачи на сегодня и
просроченные, ближайшие встречи, привычки (отмечено ли сегодня) и траты за день по категориям.
Клик по задаче или привычке переключает её. Клик по полю ввода вызывает `/planner/input`, которое
открывает отдельное маленькое top-level окно в этой точке экрана (окно обоев не может надёжно
удерживать фокус клавиатуры); кнопка микрофона пишет голос через `MediaRecorder` самой страницы и
отправляет его через `/planner/capture`. Какие блоки показывать: настраивается во вкладке
«Планировщик» (список `planner.show` в `app.json`).

## 6. Профиль, аватар и статистика (этап 6)

`auth-telegram-widget` v2 отдаёт в `profile` поля `first_name`, `username`, `photo_url` (адрес вида
`https://t.me/...`) вместе с прежними `display_name`, `lang`, `timezone`, `tier`, `sections`. Хост
сохраняет всё это в `planner-session.json`, один раз скачивает аватар в `<data>\cache\avatar.jpg`
(только с `https://t.me/...`) сразу после входа; `GET /planner/avatar` отдаёт файл или 404 (нет
фото или скачивание ещё не прошло: страница настроек показывает инициалы). Сессия, сохранённая до
этого этапа, не ломается: обычный рефреш токена GoTrue сам по себе не присылает `profile`, поэтому
довыполнять там сейчас нечего: `PlannerSession.ApplyRefresh` подмешивает такие поля, если их
когда-нибудь начнёт отдавать эндпоинт рефреша, не затирая уже известные значения частичным ответом.
До тех пор такая сессия просто показывает инициалы вместо аватара: это ожидаемое поведение, не баг.

`GET /planner/stats?range=day|week`: поля и откуда они реально взяты (запросы PostgREST к тем же
колонкам `entries`, что `PlannerClient` уже использует в других местах, **не** `rpc/summary_for`,
чью форму ответа этот клиент никогда не проверял):

- `tasks.total`/`tasks.done`: total = открытые задачи со сроком до конца периода (включая
  просроченные) плюс завершённые в периоде; done = только завершённые в периоде.
- `habits.total`/`habits.done`: total = все открытые привычки; done = привычки, у которых в
  `meta.checks` есть отметка внутри окна периода.
- `habits.streak`: самая длинная текущая серия подряд идущих дней (заканчивая сегодня) по
  `meta.checks` любой одной привычки: реальный расчёт по этой колонке, не выдумка.
- `meetings`: число встреч, начинающихся от текущего момента до конца периода.
- `money.sum`: `income_minor - spent_minor` за период, в минорных единицах (как в `money.spent_minor`/`income_minor` у `/planner/today`).
- `money.currency`: **не из бэкенда**, всегда `"RUB"`: ни один запрос этого клиента не выбирает
  колонку валюты, поэтому это фиксированное значение по умолчанию, а не реальные данные.

## 7. Настройки блока TASKS и голос / push-to-talk (этап 6)

Страница «Планировщик» в настройках (`settings/pages/planner.js`) добавляет, помимо статуса входа
и переключателей секций: карточку профиля (аватар/инициалы, имя, `@username`, тариф, срок сессии,
«выйти», «открыть веб-планировщик», «открыть бота»: `planner.botUsername`, по умолчанию
`NNAplanner_bot`); статистику сегодня/неделя (§6); в блоке TASKS: слайдер числа задач
(`planner.maxTasks`, 3-13, по умолчанию 8) и интервал обновления (`planner.refreshSec`, 30/60/120с,
по умолчанию 60); группу «Голос»: выбор микрофона (тот же `GET`/`PUT /audio/capture-device`, что
и у микрофона блока на обоях: один выбор для обоих), поле захвата горячей клавиши
(`planner.hotkey`, по умолчанию `Ctrl+Shift+Space`; нажмите кнопку, затем комбинацию, Esc:
отмена), переключатель «показывать распознанный текст перед отправкой»
(`planner.showRecognizedText`) и кнопку «проверить микрофон» (3 с индикатора уровня через
`getUserMedia` прямо в окне настроек: разрешение на микрофон для этого WebView2 выдаёт
`SettingsWindow.xaml.cs`).

**Push-to-talk** (`src/NNA.Wallpaper/Hotkeys.cs`): регистрирует `planner.hotkey` через
`RegisterHotKey` на скрытом окне только для сообщений, `MOD_NOREPEAT`: одно физическое нажатие
даёт одно `WM_HOTKEY`. У `WM_HOTKEY` нет отпускания клавиши, поэтому «всё ещё зажато» опрашивается
`GetAsyncKeyState` каждые 50 мс (`DispatcherTimer`) до отпускания. По нажатию стартует
`VoiceCaptureService` (на хосте, NAudio `WasapiCapture` на выбранном или дефолтном микрофоне, 16
кГц моно float, ресемпл в 16-бит PCM, максимум 60 с); по отпусканию: стоп, и WAV уходит в
`PlannerService.CaptureVoiceAsync` (через статику `PlannerService.Current`, тот же приём, что и
`EventsService.Current`), который вызывает существующий `PlannerClient.CaptureAsync` и рассылает
**то же самое** сообщение `{"type":"planner","event":"captured","ok":...,"entries":...,"batch_id":...,"error":...}`,
которое уже слушает `widgets/planner/widget.js` (`onHostMessage`/`handleCaptureResult`): поэтому
голос с хоткея показывается в панели распознанного текста/отмены блока TASKS так же, как голос из
самого блока. Рассылка `{"type":"voice-rec","on":true|false}` (`EventsService`) включает индикатор
записи `rec` в верхней строке (`topbar/modules.js`: точка Signal-red плюс mm:ss); этот модуль не
входит в `DEFAULT_MODULES` в `bar.js` (вне области файлов этого этапа): чтобы он показывался,
добавьте `{"id":"rec","side":"right"}` в `topbar.modules` (или через редактор модулей верхней
строки в настройках).

`--test-audio <путь к wav>` (нераспознанный флаг CLI, поэтому `CliArgs.Unknown` проносит его как
есть) подменяет микрофон на фиксированный WAV-файл: одно нажатие сразу отправляет файл, без
ожидания удержания: для ручной проверки всей цепочки без рабочего микрофона.

`GET /planner/status` получил поле `hotkey: {registered, error}` (есть и без входа):
`registered=false` с текстом ошибки значит, что `RegisterHotKey` не удался (почти всегда: комбинация
уже занята другим приложением) или спецификация не разобралась; оба случая логируются, не бросают
исключение. Хоткей не связан с паузой `WallpaperEngine` (для этого нужно событие смены паузы,
которого движок сейчас не бросает: вне области этого этапа): он остаётся зарегистрированным всё
время работы приложения и снимается только `Hotkeys.Dispose()` при выходе.

**Что хранится локально, сверх уже существующего файла сессии**: `<data>\cache\avatar.jpg`
(удаляется при выходе). Больше ничего нового на диск не пишется.

**Что не проверено в headless-режиме** (`tests/hotkey-probe.ps1` доказывает только то, что вообще
можно проверить без WPF, без реальной глобальной горячей клавиши и без микрофона): реальное
нажатие-и-удержание настроенной комбинации, обработку конфликта `RegisterHotKey` с другим
запущенным приложением, и путь захвата WASAPI с реальным микрофоном. Это ещё предстоит проверить
человеку на живой установке у владельца после релиза.

## Замечания по безопасности

Секретов планировщика в этом репозитории нет; токен бота для проверки подписи виджета хранится
только в окружении самого сервиса планировщика. URL и publishable-ключ Supabase, которые использует
десктопное приложение, не являются секретом: доступ на сервере ограничивает row-level security.
Файл сессии планировщика существует только на локальной машине и зашифрован DPAPI.
