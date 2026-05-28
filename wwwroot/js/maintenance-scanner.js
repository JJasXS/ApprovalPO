/* maintenance-scanner.js — port of ProAccScanner Scanner.cshtml inline JS. */
(function () {
  'use strict';

  var config = document.getElementById('ms-config');
  if (!config) return;

  var validateUrl = config.getAttribute('data-validate-url') || '';
  var locationsUrl = config.getAttribute('data-locations-url') || '';
  var insertUrl = config.getAttribute('data-insert-url') || '';
  var antiforgeryToken = '';
  var tokenEl = document.querySelector('input[name="__RequestVerificationToken"]');
  if (tokenEl) antiforgeryToken = tokenEl.value || '';

  var startBtn = document.getElementById('ms-start');
  var videoWrap = document.getElementById('ms-video-wrap');
  var orientationBtn = document.getElementById('ms-orientation');
  var flashBtn = document.getElementById('ms-flash');
  var scanResult = document.getElementById('ms-scan-result');

  var locationInput = document.getElementById('ms-location');
  var yesBtn = document.getElementById('ms-yes');
  var noBtn = document.getElementById('ms-no');

  var manualContainer = document.getElementById('ms-manual-location-container');
  var manualInput = document.getElementById('ms-manual-location');

  var remark1Input = document.getElementById('ms-remark1');
  var remark2Input = document.getElementById('ms-remark2');
  var remark3Input = document.getElementById('ms-remark3');
  var projectInput = document.getElementById('ms-project');
  var lastScannedInput = document.getElementById('ms-last-scanned');

  var updateBtn = document.getElementById('ms-update');

  var manualCodeInput = document.getElementById('ms-manual-code');
  var manualCodeBtn = document.getElementById('ms-manual-code-btn');

  function getHttpsScannerUrl() {
    var host = location.hostname || 'localhost';
    var httpsMeta = document.querySelector('meta[name="approval-https-port"]');
    var httpsPort = (httpsMeta && httpsMeta.content) ? httpsMeta.content : '2096';
    return 'https://' + host + ':' + httpsPort + '/MaintenanceScanner';
  }

  function canUseLiveCamera() {
    return window.isSecureContext === true;
  }

  function requestCamera(constraints) {
    var nav = navigator;
    if (nav.mediaDevices && nav.mediaDevices.getUserMedia) {
      return nav.mediaDevices.getUserMedia(constraints);
    }
    var legacy = nav.getUserMedia || nav.webkitGetUserMedia || nav.mozGetUserMedia;
    if (legacy) {
      return new Promise(function (resolve, reject) {
        legacy.call(nav, constraints, resolve, reject);
      });
    }
    return Promise.reject(new Error('Live camera is not available in this browser.'));
  }

  function cameraErrorMessage(err) {
    var name = (err && err.name) ? String(err.name) : '';
    var msg = (err && err.message) ? String(err.message) : String(err || '');
    if (name === 'NotAllowedError' || name === 'PermissionDeniedError' || /permission|denied/i.test(msg)) {
      return 'Camera permission was blocked. Allow camera for this site in browser settings, then tap Start Scanner again.';
    }
    if (name === 'NotFoundError' || name === 'DevicesNotFoundError' || /not found|no device/i.test(msg)) {
      return 'No camera was found on this device. Use “Type code manually” below, or connect a webcam.';
    }
    if (name === 'NotReadableError' || /in use|busy/i.test(msg)) {
      return 'Camera is in use by another app. Close other apps using the camera and try again.';
    }
    if (name === 'SecurityError' || /secure|https/i.test(msg)) {
      return 'Camera requires HTTPS. Open:\n' + getHttpsScannerUrl();
    }
    if (msg) return msg;
    return 'Could not access the camera.';
  }

  function alertScannerStartFailed(err, triedQuagga) {
    console.error('[MaintenanceScanner] start failed', err);
    var detail = cameraErrorMessage(err);
    if (!canUseLiveCamera()) {
      detail = 'Live camera only works on HTTPS (or localhost).\n\nOpen:\n' + getHttpsScannerUrl()
        + '\n\nOn HTTP you can still type the code in “Type code manually” and tap Check.';
    } else if (triedQuagga && typeof Quagga === 'undefined') {
      detail = 'Scanner library failed to load (check internet / CDN). Use manual entry below.';
    }
    alert('Failed to start scanner.\n\n' + detail);
  }

  var scannerActive = false;
  var lastScannedCode = '';
  var lastResolvedLocationCode = '';
  var lastDetectedAt = 0;
  // Scanner is locked to horizontal (landscape) preview; vertical mode + toggle
  // button are hidden in the UI. Keep the flag for CSS hooks below.
  var previewLandscape = true;
  var flashOn = false;

  var scannerMode = null;
  var nativeStream = null;
  var nativeRafId = 0;
  var nativeDetector = null;
  var nativeDetectBusy = false;
  var nativeLastScanAttempt = 0;

  function getScannerVideoTrack() {
    if (scannerMode === 'native' && nativeStream) {
      var tracks = nativeStream.getVideoTracks();
      return tracks.length ? tracks[0] : null;
    }
    if (scannerMode === 'quagga' && typeof Quagga !== 'undefined' && Quagga.CameraAccess) {
      return Quagga.CameraAccess.getActiveTrack();
    }
    return null;
  }

  function applyPreviewOrientation() {
    // Scanner is hard-locked to landscape; the orientation toggle button is hidden.
    previewLandscape = true;
    videoWrap.classList.add('is-landscape');
    if (orientationBtn) {
      orientationBtn.textContent = 'Horizontal view';
      orientationBtn.hidden = true;
    }
  }

  async function setTorchEnabled(on) {
    var track = getScannerVideoTrack();
    if (!track || typeof track.applyConstraints !== 'function') return false;
    try {
      var caps = typeof track.getCapabilities === 'function' ? track.getCapabilities() : {};
      if (caps.torch !== undefined) {
        await track.applyConstraints({ advanced: [{ torch: on }] });
        return true;
      }
      if (caps.fillLightMode && caps.fillLightMode.indexOf('flash') !== -1) {
        await track.applyConstraints({ advanced: [{ fillLightMode: on ? 'flash' : 'off' }] });
        return true;
      }
    } catch (e) { console.warn('Torch not applied:', e); }
    return false;
  }

  function updateFlashUi() {
    var canTorch = false;
    if (scannerActive) {
      var track = getScannerVideoTrack();
      if (track && typeof track.getCapabilities === 'function') {
        var caps = track.getCapabilities();
        canTorch = caps.torch !== undefined
          || (caps.fillLightMode && caps.fillLightMode.indexOf('flash') !== -1);
      }
    }
    flashBtn.disabled = !scannerActive || !canTorch;
    flashBtn.textContent = flashOn ? 'Flash on' : 'Flash off';
    flashBtn.classList.toggle('ms-btn--flash-on', flashOn);
  }

  applyPreviewOrientation();
  // Orientation toggle is hidden by design; no click handler needed.

  flashBtn.addEventListener('click', async function () {
    if (!scannerActive || flashBtn.disabled) return;
    var next = !flashOn;
    var ok = await setTorchEnabled(next);
    flashOn = ok ? next : false;
    if (!ok) await setTorchEnabled(false);
    updateFlashUi();
  });

  function setConfirmSelected(which) {
    yesBtn.classList.remove('ms-btn--selected-yes');
    noBtn.classList.remove('ms-btn--selected-no');
    if (which === 'yes') yesBtn.classList.add('ms-btn--selected-yes');
    if (which === 'no') noBtn.classList.add('ms-btn--selected-no');
  }

  function clearConfirmSelected() {
    yesBtn.classList.remove('ms-btn--selected-yes');
    noBtn.classList.remove('ms-btn--selected-no');
  }

  var modalEl = null;

  function ensureModal() {
    if (modalEl) return;
    modalEl = document.createElement('div');
    modalEl.className = 'ms-modal';
    modalEl.innerHTML =
      '<div class="ms-modal-card">' +
      '<h3 class="ms-modal-title">Updated</h3>' +
      '<p class="ms-modal-desc">Scan recorded into ST_ITEM_TPLDTL.</p>' +
      '<div class="ms-modal-actions">' +
      '<button type="button" id="ms-next-scan" class="ms-btn ms-btn--primary">Next scan</button>' +
      '<a href="/Dashboard" class="ms-btn ms-btn--outline" style="display:inline-block;text-align:center;text-decoration:none;">Back to home</a>' +
      '</div></div>';
    document.body.appendChild(modalEl);
    modalEl.addEventListener('click', function (e) {
      if (e.target === modalEl) hideUpdatedModal();
    });
    document.addEventListener('keydown', function (e) {
      if (e.key === 'Escape') hideUpdatedModal();
    });
    modalEl.querySelector('#ms-next-scan').addEventListener('click', function () {
      resetForNextScan();
      hideUpdatedModal();
    });
  }

  function showUpdatedModal() { ensureModal(); modalEl.classList.add('is-visible'); }
  function hideUpdatedModal() { if (modalEl) modalEl.classList.remove('is-visible'); }

  function resetForNextScan() {
    scanResult.textContent = '';
    locationInput.value = '';
    remark1Input.value = '';
    remark2Input.value = '';
    remark3Input.value = '';
    projectInput.value = '';
    lastScannedInput.value = '';
    manualContainer.style.display = 'none';
    manualInput.innerHTML = '';
    manualInput.value = '';
    if (manualCodeInput) manualCodeInput.value = '';
    clearConfirmSelected();
    lastScannedCode = '';
    lastResolvedLocationCode = '';
    updateBtn.disabled = true;
    if (!scannerActive) startBtn.click();
  }

  async function stopScanner() {
    scannerActive = false;
    if (nativeRafId) { cancelAnimationFrame(nativeRafId); nativeRafId = 0; }
    try { await setTorchEnabled(false); } catch (e) {}
    flashOn = false;
    if (scannerMode === 'native' && nativeStream) {
      nativeStream.getTracks().forEach(function (t) { t.stop(); });
      nativeStream = null;
      nativeDetector = null;
    }
    if (scannerMode === 'quagga') {
      try { Quagga.stop(); } catch (e) {}
    }
    scannerMode = null;
    nativeDetectBusy = false;
    videoWrap.querySelectorAll('video, canvas, br[clear]').forEach(function (el) { el.remove(); });
    startBtn.textContent = 'Start Scanner';
    updateFlashUi();
  }

  async function tryStartNativeBarcodeScanner() {
    if (!('BarcodeDetector' in window)) return false;
    var formats;
    try {
      var supported = await BarcodeDetector.getSupportedFormats();
      var want = ['qr_code', 'code_128', 'ean_13', 'ean_8', 'code_39'];
      formats = want.filter(function (f) { return supported.indexOf(f) !== -1; });
      if (formats.length === 0) return false;
    } catch (e) { return false; }

    var detector;
    try { detector = new BarcodeDetector({ formats: formats }); }
    catch (e) { return false; }

    var preferRearPhone = {
      video: {
        width: { min: 320, ideal: 1280 },
        height: { min: 240, ideal: 720 },
        facingMode: { ideal: 'environment' }
      }
    };
    var anyCam = { video: { width: { min: 320, ideal: 1280 }, height: { min: 240, ideal: 720 } } };

    var stream;
    try { stream = await requestCamera(preferRearPhone); }
    catch (e1) {
      try { stream = await requestCamera(anyCam); }
      catch (e2) {
        if (!canUseLiveCamera()) throw e2;
        return false;
      }
    }

    var video = document.createElement('video');
    video.setAttribute('playsinline', '');
    video.setAttribute('autoplay', '');
    video.muted = true;
    video.srcObject = stream;
    videoWrap.appendChild(video);

    try { await video.play(); }
    catch (e) {
      stream.getTracks().forEach(function (t) { t.stop(); });
      video.remove();
      return false;
    }

    nativeStream = stream;
    nativeDetector = detector;
    scannerMode = 'native';

    var canvas = document.createElement('canvas');
    var ctx = canvas.getContext('2d');
    var minMs = 220;

    function nativeLoop() {
      if (!scannerActive || scannerMode !== 'native') return;
      nativeRafId = requestAnimationFrame(nativeLoop);
      if (nativeDetectBusy) return;
      var t = Date.now();
      if (t - nativeLastScanAttempt < minMs) return;
      if (video.readyState < 2) return;
      var w = video.videoWidth;
      var h = video.videoHeight;
      if (w < 8 || h < 8) return;
      nativeLastScanAttempt = t;
      nativeDetectBusy = true;
      canvas.width = w; canvas.height = h;
      ctx.drawImage(video, 0, 0);
      nativeDetector.detect(canvas).then(function (codes) {
        nativeDetectBusy = false;
        if (!scannerActive || scannerMode !== 'native' || !codes || codes.length === 0) return;
        var raw = codes[0].rawValue != null ? codes[0].rawValue : codes[0].value;
        if (raw == null || raw === '') return;
        void processScannedCode(String(raw).trim());
      }).catch(function () { nativeDetectBusy = false; });
    }

    scannerActive = true;
    startBtn.textContent = 'Stop Scanner';
    flashOn = false;
    nativeRafId = requestAnimationFrame(nativeLoop);
    requestAnimationFrame(function () { updateFlashUi(); });
    setTimeout(function () { updateFlashUi(); }, 400);
    return true;
  }

  function initQuaggaWithConstraints(constraints) {
    return new Promise(function (resolve, reject) {
      Quagga.init({
        inputStream: { type: 'LiveStream', target: videoWrap, constraints: constraints },
        locator: { patchSize: 'medium', halfSample: true },
        decoder: { readers: ['code_128_reader', 'ean_reader'] }
      }, function (err) {
        if (err) reject(err); else resolve();
      });
    });
  }

  async function startQuaggaScanner() {
    var preferRearPhone = {
      width: { min: 320, ideal: 1280 },
      height: { min: 240, ideal: 720 },
      facingMode: { ideal: 'environment' }
    };
    var anyWebcam = { width: { min: 320, ideal: 1280 }, height: { min: 240, ideal: 720 } };
    try {
      try { await initQuaggaWithConstraints(preferRearPhone); }
      catch (e1) {
        try { Quagga.stop(); } catch (e) {}
        await initQuaggaWithConstraints(anyWebcam);
      }
      Quagga.start();
      scannerMode = 'quagga';
      scannerActive = true;
      startBtn.textContent = 'Stop Scanner';
      flashOn = false;
      requestAnimationFrame(function () { updateFlashUi(); });
      setTimeout(function () { updateFlashUi(); }, 400);
    } catch (err) {
      throw err;
    }
  }

  async function processScannedCode(scannedCode) {
    var now = Date.now();
    if (now - lastDetectedAt < 1200) return;
    lastDetectedAt = now;
    if (!scannedCode) return;

    lastScannedCode = scannedCode;
    lastResolvedLocationCode = '';
    scanResult.textContent = 'Scanned: ' + scannedCode;
    locationInput.value = '';
    updateBtn.disabled = true;

    await stopScanner();

    try {
      var headers = { 'Content-Type': 'application/json' };
      if (antiforgeryToken) headers['X-CSRF-TOKEN'] = antiforgeryToken;
      var res = await fetch(validateUrl, {
        method: 'POST',
        headers: headers,
        body: JSON.stringify({ code: scannedCode })
      });
      var data = await res.json();
      if (!data.success) {
        alert('Error: ' + (data.message || 'Unknown error'));
        return;
      }
      if (data.exists) {
        var locDesc = (data.location != null ? String(data.location) : '').trim();
        lastResolvedLocationCode = (data.locationCode != null ? String(data.locationCode) : '').trim();
        locationInput.value = locDesc;
        projectInput.value = (data.project != null ? String(data.project) : '').trim();
        lastScannedInput.value = (data.lastScanned != null ? String(data.lastScanned) : '').trim();
        manualContainer.style.display = 'none';
        manualInput.value = '';

        var canConfirm = locDesc.length > 0 || lastResolvedLocationCode.length > 0;
        if (!canConfirm) {
          clearConfirmSelected();
          updateBtn.disabled = true;
        } else {
          setConfirmSelected('yes');
          updateBtn.disabled = !lastScannedCode;
        }
      } else {
        clearConfirmSelected();
        manualContainer.style.display = 'none';
        updateBtn.disabled = true;
        alert(data.message || 'Code not found.');
      }
    } catch (err) {
      console.error(err);
      alert('Error validating code. Check console.');
    }
  }

  async function startScanner() {
    if (!canUseLiveCamera()) {
      var insecure = new Error('Not a secure context');
      insecure.name = 'SecurityError';
      throw insecure;
    }
    if (!navigator.mediaDevices && !navigator.getUserMedia && !navigator.webkitGetUserMedia) {
      throw new Error('This browser does not support camera access.');
    }
    var nativeOk = await tryStartNativeBarcodeScanner();
    if (nativeOk) return;
    if (typeof Quagga === 'undefined') {
      throw new Error('Quagga library not loaded');
    }
    await startQuaggaScanner();
  }

  startBtn.addEventListener('click', async function () {
    if (scannerActive) { await stopScanner(); return; }
    try {
      await startScanner();
    } catch (e) {
      alertScannerStartFailed(e, true);
      await stopScanner();
    }
  });

  if (typeof Quagga !== 'undefined') {
    Quagga.onDetected(function (result) {
      var scannedCode = result && result.codeResult && result.codeResult.code;
      if (!scannedCode) return;
      void processScannedCode(scannedCode);
    });
  }

  yesBtn.addEventListener('click', function () {
    setConfirmSelected('yes');
    manualContainer.style.display = 'none';
    manualInput.value = '';
    updateBtn.disabled = !lastScannedCode;
  });

  noBtn.addEventListener('click', function () {
    setConfirmSelected('no');
    manualContainer.style.display = 'block';
    updateBtn.disabled = true;

    fetch(locationsUrl)
      .then(function (res) { return res.json(); })
      .then(function (data) {
        if (!data.success || !Array.isArray(data.locations)) {
          alert('Failed to load locations.');
          return;
        }
        manualInput.innerHTML = '';
        var placeholder = document.createElement('option');
        placeholder.value = '';
        placeholder.text = '-- Select Location --';
        manualInput.appendChild(placeholder);
        data.locations.forEach(function (loc) {
          var opt = document.createElement('option');
          opt.value = loc;
          opt.text = loc;
          manualInput.appendChild(opt);
        });
        manualInput.onchange = function () {
          updateBtn.disabled = (manualInput.value === '') || !lastScannedCode;
        };
      })
      .catch(function (err) {
        console.error(err);
        alert('Failed to load locations.');
      });
  });

  if (manualCodeBtn && manualCodeInput) {
    manualCodeBtn.addEventListener('click', function () {
      var v = (manualCodeInput.value || '').trim();
      if (!v) { manualCodeInput.focus(); return; }
      void processScannedCode(v);
    });
    manualCodeInput.addEventListener('keydown', function (e) {
      if (e.key === 'Enter') { e.preventDefault(); manualCodeBtn.click(); }
    });
  }

  updateBtn.addEventListener('click', async function () {
    if (!lastScannedCode) { alert('No scanned code.'); return; }

    if (window.__msOperatorPromptPromise) {
      try { await window.__msOperatorPromptPromise; } catch (e) {}
    }

    var manualOpen = manualContainer.style.display === 'block';
    var chosenLocation =
      (manualOpen && manualInput.value)
        ? manualInput.value
        : (locationInput.value || '').trim();

    var locationCodeForInsert = manualOpen ? '' : lastResolvedLocationCode;

    var operatorName =
      (window.MaintenanceScannerOperator && window.MaintenanceScannerOperator.read)
        ? window.MaintenanceScannerOperator.read()
        : '';

    try {
      var headers = { 'Content-Type': 'application/json' };
      if (antiforgeryToken) headers['X-CSRF-TOKEN'] = antiforgeryToken;
      var res = await fetch(insertUrl, {
        method: 'POST',
        headers: headers,
        body: JSON.stringify({
          code: lastScannedCode,
          locationDesc: chosenLocation,
          locationCode: locationCodeForInsert,
          operatorDisplayName: operatorName,
          remark1: remark1Input.value || '',
          remark2: remark2Input.value || '',
          remark3: remark3Input.value || ''
        })
      });
      var data = await res.json();
      if (!data.success) {
        var detail = data.detail ? '\n\nDetail: ' + data.detail : '';
        alert('Insert failed: ' + (data.message || 'Unknown error') + detail);
        return;
      }
      updateBtn.disabled = true;
      showUpdatedModal();
    } catch (err) {
      console.error(err);
      alert('Insert failed. Check console.');
    }
  });
})();
