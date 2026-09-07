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
  tab_taskbar: { ru: "Панель задач", en: "Taskbar" },

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

  // γ — appearance (one fixed brand theme now — no palette editor or preset picker, see
  // ThemeSettings in AppSettings.cs; this tab only edits geometry/perf knobs)
  themeGeometry: { ru: "Геометрия и производительность", en: "Geometry & performance" },
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
  repoLink: { ru: "Репозиторий", en: "Repository" },
  releasesLink: { ru: "Релизы", en: "Releases" },

  // forms.js generic
  formPath: { ru: "Путь", en: "Path" },
  formBrowse: { ru: "…", en: "…" },
  formAdd: { ru: "+", en: "+" },
  formRemove: { ru: "−", en: "−" },

  // ζ — taskbar
  taskbarPresetSection: { ru: "Пресет", en: "Preset" },
  taskbarWindowsSection: { ru: "Панель Windows", en: "Windows taskbar" },
  taskbarTopbarSection: { ru: "Верхняя строка", en: "Top bar" },

  presetSelectLabel: { ru: "Пресет", en: "Preset" },
  presetApply: { ru: "Применить пресет", en: "Apply preset" },
  presetSaveAs: { ru: "Сохранить текущее как пресет", en: "Save current as preset" },
  presetNamePrompt: { ru: "Имя пресета", en: "Preset name" },
  presetExport: { ru: "Экспорт JSON", en: "Export JSON" },
  presetImport: { ru: "Импорт JSON", en: "Import JSON" },
  presetImportBtn: { ru: "Импортировать", en: "Import" },
  presetGroupBuiltin: { ru: "Встроенные", en: "Built-in" },
  presetGroupUser: { ru: "Свои", en: "User" },
  presetNone: { ru: "Без пресета", en: "No preset" },

  taskbarEnabled: { ru: "Панель Windows управляется приложением", en: "App manages the Windows taskbar" },
  taskbarSecondary: { ru: "Вторые мониторы", en: "Secondary monitors" },
  stateNormal: { ru: "Обычно", en: "Normal" },
  stateMaximized: { ru: "Окно развёрнуто", en: "Window maximized" },
  stateFullscreen: { ru: "Полноэкранное", en: "Fullscreen" },
  surfaceStyleLabel: { ru: "Стиль", en: "Style" },
  surfaceMode: { ru: "Режим", en: "Mode" },
  surfaceColor: { ru: "Цвет", en: "Color" },
  surfaceOpacity: { ru: "Непрозрачность", en: "Opacity" },
  mode_normal: { ru: "Обычный", en: "Normal" },
  mode_clear: { ru: "Прозрачный", en: "Clear" },
  mode_blur: { ru: "Размытие", en: "Blur" },
  mode_acrylic: { ru: "Акрил", en: "Acrylic" },
  mode_opaque: { ru: "Сплошной", en: "Opaque" },

  winToggles: { ru: "Переключатели Windows", en: "Windows toggles" },
  winToggle_centered: { ru: "По центру", en: "Centered" },
  winToggle_hideSearch: { ru: "Скрыть поиск", en: "Hide search" },
  winToggle_hideTaskView: { ru: "Скрыть представление задач", en: "Hide task view" },
  winToggle_hideWidgets: { ru: "Скрыть виджеты", en: "Hide widgets" },
  winToggle_hideClock: { ru: "Скрыть часы", en: "Hide clock" },
  winToggle_small: { ru: "Маленькие значки", en: "Small icons" },
  winToggle_transparency: { ru: "Прозрачность", en: "Transparency" },
  winToggle_oledTransparency: { ru: "OLED-прозрачность", en: "OLED transparency" },
  winToggle_autoHide: { ru: "Автоскрытие", en: "Auto-hide" },
  triStateUnset: { ru: "не трогать", en: "leave as is" },
  triStateOn: { ru: "вкл", en: "on" },
  triStateOff: { ru: "выкл", en: "off" },

  taskbarApply: { ru: "Применить", en: "Apply" },
  restartExplorer: { ru: "Перезапустить explorer", en: "Restart explorer" },
  confirmRestartExplorer: { ru: "Перезапустить explorer.exe сейчас?", en: "Restart explorer.exe now?" },
  resetWindows: { ru: "Вернуть настройки Windows", en: "Restore Windows settings" },
  taskbarUnavailable: { ru: "недоступно", en: "unavailable" },

  topbarEnabled: { ru: "Верхняя строка включена", en: "Top bar enabled" },
  topbarMonitors: { ru: "Мониторы", en: "Monitors" },
  monitorsAll: { ru: "Все", en: "All" },
  monitorsPrimary: { ru: "Основной", en: "Primary" },
  topbarHeight: { ru: "Высота", en: "Height" },
  topbarFontSize: { ru: "Размер шрифта", en: "Font size" },
  topbarAutoHide: { ru: "Автоскрытие", en: "Auto-hide" },
  topbarReserveSpace: { ru: "Сдвигать окна вниз", en: "Reserve space (push windows down)" },
  topbarModules: { ru: "Модули", en: "Modules" },
  addModule: { ru: "+ модуль", en: "+ module" },
  moduleId: { ru: "Модуль", en: "Module" },
  moduleSide: { ru: "Сторона", en: "Side" },
  sideLeft: { ru: "Слева", en: "Left" },
  sideCenter: { ru: "По центру", en: "Center" },
  sideRight: { ru: "Справа", en: "Right" },
  module_brand: { ru: "Бренд", en: "Brand" },
  module_date: { ru: "Дата", en: "Date" },
  module_clock: { ru: "Часы", en: "Clock" },
  module_weather: { ru: "Погода", en: "Weather" },
  module_stats: { ru: "Статистика", en: "Stats" },
  module_media: { ru: "Медиа", en: "Media" },
  module_planner: { ru: "Планировщик", en: "Planner" },
  module_spacer: { ru: "Разделитель", en: "Spacer" },
};

export function makeT(lang) {
  const l = lang === "en" ? "en" : "ru";
  const t = function t(key, ...args) {
    const entry = DICT[key];
    let s = entry ? (entry[l] || entry.ru || key) : key;
    args.forEach((a, i) => { s = s.replace(`{${i}}`, a); });
    return s;
  };
  // Exposed so form builders can resolve { ru, en } label/help objects from widget.json
  // (see settings/forms.js localize()) against the same language this t() uses.
  t.lang = l;
  return t;
}

export function detectLang(explicit) {
  if (explicit === "en" || explicit === "ru") return explicit;
  const nav = (typeof navigator !== "undefined" && navigator.language) || "ru";
  return nav.toLowerCase().startsWith("en") ? "en" : "ru";
}
