/* NNA1618 — библиотека для стороннего виджета (kind=page), подключается его index.html.
   Всё общение с хостом идёт через postMessage к родительской странице обоев (layout.js),
   которая пропускает только пути из needs манифеста; токен внутри iframe не виден. */
(function () {
  'use strict';

  var reqId = 0;
  var pending = {};
  var handlers = {};
  var readyCbs = [];
  var ctxCache = null;

  function request(method, path) {
    return new Promise(function (resolve) {
      var id = 'r' + (++reqId);
      pending[id] = resolve;
      try {
        parent.postMessage({ __nna: true, type: 'bridge', id: id, method: method, path: path }, '*');
      } catch (e) {
        resolve({ error: String((e && e.message) || e) });
      }
    });
  }

  window.addEventListener('message', function (e) {
    var d = e.data;
    if (!d || typeof d !== 'object') return;
    if (d.type === 'bridge-reply' && d.id && pending.hasOwnProperty(d.id)) {
      var resolve = pending[d.id];
      delete pending[d.id];
      resolve(d.result);
      return;
    }
    if (d.type === 'ctx') {
      ctxCache = d.ctx;
      var cbs = readyCbs;
      readyCbs = [];
      for (var i = 0; i < cbs.length; i++) {
        try { cbs[i](ctxCache); } catch (err) { /* noop */ }
      }
      return;
    }
    if (d.type === 'event' && d.name && handlers[d.name]) {
      var list = handlers[d.name];
      for (var j = 0; j < list.length; j++) {
        try { list[j](d.detail); } catch (err) { /* noop */ }
      }
    }
  });

  window.NNA = {
    ready: function (cb) {
      if (ctxCache) cb(ctxCache); else readyCbs.push(cb);
    },
    get: function (path) { return request('get', path); },
    post: function (path) { return request('post', path); },
    on: function (name, cb) {
      handlers[name] = handlers[name] || [];
      handlers[name].push(cb);
    },
    toast: function (text) {
      try { parent.postMessage({ __nna: true, type: 'toast', text: text }, '*'); } catch (e) { /* noop */ }
    }
  };
})();
