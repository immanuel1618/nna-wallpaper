/* NNA1618 — служебные подписи внутри виджетов (не заголовки блоков — те приходят из
   title:{ru,en} манифестов, см. wallpaper/layout.js). window.NNA.t(key, fallback) возвращает
   значение текущего языка (window.NNA_CONFIG.language, по умолчанию ru) — обычно строку, но для
   days/months/weatherCodes это массив или объект-таблица, который вызывающий код использует как
   есть (индексирует по номеру дня/месяца или по коду погоды). Регистр — обычный, капс делает CSS
   (.nna-mono/.nna-btn/.nna-corner/.nna-label/.nna-toast — text-transform: uppercase), строки
   здесь пишем как обычный текст. */
(function (root, factory) {
  var api = factory();
  if (typeof module === 'object' && module.exports) {
    module.exports = api;
  }
  if (root) {
    root.NNA = root.NNA || {};
    root.NNA.t = api.t;
  }
})(typeof window !== 'undefined' ? window : (typeof globalThis !== 'undefined' ? globalThis : null), function () {
  'use strict';

  var DAYS_RU = ['вс', 'пн', 'вт', 'ср', 'чт', 'пт', 'сб'];
  var DAYS_EN = ['sun', 'mon', 'tue', 'wed', 'thu', 'fri', 'sat'];
  var MONTHS_RU = ['янв', 'фев', 'мар', 'апр', 'май', 'июн', 'июл', 'авг', 'сен', 'окт', 'ноя', 'дек'];
  var MONTHS_EN = ['jan', 'feb', 'mar', 'apr', 'may', 'jun', 'jul', 'aug', 'sep', 'oct', 'nov', 'dec'];

  // Open-Meteo WMO weathercode -> текст. Тот же список кодов, что в helper.py / WeatherService.cs
  // (см. widgets/weather/widget.js), но переводится на клиенте по числовому коду вместо текста,
  // которое всегда отдаёт помощник по-английски.
  var WEATHER_CODES_RU = {
    0: 'ясно', 1: 'малооблачно', 2: 'переменная облачность', 3: 'пасмурно',
    45: 'туман', 48: 'изморозь',
    51: 'лёгкая морось', 53: 'морось', 55: 'сильная морось',
    56: 'морозящая морось', 57: 'морозящая морось',
    61: 'небольшой дождь', 63: 'дождь', 65: 'сильный дождь',
    66: 'ледяной дождь', 67: 'ледяной дождь',
    71: 'небольшой снег', 73: 'снег', 75: 'сильный снег', 77: 'снежная крупа',
    80: 'ливень', 81: 'ливень', 82: 'сильный ливень',
    85: 'снегопад', 86: 'снегопад',
    95: 'гроза', 96: 'гроза с градом', 99: 'гроза с градом'
  };
  var WEATHER_CODES_EN = {
    0: 'clear', 1: 'mainly clear', 2: 'partly cloudy', 3: 'overcast',
    45: 'fog', 48: 'rime fog',
    51: 'light drizzle', 53: 'drizzle', 55: 'heavy drizzle',
    56: 'freezing drizzle', 57: 'freezing drizzle',
    61: 'light rain', 63: 'rain', 65: 'heavy rain',
    66: 'freezing rain', 67: 'freezing rain',
    71: 'light snow', 73: 'snow', 75: 'heavy snow', 77: 'snow grains',
    80: 'showers', 81: 'showers', 82: 'heavy showers',
    85: 'snow showers', 86: 'snow showers',
    95: 'thunderstorm', 96: 'hail storm', 99: 'hail storm'
  };

  var DICT = {
    ru: {
      edit: 'Правка',
      idle: 'Ничего не играет',
      tasksSoon: 'NNA PLANNER',
      tasksSub: 'Скоро',
      live: 'Звучит',
      silent: 'Тихо',
      netDown: 'Приём',
      netUp: 'Отдача',
      free: 'свободно',
      vol: 'дисков', vol_1: 'диск', vol_2: 'диска', vol_5: 'дисков',
      threads: 'потоков',
      failed: 'Ошибка',
      helperOffline: 'Помощник офлайн',
      noData: 'Нет данных',
      noGpuData: 'Нет данных GPU',
      other: 'Другое',
      eventsEmpty: 'Нет событий · нажмите Правка',
      launchEmpty: 'Добавьте приложения на странице Блоки',

      days: DAYS_RU,
      months: MONTHS_RU,
      weatherCodes: WEATHER_CODES_RU,

      // focus/widget.js
      focusModelLoadFailed: 'не удалось загрузить модель таймера',
      focusPresetsBtn: 'пресеты',
      focusStatsBtn: 'статистика',
      focusSetBtn: 'настроить',
      focusResetBtn: 'сбросить',
      focusStartHint: 'нажмите, чтобы начать',
      focusPauseHint: 'нажмите, чтобы приостановить',
      focusRestartHint: 'нажмите, чтобы начать заново',
      focusWork: 'фокус',
      focusChill: 'отдых',
      focusDoneState: 'завершено',
      focusReady: 'готово',
      focusPaused: 'пауза',
      focusSessionDone: 'сессия фокуса завершена',
      focusBreak: 'фокус завершён · отдых',
      focusResume: 'отдых закончен · фокус',
      focusReset: 'таймер сброшен',
      focusDoneBtn: 'готово',
      focusBack: 'назад',
      focusSettingsTitle: 'настройки · работа / отдых / циклы',
      focusHelpWork: 'длительность рабочей фазы',
      focusHelpChill: 'отдых между рабочими фазами',
      focusHelpCycles: 'рабочих фаз до завершения',
      focusStatsTitle: 'статистика · последние 7 дней',
      focusStatSessions: 'сессий сегодня',
      focusStatMinutes: 'минут сегодня',
      focusStatWeek: 'минут в неделю',
      focusPresetsTitle: 'пресеты · работа / отдых x циклы',
      focusFieldWork: 'работа',
      focusFieldChill: 'отдых',
      focusFieldCycles: 'циклы',
      focusCustom: 'свой',

      // events/widget.js
      evEvent: 'событие',
      evDaily: 'ежедневно',
      evDay: 'дней', evDay_1: 'день', evDay_2: 'дня', evDay_5: 'дней',
      evToday: 'сегодня',
      evHoursLeft: 'часов осталось',
      evHM: 'ч : м',
      evMS: 'м : с',
      eventsOpening: 'открываю events.json',

      // weather/widget.js
      weatherFeels: 'ощущается',
      weatherWind: 'ветер',
      weatherHum: 'влажность',
      weatherMs: 'м/с',

      // graph/widget.js
      graphLoading: 'загрузка',
      graphNodesUnit: 'узлов',
      graphNotFound: 'не найдено',
      graphOpen: 'открыто',

      // player/widget.js
      playerIdleState: 'ожидание',
      playerBrowser: 'браузер',
      playerMedia: 'медиа',

      // launch/widget.js
      launchGroup: 'группа',
      launchLaunched: 'запущено',
      launchOpening: 'открываю launch.json',

      // photos/widget.js
      photosEmpty: 'нет фото · укажите папку в настройках'
    },
    en: {
      edit: 'Edit',
      idle: 'Nothing playing',
      tasksSoon: 'NNA PLANNER',
      tasksSub: 'Soon',
      live: 'Live',
      silent: 'Silent',
      netDown: 'Down',
      netUp: 'Up',
      free: 'free',
      vol: 'vol', vol_1: 'vol', vol_2: 'vol', vol_5: 'vol',
      threads: 'threads',
      failed: 'Failed',
      helperOffline: 'Helper offline',
      noData: 'No data',
      noGpuData: 'No GPU data',
      other: 'Other',
      eventsEmpty: 'No events · press Edit',
      launchEmpty: 'Add apps on the Blocks page',

      days: DAYS_EN,
      months: MONTHS_EN,
      weatherCodes: WEATHER_CODES_EN,

      // focus/widget.js
      focusModelLoadFailed: 'focus model load failed',
      focusPresetsBtn: 'preset',
      focusStatsBtn: 'stats',
      focusSetBtn: 'set',
      focusResetBtn: 'reset',
      focusStartHint: 'click to start',
      focusPauseHint: 'click to pause',
      focusRestartHint: 'click to restart',
      focusWork: 'focus',
      focusChill: 'chill',
      focusDoneState: 'done',
      focusReady: 'ready',
      focusPaused: 'paused',
      focusSessionDone: 'focus session done',
      focusBreak: 'focus done · chill',
      focusResume: 'chill over · focus',
      focusReset: 'focus reset',
      focusDoneBtn: 'done',
      focusBack: 'back',
      focusSettingsTitle: 'settings · work / chill / cycles',
      focusHelpWork: 'length of the work phase',
      focusHelpChill: 'chill between work phases',
      focusHelpCycles: 'work phases until done',
      focusStatsTitle: 'stats · last 7 days',
      focusStatSessions: 'sessions today',
      focusStatMinutes: 'min today',
      focusStatWeek: 'min / week',
      focusPresetsTitle: 'presets · work / chill x cycles',
      focusFieldWork: 'work',
      focusFieldChill: 'chill',
      focusFieldCycles: 'cycles',
      focusCustom: 'custom',

      // events/widget.js
      evEvent: 'event',
      evDaily: 'daily',
      evDay: 'days', evDay_1: 'day', evDay_2: 'days', evDay_5: 'days',
      evToday: 'today',
      evHoursLeft: 'hours left',
      evHM: 'h : m',
      evMS: 'm : s',
      eventsOpening: 'opening events.json',

      // weather/widget.js
      weatherFeels: 'feels',
      weatherWind: 'wind',
      weatherHum: 'hum',
      weatherMs: 'm/s',

      // graph/widget.js
      graphLoading: 'loading',
      graphNodesUnit: 'nodes',
      graphNotFound: 'not found',
      graphOpen: 'open',

      // player/widget.js
      playerIdleState: 'idle',
      playerBrowser: 'browser',
      playerMedia: 'media',

      // launch/widget.js
      launchGroup: 'group',
      launchLaunched: 'launched',
      launchOpening: 'opening launch.json',

      // photos/widget.js
      photosEmpty: 'no photos · set folder in settings'
    }
  };

  var globalObj = typeof window !== 'undefined' ? window : (typeof globalThis !== 'undefined' ? globalThis : {});

  function lang() {
    var l = (globalObj.NNA_CONFIG && globalObj.NNA_CONFIG.language) || 'ru';
    l = String(l).toLowerCase();
    return DICT.hasOwnProperty(l) ? l : 'ru';
  }

  function t(key, fallback) {
    var d = DICT[lang()];
    if (d && Object.prototype.hasOwnProperty.call(d, key)) return d[key];
    return fallback != null ? fallback : key;
  }

  return { t: t, dict: DICT };
});
