/* scan-po-detail.js — QR scan; open link, show page text, match PO lines */
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
  const resultMeta = document.getElementById('scanLastResultMeta');
  const previewWrap = document.getElementById('scanLastResultPreviewWrap');
  const previewEl = document.getElementById('scanLastResultPreview');

  const lineRows = () =>
    Array.from(document.querySelectorAll('.scan-lines-table tbody tr[data-item-code]'));

  const knownItemCodes = () =>
    lineRows()
      .map((row) => (row.dataset.itemCode || '').trim())
      .filter(Boolean);

  const showResult = (resolved, itemCode, matchMsg, warn) => {
    if (!resultBox || !resultText) return;
    const scanned = resolved.scanned || '';
    const source = resolved.source || '';

    resultBox.hidden = false;
    resultBox.classList.toggle('is-warn', !!warn);
    resultText.textContent = scanned;

    if (resultCode) {
      if (itemCode && itemCode !== scanned) {
        resultCode.hidden = false;
        const src = typeof ScanResolve.sourceLabel === 'function' ? ScanResolve.sourceLabel(source) : source;
        resultCode.textContent = `Item code: ${itemCode}${src ? ` — ${src}` : ''}`;
      } else if (itemCode) {
        resultCode.hidden = false;
        resultCode.textContent = `Item code: ${itemCode}`;
      } else {
        resultCode.hidden = true;
        resultCode.textContent = '';
      }
    }

    if (resultMeta) {
      const parts = [];
      if (resolved.finalUrl && resolved.finalUrl !== scanned) {
        parts.push(`Opened: ${resolved.finalUrl}`);
      }
      if (resolved.httpStatus) {
        parts.push(`HTTP ${resolved.httpStatus}`);
      }
      if (resolved.contentType) {
        parts.push(resolved.contentType);
      }
      const searched = resolved.searchedCodes || resolved.SearchedCodes;
      if (searched?.length) {
        parts.push(`Looking for PO codes: ${searched.join(', ')}`);
      }
      if (parts.length) {
        resultMeta.hidden = false;
        resultMeta.textContent = parts.join(' · ');
      } else {
        resultMeta.hidden = true;
        resultMeta.textContent = '';
      }
    }

    const preview = resolved.pagePreview || resolved.PagePreview;
    if (previewWrap && previewEl) {
      if (preview) {
        previewWrap.hidden = false;
        previewEl.textContent = preview;
        previewWrap.open = !itemCode || warn;
      } else {
        previewWrap.hidden = true;
        previewEl.textContent = '';
        previewWrap.open = false;
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
      resolved = await ScanResolve.resolve(resolveUrl, raw, knownItemCodes());
    } catch (e) {
      showResult({ scanned: raw }, null, `Could not resolve link: ${e.message}`, true);
      return;
    }

    const code = (resolved.itemCode || resolved.ItemCode || '').trim();

    if (resolved.error && !code) {
      showResult(resolved, null, resolved.error, true);
      return;
    }

    if (!code) {
      showResult(resolved, null, resolved.error || 'No item code found.', true);
      return;
    }

    const matchRow = lineRows().find((row) => ScanResolve.codesMatch(code, row.dataset.itemCode));
    if (matchRow) {
      highlightItem(code);
      const same = ScanResolve.codesEqual(code, matchRow.dataset.itemCode);
      showResult(
        resolved,
        code,
        same
          ? `Match: line item ${matchRow.dataset.itemCode} (exact).`
          : `Match: line item ${matchRow.dataset.itemCode} (same code).`,
        false
      );
      return;
    }

    showResult(
      resolved,
      code,
      `No match — this PO has no line with item code "${code}". Compare with text below.`,
      true
    );
  };

  const qr = ScanQrScanner.create({
    onDetected(text) {
      const t = (text || '').trim();
      setTimeout(async () => {
        qr.close();
        if (!t) return;
        if (resultMatch) resultMatch.textContent = 'Opening link and reading page…';
        if (resultBox) {
          resultBox.hidden = false;
          resultBox.classList.remove('is-warn');
        }
        if (previewWrap) previewWrap.hidden = true;
        await processScan(t);
      }, 400);
    },
  });

  qr.bindButton(scanBtn);
})();
