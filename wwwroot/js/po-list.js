(() => {
  const cfgEl = document.getElementById('poPageConfig');
  const ordersJsonUrl = cfgEl?.dataset.ordersJsonUrl || '';
  const highValueThreshold = parseFloat(String(cfgEl?.dataset.highValueThreshold || '5000'), 10) || 5000;

  const modal = document.getElementById('reviewModal');
  const reviewBody = document.getElementById('reviewBody');
  const modalApproveBtn = document.getElementById('modalApproveBtn');
  const confirmModal = document.getElementById('confirmModal');
  const confirmBody = document.getElementById('confirmBody');
  const confirmOkBtn = document.getElementById('confirmOkBtn');
  const confirmTitle = document.getElementById('confirmTitle');
  const toast = document.getElementById('toast');
  const undoToast = document.getElementById('undoToast');
  const undoToastMsg = document.getElementById('undoToastMsg');
  const undoBtn = document.getElementById('undoBtn');
  const tabPending = document.getElementById('tabPending');
  const tabApproved = document.getElementById('tabApproved');
  const tabRejected = document.getElementById('tabRejected');
  const countPending = document.getElementById('countPending');
  const countApproved = document.getElementById('countApproved');
  const countRejected = document.getElementById('countRejected');
  const poEmptyState = document.getElementById('poEmptyState');
  const poTableScroll = document.getElementById('poTableScroll');
  const poSkeleton = document.getElementById('poSkeleton');
  const poTableWrap = document.getElementById('poTableWrap');
  const tbody = document.getElementById('poTableBody');
  const refreshBtn = document.getElementById('refreshBtn');
  const bulkApproveBtn = document.getElementById('bulkApproveBtn');
  const bulkSelectAll = document.getElementById('bulkSelectAll');
  const filterVendor = document.getElementById('filterVendor');
  const filterDateFrom = document.getElementById('filterDateFrom');
  const filterClearBtn = document.getElementById('filterClearBtn');
  const menuBtn = document.getElementById('menuBtn');
  const menuPanel = document.getElementById('menuPanel');
  const modalPrevBtn = document.getElementById('modalPrevBtn');
  const modalNextBtn = document.getElementById('modalNextBtn');
  const tableHeadEl = document.querySelector('.po-table-wrap__head');
  const headerEl = document.querySelector('.po-header');

  let rows = Array.from(document.querySelectorAll('.po-row'));
  let currentRow = null;
  /** @type {'Pending' | 'Approved' | 'Rejected'} */
  let activeListTab = 'Pending';
  let undoTimer = null;
  let pullStartY = null;
  let pullArmed = false;
  /** @type {null | (() => void)} */
  let confirmOkCallback = null;

  const filters = {
    vendor: '',
    dateFrom: '',
  };

  const showToast = (text) => {
    if (!toast) return;
    toast.textContent = text;
    toast.classList.add('show');
    clearTimeout(showToast._t);
    showToast._t = setTimeout(() => toast.classList.remove('show'), 3200);
  };

  const setLoading = (on) => {
    if (poSkeleton) poSkeleton.hidden = !on;
    if (poTableWrap) poTableWrap.classList.toggle('is-loading', !!on);
  };

  const syncStickyHeights = () => {
    if (headerEl) {
      const hh = headerEl.getBoundingClientRect().height || 0;
      document.documentElement.style.setProperty('--po-header-height', `${Math.ceil(hh)}px`);
    }
    if (tableHeadEl) {
      const h = tableHeadEl.getBoundingClientRect().height || 0;
      document.documentElement.style.setProperty('--po-table-wrap-head-height', `${Math.ceil(h)}px`);
    }
  };

  const escapeHtml = (s) => {
    const d = document.createElement('div');
    d.textContent = s == null ? '' : String(s);
    return d.innerHTML;
  };

  const formatDisplayDate = (isoYmd) => {
    if (!isoYmd || String(isoYmd).length < 10) return '—';
    const d = new Date(`${String(isoYmd).slice(0, 10)}T12:00:00`);
    if (Number.isNaN(d.getTime())) return '—';
    return d.toLocaleDateString(undefined, { day: 'numeric', month: 'short', year: 'numeric' });
  };

  const statusStackClass = (status) =>
    status === 'Approved' ? 'po-status-stack--approved' : status === 'Rejected' ? 'po-status-stack--rejected' : 'po-status-stack--pending';
  const statusDisplay = (status) => (status === 'Rejected' ? 'Cancelled' : status);

  const rowFromOrder = (o) => {
    const po = o.poNumber || '';
    const vendor = o.vendor || '';
    const amount =
      typeof o.amount === 'number' ? o.amount.toFixed(2) : String(o.amount ?? '0');
    const status = o.status || 'Pending';
    const desc = o.description || '';
    const transfer = o.transferable === true ? 'true' : '';
    const dateStr = (o.orderDate && String(o.orderDate).slice(0, 10)) || '';
    const chk =
      status === 'Pending'
        ? `<input type="checkbox" class="row-select" aria-label="Select ${escapeHtml(po)}" />`
        : '';
    const amtNum = Number.parseFloat(amount) || 0;
    const stack = statusStackClass(status);
    return `<tr class="po-row" data-po="${escapeHtml(po)}" data-vendor="${escapeHtml(vendor)}" data-amount="${escapeHtml(amount)}" data-transferable="${transfer}" data-status="${escapeHtml(status)}" data-description="${escapeHtml(desc)}" data-order-date="${escapeHtml(dateStr)}">
      <td class="td-check" data-label="">${chk}</td>
      <td class="po-status-td" data-label=""><div class="po-status-stack ${stack}"><span class="po-status-icon" aria-hidden="true"></span><span class="po-status-text">${escapeHtml(statusDisplay(status))}</span></div></td>
      <td class="po-num" data-label="PO #"><span class="po-num-text">${escapeHtml(po)}</span></td>
      <td class="po-vendor" data-label="Vendor">${escapeHtml(vendor)}</td>
      <td class="po-agent" data-label="Created by">—</td>
      <td class="num po-date" data-label="Date">${escapeHtml(formatDisplayDate(dateStr))}</td>
      <td class="num po-amount" data-label="Amount">${amtNum.toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 })}</td>
      <td class="actions" data-label="">
        <div class="actions-row-block"><div class="actions-inner">
          <button type="button" class="po-btn po-btn--review btn-review" data-action="review">Review</button>
          <button type="button" class="po-btn po-btn--reject btn-reject" data-action="reject">Reject</button>
          <button type="button" class="po-btn po-btn--approve btn-approve" data-action="approve">Approve</button>
        </div></div></td></tr>`;
  };

  const refreshRowsFromServer = async () => {
    if (!ordersJsonUrl || !tbody) return;
    setLoading(true);
    try {
      const res = await fetch(ordersJsonUrl, { method: 'GET', cache: 'no-store' });
      const data = await res.json();
      if (!res.ok || !Array.isArray(data)) throw new Error('Bad response');
      tbody.innerHTML = data.map(rowFromOrder).join('');
      rows = Array.from(document.querySelectorAll('.po-row'));
      if (bulkSelectAll) bulkSelectAll.checked = false;
      refreshView();
      showToast('List updated.');
    } catch {
      showToast('Refresh failed. Try again.');
    } finally {
      setLoading(false);
    }
  };

  const openModal = (el) => {
    if (!el) return;
    el.classList.add('show');
    el.setAttribute('aria-hidden', 'false');
  };

  const closeModal = (el) => {
    if (!el) return;
    el.classList.remove('show');
    el.setAttribute('aria-hidden', 'true');
  };

  const openConfirm = (title, bodyHtml, onOk) => {
    if (!confirmModal || !confirmTitle || !confirmBody || !confirmOkBtn) return;
    confirmOkCallback = onOk;
    confirmTitle.textContent = title;
    confirmBody.innerHTML = bodyHtml;
    openModal(confirmModal);
  };

  const dismissConfirm = () => {
    confirmOkCallback = null;
    closeModal(confirmModal);
  };

  modal?.querySelectorAll('[data-close]').forEach((el) => {
    el.addEventListener('click', () => {
      closeModal(modal);
      currentRow = null;
    });
  });

  confirmModal?.querySelectorAll('[data-confirm-close]').forEach((el) => {
    el.addEventListener('click', () => dismissConfirm());
  });

  confirmOkBtn?.addEventListener('click', () => {
    const fn = confirmOkCallback;
    confirmOkCallback = null;
    closeModal(confirmModal);
    if (typeof fn === 'function') fn();
  });

  menuBtn?.addEventListener('click', (e) => {
    e.stopPropagation();
    if (!menuPanel) return;
    const next = menuPanel.hasAttribute('hidden');
    if (next) menuPanel.removeAttribute('hidden');
    else menuPanel.setAttribute('hidden', '');
    menuBtn.setAttribute('aria-expanded', next ? 'true' : 'false');
  });

  document.addEventListener('click', (e) => {
    if (!menuPanel || !menuBtn) return;
    if (menuPanel.hasAttribute('hidden')) return;
    if (menuPanel.contains(e.target) || menuBtn.contains(e.target)) return;
    menuPanel.setAttribute('hidden', '');
    menuBtn.setAttribute('aria-expanded', 'false');
  });

  const getMockItems = (po, vendor) => {
    const seed = Number((po || '').replace(/\D/g, '').slice(-2)) || 1;
    const v = vendor || '';
    return [
      {
        code: `ITM-${(100 + seed).toString()}`,
        name: `${v} — Main supply`,
        qty: 1 + (seed % 3),
        unitPrice: 120 + seed * 2
      },
      {
        code: `ITM-${(200 + seed).toString()}`,
        name: 'Support material',
        qty: 2 + (seed % 4),
        unitPrice: 45 + seed
      },
      {
        code: `ITM-${(300 + seed).toString()}`,
        name: 'Service charge',
        qty: 1,
        unitPrice: 80 + seed * 1.5
      }
    ];
  };

  const getVisibleRowsInList = () => rows.filter((r) => !r.classList.contains('is-hidden'));

  const updateModalNav = () => {
    if (!modalPrevBtn || !modalNextBtn || !currentRow) return;
    const visible = getVisibleRowsInList();
    const idx = visible.indexOf(currentRow);
    const hasPrev = idx > 0;
    const hasNext = idx >= 0 && idx < visible.length - 1;
    modalPrevBtn.disabled = !hasPrev;
    modalNextBtn.disabled = !hasNext;
    modalPrevBtn.setAttribute('aria-disabled', hasPrev ? 'false' : 'true');
    modalNextBtn.setAttribute('aria-disabled', hasNext ? 'false' : 'true');
  };

  const navigateModal = (delta) => {
    if (!currentRow) return;
    const visible = getVisibleRowsInList();
    const idx = visible.indexOf(currentRow);
    if (idx < 0) return;
    const nextIdx = idx + delta;
    if (nextIdx < 0 || nextIdx >= visible.length) return;
    const target = visible[nextIdx];
    currentRow = target;
    renderReview(target);
    updateModalNav();
    target.scrollIntoView({ block: 'nearest' });
  };

  const renderReview = (tr) => {
    if (!reviewBody || !modalApproveBtn) return;
    const modalRejectBtnEl = document.getElementById('modalRejectBtn');
    const po = tr.dataset.po || '';
    const vendor = tr.dataset.vendor || '';
    const status = tr.dataset.status || '';
    const desc = tr.dataset.description || '';
    const orderIso = tr.dataset.orderDate || '';
    const displayDate = formatDisplayDate(orderIso);
    const amt = parseAmount(tr);
    const amtStr = amt.toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 });
    let statusClass = 'review-badge--pending';
    if (status === 'Approved') statusClass = 'review-badge--approved';
    else if (status === 'Rejected') statusClass = 'review-badge--rejected';

    const items = getMockItems(po, vendor);
    const totalQty = items.reduce((sum, item) => sum + item.qty, 0);
    const lineTotal = (item) => item.qty * item.unitPrice;
    const itemsTotal = items.reduce((sum, item) => sum + lineTotal(item), 0);
    const itemsHtml = items
      .map(
        (item) => `
      <tr>
        <td class="items-check">
          <label class="po-check" aria-label="Select item ${escapeHtml(item.code)}">
            <input type="checkbox" checked />
            <span class="po-check__box" aria-hidden="true"></span>
          </label>
        </td>
        <td class="items-code">${escapeHtml(item.code)}</td>
        <td class="items-desc">${escapeHtml(item.name)}</td>
        <td class="num">${item.qty}</td>
        <td class="num">${lineTotal(item).toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 })}</td>
      </tr>
    `
      )
      .join('');

    const note =
      amt >= highValueThreshold && status === 'Pending'
        ? '<div class="modal__note">High-value PO — confirm approval aligns with your policy.</div>'
        : '';

    reviewBody.innerHTML = `
      <div class="review-hero">
        <div class="review-hero__po">${escapeHtml(po)}</div>
        <span class="review-badge ${statusClass}">${escapeHtml(statusDisplay(status))}</span>
      </div>

      <div class="review-card">
        <div class="review-grid">
          <div class="review-field">
            <div class="review-label">Company</div>
            <div class="review-value">${escapeHtml(vendor)}</div>
          </div>
          <div class="review-field">
            <div class="review-label">Date</div>
            <div class="review-value">${escapeHtml(displayDate)}</div>
          </div>
          <div class="review-field">
            <div class="review-label">Agent</div>
            <div class="review-value">—</div>
          </div>
          <div class="review-field">
            <div class="review-label">Amount</div>
            <div class="review-value review-value--amount">${escapeHtml(amtStr)}</div>
          </div>
          <div class="review-field review-field--full">
            <div class="review-label">Description</div>
            <div class="review-value">${escapeHtml(desc) || '—'}</div>
          </div>
        </div>
      </div>

      ${note}

      <div class="items-title">Line items</div>
      <div class="review-card review-card--items">
        <div class="items-wrap">
          <table class="items-table">
            <thead>
              <tr>
                <th class="items-th-check" aria-label="Select"></th>
                <th>Item code</th>
                <th>Description</th>
                <th class="num">Qty</th>
                <th class="num">Amount</th>
              </tr>
            </thead>
            <tbody>${itemsHtml}</tbody>
            <tfoot>
              <tr>
                <td colspan="3" class="items-total-label">Total</td>
                <td class="num items-total-qty">${totalQty}</td>
                <td class="num items-total-amt">${itemsTotal.toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 })}</td>
              </tr>
            </tfoot>
          </table>
        </div>
      </div>
    `;

    const pending = status === 'Pending';
    modalApproveBtn.disabled = !pending;
    modalApproveBtn.title =
      status === 'Approved' ? 'Already approved' : status === 'Rejected' ? 'Cannot approve rejected PO' : '';
    if (modalRejectBtnEl) {
      modalRejectBtnEl.disabled = !pending;
      modalRejectBtnEl.style.display = pending ? '' : 'none';
    }

    updateModalNav();
  };

  const parseAmount = (tr) => Number.parseFloat(String(tr.dataset.amount || '0')) || 0;

  const showUndo = (msg, revertFn) => {
    if (!undoToast || !undoToastMsg || !undoBtn) return;
    if (undoTimer) clearTimeout(undoTimer);
    undoToastMsg.textContent = msg;
    undoToast.hidden = false;
    undoBtn.onclick = () => {
      revertFn();
      undoToast.hidden = true;
      if (undoTimer) clearTimeout(undoTimer);
      undoBtn.onclick = null;
    };
    undoTimer = setTimeout(() => {
      undoToast.hidden = true;
      undoBtn.onclick = null;
    }, 8000);
  };

  /**
   * @param {HTMLElement} tr
   * @param {{ quiet?: boolean }} [opts]
   */
  const approveRowInternal = (tr, opts = {}) => {
    const { quiet = false } = opts;
    tr.dataset.status = 'Approved';
    tr.dataset.transferable = 'true';
    const approveBtn = tr.querySelector('.btn-approve');
    const rejectBtn = tr.querySelector('.btn-reject');
    if (approveBtn) approveBtn.style.display = 'none';
    if (rejectBtn) rejectBtn.style.display = 'none';
    const cb = tr.querySelector('.row-select');
    if (cb) {
      cb.checked = false;
      cb.disabled = true;
    }
    if (!quiet) showToast(`PO ${tr.dataset.po} approved.`);
    refreshView();
  };

  const revertRowApproval = (tr, prev) => {
    tr.dataset.status = prev.status;
    tr.dataset.transferable = prev.transferable;
    const ab = tr.querySelector('.btn-approve');
    const rb = tr.querySelector('.btn-reject');
    if (ab) {
      ab.style.display = '';
      ab.disabled = prev.status !== 'Pending';
    }
    if (rb) {
      rb.style.display = '';
      rb.disabled = prev.status !== 'Pending';
    }
    const c = tr.querySelector('.row-select');
    if (c) {
      c.disabled = false;
    }
  };

  const runApprove = (tr) => {
    const amt = parseAmount(tr);
    const doApprove = () => {
      const prev = {
        status: tr.dataset.status,
        transferable: tr.dataset.transferable || ''
      };
      approveRowInternal(tr, { quiet: true });
      showUndo('Approved.', () => {
        revertRowApproval(tr, prev);
        refreshView();
      });
    };

    if (amt >= highValueThreshold) {
      openConfirm(
        'Confirm high-value approval',
        `<p>Approve <strong>${escapeHtml(tr.dataset.po)}</strong> for <strong>${amt.toLocaleString(undefined, { style: 'currency', currency: 'USD' })}</strong>?</p>`,
        doApprove
      );
      return;
    }
    doApprove();
  };

  const approveRow = (tr) => {
    if (tr.dataset.status === 'Approved') {
      showToast('Already approved.');
      return;
    }
    runApprove(tr);
  };

  const rejectRow = (tr) => {
    if (tr.dataset.status !== 'Pending') {
      showToast('Only pending PO can be cancelled.');
      return;
    }
    const prev = {
      status: tr.dataset.status,
      transferable: tr.dataset.transferable || ''
    };
    tr.dataset.status = 'Rejected';
    tr.dataset.transferable = '';
    const ab = tr.querySelector('.btn-approve');
    const rb = tr.querySelector('.btn-reject');
    if (ab) {
      ab.style.display = 'none';
      ab.disabled = true;
    }
    if (rb) {
      rb.style.display = 'none';
      rb.disabled = true;
    }
    const c = tr.querySelector('.row-select');
    if (c) {
      c.checked = false;
      c.disabled = true;
    }
    showUndo('Rejected.', () => {
      tr.dataset.status = prev.status;
      tr.dataset.transferable = prev.transferable;
      if (ab) {
        ab.style.display = '';
        ab.disabled = false;
      }
      if (rb) {
        rb.style.display = '';
        rb.disabled = false;
      }
      if (c) c.disabled = false;
      refreshView();
    });
    refreshView();
  };

  const updateRowButtons = (tr) => {
    const status = tr.dataset.status || 'Pending';
    const approveBtn = tr.querySelector('.btn-approve');
    const rejectBtn = tr.querySelector('.btn-reject');
    if (approveBtn) {
      approveBtn.style.display = status === 'Pending' ? '' : 'none';
      approveBtn.disabled = status !== 'Pending';
    }
    if (rejectBtn) {
      rejectBtn.style.display = status === 'Pending' ? '' : 'none';
      rejectBtn.disabled = status !== 'Pending';
    }
  };

  const rowMatchesFilters = (tr) => {
    const vendor = (tr.dataset.vendor || '').toLowerCase();
    const od = tr.dataset.orderDate || '';
    if (filters.vendor && vendor !== filters.vendor.toLowerCase()) return false;
    if (filters.dateFrom && od < filters.dateFrom) return false;
    return true;
  };

  const updateTabUi = () => {
    if (!tabPending || !tabApproved || !tabRejected) return;
    const pendingOn = activeListTab === 'Pending';
    const approvedOn = activeListTab === 'Approved';
    const rejectedOn = activeListTab === 'Rejected';
    tabPending.classList.toggle('is-active', pendingOn);
    tabApproved.classList.toggle('is-active', approvedOn);
    tabRejected.classList.toggle('is-active', rejectedOn);
    tabPending.setAttribute('aria-selected', pendingOn ? 'true' : 'false');
    tabApproved.setAttribute('aria-selected', approvedOn ? 'true' : 'false');
    tabRejected.setAttribute('aria-selected', rejectedOn ? 'true' : 'false');
  };

  const setListTab = (tab) => {
    if (tab === 'Approved') activeListTab = 'Approved';
    else if (tab === 'Rejected') activeListTab = 'Rejected';
    else activeListTab = 'Pending';
    setLoading(true);
    window.setTimeout(() => {
      updateTabUi();
      refreshView();
      setLoading(false);
    }, 220);
  };

  const syncBulkHeaderCheckbox = () => {
    if (!bulkSelectAll) return;
    const visiblePending = rows.filter(
      (r) => !r.classList.contains('is-hidden') && r.dataset.status === 'Pending'
    );
    if (visiblePending.length === 0) {
      bulkSelectAll.checked = false;
      bulkSelectAll.indeterminate = false;
      return;
    }
    const checked = visiblePending.filter((r) => r.querySelector('.row-select')?.checked).length;
    if (checked === 0) {
      bulkSelectAll.checked = false;
      bulkSelectAll.indeterminate = false;
    } else if (checked === visiblePending.length) {
      bulkSelectAll.checked = true;
      bulkSelectAll.indeterminate = false;
    } else {
      bulkSelectAll.checked = false;
      bulkSelectAll.indeterminate = true;
    }
  };

  const refreshView = () => {
    const pendingCount = rows.filter((r) => (r.dataset.status || 'Pending') === 'Pending').length;
    const approvedCount = rows.filter((r) => r.dataset.status === 'Approved').length;
    const rejectedCount = rows.filter((r) => r.dataset.status === 'Rejected').length;
    if (countPending) countPending.textContent = String(pendingCount);
    if (countApproved) countApproved.textContent = String(approvedCount);
    if (countRejected) countRejected.textContent = String(rejectedCount);

    let visibleCount = 0;
    let countInTab = 0;
    rows.forEach((tr) => {
      updateRowButtons(tr);
      const status = tr.dataset.status || 'Pending';
      const matchesStatus =
        activeListTab === 'Pending'
          ? status === 'Pending'
          : activeListTab === 'Approved'
            ? status === 'Approved'
            : status === 'Rejected';
      if (matchesStatus) countInTab++;
      const matchesFilters = rowMatchesFilters(tr);
      const shouldShow = matchesStatus && matchesFilters;
      tr.classList.toggle('is-hidden', !shouldShow);
      if (shouldShow) visibleCount++;
    });

    syncBulkHeaderCheckbox();

    const selectedPending = rows.filter(
      (r) =>
        !r.classList.contains('is-hidden') &&
        r.dataset.status === 'Pending' &&
        r.querySelector('.row-select')?.checked
    );
    if (bulkApproveBtn) bulkApproveBtn.disabled = selectedPending.length === 0;

    if (poEmptyState && poTableScroll) {
      const emptyTextEl = poEmptyState.querySelector('.po-empty__text');
      if (visibleCount === 0) {
        poEmptyState.hidden = false;
        const msg =
          countInTab === 0
            ? activeListTab === 'Pending'
              ? 'No pending purchase orders.'
              : activeListTab === 'Approved'
                ? 'No approved purchase orders.'
                : 'No cancelled purchase orders.'
            : 'No purchase orders match your filters.';
        if (emptyTextEl) emptyTextEl.textContent = msg;
        else poEmptyState.textContent = msg;
        poTableScroll.hidden = true;
      } else {
        poEmptyState.hidden = true;
        poTableScroll.hidden = false;
      }
    }
  };

  tbody?.addEventListener('click', (e) => {
    const reviewBtn = e.target.closest('.btn-review');
    if (reviewBtn) {
      const tr = reviewBtn.closest('tr');
      if (!tr) return;
      currentRow = tr;
      renderReview(tr);
      openModal(modal);
      updateModalNav();
      return;
    }
    const approveBtn = e.target.closest('.btn-approve');
    if (approveBtn) {
      const tr = approveBtn.closest('tr');
      if (tr) approveRow(tr);
      return;
    }
    const rejectBtn = e.target.closest('.btn-reject');
    if (rejectBtn) {
      const tr = rejectBtn.closest('tr');
      if (tr) rejectRow(tr);
    }
  });

  modalApproveBtn?.addEventListener('click', () => {
    if (currentRow) approveRow(currentRow);
    closeModal(modal);
    currentRow = null;
  });

  const modalRejectBtn = document.getElementById('modalRejectBtn');
  modalRejectBtn?.addEventListener('click', () => {
    if (!currentRow || currentRow.dataset.status !== 'Pending') return;
    rejectRow(currentRow);
    closeModal(modal);
    currentRow = null;
  });

  modalPrevBtn?.addEventListener('click', () => navigateModal(-1));
  modalNextBtn?.addEventListener('click', () => navigateModal(1));

  tabPending?.addEventListener('click', () => setListTab('Pending'));
  tabApproved?.addEventListener('click', () => setListTab('Approved'));
  tabRejected?.addEventListener('click', () => setListTab('Rejected'));
  filterVendor?.addEventListener('change', () => {
    filters.vendor = (filterVendor.value || '').trim();
    refreshView();
  });
  filterDateFrom?.addEventListener('change', () => {
    filters.dateFrom = filterDateFrom.value || '';
    refreshView();
  });

  filterClearBtn?.addEventListener('click', () => {
    if (filterVendor) filterVendor.value = '';
    if (filterDateFrom) filterDateFrom.value = '';
    filters.vendor = '';
    filters.dateFrom = '';
    refreshView();
  });

  refreshBtn?.addEventListener('click', () => refreshRowsFromServer());

  bulkSelectAll?.addEventListener('change', () => {
    const on = bulkSelectAll.checked;
    rows.forEach((tr) => {
      if (tr.classList.contains('is-hidden')) return;
      if (tr.dataset.status !== 'Pending') return;
      const c = tr.querySelector('.row-select');
      if (c) c.checked = on;
    });
    refreshView();
  });

  tbody?.addEventListener('change', (e) => {
    if (e.target.classList?.contains('row-select')) refreshView();
  });

  bulkApproveBtn?.addEventListener('click', () => {
    const selected = rows.filter(
      (r) =>
        !r.classList.contains('is-hidden') &&
        r.dataset.status === 'Pending' &&
        r.querySelector('.row-select')?.checked
    );
    if (selected.length === 0) return;
    const anyHigh = selected.some((r) => parseAmount(r) >= highValueThreshold);
    const lines = selected
      .map(
        (r) =>
          `${escapeHtml(r.dataset.po)} — ${parseAmount(r).toLocaleString(undefined, { style: 'currency', currency: 'USD' })}`
      )
      .join('<br/>');
    openConfirm(
      'Bulk approve',
      `<p>Approve <strong>${selected.length}</strong> purchase order(s)?</p><div class="bulk-list">${lines}</div>${
        anyHigh
          ? '<p class="modal__note">Includes high-value PO(s). Only use bulk approve if your process allows it.</p>'
          : ''
      }`,
      () => {
        const snapshots = selected.map((tr) => ({
          tr,
          prev: { status: tr.dataset.status, transferable: tr.dataset.transferable || '' }
        }));
        selected.forEach((tr) => approveRowInternal(tr, { quiet: true }));
        showUndo(`${selected.length} PO(s) approved.`, () => {
          snapshots.forEach(({ tr, prev }) => revertRowApproval(tr, prev));
          refreshView();
        });
      }
    );
  });

  let pullAccum = 0;
  poTableScroll?.addEventListener(
    'touchstart',
    (e) => {
      if (poTableScroll.scrollTop <= 0) {
        pullStartY = e.touches[0].clientY;
        pullArmed = true;
        pullAccum = 0;
      }
    },
    { passive: true }
  );
  poTableScroll?.addEventListener(
    'touchmove',
    (e) => {
      if (!pullArmed || pullStartY == null) return;
      if (poTableScroll.scrollTop > 0) return;
      pullAccum = e.touches[0].clientY - pullStartY;
      if (pullAccum > 72) {
        pullArmed = false;
        refreshRowsFromServer();
      }
    },
    { passive: true }
  );
  poTableScroll?.addEventListener('touchend', () => {
    pullArmed = false;
    pullStartY = null;
  });

  updateTabUi();
  refreshView();
  syncStickyHeights();

  window.addEventListener('resize', () => syncStickyHeights(), { passive: true });
})();
