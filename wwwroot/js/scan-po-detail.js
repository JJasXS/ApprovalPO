/* scan-po-detail.js — scan PO lines: QR/manual, draft sync, submit rules, offline queue */
(function () {
  'use strict';

  const scanBtn = document.getElementById('scanDetailScanBtn');
  const manualInput = document.getElementById('scanManualCode');
  const manualBtn = document.getElementById('scanManualBtn');
  const undoBtn = document.getElementById('scanUndoLastBtn');
  const cfg = document.getElementById('scanDetailConfig');
  if (!cfg || typeof ScanResolve === 'undefined') return;

  const resolveUrl = cfg.dataset.resolveScanUrl || '';
  const scanStateUrl = cfg.dataset.scanStateUrl || '';
  const saveDraftUrl = cfg.dataset.saveDraftUrl || '';
  const docKey = String(cfg.dataset.docKey || new URLSearchParams(location.search).get('docKey') || '');
  const poNumber = cfg.dataset.poNumber || '';
  const isSubmitted = cfg.dataset.isSubmitted === 'true';
  const storageKey = docKey ? `approvalpo-scan-session-${docKey}` : '';
  const offlineKey = docKey ? `approvalpo-scan-offline-${docKey}` : '';
  const requireAllLines = cfg.dataset.requireAllLines !== 'false';
  const csrfToken = cfg.dataset.csrfToken || '';

  const sessionTotalEl = document.getElementById('scanSessionTotal');
  const linesProgressEl = document.getElementById('scanLinesProgress');
  const submitForm = document.getElementById(cfg.dataset.submitFormId || 'scanDetailSubmitForm');
  const scanCountsInput = document.getElementById(cfg.dataset.scanCountsInputId || 'scanCountsJson');
  const submitBtn = document.getElementById('scanDetailSubmitBtn');
  const reopenForm = document.getElementById('scanDetailReopenForm');
  const toastRoot = document.getElementById('scanToastRoot');
  const linesTableBody = document.querySelector('.scan-lines-table tbody');

  /** @type {Record<string, number>} */
  let scanCounts = {};
  /** @type {{ code: string }[]} */
  let scanHistory = [];
  let draftTimer = null;
  let readOnly = isSubmitted;

  const lineRows = () =>
    Array.from(document.querySelectorAll('.scan-lines-table tbody tr[data-item-code]'));

  const knownItemCodes = () =>
    lineRows()
      .map((row) => (row.dataset.itemCode || '').trim())
      .filter(Boolean);

  const totalLines = () => lineRows().length;

  const linesWithScan = () =>
    lineRows().filter((row) => (scanCounts[(row.dataset.itemCode || '').trim()] || 0) > 0).length;

  const totalScans = () =>
    Object.values(scanCounts).reduce((sum, n) => sum + (Number(n) || 0), 0);

  const esc = (s) => String(s ?? '').replace(/[&<>"']/g, (c) =>
    ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

  const formatActor = (name, email) => {
    const n = String(name || '').trim();
    const e = String(email || '').trim();
    if (n && e && n.toLowerCase() !== e.toLowerCase()) return `${n} (${e})`;
    return n || e || 'Unknown user';
  };

  const actionLabel = (action) =>
    ({ submitted: 'Submitted', reopened: 'Reopened', draft_saved: 'Draft saved' }[action] || action);

  const renderAuditTrail = (entries) => {
    const panel = document.getElementById('scanAuditPanel');
    const list = document.getElementById('scanAuditList');
    if (!panel || !list) return;
    const items = Array.isArray(entries) ? entries : [];
    if (!items.length) {
      panel.hidden = true;
      return;
    }
    panel.hidden = false;
    list.innerHTML = items
      .map((e) => {
        const when = e.atUtc ? new Date(e.atUtc).toLocaleString() : '';
        const who = formatActor(e.userDisplayName, e.userEmail);
        const scans = e.totalScans != null ? ` · ${e.totalScans} scan(s)` : '';
        return `<li><span class="scan-audit-action">${esc(actionLabel(e.action))}</span> ${esc(when)} · ${esc(who)}${esc(scans)}</li>`;
      })
      .join('');
  };

  const updateDraftHint = (state) => {
    if (readOnly || !state) return;
    const hint = document.querySelector('.scan-session-hint');
    if (!hint) return;
    const who = formatActor(state.draftUpdatedByName, state.draftUpdatedByEmail);
    const undoTip = ' Tap ✓ on a line to remove one scan.';
    if (!who || who === 'Unknown user') {
      hint.textContent = `Draft saves to server; submit marks PO complete.${undoTip}`;
      return;
    }
    const when = state.draftUpdatedAtUtc
      ? new Date(state.draftUpdatedAtUtc).toLocaleString()
      : '';
    hint.textContent = when
      ? `Draft on server · last saved ${when} by ${who}.${undoTip}`
      : `Draft on server · last saved by ${who}.${undoTip}`;
  };

  const showToast = (message, tone = 'info') => {
    if (!toastRoot || !message) return;
    const el = document.createElement('p');
    el.className = 'scan-toast scan-toast--' + tone;
    el.setAttribute('role', 'status');
    el.textContent = message;
    toastRoot.appendChild(el);
    setTimeout(() => el.classList.add('is-out'), 3200);
    setTimeout(() => el.remove(), 3600);
  };

  const messageForScanError = (code, detail) => {
    const d = String(detail || '').trim();
    switch (code) {
      case 'offline_queued':
        return 'You are offline. Scan was queued and will run when connection returns.';
      case 'resolve_failed':
        return d
          ? `Could not read QR or link: ${d}`
          : 'Could not read QR or link. Check connection and try again.';
      case 'no_code_on_page':
        return d && d.length < 120
          ? d
          : 'QR/link opened, but no item code was found on that page.';
      case 'not_on_po':
        return d
          ? `Item code ${d} is not on this purchase order.`
          : 'That item code is not on this purchase order.';
      case 'empty_scan':
        return 'Empty scan — try again.';
      case 'already_submitted':
        return 'This PO is already submitted. Reopen to scan again.';
      default:
        return d || 'Scan could not be processed.';
    }
  };

  const showScanError = (code, detail) => {
    showToast(messageForScanError(code, detail), 'error');
  };

  const playMatchFeedback = () => {
    try {
      if (navigator.vibrate) navigator.vibrate(40);
    } catch (_) { /* ignore */ }
    try {
      const Ctx = window.AudioContext || window.webkitAudioContext;
      if (!Ctx) return;
      const ctx = new Ctx();
      const osc = ctx.createOscillator();
      const gain = ctx.createGain();
      osc.connect(gain);
      gain.connect(ctx.destination);
      osc.frequency.value = 880;
      gain.gain.value = 0.08;
      osc.start();
      osc.stop(ctx.currentTime + 0.1);
    } catch (_) { /* ignore */ }
  };

  const mergeCounts = (incoming) => {
    if (!incoming || typeof incoming !== 'object') return;
    Object.entries(incoming).forEach(([code, n]) => {
      const c = String(code || '').trim();
      const v = Number(n) || 0;
      if (c && v > 0) scanCounts[c] = v;
    });
  };

  const loadLocalSession = () => {
    if (!storageKey) return;
    try {
      const raw = sessionStorage.getItem(storageKey);
      if (raw) mergeCounts(JSON.parse(raw));
    } catch (_) { /* ignore */ }
  };

  const saveLocalSession = () => {
    if (!storageKey) return;
    try {
      sessionStorage.setItem(storageKey, JSON.stringify(scanCounts));
    } catch (_) { /* ignore */ }
  };

  const updateUndoButton = () => {
    if (!undoBtn || readOnly) return;
    const canUndo = scanHistory.length > 0 && totalScans() > 0;
    undoBtn.hidden = !canUndo;
    undoBtn.disabled = !canUndo;
  };

  const scheduleDraftSave = () => {
    if (readOnly || !saveDraftUrl || !docKey) return;
    clearTimeout(draftTimer);
    draftTimer = setTimeout(() => void persistDraft(), 1500);
  };

  const persistDraft = async () => {
    if (readOnly || !navigator.onLine) return;
    try {
      const body = new URLSearchParams();
      body.set('docKey', docKey);
      body.set('scanCountsJson', JSON.stringify(scanCounts));
      const headers = { 'Content-Type': 'application/x-www-form-urlencoded' };
      if (csrfToken) headers['X-CSRF-TOKEN'] = csrfToken;
      await fetch(saveDraftUrl, {
        method: 'POST',
        credentials: 'same-origin',
        headers,
        body: body.toString(),
      });
    } catch (_) { /* offline — local session only */ }
  };

  const loadServerState = async () => {
    if (!scanStateUrl || !docKey) return;
    try {
      const sep = scanStateUrl.includes('?') ? '&' : '?';
      const res = await fetch(`${scanStateUrl}${sep}docKey=${encodeURIComponent(docKey)}`, {
        credentials: 'same-origin',
      });
      if (!res.ok) return;
      const state = await res.json();
      if (state.isSubmitted) {
        readOnly = true;
        scanCounts = {};
        scanHistory = [];
        mergeCounts(state.scanCounts);
        setReadOnlyUi(state);
        renderAuditTrail(state.auditTrail);
        updateUndoButton();
        return;
      }
      if (state.scanCounts && Object.keys(state.scanCounts).length > 0) {
        mergeCounts(state.scanCounts);
      }
      updateDraftHint(state);
      renderAuditTrail(state.auditTrail);
    } catch (_) { /* ignore */ }
  };

  const setReadOnlyUi = (state) => {
    readOnly = true;
    scanBtn?.setAttribute('disabled', 'disabled');
    manualInput?.setAttribute('disabled', 'disabled');
    manualBtn?.setAttribute('disabled', 'disabled');
    undoBtn?.setAttribute('hidden', 'hidden');
    submitBtn?.setAttribute('hidden', 'hidden');
    if (reopenForm) reopenForm.hidden = false;
    const hint = document.querySelector('.scan-session-hint');
    if (hint) {
      const when = state?.submittedAtUtc
        ? new Date(state.submittedAtUtc).toLocaleString()
        : '';
      const who = formatActor(state?.submittedByName, state?.submittedByEmail);
      hint.textContent = when
        ? `Submitted ${when} by ${who} — reopen below to scan again.`
        : `Submitted by ${who} — reopen below to scan again.`;
    }
    lineRows().forEach((row) => syncRowVisuals(row));
  };

  const updateProgress = () => {
    const total = totalLines();
    const withScan = linesWithScan();
    const scans = totalScans();
    if (sessionTotalEl) sessionTotalEl.textContent = String(scans);
    if (linesProgressEl) {
      linesProgressEl.textContent =
        total > 0 ? `${withScan} of ${total} lines scanned` : '';
    }
    updateUndoButton();
  };

  const syncRowVisuals = (row) => {
    const code = (row.dataset.itemCode || '').trim();
    const count = scanCounts[code] || 0;
    const tick = row.querySelector('.scan-line-tick');
    row.classList.toggle('scan-line-scanned', count > 0);
    if (tick) {
      tick.textContent = count > 1 ? String(count) : count > 0 ? '✓' : '';
      tick.classList.toggle('is-done', count > 0);
      tick.classList.toggle('is-actionable', count > 0 && !readOnly);
      if (count > 0 && !readOnly) {
        tick.setAttribute('title', `Remove one scan (${count} on this line)`);
        tick.setAttribute('role', 'button');
        tick.setAttribute('tabindex', '0');
      } else {
        tick.removeAttribute('title');
        tick.removeAttribute('role');
        tick.removeAttribute('tabindex');
      }
    }
  };

  const applyAllRowVisuals = () => {
    lineRows().forEach(syncRowVisuals);
    updateProgress();
  };

  const unscanLine = (row, opts = {}) => {
    if (readOnly) return false;
    const code = (row.dataset.itemCode || '').trim();
    const n = scanCounts[code] || 0;
    if (n <= 0) return false;

    if (n === 1) delete scanCounts[code];
    else scanCounts[code] = n - 1;

    saveLocalSession();
    scheduleDraftSave();
    syncRowVisuals(row);
    updateProgress();

    if (!opts.quiet) {
      showToast(
        n === 1 ? `Removed scan for ${code}.` : `Removed one scan for ${code} (${n - 1} left).`,
        'info'
      );
    }
    return true;
  };

  const recordScan = (row, opts = {}) => {
    if (readOnly) return;
    const code = (row.dataset.itemCode || '').trim();
    if (!code) return;
    scanCounts[code] = (scanCounts[code] || 0) + 1;
    if (!opts.skipHistory) scanHistory.push({ code });
    saveLocalSession();
    scheduleDraftSave();
    syncRowVisuals(row);

    if (!opts.quietFlash) {
      row.classList.remove('scan-line-match-flash');
      void row.offsetWidth;
      row.classList.add('scan-line-match-flash');
      setTimeout(() => row.classList.remove('scan-line-match-flash'), 900);
    }

    if (!opts.noFeedback) playMatchFeedback();
    updateProgress();
  };

  const undoLastScan = () => {
    if (readOnly) return;
    while (scanHistory.length > 0) {
      const entry = scanHistory.pop();
      const code = entry?.code;
      if (!code) continue;
      const row = lineRows().find((r) =>
        ScanResolve.codesMatch(code, r.dataset.itemCode)
      );
      if (row && (scanCounts[(row.dataset.itemCode || '').trim()] || 0) > 0) {
        unscanLine(row, { quiet: true });
        showToast(`Undid last scan (${code}).`, 'info');
        updateUndoButton();
        return;
      }
    }
    showToast('Nothing to undo.', 'warn');
    updateUndoButton();
  };

  const getOfflineQueue = () => {
    if (!offlineKey) return [];
    try {
      const raw = localStorage.getItem(offlineKey);
      return raw ? JSON.parse(raw) : [];
    } catch (_) {
      return [];
    }
  };

  const setOfflineQueue = (items) => {
    if (!offlineKey) return;
    try {
      if (!items.length) localStorage.removeItem(offlineKey);
      else localStorage.setItem(offlineKey, JSON.stringify(items));
    } catch (_) { /* ignore */ }
  };

  const enqueueOffline = (raw) => {
    const q = getOfflineQueue();
    q.push({ raw: String(raw || '').trim(), at: Date.now() });
    setOfflineQueue(q);
    showToast(messageForScanError('offline_queued'), 'warn');
  };

  const flushOfflineQueue = async () => {
    if (!navigator.onLine) return;
    const q = getOfflineQueue();
    if (!q.length) return;
    const remaining = [];
    let processed = 0;
    for (const item of q) {
      const ok = await processScan(item.raw, { fromQueue: true, silent: true });
      if (!ok) remaining.push(item);
      else processed += 1;
    }
    setOfflineQueue(remaining);
    if (processed > 0) {
      showToast(
        processed === 1 ? 'Queued scan processed.' : `${processed} queued scans processed.`,
        'ok'
      );
    }
    if (remaining.length > 0) {
      showToast(
        `${remaining.length} queued scan(s) still waiting (could not resolve yet).`,
        'warn'
      );
    }
  };

  const classifyResolveError = (resolved) => {
    const code = (resolved.errorCode || resolved.ErrorCode || '').trim();
    if (code) return code;
    const err = String(resolved.error || resolved.Error || '').trim();
    if (!err) return 'no_code_on_page';
    if (/empty scan/i.test(err)) return 'empty_scan';
    if (/could not read link|resolve failed/i.test(err)) return 'resolve_failed';
    if (/no item code|none of this po/i.test(err)) return 'no_code_on_page';
    return 'resolve_failed';
  };

  const processScan = async (raw, opts = {}) => {
    const trimmed = String(raw || '').trim();
    if (!trimmed) return false;
    if (readOnly) {
      if (!opts.silent) showScanError('already_submitted');
      return false;
    }

    let resolved;
    try {
      if (!resolveUrl) throw new Error('Resolve API not configured.');
      resolved = await ScanResolve.resolve(resolveUrl, trimmed, knownItemCodes());
    } catch (e) {
      if (!navigator.onLine || (e && e.name === 'TypeError')) {
        enqueueOffline(trimmed);
        return false;
      }
      if (!opts.silent) {
        showScanError('resolve_failed', e.message || 'Network error');
      }
      return false;
    }

    const code = (resolved.itemCode || resolved.ItemCode || '').trim();
    const err = resolved.error || resolved.Error;

    if (err && !code) {
      if (!opts.silent) {
        showScanError(classifyResolveError(resolved), err);
      }
      return false;
    }

    if (!code) {
      if (!opts.silent) showScanError('no_code_on_page');
      return false;
    }

    const matchRow = lineRows().find((row) => ScanResolve.codesMatch(code, row.dataset.itemCode));
    if (matchRow) {
      recordScan(matchRow, { noFeedback: opts.fromQueue });
      if (!opts.silent && !opts.fromQueue) {
        showToast(`Matched: ${code}`, 'ok');
      }
      return true;
    }

    if (!opts.silent) showScanError('not_on_po', code);
    return false;
  };

  const qr = typeof ScanQrScanner !== 'undefined'
    ? ScanQrScanner.create({
        closeOnDetect: false,
        onDetected(text, meta) {
          const t = (text || '').trim();
          if (!t) return;
          void processScan(t).then((ok) => {
            if (!meta?.keepOpen && ok) qr.close();
          });
        },
      })
    : null;

  if (qr && scanBtn) qr.bindButton(scanBtn);

  const runManual = () => {
    const code = (manualInput?.value || '').trim();
    if (!code) {
      showToast('Enter an item code.', 'warn');
      return;
    }
    void processScan(code);
    if (manualInput) manualInput.value = '';
  };

  manualBtn?.addEventListener('click', runManual);
  manualInput?.addEventListener('keydown', (e) => {
    if (e.key === 'Enter') {
      e.preventDefault();
      runManual();
    }
  });

  undoBtn?.addEventListener('click', () => undoLastScan());

  if (linesTableBody) {
    linesTableBody.addEventListener('click', (e) => {
      const tick = e.target.closest('.scan-line-tick.is-actionable');
      if (!tick || readOnly) return;
      const row = tick.closest('tr[data-item-code]');
      if (row) unscanLine(row);
    });
    linesTableBody.addEventListener('keydown', (e) => {
      if (e.key !== 'Enter' && e.key !== ' ') return;
      const tick = e.target.closest('.scan-line-tick.is-actionable');
      if (!tick || readOnly) return;
      e.preventDefault();
      const row = tick.closest('tr[data-item-code]');
      if (row) unscanLine(row);
    });
  }

  if (submitForm && scanCountsInput) {
    submitForm.addEventListener('submit', (e) => {
      const total = totalScans();
      const lines = totalLines();
      const withScan = linesWithScan();

      if (total < 1) {
        e.preventDefault();
        showToast('Scan at least one line before submit.', 'error');
        return;
      }

      let msg = `Submit with ${withScan} of ${lines} lines scanned (${total} scan${total !== 1 ? 's' : ''})?`;
      if (requireAllLines && withScan < lines) {
        msg =
          `Only ${withScan} of ${lines} lines scanned.\n\nSubmit anyway?`;
      }

      if (!window.confirm(msg)) {
        e.preventDefault();
        return;
      }

      scanCountsInput.value = JSON.stringify(scanCounts);
      if (storageKey) {
        try {
          sessionStorage.removeItem(storageKey);
        } catch (_) { /* ignore */ }
      }
      if (offlineKey) {
        try {
          localStorage.removeItem(offlineKey);
        } catch (_) { /* ignore */ }
      }
    });
  }

  const init = async () => {
    await loadServerState();
    if (!readOnly) loadLocalSession();
    applyAllRowVisuals();
    await flushOfflineQueue();
  };

  window.addEventListener('online', () => {
    void flushOfflineQueue();
    void persistDraft();
    showToast('Back online.', 'ok');
  });

  void init();
})();
