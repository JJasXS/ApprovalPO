/* scan-so.js — approved SOs with delivery progress */
(function () {
  'use strict';

  const cfg = document.getElementById('scanSoPageConfig');
  if (!cfg) return;

  const ordersUrl = cfg.dataset.ordersJsonUrl || '';
  const detailPageUrl = (cfg.dataset.detailPageUrl || '/ScanSODetail').replace(/\/$/, '');

  let allOrders = [];
  let searchTerm = '';
  let filter = 'outstanding';

  const tbody = document.getElementById('scanSoTableBody');
  const emptyState = document.getElementById('scanSoEmpty');
  const resultCount = document.getElementById('scanSoResultCount');
  const searchInput = document.getElementById('scanSoSearch');

  const esc = (s) => String(s ?? '').replace(/[&<>"']/g, (c) =>
    ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

  const fmtDate = (s) => {
    if (!s) return '—';
    const d = new Date(s);
    return isNaN(d) ? s : d.toLocaleDateString('en-GB', { day: '2-digit', month: 'short', year: 'numeric' });
  };

  const detailHref = (docKey) => `${detailPageUrl}?docKey=${encodeURIComponent(docKey)}`;

  const matchesFilter = (o) => {
    const label = (o.deliveryLabel || '').toLowerCase();
    if (filter === 'delivered') return label === 'delivered';
    return label !== 'delivered';
  };

  const getFiltered = () => {
    const term = searchTerm.toLowerCase().trim();
    return allOrders.filter((o) => {
      if (!matchesFilter(o)) return false;
      if (!term) return true;
      return (o.soNumber || '').toLowerCase().includes(term)
        || (o.customer || '').toLowerCase().includes(term);
    });
  };

  const fmtQty = (n) => {
    const v = Number(n);
    if (!Number.isFinite(v)) return '0';
    return String(Math.round(v * 10000) / 10000);
  };

  const progressHtml = (o) => {
    const pct = Math.max(0, Math.min(100, Number(o.deliveredPercent) || 0));
    const delivered = fmtQty(o.deliveredQty);
    const ordered = fmtQty(o.orderedQty);
    const ratio = `${delivered}/${ordered}`;
    // Fully delivered tab: Progress shows Complete (with x/x so full qty is clear).
    const label = o.deliveryLabel === 'Delivered'
      ? `Complete · ${ratio}`
      : o.deliveryLabel === 'Partial'
        ? `${pct}% · ${ratio}`
        : `0/${ordered}`;
    return `<div class="scan-progress"><div class="scan-progress-bar" style="width:${pct}%"></div><span class="scan-progress-label">${esc(label)}</span></div>`;
  };

  const rowHtml = (o) => {
    const href = detailHref(o.docKey);
    const cust = (o.customer || '').trim();
    const meta = cust ? `<span class="scan-row-meta">${esc(cust)}</span>` : '';
    return `
      <tr class="scan-row scan-row--link" data-href="${esc(href)}" role="link" tabindex="0">
        <td class="scan-col-po"><span class="scan-gr-no">${esc(o.soNumber || '—')}</span>${meta}</td>
        <td class="scan-col-progress">${progressHtml(o)}</td>
        <td class="scan-col-date">${fmtDate(o.orderDate)}</td>
      </tr>`;
  };

  const render = () => {
    const filtered = getFiltered();
    if (resultCount) resultCount.textContent = `${filtered.length} shown`;
    if (!tbody) return;
    if (filtered.length === 0) {
      tbody.innerHTML = '';
      if (emptyState) emptyState.hidden = false;
      return;
    }
    if (emptyState) emptyState.hidden = true;
    tbody.innerHTML = filtered.map(rowHtml).join('');
  };

  tbody?.addEventListener('click', (e) => {
    const tr = e.target.closest('tr[data-href]');
    if (tr?.dataset.href) window.location.href = tr.dataset.href;
  });
  tbody?.addEventListener('keydown', (e) => {
    if (e.key !== 'Enter' && e.key !== ' ') return;
    const tr = e.target.closest('tr[data-href]');
    if (!tr?.dataset.href) return;
    e.preventDefault();
    window.location.href = tr.dataset.href;
  });

  searchInput?.addEventListener('input', () => {
    searchTerm = searchInput.value || '';
    render();
  });

  document.querySelectorAll('.scan-seg-btn').forEach((btn) => {
    btn.addEventListener('click', () => {
      document.querySelectorAll('.scan-seg-btn').forEach((b) => {
        b.classList.toggle('is-active', b === btn);
        b.setAttribute('aria-selected', b === btn ? 'true' : 'false');
      });
      filter = btn.dataset.filter || 'outstanding';
      render();
    });
  });

  fetch(ordersUrl, { credentials: 'same-origin', headers: { Accept: 'application/json' } })
    .then((r) => r.json())
    .then((data) => {
      if (Array.isArray(data)) allOrders = data;
      else if (data?.error) throw new Error(data.error);
      else allOrders = [];
      render();
    })
    .catch((err) => {
      if (tbody) tbody.innerHTML = `<tr class="scan-row"><td colspan="3">${esc(err.message || 'Failed to load')}</td></tr>`;
    });
})();
