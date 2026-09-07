/**
 * Вход в NNA Wallpaper (десктоп-приложение) через Telegram Login Widget.
 *
 * У десктопа нет Mini App и initData — вместо этого используется классический
 * Telegram Login Widget: страница на витрине (site/desktop/login.html) отдаёт
 * подписанные Telegram данные пользователя, эта функция проверяет подпись и
 * выдаёт ту же сессию Supabase, что и auth-telegram.
 *
 * Алгоритм проверки виджета отличается от initData (_shared/telegram.ts):
 * секретный ключ здесь — СЫРОЙ SHA-256 от токена бота, а не HMAC("WebAppData", …).
 * Поэтому проверка написана отдельно, здесь, а не переиспользует verifyInitData.
 *
 * verify_jwt = false: JWT на входе ещё нет — его и выдаёт эта функция.
 * Защита — подпись Telegram: подделать её без токена бота нельзя.
 */
import { json, fail, preflight } from '../_shared/http.ts';
import { admin } from '../_shared/supa.ts';
import { ensureProfile, mintSession } from '../_shared/profiles.ts';
import { BOT_TOKEN } from '../_shared/telegram.ts';
import type { TgUser } from '../_shared/telegram.ts';

const enc = new TextEncoder();

const hex = (buf: ArrayBuffer) =>
  Array.from(new Uint8Array(buf)).map((b) => b.toString(16).padStart(2, '0')).join('');

/** Сравнение постоянного времени: без ранних выходов, чтобы подпись нельзя было подобрать по таймингу. */
function timingSafeEqual(a: string, b: string): boolean {
  if (a.length !== b.length) return false;
  let diff = 0;
  for (let i = 0; i < a.length; i++) diff |= a.charCodeAt(i) ^ b.charCodeAt(i);
  return diff === 0;
}

/** Поля, которые Telegram Login Widget может прислать, кроме hash — порядок неважен, сортируем сами. */
const WIDGET_FIELDS = ['auth_date', 'first_name', 'id', 'last_name', 'photo_url', 'username'] as const;

const MAX_AGE_SEC = 24 * 60 * 60;
const FUTURE_SKEW_SEC = 5 * 60;

type WidgetCheck = { ok: true; user: TgUser } | { ok: false };

/**
 * Проверка подписи Telegram Login Widget по официальному алгоритму:
 *   secret   = SHA256(bot_token)                      — сырой digest, не HMAC
 *   expected = HMAC_SHA256(key=secret, msg=data_check_string)
 * где data_check_string — пары "k=v" по присутствующим полям (кроме hash),
 * отсортированные по ключу, через "\n".
 */
async function verifyWidgetLogin(body: unknown): Promise<WidgetCheck> {
  const reject = (reason: string): { ok: false } => {
    // Наружу — только «не пущу», причина остаётся в логах функции без значений полей.
    console.warn('auth-telegram-widget rejected:', reason);
    return { ok: false };
  };

  if (!BOT_TOKEN) return reject('bot token not configured');
  if (!body || typeof body !== 'object' || Array.isArray(body)) return reject('bad payload shape');
  const p = body as Record<string, unknown>;

  const id = Number(p.id);
  if (!Number.isInteger(id) || id <= 0) return reject('bad id');

  const hash = String(p.hash ?? '').toLowerCase();
  if (!/^[0-9a-f]{64}$/.test(hash)) return reject('bad hash format');

  const authDate = Number(p.auth_date);
  if (!Number.isFinite(authDate) || authDate <= 0) return reject('bad auth_date');

  const now = Math.floor(Date.now() / 1000);
  if (authDate > now + FUTURE_SKEW_SEC) return reject('auth_date in future');
  if (now - authDate > MAX_AGE_SEC) return reject('auth_date expired');

  const fields: Record<string, string> = {};
  for (const key of WIDGET_FIELDS) {
    const v = p[key];
    if (v === undefined || v === null || v === '') continue;
    fields[key] = String(v);
  }
  const dataCheckString = Object.keys(fields).sort().map((k) => `${k}=${fields[k]}`).join('\n');

  const secret = await crypto.subtle.digest('SHA-256', enc.encode(BOT_TOKEN));
  const key = await crypto.subtle.importKey(
    'raw',
    secret,
    { name: 'HMAC', hash: 'SHA-256' },
    false,
    ['sign'],
  );
  const expected = hex(await crypto.subtle.sign('HMAC', key, enc.encode(dataCheckString)));

  if (!timingSafeEqual(expected, hash)) return reject('bad signature');

  const user: TgUser = {
    id,
    first_name: typeof p.first_name === 'string' ? p.first_name : undefined,
    last_name: typeof p.last_name === 'string' ? p.last_name : undefined,
    username: typeof p.username === 'string' ? p.username : undefined,
  };
  return { ok: true, user };
}

Deno.serve(async (req) => {
  const pre = preflight(req);
  if (pre) return pre;
  if (req.method !== 'POST') return fail(req, 405, 'method not allowed');

  let body: unknown;
  try {
    body = await req.json();
  } catch {
    return fail(req, 400, 'bad json');
  }

  try {
    const check = await verifyWidgetLogin(body);
    if (!check.ok) return fail(req, 401, 'invalid login');

    const db = admin();
    const profile = await ensureProfile(db, check.user);
    const session = await mintSession(db, check.user.id, profile.auth_user_id!);

    return json(req, {
      ...session,
      profile: {
        id: profile.id,
        display_name: profile.display_name,
        lang: profile.lang,
        timezone: profile.timezone,
        tier: profile.tier,
        sections: profile.sections,
        onboarding_step: profile.onboarding_step,
      },
    });
  } catch (e) {
    console.error('auth-telegram-widget failed:', e instanceof Error ? e.message : e);
    return fail(req, 500, 'auth failed');
  }
});
