/* NNA1618 — служебные подписи внутри виджетов (не заголовки блоков — те приходят из
   title:{ru,en} манифестов, см. wallpaper/layout.js). window.NNA.t(key, fallback) возвращает
   строку текущего языка (window.NNA_CONFIG.language, по умолчанию ru); регистр — обычный,
   капс делает CSS (.nna-mono/.nna-btn/.nna-corner/.nna-label — text-transform: uppercase),
   строки здесь пишем как обычный текст. */
(function () {
  'use strict';

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
      launchEmpty: 'Добавьте приложения на странице Блоки'
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
      launchEmpty: 'Add apps on the Blocks page'
    }
  };

  function lang() {
    var l = (window.NNA_CONFIG && window.NNA_CONFIG.language) || 'ru';
    l = String(l).toLowerCase();
    return DICT.hasOwnProperty(l) ? l : 'ru';
  }

  window.NNA = window.NNA || {};
  window.NNA.t = function (key, fallback) {
    var d = DICT[lang()];
    if (d && Object.prototype.hasOwnProperty.call(d, key)) return d[key];
    return fallback != null ? fallback : key;
  };
})();
