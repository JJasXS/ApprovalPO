/* scan-po.js — approved PO list; row links open /ScanPODetail?docKey= */
(function () {
  'use strict';

  const cfg = document.getElementById('scanPageConfig');
  if (!cfg) return;

  const ordersUrl = cfg.dataset.ordersJsonUrl || '';
  const detailPageUrl = (cfg.dataset.detailPageUrl || '/ScanPODetail').replace(/\/$/, '');

  let allOrders = [];
  let searchTerm = '';

  const tbody = document.getElementById('scanTableBody');
  const emptyState = document.getElementById('scanEmpty');
  const resultCount = document.getElementById('scanResultCount');
  const searchInput = document.getElementById('scanSearch');

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

  if (typeof ScanQrScanner !== 'undefined') {
    const qr = ScanQrScanner.create({
      onDetected(text) {
        const t = (text || '').trim();
        const match = allOrders.find(
          (o) => (o.poNumber || '').trim().toLowerCase() === t.toLowerCase()
        );

        setTimeout(() => {
          qr.close();
          if (match) {
            window.location.href = detailHref(match.docKey);
            return;
          }
          searchTerm = t;
          if (searchInput) searchInput.value = t;
          renderTable(allOrders);
          alert(`QR scanned: "${t}"\nNo matching approved PO found.`);
        }, 400);
      },
    });
    qr.bindButton(document.getElementById('scanQrBtn'));
  }

  loadOrders();
})();
