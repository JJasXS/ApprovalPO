/* scan-po.js — PO list tabs (to-scan + submitted) */
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
  /** @type {'toScan' | 'submitted'} */
  let activeTab = 'toScan';

  const tbody = document.getElementById('scanTableBody');
  const emptyState = document.getElementById('scanEmpty');
  const resultCount = document.getElementById('scanResultCount');
  const searchInput = document.getElementById('scanSearch');
  const listSentinel = document.getElementById('scanListSentinel');
  const listHint = document.getElementById('scanListHint');
  const tabToScan = document.getElementById('scanTabToScan');
  const tabSubmitted = document.getElementById('scanTabSubmitted');
  const countToScan = document.getElementById('scanCountToScan');
  const countSubmitted = document.getElementById('scanCountSubmitted');
  const listCol1Header = document.getElementById('scanListCol1Header');
  const listDateHeader = document.getElementById('scanListDateHeader');

  const esc = (s) => String(s ?? '').replace(/[&<>"']/g, (c) =>
    ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

  const fmtDate = (s) => {
    if (!s) return '—';
    const d = new Date(s);
    return isNaN(d) ? s : d.toLocaleDateString('en-GB', { day: '2-digit', month: 'short', year: 'numeric' });
  };

  const poDetailHref = (docKey) => `${detailPageUrl}?docKey=${encodeURIComponent(docKey)}`;
  const wantsSubmitted = () => activeTab === 'submitted';

  const matchesPoTab = (o) => Boolean(o.scanSubmitted) === wantsSubmitted();

  const sortPoForTab = (list) => {
    if (activeTab !== 'submitted') return list;
    return list.slice().sort((a, b) => {
      const ta = a.submittedAtUtc ? new Date(a.submittedAtUtc).getTime() : 0;
      const tb = b.submittedAtUtc ? new Date(b.submittedAtUtc).getTime() : 0;
      if (tb !== ta) return tb - ta;
      return String(a.poNumber || '').localeCompare(String(b.poNumber || ''), undefined, {
        sensitivity: 'base',
      });
    });
  };

  const getFiltered = () => {
    const term = searchTerm.toLowerCase().trim();
    const tabbed = sortPoForTab(allOrders.filter(matchesPoTab));
    return term
      ? tabbed.filter((o) => (o.poNumber || '').toLowerCase().includes(term))
      : tabbed;
  };

  const updateTabCounts = () => {
    const toScanN = allOrders.filter((o) => !o.scanSubmitted).length;
    const submittedN = allOrders.filter((o) => o.scanSubmitted).length;
    if (countToScan) countToScan.textContent = String(toScanN);
    if (countSubmitted) countSubmitted.textContent = String(submittedN);
  };

  const normalizeTab = (tab) => {
    const t = String(tab || '').toLowerCase();
    if (t === 'submitted' || t === 'approved') return 'submitted';
    return 'toScan';
  };

  const updateListChrome = () => {
    const submitted = activeTab === 'submitted';

    if (listCol1Header) {
      listCol1Header.textContent = 'PO #';
    }
    if (listDateHeader) {
      listDateHeader.textContent = submitted ? 'Submitted' : 'Date';
    }
    if (searchInput) {
      searchInput.placeholder = 'Search PO #…';
    }
    if (listHint) {
      if (submitted) {
        listHint.textContent = 'Submitted POs (newest first) — tap to view or reopen for scanning.';
      } else {
        listHint.textContent = 'ERP-approved POs to scan — tap a row to open lines.';
      }
    }
    if (emptyState) {
      if (submitted) emptyState.textContent = 'No submitted POs yet.';
      else emptyState.textContent = 'No POs waiting to scan.';
    }
  };

  const setActiveTab = (tab) => {
    activeTab = normalizeTab(tab);
    const toScanOn = activeTab === 'toScan';
    const submittedOn = activeTab === 'submitted';

    if (tabToScan) {
      tabToScan.classList.toggle('is-active', toScanOn);
      tabToScan.setAttribute('aria-selected', toScanOn ? 'true' : 'false');
    }
    if (tabSubmitted) {
      tabSubmitted.classList.toggle('is-active', submittedOn);
      tabSubmitted.setAttribute('aria-selected', submittedOn ? 'true' : 'false');
    }
    updateListChrome();

    setInitialVisible(getFiltered());
  };

  const submittedMetaHtml = (o) => {
    if (activeTab !== 'submitted' || !o.scanSubmitted) return '';
    const who = (o.submittedByName || '').trim();
    if (!who) return '';
    return `<span class="scan-row-meta">${esc(who)}</span>`;
  };

  const rowListDatePo = (o) => {
    if (activeTab === 'submitted' && o.submittedAtUtc) return fmtDate(o.submittedAtUtc);
    return fmtDate(o.orderDate);
  };

  const rowHtmlPo = (o) => {
    const href = poDetailHref(o.docKey);
    const submittedTitle =
      activeTab === 'submitted' && o.submittedAtUtc
        ? ` title="Submitted ${fmtDate(o.submittedAtUtc)}"`
        : '';
    return `
      <tr class="scan-row scan-row--link" data-href="${esc(href)}" role="link" tabindex="0">
        <td class="scan-col-po">${esc(o.poNumber || '—')}${submittedMetaHtml(o)}</td>
        <td class="scan-col-date"${submittedTitle}>${rowListDatePo(o)}</td>
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
    const label =
      activeTab === 'submitted' ? 'submitted' : 'to scan';
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

  const initTabFromUrl = () => {
    const tab = new URLSearchParams(location.search).get('tab');
    if (tab) setActiveTab(tab);
    else updateListChrome();
  };

  if (tabToScan) tabToScan.addEventListener('click', () => setActiveTab('toScan'));
  if (tabSubmitted) tabSubmitted.addEventListener('click', () => setActiveTab('submitted'));

  const loadOrders = async () => {
    try {
      const res = await fetch(ordersUrl);
      const data = await res.json();
      allOrders = Array.isArray(data) ? data : [];
      updateTabCounts();
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

  initTabFromUrl();
  loadOrders();
})();
