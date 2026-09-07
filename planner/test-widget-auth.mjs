#!/usr/bin/env node
/**
 * Локальный тест auth-telegram-widget (вход в NNA Wallpaper с десктопа).
 *
 * Подписывает payload ровно как Telegram Login Widget (node:crypto, без зависимостей)
 * и гоняет позитивные/негативные случаи против функции. Токен бота и tg-id владельца
 * читаются из H:\secrets\ только в памяти процесса — значения нигде не печатаются,
 * ни при успехе, ни при ошибке, ни в этом файле.
 *
 * Запуск:
 *   node scripts/test-widget-auth.mjs                — полный прогон на живой функции
 *   node scripts/test-widget-auth.mjs --dry-run       — только подписать и проверить локально, без сети
 *   node scripts/test-widget-auth.mjs --url <base>    — другой базовый url functions/v1
 */
import { readFileSync } from 'node:fs';
import { createHash, createHmac } from 'node:crypto';

const args = process.argv.slice(2);
const dryRun = args.includes('--dry-run');
const urlIdx = args.indexOf('--url');
const BASE_URL = urlIdx !== -1 && args[urlIdx + 1]
  ? args[urlIdx + 1]
  : 'https://tvpmtjidonohpgwhyssm.supabase.co/functions/v1';

// Публичный ключ витрины (web/src/lib/config.ts) — не секрет, доступ к данным закрывает RLS.
const APIKEY = 'sb_publishable_HYka0T2lxZndnMainWUNXg_YPWy2dy8';

const SECRETS_DIR = 'H:\\secrets';

