// settings/app.js — thin entry point. All shell/page logic lives in settings/shell.js and
// settings/pages/*.js now (see docs/SETTINGS.md); this file only exists so index.html's
// <script type="module" src="app.js"> keeps working for anything that still references it by
// name (the WPF SettingsWindow loads /settings/?token=&tab=&lang=, not this file directly, but
// external tooling/bookmarks may).
import "./shell.js";
