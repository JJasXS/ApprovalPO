/* scan-resolve.js — resolve scanned URL to item code via server */
(function (global) {
  'use strict';

  const isUrl = (s) => /^https?:\/\//i.test(String(s || '').trim());

  async function resolve(resolveUrl, scanned) {
    const raw = String(scanned ?? '').trim();
    if (!raw) return { scanned: '', itemCode: null, source: '', error: 'Empty scan.' };
    if (!isUrl(raw)) {
      return { scanned: raw, itemCode: raw, source: 'raw', error: null };
    }

    const url = `${resolveUrl}${resolveUrl.includes('?') ? '&' : '?'}url=${encodeURIComponent(raw)}`;
    const res = await fetch(url, { credentials: 'same-origin' });
    if (!res.ok) {
      return { scanned: raw, itemCode: null, source: '', error: `Resolve failed (${res.status}).` };
    }
    return res.json();
  }

  function codesEqual(a, b) {
    return String(a ?? '').trim().toLowerCase() === String(b ?? '').trim().toLowerCase();
  }

  function codesMatch(scannedCode, rowCode) {
    const s = String(scannedCode ?? '').trim().toLowerCase();
    const r = String(rowCode ?? '').trim().toLowerCase();
    if (!s || !r) return false;
    if (s === r) return true;
    if (s.length >= 2 && r.length >= 2 && (r.includes(s) || s.includes(r))) return true;
    return false;
  }

  global.ScanResolve = { isUrl, resolve, codesEqual, codesMatch };
})(window);
