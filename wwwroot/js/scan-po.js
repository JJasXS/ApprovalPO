/* scan-po.js — approved PO list; row links open /ScanPODetail?docKey= */
(function () {
  'use strict';

  const cfg = document.getElementById('scanPageConfig');
  if (!cfg) return;

  const ordersUrl = cfg.dataset.ordersJsonUrl || '';
  const resolveUrl = cfg.dataset.resolveScanUrl || '';
  const detailPageUrl = (cfg.dataset.detailPageUrl || '/ScanPODetail').replace(/\/$/, '');

  let allOrders = [];
  let searchTerm = '';

  const tbody = document.getElementById('scanTableBody');
  const emptyState = document.getElementById('scanEmpty');
  const resultCount = document.getElementById('scanResultCount');
  const searchInput = document.getElementById('scanSearch');
  const resultBox = document.getElementById('scanLastResult');
  const resultText = document.getElementById('scanLastResultText');
  const resultCode = document.getElementById('scanLastResultCode');
  const resultMatch = document.getElementById('scanLastResultMatch');

  const showScanResult = (raw, code, matchMsg, warn) => {
    if (!resultBox || !resultText) return;
    resultBox.hidden = false;
    resultBox.classList.toggle('is-warn', !!warn);
    resultText.textContent = raw;
    if (resultCode) {
      if (code && code !== raw) {
        resultCode.hidden = false;
        resultCode.textContent = `Resolved: ${code}`;
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

  const esc = (s) => String(s ?? '').replace(/[&<>"']/g, (c) =>
    ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

  const fmtDate = (s) => {
    if (!s) return '—';
    const d = new Date(s);
    return isNaN(d) ? s : d.toLocaleDateString('en-GB', { day: '2-digit', month: 'short', year: 'numeric' });
  };

  const fmtAmt = (n) => {
    const v = parseFloat(n) || 0;
    return v.toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 });
  };

  const detailHref = (docKey) => `${detailPageUrl}?docKey=${encodeURIComponent(docKey)}`;

  const renderTable = (orders) => {
    const term = searchTerm.toLowerCase().trim();
    const filtered = term
      ? orders.filter((o) => (o.poNumber || '').toLowerCase().includes(term))
      : orders;

    if (resultCount) {
      resultCount.textContent = `${filtered.length} approved PO${filtered.length !== 1 ? 's' : ''}`;
    }

    if (!tbody) return;
    if (filtered.length === 0) {
      tbody.innerHTML = '';
      if (emptyState) emptyState.hidden = false;
      return;
    }
    if (emptyState) emptyState.hidden = true;

    tbody.innerHTML = filtered
      .map((o) => {
        const href = detailHref(o.docKey);
        return `
      <tr class="scan-row scan-row--link">
        <td colspan="4" class="scan-row-anchor-cell">
          <a href="${esc(href)}" class="scan-row-anchor">
            <span class="scan-po-num">${esc(o.poNumber || '—')}</span>
            <span class="scan-date">${fmtDate(o.orderDate)}</span>
            <span class="num scan-amount">${fmtAmt(o.amount)}</span>
            <span class="scan-row-chev" aria-hidden="true">
              <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round"><polyline points="9 18 15 12 9 6"/></svg>
            </span>
          </a>
        </td>
      </tr>`;
      })
      .join('');
  };

  const loadOrders = async () => {
    try {
      const res = await fetch(ordersUrl);
      const data = await res.json();
      allOrders = Array.isArray(data) ? data : [];
      renderTable(allOrders);
    } catch (e) {
      if (tbody) {
        tbody.innerHTML = `<tr><td colspan="4" class="scan-empty">Failed to load orders: ${esc(e.message)}</td></tr>`;
      }
    }
  };

  if (searchInput) {
    searchInput.addEventListener('input', () => {
      searchTerm = searchInput.value;
      renderTable(allOrders);
    });
  }

  const menuBtn = document.getElementById('scanMenuBtn');
  const menuPanel = document.getElementById('scanMenuPanel');
  if (menuBtn && menuPanel) {
    menuBtn.addEventListener('click', (e) => {
      e.stopPropagation();
      const open = !menuPanel.hidden;
      menuPanel.hidden = open;
      menuBtn.setAttribute('aria-expanded', String(!open));
    });
    document.addEventListener('click', () => {
      menuPanel.hidden = true;
      menuBtn.setAttribute('aria-expanded', 'false');
    });
  }

  const findPoMatch = (code) => {
    const key = String(code || '').trim().toLowerCase();
    if (!key) return null;
    return allOrders.find((o) => {
      const po = (o.poNumber || '').trim().toLowerCase();
      return po === key || po.includes(key) || key.includes(po);
    });
  };

  if (typeof ScanQrScanner !== 'undefined') {
    const qr = ScanQrScanner.create({
      onDetected(text) {
        const t = (text || '').trim();
        setTimeout(async () => {
          qr.close();
          let code = t;
          if (typeof ScanResolve !== 'undefined' && resolveUrl && ScanResolve.isUrl(t)) {
            if (resultMatch) resultMatch.textContent = 'Resolving link…';
            try {
              const resolved = await ScanResolve.resolve(resolveUrl, t);
              if (resolved.itemCode) code = resolved.itemCode;
            } catch (_) { /* use raw */ }
          }

          const match = findPoMatch(code) || findPoMatch(t);
          if (match) {
            showScanResult(t, code, `Opening PO ${match.poNumber || match.docKey}…`, false);
            window.location.href = detailHref(match.docKey);
            return;
          }
          searchTerm = code;
          if (searchInput) searchInput.value = code;
          renderTable(allOrders);
          showScanResult(t, code, 'No matching approved PO # for this scan.', true);
        }, 400);
      },
    });
    qr.bindButton(document.getElementById('scanQrBtn'));
  }

  loadOrders();
})();
