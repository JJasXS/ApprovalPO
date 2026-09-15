/* detect-rfid.js — continuous multi-tag RFID session vs ST_ITEM EPC codes (HID wedge) */
(function () {
  'use strict';

  const cfg = document.getElementById('detectRfidConfig');
  if (!cfg || typeof ScanResolve === 'undefined') return;

  const expectedUrl = cfg.dataset.expectedUrl || '';
  const safetyTimeoutMs = Math.min(
    30 * 60 * 1000,
    Math.max(60 * 1000, Number(cfg.dataset.safetyTimeoutMs) || 300000)
  );

  const startBtn = document.getElementById('detectRfidStartBtn');
  const stopBtn = document.getElementById('detectRfidStopBtn');
  const statusEl = document.getElementById('detectRfidStatus');
  const capture = document.getElementById('detectListenCapture');
  const expectedMeta = document.getElementById('detectExpectedMeta');
  const livePanel = document.getElementById('detectLivePanel');
  const liveCountEl = document.getElementById('detectLiveCount');
  const liveList = document.getElementById('detectLiveList');
  const results = document.getElementById('detectResults');
  const matchedList = document.getElementById('detectMatchedList');
  const unknownList = document.getElementById('detectUnknownList');
  const missingList = document.getElementById('detectMissingList');
  const matchedCountEl = document.getElementById('detectMatchedCount');
  const unknownCountEl = document.getElementById('detectUnknownCount');
  const missingCountEl = document.getElementById('detectMissingCount');

  /** @type {Map<string, { code: string, description: string }>} */
  let expectedByKey = new Map();
  /** @type {Set<string>} */
  let detectedKeys = new Set();
  /** @type {Map<string, string>} */
  let detectedDisplay = new Map();
  /** Insertion order for live list (unique keys). */
  /** @type {string[]} */
  let liveOrder = [];

  let sessionActive = false;
  let safetyTimer = null;
  let captureIdleTimer = null;
  let refocusTimer = null;
  const CAPTURE_IDLE_MS = 70;

  const shortCode = (code) => {
    const c = String(code || '').trim();
    if (c.length <= 16) return c;
    return '…' + c.slice(-8);
  };

  const setStatus = (text) => {
    if (statusEl) statusEl.textContent = text;
  };

  const setSessionButtons = (active) => {
    if (startBtn) startBtn.disabled = !!active;
    if (stopBtn) stopBtn.disabled = !active;
  };

  const splitCodes = (raw) => {
    if (typeof ScanResolve.splitInventoryCodes === 'function')
      return ScanResolve.splitInventoryCodes(raw);
    const t = String(raw || '').trim();
    return t ? [t] : [];
  };

  const refreshLiveUi = () => {
    if (liveCountEl) liveCountEl.textContent = String(detectedKeys.size);
    if (!liveList) return;
    liveList.innerHTML = '';
    if (!liveOrder.length) {
      const li = document.createElement('li');
      li.className = 'detect-rfid-empty';
      li.textContent = 'Waiting for tags…';
      liveList.appendChild(li);
      return;
    }
    for (const key of liveOrder) {
      const code = detectedDisplay.get(key) || key;
      const li = document.createElement('li');
      const codeEl = document.createElement('code');
      codeEl.title = code;
      codeEl.textContent = shortCode(code);
      li.appendChild(codeEl);
      liveList.appendChild(li);
    }
    try {
      liveList.scrollTop = liveList.scrollHeight;
    } catch (_) { /* ignore */ }
  };

  const ingestRaw = (raw) => {
    let added = 0;
    for (const code of splitCodes(raw)) {
      const key = code.toUpperCase();
      if (!key) continue;
      if (detectedKeys.has(key)) continue;
      detectedKeys.add(key);
      detectedDisplay.set(key, code);
      liveOrder.push(key);
      added += 1;
    }
    if (added > 0) {
      refreshLiveUi();
      if (sessionActive) {
        setStatus(`Listening… ${detectedKeys.size} unique tag(s)`);
      }
    }
  };

  const flushCapture = () => {
    clearTimeout(captureIdleTimer);
    captureIdleTimer = null;
    if (!capture) return;
    const text = String(capture.value || '').trim();
    capture.value = '';
    if (text.length >= 3) ingestRaw(text);
  };

  const focusCapture = () => {
    if (!capture || !sessionActive) return;
    try {
      capture.focus({ preventScroll: true });
    } catch (_) {
      try { capture.focus(); } catch (__) { /* ignore */ }
    }
  };

  const fillList = (ul, items, emptyText) => {
    if (!ul) return;
    ul.innerHTML = '';
    if (!items.length) {
      const li = document.createElement('li');
      li.className = 'detect-rfid-empty';
      li.textContent = emptyText;
      ul.appendChild(li);
      return;
    }
    for (const item of items) {
      const li = document.createElement('li');
      const codeEl = document.createElement('code');
      codeEl.title = item.code;
      codeEl.textContent = shortCode(item.code);
      li.appendChild(codeEl);
      if (item.description) {
        const desc = document.createElement('span');
        desc.className = 'detect-rfid-desc';
        desc.textContent = item.description;
        li.appendChild(desc);
      }
      ul.appendChild(li);
    }
  };

  const reconcileAndShow = () => {
    const matched = [];
    const unknown = [];
    const missing = [];

    for (const key of detectedKeys) {
      const code = detectedDisplay.get(key) || key;
      const exp = expectedByKey.get(key);
      if (exp) matched.push({ code, description: exp.description || '' });
      else unknown.push({ code, description: '' });
    }

    for (const [key, exp] of expectedByKey) {
      if (!detectedKeys.has(key))
        missing.push({ code: exp.code, description: exp.description || '' });
    }

    matched.sort((a, b) => a.code.localeCompare(b.code));
    unknown.sort((a, b) => a.code.localeCompare(b.code));
    missing.sort((a, b) => a.code.localeCompare(b.code));

    if (matchedCountEl) matchedCountEl.textContent = String(matched.length);
    if (unknownCountEl) unknownCountEl.textContent = String(unknown.length);
    if (missingCountEl) missingCountEl.textContent = String(missing.length);

    fillList(matchedList, matched, 'None matched');
    fillList(unknownList, unknown, 'None unknown');
    fillList(missingList, missing, 'None missing');

    if (results) results.hidden = false;
    setStatus(
      `Done — ${detectedKeys.size} unique, matched ${matched.length}, unknown ${unknown.length}, missing ${missing.length}`
    );
  };

  const clearSessionTimers = () => {
    clearTimeout(safetyTimer);
    clearTimeout(captureIdleTimer);
    clearTimeout(refocusTimer);
    safetyTimer = null;
    captureIdleTimer = null;
    refocusTimer = null;
  };

  const stopSession = () => {
    if (!sessionActive) return;
    sessionActive = false;
    clearSessionTimers();
    setSessionButtons(false);
    flushCapture();
    if (capture) {
      try { capture.blur(); } catch (_) { /* ignore */ }
    }
    reconcileAndShow();
  };

  const startSession = () => {
    if (sessionActive) return;
    if (expectedByKey.size === 0) {
      setStatus('No EPC-style inventory items loaded.');
      return;
    }

    detectedKeys = new Set();
    detectedDisplay = new Map();
    liveOrder = [];
    if (results) results.hidden = true;
    if (livePanel) livePanel.hidden = false;
    refreshLiveUi();

    sessionActive = true;
    setSessionButtons(true);
    setStatus('Listening… hold gun trigger (press Stop when done)');
    focusCapture();

    clearTimeout(safetyTimer);
    safetyTimer = setTimeout(() => {
      if (!sessionActive) return;
      setStatus('Safety timeout — stopping scan.');
      stopSession();
    }, safetyTimeoutMs);
  };

  const loadExpected = async () => {
    if (!expectedUrl) {
      if (expectedMeta) expectedMeta.textContent = 'Expected inventory URL missing.';
      return;
    }
    try {
      const res = await fetch(expectedUrl, { credentials: 'same-origin' });
      if (!res.ok) throw new Error(`HTTP ${res.status}`);
      const data = await res.json();
      expectedByKey = new Map();
      for (const row of data.items || []) {
        const code = String(row.code || '').trim();
        if (!code) continue;
        expectedByKey.set(code.toUpperCase(), {
          code,
          description: String(row.description || '').trim()
        });
      }
      if (expectedMeta) {
        expectedMeta.textContent =
          `${expectedByKey.size} EPC-style item(s) in inventory — Start scan, then Stop to compare`;
      }
      setStatus('Ready');
      setSessionButtons(false);
    } catch (e) {
      if (expectedMeta) expectedMeta.textContent = 'Failed to load inventory codes.';
      setStatus(e.message || 'Load failed');
      if (startBtn) startBtn.disabled = true;
      if (stopBtn) stopBtn.disabled = true;
    }
  };

  startBtn?.addEventListener('click', () => startSession());
  stopBtn?.addEventListener('click', () => stopSession());

  if (capture) {
    capture.addEventListener('keydown', (e) => {
      if (!sessionActive) return;
      if (e.key === 'Enter' || e.key === 'Tab') {
        e.preventDefault();
        flushCapture();
      }
    });
    capture.addEventListener('input', () => {
      if (!sessionActive) return;
      clearTimeout(captureIdleTimer);
      captureIdleTimer = setTimeout(flushCapture, CAPTURE_IDLE_MS);
    });
    capture.addEventListener('blur', () => {
      if (!sessionActive) return;
      clearTimeout(refocusTimer);
      refocusTimer = setTimeout(focusCapture, 40);
    });
  }

  // Page-wide wedge backup while session active (if focus escapes).
  let wedgeBuf = '';
  let wedgeTimer = null;
  document.addEventListener('keydown', (e) => {
    if (!sessionActive) return;
    if (e.target === capture) return;
    if (e.target === startBtn || e.target === stopBtn) return;
    if (e.ctrlKey || e.metaKey || e.altKey) return;
    if (e.key === 'Enter' || e.key === 'Tab') {
      if (!wedgeBuf.trim()) return;
      e.preventDefault();
      clearTimeout(wedgeTimer);
      ingestRaw(wedgeBuf);
      wedgeBuf = '';
      return;
    }
    if (e.key.length !== 1) return;
    wedgeBuf += e.key;
    clearTimeout(wedgeTimer);
    wedgeTimer = setTimeout(() => {
      ingestRaw(wedgeBuf);
      wedgeBuf = '';
    }, CAPTURE_IDLE_MS);
  });

  void loadExpected();
})();
