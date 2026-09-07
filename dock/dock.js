/* NNA1618 — док (stage 9): тянет /dock/items (плюс /config за токеном), строит полосу иконок,
   увеличение по macOS-формуле у курсора, подпись над иконкой, прыжок при запуске, разделитель,
   папки веером, корзина, правый клик — контекстное меню (NNAUI.menu/dialog/popover из
   /ui/components.js). Сообщает хосту размер контента ({type:'size',...}) через
   window.chrome.webview.postMessage, чтобы Dock/DockWindow.xaml.cs выставил окно по содержимому.
   ?mock=1 рисует 8 иконок-заглушек без единого сетевого запроса — используется скриншот-тестом
   (tests/dock-probe.ps1), которому не нужно живое состояние окон/launch.json. ES5-стиль. */
(function () {
  'use strict';

  function qparam(name) {
    var m = new RegExp('[?&]' + name + '=([^&]*)').exec(location.search);
    return m ? decodeURIComponent(m[1].replace(/\+/g, ' ')) : null;
  }

  var monitorId = qparam('monitor') || 'main';
  var mockMode = qparam('mock') === '1';

  var DK = window.DK = {
    token: '',
    lang: 'ru',
    size: 55,
    magnify: true,
    magnifyMax: 1.6
  };

  /* ---- fetch-хелперы ------------------------------------------------------ */
  DK.get = function (path) {
    return fetch(path, { cache: 'no-store' }).then(function (r) {
      if (!r.ok) throw new Error('HTTP ' + r.status);
      return r.json();
    });
  };

  DK.post = function (path, body) {
    var sep = path.indexOf('?') >= 0 ? '&' : '?';
    var opts = { method: 'POST' };
    if (body !== undefined) {
      opts.headers = { 'Content-Type': 'application/json; charset=utf-8' };
      opts.body = JSON.stringify(body);
    }
    return fetch(path + sep + 't=' + encodeURIComponent(DK.token), opts)
      .then(function (r) { return r.json().catch(function () { return {}; }); });
  };

  DK.svg = function (inner, viewBox) {
    var wrap = document.createElement('span');
    wrap.innerHTML = '<svg viewBox="' + (viewBox || '0 0 24 24') + '">' + inner + '</svg>';
    return wrap.firstChild;
  };

  var text = function (ru, en) { return DK.lang === 'en' ? en : ru; };

  /* ---- сообщения хосту (размер окна) --------------------------------------- */
  function postToHost(msg) {
    try {
      if (window.chrome && window.chrome.webview) window.chrome.webview.postMessage(msg);
    } catch (e) { /* не внутри WebView2 (обычный браузер при разработке) */ }
  }

  function reportSize() {
    requestAnimationFrame(function () {
      var wrap = document.getElementById('dkWrap');
      postToHost({ type: 'size', width: wrap.scrollWidth, height: wrap.scrollHeight });
    });
  }

  /* ---- разметка -------------------------------------------------------- */
  var bar = document.getElementById('dkBar');
  var openFan = null;   /* { close } из NNAUI.popover, при клике по другой папке закрывается */
  var openMenu = null;  /* { close } из NNAUI.menu */

  function closePopups() {
    if (openFan) { openFan.close(); openFan = null; }
    if (openMenu) { openMenu.close(); openMenu = null; }
  }

  function iconImg(src, alt, svgInner) {
    var box = document.createElement('div');
    box.className = 'dk-icon-img';
    if (svgInner) {
      /* монохромный SVG-силуэт (реальная иконка не извлечена — так и ?mock=1) — не монограмма */
      box.appendChild(DK.svg(svgInner, '0 0 24 24'));
    } else if (src) {
      var img = document.createElement('img');
      img.src = src;
      img.alt = alt || '';
      img.draggable = false;
      box.appendChild(img);
    } else {
      /* ни иконки, ни силуэта: монограмма, а не пустой квадрат */
      var mono = document.createElement('div');
      mono.className = 'dk-icon-placeholder';
      var letter = (alt || '?').replace(/^\s+/, '');
      mono.textContent = letter.charAt(0).toUpperCase() || '?';
      box.appendChild(mono);
    }
    return box;
  }

  function trashSvg(empty) {
    /* минималистичная своя иконка корзины (без готовых наборов): корпус + крышка;
       заполненная — с дополнительной внутренней полосой. */
    var body = '<path d="M6 8h12l-1 12a2 2 0 0 1-2 2H9a2 2 0 0 1-2-2L6 8z" fill="none" stroke="currentColor" stroke-width="1.6"/>' +
      '<path d="M4 8h16" stroke="currentColor" stroke-width="1.6"/>' +
      '<path d="M9.5 8V6a1.5 1.5 0 0 1 1.5-1.5h2A1.5 1.5 0 0 1 14.5 6v2" fill="none" stroke="currentColor" stroke-width="1.6"/>';
    if (!empty) {
      body += '<path d="M9.5 12v6M12 12v6M14.5 12v6" stroke="currentColor" stroke-width="1.4" opacity=".7"/>';
    }
    return DK.svg(body, '0 0 24 24');
  }

  function buildIcon(item) {
    var el = document.createElement('div');
    el.className = 'dk-icon';
    el.dataset.kind = item.kind;
    if (item.id) el.dataset.id = item.id;

    var img;
    if (item.kind === 'trash') {
      img = document.createElement('div');
      img.className = 'dk-icon-img';
      img.appendChild(trashSvg(!!item.empty));
    } else {
      img = iconImg(item.icon, item.title, item.svgIcon);
    }
    el.appendChild(img);

    var tip = document.createElement('div');
    tip.className = 'dk-icon-tip';
    tip.textContent = item.title || '';
    el.appendChild(tip);

    if (item.kind === 'app') {
      var dot = document.createElement('div');
      dot.className = 'dk-icon-dot';
      el.appendChild(dot);
      if (item.running) el.classList.add('is-running');
      if (item.foreground) el.classList.add('is-foreground');
    }

    el.addEventListener('click', function (e) { onIconClick(item, el, e); });
    el.addEventListener('contextmenu', function (e) { e.preventDefault(); onIconContext(item, el); });
    return el;
  }

  function render(items) {
    closePopups();
    bar.innerHTML = '';
    items.forEach(function (item) {
      if (item.kind === 'separator') {
        var sep = document.createElement('div');
        sep.className = 'dk-sep';
        bar.appendChild(sep);
        return;
      }
      bar.appendChild(buildIcon(item));
    });
    reportSize();
  }

  /* ---- клики ------------------------------------------------------------- */
  function activateWindow(hwnd) {
    return DK.post('/windows/activate', { hwnd: hwnd });
  }

  function onIconClick(item, el, e) {
    closePopups();
    if (item.kind === 'folder') {
      openFolderFan(item, el);
      return;
    }
    if (item.kind === 'trash') {
      DK.post('/dock/trash/open');
      return;
    }
    if (item.kind !== 'app') return;

    var windows = item.windows || [];
    var newInstance = !!e.shiftKey;
    if (!newInstance && windows.length > 0) {
      var target = windows.filter(function (w) { return !w.minimized; })[0] || windows[0];
      activateWindow(target.hwnd);
      return;
    }
    if (!item.launchId) return; /* running-only entry with no matching launch item: nothing to (re)launch */
    el.classList.add('is-jumping');
    setTimeout(function () { el.classList.remove('is-jumping'); }, 1200);
    var path = '/launch/item?id=' + encodeURIComponent(item.launchId) + (newInstance ? '&newInstance=true' : '');
    DK.post(path);
  }

  function onIconContext(item, el) {
    closePopups();
    var items = [];
    if (item.kind === 'app') {
      (item.windows || []).forEach(function (w) {
        items.push({ label: w.title || text('Окно', 'Window'), onClick: function () { activateWindow(w.hwnd); } });
      });
      if (items.length) items.push({ separator: true });
      if ((item.windows || []).length) {
        items.push({
          label: text('Свернуть', 'Minimize'),
          onClick: function () { (item.windows || []).forEach(function (w) { DK.post('/windows/minimize', { hwnd: w.hwnd }); }); }
        });
        items.push({
          label: text('Закрыть', 'Close'),
          onClick: function () { (item.windows || []).forEach(function (w) { DK.post('/windows/close', { hwnd: w.hwnd }); }); }
        });
        items.push({ separator: true });
      }
      if (item.launchId) {
        items.push({
          label: item.pinned ? text('Открепить', 'Unpin') : text('Закрепить', 'Pin'),
          onClick: function () {
            DK.post(item.pinned ? '/dock/unpin' : '/dock/pin', { launchId: item.launchId }).then(refresh);
          }
        });
      }
    } else if (item.kind === 'folder') {
      items.push({ label: text('Открыть', 'Open'), onClick: function () { DK.post('/dock/folder/open', { path: item.path }); } });
    } else if (item.kind === 'trash') {
      items.push({ label: text('Открыть', 'Open'), onClick: function () { DK.post('/dock/trash/open'); } });
      items.push({ label: text('Очистить', 'Empty'), onClick: confirmEmptyTrash });
    }
    if (!items.length) return;
    openMenu = window.NNAUI.menu(el, { items: items, placement: 'top', onClose: function () { openMenu = null; } });
  }

  function confirmEmptyTrash() {
    window.NNAUI.dialog({
      title: text('Очистить корзину', 'Empty trash'),
      body: text('Файлы будут удалены без возможности восстановления.', 'Files will be permanently deleted.'),
      actions: [
        { label: text('Отмена', 'Cancel') },
        {
          label: text('Очистить', 'Empty'), primary: true,
          onClick: function () { DK.post('/dock/trash/empty', { confirm: true }).then(refresh); }
        }
      ]
    });
  }

  function openFolderFan(item, anchorEl) {
    var grid = document.createElement('div');
    grid.className = 'dk-fan-grid';
    var loading = document.createElement('div');
    loading.className = 'dk-fan-empty';
    loading.textContent = text('Загрузка…', 'Loading…');
    grid.appendChild(loading);

    openFan = window.NNAUI.popover(anchorEl, { content: grid, placement: 'top', onClose: function () { openFan = null; } });

    DK.get('/dock/folder?path=' + encodeURIComponent(item.path)).then(function (data) {
      grid.innerHTML = '';
      var entries = data.entries || [];
      if (!entries.length) {
        var empty = document.createElement('div');
        empty.className = 'dk-fan-empty';
        empty.textContent = text('Пусто', 'Empty');
        grid.appendChild(empty);
        return;
      }
      entries.forEach(function (entry) {
        var row = document.createElement('div');
        row.className = 'dk-fan-item';
        var img = document.createElement('img');
        img.src = entry.icon;
        img.alt = '';
        row.appendChild(img);
        var label = document.createElement('span');
        label.textContent = entry.name;
        row.appendChild(label);
        row.addEventListener('click', function () {
          if (entry.isDir) { DK.post('/dock/folder/open', { path: entry.path }); }
          else { DK.post('/open?path=' + encodeURIComponent(entry.path)); }
          closePopups();
        });
        grid.appendChild(row);
      });
    }).catch(function () {
      grid.innerHTML = '';
      var err = document.createElement('div');
      err.className = 'dk-fan-empty';
      err.textContent = text('Не удалось загрузить', 'Failed to load');
      grid.appendChild(err);
    });
  }

  /* ---- увеличение по наведению (macOS-формула, косинусная волна) --------- */
  function setupMagnify() {
    if (!DK.magnify) return;
    var radiusIcons = 3;
    bar.addEventListener('mousemove', function (e) {
      var icons = bar.querySelectorAll('.dk-icon');
      var radius = DK.size * radiusIcons;
      for (var i = 0; i < icons.length; i++) {
        var img = icons[i].querySelector('.dk-icon-img');
        var r = icons[i].getBoundingClientRect();
        var center = r.left + r.width / 2;
        var dx = e.clientX - center;
        var scale = 1;
        if (Math.abs(dx) < radius) {
          scale = 1 + (DK.magnifyMax - 1) * Math.cos((Math.PI / 2) * (dx / radius));
        }
        img.style.transform = 'scale(' + scale.toFixed(3) + ')';
      }
    });
    bar.addEventListener('mouseleave', function () {
      var imgs = bar.querySelectorAll('.dk-icon-img');
      for (var i = 0; i < imgs.length; i++) imgs[i].style.transform = 'scale(1)';
    });
  }

  /* ---- живые обновления ---------------------------------------------------- */
  var lastItems = [];
  function refresh() {
    DK.get('/dock/items').then(function (data) {
      lastItems = data.items || [];
      render(lastItems);
    }).catch(function () { /* временная сеть/рестарт хоста — оставляем прежний вид */ });
  }

  function connectEvents() {
    try {
      var proto = location.protocol === 'https:' ? 'wss:' : 'ws:';
      var ws = new WebSocket(proto + '//' + location.host + '/events');
      ws.onmessage = function (ev) {
        var d = null;
        try { d = JSON.parse(ev.data); } catch (e) { return; }
        if (!d || typeof d !== 'object') return;
        if (d.type === 'dock-changed' || (d.type === 'config-changed' && (d.what === 'app' || d.what === 'launch'))) {
          refresh();
        }
      };
      ws.onclose = function () { setTimeout(connectEvents, 3000); };
      ws.onerror = function () { try { ws.close(); } catch (e) {} };
    } catch (e) { /* без /events страница просто не обновляется само по себе */ }
  }

  /* ---- заглушки для скриншот-теста (?mock=1) ------------------------------
     Честный превью дока: 8 приложений как монохромные SVG-силуэты (не монограммы — те
     означают "иконку не удалось извлечь", это другое сообщение), разделитель, две папки,
     корзина — та же форма набора, что реальный /dock/items (app.. + separator + folder×2 +
     trash), см. tests/dock-probe.ps1. Силуэты нарочно обобщённые (браузер/чат/почта/
     календарь/музыка/камера/заметки/терминал), а не логотипы конкретных программ. */
  var MOCK_APP_ICONS = [
    { title: 'Browser', svg: '<circle cx="12" cy="12" r="8" fill="none" stroke="currentColor" stroke-width="1.6"/>' +
      '<path d="M4 12h16M12 4c2.6 2.4 2.6 13.2 0 16M12 4c-2.6 2.4-2.6 13.2 0 16" fill="none" stroke="currentColor" stroke-width="1.3"/>' },
    { title: 'Chat', svg: '<path d="M5 6h14a1 1 0 0 1 1 1v8a1 1 0 0 1-1 1H10l-4 3v-3H5a1 1 0 0 1-1-1V7a1 1 0 0 1 1-1z" ' +
      'fill="none" stroke="currentColor" stroke-width="1.6" stroke-linejoin="round"/>' },
    { title: 'Mail', svg: '<rect x="4" y="6" width="16" height="12" rx="1.5" fill="none" stroke="currentColor" stroke-width="1.6"/>' +
      '<path d="M5 7l7 6 7-6" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linejoin="round" stroke-linecap="round"/>' },
    { title: 'Calendar', svg: '<rect x="4" y="5" width="16" height="15" rx="1.5" fill="none" stroke="currentColor" stroke-width="1.6"/>' +
      '<path d="M4 10h16M8 3v4M16 3v4" stroke="currentColor" stroke-width="1.6" stroke-linecap="round"/>' },
    { title: 'Music', svg: '<circle cx="7" cy="18" r="2.3" fill="currentColor"/><circle cx="17" cy="16" r="2.3" fill="currentColor"/>' +
      '<path d="M9.3 18V6.5L19.3 4.5V14" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"/>' },
    { title: 'Camera', svg: '<rect x="3.5" y="7" width="17" height="12" rx="2" fill="none" stroke="currentColor" stroke-width="1.6"/>' +
      '<path d="M8.5 7l1.4-2.2h4.2L15.5 7" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linejoin="round"/>' +
      '<circle cx="12" cy="13" r="3.3" fill="none" stroke="currentColor" stroke-width="1.6"/>' },
    { title: 'Notes', svg: '<path d="M6 3h9l4 4v14H6z" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linejoin="round"/>' +
      '<path d="M9 12h7M9 16h7" stroke="currentColor" stroke-width="1.4" stroke-linecap="round"/>' },
    { title: 'Terminal', svg: '<rect x="3.5" y="4.5" width="17" height="15" rx="2" fill="none" stroke="currentColor" stroke-width="1.6"/>' +
      '<path d="M7 9l3 3-3 3M12.5 15h4.5" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"/>' }
  ];
  function mockItems() {
    var out = [];
    MOCK_APP_ICONS.forEach(function (app, i) {
      out.push({
        kind: 'app',
        id: 'mock-' + i,
        title: app.title,
        icon: null,
        svgIcon: app.svg,
        running: i % 3 === 0,
        foreground: i === 0,
        windows: i % 3 === 0 ? [{ hwnd: 1000 + i, title: 'Window', minimized: false }] : [],
        pinned: true,
        launchId: 'mock-' + i,
        path: null
      });
    });
    var folderSvg = '<path d="M4 7a1 1 0 0 1 1-1h4l2 2h8a1 1 0 0 1 1 1v9a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V7z" ' +
      'fill="none" stroke="currentColor" stroke-width="1.6" stroke-linejoin="round"/>';
    out.push({ kind: 'separator' });
    out.push({ kind: 'folder', id: 'mock-folder-downloads', title: text('Загрузки', 'Downloads'), svgIcon: folderSvg, path: null });
    out.push({ kind: 'folder', id: 'mock-folder-desktop', title: text('Рабочий стол', 'Desktop'), svgIcon: folderSvg, path: null });
    out.push({ kind: 'trash', id: 'mock-trash', title: text('Корзина', 'Trash'), empty: true });
    return out;
  }

  /* ---- загрузка и старт ----------------------------------------------------- */
  function applySettings(cfg, dockSettings) {
    DK.token = cfg.token || '';
    DK.lang = cfg.language === 'en' ? 'en' : 'ru';
    var s = dockSettings || {};
    DK.size = s.size || 55;
    DK.magnify = s.magnify !== false;
    DK.magnifyMax = s.magnifyMax || 1.6;
    document.documentElement.style.setProperty('--dk-size', DK.size + 'px');
  }

  function boot(cfg, dockData) {
    applySettings(cfg, dockData.settings);
    render(dockData.items || []);
    setupMagnify();
    connectEvents();
  }

  if (mockMode) {
    applySettings({ token: '', language: 'ru' }, { size: 55, magnify: true, magnifyMax: 1.6 });
    render(mockItems());
    setupMagnify();
  } else {
    Promise.all([
      DK.get('/config?monitor=' + encodeURIComponent(monitorId)),
      DK.get('/dock/items')
    ]).then(function (r) {
      boot(r[0], r[1]);
    }).catch(function (err) {
      document.body.textContent = 'DOCK LOAD FAILED: ' + ((err && err.message) || err);
    });
  }
})();
