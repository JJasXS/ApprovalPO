(() => {
  const LS_ENABLED = 'approvalPo_pendingNotify_enabled';
  const LS_LAST_KEYS = 'approvalPo_pendingNotify_lastDocKeys';

  const cfg = document.getElementById('poPageConfig');
  const url = cfg?.dataset.pendingNotifyUrl || '';
  const pollMsRaw = parseInt(String(cfg?.dataset.pendingNotifyPollMs || '120000'), 10);
  const pollMs = Number.isFinite(pollMsRaw) && pollMsRaw >= 30000 ? pollMsRaw : 120000;

  const toast = document.getElementById('poNotifyToast');
  const btnEnable = document.getElementById('poNotifyEnableBtn');
  const btnDismiss = document.getElementById('poNotifyDismissBtn');
  const btnClose = document.getElementById('poNotifyToastClose');
  const menuStop = document.getElementById('poNotifyMenuStop');

  if (!cfg || !url) {
    return;
  }

  /** @type {ReturnType<typeof setInterval> | null} */
  let timerId = null;

  const parseLastKeySet = () => {
    try {
      const raw = localStorage.getItem(LS_LAST_KEYS);
      if (!raw) return new Set();
      const arr = JSON.parse(raw);
      if (!Array.isArray(arr)) return new Set();
      return new Set(arr.map((n) => Number(n)).filter((n) => n > 0));
    } catch {
      return new Set();
    }
  };

  const saveLastKeys = (keys) => {
    const sorted = [...keys].sort((a, b) => a - b);
    localStorage.setItem(LS_LAST_KEYS, JSON.stringify(sorted));
  };

  const fetchSnapshot = async () => {
    const res = await fetch(url, { credentials: 'same-origin', headers: { Accept: 'application/json' } });
    if (!res.ok) throw new Error(String(res.status));
    return res.json();
  };

  const isNotifySupported = () => typeof window !== 'undefined' && 'Notification' in window;

  const isMonitoring = () =>
    localStorage.getItem(LS_ENABLED) === '1' && isNotifySupported() && Notification.permission === 'granted';

  const syncMenuStop = () => {
    if (!menuStop) return;
    menuStop.hidden = !isMonitoring();
  };

  const showToast = () => {
    if (toast) toast.hidden = false;
  };

  const hideToast = () => {
    if (toast) toast.hidden = true;
  };

  const stripPromptNotifyFromUrl = () => {
    try {
      const u = new URL(window.location.href);
      if (!u.searchParams.has('promptNotify')) return false;
      u.searchParams.delete('promptNotify');
      const qs = u.searchParams.toString();
      const next = `${u.pathname}${qs ? `?${qs}` : ''}${u.hash}`;
      window.history.replaceState({}, '', next);
      return true;
    } catch {
      return false;
    }
  };

  const hadPostLoginPrompt = (() => {
    try {
      return new URLSearchParams(window.location.search).has('promptNotify');
    } catch {
      return false;
    }
  })();

  if (hadPostLoginPrompt) {
    stripPromptNotifyFromUrl();
  }

  const stopPolling = () => {
    if (timerId != null) {
      clearInterval(timerId);
      timerId = null;
    }
  };

  const pollTick = async (isBaseline) => {
    if (!isNotifySupported() || Notification.permission !== 'granted') return;
    if (localStorage.getItem(LS_ENABLED) !== '1') return;

    let data;
    try {
      data = await fetchSnapshot();
    } catch {
      return;
    }

    const items = Array.isArray(data.pending) ? data.pending : [];
    const keys = new Set(
      items
        .map((x) => Number(x.docKey))
        .filter((n) => Number.isFinite(n) && n > 0)
    );

    if (isBaseline) {
      saveLastKeys(keys);
      return;
    }

    const last = parseLastKeySet();
    const newOnes = items.filter((x) => {
      const k = Number(x.docKey);
      return Number.isFinite(k) && k > 0 && !last.has(k);
    });

    if (newOnes.length > 0) {
      const title = newOnes.length === 1 ? 'New pending order' : `${newOnes.length} new pending orders`;
      const body = newOnes
        .slice(0, 6)
        .map((x) => (x.poNumber && String(x.poNumber).trim()) || `#${x.docKey}`)
        .join(', ');
      try {
        const n = new Notification(title, { body, tag: 'approval-po-pending' });
        n.onclick = () => {
          window.focus();
          n.close();
        };
      } catch {
        /* ignore */
      }
    }

    saveLastKeys(keys);
  };

  const startIntervalOnly = () => {
    stopPolling();
    timerId = setInterval(() => void pollTick(false), pollMs);
  };

  const onStopAlerts = () => {
    localStorage.removeItem(LS_ENABLED);
    stopPolling();
    syncMenuStop();
  };

  menuStop?.addEventListener('click', () => {
    onStopAlerts();
    hideToast();
  });

  const dismissToast = () => hideToast();

  btnClose?.addEventListener('click', dismissToast);
  btnDismiss?.addEventListener('click', dismissToast);

  if (btnEnable) {
    btnEnable.addEventListener('click', async () => {
      if (!isNotifySupported()) return;
      if (btnEnable.disabled) return;
      let perm = Notification.permission;
      if (perm === 'default') {
        perm = await Notification.requestPermission();
      }
      if (perm !== 'granted') {
        window.alert(
          perm === 'denied'
            ? 'The browser blocked notifications for this site. Use the lock icon or site settings to allow them. In Incognito/Private mode you may need to allow each visit, and some browsers restrict alerts there.'
            : 'Notifications were not allowed.'
        );
        return;
      }

      localStorage.setItem(LS_ENABLED, '1');
      hideToast();
      syncMenuStop();
      await pollTick(true);
      startIntervalOnly();
    });
  }

  if (hadPostLoginPrompt && toast && !isMonitoring()) {
    if (isNotifySupported()) {
      showToast();
    }
  }

  if (isMonitoring()) {
    syncMenuStop();
    void pollTick(false);
    startIntervalOnly();
  } else {
    syncMenuStop();
  }

  document.getElementById('poDevNotifyPing')?.addEventListener('click', async () => {
    if (!isNotifySupported()) {
      window.alert('This browser has no Notification API.');
      return;
    }
    let perm = Notification.permission;
    if (perm === 'default') {
      perm = await Notification.requestPermission();
    }
    if (perm !== 'granted') {
      window.alert('Allow notifications first, then try again.');
      return;
    }
    try {
      new Notification('ApprovalPO', { body: 'Test — if you see this, the browser can show alerts.' });
    } catch (e) {
      window.alert(`Could not show notification: ${e && e.message ? e.message : e}`);
    }
  });
})();