function readBotToken() {
  let text;
  try {
    text = readFileSync(`${SECRETS_DIR}\\tg-bot-nna-planner.md`, 'utf8');
  } catch {
    return null;
  }
  const m = text.match(/\|\s*Token\s*\|\s*`([^`]+)`/);
  return m ? m[1] : null;
}

function readOwnerId() {
  let text;
  try {
    text = readFileSync(`${SECRETS_DIR}\\tg-ids.md`, 'utf8');
  } catch {
    return null;
  }
  const line = text.split('\n').find((l) => l.includes('Костя'));
  if (!line) return null;
  const m = line.match(/`(\d+)`/);
  return m ? Number(m[1]) : null;
}

// ── та же логика подписи/проверки, что в supabase/functions/auth-telegram-widget/index.ts ──

const WIDGET_FIELDS = ['auth_date', 'first_name', 'id', 'last_name', 'photo_url', 'username'];

function dataCheckString(payload) {
  const fields = {};
  for (const key of WIDGET_FIELDS) {
    const v = payload[key];
    if (v === undefined || v === null || v === '') continue;
    fields[key] = String(v);
  }
  return Object.keys(fields).sort().map((k) => `${k}=${fields[k]}`).join('\n');
}

function signPayload(botToken, payload) {
  const secret = createHash('sha256').update(botToken).digest();
  return createHmac('sha256', secret).update(dataCheckString(payload)).digest('hex');
}

function timingSafeEqualStr(a, b) {
  if (a.length !== b.length) return false;
  let diff = 0;
  for (let i = 0; i < a.length; i++) diff |= a.charCodeAt(i) ^ b.charCodeAt(i);
  return diff === 0;
}

/** Зеркало серверной проверки — используется только для --dry-run, сети не трогает. */
function verifyLocally(botToken, payload) {
  const id = Number(payload.id);
  if (!Number.isInteger(id) || id <= 0) return { ok: false, reason: 'bad id' };
  const hash = String(payload.hash ?? '').toLowerCase();
  if (!/^[0-9a-f]{64}$/.test(hash)) return { ok: false, reason: 'bad hash format' };
  const authDate = Number(payload.auth_date);
  if (!Number.isFinite(authDate) || authDate <= 0) return { ok: false, reason: 'bad auth_date' };
  const now = Math.floor(Date.now() / 1000);
  if (authDate > now + 5 * 60) return { ok: false, reason: 'auth_date in future' };
  if (now - authDate > 24 * 60 * 60) return { ok: false, reason: 'auth_date expired' };
  const expected = signPayload(botToken, payload);
  if (!timingSafeEqualStr(expected, hash)) return { ok: false, reason: 'bad signature' };
  return { ok: true };
}

function buildSignedPayload(botToken, ownerId, authDate = Math.floor(Date.now() / 1000)) {
  const base = { id: ownerId, first_name: 'Konstantin', username: 'immanuel_1618', auth_date: authDate };
  const hash = signPayload(botToken, base);
  return { ...base, hash };
}

async function main() {
  const botToken = readBotToken();
  if (!botToken) { console.error('token not found'); process.exit(2); return; }
  const ownerId = readOwnerId();
  if (!ownerId) { console.error('id not found'); process.exit(2); return; }

  const payload = buildSignedPayload(botToken, ownerId);

  if (dryRun) {
    const check = verifyLocally(botToken, payload);
    if (!check.ok) {
      console.error('dry-run failed: signature does not verify locally');
      process.exit(1);
      return;
    }
    console.log('dry-run ok');
    process.exit(0);
    return;
  }

  let passed = 0;
  let failed = 0;
  const record = (n, ok, expectMsg, gotMsg) => {
    if (ok) { console.log(`PASS ${n}: ${expectMsg}`); passed++; }
    else { console.log(`FAIL ${n}: ожидали ${expectMsg}, получили ${gotMsg}`); failed++; }
  };

  const fnUrl = (name) => `${BASE_URL}/${name}`;
  const post = async (name, body) => {
    const res = await fetch(fnUrl(name), {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', apikey: APIKEY },
      body: typeof body === 'string' ? body : JSON.stringify(body),
    });
    let json = null;
    try { json = await res.json(); } catch { /* не JSON — оставим null */ }
    return { status: res.status, json };
  };

  // 1. валидная подпись → 200, есть access_token/refresh_token/profile
  {
    const r = await post('auth-telegram-widget', payload);
    const ok = r.status === 200
      && typeof r.json?.access_token === 'string'
      && typeof r.json?.refresh_token === 'string'
      && !!r.json?.profile?.id;
    if (ok) {
      console.log(`  access_token length: ${r.json.access_token.length}, profile.id: ${r.json.profile.id}`);
    }
    record(1, ok, '200 c access_token/refresh_token/profile', `${r.status} ${JSON.stringify(r.json)}`);
  }

  // 2. испорченный hash (последний символ заменён) → 401
  {
    const lastChar = payload.hash.at(-1);
    const flipped = lastChar === '0' ? '1' : '0';
    const bad = { ...payload, hash: payload.hash.slice(0, -1) + flipped };
    const r = await post('auth-telegram-widget', bad);
    record(2, r.status === 401, '401', `${r.status}`);
  }

  // 3. auth_date на 2 дня назад с корректной (пересчитанной) подписью → 401
  {
    const oldAuthDate = Math.floor(Date.now() / 1000) - 2 * 24 * 60 * 60;
    const old = buildSignedPayload(botToken, ownerId, oldAuthDate);
    const r = await post('auth-telegram-widget', old);
    record(3, r.status === 401, '401', `${r.status}`);
  }

  // 4. GET → 405
  {
    const res = await fetch(fnUrl('auth-telegram-widget'), {
      method: 'GET',
      headers: { apikey: APIKEY },
    });
    record(4, res.status === 405, '405', `${res.status}`);
  }

  // 5. POST с телом "not json" → 400
  {
    const r = await post('auth-telegram-widget', 'not json');
    record(5, r.status === 400, '400', `${r.status}`);
  }

  // 6. регрессия: auth-telegram с пустым initData по-прежнему 401 — функцию не тронули
  {
    const r = await post('auth-telegram', { initData: '' });
    record(6, r.status === 401, '401', `${r.status}`);
  }

  console.log(`RESULT: ${passed} passed, ${failed} failed`);
  process.exit(failed === 0 ? 0 : 1);
}

main().catch((e) => {
  console.error('test failed:', e instanceof Error ? e.message : e);
  process.exit(1);
});
