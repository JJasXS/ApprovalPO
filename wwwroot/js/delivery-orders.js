/* delivery-orders.js — SL_DO list */
(function () {
  'use strict';

  const cfg = document.getElementById('deliveryOrdersConfig');
  if (!cfg) return;

  const listUrl = cfg.dataset.listJsonUrl || '';
  const detailPageUrl = (cfg.dataset.detailPageUrl || '/ScanDeliveredDetail').replace(/\/$/, '');

  let allOrders = [];
  let searchTerm = '';

  const tbody = document.getElementById('doTableBody');
  const emptyState = document.getElementById('doEmpty');
  const resultCount = document.getElementById('doResultCount');
  const searchInput = document.getElementById('doSearch');

  const esc = (s) =>
    String(s ?? '').replace(/[&<>"']/g, (c) =>
      ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c])
    );

  const fmtDate = (s) => {
    if (!s) return '—';
    const d = new Date(s);
    if (isNaN(d)) return s;
    const pad = (n) => String(n).padStart(2, '0');
    return `${pad(d.getDate())}-${pad(d.getMonth() + 1)}-${d.getFullYear()}`;
  };

  const detailHref = (docKey) => `${detailPageUrl}?docKey=${encodeURIComponent(docKey)}`;

  const getFiltered = () => {
    const term = searchTerm.toLowerCase().trim();
    const sorted = allOrders.slice().sort((a, b) => {
      const da = a.doDate ? new Date(a.doDate).getTime() : 0;
      const db = b.doDate ? new Date(b.doDate).getTime() : 0;
      if (db !== da) return db - da;
      return String(a.doNumber || '').localeCompare(String(b.doNumber || ''), undefined, {
        sensitivity: 'base',
      });
    });
    if (!term) return sorted;
    return sorted.filter((r) => {
      const dono = (r.doNumber || '').toLowerCase();
      const sono = (r.soNumber || '').toLowerCase();
      const cust = (r.customer || '').toLowerCase();
      return dono.includes(term) || sono.includes(term) || cust.includes(term);
    });
  };

  const customerMeta = (r) => {
    const v = (r.customer || '').trim();
    if (!v) return '';
    return `<span class="scan-row-meta">${esc(v)}</span>`;
  };

  const rowHtml = (r) => {
    const href = detailHref(r.docKey);
    return `
      <tr class="scan-row scan-row--link scan-row--gr" data-href="${esc(href)}" role="link" tabindex="0">
        <td class="scan-col-gr"><span class="scan-gr-no">${esc(r.doNumber || '—')}</span>${customerMeta(r)}</td>
        <td class="scan-col-po-ref">${esc(r.soNumber || '—')}</td>
        <td class="scan-col-date">${fmtDate(r.doDate)}</td>
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

  fetch(listUrl, { credentials: 'same-origin', headers: { Accept: 'application/json' } })
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
