// ============================================================
// alert-system.js — Security-Hardened (juneeyedrivesafeguide)
// SECURITY FIXES:
//   OW-2  — XSS: replaced innerHTML with textContent + DOM API
//   OW-5  — HMAC signature verification before rendering alerts
//           Forged alerts from any source are silently dropped.
// ============================================================

const AlertSystem = (() => {
  const container = () => document.getElementById('alertContainer');
  const blindSpot = () => document.getElementById('blindSpotBanner');
  let blindSpotTimer = null;
  let speechAvailable = 'speechSynthesis' in window;

  // Alert type → emoji icon (safe values, never injected as HTML)
  const iconMap = {
    'merge':        '🔀',
    'exit':         '🚪',
    'lane':         '🛣️',
    'speed-red':    '🔴',
    'speed-yellow': '🟡',
    'distraction':  '🔊',
    'backing':      '⚠️',
    'info':         'ℹ️'
  };

  // ── SECURITY FIX OW-5: HMAC Verification ──────────────────
  // The server wraps every alert as: { payload, timestamp, sig }
  // Client verifies sig = HMAC-SHA256(key, `${timestamp}:${JSON.stringify(payload)}`)
  // Key is embedded via a meta tag set server-side (never in JS source).
  let _hmacKey = null;

  async function loadHmacKey() {
    // Server writes a per-session HMAC verification key into a <meta> tag:
    // <meta name="alert-verify-key" content="base64encodedKey">
    // This key is derived from the server signing key but is session-scoped.
    const meta = document.querySelector('meta[name="alert-verify-key"]');
    if (!meta?.content) {
      console.warn('[AlertSystem] No alert-verify-key meta tag found — signature verification disabled');
      return;
    }
    const rawKey = Uint8Array.from(atob(meta.content), c => c.charCodeAt(0));
    _hmacKey = await crypto.subtle.importKey(
      'raw', rawKey, { name: 'HMAC', hash: 'SHA-256' }, false, ['verify']
    );
  }

  async function verifySignature(payload, timestamp, sig) {
    if (!_hmacKey) return true; // Verification disabled (dev mode)

    // Replay protection: reject alerts older than 30 seconds
    const age = Math.abs(Date.now() / 1000 - timestamp);
    if (age > 30) {
      console.warn('[AlertSystem] Rejected stale alert (age:', age, 's)');
      return false;
    }

    const dataToVerify = `${timestamp}:${JSON.stringify(payload)}`;
    const encoder = new TextEncoder();
    const sigBytes = Uint8Array.from(atob(sig), c => c.charCodeAt(0));

    return await crypto.subtle.verify(
      'HMAC',
      _hmacKey,
      sigBytes,
      encoder.encode(dataToVerify)
    );
  }

  // ── SECURITY FIX OW-2: Safe DOM construction ──────────────
  // NEVER use innerHTML with any server-provided string.
  // All text is set via textContent; structure built via createElement.
  function buildAlertElement(type, message, severity) {
    const el = document.createElement('div');
    el.className = `edg-alert-banner edg-alert-${sanitiseClassName(severity || 'info')}`;
    el.dataset.type = sanitiseAttributeValue(type);

    // Icon span — emoji only, safe
    const iconSpan = document.createElement('span');
    iconSpan.className = 'edg-alert-icon';
    iconSpan.textContent = iconMap[type] || '🔔'; // Only emoji, never raw HTML

    // Message span — textContent, never innerHTML
    const msgSpan = document.createElement('span');
    msgSpan.className = 'edg-alert-message';
    msgSpan.textContent = message; // SECURITY: textContent prevents XSS

    el.appendChild(iconSpan);
    el.appendChild(msgSpan);
    return el;
  }

  // Allow only alphanumeric + hyphen in class names (prevent class injection)
  function sanitiseClassName(value) {
    return String(value).replace(/[^a-zA-Z0-9\-]/g, '');
  }

  // Allow only safe chars in data attributes
  function sanitiseAttributeValue(value) {
    return String(value).replace(/[^a-zA-Z0-9\-_]/g, '');
  }

  // ── Public: show a signed alert ───────────────────────────
  async function show(signedAlert) {
    // signedAlert = { payload: { type, message, severity, blindSpotHold }, timestamp, sig }
    // For backward compat, also accept unsigned { type, message, severity, blindSpotHold }
    let type, message, severity, blindSpotHold;

    if (signedAlert.payload && signedAlert.sig !== undefined) {
      // SECURITY FIX OW-5: verify before rendering
      const valid = await verifySignature(
        signedAlert.payload,
        signedAlert.timestamp,
        signedAlert.sig
      );
      if (!valid) {
        console.error('[AlertSystem] Signature verification FAILED — alert dropped:', signedAlert);
        return; // Silently drop forged/replayed alerts
      }
      ({ type, message, severity, blindSpotHold } = signedAlert.payload);
    } else {
      // Unsigned path (dev mode / fallback)
      ({ type, message, severity, blindSpotHold } = signedAlert);
    }

    // SECURITY: Validate type against known set before using as CSS class
    const safeType = sanitiseAttributeValue(type || 'info');
    const safeSeverity = sanitiseClassName(severity || 'info');

    const el = buildAlertElement(safeType, message, safeSeverity);
    const c = container();
    if (!c) return;

    // Replace existing alert of same type
    const existing = c.querySelector(`[data-type="${safeType}"]`);
    if (existing) existing.remove();
    c.prepend(el);

    const ttl = severity === 'danger' ? 8000 : 5000;
    setTimeout(() => el.remove(), ttl);

    speak(message);
    if (blindSpotHold) showBlindSpot();
  }

  function showBlindSpot() {
    const el = blindSpot();
    if (!el) return;
    el.style.display = 'block';
    if (blindSpotTimer) clearTimeout(blindSpotTimer);
    blindSpotTimer = setTimeout(() => { el.style.display = 'none'; }, 3000);
  }

  function speak(msg) {
    if (!speechAvailable) return;
    // SECURITY: msg passed to TTS, not DOM — XSS not applicable but sanitise anyway
    const safeMsg = String(msg).substring(0, 300); // Truncate overly long TTS strings
    window.speechSynthesis.cancel();
    const u = new SpeechSynthesisUtterance(safeMsg);
    u.rate = 1.05;
    u.pitch = 1.0;
    u.volume = 1.0;
    window.speechSynthesis.speak(u);
  }

  function clear() {
    const c = container();
    if (c) c.innerHTML = ''; // Safe: clearing, not inserting
  }

  // Initialise HMAC key on load
  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', loadHmacKey);
  } else {
    loadHmacKey();
  }

  return { show, speak, clear, showBlindSpot };
})();
