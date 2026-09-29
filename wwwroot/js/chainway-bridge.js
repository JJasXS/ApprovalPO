/* chainway-bridge.js — Chainway WebView EPC events. Ordinary browsers never fire these. */
(function () {
  'use strict';

  var lastKey = '';
  var lastAt = 0;
  var dedupeMs = 800;

  window.approvalSetChainwayMode = function (mode) {
    var value = mode === 'continuous' ? 'continuous' : 'single';
    document.documentElement.setAttribute('data-chainway-mode', value);
  };

  window.approvalSetChainwayMode('single');

  window.addEventListener('chainway:epc', function (event) {
    if (!event || !event.detail) return;
    var epc = event.detail.epc;
    if (epc == null) return;
    var text = String(epc).trim();
    if (!text) return;
    var key = text.toUpperCase();
    var now = Date.now();
    if (key === lastKey && now - lastAt < dedupeMs) return;
    lastKey = key;
    lastAt = now;
    if (typeof window.approvalOnChainwayEpc === 'function')
      window.approvalOnChainwayEpc(text);
  });
})();
