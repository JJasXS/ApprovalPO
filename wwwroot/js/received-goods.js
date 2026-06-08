/* received-goods.js — PH_GR list (separate from Scan PO) */
(function () {
  'use strict';

  const cfg = document.getElementById('receivedGoodsConfig');
  if (!cfg) return;

  const listUrl = cfg.dataset.listJsonUrl || '';
  const detailPageUrl = (cfg.dataset.detailPageUrl || '/ScanReceivedDetail').replace(/\/$/, '');

  const MORE_STEP = 10;
  const VIEWPORT_PAD = 12;

  let allReceipts = [];
  let searchTerm = '';
  let visibleCount = 0;

  const tbody = document.getElementById('grTableBody');
  const emptyState = document.getElementById('grEmpty');
  const resultCount = document.getElementById('grResultCount');
  const searchInput = document.getElementById('grSearch');
  const loadMoreBtn = document.getElementById('grLoadMoreBtn');
  const listFooter = document.querySelector('.scan-list-footer');

  const esc = (s) =>
    String(s ?? '').replace(/[&<>"']/g, (c) =>
      ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c])
    );

  const fmtDate = (s) => {
    if (!s) return '—';
    const d = new Date(s);
    return isNaN(d) ? s : d.toLocaleDateString('en-GB', { day: '2-digit', month: 'short', year: 'numeric' });
  };

  const detailHref = (docKey) => `${detailPageUrl}?docKey=${encodeURIComponent(docKey)}`;

  const getFiltered = () => {
    const term = searchTerm.toLowerCase().trim();
    const sorted = allReceipts.slice().sort((a, b) => {
      const da = a.grDate ? new Date(a.grDate).getTime() : 0;
      const db = b.grDate ? new Date(b.grDate).getTime() : 0;
      if (db !== da) return db - da;
      return String(a.grNumber || '').localeCompare(String(b.grNumber || ''), undefined, {
        sensitivity: 'base',
      });
    });
    if (!term) return sorted;
    return sorted.filter((r) => {
      const gr = (r.grNumber || '').toLowerCase();
      const po = (r.poNumber || '').toLowerCase();
      const vendor = (r.vendor || '').toLowerCase();
      return gr.includes(term) || po.includes(term) || vendor.includes(term);
    });
  };

  const vendorMeta = (r) => {
    const v = (r.vendor || '').trim();
    if (!v) return '';
    return `<span class="scan-row-meta">${esc(v)}</span>`;
  };

  const rowHtml = (r) => {
    const href = detailHref(r.docKey);
    return `
      <tr class="scan-row scan-row--link scan-row--gr" data-href="${esc(href)}" role="link" tabindex="0">
        <td class="scan-col-gr">${esc(r.grNumber || '—')}${vendorMeta(r)}</td>
        <td class="scan-col-po-ref">${esc(r.poNumber || '—')}</td>
        <td class="scan-col-date">${fmtDate(r.grDate)}</td>
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
    const moreH = reserveMoreBtn && loadMoreBtn ? loadMoreBtn.offsetHeight + 6 : 0;
    return window.innerHeight - VIEWPORT_PAD - moreH;
  };

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
    loadMoreBtn.setAttribute('aria-label', `Load ${Math.min(MORE_STEP, remaining)} more (${remaining} remaining)`);
  };

  const updateResultCount = (shown, total) => {
    if (!resultCount) return;
    if (total === 0) {
      resultCount.textContent = '0 receipts';
      return;
    }
    if (shown < total) {
      resultCount.textContent = `Showing ${shown} of ${total} receipts`;
      return;
    }
    resultCount.textContent = `${total} receipt${total === 1 ? '' : 's'}`;
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

  if (loadMoreBtn) loadMoreBtn.addEventListener('click', loadMore);

  let resizeTimer;
  window.addEventListener('resize', () => {
    clearTimeout(resizeTimer);
    resizeTimer = setTimeout(() => {
      if (allReceipts.length) setInitialVisible(getFiltered());
    }, 200);
  });

  const loadList = async () => {
    try {
      const res = await fetch(listUrl);
      const data = await res.json();
      if (!res.ok) {
        throw new Error(data?.error || res.statusText || 'Failed to load');
      }
      allReceipts = Array.isArray(data) ? data : [];
      setInitialVisible(getFiltered());
    } catch (e) {
      if (tbody) {
        tbody.innerHTML = `<tr><td colspan="3" class="scan-empty">Failed to load goods receipts: ${esc(e.message)}</td></tr>`;
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

  loadList();
})();
