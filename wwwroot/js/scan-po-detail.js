/* scan-po-detail.js — scan PO lines: QR/manual, draft sync, submit rules, offline queue */
(function () {
  'use strict';

  const scanBtn = document.getElementById('scanDetailScanBtn');
  const manualInput = document.getElementById('scanManualCode');
  const manualBtn = document.getElementById('scanManualBtn');
  const undoBtn = document.getElementById('scanUndoLastBtn');
  const resetBtn = document.getElementById('scanResetBtn');
  const cfg = document.getElementById('scanDetailConfig');
  if (!cfg || typeof ScanResolve === 'undefined') return;

  const resolveUrl = cfg.dataset.resolveScanUrl || '';
  const scanStateUrl = cfg.dataset.scanStateUrl || '';
  const saveDraftUrl = cfg.dataset.saveDraftUrl || '';
  const resetScanUrl = cfg.dataset.resetScanUrl || '';
  const docKey = String(cfg.dataset.docKey || new URLSearchParams(location.search).get('docKey') || '');
  const poNumber = cfg.dataset.poNumber || '';
  const isSubmitted = cfg.dataset.isSubmitted === 'true';
  const storageKey = docKey ? `approvalpo-scan-lines-v2-${docKey}` : '';
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
  const statusBar = document.getElementById('scanStatusBar');
  const statusMain = document.getElementById('scanStatusMain');
  const statusMeta = document.getElementById('scanStatusMeta');
  const unmatchedWrap = document.getElementById('scanUnmatchedWrap');
  const unmatchedBody = document.getElementById('scanUnmatchedBody');
  const unmatchedClearBtn = document.getElementById('scanUnmatchedClear');
  const listenHint = document.getElementById('scanListenHint');
  const wedgeDebugEl = document.getElementById('scanWedgeDebug');
  const debugArmedEl = document.getElementById('scanDebugArmed');
  const debugFocusEl = document.getElementById('scanDebugFocus');
  const debugBufferEl = document.getElementById('scanDebugBuffer');
  const debugLastFlushEl = document.getElementById('scanDebugLastFlush');
  const debugLogEl = document.getElementById('scanDebugLog');
  let statusResetTimer = null;
  let inventoryArmed = false;
  const manualPlaceholderDefault =
    (manualInput && manualInput.getAttribute('placeholder')) || 'Type item code';
  const MANUAL_PLACEHOLDER_LISTENING = 'Scan tags here (multi OK)…';
  /** @type {Set<string>} */
  const unmatchedKeys = new Set();
  /** @type {string[]} */
  const wedgeDebugLines = [];
  let wedgeBuf = '';
  let lastWedgeFlush = '';
  let lastWedgeCodes = [];
  let captureIdleTimer = null;
  let captureRefocusTimer = null;

  if (docKey && typeof ScanResolve.clearCachedForDoc === 'function') {
    ScanResolve.clearCachedForDoc(docKey);
  }

  /** @type {Record<string, number>} */
  let scanCounts = {};
  /** @type {{ lineKey: string, code: string, project: string, count: number }[]} */
  let scanHistory = [];
  let draftTimer = null;
  let readOnly = isSubmitted;
  let submitInProgress = false;

  const lineRows = () =>
    Array.from(document.querySelectorAll('.scan-lines-table tbody tr[data-line-no]'));

  const lineKey = (row) => `L:${String(row?.dataset?.lineNo || '').trim()}`;

  const orderQty = (row) => {
    const n = Number(row?.dataset?.orderQty);
    return Number.isFinite(n) && n > 0 ? n : 0;
  };

  const receivedQty = (row) => {
    const key = lineKey(row);
    return scanCounts[key] || 0;
  };

  const normalizeReceived = (row, raw) => {
    let n = Number(raw);
    if (!Number.isFinite(n) || n < 0) n = 0;
    n = Math.round(n * 100) / 100;
    const max = orderQty(row);
    if (max > 0 && n > max) n = max;
    return n;
  };

  const projectNorm = (s) => {
    const t = String(s ?? '').trim().replace(/\s+/g, '');
    // SQL Accounting empty-project placeholders (e.g. PH_PODTL.PROJECT = '----')
    if (!t || t === '-' || t === '----' || t === '—' || t === '–' || t === '―')
      return '';
    return t;
  };

  const projectMatches = (rowProject, scanProject) => {
    const a = projectNorm(rowProject);
    const b = projectNorm(scanProject);
    if (!b) return !a;
    return a.toLowerCase() === b.toLowerCase();
  };

  const findRowByLineNo = (lineNo) =>
    lineRows().find((r) => String(r.dataset.lineNo) === String(lineNo));

  const findRowsByItemProject = (code, project) =>
    lineRows().filter(
      (r) =>
        ScanResolve.codesEqual(code, r.dataset.itemCode) &&
        projectMatches(r.dataset.project, project)
    );

  const pickScanRow = (code, project, lineNo) => {
    const candidates = findRowsByItemProject(code, project);
    if (!candidates.length) return null;

    if (lineNo != null) {
      const byNo = candidates.find((r) => String(r.dataset.lineNo) === String(lineNo));
      if (byNo && receivedQty(byNo) === 0) return byNo;
    }

    return candidates.find((r) => receivedQty(r) === 0) || candidates[0];
  };

  const shortCode = (code) => {
    const c = String(code || '').trim();
    if (c.length <= 16) return c;
    return '…' + c.slice(-8);
  };

  const remainingLines = () =>
    lineRows().filter((row) => {
      const po = orderQty(row);
      const got = receivedQty(row);
      if (po > 0) return got < po;
      return got <= 0;
    }).length;

  const scrollRowIntoView = (row) => {
    if (!row || typeof row.scrollIntoView !== 'function') return;
    try {
      // Instant scroll while listening — smooth scroll can steal focus from Item code mid-burst.
      row.scrollIntoView({
        block: 'nearest',
        behavior: inventoryArmed ? 'auto' : 'smooth',
      });
    } catch (_) {
      row.scrollIntoView(false);
    }
  };

  const updateRemainingMeta = () => {
    if (statusMeta) statusMeta.textContent = `${remainingLines()} remaining`;
  };

  const setStatusBar = (state, mainText, metaText, opts = {}) => {
    updateRemainingMeta();
    if (!statusBar) return;
    statusBar.dataset.state = state || 'ready';
    if (statusMain) statusMain.textContent = mainText || 'Ready to scan';
    if (statusBar.getAttribute('aria-pressed') != null)
      statusBar.setAttribute('aria-pressed', inventoryArmed ? 'true' : 'false');
    clearTimeout(statusResetTimer);
    const sticky =
      opts.sticky === true ||
      state === 'ready' ||
      state === 'armed' ||
      state === 'done';
    if (!sticky && state) {
      statusResetTimer = setTimeout(() => {
        if (readOnly) return;
        refreshIdleStatusBar();
      }, 2200);
    }
  };

  const refreshIdleStatusBar = () => {
    updateRemainingMeta();
    if (!statusBar) return;
    if (readOnly) {
      setStatusBar('done', 'Submitted', `${remainingLines()} remaining`, { sticky: true });
      return;
    }
    if (inventoryArmed) {
      setStatusBar('armed', 'Listening… tap to pause', `${remainingLines()} remaining`, { sticky: true });
      return;
    }
    setStatusBar('ready', 'Tap to resume scanning', `${remainingLines()} remaining`, { sticky: true });
  };

  const focusDebug = () => {
    const el = document.activeElement;
    if (!el || el === document.body) return 'body';
    const id = el.id ? `#${el.id}` : '';
    const cls = el.className && typeof el.className === 'string'
      ? '.' + el.className.trim().split(/\s+/).slice(0, 2).join('.')
      : '';
    return `${(el.tagName || '?').toLowerCase()}${id}${cls}`.slice(0, 80);
  };

  const refreshWedgeDebug = () => {
    if (debugArmedEl) debugArmedEl.textContent = inventoryArmed ? 'yes' : 'no';
    if (debugFocusEl) debugFocusEl.textContent = focusLabel();
    if (debugBufferEl) {
      const cap = inventoryArmed && manualInput ? String(manualInput.value || '') : '';
      debugBufferEl.textContent = wedgeBuf || cap || '(empty)';
    }
    if (debugLastFlushEl) {
      debugLastFlushEl.textContent = lastWedgeFlush
        ? `${lastWedgeFlush} → [${lastWedgeCodes.join(', ') || '—'}]`
        : '(none)';
    }
    if (debugLogEl) debugLogEl.textContent = wedgeDebugLines.join('\n') || '(no events yet)';
  };

  const wedgeDebugLog = (msg) => {
    const stamp = new Date().toISOString().slice(11, 23);
    wedgeDebugLines.push(`${stamp} ${msg}`);
    while (wedgeDebugLines.length > 30) wedgeDebugLines.shift();
    refreshWedgeDebug();
  };

  const clearUnmatched = () => {
    unmatchedKeys.clear();
    if (unmatchedBody) unmatchedBody.innerHTML = '';
    if (unmatchedWrap) unmatchedWrap.hidden = true;
  };

  const addUnmatchedCodes = (codes) => {
    if (!unmatchedBody || !unmatchedWrap) return;
    let added = 0;
    for (const raw of codes || []) {
      const code = String(raw || '').trim();
      if (!code) continue;
      const key = code.toUpperCase();
      if (unmatchedKeys.has(key)) continue;
      unmatchedKeys.add(key);
      added += 1;
      const tr = document.createElement('tr');
      const tdN = document.createElement('td');
      tdN.textContent = String(unmatchedKeys.size);
      const tdCode = document.createElement('td');
      tdCode.className = 'scan-unmatched-code';
      tdCode.title = code;
      tdCode.textContent = shortCode(code);
      const tdNote = document.createElement('td');
      tdNote.className = 'scan-unmatched-note';
      tdNote.textContent = 'Not on this PO';
      tr.append(tdN, tdCode, tdNote);
      unmatchedBody.appendChild(tr);
    }
    if (added > 0 || unmatchedKeys.size > 0) unmatchedWrap.hidden = false;
  };

  const stopCaptureRefocus = () => {
    clearTimeout(captureRefocusTimer);
    captureRefocusTimer = null;
  };

  const focusManualCapture = () => {
    if (!manualInput || readOnly || !inventoryArmed || manualInput.disabled) return;
    try {
      manualInput.scrollIntoView({ block: 'center', behavior: 'smooth' });
    } catch (_) {
      try { manualInput.scrollIntoView(true); } catch (__) { /* ignore */ }
    }
    try {
      manualInput.focus({ preventScroll: false });
    } catch (_) {
      try { manualInput.focus(); } catch (__) { /* ignore */ }
    }
    // Do not select() — that races HID keystrokes and drops RFID characters.
  };

  const scheduleManualCaptureFocus = () => {
    const run = () => focusManualCapture();
    if (typeof requestAnimationFrame === 'function') {
      requestAnimationFrame(() => setTimeout(run, 40));
      return;
    }
    setTimeout(run, 50);
  };

  const applyManualListeningUi = (armed) => {
    if (!manualInput) return;
    if (armed) {
      manualInput.classList.add('scan-manual-input--listening');
      manualInput.setAttribute('placeholder', MANUAL_PLACEHOLDER_LISTENING);
      return;
    }
    manualInput.classList.remove('scan-manual-input--listening');
    manualInput.setAttribute('placeholder', manualPlaceholderDefault);
    manualInput.value = '';
  };

  /** True only when idle flush is safe (complete EPC / multi dump). Never mid-stream. */
  const isBufferCompleteForIdleFlush = (text) => {
    const t = String(text || '').trim();
    if (t.length < 3) return false;
    if (t.includes(';') || /[\s,|]/.test(t)) return true;
    if (/^[A-Fa-f0-9]+$/.test(t)) {
      // Complete single EPC (24) or glued multiples of 24.
      return t.length >= 24 && t.length % 24 === 0;
    }
    // Non-hex stock codes: require a pause after a substantial token (Enter is preferred).
    return t.length >= 8;
  };

  // Declared early; assigned after processScan / enqueueWedgeCodes exist.
  let processManualCodes = async (_raw) => false;
  let enqueueWedgeCodes = (_raw) => {};

  const flushManualCapture = (opts) => {
    const force = !!(opts && opts.force);
    clearTimeout(captureIdleTimer);
    captureIdleTimer = null;
    if (!manualInput) return;
    const text = String(manualInput.value || '').trim();
    // Never flush truncated hex EPCs — wait for more keystrokes or Enter.
    if (!force && !isBufferCompleteForIdleFlush(text)) return;
    if (force && /^[A-Fa-f0-9]+$/.test(text) && text.length < 24 && !/[\s,|;]/.test(text)) {
      wedgeDebugLog(`manual skip truncated hex (${text.length}): ${text}`);
      return;
    }
    manualInput.value = '';
    refreshWedgeDebug();
    if (readOnly || text.length < 3) {
      if (text) wedgeDebugLog(`manual ignored short/readonly: ${text}`);
      return;
    }
    lastWedgeFlush = text;
    lastWedgeCodes = typeof ScanResolve.splitInventoryCodes === 'function'
      ? ScanResolve.splitInventoryCodes(text)
      : [text];
    wedgeDebugLog(`manual flush "${text}" → ${lastWedgeCodes.length} code(s)`);
    refreshWedgeDebug();
    // Listening: enqueue into the burst window — do not match the first tag alone.
    if (inventoryArmed) {
      enqueueWedgeCodes(text);
      scheduleManualCaptureFocus();
      return;
    }
    void processManualCodes(text);
  };

  const setInventoryArmed = (armed, opts = {}) => {
    if (readOnly) return;
    stopCaptureRefocus();
    inventoryArmed = !!armed;
    if (listenHint) listenHint.hidden = !inventoryArmed;
    applyManualListeningUi(inventoryArmed);
    refreshIdleStatusBar();
    if (inventoryArmed) {
      if (!opts.quiet) showToast('Listening — scan Item code', 'ok');
      scheduleManualCaptureFocus();
    } else {
      if (!opts.quiet) showToast('Paused', 'warn');
      clearTimeout(captureIdleTimer);
      // Flush any tags already heard in this burst before pausing.
      if (batchCodes.length || batchTimer) flushInventoryBatch();
    }
    wedgeDebugLog(inventoryArmed ? 'armed' : 'disarmed');
    refreshWedgeDebug();
  };

  let lastArmToggleAt = 0;
  const TOGGLE_DEBOUNCE_MS = 350;

  const toggleInventoryArmed = () => {
    if (readOnly) return;
    const now = Date.now();
    if (now - lastArmToggleAt < TOGGLE_DEBOUNCE_MS) return;
    lastArmToggleAt = now;
    setInventoryArmed(!inventoryArmed);
  };

  const playAlreadyFeedback = () => {
    try {
      if (navigator.vibrate) navigator.vibrate([20, 40, 20]);
    } catch (_) { /* ignore */ }
  };

  const lineAlreadyComplete = (row) => {
    const po = orderQty(row);
    const got = receivedQty(row);
    if (po > 0) return got >= po;
    return got > 0;
  };

  /** Apply a matched PO line: green Received, or soft Already received. */
  const confirmLineMatch = (row, code, qtyRaw, opts = {}) => {
    if (!row) return { ok: false };
    const display = shortCode(code || row.dataset.itemCode || '');
    if (!opts.skipScroll) scrollRowIntoView(row);

    if (lineAlreadyComplete(row)) {
      if (!opts.silent && !opts.fromQueue) {
        showToast(`Already received: ${display}`, 'warn');
        playAlreadyFeedback();
        setStatusBar('already', 'Already received', display);
      }
      return { ok: true, already: true, row, qty: receivedQty(row) };
    }

    const qty =
      qtyRaw != null && Number(qtyRaw) > 0
        ? normalizeReceived(row, Number(qtyRaw))
        : orderQty(row) || 1;

    setReceivedQty(row, qty, {
      noFeedback: opts.fromQueue || opts.noFeedback,
      skipHistory: opts.fromQueue || opts.skipHistory,
      quietFlash: opts.quietFlash,
    });
    if (!opts.skipScroll) scrollRowIntoView(row);

    if (!opts.silent && !opts.fromQueue) {
      const proj = projectNorm(row.dataset.project || opts.project || '');
      const locPart = proj ? ` @ ${proj}` : '';
      showToast(`Matched: ${display}${locPart} — received ${qty}`, 'ok');
      setStatusBar('ok', `Received ${qty}`, display);
    } else {
      refreshIdleStatusBar();
    }
    return { ok: true, already: false, row, qty };
  };

  const setReceivedQty = (row, rawQty, opts = {}) => {
    if (readOnly || !row) return 0;
    const key = lineKey(row);
    if (!key || key === 'L:') return 0;
    const prev = scanCounts[key] || 0;
    const n = normalizeReceived(row, rawQty);
    if (n <= 0) delete scanCounts[key];
    else scanCounts[key] = n;

    if (!opts.skipHistory && n !== prev) {
      scanHistory.push({
        lineKey: key,
        code: (row.dataset.itemCode || '').trim(),
        project: (row.dataset.project || '').trim(),
        prevQty: prev,
        count: n,
      });
    }

    saveLocalSession();
    scheduleDraftSave();
    syncRowVisuals(row);

    if (!opts.quietFlash && n > 0 && n !== prev) {
      row.classList.remove('scan-line-match-flash');
      void row.offsetWidth;
      row.classList.add('scan-line-match-flash');
      setTimeout(() => row.classList.remove('scan-line-match-flash'), 900);
    }

    if (!opts.noFeedback && n > 0 && n !== prev) playMatchFeedback();
    updateProgress();
    return n;
  };

  const applyParsedBarcode = (local, opts = {}) => {
    const proj = (local.location || '').trim();
    if (!projectNorm(proj)) return false;

    const row = pickScanRow(local.itemCode, proj, null);
    if (!row) return false;

    const result = confirmLineMatch(row, local.itemCode, local.quantity, {
      ...opts,
      project: proj,
      silent: true,
      noFeedback: true,
    });
    return result.ok ? { row, qty: result.qty, proj } : false;
  };

  const tryLocalBarcodeScan = (raw, opts = {}) => {
    const trimmed = String(raw || '').trim();
    if (!trimmed.includes(';')) return null;

    const allParsed = typeof ScanResolve.parseSemicolonPayloadAll === 'function'
      ? ScanResolve.parseSemicolonPayloadAll(trimmed, knownItemCodes())
      : [ScanResolve.parseSemicolonPayload(trimmed, knownItemCodes())].filter(Boolean);

    if (!allParsed.length) return null;

    if (allParsed.length > 1) {
      const hits = [];
      for (const local of allParsed) {
        const hit = applyParsedBarcode(local, opts);
        if (hit) hits.push(hit);
      }
      if (hits.length > 0) {
        if (!opts.silent && !opts.fromQueue) {
          showToast(`Matched ${hits.length} PO line(s) from QR`, 'ok');
          setStatusBar('ok', `Matched ${hits.length}`, `${remainingLines()} remaining`);
        }
        return { handled: true, ok: true };
      }
      if (!opts.silent) {
        showScanError('not_on_po', 'None of the QR lines match this PO.');
      }
      return { handled: true, ok: false };
    }

    const local = allParsed[0];
    const proj = (local.location || '').trim();
    if (!projectNorm(proj)) {
      if (!opts.silent) {
        showScanError('need_project', 'Scan a label that includes the project (P1, P2, …).');
      }
      return { handled: true, ok: false };
    }

    const row = pickScanRow(local.itemCode, proj, null);
    if (!row) {
      if (!opts.silent) {
        showScanError('not_on_po', `${shortCode(local.itemCode)} @ ${proj} is not on this PO.`);
      }
      return { handled: true, ok: false };
    }

    const result = confirmLineMatch(row, local.itemCode, local.quantity, {
      ...opts,
      project: proj,
    });
    return { handled: true, ok: result.ok };
  };

  const knownItemCodes = () =>
    lineRows()
      .map((row) => (row.dataset.itemCode || '').trim())
      .filter(Boolean);

  const totalLines = () => lineRows().length;

  const linesWithScan = () =>
    lineRows().filter((row) => receivedQty(row) > 0).length;

  const totalScans = () =>
    lineRows().reduce((sum, row) => sum + receivedQty(row), 0);

  const esc = (s) => String(s ?? '').replace(/[&<>"']/g, (c) =>
    ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

  const formatActor = (name, email) => {
    const n = String(name || '').trim();
    const e = String(email || '').trim();
    if (n && e && n.toLowerCase() !== e.toLowerCase()) return `${n} (${e})`;
    return n || e || 'Unknown user';
  };

  const actionLabel = (action) =>
    ({ submitted: 'Submitted', reopened: 'Reopened', draft_saved: 'Draft saved', scan_reset: 'Scan reset' }[action] || action);

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
    const undoTip = ' Clear Received on a line to remove it.';
    if (!who || who === 'Unknown user') {
      hint.textContent = `Draft saves to server; submit creates Goods Received from Received qty.${undoTip}`;
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
      case 'need_project':
        return d || 'This item appears on multiple project lines. Scan a QR that includes P1, P2, etc.';
      case 'not_on_po':
        if (/^[A-Fa-f0-9]{16,}$/i.test(d))
          return `Item ${shortCode(d)} is not on this purchase order.`;
        return d || 'That item / project is not on this purchase order.';
      case 'wrong_po':
        return d || 'This QR belongs to a different purchase order. Open the correct PO and scan again.';
      case 'po_not_found':
        return 'This purchase order is not found or not approved.';
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
    const label =
      code === 'not_on_po'
        ? 'Not on PO'
        : code === 'need_project'
          ? 'Need project'
          : code === 'already_submitted'
            ? 'Submitted'
            : 'Scan error';
    setStatusBar('error', label, shortCode(detail) || `${remainingLines()} remaining`);
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
    const canUndo = scanHistory.length > 0 || totalScans() > 0;
    undoBtn.hidden = false;
    undoBtn.disabled = !canUndo;
  };

  const updateResetButton = () => {
    if (!resetBtn || readOnly) return;
    const hasScans = totalScans() > 0;
    resetBtn.disabled = !hasScans;
  };

  const clearLocalScanSession = () => {
    scanCounts = {};
    scanHistory = [];
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
    if (docKey && typeof ScanResolve.clearCachedForDoc === 'function') {
      ScanResolve.clearCachedForDoc(docKey);
    }
    applyAllRowVisuals();
  };

  const resetScanSession = async () => {
    if (readOnly) {
      showScanError('already_submitted');
      return;
    }
    if (totalScans() < 1) {
      showToast('Nothing to reset.', 'warn');
      return;
    }
    const lines = totalLines();
    const withScan = linesWithScan();
    const msg =
      lines > 0
        ? `Clear all received qty for this PO (${withScan} of ${lines} lines)?`
        : 'Clear all received qty for this PO?';
    if (!window.confirm(msg)) return;

    clearLocalScanSession();
    clearUnmatched();
    updateUndoButton();
    updateResetButton();
    refreshIdleStatusBar();
    setInventoryArmed(true, { quiet: true });

    if (resetScanUrl && docKey && navigator.onLine) {
      try {
        const body = new URLSearchParams();
        body.set('docKey', docKey);
        const headers = { 'Content-Type': 'application/x-www-form-urlencoded' };
        if (csrfToken) headers['X-CSRF-TOKEN'] = csrfToken;
        const res = await fetch(resetScanUrl, {
          method: 'POST',
          credentials: 'same-origin',
          headers,
          body: body.toString(),
        });
        const data = await res.json().catch(() => ({}));
        if (!res.ok || !data.ok) {
          showToast(data.error || 'Could not reset on server.', 'error');
          return;
        }
      } catch (_) {
        showToast('Reset locally; server sync failed (offline?).', 'warn');
        return;
      }
    }

    showToast('Received quantities cleared.', 'ok');
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
    resetBtn?.setAttribute('hidden', 'hidden');
    submitBtn?.setAttribute('hidden', 'hidden');
    document.querySelectorAll('.scan-received-input').forEach((el) => {
      el.setAttribute('disabled', 'disabled');
    });
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
        total > 0 ? `${withScan} of ${total} lines with received qty` : '';
    }
    if (statusBar) {
      const state = statusBar.dataset.state || 'ready';
      if (state === 'ready' || state === 'armed' || state === 'done' || !state) {
        refreshIdleStatusBar();
      } else {
        updateRemainingMeta();
      }
    } else {
      updateRemainingMeta();
    }
    updateUndoButton();
    updateScanButton();
    updateResetButton();
  };

  const updateScanButton = () => {
    if (!scanBtn || readOnly) return;
    const total = totalLines();
    const allDone = requireAllLines && total > 0 && linesWithScan() >= total;
    if (allDone) {
      scanBtn.setAttribute('disabled', 'disabled');
      scanBtn.setAttribute('title', 'All lines have received qty');
    } else {
      scanBtn.removeAttribute('disabled');
      scanBtn.removeAttribute('title');
    }
  };

  const canOpenScanner = () => {
    if (readOnly) {
      showScanError('already_submitted');
      return false;
    }
    if (requireAllLines && totalLines() > 0 && linesWithScan() >= totalLines()) {
      showToast('All lines already have received qty.', 'ok');
      return false;
    }
    return true;
  };

  const syncRowVisuals = (row) => {
    const qty = receivedQty(row);
    const po = orderQty(row);
    const input = row.querySelector('.scan-received-input');
    const readonly = row.querySelector('.scan-received-readonly');

    row.classList.toggle('scan-line-received', qty > 0);
    row.classList.toggle('scan-line-partial', qty > 0 && po > 0 && qty < po);
    row.classList.toggle('scan-line-over', po > 0 && qty > po);

    if (input && document.activeElement !== input) {
      input.value = qty > 0 ? String(qty) : '';
    }
    if (readonly) {
      readonly.textContent = qty > 0 ? String(qty) : '—';
    }
  };

  const applyAllRowVisuals = () => {
    lineRows().forEach(syncRowVisuals);
    updateProgress();
  };

  /** Read editable Received inputs into scanCounts (submit must not rely on change/blur alone). */
  const syncReceivedFromDom = () => {
    if (readOnly) return;
    lineRows().forEach((row) => {
      const input = row.querySelector('.scan-received-input');
      if (!input) return;
      const key = lineKey(row);
      if (!key || key === 'L:') return;
      const raw = input.value.trim();
      if (!raw) {
        delete scanCounts[key];
        return;
      }
      const n = normalizeReceived(row, raw);
      if (n <= 0) delete scanCounts[key];
      else scanCounts[key] = n;
    });
    saveLocalSession();
    lineRows().forEach(syncRowVisuals);
    updateProgress();
  };

  const clearReceived = (row, opts = {}) => {
    if (readOnly) return false;
    if (receivedQty(row) <= 0) return false;
    setReceivedQty(row, 0, { skipHistory: true, quietFlash: true, noFeedback: true });
    if (!opts.quiet) {
      const code = (row.dataset.itemCode || '').trim();
      const proj = (row.dataset.project || '').trim();
      const label = proj ? `${code} (${proj})` : code;
      showToast(`Cleared received qty for ${label}.`, 'info');
    }
    return true;
  };

  const recordScan = (row, opts = {}) => {
    const qty =
      opts.incrementBy != null && Number(opts.incrementBy) > 0
        ? Number(opts.incrementBy)
        : orderQty(row) || 1;
    setReceivedQty(row, qty, opts);
  };

  const undoLastScan = () => {
    if (readOnly) return;
    while (scanHistory.length > 0) {
      const entry = scanHistory.pop();
      const key = entry?.lineKey;
      if (!key) continue;
      const row = lineRows().find((r) => lineKey(r) === key);
      if (!row) continue;
      const prev = entry?.prevQty != null ? entry.prevQty : 0;
      setReceivedQty(row, prev, { skipHistory: true, quietFlash: true, noFeedback: true });
      const proj = entry?.project || '';
      const label = proj ? `${entry.code} (${proj})` : (entry.code || shortCode(row.dataset.itemCode));
      showToast(`Undid change for ${label}.`, 'info');
      updateUndoButton();
      updateResetButton();
      return;
    }

    // Draft/reload: no history stack — clear the last line that still has received qty.
    const rows = lineRows();
    for (let i = rows.length - 1; i >= 0; i -= 1) {
      const row = rows[i];
      if (receivedQty(row) <= 0) continue;
      const code = (row.dataset.itemCode || '').trim();
      const proj = (row.dataset.project || '').trim();
      const label = proj ? `${code} (${proj})` : code;
      setReceivedQty(row, 0, { skipHistory: true, quietFlash: true, noFeedback: true });
      showToast(`Undid change for ${label}.`, 'info');
      updateUndoButton();
      updateResetButton();
      return;
    }

    showToast('Nothing to undo.', 'warn');
    updateUndoButton();
    updateResetButton();
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
    if (/need project|multiple project/i.test(err)) return 'need_project';
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

    const localResult = tryLocalBarcodeScan(trimmed, opts);
    if (localResult?.handled) return localResult.ok;

    let resolved;
    try {
      if (!resolveUrl) throw new Error('Resolve API not configured.');
      resolved = await ScanResolve.resolve(resolveUrl, trimmed, knownItemCodes(), { docKey });
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
    const lineNo = resolved.lineNo ?? resolved.LineNo;
    const scanProject = (resolved.scanLocation || resolved.ScanLocation || '').trim();
    const err = resolved.error || resolved.Error;

    if (err && !code) {
      if (!opts.silent) {
        showScanError(classifyResolveError(resolved), err);
      }
      return false;
    }

    if (err && code) {
      const local = ScanResolve.parseSemicolonPayload(trimmed, knownItemCodes());
      const localProject = (local?.location || '').trim();
      const localRow = local?.itemCode && projectNorm(localProject)
        ? pickScanRow(local.itemCode, localProject, null)
        : null;
      if (localRow) {
        const qtyRaw = local.quantity ?? resolved.scanQuantity ?? resolved.ScanQuantity;
        const result = confirmLineMatch(localRow, local.itemCode, qtyRaw, {
          ...opts,
          project: localProject,
        });
        return result.ok;
      }
      if (!opts.silent) {
        showScanError(classifyResolveError(resolved), err);
      }
      return false;
    }

    if (!code) {
      if (!opts.silent) showScanError('no_code_on_page');
      return false;
    }

    if (!projectNorm(scanProject)) {
      const sameItemRows = lineRows().filter((r) => ScanResolve.codesEqual(code, r.dataset.itemCode));
      if (sameItemRows.length > 1) {
        if (!opts.silent) {
          showScanError('need_project', 'This item is on multiple lines. Scan a label that includes P1, P2, …');
        }
        return false;
      }
    }

    let matchRow = pickScanRow(code, scanProject, lineNo);
    if (matchRow) {
      const qtyRaw = resolved.scanQuantity ?? resolved.ScanQuantity;
      const result = confirmLineMatch(matchRow, code, qtyRaw, {
        ...opts,
        project: scanProject,
      });
      return result.ok;
    }

    if (!opts.silent) showScanError('not_on_po', err || code);
    return false;
  };

  /** Item code path: split multi dumps, processScan each (restores working RFID), keep focus. */
  processManualCodes = async (raw) => {
    const parts = typeof ScanResolve.splitInventoryCodes === 'function'
      ? ScanResolve.splitInventoryCodes(raw)
      : [String(raw || '').trim()].filter(Boolean);
    if (!parts.length) return false;

    const multi = parts.length > 1;
    let matched = 0;
    let missed = 0;
    for (const code of parts) {
      const ok = await processScan(code, {
        silent: multi,
        noFeedback: multi,
        quietFlash: true,
      });
      if (ok) matched += 1;
      else {
        missed += 1;
        if (multi) addUnmatchedCodes([code]);
      }
    }

    if (manualInput) manualInput.value = '';
    if (inventoryArmed) scheduleManualCaptureFocus();
    else {
      try { manualInput?.focus(); } catch (_) { /* ignore */ }
    }

    if (multi) {
      if (matched > 0) {
        showToast(
          `Matched ${matched} tag(s)` + (missed ? `, ${missed} not on PO` : ''),
          missed ? 'warn' : 'ok'
        );
      } else if (missed > 0) {
        showToast(`${missed} tag(s) not on this PO`, 'warn');
      }
    }

    if (inventoryArmed) {
      clearTimeout(statusResetTimer);
      statusResetTimer = setTimeout(() => refreshIdleStatusBar(), 1200);
    }
    return matched > 0;
  };

  const qr = typeof ScanQrScanner !== 'undefined'
    ? ScanQrScanner.create({
        closeOnDetect: true,
        debounceMs: 600,
        onDetected(text) {
          const t = (text || '').trim();
          if (!t) return;
          void processScan(t).finally(() => {
            qr.close();
          });
        },
      })
    : null;

  if (qr && scanBtn) {
    scanBtn.addEventListener('click', () => {
      if (!canOpenScanner()) return;
      qr.open();
    });
  }

  const runManual = () => {
    if (readOnly) return;
    const text = (manualInput?.value || '').trim();
    if (!text) {
      showToast(inventoryArmed ? 'Scan a tag into Item code.' : 'Enter an item code.', 'warn');
      return;
    }
    // Go button: match immediately (typed codes, not a gun inventory burst).
    clearTimeout(captureIdleTimer);
    captureIdleTimer = null;
    if (manualInput) manualInput.value = '';
    void processManualCodes(text);
  };

  /** While listening, Enter/Tab enqueue into the burst — wait for more gun tags. */
  const enqueueArmedManualField = () => {
    if (readOnly || !manualInput) return;
    clearTimeout(captureIdleTimer);
    captureIdleTimer = null;
    const text = String(manualInput.value || '').trim();
    manualInput.value = '';
    refreshWedgeDebug();
    if (text.length >= 3) {
      lastWedgeFlush = text;
      lastWedgeCodes = typeof ScanResolve.splitInventoryCodes === 'function'
        ? ScanResolve.splitInventoryCodes(text)
        : [text];
      wedgeDebugLog(`armed enqueue "${text}" → ${lastWedgeCodes.length} code(s)`);
      refreshWedgeDebug();
      enqueueWedgeCodes(text);
    }
    scheduleManualCaptureFocus();
  };

  manualBtn?.addEventListener('click', runManual);
  manualInput?.addEventListener('keydown', (e) => {
    if (e.key === 'Enter' || e.key === 'Tab') {
      e.preventDefault();
      if (inventoryArmed) {
        enqueueArmedManualField();
        return;
      }
      runManual();
    }
  });
  manualInput?.addEventListener('input', () => {
    if (readOnly) return;
    refreshWedgeDebug();
    clearTimeout(captureIdleTimer);
    const text = String(manualInput?.value || '').trim();
    // Idle flush only when buffer looks complete — never mid-EPC.
    if (!isBufferCompleteForIdleFlush(text)) return;
    captureIdleTimer = setTimeout(() => flushManualCapture(), wedgeIdleMs());
  });
  manualInput?.addEventListener('blur', (e) => {
    if (!inventoryArmed || readOnly) return;
    const rt = e.relatedTarget;
    if (rt instanceof Element) {
      if (rt.id === 'scanActionsMenuBtn' || rt.id === 'scanUndoLastBtn' || rt.id === 'scanResetBtn' || rt.id === 'scanDetailSubmitBtn')
        return;
      if (rt.classList.contains('scan-meta-kebab-btn') || rt.classList.contains('scan-meta-menu-item') || rt.classList.contains('scan-detail-submit-btn'))
        return;
      if (rt.closest('.scan-meta-menu-panel'))
        return;
    }
    stopCaptureRefocus();
    captureRefocusTimer = setTimeout(() => {
      if (!inventoryArmed || readOnly) return;
      const ae = document.activeElement;
      if (ae && (ae.id === 'scanStatusBar' || ae.id === 'scanUnmatchedClear'))
        return;
      if (ae && ae.classList?.contains('scan-status-bar'))
        return;
      if (ae && (ae.id === 'scanActionsMenuBtn' || ae.id === 'scanUndoLastBtn' || ae.id === 'scanResetBtn' || ae.id === 'scanDetailSubmitBtn'))
        return;
      if (ae && (ae.classList?.contains('scan-meta-kebab-btn') || ae.classList?.contains('scan-meta-menu-item') || ae.classList?.contains('scan-detail-submit-btn')))
        return;
      if (ae && ae.closest?.('.scan-meta-menu-panel'))
        return;
      // While listening, reclaim focus even from Received qty so gun dumps aren't dropped.
      if (ae && ae.classList?.contains('scan-received-input') && !inventoryArmed)
        return;
      if (ae && (ae.id === 'scanManualBtn' || ae.classList?.contains('scan-manual-btn')))
        return;
      scheduleManualCaptureFocus();
    }, 50);
  });

  // RFID / barcode guns: HID keyboard wedge only (no RFID SDK in this app).
  // Gun model / inventory profile is firmware-side and not configured here.
  // Multi-tag: wedge batch-split — coalesce rapid Enter dumps, then match each EPC.
  // While Listening, #scanManualCode owns typing (same field that works for Item code).
  const isWedgeTypingField = (el) => {
    if (!el || el === document.body) return false;
    if (el.isContentEditable) return true;
    const tag = (el.tagName || '').toUpperCase();
    if (tag === 'TEXTAREA' || tag === 'SELECT') return true;
    if (tag !== 'INPUT') return false;
    const type = String(el.type || 'text').toLowerCase();
    if (['checkbox', 'radio', 'button', 'submit', 'reset', 'file', 'hidden', 'image'].includes(type))
      return false;
    // Manual item field owns typing (Listening capture + normal Item code).
    if (el.id === 'scanManualCode' || el.classList.contains('scan-manual-input'))
      return true;
    // While inventory listening, allow page-wide capture even from Received qty.
    if (inventoryArmed && el.classList.contains('scan-received-input'))
      return false;
    if (el.classList.contains('scan-received-input'))
      return true;
    return true;
  };

  let wedgeTimer = null;
  let batchCodes = [];
  let batchTimer = null;
  // Assemble one HID burst (one EPC typing stream).
  const WEDGE_IDLE_MS = 70;
  // Slower Bluetooth / inventory dumps: wait longer between keystrokes while Listening.
  const WEDGE_IDLE_ARMED_MS = 180;
  // After each code/Enter, wait for more inventory tags from the same trigger hold.
  const BATCH_IDLE_MS = 280;
  // Inventory dump: hold ~2s after the last tag before matching the whole burst.
  const BATCH_IDLE_ARMED_MS = 2000;

  const wedgeIdleMs = () => (inventoryArmed ? WEDGE_IDLE_ARMED_MS : WEDGE_IDLE_MS);
  const batchIdleMs = () => (inventoryArmed ? BATCH_IDLE_ARMED_MS : BATCH_IDLE_MS);

  const splitWedgeCodes = (raw) => {
    if (typeof ScanResolve.splitInventoryCodes === 'function')
      return ScanResolve.splitInventoryCodes(raw);
    const t = String(raw || '').trim();
    return t ? [t] : [];
  };

  const matchOneInventoryCode = (code) => {
    const row = pickScanRow(code, '', null);
    if (!row) return { ok: false, missed: true, code };
    const result = confirmLineMatch(row, code, null, {
      silent: true,
      noFeedback: true,
      quietFlash: true,
      skipScroll: true,
    });
    return {
      ok: result.ok,
      already: !!result.already,
      missed: false,
      row: result.row,
      code,
    };
  };

  const processInventoryBatch = async (codes) => {
    if (!codes.length) return false;

    let matched = 0;
    let already = 0;
    let missed = 0;
    let lastRow = null;
    const missedCodes = [];

    for (const code of codes) {
      if (code.includes(';') || (typeof ScanResolve.isUrl === 'function' && ScanResolve.isUrl(code))) {
        const ok = await processScan(code, {
          silent: true,
          noFeedback: true,
          quietFlash: true,
          skipScroll: true,
        });
        if (ok) matched += 1;
        else {
          missed += 1;
          missedCodes.push(code);
        }
        continue;
      }

      const hit = matchOneInventoryCode(code);
      if (hit.missed) {
        // Fall back to full processScan (API resolve) — restores pre-batch reliability.
        const ok = await processScan(code, {
          silent: true,
          noFeedback: true,
          quietFlash: true,
          skipScroll: true,
        });
        if (ok) {
          matched += 1;
          continue;
        }
        missed += 1;
        missedCodes.push(code);
        continue;
      }
      if (hit.already) already += 1;
      else if (hit.ok) {
        matched += 1;
        lastRow = hit.row;
      }
    }

    if (missedCodes.length) addUnmatchedCodes(missedCodes);
    // Keep Item code focused for the next gun dump; scroll after focus restore.
    if (inventoryArmed) focusManualCapture();
    if (lastRow) scrollRowIntoView(lastRow);
    wedgeDebugLog(
      `batch matched=${matched} already=${already} missed=${missed} codes=${codes.length}`
    );

    if (matched > 0) {
      playMatchFeedback();
      showToast(
        `Matched ${matched} tag(s)` +
          (already ? `, ${already} already received` : '') +
          (missed ? `, ${missed} not on PO` : ''),
        missed ? 'warn' : 'ok'
      );
      setStatusBar(
        inventoryArmed ? 'armed' : 'ok',
        `Matched ${matched}`,
        `${remainingLines()} remaining`,
        { sticky: inventoryArmed }
      );
      if (inventoryArmed) {
        clearTimeout(statusResetTimer);
        statusResetTimer = setTimeout(() => refreshIdleStatusBar(), 1200);
        scheduleManualCaptureFocus();
      }
      return true;
    }

    if (already > 0 && missed === 0) {
      playAlreadyFeedback();
      showToast(`Already received (${already})`, 'warn');
      setStatusBar('already', 'Already received', `${remainingLines()} remaining`);
      if (inventoryArmed) {
        clearTimeout(statusResetTimer);
        statusResetTimer = setTimeout(() => refreshIdleStatusBar(), 1200);
        scheduleManualCaptureFocus();
      }
      return true;
    }

    if (missedCodes.length) {
      showToast(`${missedCodes.length} tag(s) not on this PO`, 'warn');
      setStatusBar('error', 'Not on PO', `${remainingLines()} remaining`);
      if (inventoryArmed) {
        clearTimeout(statusResetTimer);
        statusResetTimer = setTimeout(() => refreshIdleStatusBar(), 1200);
        scheduleManualCaptureFocus();
      }
      return false;
    }

    showScanError('not_on_po', 'No tags matched');
    if (inventoryArmed) scheduleManualCaptureFocus();
    return false;
  };

  const flushInventoryBatch = () => {
    clearTimeout(batchTimer);
    batchTimer = null;
    const pending = batchCodes.splice(0, batchCodes.length);
    if (readOnly || !pending.length) return;
    const seen = new Set();
    const codes = [];
    for (const code of pending) {
      const key = String(code || '').trim().toUpperCase();
      if (!key || seen.has(key)) continue;
      seen.add(key);
      codes.push(code);
    }
    if (!codes.length) return;
    if (inventoryArmed) {
      setStatusBar(
        'armed',
        codes.length === 1
          ? 'Heard 1 tag (gun may be single-read)'
          : `Heard ${codes.length} tags`,
        `${remainingLines()} remaining`,
        { sticky: true }
      );
    }
    void processInventoryBatch(codes).finally(() => {
      if (inventoryArmed) scheduleManualCaptureFocus();
    });
  };

  enqueueWedgeCodes = (raw) => {
    const parts = splitWedgeCodes(raw);
    if (!parts.length) return;
    for (const part of parts) batchCodes.push(part);
    clearTimeout(batchTimer);
    batchTimer = setTimeout(flushInventoryBatch, batchIdleMs());
    if (inventoryArmed && batchCodes.length > 0) {
      updateRemainingMeta();
      wedgeDebugLog(`burst pending=${batchCodes.length} (wait ${batchIdleMs()}ms)`);
    }
  };

  const flushWedgeBuffer = (opts) => {
    const force = !!(opts && opts.force);
    clearTimeout(wedgeTimer);
    wedgeTimer = null;
    const text = wedgeBuf.trim();
    // Idle: only flush complete buffers. Enter/Tab force still allowed.
    if (!force && !isBufferCompleteForIdleFlush(text)) return;
    if (force && /^[A-Fa-f0-9]+$/.test(text) && text.length < 24 && !/[\s,|;]/.test(text)) {
      // Enter on truncated hex is still worth trying via processScan path in batch fallback.
    }
    wedgeBuf = '';
    refreshWedgeDebug();
    if (readOnly || text.length < 3) {
      if (text) wedgeDebugLog(`flush ignored short/readonly: ${text}`);
      return;
    }
    lastWedgeFlush = text;
    lastWedgeCodes = splitWedgeCodes(text);
    wedgeDebugLog(`flush "${text}" → ${lastWedgeCodes.length} code(s)`);
    refreshWedgeDebug();
    enqueueWedgeCodes(text);
  };

  document.addEventListener('keydown', (e) => {
    if (readOnly) return;
    if (isWedgeTypingField(e.target)) {
      refreshWedgeDebug();
      return;
    }
    if (e.ctrlKey || e.metaKey || e.altKey) return;

    if (e.key === 'Enter' || e.key === 'Tab') {
      if (!wedgeBuf.trim()) return;
      e.preventDefault();
      wedgeDebugLog(`key ${e.key} (flush)`);
      flushWedgeBuffer({ force: true });
      return;
    }

    if (e.key.length !== 1) return;
    if (inventoryArmed && e.target?.classList?.contains('scan-received-input'))
      e.preventDefault();
    wedgeBuf += e.key;
    clearTimeout(wedgeTimer);
    wedgeTimer = setTimeout(flushWedgeBuffer, wedgeIdleMs());
    refreshWedgeDebug();
  });

  if (wedgeDebugEl && new URLSearchParams(location.search).get('scanDebug') === '1') {
    wedgeDebugEl.open = true;
  }
  refreshWedgeDebug();
  document.addEventListener('focusin', () => refreshWedgeDebug());
  document.addEventListener('focusout', () => setTimeout(refreshWedgeDebug, 0));

  if (!readOnly && statusBar && statusBar.tagName === 'BUTTON') {
    // Cancel Item-code steal-back as soon as the finger/mouse hits the bar.
    statusBar.addEventListener('pointerdown', (e) => {
      stopCaptureRefocus();
      // Keep the bar as the event target; don't let focus bounce to Item code mid-tap.
      e.stopPropagation();
    });
    let toggleFromPointer = false;
    statusBar.addEventListener('pointerup', (e) => {
      if (e.button != null && e.button !== 0) return;
      e.preventDefault();
      e.stopPropagation();
      toggleFromPointer = true;
      toggleInventoryArmed();
      // Ignore the synthetic click that follows pointerup on many browsers.
      setTimeout(() => { toggleFromPointer = false; }, TOGGLE_DEBOUNCE_MS);
    });
    statusBar.addEventListener('click', (e) => {
      e.preventDefault();
      if (toggleFromPointer) return;
      toggleInventoryArmed();
    });
  }
  unmatchedClearBtn?.addEventListener('click', () => clearUnmatched());

  const actionsMenuBtn = document.getElementById('scanActionsMenuBtn');
  const actionsMenuPanel = document.getElementById('scanActionsMenuPanel');
  const closeActionsMenu = () => {
    if (actionsMenuPanel) actionsMenuPanel.hidden = true;
    if (actionsMenuBtn) actionsMenuBtn.setAttribute('aria-expanded', 'false');
  };

  actionsMenuBtn?.addEventListener('pointerdown', () => stopCaptureRefocus());

  const stopRefocusOnActionPointer = (btn) => {
    btn?.addEventListener('pointerdown', () => stopCaptureRefocus());
  };
  stopRefocusOnActionPointer(undoBtn);
  stopRefocusOnActionPointer(resetBtn);
  stopRefocusOnActionPointer(submitBtn);

  undoBtn?.addEventListener('click', (e) => {
    e.preventDefault();
    e.stopPropagation();
    closeActionsMenu();
    undoLastScan();
  });
  resetBtn?.addEventListener('click', (e) => {
    e.preventDefault();
    e.stopPropagation();
    closeActionsMenu();
    void resetScanSession();
  });

  if (linesTableBody) {
    linesTableBody.addEventListener('change', (e) => {
      const input = e.target.closest('.scan-received-input');
      if (!input || readOnly) return;
      const row = input.closest('tr[data-item-code]');
      if (!row) return;
      const raw = input.value.trim();
      if (!raw) {
        clearReceived(row, { quiet: true });
        return;
      }
      setReceivedQty(row, raw);
    });
    let receivedInputTimer = null;
    linesTableBody.addEventListener('input', (e) => {
      const input = e.target.closest('.scan-received-input');
      if (!input || readOnly) return;
      const row = input.closest('tr[data-item-code]');
      if (!row) return;
      const qty = normalizeReceived(row, input.value);
      row.classList.toggle('scan-line-partial', qty > 0 && orderQty(row) > 0 && qty < orderQty(row));
      row.classList.toggle('scan-line-received', qty > 0);
      clearTimeout(receivedInputTimer);
      receivedInputTimer = setTimeout(() => {
        const raw = input.value.trim();
        if (!raw) clearReceived(row, { quiet: true });
        else setReceivedQty(row, raw, { skipHistory: true });
      }, 400);
    });
  }


  if (submitForm && scanCountsInput && submitBtn) {
    submitForm.addEventListener('submit', (e) => {
      if (submitInProgress) {
        e.preventDefault();
        return;
      }

      syncReceivedFromDom();

      const total = totalScans();
      const lines = totalLines();
      const withScan = linesWithScan();

      if (total < 1) {
        e.preventDefault();
        showToast('Enter received qty for at least one line before submit.', 'error');
        return;
      }

      let msg = `Create Goods Received with ${total} total qty across ${withScan} line(s)?`;
      if (requireAllLines && withScan < lines) {
        msg =
          `Only ${withScan} of ${lines} lines have received qty.\n\nSubmit anyway?`;
      }

      if (!window.confirm(msg)) {
        e.preventDefault();
        return;
      }

      scanCountsInput.value = JSON.stringify(scanCounts);
      submitInProgress = true;
      submitBtn.disabled = true;
      submitBtn.classList.add('is-busy');
      submitBtn.textContent = 'Creating GR...';

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
    // Start continuous Item code scanning — no extra Ready to scan tap required.
    if (!readOnly) setInventoryArmed(true, { quiet: true });
  };

  window.addEventListener('online', () => {
    void flushOfflineQueue();
    void persistDraft();
    showToast('Back online.', 'ok');
  });

  void init();
})();
