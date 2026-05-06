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

  const escapeHtml = (s) => {
    const d = document.createElement('div');
    d.textContent = s == null ? '' : String(s);
    return d.innerHTML;
  };

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
    return `<tr class="po-row" data-po="${escapeHtml(po)}" data-vendor="${escapeHtml(vendor)}" data-amount="${escapeHtml(amount)}" data-transferable="${transfer}" data-status="${escapeHtml(status)}" data-description="${escapeHtml(desc)}" data-order-date="${escapeHtml(dateStr)}">
      <td class="td-check" data-label="">${chk}</td>
      <td class="po-num" data-label="PO #"><span class="po-num-text">${escapeHtml(po)}</span></td>
      <td data-label="Vendor">${escapeHtml(vendor)}</td>
      <td class="num" data-label="Amount">${amtNum.toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 })}</td>
      <td class="actions" data-label="Actions">
        <div class="actions-row-block"><div class="actions-inner">
          <button type="button" class="btn btn-secondary btn-review" data-action="review">Review</button>
          <button type="button" class="btn btn-danger btn-reject" data-action="reject">Reject</button>
          <button type="button" class="btn btn-primary btn-approve" data-action="approve">Approve</button>
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
    const v = escapeHtml(vendor);
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

  const renderReview = (tr) => {
    if (!reviewBody || !modalApproveBtn) return;
    const po = tr.dataset.po || '';
    const vendor = tr.dataset.vendor || '';
    const status = tr.dataset.status || '';
    const statusClass =
      status === 'Approved' ? 'review-head__status--approved' : 'review-head__status--pending';
    const items = getMockItems(po, vendor);
    const totalQty = items.reduce((sum, item) => sum + item.qty, 0);
    const lineTotal = (item) => item.qty * item.unitPrice;
    const itemsTotal = items.reduce((sum, item) => sum + lineTotal(item), 0);
    const itemsHtml = items
      .map(
        (item) => `
      <tr>
        <td>${escapeHtml(item.code)}</td>
        <td>${item.name}</td>
        <td class="num">${item.qty}</td>
        <td class="num">${lineTotal(item).toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 })}</td>
      </tr>
    `
      )
      .join('');

    reviewBody.innerHTML = `
      <div class="review-head">
        <div class="review-head__po">${escapeHtml(po)}</div>
        <div class="review-head__status ${statusClass}">${escapeHtml(status)}</div>
      </div>
      <div class="items-title">Items</div>
      <div class="items-wrap">
        <table class="items-table">
          <thead>
            <tr>
              <th>Item</th>
              <th>Description</th>
              <th class="num">Qty</th>
              <th class="num">Total</th>
            </tr>
          </thead>
          <tbody>${itemsHtml}</tbody>
          <tfoot>
            <tr>
              <td colspan="2">Total</td>
              <td class="num">${totalQty}</td>
              <td class="num">${itemsTotal.toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 })}</td>
            </tr>
          </tfoot>
        </table>
      </div>
    `;

    modalApproveBtn.disabled = status === 'Approved';
    modalApproveBtn.title = status === 'Approved' ? 'Already approved' : '';
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
      showToast('Only pending PO can be rejected.');
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
      if (visibleCount === 0) {
        poEmptyState.hidden = false;
        if (countInTab === 0) {
          poEmptyState.textContent =
            activeListTab === 'Pending'
              ? 'No pending purchase orders.'
              : activeListTab === 'Approved'
                ? 'No approved purchase orders.'
                : 'No rejected purchase orders.';
        } else {
          poEmptyState.textContent = 'No purchase orders match your filters.';
        }
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
})();
