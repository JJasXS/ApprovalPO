/* scan-qr-scanner.js — HTTPS: live camera (BarcodeDetector + jsQR). HTTP: photo of QR (works on phone). */
(function (global) {
  'use strict';

  const PREFER_REAR = {
    video: {
      width: { min: 320, ideal: 1280 },
      height: { min: 240, ideal: 720 },
      facingMode: { ideal: 'environment' },
    },
  };

  const ANY_CAM = {
    video: { width: { min: 320, ideal: 1280 }, height: { min: 240, ideal: 720 } },
  };

  function requestCamera(constraints) {
    const nav = global.navigator;
    if (nav?.mediaDevices?.getUserMedia) {
      return nav.mediaDevices.getUserMedia(constraints);
    }
    const legacy = nav?.getUserMedia || nav?.webkitGetUserMedia || nav?.mozGetUserMedia;
    if (legacy) {
      return new Promise((resolve, reject) => {
        legacy.call(nav, constraints, resolve, reject);
      });
    }
    return Promise.reject(new Error('Live camera is not available in this browser.'));
  }

  function create(opts) {
    const onDetected = opts.onDetected;
    const debounceMs = opts.debounceMs ?? 1200;
    const closeOnDetect = opts.closeOnDetect !== false;

    const overlay = document.getElementById(opts.overlayId || 'qrOverlay');
    const video = document.getElementById(opts.videoId || 'qrVideo');
    const canvas = document.getElementById(opts.canvasId || 'qrCanvas');
    const statusEl = document.getElementById(opts.statusId || 'qrStatus');
    const closeBtn = document.getElementById(opts.closeBtnId || 'qrCloseBtn');
    const photoPanel = document.getElementById(opts.photoPanelId || 'qrPhotoPanel');
    const photoBtn = document.getElementById(opts.photoBtnId || 'qrPhotoBtn');
    const photoInput = document.getElementById(opts.photoInputId || 'qrPhotoInput');
    const cameraHelp = document.getElementById(opts.cameraHelpId || 'qrCameraHelp');
    const retryCameraBtn = document.getElementById(opts.retryCameraBtnId || 'qrRetryCameraBtn');
    const usePhotoBtn = document.getElementById(opts.usePhotoBtnId || 'qrUsePhotoBtn');
    const cameraPermState = document.getElementById(opts.cameraPermStateId || 'qrCameraPermState');
    const liveBox = document.querySelector('.qr-box');

    let mode = null;
    let stream = null;
    let rafId = null;
    let locked = false;
    let lastDetectedAt = 0;
    let nativeDetector = null;
    let nativeBusy = false;
    let nativeLastAttempt = 0;
    const nativeMinMs = 220;

    const useLiveCamera = () => global.isSecureContext === true;

    const setStatus = (msg, tone = '') => {
      if (!statusEl) return;
      statusEl.textContent = msg;
      statusEl.className = 'qr-status' + (tone ? ` ${tone}` : '');
    };

    const isCameraDenied = (err) => {
      const name = String(err?.name || '');
      const msg = String(err?.message || err || '').toLowerCase();
      return (
        name === 'NotAllowedError' ||
        name === 'PermissionDeniedError' ||
        name === 'SecurityError' ||
        msg.includes('permission') ||
        msg.includes('denied') ||
        msg.includes('not allowed') ||
        msg.includes('notallowed')
      );
    };

    const setPhotoMode = (photo) => {
      overlay?.classList.toggle('qr-overlay--photo', photo);
      if (liveBox) liveBox.hidden = photo;
      if (photoPanel) photoPanel.hidden = !photo;
    };

    const updateCameraPermState = async () => {
      if (retryCameraBtn) retryCameraBtn.hidden = true;
      if (!cameraPermState) return;
      try {
        if (!navigator.permissions?.query) {
          cameraPermState.hidden = false;
          cameraPermState.textContent =
            'The browser does not report camera status. Use Take photo, or reset Camera in site settings (see steps below).';
          return;
        }
        const status = await navigator.permissions.query({ name: 'camera' });
        cameraPermState.hidden = false;
        if (status.state === 'granted') {
          cameraPermState.textContent = 'Camera is allowed. Tap Try live camera again.';
          if (retryCameraBtn) retryCameraBtn.hidden = false;
        } else if (status.state === 'prompt') {
          cameraPermState.textContent =
            'Camera is not set yet. Tap Try live camera again — the browser should ask Allow / Block.';
          if (retryCameraBtn) retryCameraBtn.hidden = false;
        } else {
          cameraPermState.textContent =
            'Camera is blocked (denied). This app cannot undo that — change it in browser settings below, or use Take photo of QR.';
          if (retryCameraBtn) retryCameraBtn.hidden = true;
        }
        status.onchange = () => void updateCameraPermState();
      } catch (_) {
        cameraPermState.hidden = false;
        cameraPermState.textContent =
          'Reset Camera in your browser site settings (steps below), or use Take photo of QR.';
      }
    };

    const showCameraDeniedHelp = () => {
      overlay?.classList.add('qr-overlay--camera-denied');
      setPhotoMode(true);
      if (cameraHelp) cameraHelp.hidden = false;
      if (photoPanel) photoPanel.hidden = true;
      if (liveBox) liveBox.hidden = true;
      setStatus('Use Take photo, or reset Camera in browser settings.', 'is-error');
      void updateCameraPermState();
    };

    const hideCameraDeniedHelp = () => {
      overlay?.classList.remove('qr-overlay--camera-denied');
      if (cameraHelp) cameraHelp.hidden = true;
      if (cameraPermState) cameraPermState.hidden = true;
      if (liveBox) liveBox.hidden = false;
    };

    const openPhotoFallback = () => {
      hideCameraDeniedHelp();
      if (typeof global.jsQR !== 'function') {
        setStatus('QR scanner library not loaded.', 'is-error');
        return;
      }
      locked = false;
      setPhotoMode(true);
      overlay?.classList.add('is-open');
      document.body.style.overflow = 'hidden';
      setStatus('Tap Take photo, then point at the QR code.');
    };

    const stopLoop = () => {
      if (rafId != null) {
        cancelAnimationFrame(rafId);
        rafId = null;
      }
    };

    const stopStream = () => {
      stopLoop();
      if (stream) {
        stream.getTracks().forEach((t) => t.stop());
        stream = null;
      }
      if (video) video.srcObject = null;
      nativeDetector = null;
      mode = null;
      nativeBusy = false;
    };

    const close = () => {
      stopStream();
      locked = false;
      setPhotoMode(false);
      hideCameraDeniedHelp();
      overlay?.classList.remove('is-open');
      document.body.style.overflow = '';
      if (photoInput) photoInput.value = '';
    };

    const unlockAfterDetect = () => {
      if (closeOnDetect) return;
      setTimeout(() => {
        locked = false;
        if (mode && overlay?.classList.contains('is-open')) {
          setStatus('Point camera at next QR code…');
        }
      }, Math.min(debounceMs, 900));
    };

    const emitDetected = (raw) => {
      const text = String(raw ?? '').trim();
      if (!text) return;
      const now = Date.now();
      if (locked || now - lastDetectedAt < debounceMs) return;
      locked = true;
      lastDetectedAt = now;
      if (closeOnDetect) {
        stopStream();
      }
      setStatus(closeOnDetect ? `QR found: ${text}` : 'Scanned — scan next…', 'is-found');
      try {
        onDetected(text, { keepOpen: !closeOnDetect });
      } catch (e) {
        console.error(e);
        locked = false;
      } finally {
        unlockAfterDetect();
      }
    };

    const decodeJsQr = (ctx, w, h) => {
      if (typeof global.jsQR !== 'function') return null;
      try {
        const img = ctx.getImageData(0, 0, w, h);
        const code = global.jsQR(img.data, w, h, { inversionAttempts: 'attemptBoth' });
        return code?.data || null;
      } catch (_) {
        return null;
      }
    };

    const decodeFromImageFile = (file) =>
      new Promise((resolve, reject) => {
        const img = new Image();
        const url = URL.createObjectURL(file);
        img.onload = () => {
          try {
            const c = document.createElement('canvas');
            const w = img.naturalWidth;
            const h = img.naturalHeight;
            if (w < 8 || h < 8) {
              reject(new Error('Image too small.'));
              return;
            }
            c.width = w;
            c.height = h;
            const ctx = c.getContext('2d', { willReadFrequently: true });
            ctx.drawImage(img, 0, 0);
            const data = decodeJsQr(ctx, w, h);
            if (data) resolve(data);
            else reject(new Error('No QR code found. Try again with the code centred and in focus.'));
          } finally {
            URL.revokeObjectURL(url);
          }
        };
        img.onerror = () => {
          URL.revokeObjectURL(url);
          reject(new Error('Could not read that image.'));
        };
        img.src = url;
      });

    const openPhotoScan = () => {
      if (typeof global.jsQR !== 'function') {
        setStatus('QR scanner library not loaded.', 'is-error');
        return;
      }
      locked = false;
      hideCameraDeniedHelp();
      setPhotoMode(true);
      overlay?.classList.add('is-open');
      document.body.style.overflow = 'hidden';
      setStatus('Tap Take photo, then point at the QR code.');
    };

    const onPhotoSelected = async () => {
      const file = photoInput?.files?.[0];
      if (!file) return;
      setStatus('Reading QR from photo…');
      try {
        const text = await decodeFromImageFile(file);
        emitDetected(text);
      } catch (err) {
        locked = false;
        setStatus(err.message || 'Could not read QR.', 'is-error');
        if (photoInput) photoInput.value = '';
      }
    };

    const loopJsQr = () => {
      if (mode !== 'jsqr') return;
      rafId = requestAnimationFrame(loopJsQr);
      if (!video || !canvas || video.readyState < 2) return;
      const w = video.videoWidth;
      const h = video.videoHeight;
      if (w < 8 || h < 8) return;
      const ctx = canvas.getContext('2d', { willReadFrequently: true });
      canvas.width = w;
      canvas.height = h;
      ctx.drawImage(video, 0, 0, w, h);
      const data = decodeJsQr(ctx, w, h);
      if (data) emitDetected(data);
    };

    const loopNative = () => {
      if (mode !== 'native' || !nativeDetector || !canvas) return;
      rafId = requestAnimationFrame(loopNative);
      if (nativeBusy) return;
      const now = Date.now();
      if (now - nativeLastAttempt < nativeMinMs) return;
      if (!video || video.readyState < 2) return;
      const w = video.videoWidth;
      const h = video.videoHeight;
      if (w < 8 || h < 8) return;

      nativeLastAttempt = now;
      nativeBusy = true;
      const ctx = canvas.getContext('2d', { willReadFrequently: true });
      canvas.width = w;
      canvas.height = h;
      ctx.drawImage(video, 0, 0, w, h);
      nativeDetector
        .detect(canvas)
        .then((codes) => {
          nativeBusy = false;
          if (mode !== 'native' || !codes?.length) return;
          const raw = codes[0].rawValue != null ? codes[0].rawValue : codes[0].value;
          if (raw != null && raw !== '') emitDetected(String(raw).trim());
        })
        .catch(() => {
          nativeBusy = false;
        });
    };

    const attachStream = async (mediaStream) => {
      stream = mediaStream;
      if (!video) return;
      video.srcObject = stream;
      await video.play();
    };

    const requestCameraWithFallback = async () => {
      try {
        return await requestCamera(PREFER_REAR);
      } catch (e1) {
        try {
          return await requestCamera(ANY_CAM);
        } catch (e2) {
          if (isCameraDenied(e2) || isCameraDenied(e1)) throw e2;
          throw e1;
        }
      }
    };

    const tryNative = async () => {
      if (!('BarcodeDetector' in global)) return false;
      let formats;
      try {
        const supported = await global.BarcodeDetector.getSupportedFormats();
        formats = ['qr_code'].filter((f) => supported.includes(f));
        if (!formats.length) return false;
      } catch (_) {
        return false;
      }

      let detector;
      try {
        detector = new global.BarcodeDetector({ formats });
      } catch (_) {
        return false;
      }

      let mediaStream;
      try {
        mediaStream = await requestCameraWithFallback();
      } catch (err) {
        if (isCameraDenied(err)) throw err;
        return false;
      }

      try {
        await attachStream(mediaStream);
      } catch (_) {
        mediaStream.getTracks().forEach((t) => t.stop());
        return false;
      }

      nativeDetector = detector;
      mode = 'native';
      rafId = requestAnimationFrame(loopNative);
      return true;
    };

    const startJsQr = async () => {
      const mediaStream = await requestCameraWithFallback();
      await attachStream(mediaStream);
      mode = 'jsqr';
      rafId = requestAnimationFrame(loopJsQr);
    };

    const openLiveCamera = async () => {
      hideCameraDeniedHelp();
      setPhotoMode(false);
      setStatus('Point camera at a QR code…');
      overlay?.classList.add('is-open');
      document.body.style.overflow = 'hidden';

      try {
        const nativeOk = await tryNative();
        if (nativeOk) return;
        if (typeof global.jsQR !== 'function') {
          setStatus('QR scanner library not loaded.', 'is-error');
          return;
        }
        await startJsQr();
      } catch (err) {
        if (isCameraDenied(err)) {
          showCameraDeniedHelp();
          return;
        }
        setStatus(`Camera error: ${err.message || err}`, 'is-error');
      }
    };

    const open = () => {
      if (!overlay || !onDetected) return;
      locked = false;
      if (useLiveCamera()) {
        void openLiveCamera();
      } else {
        openPhotoScan();
      }
    };

    if (closeBtn) closeBtn.addEventListener('click', close);
    if (overlay) {
      overlay.addEventListener('click', (e) => {
        if (e.target === overlay) close();
      });
    }
    if (photoBtn && photoInput) {
      photoBtn.addEventListener('click', () => photoInput.click());
      photoInput.addEventListener('change', () => void onPhotoSelected());
    }
    if (retryCameraBtn) {
      retryCameraBtn.addEventListener('click', () => {
        locked = false;
        void openLiveCamera();
      });
    }
    if (usePhotoBtn) {
      usePhotoBtn.addEventListener('click', () => openPhotoFallback());
    }

    return {
      open,
      close,
      bindButton(el) {
        el?.addEventListener('click', () => open());
      },
    };
  }

  global.ScanQrScanner = { create };
})(window);
