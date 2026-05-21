/* scan-po-detail.js — QR scan; resolve link → item code → match PO lines */
(function () {
  'use strict';

  const scanBtn = document.getElementById('scanDetailScanBtn');
  const cfg = document.getElementById('scanDetailConfig');
  if (!scanBtn || typeof ScanQrScanner === 'undefined' || typeof ScanResolve === 'undefined') return;

  const resolveUrl = cfg?.dataset.resolveScanUrl || '';
  const resultBox = document.getElementById('scanLastResult');
  const resultText = document.getElementById('scanLastResultText');
  const resultCode = document.getElementById('scanLastResultCode');
  const resultMatch = document.getElementById('scanLastResultMatch');
  const lineRows = () =>
    Array.from(document.querySelectorAll('.scan-lines-table tbody tr[data-item-code]'));

  const showResult = (scanned, itemCode, matchMsg, warn, source) => {
    if (!resultBox || !resultText) return;
    resultBox.hidden = false;
    resultBox.classList.toggle('is-warn', !!warn);
    resultText.textContent = scanned;
    if (resultCode) {
      if (itemCode && itemCode !== scanned) {
        resultCode.hidden = false;
        resultCode.textContent = `Item code from link: ${itemCode}${source ? ` (${source})` : ''}`;
      } else if (itemCode) {
        resultCode.hidden = false;
        resultCode.textContent = `Item code: ${itemCode}`;
      } else {
        resultCode.hidden = true;
        resultCode.textContent = '';
      }
    }
    if (resultMatch) {
      if (matchMsg) {
        resultMatch.textContent = matchMsg;
        resultMatch.hidden = false;
      } else {
        resultMatch.hidden = true;
        resultMatch.textContent = '';
      }
    }
    resultBox.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
  };

  const highlightItem = (code) => {
    let hit = null;
    lineRows().forEach((row) => {
      const isMatch = ScanResolve.codesMatch(code, row.dataset.itemCode);
      row.classList.toggle('scan-line-match', isMatch);
      if (isMatch) hit = row;
    });
    if (hit) hit.scrollIntoView({ behavior: 'smooth', block: 'center' });
    return hit;
  };

  const processScan = async (raw) => {
    lineRows().forEach((row) => row.classList.remove('scan-line-match'));

    let resolved;
    try {
      if (!resolveUrl) throw new Error('Resolve API not configured.');
      resolved = await ScanResolve.resolve(resolveUrl, raw);
    } catch (e) {
      showResult(raw, null, `Could not resolve link: ${e.message}`, true, '');
      return;
    }

    if (resolved.error && !resolved.itemCode) {
      showResult(resolved.scanned || raw, null, resolved.error, true, resolved.source || '');
      return;
    }

    const code = (resolved.itemCode || '').trim();
    const scanned = resolved.scanned || raw;
    const source = resolved.source || '';

    if (!code) {
      showResult(scanned, null, resolved.error || 'No item code found.', true, source);
      return;
    }

    const matchRow = lineRows().find((row) => ScanResolve.codesMatch(code, row.dataset.itemCode));
    if (matchRow) {
      highlightItem(code);
      const same = ScanResolve.codesEqual(code, matchRow.dataset.itemCode);
      showResult(
        scanned,
        code,
        same
          ? `Match: line item ${matchRow.dataset.itemCode} (exact).`
          : `Match: line item ${matchRow.dataset.itemCode} (same code).`,
        false,
        source
      );
      return;
    }

    showResult(
      scanned,
      code,
      `No match — this PO has no line with item code "${code}".`,
      true,
      source
    );
  };

  const qr = ScanQrScanner.create({
    onDetected(text) {
      const t = (text || '').trim();
      setTimeout(async () => {
        qr.close();
        if (!t) return;
        if (resultMatch) resultMatch.textContent = 'Resolving link…';
        if (resultBox) {
          resultBox.hidden = false;
          resultBox.classList.remove('is-warn');
        }
        await processScan(t);
      }, 400);
    },
  });

  qr.bindButton(scanBtn);
})();
