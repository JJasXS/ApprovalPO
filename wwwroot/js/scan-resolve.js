/* scan-resolve.js — resolve scanned URL to item code via server (cached) */
(function (global) {
  'use strict';

  const CACHE_PREFIX = 'approvalpo-resolve-cache:';
  const CACHE_TTL_MS = 15 * 60 * 1000;

  const isUrl = (s) => /^https?:\/\//i.test(String(s || '').trim());

  function cacheKey(url, knownCodes) {
    const codes = (knownCodes || [])
      .map((c) => String(c || '').trim().toLowerCase())
      .filter(Boolean)
      .sort()
      .join('|');
    return CACHE_PREFIX + String(url || '').trim().toLowerCase() + ':' + codes;
  }

  function getCached(url, knownCodes) {
    try {
      const raw = sessionStorage.getItem(cacheKey(url, knownCodes));
      if (!raw) return null;
      const entry = JSON.parse(raw);
      if (!entry?.at || Date.now() - entry.at > CACHE_TTL_MS) {
        sessionStorage.removeItem(cacheKey(url, knownCodes));
        return null;
      }
      return entry.result;
    } catch (_) {
      return null;
    }
  }

  function setCached(url, knownCodes, result) {
    try {
      sessionStorage.setItem(
        cacheKey(url, knownCodes),
        JSON.stringify({ at: Date.now(), result })
      );
    } catch (_) { /* ignore */ }
  }

  async function resolve(resolveUrl, scanned, knownCodes) {
    const raw = String(scanned ?? '').trim();
    if (!raw) return { scanned: '', itemCode: null, source: '', error: 'Empty scan.' };
    if (!isUrl(raw)) {
      return { scanned: raw, itemCode: raw, source: 'raw', error: null };
    }

    const cached = getCached(raw, knownCodes);
    if (cached) return { ...cached, fromCache: true };

    const params = new URLSearchParams();
    params.set('url', raw);
    (knownCodes || []).forEach((c) => {
      const t = String(c || '').trim();
      if (t) params.append('codes', t);
    });

    const sep = resolveUrl.includes('?') ? '&' : '?';
    const res = await fetch(`${resolveUrl}${sep}${params}`, { credentials: 'same-origin' });
    if (!res.ok) {
      return {
        scanned: raw,
        itemCode: null,
        source: '',
        error: `Resolve failed (${res.status}).`,
        errorCode: 'resolve_failed',
      };
    }
    const result = await res.json();
    setCached(raw, knownCodes, result);
    return result;
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
      'text-match': 'matched text',
    };
    return map[source] || source || '';
  };

  global.ScanResolve = { isUrl, resolve, codesEqual, codesMatch, sourceLabel, getCached, setCached };
})(window);
