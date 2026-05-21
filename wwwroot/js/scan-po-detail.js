/* scan-po-detail.js — QR scan on PO detail page */
(function () {
  'use strict';

  const scanBtn = document.getElementById('scanDetailScanBtn');
  if (!scanBtn || typeof ScanQrScanner === 'undefined') return;

  const qr = ScanQrScanner.create({
    onDetected(text) {
      const t = (text || '').trim();
      setTimeout(() => {
        qr.close();
        if (t) alert(`Scanned: ${t}`);
      }, 400);
    },
  });

  qr.bindButton(scanBtn);
})();
