# Planner-side reference copies

Source of truth is the NNA Planner repository. Copies here document the desktop login contract:

- `edge/auth-telegram-widget/index.ts` — Supabase edge function that verifies a Telegram Login Widget payload and mints the same session as `auth-telegram`.
- `site/login.html` — the login page served at `https://planner.nna1618.com/desktop/login.html?port=<api port>`; on success it redirects to `http://127.0.0.1:<port>/planner/callback?...`.
- `test-widget-auth.mjs` — signature test (reads the bot token from a local secrets file that is not part of this repo).
