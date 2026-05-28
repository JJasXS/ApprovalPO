(() => {
  const cfgEl = document.getElementById('poPageConfig');
  const ordersJsonUrl = cfgEl?.dataset.ordersJsonUrl || '';
  const linesJsonUrlBase = cfgEl?.dataset.linesJsonUrlBase || '';
  const setTransferableUrl = cfgEl?.dataset.setTransferableUrl || '';
  const setLineTransferableUrl = cfgEl?.dataset.setLineTransferableUrl || '';
  const csrfToken = cfgEl?.dataset.csrfToken || '';
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
  const tabCancelled = document.getElementById('tabCancelled');
  const tabRejected = document.getElementById('tabRejected');
  const countPending = document.getElementById('countPending');
  const countApproved = document.getElementById('countApproved');
  const countCancelled = document.getElementById('countCancelled');
  const countRejected = document.getElementById('countRejected');
  const poEmptyState = document.getElementById('poEmptyState');
  const poTableScroll = document.getElementById('poTableScroll');
  const poSkeleton = document.getElementById('poSkeleton');
  const poTableWrap = document.getElementById('poTableWrap');
  const tbody = document.getElementById('poTableBody');
  const refreshBtn = document.getElementById('refreshBtn');
  const bulkApproveBtn = document.getElementById('bulkApproveBtn');
  const bulkSelectAll = document.getElementById('bulkSelectAll');
  const filterDateFrom = document.getElementById('filterDateFrom');
  const filterClearBtn = document.getElementById('filterClearBtn');
  const menuBtn = document.getElementById('menuBtn');
  const menuPanel = document.getElementById('menuPanel');
  const poHeadToggleBtn = document.getElementById('poHeadToggleBtn');
  const modalPrevBtn = document.getElementById('modalPrevBtn');
  const modalNextBtn = document.getElementById('modalNextBtn');
  const tableHeadEl = document.querySelector('.po-table-wrap__head');
  const headerEl = document.querySelector('.po-header');

  let rows = Array.from(document.querySelectorAll('.po-row'));
  let currentRow = null;
  /** @type {'Pending' | 'Approved' | 'Cancelled' | 'Rejected'} */
  let activeListTab = 'Pending';
  let undoTimer = null;
  /** @type {null | (() => void)} */
  let confirmOkCallback = null;

  const filters = {
    dateFrom: '',
  };

  const showToast = (text) => {
    if (!toast) return;
    toast.textContent = text;
    toast.classList.add('show');
    clearTimeout(showToast._t);
    showToast._t = setTimeout(() => toast.classList.remove('show'), 3200);
  };

  const persistListStatus = async (tr, listStatus) => {
    if (!setTransferableUrl || !csrfToken) {
      showToast('Cannot save: page is missing save configuration.');
      return false;
    }
    const docKey = parseInt(String(tr?.dataset?.docKey ?? ''), 10);
    if (!docKey) {
      showToast('Cannot save: missing document key for this row.');
      return false;
    }
    try {
      const res = await fetch(setTransferableUrl, {
        method: 'POST',
        credentials: 'same-origin',
        headers: {
          Accept: 'application/json',
          'Content-Type': 'application/json',
          'X-CSRF-TOKEN': csrfToken
        },
        body: JSON.stringify({ docKey, listStatus })
      });
      let data = null;
      try {
        data = await res.json();
      } catch {
        data = null;
      }
      if (!res.ok || !data || data.ok !== true) {
        const msg = (data && data.error) || `Save failed (${res.status}).`;
        showToast(String(msg));
        return false;
      }
      return true;
    } catch {
      showToast('Save failed. Check your connection.');
      return false;
    }
  };

  const persistLineTransferable = async (docKeyN, lineNo, transferable, cbEl) => {
    if (!setLineTransferableUrl || !csrfToken) {
      showToast('Cannot save: page is missing save configuration.');
      return false;
    }
    const wasDisabled = cbEl.disabled;
    cbEl.disabled = true;
    try {
      const res = await fetch(setLineTransferableUrl, {
        method: 'POST',
        credentials: 'same-origin',
        headers: {
          Accept: 'application/json',
          'Content-Type': 'application/json',
          'X-CSRF-TOKEN': csrfToken
        },
        body: JSON.stringify({ docKey: docKeyN, lineNo, transferable })
      });
      let data = null;
      try {
        data = await res.json();
      } catch {
        data = null;
      }
      if (!res.ok || !data || data.ok !== true) {
        const msg = (data && data.error) || `Line save failed (${res.status}).`;
        showToast(String(msg));
        cbEl.checked = !transferable;
        return false;
      }
      return true;
    } catch {
      showToast('Line save failed. Check your connection.');
      cbEl.checked = !transferable;
      return false;
    } finally {
      if (!wasDisabled) cbEl.disabled = false;
    }
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
      const collapsed = tableHeadEl.classList.contains('is-collapsed');
      const h = collapsed ? 0 : tableHeadEl.getBoundingClientRect().height || 0;
      document.documentElement.style.setProperty('--po-table-wrap-head-height', `${Math.ceil(h)}px`);
    }
  };

  const applyTableHeadCollapsed = (collapsed) => {
    if (!tableHeadEl || !poHeadToggleBtn) return;
    tableHeadEl.classList.toggle('is-collapsed', !!collapsed);
    const expanded = !collapsed;
    poHeadToggleBtn.setAttribute('aria-expanded', expanded ? 'true' : 'false');
    poHeadToggleBtn.setAttribute('aria-label', expanded ? 'Hide filters and tabs' : 'Show filters and tabs');
    syncStickyHeights();
    try {
      localStorage.setItem('po-head-collapsed', collapsed ? '1' : '0');
    } catch {
      /* ignore */
    }
  };

  poHeadToggleBtn?.addEventListener('click', (e) => {
    e.stopPropagation();
    const next = !tableHeadEl?.classList.contains('is-collapsed');
    applyTableHeadCollapsed(next);
  });

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
    status === 'Approved'
      ? 'po-status-stack--approved'
      : status === 'Rejected'
        ? 'po-status-stack--rejected'
        : status === 'Cancelled'
          ? 'po-status-stack--cancelled'
          : 'po-status-stack--pending';
  const statusDisplay = (status) => status;

  const syncRowStatusDom = (tr, status) => {
    if (!tr) return;
    const stack = tr.querySelector('.po-status-stack');
    const text = tr.querySelector('.po-status-text');
    if (stack) {
      stack.classList.remove(
        'po-status-stack--pending',
        'po-status-stack--approved',
        'po-status-stack--rejected',
        'po-status-stack--cancelled'
      );
      stack.classList.add(statusStackClass(status));
    }
    if (text) text.textContent = statusDisplay(status);
  };

  const listStatusFromSnapshot = (prev) => {
    const s = String(prev?.status ?? '').trim();
    if (s === 'Pending' || s === 'Approved' || s === 'Cancelled' || s === 'Rejected') return s;
    const t = String(prev?.transferable ?? '').trim();
    if (t === 'true') return 'Approved';
    if (t === 'false') return 'Cancelled';
    return 'Pending';
  };

  /** Server may send numeric PH_PO.STATUS; tabs use Pending / Approved / Cancelled / Rejected. */
  const normalizeOrderStatus = (o) => {
    const s = String(o?.status ?? '').trim();
    if (s === 'Pending' || s === 'Approved' || s === 'Cancelled' || s === 'Rejected') return s;
    if (o?.transferable === true) return 'Approved';
    if (o?.transferable === false) return 'Cancelled';
    return 'Pending';
  };

  /** DOM row: tri-state transferable (true / false / empty = null) + data-status. */
  const rowListStatus = (tr) => {
    if (!tr?.dataset) return 'Pending';
    const s = String(tr.dataset.status ?? '').trim();
    if (s === 'Pending' || s === 'Approved' || s === 'Cancelled' || s === 'Rejected') return s;
    if (tr.dataset.transferable === 'true') return 'Approved';
    if (tr.dataset.transferable === 'false') return 'Cancelled';
    return 'Pending';
  };

  const rowFromOrder = (o) => {
    const po = o.poNumber || '';
    const vendor = o.vendor || '';
    const amount =
      typeof o.amount === 'number' ? o.amount.toFixed(2) : String(o.amount ?? '0');
    const status = normalizeOrderStatus(o);
    const desc = o.description || '';
    const transfer = o.transferable === true ? 'true' : o.transferable === false ? 'false' : '';
    const dateStr = (o.orderDate && String(o.orderDate).slice(0, 10)) || '';
    const docKey = o.docKey != null && o.docKey !== '' ? String(o.docKey) : '';
    const chk =
      status === 'Pending'
        ? `<input type="checkbox" class="row-select" aria-label="Select ${escapeHtml(po)}" />`
        : '';
    const amtNum = Number.parseFloat(amount) || 0;
    const stack = statusStackClass(status);
    return `<tr class="po-row" data-po="${escapeHtml(po)}" data-doc-key="${escapeHtml(docKey)}" data-amount="${escapeHtml(amount)}" data-transferable="${transfer}" data-status="${escapeHtml(status)}" data-vendor="${escapeHtml(vendor)}" data-description="${escapeHtml(desc)}" data-order-date="${escapeHtml(dateStr)}">
      <td class="td-check" data-label="">${chk}</td>
      <td class="po-status-td" data-label=""><div class="po-status-stack ${stack}"><span class="po-status-icon" aria-hidden="true"></span><span class="po-status-text">${escapeHtml(statusDisplay(status))}</span></div></td>
      <td class="po-num" data-label="PO #"><span class="po-num-text">${escapeHtml(po)}</span></td>
      <td class="po-vendor" data-label="Supplier">${escapeHtml(vendor) || '—'}</td>
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

  modal?.addEventListener('change', (e) => {
    const t = e.target;
    if (!(t instanceof HTMLInputElement) || !t.classList.contains('line-transfer-cb')) return;
    if (t.disabled) return;
    const docKeyN = parseInt(t.dataset.docKey || '', 10);
    const lineNo = parseInt(t.dataset.lineNo || '', 10);
    if (!docKeyN) return;
    const newVal = t.checked;
    void persistLineTransferable(docKeyN, lineNo, newVal, t);
  });

  confirmModal?.querySelectorAll('[data-confirm-close]').forEach((el) => {
    el.addEventListener('click', () => dismissConfirm());
  });

  confirmOkBtn?.addEventListener('click', () => {
    const fn = confirmOkCallback;
    confirmOkCallback = null;
    closeModal(confirmModal);
    if (typeof fn === 'function') void Promise.resolve(fn()).catch(() => {});
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

  const navigateModal = async (delta) => {
    if (!currentRow) return;
    const visible = getVisibleRowsInList();
    const idx = visible.indexOf(currentRow);
    if (idx < 0) return;
    const nextIdx = idx + delta;
    if (nextIdx < 0 || nextIdx >= visible.length) return;
    const target = visible[nextIdx];
    currentRow = target;
    await renderReview(target);
    updateModalNav();
    target.scrollIntoView({ block: 'nearest' });
  };

  /** PH_PODTL.TRANSFERABLE: only true ticks the review checkbox; false or null is unticked. */
  const isLineTransferableTicked = (item) => {
    if (!item || item.transferable === undefined || item.transferable === null) return false;
    const v = item.transferable;
    if (v === true || v === 'true' || v === 1 || v === '1') return true;
    return false;
  };

  /** Approved: ticked → green row; not ticked → red row. Cancelled: all red rows. Pending: default. */
  const lineXferRowClass = (listStatus, ticked) => {
    if (listStatus === 'Rejected' || listStatus === 'Cancelled') return 'items-row--xfer-bad';
    if (listStatus === 'Approved') return ticked ? 'items-row--xfer-ok' : 'items-row--xfer-bad';
    return '';
  };

  const renderReview = async (tr) => {
    if (!reviewBody || !modalApproveBtn) return;
    const modalRejectBtnEl = document.getElementById('modalRejectBtn');
    const po = tr.dataset.po || '';
    const vendor = tr.dataset.vendor || '';
    const status = rowListStatus(tr);
    const desc = tr.dataset.description || '';
    const orderIso = tr.dataset.orderDate || '';
    const displayDate = formatDisplayDate(orderIso);
    const amt = parseAmount(tr);
    const amtStr = amt.toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 });
    let statusClass = 'review-badge--pending';
    if (status === 'Approved') statusClass = 'review-badge--approved';
    else if (status === 'Rejected') statusClass = 'review-badge--rejected';
    else if (status === 'Cancelled') statusClass = 'review-badge--cancelled';

    const docKey = (tr.dataset.docKey || '').trim();
    let itemsHtml = `<tr><td colspan="6" class="items-empty">No line items loaded.</td></tr>`;
    let totalSqty = 0;
    let totalSuomQty = 0;
    let itemsTotal = 0;

    if (docKey && linesJsonUrlBase) {
      const sep = linesJsonUrlBase.includes('?') ? '&' : '?';
      const url = `${linesJsonUrlBase}${sep}docKey=${encodeURIComponent(docKey)}`;
      const docKeyNum = parseInt(String(docKey), 10) || 0;
      const linePending = status === 'Pending';
      try {
        const res = await fetch(url, { method: 'GET', credentials: 'same-origin', cache: 'no-store' });
        const data = await res.json();
        if (res.ok && Array.isArray(data) && data.length > 0) {
          itemsHtml = data
            .map((item) => {
              const code = escapeHtml(item.itemCode || '');
              const name = escapeHtml(item.description || '');
              const sqty = Number(item.sqty) || 0;
              const suomQty = Number(item.suomQty) || 0;
              const qtyFallback = Number(item.qty) || 0;
              const up = Number(item.unitPrice) || 0;
              const baseQtyForAmt = sqty || qtyFallback;
              const lineAmt =
                item.lineAmount != null && item.lineAmount !== ''
                  ? Number(item.lineAmount)
                  : baseQtyForAmt * up;
              const lineStr = lineAmt.toLocaleString(undefined, {
                minimumFractionDigits: 2,
                maximumFractionDigits: 2,
              });
              const ticked = isLineTransferableTicked(item);
              const lineNoRaw = Number.parseInt(String(item.lineNo ?? ''), 10);
              const lineNoSafe = Number.isFinite(lineNoRaw) ? lineNoRaw : 0;
              const disabledAttr = linePending ? '' : ' disabled';
              const labelClass = linePending
                ? 'po-check po-check--line-xfer'
                : 'po-check po-check--line-xfer po-check--line-xfer-readonly';
              const xferRowClass = lineXferRowClass(status, ticked);
              return `
      <tr${xferRowClass ? ` class="${xferRowClass}"` : ''}>
        <td class="items-check">
            <label class="${labelClass}" aria-label="Line Sct (PH_PODTL.TRANSFERABLE) for item ${code}: ${ticked ? 'true' : 'false'}">
            <input type="checkbox" class="line-transfer-cb" data-doc-key="${docKeyNum}" data-line-no="${lineNoSafe}" ${ticked ? 'checked' : ''}${disabledAttr} />
            <span class="po-check__box" aria-hidden="true"></span>
          </label>
        </td>
        <td class="items-code">${code}</td>
        <td class="items-desc">${name}</td>
        <td class="num items-qty-sqty">${sqty}</td>
        <td class="num items-qty-suom">${suomQty}</td>
        <td class="num">${lineStr}</td>
      </tr>`;
            })
            .join('');
          totalSqty = data.reduce((sum, item) => sum + (Number(item.sqty) || 0), 0);
          totalSuomQty = data.reduce((sum, item) => sum + (Number(item.suomQty) || 0), 0);
          itemsTotal = data.reduce((sum, item) => {
            const sq = Number(item.sqty) || 0;
            const qf = Number(item.qty) || 0;
            const up = Number(item.unitPrice) || 0;
            const base = sq || qf;
            const la =
              item.lineAmount != null && item.lineAmount !== '' ? Number(item.lineAmount) : base * up;
            return sum + la;
          }, 0);
        } else if (res.ok && Array.isArray(data)) {
          itemsHtml = `<tr><td colspan="6" class="items-empty">No detail lines on this purchase order.</td></tr>`;
        } else {
          itemsHtml = `<tr><td colspan="6" class="items-empty">Could not load line items (${res.status}).</td></tr>`;
        }
      } catch {
        itemsHtml = `<tr><td colspan="6" class="items-empty">Could not load line items.</td></tr>`;
      }
    } else if (!docKey) {
      itemsHtml = `<tr><td colspan="6" class="items-empty">No document key (DOCKEY) for this row — include DOCKEY in the PH_PO list SQL.</td></tr>`;
    }

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
            <div class="review-label">Supplier</div>
            <div class="review-value">${escapeHtml(vendor) || '—'}</div>
          </div>
          <div class="review-field">
            <div class="review-label">Date</div>
            <div class="review-value">${escapeHtml(displayDate)}</div>
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
                <th class="items-th-check" scope="col" title="PH_PODTL.TRANSFERABLE">Sct</th>
                <th>Item code</th>
                <th>Description</th>
                <th class="num" scope="col" title="PH_PODTL.SQTY">SQTY</th>
                <th class="num" scope="col" title="PH_PODTL.SUOMQTY">SUOMQTY</th>
                <th class="num">Amount</th>
              </tr>
            </thead>
            <tbody>${itemsHtml}</tbody>
            <tfoot>
              <tr>
                <td colspan="3" class="items-total-label">Total</td>
                <td class="num items-total-qty">${totalSqty}</td>
                <td class="num items-total-qty">${totalSuomQty}</td>
                <td class="num items-total-amt">${itemsTotal.toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 })}</td>
              </tr>
            </tfoot>
          </table>
        </div>
      </div>
    `;

    const pending = status === 'Pending';
    const approved = status === 'Approved';
    const rejected = status === 'Rejected';
    const cancelled = status === 'Cancelled';
    const canApprove = pending || rejected || cancelled;
    const canReject = pending || approved;

    modalApproveBtn.disabled = !canApprove;
    modalApproveBtn.style.display = canApprove ? '' : 'none';
    modalApproveBtn.title = '';

    if (modalRejectBtnEl) {
      modalRejectBtnEl.textContent = approved ? 'Cancel approval' : 'Reject';
      modalRejectBtnEl.disabled = !canReject;
      modalRejectBtnEl.style.display = canReject ? '' : 'none';
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
      void Promise.resolve(typeof revertFn === 'function' ? revertFn() : undefined)
        .catch(() => {})
        .finally(() => {
          undoToast.hidden = true;
          if (undoTimer) clearTimeout(undoTimer);
          undoBtn.onclick = null;
        });
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
    syncRowStatusDom(tr, 'Approved');
    const cb = tr.querySelector('.row-select');
    if (cb) {
      cb.checked = false;
      cb.disabled = true;
    }
    updateRowButtons(tr);
    if (!quiet) showToast(`PO ${tr.dataset.po} approved.`);
    refreshView();
  };

  const rejectRowInternal = (tr, opts = {}) => {
    const { quiet = false } = opts;
    tr.dataset.status = 'Rejected';
    tr.dataset.transferable = 'false';
    syncRowStatusDom(tr, 'Rejected');
    const cb = tr.querySelector('.row-select');
    if (cb) {
      cb.checked = false;
      cb.disabled = true;
    }
    updateRowButtons(tr);
    if (!quiet) showToast(`${tr.dataset.po} rejected.`);
    refreshView();
  };

  const revertRowApproval = (tr, prev) => {
    tr.dataset.status = prev.status;
    tr.dataset.transferable = prev.transferable;
    const c = tr.querySelector('.row-select');
    if (c) c.disabled = rowListStatus(tr) !== 'Pending';
    updateRowButtons(tr);
    syncRowStatusDom(tr, rowListStatus(tr));
  };

  const runApprove = (tr) => {
    const amt = parseAmount(tr);
    const doApprove = async () => {
      const prev = {
        status: tr.dataset.status,
        transferable: tr.dataset.transferable || ''
      };
      const ok = await persistListStatus(tr, 'Approved');
      if (!ok) return;
      approveRowInternal(tr, { quiet: true });
      showUndo('Approved.', async () => {
        const uok = await persistListStatus(tr, listStatusFromSnapshot(prev));
        if (!uok) {
          showToast('Undo could not be saved to the database.');
          return;
        }
        revertRowApproval(tr, prev);
        refreshView();
      });
    };

    if (amt >= highValueThreshold) {
      openConfirm(
        'Confirm high-value approval',
        `<p>Approve <strong>${escapeHtml(tr.dataset.po)}</strong> for <strong>${amt.toLocaleString(undefined, { style: 'currency', currency: 'USD' })}</strong>?</p>`,
        () => void doApprove()
      );
      return;
    }
    void doApprove();
  };

  const approveRow = (tr) => {
    const st = rowListStatus(tr);
    if (st === 'Approved') {
      showToast('Already approved.');
      return;
    }
    if (st !== 'Pending' && st !== 'Cancelled' && st !== 'Rejected') {
      showToast('Approve is only available for pending, cancelled, or rejected purchase orders.');
      return;
    }
    runApprove(tr);
  };

  const rejectRow = (tr) => {
    const st = rowListStatus(tr);
    if (st !== 'Pending' && st !== 'Approved') {
      showToast('Reject is only available for pending or approved orders.');
      return;
    }
    void (async () => {
      const prev = {
        status: tr.dataset.status,
        transferable: tr.dataset.transferable || ''
      };
      const ok = await persistListStatus(tr, 'Rejected');
      if (!ok) return;
      rejectRowInternal(tr, { quiet: true });
      showUndo('Rejected.', async () => {
        const uok = await persistListStatus(tr, listStatusFromSnapshot(prev));
        if (!uok) {
          showToast('Undo could not be saved to the database.');
          return;
        }
        revertRowApproval(tr, prev);
        refreshView();
      });
    })();
  };

  const updateRowButtons = (tr) => {
    const status = rowListStatus(tr);
    const approveBtn = tr.querySelector('.btn-approve');
    const rejectBtn = tr.querySelector('.btn-reject');
    if (approveBtn) {
      approveBtn.style.display = status === 'Pending' || status === 'Cancelled' || status === 'Rejected' ? '' : 'none';
      approveBtn.disabled = status === 'Approved';
    }
    if (rejectBtn) {
      rejectBtn.style.display = status === 'Pending' || status === 'Approved' ? '' : 'none';
      rejectBtn.disabled = status === 'Cancelled' || status === 'Rejected';
    }
  };

  const rowMatchesFilters = (tr) => {
    const od = tr.dataset.orderDate || '';
    if (filters.dateFrom && od < filters.dateFrom) return false;
    return true;
  };

  const updateTabUi = () => {
    if (!tabPending || !tabApproved || !tabCancelled || !tabRejected) return;
    const pendingOn = activeListTab === 'Pending';
    const approvedOn = activeListTab === 'Approved';
    const cancelledOn = activeListTab === 'Cancelled';
    const rejectedOn = activeListTab === 'Rejected';
    tabPending.classList.toggle('is-active', pendingOn);
    tabApproved.classList.toggle('is-active', approvedOn);
    tabCancelled.classList.toggle('is-active', cancelledOn);
    tabRejected.classList.toggle('is-active', rejectedOn);
    tabPending.setAttribute('aria-selected', pendingOn ? 'true' : 'false');
    tabApproved.setAttribute('aria-selected', approvedOn ? 'true' : 'false');
    tabCancelled.setAttribute('aria-selected', cancelledOn ? 'true' : 'false');
    tabRejected.setAttribute('aria-selected', rejectedOn ? 'true' : 'false');
  };

  const setListTab = (tab) => {
    if (tab === 'Approved') activeListTab = 'Approved';
    else if (tab === 'Cancelled') activeListTab = 'Cancelled';
    else if (tab === 'Rejected') activeListTab = 'Rejected';
    else activeListTab = 'Pending';
    updateTabUi();
    refreshView();
  };

  const syncBulkHeaderCheckbox = () => {
    if (!bulkSelectAll) return;
    const visiblePending = rows.filter(
      (r) => !r.classList.contains('is-hidden') && rowListStatus(r) === 'Pending'
    );
    bulkSelectAll.disabled = visiblePending.length === 0;
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
    const pendingCount = rows.filter((r) => rowListStatus(r) === 'Pending').length;
    const approvedCount = rows.filter((r) => rowListStatus(r) === 'Approved').length;
    const cancelledCount = rows.filter((r) => rowListStatus(r) === 'Cancelled').length;
    const rejectedCount = rows.filter((r) => rowListStatus(r) === 'Rejected').length;
    if (countPending) countPending.textContent = String(pendingCount);
    if (countApproved) countApproved.textContent = String(approvedCount);
    if (countCancelled) countCancelled.textContent = String(cancelledCount);
    if (countRejected) countRejected.textContent = String(rejectedCount);

    let visibleCount = 0;
    let countInTab = 0;
    rows.forEach((tr) => {
      updateRowButtons(tr);
      const status = rowListStatus(tr);
      const matchesStatus =
        activeListTab === 'Pending'
          ? status === 'Pending'
          : activeListTab === 'Approved'
            ? status === 'Approved'
            : activeListTab === 'Cancelled'
              ? status === 'Cancelled'
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
        rowListStatus(r) === 'Pending' &&
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
                : activeListTab === 'Cancelled'
                  ? 'No cancelled purchase orders.'
                  : 'No rejected purchase orders.'
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
      void (async () => {
        await renderReview(tr);
        openModal(modal);
        updateModalNav();
      })();
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
    if (!currentRow) return;
    const st = rowListStatus(currentRow);
    if (st !== 'Pending' && st !== 'Approved') return;
    rejectRow(currentRow);
    closeModal(modal);
    currentRow = null;
  });

  modalPrevBtn?.addEventListener('click', () => void navigateModal(-1));
  modalNextBtn?.addEventListener('click', () => void navigateModal(1));

  tableHeadEl?.addEventListener('click', (e) => {
    const tabBtn = e.target.closest('.tab-btn');
    if (!tabBtn || !tableHeadEl.contains(tabBtn)) return;
    if (tabBtn.id === 'tabPending') setListTab('Pending');
    else if (tabBtn.id === 'tabApproved') setListTab('Approved');
    else if (tabBtn.id === 'tabCancelled') setListTab('Cancelled');
    else if (tabBtn.id === 'tabRejected') setListTab('Rejected');
  });
  filterDateFrom?.addEventListener('change', () => {
    filters.dateFrom = filterDateFrom.value || '';
    refreshView();
  });

  filterClearBtn?.addEventListener('click', () => {
    if (filterDateFrom) filterDateFrom.value = '';
    filters.dateFrom = '';
    refreshView();
  });

  refreshBtn?.addEventListener('click', () => refreshRowsFromServer());

  bulkSelectAll?.addEventListener('change', () => {
    const on = bulkSelectAll.checked;
    rows.forEach((tr) => {
      if (tr.classList.contains('is-hidden')) return;
      if (rowListStatus(tr) !== 'Pending') return;
      const c = tr.querySelector('.row-select');
      if (c) c.checked = on;
    });
    refreshView();
  });

  tbody?.addEventListener('change', (e) => {
    if (e.target.classList?.contains('row-select')) refreshView();
  });

  const bulkApproveConfirmed = async (selected) => {
    const snapshots = selected.map((tr) => ({
      tr,
      prev: { status: tr.dataset.status, transferable: tr.dataset.transferable || '' }
    }));
    for (const tr of selected) {
      const ok = await persistListStatus(tr, 'Approved');
      if (!ok) {
        await refreshRowsFromServer();
        return;
      }
      approveRowInternal(tr, { quiet: true });
    }
    showUndo(`${selected.length} PO(s) approved.`, async () => {
      for (const { tr, prev } of snapshots) {
        const uok = await persistListStatus(tr, listStatusFromSnapshot(prev));
        if (!uok) {
          showToast('Bulk undo failed partway; refreshing list.');
          await refreshRowsFromServer();
          return;
        }
        revertRowApproval(tr, prev);
      }
      refreshView();
    });
  };

  bulkApproveBtn?.addEventListener('click', () => {
    const selected = rows.filter(
      (r) =>
        !r.classList.contains('is-hidden') &&
        rowListStatus(r) === 'Pending' &&
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
      () => void bulkApproveConfirmed(selected)
    );
  });

  try {
    if (localStorage.getItem('po-head-collapsed') === '1') {
      applyTableHeadCollapsed(true);
    }
  } catch {
    /* ignore */
  }

  updateTabUi();
  refreshView();
  syncStickyHeights();

  window.addEventListener('resize', () => syncStickyHeights(), { passive: true });
})();
