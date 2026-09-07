// Tiny i18n: a flat key -> {ru, en} dictionary and a t(key) factory. No framework, no build step.

const DICT = {
  appTitle: { ru: "Настройки — NNA Wallpaper", en: "Settings — NNA Wallpaper" },
  statusIdle: { ru: "Готово", en: "Ready" },
  statusSaving: { ru: "Сохранение…", en: "Saving…" },
  statusSaved: { ru: "Сохранено", en: "Saved" },
  statusError: { ru: "Ошибка: {0}", en: "Error: {0}" },
  statusLoadError: { ru: "Не удалось загрузить настройки", en: "Failed to load settings" },

  tab_layout: { ru: "Мониторы и раскладка", en: "Monitors and layout" },
  tab_widgets: { ru: "Блоки", en: "Blocks" },
  tab_appearance: { ru: "Внешний вид", en: "Appearance" },
  tab_planner: { ru: "Планировщик", en: "Planner" },
  tab_general: { ru: "Общие", en: "General" },

  // α — layout
  monitorsTitle: { ru: "Мониторы", en: "Monitors" },
  monitorEnabled: { ru: "Включён", en: "Enabled" },
  gridCols: { ru: "Колонки", en: "Columns" },
  gridRows: { ru: "Строки", en: "Rows" },
  gridGap: { ru: "Отступ", en: "Gap" },
  gridPad: { ru: "Поля", en: "Padding" },
  gridWeights: { ru: "Веса колонок", en: "Column weights" },
  resetDefault: { ru: "Сброс к умолчанию", en: "Reset to default" },
  undo: { ru: "Отменить", en: "Undo" },
  addBlock: { ru: "+ блок", en: "+ block" },
  deleteBlock: { ru: "Удалить", en: "Delete" },
  noFreeSlot: { ru: "Нет места для блока такого размера", en: "No room for a block of this size" },
  overlapWarning: { ru: "Блоки пересекаются — сохранение остановлено", en: "Blocks overlap — save blocked" },
  emptyWidgets: { ru: "Нет доступных виджетов", en: "No widgets available" },
  emptyMonitors: { ru: "Нет подключённых мониторов", en: "No monitors detected" },

  // β — widgets / blocks
  widgetsTitle: { ru: "Виджеты", en: "Widgets" },
  save: { ru: "Сохранить", en: "Save" },
  noWidgetSelected: { ru: "Выберите виджет слева", en: "Select a widget on the left" },
  launchTitle: { ru: "Запуск", en: "Launch" },
  eventsTitle: { ru: "События", en: "Events" },
  groupLabel: { ru: "Группа", en: "Group" },
  itemLabel: { ru: "Пункт", en: "Item" },
  addGroup: { ru: "+ группа", en: "+ group" },
  addItem: { ru: "+ пункт", en: "+ item" },
  remove: { ru: "убрать", en: "remove" },
  eventOneTime: { ru: "События с датой", en: "One-time events" },
  eventDaily: { ru: "Ежедневные", en: "Daily" },
  addEvent: { ru: "+ событие", en: "+ event" },

  // γ — appearance
  themePreset: { ru: "Пресет темы", en: "Theme preset" },
  palette: { ru: "Палитра", en: "Palette" },
  fonts: { ru: "Шрифты", en: "Fonts" },
  fontDisplay: { ru: "Заголовочный", en: "Display" },
  fontMono: { ru: "Моно", en: "Mono" },
  radius: { ru: "Скругление", en: "Radius" },
  blur: { ru: "Размытие", en: "Blur" },
  dim: { ru: "Затемнение", en: "Dim" },
  fpsCap: { ru: "Лимит FPS", en: "FPS cap" },
  pauseOnFullscreen: { ru: "Пауза под полноэкранными", en: "Pause under fullscreen apps" },

  // δ — planner
  plannerStatus: { ru: "Статус входа", en: "Login status" },
  plannerNotAvailable: { ru: "Планировщик появится после входа", en: "Planner unlocks after you log in" },
  loginTelegram: { ru: "Войти через Telegram", en: "Log in with Telegram" },
  logout: { ru: "Выйти", en: "Log out" },
  plannerShow: { ru: "Что показывать", en: "What to show" },
  show_tasks: { ru: "Задачи", en: "Tasks" },
  show_meetings: { ru: "Встречи", en: "Meetings" },
  show_habits: { ru: "Привычки", en: "Habits" },
  show_money: { ru: "Финансы", en: "Money" },
  show_briefing: { ru: "Брифинг", en: "Briefing" },

  // ε — general
  autostart: { ru: "Автозапуск", en: "Autostart" },
  language: { ru: "Язык", en: "Language" },
  apiPort: { ru: "Порт API", en: "API port" },
  apiPortHint: { ru: "Изменится после перезапуска приложения", en: "Takes effect after the app restarts" },
  updates: { ru: "Обновления", en: "Updates" },
  checkNow: { ru: "Проверить сейчас", en: "Check now" },
  channel: { ru: "Канал", en: "Channel" },
  importFolder: { ru: "Импорт из папки", en: "Import from folder" },
  importBtn: { ru: "Импортировать", en: "Import" },
  importHint: { ru: "Недоступно из приложения — используйте флаг командной строки --import <папка>", en: "Not available from the app — use the --import <folder> command-line flag" },
  openDataFolder: { ru: "Открыть папку данных", en: "Open data folder" },
  openLog: { ru: "Открыть лог", en: "Open log" },
  version: { ru: "Версия", en: "Version" },
  licenses: { ru: "Лицензии", en: "Licenses" },

  // forms.js generic
  formPath: { ru: "Путь", en: "Path" },
  formBrowse: { ru: "…", en: "…" },
  formAdd: { ru: "+", en: "+" },
  formRemove: { ru: "−", en: "−" },
};

export function makeT(lang) {
  const l = lang === "en" ? "en" : "ru";
  return function t(key, ...args) {
    const entry = DICT[key];
    let s = entry ? (entry[l] || entry.ru || key) : key;
    args.forEach((a, i) => { s = s.replace(`{${i}}`, a); });
    return s;
  };
}

export function detectLang(explicit) {
  if (explicit === "en" || explicit === "ru") return explicit;
  const nav = (typeof navigator !== "undefined" && navigator.language) || "ru";
  return nav.toLowerCase().startsWith("en") ? "en" : "ru";
}
