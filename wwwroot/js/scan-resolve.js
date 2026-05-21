/* scan-resolve.js — resolve scanned URL to item code via server (fetches page body) */
(function (global) {
  'use strict';

  const isUrl = (s) => /^https?:\/\//i.test(String(s || '').trim());

  async function resolve(resolveUrl, scanned, knownCodes) {
    const raw = String(scanned ?? '').trim();
    if (!raw) return { scanned: '', itemCode: null, source: '', error: 'Empty scan.' };
    if (!isUrl(raw)) {
      return { scanned: raw, itemCode: raw, source: 'raw', error: null };
    }

    const params = new URLSearchParams();
    params.set('url', raw);
    (knownCodes || []).forEach((c) => {
      const t = String(c || '').trim();
      if (t) params.append('codes', t);
    });

    const sep = resolveUrl.includes('?') ? '&' : '?';
    const res = await fetch(`${resolveUrl}${sep}${params}`, { credentials: 'same-origin' });
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

  const sourceLabel = (source) => {
    const map = {
      'page-match': 'found on linked page',
      'page-text-match': 'found in page text',
      'page-html': 'read from page HTML',
      'page-json': 'read from page JSON',
      'url-query': 'from URL parameter',
      'url-path': 'from URL path',
      raw: 'scanned text',
    };
    return map[source] || source || '';
  };

  global.ScanResolve = { isUrl, resolve, codesEqual, codesMatch, sourceLabel };
})(window);
