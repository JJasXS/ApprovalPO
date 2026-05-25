/* scan-po.js — approved PO list with screen-fit paging + More */
(function () {
  'use strict';

  const cfg = document.getElementById('scanPageConfig');
  if (!cfg) return;

  const ordersUrl = cfg.dataset.ordersJsonUrl || '';
  const detailPageUrl = (cfg.dataset.detailPageUrl || '/ScanPODetail').replace(/\/$/, '');

  let allOrders = [];
  let searchTerm = '';
  const MORE_STEP = 10;
  let visibleCount = 0;
  let initialBatchSize = 8;
  let rowHeightPx = 52;

  const tbody = document.getElementById('scanTableBody');
  const emptyState = document.getElementById('scanEmpty');
  const resultCount = document.getElementById('scanResultCount');
  const searchInput = document.getElementById('scanSearch');
  const loadMoreBtn = document.getElementById('scanLoadMoreBtn');

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

  const measureRowHeight = () => {
    const sample = tbody?.querySelector('tr.scan-row');
    if (sample?.offsetHeight > 0) {
      rowHeightPx = sample.offsetHeight;
      return;
    }
    rowHeightPx = window.innerWidth < 480 ? 58 : 52;
  };

  const recalcPageSize = () => {
    const header = document.querySelector('.scan-header');
    const searchBar = document.querySelector('.scan-search-bar');
    const hint = document.querySelector('.scan-list-hint');
    const thead = document.querySelector('.scan-table thead');
    const footer = document.querySelector('.scan-list-footer');
    const mainPad = 100;

    let used = mainPad;
    if (header) used += header.getBoundingClientRect().height;
    if (searchBar) used += searchBar.offsetHeight;
    if (hint) used += hint.offsetHeight + 12;
    if (footer) used += footer.offsetHeight + 8;

    const theadH = thead?.offsetHeight ?? 40;
    const available = window.innerHeight - used - theadH;
    initialBatchSize = Math.max(3, Math.floor(available / rowHeightPx));
  };

  const getFiltered = () => {
    const term = searchTerm.toLowerCase().trim();
    return term
      ? allOrders.filter((o) => (o.poNumber || '').toLowerCase().includes(term))
      : allOrders;
  };

  const rowHtml = (o) => {
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
  };

  const updateLoadMore = (shown, total) => {
    if (!loadMoreBtn) return;
    const remaining = total - shown;
    if (remaining <= 0) {
      loadMoreBtn.hidden = true;
      return;
    }
    loadMoreBtn.hidden = false;
    loadMoreBtn.textContent = 'More';
    loadMoreBtn.setAttribute('aria-label', `Show ${Math.min(MORE_STEP, remaining)} more purchase orders (${remaining} remaining)`);
  };

  const updateResultCount = (shown, total) => {
    if (!resultCount) return;
    if (total === 0) {
      resultCount.textContent = '0 approved POs';
      return;
    }
    if (shown < total) {
      resultCount.textContent = `Showing ${shown} of ${total}`;
      return;
    }
    resultCount.textContent = `${total} approved PO${total !== 1 ? 's' : ''}`;
  };

  const renderTable = (orders) => {
    const filtered = getFiltered();

    if (!tbody) return;

    if (filtered.length === 0) {
      tbody.innerHTML = '';
      if (emptyState) emptyState.hidden = false;
      updateLoadMore(0, 0);
      updateResultCount(0, 0);
      return;
    }
    if (emptyState) emptyState.hidden = true;

    const shown = Math.min(visibleCount, filtered.length);
    tbody.innerHTML = filtered.slice(0, shown).map(rowHtml).join('');
    measureRowHeight();

    updateLoadMore(shown, filtered.length);
    updateResultCount(shown, filtered.length);
  };

  const loadMore = () => {
    const filtered = getFiltered();
    const prev = visibleCount;
    visibleCount = Math.min(filtered.length, visibleCount + MORE_STEP);
    if (visibleCount === prev || !tbody) return;

    const chunk = filtered.slice(prev, visibleCount);
    tbody.insertAdjacentHTML('beforeend', chunk.map(rowHtml).join(''));
    measureRowHeight();

    updateLoadMore(visibleCount, filtered.length);
    updateResultCount(visibleCount, filtered.length);
  };

  if (loadMoreBtn) {
    loadMoreBtn.addEventListener('click', loadMore);
  }

  let resizeTimer;
  window.addEventListener('resize', () => {
    clearTimeout(resizeTimer);
    resizeTimer = setTimeout(() => recalcPageSize(), 200);
  });

  const loadOrders = async () => {
    try {
      const res = await fetch(ordersUrl);
      const data = await res.json();
      allOrders = Array.isArray(data) ? data : [];
      recalcPageSize();
      visibleCount = Math.min(allOrders.length, initialBatchSize);
      renderTable(allOrders);
    } catch (e) {
      if (tbody) {
        tbody.innerHTML = `<tr><td colspan="4" class="scan-empty">Failed to load orders: ${esc(e.message)}</td></tr>`;
      }
      if (loadMoreBtn) loadMoreBtn.hidden = true;
    }
  };

  if (searchInput) {
    searchInput.addEventListener('input', () => {
      searchTerm = searchInput.value;
      recalcPageSize();
      const filtered = getFiltered();
      visibleCount = Math.min(filtered.length, initialBatchSize);
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

  loadOrders();
})();
