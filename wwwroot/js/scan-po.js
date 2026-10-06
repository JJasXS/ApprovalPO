/* scan-po.js — purchase orders still waiting to scan */
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
  const listSentinel = document.getElementById('scanListSentinel');

  const esc = (s) => String(s ?? '').replace(/[&<>"']/g, (c) =>
    ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

  const fmtDate = (s) => {
    if (!s) return '—';
    const d = new Date(s);
    return isNaN(d) ? s : d.toLocaleDateString('en-GB', { day: '2-digit', month: 'short', year: 'numeric' });
  };

  const poDetailHref = (docKey) => `${detailPageUrl}?docKey=${encodeURIComponent(docKey)}`;

  const waitingToScan = () => allOrders.filter((o) => !o.scanSubmitted);

  const getFiltered = () => {
    const term = searchTerm.toLowerCase().trim();
    const waiting = waitingToScan();
    return term
      ? waiting.filter((o) => (o.poNumber || '').toLowerCase().includes(term))
      : waiting;
  };

  const rowHtmlPo = (o) => {
    const href = poDetailHref(o.docKey);
    return `
      <tr class="scan-row scan-row--link" data-href="${esc(href)}" role="link" tabindex="0">
        <td class="scan-col-po">${esc(o.poNumber || '—')}</td>
        <td class="scan-col-date">${fmtDate(o.orderDate)}</td>
      </tr>`;
  };

  const rowHtml = (item) => rowHtmlPo(item);

  let loadingMore = false;

  const contentBottom = () => {
    const rows = tbody?.querySelectorAll('tr.scan-row');
    if (!rows?.length) return 0;
    return rows[rows.length - 1].getBoundingClientRect().bottom;
  };

  const viewportLimit = () => window.innerHeight - VIEWPORT_PAD;

  const countRowsThatFit = (filtered) => {
    if (!tbody || filtered.length === 0) return 0;

    const limit = viewportLimit();
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

  const updateResultCount = (shown, total) => {
    if (!resultCount) return;
    const label = 'to scan';
    if (total === 0) {
      resultCount.textContent = `0 ${label}`;
      return;
    }
    if (shown < total) {
      resultCount.textContent = `Showing ${shown} of ${total} ${label}`;
      return;
    }
    resultCount.textContent = `${total} ${label}`;
  };

  const renderSlice = (filtered, count) => {
    const shown = Math.min(count, filtered.length);
    tbody.innerHTML = filtered.slice(0, shown).map(rowHtml).join('');
    return shown;
  };

  const hasMore = () => visibleCount < getFiltered().length;

  const pageCanScroll = () =>
    document.documentElement.scrollHeight > window.innerHeight + 8;

  const sentinelInRange = () => {
    if (!listSentinel) return false;
    return listSentinel.getBoundingClientRect().top < window.innerHeight + 80;
  };

  const setInitialVisible = (filtered) => {
    loadingMore = false;
    if (filtered.length === 0) {
      visibleCount = 0;
      if (tbody) tbody.innerHTML = '';
      if (emptyState) emptyState.hidden = false;
      updateResultCount(0, 0);
      return;
    }
    if (emptyState) emptyState.hidden = true;

    let fit = countRowsThatFit(filtered);

    if (fit < 1 && filtered.length > 0) {
      fit = 1;
      renderSlice(filtered, 1);
    } else {
      renderSlice(filtered, fit);
    }

    visibleCount = fit;
    updateResultCount(fit, filtered.length);
    fillUntilScrollable();
  };

  const loadMore = () => {
    if (loadingMore) return;
    const filtered = getFiltered();
    const prev = visibleCount;
    if (prev >= filtered.length || !tbody) return;
    loadingMore = true;
    visibleCount = Math.min(filtered.length, visibleCount + MORE_STEP);
    const chunk = filtered.slice(prev, visibleCount);
    tbody.insertAdjacentHTML('beforeend', chunk.map(rowHtml).join(''));
    updateResultCount(visibleCount, filtered.length);
    loadingMore = false;
  };

  const fillUntilScrollable = () => {
    let steps = 0;
    while (hasMore() && steps < 30) {
      if (pageCanScroll() && !sentinelInRange()) break;
      const before = visibleCount;
      loadMore();
      if (visibleCount === before) break;
      steps += 1;
    }
  };

  if (listSentinel && 'IntersectionObserver' in window) {
    const observer = new IntersectionObserver(
      (entries) => {
        if (!entries.some((e) => e.isIntersecting)) return;
        fillUntilScrollable();
      },
      { root: null, rootMargin: '80px', threshold: 0 }
    );
    observer.observe(listSentinel);
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
        tbody.innerHTML = `<tr><td colspan="2" class="scan-empty">Failed to load orders: ${esc(e.message)}</td></tr>`;
      }
    }
  };

  if (searchInput) {
    searchInput.addEventListener('input', () => {
      searchTerm = searchInput.value;
      setInitialVisible(getFiltered());
    });
  }

  if (tbody) {
    tbody.addEventListener('click', (e) => {
      const row = e.target.closest('tr.scan-row--link');
      const href = row?.dataset?.href;
      if (href) window.location.href = href;
    });
    tbody.addEventListener('keydown', (e) => {
      if (e.key !== 'Enter' && e.key !== ' ') return;
      const row = e.target.closest('tr.scan-row--link');
      const href = row?.dataset?.href;
      if (!href) return;
      e.preventDefault();
      window.location.href = href;
    });
  }

  loadOrders();
})();
