(() => {
  const modal = document.getElementById('reviewModal');
  const reviewBody = document.getElementById('reviewBody');
  const modalApproveBtn = document.getElementById('modalApproveBtn');
  const toast = document.getElementById('toast');
  const tabPending = document.getElementById('tabPending');
  const tabApproved = document.getElementById('tabApproved');
  const poSearchInput = document.getElementById('poSearchInput');
  const poEmptyState = document.getElementById('poEmptyState');
  const poTableScroll = document.getElementById('poTableScroll');
  const menuBtn = document.getElementById('menuBtn');
  const menuPanel = document.getElementById('menuPanel');
  const rows = Array.from(document.querySelectorAll('.po-row'));

  let currentRow = null;
  /** @type {'Pending' | 'Approved'} */
  let activeListTab = 'Pending';

  const showToast = (text) => {
    toast.textContent = text;
    toast.classList.add('show');
    clearTimeout(showToast._t);
    showToast._t = setTimeout(() => toast.classList.remove('show'), 3200);
  };

  const openModal = () => {
    modal.classList.add('show');
    modal.setAttribute('aria-hidden', 'false');
  };

  const closeModal = () => {
    modal.classList.remove('show');
    modal.setAttribute('aria-hidden', 'true');
    currentRow = null;
  };

  modal.querySelectorAll('[data-close]').forEach((el) => {
    el.addEventListener('click', closeModal);
  });

  menuBtn?.addEventListener('click', (e) => {
    e.stopPropagation();
    const next = menuPanel.hasAttribute('hidden');
    if (next) {
      menuPanel.removeAttribute('hidden');
    } else {
      menuPanel.setAttribute('hidden', '');
    }
    menuBtn.setAttribute('aria-expanded', next ? 'true' : 'false');
  });

  document.addEventListener('click', (e) => {
    if (!menuPanel || !menuBtn) return;
    if (menuPanel.hasAttribute('hidden')) return;
    if (menuPanel.contains(e.target) || menuBtn.contains(e.target)) return;
    menuPanel.setAttribute('hidden', '');
    menuBtn.setAttribute('aria-expanded', 'false');
  });

  const renderReview = (tr) => {
    const po = tr.dataset.po || '';
    const vendor = tr.dataset.vendor || '';
    const status = tr.dataset.status || '';
    const statusClass =
      status === 'Approved' ? 'review-head__status--approved' : 'review-head__status--pending';
    const items = getMockItems(po, vendor);
    const totalQty = items.reduce((sum, item) => sum + item.qty, 0);
    const lineTotal = (item) => item.qty * item.unitPrice;
    const itemsTotal = items.reduce((sum, item) => sum + lineTotal(item), 0);
    const itemsHtml = items.map((item) => `
      <tr>
        <td>${escapeHtml(item.code)}</td>
        <td>${escapeHtml(item.name)}</td>
        <td class="num">${item.qty}</td>
        <td class="num">${lineTotal(item).toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 })}</td>
      </tr>
    `).join('');

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
          <tbody>
            ${itemsHtml}
          </tbody>
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

  const escapeHtml = (s) => {
    const d = document.createElement('div');
    d.textContent = s;
    return d.innerHTML;
  };

  const getMockItems = (po, vendor) => {
    const seed = Number((po || '').replace(/\D/g, '').slice(-2)) || 1;
    return [
      {
        code: `ITM-${(100 + seed).toString()}`,
        name: `${vendor} - Main Supply`,
        qty: 1 + (seed % 3),
        unitPrice: 120 + seed * 2
      },
      {
        code: `ITM-${(200 + seed).toString()}`,
        name: 'Support Material',
        qty: 2 + (seed % 4),
        unitPrice: 45 + seed
      },
      {
        code: `ITM-${(300 + seed).toString()}`,
        name: 'Service Charge',
        qty: 1,
        unitPrice: 80 + seed * 1.5
      }
    ];
  };

  const approveRow = (tr) => {
    if (tr.dataset.status === 'Approved') {
      showToast('Already approved.');
      return;
    }
    tr.dataset.status = 'Approved';
    tr.dataset.transferable = 'true';
    const pill = tr.querySelector('.status-pill');
    if (pill) {
      pill.textContent = 'Approved';
      pill.classList.add('status-pill--approved');
    }
    const approveBtn = tr.querySelector('.btn-approve');
    if (approveBtn && !approveBtn.disabled) {
      approveBtn.disabled = true;
    }
    showToast(`PO ${tr.dataset.po} approved (mock).`);
    refreshView();
  };

  const updateRowButtons = (tr) => {
    const status = tr.dataset.status || 'Pending';
    const approveBtn = tr.querySelector('.btn-approve');

    if (approveBtn) {
      approveBtn.style.display = status === 'Pending' ? '' : 'none';
      approveBtn.disabled = status !== 'Pending';
    }
  };

  const updateTabUi = () => {
    const pendingOn = activeListTab === 'Pending';
    tabPending.classList.toggle('is-active', pendingOn);
    tabApproved.classList.toggle('is-active', !pendingOn);
    tabPending.setAttribute('aria-selected', pendingOn ? 'true' : 'false');
    tabApproved.setAttribute('aria-selected', pendingOn ? 'false' : 'true');
  };

  const setListTab = (tab) => {
    activeListTab = tab === 'Approved' ? 'Approved' : 'Pending';
    updateTabUi();
    refreshView();
  };

  const refreshView = () => {
    const searchTerm = (poSearchInput?.value || '').trim().toLowerCase();
    let visibleCount = 0;
    let countInTab = 0;
    rows.forEach((tr) => {
      updateRowButtons(tr);
      const isApproved = tr.dataset.status === 'Approved';
      const matchesStatus =
        activeListTab === 'Pending' ? !isApproved : isApproved;
      if (matchesStatus) countInTab++;
      const po = (tr.dataset.po || '').toLowerCase();
      const matchesSearch = !searchTerm || po.includes(searchTerm);
      const shouldShow = matchesStatus && matchesSearch;
      tr.classList.toggle('is-hidden', !shouldShow);
      if (shouldShow) visibleCount++;
    });

    if (poEmptyState && poTableScroll) {
      if (visibleCount === 0) {
        poEmptyState.hidden = false;
        if (countInTab === 0) {
          poEmptyState.textContent =
            activeListTab === 'Pending'
              ? 'No pending purchase orders.'
              : 'No approved purchase orders.';
        } else {
          poEmptyState.textContent =
            'No purchase orders match your search.';
        }
        poTableScroll.hidden = true;
      } else {
        poEmptyState.hidden = true;
        poTableScroll.hidden = false;
      }
    }
  };

  document.querySelectorAll('.btn-review').forEach((btn) => {
    btn.addEventListener('click', () => {
      const tr = btn.closest('tr');
      if (!tr) return;
      currentRow = tr;
      renderReview(tr);
      openModal();
    });
  });

  document.querySelectorAll('.btn-approve').forEach((btn) => {
    btn.addEventListener('click', () => {
      const tr = btn.closest('tr');
      if (tr) approveRow(tr);
    });
  });

  modalApproveBtn.addEventListener('click', () => {
    if (currentRow) approveRow(currentRow);
    closeModal();
  });

  tabPending?.addEventListener('click', () => setListTab('Pending'));
  tabApproved?.addEventListener('click', () => setListTab('Approved'));
  poSearchInput?.addEventListener('input', refreshView);

  updateTabUi();
  refreshView();
})();
