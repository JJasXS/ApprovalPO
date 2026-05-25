/* scan-po.js — PO list: fill viewport first, More adds 10 below (no scroll jump) */
(function () {
  'use strict';

  const cfg = document.getElementById('scanPageConfig');
  if (!cfg) return;

  const ordersUrl = cfg.dataset.ordersJsonUrl || '';
  const detailPageUrl = (cfg.dataset.detailPageUrl || '/ScanPODetail').replace(/\/$/, '');

  const MORE_STEP = 10;
  const VIEWPORT_PAD = 12;

  let allOrders = [];
  let searchTerm = '';
  let visibleCount = 0;

  const tbody = document.getElementById('scanTableBody');
  const emptyState = document.getElementById('scanEmpty');
  const resultCount = document.getElementById('scanResultCount');
  const searchInput = document.getElementById('scanSearch');
  const loadMoreBtn = document.getElementById('scanLoadMoreBtn');
  const listFooter = document.querySelector('.scan-list-footer');

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

  const contentBottom = () => {
    let bottom = 0;
    const rows = tbody?.querySelectorAll('tr.scan-row');
    if (rows?.length) {
      bottom = rows[rows.length - 1].getBoundingClientRect().bottom;
    }
    if (loadMoreBtn && !loadMoreBtn.hidden) {
      bottom = Math.max(bottom, loadMoreBtn.getBoundingClientRect().bottom);
    } else if (listFooter) {
      bottom = Math.max(bottom, listFooter.getBoundingClientRect().top);
    }
    return bottom;
  };

  const viewportLimit = (reserveMoreBtn) => {
    const moreH =
      reserveMoreBtn && loadMoreBtn
        ? loadMoreBtn.offsetHeight + 6
        : 0;
    return window.innerHeight - VIEWPORT_PAD - moreH;
  };

  /** How many rows fit on screen (measured with real DOM). */
  const countRowsThatFit = (filtered, reserveMoreBtn) => {
    if (!tbody || filtered.length === 0) return 0;

    const limit = viewportLimit(reserveMoreBtn);
    tbody.innerHTML = '';
    let fit = 0;

    for (let i = 0; i < filtered.length; i++) {
      tbody.insertAdjacentHTML('beforeend', rowHtml(filtered[i]));
      fit = i + 1;
      if (contentBottom() > limit) {
        tbody.lastElementChild?.remove();
        fit = i;
        break;
      }
    }

    return fit;
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
    loadMoreBtn.setAttribute(
      'aria-label',
      `Load ${Math.min(MORE_STEP, remaining)} more (${remaining} remaining)`
    );
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

  const renderSlice = (filtered, count) => {
    const shown = Math.min(count, filtered.length);
    tbody.innerHTML = filtered.slice(0, shown).map(rowHtml).join('');
    return shown;
  };

  const setInitialVisible = (filtered) => {
    if (filtered.length === 0) {
      visibleCount = 0;
      if (tbody) tbody.innerHTML = '';
      if (emptyState) emptyState.hidden = false;
      updateLoadMore(0, 0);
      updateResultCount(0, 0);
      return;
    }
    if (emptyState) emptyState.hidden = true;

    if (loadMoreBtn) loadMoreBtn.hidden = true;

    let fit = countRowsThatFit(filtered, false);

    if (fit < filtered.length) {
      if (loadMoreBtn) loadMoreBtn.hidden = false;
      fit = countRowsThatFit(filtered, true);
    }

    if (fit < 1 && filtered.length > 0) {
      fit = 1;
      renderSlice(filtered, 1);
    } else {
      renderSlice(filtered, fit);
    }

    visibleCount = fit;
    updateLoadMore(fit, filtered.length);
    updateResultCount(fit, filtered.length);
  };

  const loadMore = () => {
    const filtered = getFiltered();
    const prev = visibleCount;
    visibleCount = Math.min(filtered.length, visibleCount + MORE_STEP);
    if (visibleCount === prev || !tbody) return;

    const chunk = filtered.slice(prev, visibleCount);
    tbody.insertAdjacentHTML('beforeend', chunk.map(rowHtml).join(''));

    updateLoadMore(visibleCount, filtered.length);
    updateResultCount(visibleCount, filtered.length);
  };

  if (loadMoreBtn) {
    loadMoreBtn.addEventListener('click', loadMore);
  }

  let resizeTimer;
  window.addEventListener('resize', () => {
    clearTimeout(resizeTimer);
    resizeTimer = setTimeout(() => {
      if (allOrders.length) setInitialVisible(getFiltered());
    }, 200);
  });

  const loadOrders = async () => {
    try {
      const res = await fetch(ordersUrl);
      const data = await res.json();
      allOrders = Array.isArray(data) ? data : [];
      setInitialVisible(getFiltered());
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
      setInitialVisible(getFiltered());
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
