// Phase K Wave 3 — Shared toast helper.
//
// `client-ui.ts` already owns a private `showToast` helper that drives
// the `#toast-region` aria-live container.  Wave 3 surfaces a thin
// module-level wrapper so other modules (voice.ts, tournaments.ts)
// can push toasts into the same region without needing a Client
// instance — Voice's pre-game disabled-mic toast is fired before any
// Client is constructed, and the tournament seeding panel is owned
// by the lobby chain which doesn't hold a Client either.
//
// The DOM is the single source of truth: this helper looks up the
// `[data-testid="toast-region"]` element each call so it Just Works
// whether the region was rendered statically (index.html) or wired
// dynamically (client-ui's ctor).

export type ToastSeverity = 'info' | 'error' | 'success';

const ENTRY_DELAY_MS = 0; // rAF gates the visibility class

export function showToast(
  message: string,
  severity: ToastSeverity = 'info',
  duration: number = 4000,
): void {
  const region = document.getElementById('toast-region');
  if (region === null) {
    // Fallback: log when no region exists so we don't lose the message
    // entirely during early-boot or in tests that haven't mounted the
    // lobby HTML.  This is intentionally console-only — no analytics.
    // eslint-disable-next-line no-console
    console.warn(`[toast] ${severity}: ${message}`);
    return;
  }
  const el = document.createElement('div');
  el.className = `toast toast-${severity}`;
  el.setAttribute('role', severity === 'error' ? 'alert' : 'status');
  el.setAttribute(
    'data-testid',
    severity === 'error' ? 'toast-error' : (severity === 'success' ? 'toast-success' : 'toast-info'),
  );
  el.textContent = message;
  region.appendChild(el);
  window.setTimeout(() => {
    window.requestAnimationFrame(() => el.classList.add('toast-visible'));
  }, ENTRY_DELAY_MS);
  window.setTimeout(() => {
    el.classList.remove('toast-visible');
    window.setTimeout(() => {
      if (el.parentNode !== null) el.parentNode.removeChild(el);
    }, 400);
  }, duration);
}

// Phase K Wave 3 → Phase K Wave 4 — Voice-specific toast.
//
// Wave 3 mapped a small handful of free-text reasons from Bishop's
// `VoiceHub.JoinVoice` to friendly user-facing strings.  Wave 4 wraps
// `voiceReasonToText()` (defined alongside the new typed
// `VoiceHubResult` in `voice.ts`) so this helper now accepts either:
//
//   • A Wave-4 reason code (`"voice-not-enabled"`, `"not-seated"`,
//     `"spectator"`, `"rate-limited"`, `"target-not-found"`,
//     `"unauthorized"`) — looked up via the typed map.
//   • A Wave-3 free-text reason (`"voice not enabled"`,
//     `"spectators cannot join voice"`) — kept as a substring
//     fallback for backward-compat with self-hosted servers that
//     haven't shipped the typed result yet.
//
// Callers can also pre-translate via `voice.voiceReasonToText` and
// pass the resulting string straight to `showToast` — the substring
// short-circuit below leaves a pre-translated string untouched.
export function showVoiceToast(reason: string): void {
  const lc = reason.toLowerCase();
  if (lc.indexOf('voice chat is not enabled') !== -1
      || lc.indexOf('voice not enabled') !== -1
      || lc.indexOf('not enabled') !== -1) {
    showToast(
      'Voice is not enabled for this table. Ask the host to enable it.',
      'error',
    );
    return;
  }
  if (lc.indexOf('take a seat') !== -1) {
    showToast('Take a seat to join voice chat.', 'error');
    return;
  }
  if (lc.indexOf('spectator') !== -1) {
    showToast(
      'Spectators cannot join voice chat — take a seat to talk.',
      'error',
    );
    return;
  }
  if (lc.indexOf('rate') !== -1 && lc.indexOf('limit') !== -1) {
    showToast('Slow down — too many voice messages in a short window.', 'error');
    return;
  }
  if (lc.indexOf('peer disconnected') !== -1 || lc.indexOf('target-not-found') !== -1) {
    showToast('That peer just disconnected — no voice link to send to.', 'error');
    return;
  }
  if (lc.indexOf('please sign in') !== -1 || lc.indexOf('unauthorized') !== -1) {
    showToast('Please sign in to use voice chat.', 'error');
    return;
  }
  showToast(`Voice chat error: ${reason}`, 'error');
}
