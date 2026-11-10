/* scan-so.js — approved SOs with delivery progress */
(function () {
  'use strict';

  const cfg = document.getElementById('scanSoPageConfig');
  if (!cfg) return;

  const ordersUrl = cfg.dataset.ordersJsonUrl || '';
  const detailPageUrl = (cfg.dataset.detailPageUrl || '/ScanSODetail').replace(/\/$/, '');

  const FILTER_OUTSTANDING = 'outstanding';
  const FILTER_FULL = 'fully-delivered';

  const canonicalFilter = (raw) => {
    const value = String(raw || '').trim().toLowerCase().replace(/_/g, '-');
    if (value === FILTER_FULL || value === 'fullydelivered' || value === 'delivered' || value === 'complete')
      return FILTER_FULL;
    return FILTER_OUTSTANDING;
  };

  let allOrders = [];
  let searchTerm = '';
  let filter = canonicalFilter(cfg.dataset.initialFilter);

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
    if (filter === FILTER_FULL) return label === 'delivered';
    return label !== 'delivered';
  };

  const filterBtn = document.getElementById('scanFilterKebabBtn');
  const filterPanel = document.getElementById('scanFilterKebabPanel');
  const filterLabel = () => (filter === FILTER_FULL ? 'Fully delivered' : 'Outstanding');

  const setFilterPanelOpen = (open) => {
    if (!filterBtn || !filterPanel) return;
    filterPanel.hidden = !open;
    filterBtn.setAttribute('aria-expanded', open ? 'true' : 'false');
  };

  const syncFilterButtons = () => {
    document.querySelectorAll('.scan-filter-item').forEach((b) => {
      const on = (b.dataset.filter || '') === filter;
      b.classList.toggle('is-active', on);
      b.setAttribute('aria-selected', on ? 'true' : 'false');
    });
    if (filterBtn) filterBtn.setAttribute('aria-label', filterLabel());
  };

  const writeFilterParam = () => {
    const url = new URL(window.location.href);
    if (url.searchParams.get('filter') === filter) return;
    url.searchParams.set('filter', filter);
    history.replaceState({ filter }, '', url);
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
    const label = o.deliveryLabel === 'Delivered'
      ? 'Complete'
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

  filterBtn?.addEventListener('click', (e) => {
    e.stopPropagation();
    setFilterPanelOpen(Boolean(filterPanel?.hidden));
  });

  filterPanel?.addEventListener('click', (e) => {
    const item = e.target.closest('.scan-filter-item');
    if (!item) return;
    filter = canonicalFilter(item.dataset.filter);
    syncFilterButtons();
    writeFilterParam();
    render();
    setFilterPanelOpen(false);
  });

  document.addEventListener('click', (e) => {
    if (!filterPanel || filterPanel.hidden) return;
    if (filterBtn?.contains(e.target) || filterPanel.contains(e.target)) return;
    setFilterPanelOpen(false);
  });

  document.addEventListener('keydown', (e) => {
    if (e.key === 'Escape') setFilterPanelOpen(false);
  });

  syncFilterButtons();
  writeFilterParam();

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
