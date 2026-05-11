/* ApprovalPO — Web Push + notification click (scope: site root). */
self.addEventListener('push', (event) => {
  let payload = { title: 'Approval PO', body: '', url: '/PurchaseOrders' };
  try {
    if (event.data) {
      const j = event.data.json();
      if (j && typeof j === 'object') Object.assign(payload, j);
    }
  } catch (_) {
    try {
      const t = event.data && event.data.text();
      if (t) payload.body = t;
    } catch (_) {}
  }
  event.waitUntil(
    self.registration.showNotification(payload.title || 'Approval PO', {
      body: payload.body || '',
      data: { url: payload.url || '/PurchaseOrders' },
      tag: 'approval-po-push',
    })
  );
});

self.addEventListener('notificationclick', (event) => {
  event.notification.close();
  const path = (event.notification.data && event.notification.data.url) || '/PurchaseOrders';
  const abs = new URL(path, self.location.origin).href;
  event.waitUntil(
    self.clients.matchAll({ type: 'window', includeUncontrolled: true }).then((clientList) => {
      for (const client of clientList) {
        if (client.url && 'focus' in client) {
          try {
            client.navigate(abs);
            return client.focus();
          } catch (_) {
            break;
          }
        }
      }
      if (self.clients.openWindow) return self.clients.openWindow(abs);
    })
  );
});
