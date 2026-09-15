/* detect-rfid.js — timed multi-tag RFID detect vs ST_ITEM EPC codes */
(function () {
  'use strict';

  const cfg = document.getElementById('detectRfidConfig');
  if (!cfg || typeof ScanResolve === 'undefined') return;

  const expectedUrl = cfg.dataset.expectedUrl || '';
  let detectSeconds = Math.clamp
    ? Math.clamp(Number(cfg.dataset.detectSeconds) || 3, 2, 5)
    : Math.min(5, Math.max(2, Number(cfg.dataset.detectSeconds) || 3));

  const btn = document.getElementById('detectRfidBtn');
  const statusEl = document.getElementById('detectRfidStatus');
  const countdownWrap = document.getElementById('detectRfidCountdown');
  const countdownNum = document.getElementById('detectRfidCountdownNum');
  const capture = document.getElementById('detectListenCapture');
  const expectedMeta = document.getElementById('detectExpectedMeta');
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

  let sessionActive = false;
  let sessionTimer = null;
  let tickTimer = null;
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

  const splitCodes = (raw) => {
    if (typeof ScanResolve.splitInventoryCodes === 'function')
      return ScanResolve.splitInventoryCodes(raw);
    const t = String(raw || '').trim();
    return t ? [t] : [];
  };

  const ingestRaw = (raw) => {
    for (const code of splitCodes(raw)) {
      const key = code.toUpperCase();
      if (!key) continue;
      detectedKeys.add(key);
      if (!detectedDisplay.has(key)) detectedDisplay.set(key, code);
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
      `Done — heard ${detectedKeys.size}, matched ${matched.length}, unknown ${unknown.length}, missing ${missing.length}`
    );
  };

  const stopSession = () => {
    sessionActive = false;
    clearTimeout(sessionTimer);
    clearInterval(tickTimer);
    clearTimeout(captureIdleTimer);
    clearTimeout(refocusTimer);
    sessionTimer = null;
    tickTimer = null;
    if (countdownWrap) countdownWrap.hidden = true;
    if (btn) {
      btn.disabled = false;
      btn.textContent = 'Detect RFID';
    }
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
    if (results) results.hidden = true;

    sessionActive = true;
    if (btn) {
      btn.disabled = true;
      btn.textContent = 'Detecting…';
    }
    if (countdownWrap) countdownWrap.hidden = false;

    let left = detectSeconds;
    if (countdownNum) countdownNum.textContent = String(left);
    setStatus(`Listening… hold trigger (${left}s)`);
    focusCapture();

    tickTimer = setInterval(() => {
      left -= 1;
      if (countdownNum) countdownNum.textContent = String(Math.max(0, left));
      if (left > 0) setStatus(`Listening… hold trigger (${left}s)`);
    }, 1000);

    sessionTimer = setTimeout(stopSession, detectSeconds * 1000);
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
          `${expectedByKey.size} EPC-style item(s) in inventory (detect window ${detectSeconds}s)`;
      }
      setStatus('Ready');
    } catch (e) {
      if (expectedMeta) expectedMeta.textContent = 'Failed to load inventory codes.';
      setStatus(e.message || 'Load failed');
    }
  };

  btn?.addEventListener('click', () => startSession());

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
