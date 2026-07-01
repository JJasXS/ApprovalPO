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

  const projectNorm = (s) => String(s ?? '').trim().replace(/\s+/g, '');

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

    if (!opts.quietFlash && n > 0) {
      row.classList.remove('scan-line-match-flash');
      void row.offsetWidth;
      row.classList.add('scan-line-match-flash');
      setTimeout(() => row.classList.remove('scan-line-match-flash'), 900);
    }

    if (!opts.noFeedback && n > 0) playMatchFeedback();
    updateProgress();
    return n;
  };

  const applyParsedBarcode = (local, opts = {}) => {
    const proj = (local.location || '').trim();
    if (!projectNorm(proj)) return false;

    const row = pickScanRow(local.itemCode, proj, null);
    if (!row) return false;

    const qtyRaw = local.quantity;
    const qty =
      qtyRaw != null && Number(qtyRaw) > 0
        ? normalizeReceived(row, Number(qtyRaw))
        : orderQty(row) || 1;
    setReceivedQty(row, qty, { noFeedback: true, skipHistory: opts.fromQueue, ...opts });
    return { row, qty, proj };
  };

  const tryLocalBarcodeScan = (raw, opts = {}) => {
    const trimmed = String(raw || '').trim();
    if (!trimmed.includes(';')) return null;

    const parseAll = ScanResolve.parseSemicolonPayloadAll || ScanResolve.parseSemicolonPayload;
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
        showScanError('not_on_po', `${local.itemCode} @ ${proj} is not on this PO.`);
      }
      return { handled: true, ok: false };
    }

    const qtyRaw = local.quantity;
    const qty =
      qtyRaw != null && Number(qtyRaw) > 0
        ? normalizeReceived(row, Number(qtyRaw))
        : orderQty(row) || 1;
    setReceivedQty(row, qty, { noFeedback: opts.fromQueue, ...opts });
    if (!opts.silent && !opts.fromQueue) {
      const displayProj = (row.dataset.project || proj || '').trim();
      const locPart = displayProj ? ` @ ${displayProj}` : '';
      showToast(`Matched: ${local.itemCode}${locPart} — received ${qty}`, 'ok');
    }
    return { handled: true, ok: true };
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
        }
      } catch (_) {
        showToast('Reset locally; server sync failed (offline?).', 'warn');
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
      const label = proj ? `${entry.code} (${proj})` : entry.code;
      showToast(`Undid change for ${label}.`, 'info');
      updateUndoButton();
      return;
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
        const qty =
          qtyRaw != null && Number(qtyRaw) > 0
            ? normalizeReceived(localRow, Number(qtyRaw))
            : orderQty(localRow) || 1;
        setReceivedQty(localRow, qty, { noFeedback: opts.fromQueue });
        if (!opts.silent && !opts.fromQueue) {
          const proj = (localRow.dataset.project || localProject || '').trim();
          const locPart = proj ? ` @ ${proj}` : '';
          showToast(`Matched: ${local.itemCode}${locPart} — received ${qty}`, 'ok');
        }
        return true;
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
      const qty =
        qtyRaw != null && Number(qtyRaw) > 0
          ? normalizeReceived(matchRow, Number(qtyRaw))
          : orderQty(matchRow) || 1;
      setReceivedQty(matchRow, qty, { noFeedback: opts.fromQueue });
      if (!opts.silent && !opts.fromQueue) {
        const proj = (matchRow.dataset.project || scanProject || '').trim();
        const locPart = proj ? ` @ ${proj}` : '';
        showToast(`Matched: ${code}${locPart} — received ${qty}`, 'ok');
      }
      return true;
    }

    if (!opts.silent) showScanError('not_on_po', err || code);
    return false;
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
  resetBtn?.addEventListener('click', () => void resetScanSession());

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

  const scanTabScan = document.getElementById('scanTabScan');
  const scanTabReceive = document.getElementById('scanTabReceive');
  const scanPanelScan = document.getElementById('scanPanelScan');
  const scanPanelReceive = document.getElementById('scanPanelReceive');

  const activateDetailTab = (which) => {
    const isScan = which === 'scan';
    scanTabScan?.classList.toggle('is-active', isScan);
    scanTabReceive?.classList.toggle('is-active', !isScan);
    scanTabScan?.setAttribute('aria-selected', String(isScan));
    scanTabReceive?.setAttribute('aria-selected', String(!isScan));
    if (scanPanelScan) scanPanelScan.hidden = !isScan;
    if (scanPanelReceive) scanPanelReceive.hidden = isScan;
  };

  scanTabScan?.addEventListener('click', () => activateDetailTab('scan'));
  scanTabReceive?.addEventListener('click', () => activateDetailTab('receive'));

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
  };

  window.addEventListener('online', () => {
    void flushOfflineQueue();
    void persistDraft();
    showToast('Back online.', 'ok');
  });

  void init();
})();
