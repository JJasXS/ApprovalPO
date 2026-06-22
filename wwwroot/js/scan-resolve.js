/* scan-resolve.js — resolve scanned URL or barcode text to item code via server (cached) */
(function (global) {
  'use strict';

  const CACHE_PREFIX = 'approvalpo-resolve-cache:';
  const CACHE_TTL_MS = 15 * 60 * 1000;

  const isUrl = (s) => /^https?:\/\//i.test(String(s || '').trim());

  function normalizeKnown(codes) {
    return (codes || [])
      .map((c) => String(c || '').trim())
      .filter(Boolean)
      .sort((a, b) => b.length - a.length);
  }

  function matchKnown(candidate, knownCodes) {
    const c = String(candidate || '').trim();
    if (!c || !knownCodes.length) return null;
    const exact = knownCodes.find((h) => h.toLowerCase() === c.toLowerCase());
    if (exact) return exact;
    for (const code of knownCodes) {
      const lc = code.toLowerCase();
      const lcc = c.toLowerCase();
      if (lcc.includes(lc) || lc.includes(lcc)) return code;
    }
    return null;
  }

  function matchKnownInText(text, knownCodes) {
    const hay = String(text || '');
    if (!hay || !knownCodes.length) return null;
    for (const code of knownCodes) {
      if (code && hay.toLowerCase().includes(code.toLowerCase())) return code;
    }
    return null;
  }

  function exactKnown(itemCode, knownCodes) {
    const c = String(itemCode || '').trim();
    if (!c || !knownCodes.length) return null;
    return knownCodes.find((h) => h.toLowerCase() === c.toLowerCase()) || null;
  }

  function isSkippableDocumentField(field) {
    const t = String(field || '').trim();
    if (!t) return false;
    return /^DO[-_]/i.test(t) || /^DO\d/i.test(t);
  }

  function normalizePayload(raw) {
    let text = String(raw || '').trim();
    text = text.replace(/\/\*.*?\*\//g, '').trim();
    text = text.replace(/^DO[-_][^;]+;/i, '').trim();
    return text;
  }

  function splitBarcodeLines(raw) {
    return String(raw || '')
      .split(/\r?\n/)
      .map((l) => l.trim())
      .filter((l) => l.includes(';'));
  }

  function parseSemicolonLine(raw, knownCodes) {
    const text = normalizePayload(raw);
    if (!text.includes(';')) return null;
    const parts = text.split(';').map((p) => p.trim());
    if (parts.length < 2) return null;

    let itemIndex = 0;
    if (parts.length > 1 && isSkippableDocumentField(parts[0])) itemIndex = 1;

    let itemCode = parts[itemIndex] || '';
    if (!itemCode) {
      for (let i = itemIndex; i < parts.length; i++) {
        if (!parts[i] || (i === 0 && isSkippableDocumentField(parts[i]))) continue;
        const matched = exactKnown(parts[i], knownCodes);
        if (matched) {
          itemCode = matched;
          break;
        }
      }
    } else {
      const matched = exactKnown(itemCode, knownCodes);
      itemCode = matched || itemCode;
    }
    if (!itemCode) return null;

    let quantity = null;
    const qtyIndex = itemIndex + 2;
    if (parts.length > qtyIndex && parts[qtyIndex]) {
      const q = Number(parts[qtyIndex].replace(/,/g, ''));
      if (Number.isFinite(q) && q > 0) quantity = q;
    }

    const unitIndex = qtyIndex + 1;
    const locIndex = qtyIndex + 2;
    const isProjectToken = (field) => /^P\d+$/i.test(projectNorm(field));
    const isSkippableLocation = (field) => {
      const t = String(field || '').trim();
      if (!t) return true;
      if (isSkippableDocumentField(t)) return true;
      const n = Number(t.replace(/,/g, ''));
      if (Number.isFinite(n) && n > 0) return true;
      return /^(UNIT|EA|PCS)$/i.test(t);
    };

    let location = null;
    if (parts.length > locIndex && parts[locIndex] && isProjectToken(parts[locIndex])) {
      location = parts[locIndex];
    } else {
      for (let i = parts.length - 1; i > itemIndex; i--) {
        const field = parts[i];
        if (field && isProjectToken(field)) {
          location = field;
          break;
        }
      }
      if (!location && parts.length > locIndex && parts[locIndex] && !isSkippableLocation(parts[locIndex])) {
        location = parts[locIndex];
      }
    }

    return {
      itemCode,
      quantity,
      unit: parts.length > unitIndex && parts[unitIndex] ? parts[unitIndex] : null,
      location,
    };
  }

  /** PMF-Pillow;;30;UNIT;P5 or legacy DO-987451;PMF-Pillow;;30;UNIT;P5 (DO ignored) */
  function parseSemicolonPayload(raw, knownCodes) {
    const lines = splitBarcodeLines(raw);
    const source = lines.length ? lines : (String(raw || '').includes(';') ? [raw] : []);
    for (const line of source) {
      const parsed = parseSemicolonLine(line, knownCodes);
      if (parsed) return parsed;
    }
    return null;
  }

  function parseSemicolonPayloadAll(raw, knownCodes) {
    const lines = splitBarcodeLines(raw);
    const source = lines.length ? lines : (String(raw || '').includes(';') ? [raw] : []);
    return source.map((line) => parseSemicolonLine(line, knownCodes)).filter(Boolean);
  }

  function projectNorm(s) {
    return String(s ?? '').trim().replace(/\s+/g, '');
  }

  function cacheKey(url, knownCodes) {
    const codes = (knownCodes || [])
      .map((c) => String(c || '').trim().toLowerCase())
      .filter(Boolean)
      .sort()
      .join('|');
    return CACHE_PREFIX + String(url || '').trim().toLowerCase() + ':' + codes;
  }

  function clearCachedForDoc(docKey) {
    if (!docKey) return;
    const suffix = `:dk${docKey}`;
    try {
      const keys = [];
      for (let i = 0; i < sessionStorage.length; i++) {
        const k = sessionStorage.key(i);
        if (k && k.startsWith(CACHE_PREFIX) && k.includes(suffix)) keys.push(k);
      }
      keys.forEach((k) => sessionStorage.removeItem(k));
    } catch (_) { /* ignore */ }
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

  function resolveLocal(raw, knownCodes) {
    const parsed = parseSemicolonPayload(raw, knownCodes);
    if (parsed) {
      return {
        scanned: raw,
        itemCode: parsed.itemCode,
        scanQuantity: parsed.quantity,
        scanLocation: parsed.location,
        source: 'semicolon-format',
        error: null,
      };
    }

    if (raw.includes(';')) {
      return {
        scanned: raw,
        itemCode: null,
        source: 'semicolon-format',
        error: 'Could not read item code from barcode.',
        errorCode: 'no_code_on_page',
      };
    }

    const matched = matchKnownInText(raw, knownCodes);
    if (matched) {
      return {
        scanned: raw,
        itemCode: matched,
        source: 'text-match',
        error: null,
      };
    }

    return { scanned: raw, itemCode: raw, source: 'raw', error: null };
  }

  async function resolve(resolveUrl, scanned, knownCodes, options = {}) {
    const raw = String(scanned ?? '').trim();
    const hints = normalizeKnown(knownCodes);
    const docKey = options.docKey != null ? String(options.docKey).trim() : '';
    if (!raw) return { scanned: '', itemCode: null, source: '', error: 'Empty scan.' };

    const useServer = Boolean(resolveUrl && docKey);
    if (!useServer && !isUrl(raw)) {
      return resolveLocal(raw, hints);
    }

    const cacheKeySuffix = docKey ? `:dk${docKey}` : '';
    const cached = getCached(raw + cacheKeySuffix, hints);
    if (cached) return { ...cached, fromCache: true };

    const params = new URLSearchParams();
    params.set('url', raw);
    if (docKey) params.set('docKey', docKey);
    hints.forEach((c) => params.append('codes', c));

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
    if (!(result.error || result.Error)) {
      setCached(raw + cacheKeySuffix, hints, result);
    }
    return result;
  }

  function codesEqual(a, b) {
    return String(a ?? '').trim().toLowerCase() === String(b ?? '').trim().toLowerCase();
  }

  function codesMatch(scannedCode, rowCode) {
    return codesEqual(scannedCode, rowCode);
  }

  const sourceLabel = (source) => {
    const map = {
      'semicolon-format': 'barcode fields',
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

  global.ScanResolve = {
    isUrl,
    resolve,
    parseSemicolonPayload,
    parseSemicolonPayloadAll,
    splitBarcodeLines,
    codesEqual,
    codesMatch,
    sourceLabel,
    getCached,
    setCached,
    clearCachedForDoc,
  };
})(window);
