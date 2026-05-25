/* scan-po-detail.js — QR scan, ice highlight, session counter (no DB, no last-scan panel) */
(function () {
  'use strict';

  const scanBtn = document.getElementById('scanDetailScanBtn');
  const cfg = document.getElementById('scanDetailConfig');
  if (!scanBtn || typeof ScanQrScanner === 'undefined' || typeof ScanResolve === 'undefined') return;

  const resolveUrl = cfg?.dataset.resolveScanUrl || '';
  const docKey = String(cfg?.dataset.docKey || new URLSearchParams(location.search).get('docKey') || '');
  const storageKey = docKey ? `approvalpo-scan-session-${docKey}` : '';

  const sessionTotalEl = document.getElementById('scanSessionTotal');

  /** @type {Record<string, number>} item code -> scan count */
  let scanCounts = {};

  const lineRows = () =>
    Array.from(document.querySelectorAll('.scan-lines-table tbody tr[data-item-code]'));

  const knownItemCodes = () =>
    lineRows()
      .map((row) => (row.dataset.itemCode || '').trim())
      .filter(Boolean);

  const loadCounts = () => {
    if (!storageKey) return;
    try {
      const raw = sessionStorage.getItem(storageKey);
      if (raw) scanCounts = JSON.parse(raw) || {};
    } catch (_) {
      scanCounts = {};
    }
  };

  const saveCounts = () => {
    if (!storageKey) return;
    try {
      sessionStorage.setItem(storageKey, JSON.stringify(scanCounts));
    } catch (_) { /* ignore */ }
  };

  const totalScans = () =>
    Object.values(scanCounts).reduce((sum, n) => sum + (Number(n) || 0), 0);

  const updateSessionTotal = () => {
    if (sessionTotalEl) sessionTotalEl.textContent = String(totalScans());
  };

  const syncRowVisuals = (row) => {
    const code = (row.dataset.itemCode || '').trim();
    const count = scanCounts[code] || 0;
    row.classList.toggle('scan-line-scanned', count > 0);
  };

  const applyAllRowVisuals = () => {
    lineRows().forEach(syncRowVisuals);
    updateSessionTotal();
  };

  const recordScan = (row) => {
    const code = (row.dataset.itemCode || '').trim();
    if (!code) return;
    scanCounts[code] = (scanCounts[code] || 0) + 1;
    saveCounts();
    syncRowVisuals(row);

    row.classList.remove('scan-line-match-flash');
    void row.offsetWidth;
    row.classList.add('scan-line-match-flash');
    setTimeout(() => row.classList.remove('scan-line-match-flash'), 900);

    row.scrollIntoView({ behavior: 'smooth', block: 'center' });
    updateSessionTotal();
  };

  const processScan = async (raw) => {
    let resolved;
    try {
      if (!resolveUrl) throw new Error('Resolve API not configured.');
      resolved = await ScanResolve.resolve(resolveUrl, raw, knownItemCodes());
    } catch (e) {
      return;
    }

    const code = (resolved.itemCode || resolved.ItemCode || '').trim();

    if (resolved.error && !code) return;
    if (!code) return;

    const matchRow = lineRows().find((row) => ScanResolve.codesMatch(code, row.dataset.itemCode));
    if (matchRow) {
      recordScan(matchRow);
      return;
    }
  };

  const qr = ScanQrScanner.create({
    onDetected(text) {
      const t = (text || '').trim();
      setTimeout(async () => {
        qr.close();
        if (!t) return;
        await processScan(t);
      }, 400);
    },
  });

  qr.bindButton(scanBtn);
  loadCounts();
  applyAllRowVisuals();
})();
