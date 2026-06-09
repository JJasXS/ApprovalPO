// OCR scan popup: capture → rotate → AI read document → compare to PO lines in DB.
(function () {
  'use strict';

  var modal = document.getElementById('ocrModal');
  if (!modal) return;

  var openBtn = document.getElementById('ocrOpenBtn');
  var fileInput = document.getElementById('ocrFileInput');
  var captureBtn = document.getElementById('ocrCaptureBtn');
  var previewWrap = document.getElementById('ocrPreviewWrap');
  var previewImg = document.getElementById('ocrPreviewImg');
  var rotateBtn = document.getElementById('ocrRotateBtn');
  var statusEl = document.getElementById('ocrStatus');
  var compareWrap = document.getElementById('ocrCompareWrap');
  var compareSummary = document.getElementById('ocrCompareSummary');
  var compareBody = document.getElementById('ocrCompareBody');
  var aiBtn = document.getElementById('ocrAiBtn');
  var confirmBtn = document.getElementById('ocrConfirmBtn');

  var captureUrl = modal.getAttribute('data-capture-url') || '';
  var analyzeUrl = modal.getAttribute('data-analyze-url') || '';
  var confirmUrl = modal.getAttribute('data-confirm-url') || '';
  var scanPoUrl = modal.getAttribute('data-scan-po-url') || '';
  var docKey = modal.getAttribute('data-doc-key') || '';
  var poNumber = modal.getAttribute('data-po-number') || '';
  var csrf = modal.getAttribute('data-csrf-token') || '';

  var expectedLines = loadExpectedLines();
  var lastObjectUrl = null;
  var lastFile = null;
  var lastCleanedText = '';
  var lastCompare = null;

  function loadExpectedLines() {
    try {
      var el = document.getElementById('ocrExpectedLinesJson');
      if (!el || !el.textContent) return [];
      var parsed = JSON.parse(el.textContent);
      if (!Array.isArray(parsed)) return [];
      return parsed.slice().sort(function (a, b) {
        return normToken(a.code).localeCompare(normToken(b.code));
      });
    } catch (_) {
      return [];
    }
  }

  function sortRowsByCode(rows) {
    return rows.slice().sort(function (a, b) {
      var ac = a.po ? normToken(a.po.code) : normToken(a.scanned && a.scanned.code);
      var bc = b.po ? normToken(b.po.code) : normToken(b.scanned && b.scanned.code);
      return ac.localeCompare(bc);
    });
  }

  function setStatus(msg, kind) {
    if (!statusEl) return;
    if (!msg) {
      statusEl.hidden = true;
      statusEl.textContent = '';
      statusEl.className = 'ocr-status';
      return;
    }
    statusEl.hidden = false;
    statusEl.textContent = msg;
    statusEl.className = 'ocr-status' + (kind ? ' ocr-status--' + kind : '');
  }

  function openModal() {
    modal.hidden = false;
    modal.setAttribute('aria-hidden', 'false');
    document.body.classList.add('ocr-modal-open');
  }

  function closeModal() {
    modal.hidden = true;
    modal.setAttribute('aria-hidden', 'true');
    document.body.classList.remove('ocr-modal-open');
  }

  function resetUi() {
    setStatus('');
    if (compareWrap) compareWrap.hidden = true;
    if (compareSummary) compareSummary.innerHTML = '';
    if (compareBody) compareBody.innerHTML = '';
    if (rotateBtn) rotateBtn.hidden = true;
    if (aiBtn) { aiBtn.hidden = true; aiBtn.disabled = false; aiBtn.textContent = 'Analyze document'; }
    if (confirmBtn) { confirmBtn.hidden = true; confirmBtn.disabled = false; confirmBtn.textContent = 'Confirm & transfer to Goods Received'; }
    if (previewWrap) previewWrap.hidden = true;
    lastFile = null;
    lastCleanedText = '';
    lastCompare = null;
  }

  function clearCompare() {
    if (compareWrap) compareWrap.hidden = true;
    if (compareSummary) compareSummary.innerHTML = '';
    if (compareBody) compareBody.innerHTML = '';
    if (confirmBtn) confirmBtn.hidden = true;
  }

  if (openBtn) {
    openBtn.addEventListener('click', function () {
      resetUi();
      openModal();
    });
  }

  modal.addEventListener('click', function (e) {
    var t = e.target;
    if (t && t.hasAttribute && t.hasAttribute('data-ocr-close')) closeModal();
  });

  document.addEventListener('keydown', function (e) {
    if (e.key === 'Escape' && !modal.hidden) closeModal();
  });

  if (captureBtn && fileInput) {
    captureBtn.addEventListener('click', function () { fileInput.click(); });
  }

  if (fileInput) {
    fileInput.addEventListener('change', function () {
      var file = fileInput.files && fileInput.files[0];
      if (file) handleImage(file);
    });
  }

  if (aiBtn) aiBtn.addEventListener('click', function () { analyzeWithAi(); });
  if (rotateBtn) rotateBtn.addEventListener('click', function () { rotateImage90(); });
  if (confirmBtn) confirmBtn.addEventListener('click', function () { confirmTransfer(); });

  function showPreview(file) {
    if (!previewWrap || !previewImg) return;
    if (lastObjectUrl) { URL.revokeObjectURL(lastObjectUrl); lastObjectUrl = null; }
    lastObjectUrl = URL.createObjectURL(file);
    previewImg.src = lastObjectUrl;
    previewWrap.hidden = false;
    if (rotateBtn) rotateBtn.hidden = false;
  }

  function loadImageFromFile(file) {
    return new Promise(function (resolve, reject) {
      var img = new Image();
      var url = URL.createObjectURL(file);
      img.onload = function () {
        URL.revokeObjectURL(url);
        resolve(img);
      };
      img.onerror = function () {
        URL.revokeObjectURL(url);
        reject(new Error('Could not load image'));
      };
      img.src = url;
    });
  }

  function canvasToFile(canvas, baseName) {
    return new Promise(function (resolve, reject) {
      canvas.toBlob(function (blob) {
        if (!blob) { reject(new Error('Could not process image')); return; }
        var stem = (baseName || 'ocr-capture').replace(/\.[^.]+$/, '');
        resolve(new File([blob], stem + '.jpg', { type: 'image/jpeg', lastModified: Date.now() }));
      }, 'image/jpeg', 0.92);
    });
  }

  async function rotateImage90() {
    if (!lastFile) return;
    if (rotateBtn) rotateBtn.disabled = true;
    setStatus('Rotating…', 'busy');

    try {
      var img = await loadImageFromFile(lastFile);
      var w = img.naturalWidth || img.width;
      var h = img.naturalHeight || img.height;
      var canvas = document.createElement('canvas');
      canvas.width = h;
      canvas.height = w;
      var ctx = canvas.getContext('2d');
      if (!ctx) throw new Error('Canvas not supported');
      ctx.translate(canvas.width / 2, canvas.height / 2);
      ctx.rotate(Math.PI / 2);
      ctx.drawImage(img, -w / 2, -h / 2);

      lastFile = await canvasToFile(canvas, lastFile.name);
      clearCompare();
      showPreview(lastFile);
      if (aiBtn) { aiBtn.hidden = false; aiBtn.textContent = 'Analyze document'; }
      setStatus('Rotated 90°. Tap Analyze document when the page looks straight.', '');
    } catch (err) {
      setStatus('Could not rotate the image.', 'warn');
    } finally {
      if (rotateBtn) rotateBtn.disabled = false;
    }
  }

  async function handleImage(file) {
    resetUi();
    lastFile = file;
    showPreview(file);

    if (aiBtn && analyzeUrl) {
      aiBtn.hidden = false;
      setStatus('Photo added. Tap Rotate 90° until the page is straight, then tap Analyze document.', '');
    } else {
      setStatus('AI analysis is not available.', 'warn');
    }
  }

  function esc(s) {
    return String(s == null ? '' : s).replace(/[&<>"]/g, function (c) {
      return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c];
    });
  }

  function normToken(s) {
    return String(s || '').trim().toUpperCase().replace(/[^A-Z0-9]/g, '');
  }

  function normDesc(s) {
    return String(s || '').trim().toUpperCase().replace(/\s+/g, ' ');
  }

  function parseQty(v) {
    if (v == null || v === '') return null;
    var n = parseFloat(String(v).replace(/,/g, ''));
    return isNaN(n) ? null : n;
  }

  function formatQty(v) {
    var n = parseQty(v);
    if (n == null) return '—';
    return Number.isInteger(n) ? String(n) : n.toFixed(2);
  }

  function qtyMatch(poQty, scanQty) {
    var p = parseQty(poQty);
    var s = parseQty(scanQty);
    if (p == null && s == null) return true;
    if (p == null || s == null) return false;
    return Math.abs(p - s) < 0.02;
  }

  function descMatch(poDesc, scanDesc) {
    var pd = normDesc(poDesc);
    var sd = normDesc(scanDesc);
    if (!pd && !sd) return true;
    if (!pd || !sd) return false;
    if (pd === sd) return true;
    if (pd.length >= 4 && sd.length >= 4 && (pd.indexOf(sd) >= 0 || sd.indexOf(pd) >= 0)) return true;
    return false;
  }

  function tokensMatch(a, b) {
    if (!a || !b) return false;
    if (a === b) return true;
    return a.length >= 3 && b.length >= 3 && (a.indexOf(b) >= 0 || b.indexOf(a) >= 0);
  }

  function lineAccurate(poLine, scanned) {
    if (!scanned) return false;
    var hasCode = !!normToken(poLine.code);
    var codeOk = hasCode ? tokensMatch(normToken(poLine.code), normToken(scanned.code)) : true;
    var qtyOk = qtyMatch(poLine.qty, scanned.quantity);
    // When item code matches, description is validated against stock master on the server (not OCR text).
    if (hasCode && codeOk) return qtyOk;
    var descOk = descMatch(poLine.description, scanned.description);
    return codeOk && descOk && qtyOk;
  }

  function buildTransferLinesFromCompare(compare) {
    if (!compare || !compare.rows) return [];
    var lines = [];
    compare.rows.forEach(function (row) {
      if (row.kind !== 'po' || !row.scanned) return;
      var poCode = normToken(row.po && row.po.code);
      var scanCode = normToken(row.scanned.code);
      // Transfer only lines where OCR detected an item code that matches this PO line.
      if (!poCode || !scanCode || !tokensMatch(poCode, scanCode)) return;
      var qty = parseQty(row.scanned.quantity);
      if (qty == null || qty <= 0) return;
      lines.push({ itemCode: (row.po.code || '').trim(), quantity: qty });
    });
    return lines;
  }

  function buildCompare(data) {
    var f = (data && data.fields) || {};
    var expectedPo = normToken(poNumber);
    var detectedPo = normToken(f.documentNumber || '');
    var poDetected = detectedPo.length > 0;
    var poNumberMatch = poDetected && tokensMatch(expectedPo, detectedPo);

    var aiItems = (f.items && f.items.length) ? f.items : [];
    var used = {};
    var rows = [];
    var matches = new Array(expectedLines.length);

    // Pass 1: pair PO lines to scanned items by exact code (across ALL lines first),
    // so a code-less line can't steal a scanned item that belongs to a coded line.
    expectedLines.forEach(function (poLine, idx) {
      var normCode = normToken(poLine.code);
      if (!normCode) return;
      for (var i = 0; i < aiItems.length; i++) {
        if (used[i]) continue;
        if (tokensMatch(normCode, normToken(aiItems[i].code))) {
          matches[idx] = aiItems[i];
          used[i] = true;
          break;
        }
      }
    });

    // Pass 2: description match only for PO lines without an item code.
    expectedLines.forEach(function (poLine, idx) {
      if (matches[idx]) return;
      if (normToken(poLine.code)) return;
      for (var j = 0; j < aiItems.length; j++) {
        if (used[j]) continue;
        if (descMatch(poLine.description, aiItems[j].description)) {
          matches[idx] = aiItems[j];
          used[j] = true;
          break;
        }
      }
    });

    expectedLines.forEach(function (poLine, idx) {
      var scanned = matches[idx] || null;
      rows.push({
        kind: 'po',
        accurate: lineAccurate(poLine, scanned),
        po: poLine,
        scanned: scanned,
        issues: issuesForLine(poLine, scanned)
      });
    });

    aiItems.forEach(function (it, idx) {
      if (used[idx]) return;
      if (!normToken(it.code) && !normDesc(it.description)) return;
      rows.push({
        kind: 'extra',
        accurate: false,
        po: null,
        scanned: it,
        issues: ['Not on this PO in system']
      });
    });

    rows = sortRowsByCode(rows);

    var accurateCount = rows.filter(function (r) { return r.accurate; }).length;
    var poRows = rows.filter(function (r) { return r.kind === 'po'; });

    return {
      poNumberMatch: poNumberMatch,
      poDetected: poDetected,
      detectedPo: f.documentNumber || '',
      expectedPo: poNumber,
      rows: rows,
      accurateCount: accurateCount,
      totalPoLines: poRows.length
    };
  }

  function issuesForLine(poLine, scanned) {
    if (!scanned) return ['Not found on scanned document'];
    var issues = [];
    var hasCode = !!normToken(poLine.code);
    var codeOk = hasCode && tokensMatch(normToken(poLine.code), normToken(scanned.code));
    if (hasCode && !codeOk) issues.push('Code');
    // When item code matches, description is validated via stock master on the server (not OCR text).
    if (!codeOk && !descMatch(poLine.description, scanned.description)) issues.push('Description');
    if (!qtyMatch(poLine.qty, scanned.quantity)) issues.push('Qty');
    return issues;
  }

  function formatScannedDescription(sc) {
    if (!sc) return '<span class="ocr-compare__empty">—</span>';
    var main = esc(sc.description || '');
    if (sc.descriptionCorrected && sc.scannedDescription) {
      return main +
        '<span class="ocr-compare__hint" title="OCR read: ' + esc(sc.scannedDescription) + '"> · stock</span>';
    }
    return main;
  }

  function cellPair(poVal, scanVal, mismatch) {
    var po = poVal ? esc(poVal) : '<span class="ocr-compare__empty">—</span>';
    var sc = scanVal ? esc(scanVal) : '<span class="ocr-compare__empty">—</span>';
    if (mismatch && poVal && scanVal && poVal !== scanVal) {
      return '<td class="ocr-compare__cell ocr-compare__cell--bad">' + po + '</td>' +
        '<td class="ocr-compare__cell ocr-compare__cell--bad">' + sc + '</td>';
    }
    return '<td>' + po + '</td><td>' + sc + '</td>';
  }

  function renderCompare(data) {
    if (!compareWrap || !compareBody) return;
    var c = buildCompare(data);
    compareWrap.hidden = false;

    var poLine = '';
    if (!c.poDetected) {
      poLine = '<span class="ocr-compare__po ocr-compare__po--warn">PO # not read from document</span>';
    } else if (c.poNumberMatch) {
      poLine = '<span class="ocr-compare__po ocr-compare__po--ok">PO # ' + esc(c.detectedPo) + ' matches</span>';
    } else {
      poLine = '<span class="ocr-compare__po ocr-compare__po--bad">PO # on document: ' + esc(c.detectedPo) + ' (expected ' + esc(c.expectedPo) + ')</span>';
    }

    var badCount = c.rows.filter(function (r) { return !r.accurate; }).length;
    if (compareSummary) {
      compareSummary.innerHTML =
        poLine +
        '<span class="ocr-compare__counts">' +
        '<strong class="ocr-compare__ok">' + c.accurateCount + ' accurate</strong>' +
        ' · ' +
        '<strong class="ocr-compare__bad">' + badCount + ' not accurate</strong>' +
        ' (match by item code; qty must match)' +
        '</span>';
    }

    compareBody.innerHTML = c.rows.map(function (row) {
      var cls = row.accurate ? 'ocr-compare-row--ok' : 'ocr-compare-row--bad';
      var status = row.accurate
        ? '<span class="ocr-compare__badge ocr-compare__badge--ok">OK</span>'
        : '<span class="ocr-compare__badge ocr-compare__badge--bad">Not accurate</span>';

      if (row.kind === 'extra') {
        return '<tr class="' + cls + '">' +
          '<td>' + status + '</td>' +
          '<td class="ocr-compare__empty">—</td><td class="ocr-compare__empty">—</td><td class="num ocr-compare__empty">—</td>' +
          '<td>' + esc(row.scanned.code) + '</td>' +
          '<td>' + esc(row.scanned.description) + '</td>' +
          '<td class="num">' + esc(formatQty(row.scanned.quantity)) + '</td>' +
          '</tr>';
      }

      var po = row.po;
      var sc = row.scanned;
      var codeMismatch = sc && !tokensMatch(normToken(po.code), normToken(sc.code));
      var hasPoCode = !!normToken(po.code);
      var codePaired = hasPoCode && sc && tokensMatch(normToken(po.code), normToken(sc.code));
      var descMismatch = sc && !codePaired && !descMatch(po.description, sc.description);
      var qtyMismatch = sc && !qtyMatch(po.qty, sc.quantity);

      return '<tr class="' + cls + '" title="' + esc(row.issues.join(', ')) + '">' +
        '<td>' + status + '</td>' +
        '<td' + (codeMismatch ? ' class="ocr-compare__cell--bad"' : '') + '>' + esc(po.code) + '</td>' +
        '<td' + (descMismatch ? ' class="ocr-compare__cell--bad"' : '') + '>' + esc(po.description) + '</td>' +
        '<td class="num' + (qtyMismatch ? ' ocr-compare__cell--bad' : '') + '">' + esc(formatQty(po.qty)) + '</td>' +
        '<td' + (codeMismatch ? ' class="ocr-compare__cell--bad"' : '') + '>' + (sc ? esc(sc.code) : '<span class="ocr-compare__empty">—</span>') + '</td>' +
        '<td' + (descMismatch ? ' class="ocr-compare__cell--bad"' : '') + '>' + formatScannedDescription(sc) + '</td>' +
        '<td class="num' + (qtyMismatch ? ' ocr-compare__cell--bad' : '') + '">' + (sc ? esc(formatQty(sc.quantity)) : '<span class="ocr-compare__empty">—</span>') + '</td>' +
        '</tr>';
    }).join('');

    return c;
  }

  async function downscaleForUpload(file, maxDim, quality) {
    try {
      var img = await loadImageFromFile(file);
      var w = img.naturalWidth || img.width;
      var h = img.naturalHeight || img.height;
      if (!w || !h) return file;
      var scale = Math.min(1, maxDim / Math.max(w, h));
      if (scale >= 1) return file; // already small enough
      var cw = Math.round(w * scale);
      var ch = Math.round(h * scale);
      var canvas = document.createElement('canvas');
      canvas.width = cw;
      canvas.height = ch;
      canvas.getContext('2d').drawImage(img, 0, 0, cw, ch);
      return await new Promise(function (resolve) {
        canvas.toBlob(function (blob) {
          if (!blob) { resolve(file); return; }
          var stem = (file.name || 'ocr-capture').replace(/\.[^.]+$/, '');
          resolve(new File([blob], stem + '.jpg', { type: 'image/jpeg', lastModified: Date.now() }));
        }, 'image/jpeg', quality || 0.85);
      });
    } catch (_) {
      return file;
    }
  }

  async function sendAnalyze(maxDim, quality) {
    // Shrink big phone photos before upload so the AI call stays fast and doesn't time out.
    var uploadImage = await downscaleForUpload(lastFile, maxDim, quality);
    var fd = new FormData();
    fd.append('docKey', docKey);
    fd.append('poNumber', poNumber);
    fd.append('image', uploadImage, uploadImage.name || 'ocr-capture.jpg');
    var headers = {};
    if (csrf) headers['X-CSRF-TOKEN'] = csrf;
    var resp = await fetch(analyzeUrl, { method: 'POST', headers: headers, body: fd });
    var data = await resp.json().catch(function () { return null; });
    return { status: resp.status, data: data };
  }

  function isTimeoutResult(r) {
    if (!r) return true;
    if (r.status === 504 || r.status === 408 || r.status === 500) return true;
    if (!r.data) return true; // non-JSON (e.g. raw 500 before restart)
    if (r.data.ok === false && /time(d)?\s*out|timeout/i.test(String(r.data.error || ''))) return true;
    return false;
  }

  async function analyzeWithAi() {
    if (!lastFile || !analyzeUrl) return;

    var prev = aiBtn ? aiBtn.textContent : '';
    if (aiBtn) { aiBtn.disabled = true; aiBtn.textContent = 'Analyzing…'; }
    setStatus('Reading document and comparing to PO lines…', 'busy');
    clearCompare();

    try {
      var r = await sendAnalyze(2200, 0.85);
      // One automatic retry with a smaller/lighter image if the first call timed out.
      if (!(r.data && r.data.ok) && isTimeoutResult(r)) {
        setStatus('Taking longer than usual — retrying with a smaller image…', 'busy');
        r = await sendAnalyze(1500, 0.8);
      }
      var data = r.data;

      if (data && data.ok) {
        lastCleanedText = data.cleanedText || '';
        var c = renderCompare(data);
        lastCompare = c;
        var transferN = buildTransferLinesFromCompare(c).length;
        var allOk = c.poNumberMatch && c.accurateCount === c.totalPoLines && !c.rows.some(function (r) { return r.kind === 'extra'; });
        setStatus(
          allOk
            ? ('All lines match the PO. Confirm will transfer all ' + transferN + ' line(s).')
            : ('Confirm will transfer only ' + transferN + ' scanned line(s) (of ' + c.totalPoLines + ' on PO) with detected qty — not the full PO.'),
          allOk ? 'ok' : 'warn'
        );
        if (aiBtn) { aiBtn.textContent = 'Re-analyze'; aiBtn.disabled = false; }
        // Compare is ready — let the user confirm and transfer to a Goods Received.
        if (confirmBtn && confirmUrl) { confirmBtn.hidden = false; confirmBtn.disabled = false; }
        if (lastFile) uploadCapture(lastFile, lastCleanedText);
      } else {
        setStatus((data && data.error) || 'AI analysis failed.', 'warn');
        if (aiBtn) { aiBtn.disabled = false; aiBtn.textContent = prev; }
      }
    } catch (err) {
      setStatus('AI analysis failed.', 'warn');
      if (aiBtn) { aiBtn.disabled = false; aiBtn.textContent = prev; }
    }
  }

  async function confirmTransfer() {
    if (!confirmUrl) return;
    var transferLines = buildTransferLinesFromCompare(lastCompare);
    if (!transferLines.length) {
      setStatus('No scanned lines to transfer. Analyze the document first.', 'warn');
      return;
    }

    var prev = confirmBtn ? confirmBtn.textContent : '';
    if (confirmBtn) { confirmBtn.disabled = true; confirmBtn.textContent = 'Transferring…'; }
    setStatus('Creating Goods Received for ' + transferLines.length + ' scanned line(s)…', 'busy');
    try {
      var fd = new FormData();
      fd.append('docKey', docKey);
      fd.append('poNumber', poNumber);
      fd.append('transferLinesJson', JSON.stringify(transferLines));
      var scanCounts = {};
      transferLines.forEach(function (ln) {
        var c = (ln.itemCode || '').trim();
        if (!c) return;
        scanCounts[c] = Math.round(ln.quantity);
      });
      fd.append('scanCountsJson', JSON.stringify(scanCounts));
      var headers = {};
      if (csrf) headers['X-CSRF-TOKEN'] = csrf;
      var resp = await fetch(confirmUrl, { method: 'POST', headers: headers, body: fd });
      var data = await resp.json().catch(function () { return null; });

      if (data && data.ok) {
        setStatus(data.message || 'Goods Received created.', 'ok');
        if (confirmBtn) { confirmBtn.textContent = data.mode === 'gr' ? 'Transferred ✓' : 'Recorded ✓'; }
        // Move on to the submitted list so the PO reflects its new state.
        if (scanPoUrl) {
          setTimeout(function () { window.location.href = scanPoUrl; }, 1400);
        }
      } else {
        setStatus((data && data.error) || 'Could not transfer to Goods Received.', 'warn');
        if (confirmBtn) { confirmBtn.disabled = false; confirmBtn.textContent = prev; }
      }
    } catch (err) {
      setStatus('Could not transfer to Goods Received.', 'warn');
      if (confirmBtn) { confirmBtn.disabled = false; confirmBtn.textContent = prev; }
    }
  }

  async function uploadCapture(file, text) {
    if (!captureUrl) return;
    try {
      var fd = new FormData();
      fd.append('docKey', docKey);
      fd.append('poNumber', poNumber);
      fd.append('ocrText', text || '');
      fd.append('image', file, file.name || 'ocr-capture.png');

      var headers = {};
      if (csrf) headers['X-CSRF-TOKEN'] = csrf;

      await fetch(captureUrl, { method: 'POST', headers: headers, body: fd });
    } catch (_) { /* non-blocking */ }
  }
})();
