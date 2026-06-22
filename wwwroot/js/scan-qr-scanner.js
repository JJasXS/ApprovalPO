/* scan-qr-scanner.js — live camera (BarcodeDetector + multi-pass jsQR) or photo fallback on HTTP */
(function (global) {
  'use strict';

  const PREFER_REAR = {
    audio: false,
    video: {
      facingMode: { ideal: 'environment' },
      width: { min: 640, ideal: 1920, max: 4096 },
      height: { min: 480, ideal: 1080, max: 4096 },
      frameRate: { ideal: 30, max: 30 },
    },
  };

  const ANY_CAM = {
    audio: false,
    video: {
      width: { min: 640, ideal: 1920 },
      height: { min: 480, ideal: 1080 },
      frameRate: { ideal: 30, max: 30 },
    },
  };

  const SCAN_INTERVAL_MS = 90;
  const NATIVE_MIN_MS = 120;
  const MAX_DECODE_EDGE = 1280;

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

  function enhanceContrast(imageData) {
    const d = imageData.data;
    let min = 255;
    let max = 0;
    for (let i = 0; i < d.length; i += 4) {
      const g = d[i] * 0.299 + d[i + 1] * 0.587 + d[i + 2] * 0.114;
      if (g < min) min = g;
      if (g > max) max = g;
    }
    const range = max - min || 1;
    for (let i = 0; i < d.length; i += 4) {
      const g = d[i] * 0.299 + d[i + 1] * 0.587 + d[i + 2] * 0.114;
      const v = ((g - min) / range) * 255;
      d[i] = d[i + 1] = d[i + 2] = v;
    }
    return imageData;
  }

  function decodeJsQrRaw(imageData, w, h, options) {
    if (typeof global.jsQR !== 'function') return null;
    try {
      const code = global.jsQR(imageData.data, w, h, options || { inversionAttempts: 'attemptBoth' });
      return code?.data || null;
    } catch (_) {
      return null;
    }
  }

  function decodeRegion(sourceCanvas, sx, sy, sw, sh, scale, enhance) {
    const tw = Math.max(8, Math.round(sw * scale));
    const th = Math.max(8, Math.round(sh * scale));
    const tmp = document.createElement('canvas');
    tmp.width = tw;
    tmp.height = th;
    const tctx = tmp.getContext('2d', { willReadFrequently: true });
    if (!tctx) return null;
    tctx.drawImage(sourceCanvas, sx, sy, sw, sh, 0, 0, tw, th);
    let img = tctx.getImageData(0, 0, tw, th);
    if (enhance) img = enhanceContrast(img);
    return decodeJsQrRaw(img, tw, th);
  }

  function decodeJsQrMulti(sourceCanvas) {
    const w = sourceCanvas.width;
    const h = sourceCanvas.height;
    if (w < 8 || h < 8) return null;

    const fullScale = Math.min(1, MAX_DECODE_EDGE / Math.max(w, h));
    const regions = [
      { sx: 0, sy: 0, sw: w, sh: h, scale: fullScale, enhance: false },
      { sx: 0, sy: 0, sw: w, sh: h, scale: fullScale, enhance: true },
      { sx: w * 0.12, sy: h * 0.12, sw: w * 0.76, sh: h * 0.76, scale: 1.8, enhance: false },
      { sx: w * 0.12, sy: h * 0.12, sw: w * 0.76, sh: h * 0.76, scale: 1.8, enhance: true },
      { sx: w * 0.22, sy: h * 0.22, sw: w * 0.56, sh: h * 0.56, scale: 2.5, enhance: false },
    ];

    for (const r of regions) {
      const data = decodeRegion(sourceCanvas, r.sx, r.sy, r.sw, r.sh, r.scale, r.enhance);
      if (data) return data;
    }
    return null;
  }

  function decodeFromImageElement(img) {
    const w = img.naturalWidth || img.width;
    const h = img.naturalHeight || img.height;
    if (w < 8 || h < 8) return null;
    const c = document.createElement('canvas');
    c.width = w;
    c.height = h;
    const ctx = c.getContext('2d', { willReadFrequently: true });
    if (!ctx) return null;
    ctx.drawImage(img, 0, 0);
    let data = decodeJsQrMulti(c);
    if (data) return data;

    const scales = [0.75, 1.25, 1.5];
    for (const s of scales) {
      const tw = Math.round(w * s);
      const th = Math.round(h * s);
      const tmp = document.createElement('canvas');
      tmp.width = tw;
      tmp.height = th;
      const tctx = tmp.getContext('2d', { willReadFrequently: true });
      if (!tctx) continue;
      tctx.drawImage(img, 0, 0, tw, th);
      data = decodeJsQrMulti(tmp);
      if (data) return data;
    }
    return null;
  }

  function create(opts) {
    const onDetected = opts.onDetected;
    const debounceMs = opts.debounceMs ?? 900;
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
    const torchBtn = document.getElementById(opts.torchBtnId || 'qrTorchBtn');
    const photoFallbackBtn = document.getElementById(opts.photoFallbackBtnId || 'qrPhotoFallbackBtn');
    const liveBox = document.querySelector('.qr-box');

    let mode = null;
    let stream = null;
    let rafId = null;
    let locked = false;
    let lastDetectedAt = 0;
    let lastDetectedText = '';
    let lastScanAt = 0;
    let nativeDetector = null;
    let nativeBusy = false;
    let nativeLastAttempt = 0;
    let torchOn = false;
    let videoTrack = null;

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
      if (torchBtn) torchBtn.hidden = photo || !videoTrack;
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
      stopStream();
      locked = false;
      setPhotoMode(true);
      overlay?.classList.add('is-open');
      document.body.style.overflow = 'hidden';
      setStatus('Tap Take photo and fill the frame with the QR code.');
    };

    const stopLoop = () => {
      if (rafId != null) {
        cancelAnimationFrame(rafId);
        rafId = null;
      }
    };

    const stopStream = () => {
      stopLoop();
      torchOn = false;
      videoTrack = null;
      if (torchBtn) {
        torchBtn.hidden = true;
        torchBtn.classList.remove('is-on');
        torchBtn.textContent = 'Light';
      }
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
          setStatus('Scanned — point at next QR code…');
        }
      }, Math.min(debounceMs, 700));
    };

    const emitDetected = (raw) => {
      const text = String(raw ?? '').trim();
      if (!text) return;
      const now = Date.now();
      const isRepeat =
        text === lastDetectedText && now - lastDetectedAt < debounceMs;
      if (isRepeat) return;
      lastDetectedAt = now;
      lastDetectedText = text;
      if (closeOnDetect) {
        close();
      } else {
        locked = true;
      }
      setStatus(closeOnDetect ? `QR found: ${text}` : 'Scanned — scan next…', 'is-found');
      try {
        onDetected(text, { keepOpen: !closeOnDetect });
      } catch (e) {
        console.error(e);
      }
      if (!closeOnDetect) {
        unlockAfterDetect();
      }
    };

    const captureFrameCanvas = () => {
      if (!video || !canvas || video.readyState < 2) return null;
      const w = video.videoWidth;
      const h = video.videoHeight;
      if (w < 8 || h < 8) return null;
      canvas.width = w;
      canvas.height = h;
      const ctx = canvas.getContext('2d', { willReadFrequently: true });
      if (!ctx) return null;
      ctx.drawImage(video, 0, 0, w, h);
      return canvas;
    };

    const scanCurrentFrame = () => {
      const frame = captureFrameCanvas();
      if (!frame) return null;
      return decodeJsQrMulti(frame);
    };

    const decodeFromImageFile = (file) =>
      new Promise((resolve, reject) => {
        const img = new Image();
        const url = URL.createObjectURL(file);
        img.onload = () => {
          try {
            const data = decodeFromImageElement(img);
            if (data) resolve(data);
            else reject(new Error('No QR found. Fill the frame, hold steady, and try again.'));
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
      setStatus('Tap Take photo and fill the frame with the QR code.');
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

    const applyCameraEnhancements = async (track) => {
      if (!track?.applyConstraints) return;
      const attempts = [
        { advanced: [{ focusMode: 'continuous' }] },
        { focusMode: 'continuous' },
        { advanced: [{ focusMode: 'auto' }] },
      ];
      for (const c of attempts) {
        try {
          await track.applyConstraints(c);
          break;
        } catch (_) { /* ignore unsupported */ }
      }
    };

    const setupTorch = (track) => {
      videoTrack = track;
      const caps = track?.getCapabilities?.();
      const hasTorch = !!(caps && 'torch' in caps);
      if (torchBtn) {
        torchBtn.hidden = !hasTorch;
        torchBtn.classList.toggle('is-on', torchOn);
        torchBtn.textContent = torchOn ? 'Light on' : 'Light';
      }
    };

    const toggleTorch = async () => {
      if (!videoTrack?.applyConstraints) return;
      const caps = videoTrack.getCapabilities?.();
      if (!caps || !('torch' in caps)) return;
      torchOn = !torchOn;
      try {
        await videoTrack.applyConstraints({ advanced: [{ torch: torchOn }] });
        if (torchBtn) {
          torchBtn.classList.toggle('is-on', torchOn);
          torchBtn.textContent = torchOn ? 'Light on' : 'Light';
        }
      } catch (_) {
        torchOn = !torchOn;
      }
    };

    const loopScan = () => {
      if (mode !== 'jsqr' && mode !== 'native') return;
      rafId = requestAnimationFrame(loopScan);

      const now = Date.now();
      if (now - lastScanAt < SCAN_INTERVAL_MS) return;
      lastScanAt = now;

      const frame = captureFrameCanvas();
      if (!frame) return;

      if (typeof global.jsQR === 'function') {
        const jsData = decodeJsQrMulti(frame);
        if (jsData) {
          emitDetected(jsData);
          return;
        }
      }

      if (mode !== 'native' || !nativeDetector || nativeBusy) return;
      if (now - nativeLastAttempt < NATIVE_MIN_MS) return;
      nativeLastAttempt = now;
      nativeBusy = true;
      nativeDetector
        .detect(frame)
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
      video.setAttribute('playsinline', '');
      video.setAttribute('webkit-playsinline', '');
      await video.play();
      const track = stream.getVideoTracks()[0];
      if (track) {
        await applyCameraEnhancements(track);
        setupTorch(track);
      }
      setPhotoMode(false);
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

      nativeDetector = detector;
      return true;
    };

    const startLiveCamera = async () => {
      const mediaStream = await requestCameraWithFallback();
      await attachStream(mediaStream);
      await tryNative();
      mode = nativeDetector ? 'native' : 'jsqr';
      rafId = requestAnimationFrame(loopScan);
    };

    const openLiveCamera = async () => {
      hideCameraDeniedHelp();
      setPhotoMode(false);
      setStatus('Hold QR inside the frame. Move closer if it does not scan.');
      overlay?.classList.add('is-open');
      document.body.style.overflow = 'hidden';

      if (typeof global.jsQR !== 'function' && !('BarcodeDetector' in global)) {
        setStatus('QR scanner library not loaded.', 'is-error');
        return;
      }

      try {
        await startLiveCamera();
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
    if (photoFallbackBtn) {
      photoFallbackBtn.addEventListener('click', () => openPhotoFallback());
    }
    if (torchBtn) {
      torchBtn.addEventListener('click', () => void toggleTorch());
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
