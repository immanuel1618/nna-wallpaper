// settings/api.js — HTTP client, save-status pill and error toast shared by every settings page.
// Pulled out of the old monolithic app.js so shell.js and settings/pages/*.js don't each
// reimplement fetch/token handling (see docs/SETTINGS.md "Как добавить страницу").

import { SAVED_MARKERS } from "./i18n.js";

const params = new URLSearchParams(location.search);
export const TOKEN = params.get("token") || "";

/** Thin fetch wrapper: JSON body in, JSON (or null) out, throws Error{status,detail} on !ok. */
export async function api(method, path, body) {
  const opts = { method, headers: {} };
  if (body !== undefined) {
    opts.headers["Content-Type"] = "application/json; charset=utf-8";
    opts.body = JSON.stringify(body);
  }
  if (method !== "GET") opts.headers["X-Token"] = TOKEN;
  const res = await fetch(path, opts);
  let data = null;
  try { data = await res.json(); } catch { /* no body */ }
  if (!res.ok) {
    const err = new Error((data && (data.error || data.message)) || res.statusText || ("HTTP " + res.status));
    err.status = res.status;
    err.detail = data && data.detail;
    throw err;
  }
  return data;
}

export const put = (body) => api("PUT", "/config", body);

/** Debounced save: call the returned function on every input; `fn` runs once, `delay`ms after
 * the last call in a burst (used by sliders/number fields so we don't PUT on every tick). */
export function makeScheduler(fn, delay = 300) {
  let timer = null;
  return () => {
    clearTimeout(timer);
    timer = setTimeout(fn, delay);
  };
}

let toastTimer = null;
function toastBox() {
  let box = document.getElementById("toast");
  if (!box) {
    box = document.createElement("div");
    box.id = "toast";
    box.className = "toast";
    document.body.append(box);
  }
  return box;
}

/**
 * Sets the top-right status pill (#status text inside #status-wrap, written by shell.js) and,
 * on error, also raises a transient toast so a failure is noticed even off in the corner.
 * Same two-argument contract the pre-existing layout-editor.js/taskbar-tab.js already call:
 * `setStatus(text, isError)`. "Saved" (in either language) also lights the pill green — matched
 * against SAVED_MARKERS rather than a passed-in flag so every existing `onStatus(t("statusSaved"),
 * false)` call site across the app keeps working unchanged.
 */
export function setStatus(text, isError) {
  const textEl = document.getElementById("status");
  const wrap = document.getElementById("status-wrap");
  if (textEl) textEl.textContent = text;
  if (wrap) {
    wrap.classList.toggle("err", !!isError);
    wrap.classList.toggle("ok", !isError && SAVED_MARKERS.has(text));
  }
  if (isError) {
    const t = toastBox();
    t.textContent = text;
    t.classList.add("show");
    clearTimeout(toastTimer);
    toastTimer = setTimeout(() => t.classList.remove("show"), 4000);
  }
}
