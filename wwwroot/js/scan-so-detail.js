/* scan-so-detail.js — enter deliver qty and confirm DO transfer */
(function () {
  'use strict';

  const cfg = document.getElementById('scanSoDetailConfig');
  if (!cfg) return;

  const docKey = cfg.dataset.docKey || '';
  const confirmUrl = cfg.dataset.confirmUrl || '';
  const csrf = cfg.dataset.csrf || '';

  const msgEl = document.getElementById('soDeliveryMsg');
  const confirmBtn = document.getElementById('soConfirmDeliveryBtn');
  const fillBtn = document.getElementById('soFillOutstandingBtn');

  const showMsg = (text, ok) => {
    if (!msgEl) return;
    msgEl.hidden = !text;
    msgEl.textContent = text || '';
    msgEl.className = ok ? 'scan-flash scan-flash--ok' : 'scan-flash scan-flash--error';
  };

  const collectLines = () => {
    const lines = [];
    document.querySelectorAll('#soDeliveryLines tbody tr[data-item-code]').forEach((tr) => {
      const input = tr.querySelector('.scan-deliver-qty');
      if (!input) return;
      const raw = String(input.value || '').trim();
      if (!raw) return;
      const qty = Number(raw);
      if (!(qty > 0)) return;
      const outstanding = Number(tr.dataset.outstanding || '0');
      const capped = outstanding > 0 ? Math.min(qty, outstanding) : qty;
      if (!(capped > 0)) return;
      lines.push({
        itemCode: tr.dataset.itemCode || '',
        quantity: capped,
        lineNo: Number(tr.dataset.lineNo || '0') || 0
      });
    });
    return lines;
  };

  fillBtn?.addEventListener('click', () => {
    document.querySelectorAll('#soDeliveryLines tbody tr[data-item-code]').forEach((tr) => {
      const input = tr.querySelector('.scan-deliver-qty');
      if (!input) return;
      const outstanding = Number(tr.dataset.outstanding || '0');
      if (outstanding > 0) input.value = String(outstanding);
    });
  });

  confirmBtn?.addEventListener('click', async () => {
    const lines = collectLines();
    if (lines.length === 0) {
      showMsg('Enter deliver quantities on at least one line.', false);
      return;
    }

    confirmBtn.disabled = true;
    confirmBtn.textContent = 'Creating DO…';
    showMsg('', true);

    try {
      const fd = new FormData();
      fd.append('docKey', docKey);
      fd.append('transferLinesJson', JSON.stringify(lines));
      const res = await fetch(confirmUrl, {
        method: 'POST',
        body: fd,
        credentials: 'same-origin',
        headers: {
          RequestVerificationToken: csrf,
          Accept: 'application/json'
        }
      });
      const data = await res.json().catch(() => ({}));
      if (!res.ok || !data.ok) {
        showMsg(data.error || 'Delivery failed.', false);
        return;
      }
      showMsg(`Created ${data.doNumber || 'Delivery Order'}.`, true);
      if (data.redirectUrl) {
        window.location.href = data.redirectUrl;
        return;
      }
      window.location.reload();
    } catch (err) {
      showMsg(err.message || 'Network error.', false);
    } finally {
      confirmBtn.disabled = false;
      confirmBtn.textContent = 'Confirm delivery';
    }
  });
})();
