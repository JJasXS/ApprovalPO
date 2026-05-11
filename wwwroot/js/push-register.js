(() => {
  const cfg = document.getElementById('poPageConfig');
  const vapidPublic = (cfg?.dataset.webPushPublicKey || '').trim();
  const subscribeUrl = (cfg?.dataset.webPushSubscribeUrl || '').trim();
  const unsubscribeUrl = (cfg?.dataset.webPushUnsubscribeUrl || '').trim();
  const csrf = cfg?.dataset.csrfToken || '';

  if (!cfg || !vapidPublic || !subscribeUrl || !csrf) {
    return;
  }

  function urlBase64ToUint8Array(base64String) {
    const padding = '='.repeat((4 - (base64String.length % 4)) % 4);
    const base64 = (base64String + padding).replace(/-/g, '+').replace(/_/g, '/');
    const rawData = self.atob(base64);
    const outputArray = new Uint8Array(rawData.length);
    for (let i = 0; i < rawData.length; ++i) outputArray[i] = rawData.charCodeAt(i);
    return outputArray;
  }

  async function postJson(url, body) {
    const res = await fetch(url, {
      method: 'POST',
      credentials: 'same-origin',
      headers: {
        Accept: 'application/json',
        'Content-Type': 'application/json',
        'X-CSRF-TOKEN': csrf,
      },
      body: JSON.stringify(body),
    });
    if (!res.ok) {
      let err = res.statusText;
      try {
        const j = await res.json();
        if (j && j.error) err = j.error;
      } catch (_) {}
      throw new Error(err);
    }
  }

  async function trySubscribe() {
    if (!('serviceWorker' in navigator) || !('PushManager' in window)) return;

    let perm = Notification.permission;
    if (perm === 'default') {
      perm = await Notification.requestPermission();
    }
    if (perm !== 'granted') return;

    const reg = await navigator.serviceWorker.register('/sw.js', { scope: '/' });
    await reg.update();
    const ready = await navigator.serviceWorker.ready;

    const sub =
      (await ready.pushManager.getSubscription()) ||
      (await ready.pushManager.subscribe({
        userVisibleOnly: true,
        applicationServerKey: urlBase64ToUint8Array(vapidPublic),
      }));

    const j = sub.toJSON();
    await postJson(subscribeUrl, j);
  }

  window.approvalPoTrySubscribeWebPush = () => {
    void trySubscribe().catch(() => {});
  };

  if (Notification.permission === 'granted') {
    void trySubscribe().catch(() => {});
  }

  window.approvalPoUnsubscribeWebPush = async () => {
    if (!('serviceWorker' in navigator)) return;
    const reg = await navigator.serviceWorker.getRegistration('/');
    if (!reg) return;
    const sub = await reg.pushManager.getSubscription();
    if (!sub) return;
    const j = sub.toJSON();
    if (unsubscribeUrl) {
      try {
        await postJson(unsubscribeUrl, { endpoint: j.endpoint });
      } catch (_) {}
    }
    await sub.unsubscribe();
  };
})();
