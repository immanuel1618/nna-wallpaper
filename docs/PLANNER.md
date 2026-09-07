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
   widget's domain to be registered against the bot (`/setdomain` in BotFather) — without that,
   the widget shows "Bot domain invalid".
3. On success, the page's `onTelegramAuth(user)` callback builds a query string from the fields
   Telegram returns (`id, first_name, last_name?, username?, photo_url?, auth_date, hash`) and
   redirects to `http://127.0.0.1:<port>/planner/callback?<query>` — the port comes from the login
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
2. `secret = SHA256(bot_token)` — a **raw digest**, not an HMAC (unlike `initData` verification).
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
- All calls to Supabase are made by the host process itself, not by any page — the local API never
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
| `/planner/callback` | GET | receives the redirect from the login page after a successful Telegram sign-in (no API token required — this is the one unauthenticated write path, and it only accepts the widget's signed fields, which the edge function verifies) |
| `/planner/undo` | POST | undo a whole capture batch (`?batch=<batch_id>`), used by the block's "cancel" button right after a capture |
| `/planner/test-delete` | POST | test-only: delete a captured test entry |

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

## Security notes

- No planner secrets are ever stored in this repository; the bot token used for widget-signature
  verification lives only in the planner service's own environment.
- The Supabase URL and publishable key used by the desktop app are not secrets — they are meant to
  be public, with row-level security enforcing access control server-side.
- The planner session file lives only on the local machine, encrypted with DPAPI; nothing about
  the planner login is proxied through the local API's other (unauthenticated `GET`) routes.

---

# Интеграция с NNA Planner (RU)

NNA Wallpaper умеет показывать на рабочем столе блок, подключённый к
[NNA Planner](https://planner.nna1618.com) — отдельному сервису задач, привычек и учёта денег с
Telegram-ботом и веб-приложением. Здесь описан вход и данные, которые использует блок на рабочем
столе. Источник истины по самому планировщику (схема БД, edge-функции, веб-приложение) — отдельный
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
   был привязан к боту (`/setdomain` в BotFather) — без этого виджет покажет «Bot domain invalid».
3. При успехе `onTelegramAuth(user)` собирает query-строку из полей, которые вернул Telegram
   (`id, first_name, last_name?, username?, photo_url?, auth_date, hash`), и переходит на
   `http://127.0.0.1:<порт>/planner/callback?<query>` — порт берётся из адреса самой страницы.
4. Локальный обработчик `GET /planner/callback` должен передать эти данные в edge-функцию
   `auth-telegram-widget`, сохранить полученную сессию и закрыть окно входа.

Telegram перевёл этот виджет в статус «legacy» и ввёл новый вход через OIDC
(`oauth.telegram.org`); дата отключения legacy-виджета не объявлена, и v0.1 использует именно его.

## 2. Проверка на сервере (`auth-telegram-widget`)

Отдельная edge-функция Supabase (справочная копия —
`planner/edge/auth-telegram-widget/index.ts`) проверяет подпись Telegram Login Widget по
официальному алгоритму, который отличается от проверки `initData` Mini App: секрет — сырой
SHA-256 от токена бота (не HMAC), затем HMAC-SHA256 по строке отсортированных полей, сравнение
постоянного времени, `auth_date` не старше 24 часов. При успехе выдаётся сессия в том же формате,
что и у остальных способов входа в NNA Planner. Эта функция только добавляет новую точку входа и
не меняет существующие edge-функции, схему БД, RLS или тарифы.

## 3. Что делает хост с сессией

Хост хранит `access_token, refresh_token, expires_at, profile` в
`%LOCALAPPDATA%\NNA Wallpaper\planner-session.json`, зашифрованными DPAPI для текущего
пользователя; файл никогда не попадает в репозиторий и не пишется в лог. Обновление токена — за
несколько минут до истечения; при ошибке — статус «войти заново». Все обращения к Supabase делает
сам хост, ни одна страница не видит access-токен напрямую.

## 4. Точки локального API

`GET /planner/status`, `GET /planner/today` (кеш 30 секунд), `POST /planner/done`,
`POST /planner/habit`, `POST /planner/capture`, `POST /planner/input` (открывает окно ввода
InputWindow в заданной точке экрана), `GET /planner/login`, `POST /planner/logout`,
`GET /planner/callback` (принимает редирект от Telegram, без токена — проверку делает edge-функция
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
отправляет его через `/planner/capture`. Какие блоки показывать — настраивается во вкладке
«Планировщик» (список `planner.show` в `app.json`).

## Замечания по безопасности

Секретов планировщика в этом репозитории нет; токен бота для проверки подписи виджета хранится
только в окружении самого сервиса планировщика. URL и publishable-ключ Supabase, которые использует
десктопное приложение, не являются секретом — доступ на сервере ограничивает row-level security.
Файл сессии планировщика существует только на локальной машине и зашифрован DPAPI.
