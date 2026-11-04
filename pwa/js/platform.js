// The PWA's platform layer (#168, docs/design/ui.md §7, PWA). The PWA follows the
// Windows design everywhere (#166); this only settles the few details a platform does its own way:
//   * which shortcut a hint names, ⌘ on Apple's platforms and Ctrl elsewhere;
//   * whether the page draws under a phone's notch and home indicator (the safe areas), which only
//     an installed app on iOS or iPadOS asks for;
//   * no double-tap zoom wait on a tap, and the edge swipe back, which iOS gives an installed app no
//     other way;
//   * the inline mono ratio for the platform's system face (tokens.css, ui.md §4.2, #215).
// It is detection, set once at start-up on <html data-platform data-standalone>, never a reskin, and
// nothing may depend on it being right: "unknown" is Windows, both modifiers work everywhere, and
// a platform it gets wrong keeps the browser's own safe areas.

export const PLATFORMS = Object.freeze(['windows', 'macos', 'ios', 'ipados', 'android', 'chromeos', 'linux', 'unknown']);

// navigator.userAgentData.platform, in Chromium. Anything else it says is left to the user agent.
const CLIENT_HINTS = new Map([
  ['windows', 'windows'], ['macos', 'macos'], ['android', 'android'],
  ['chrome os', 'chromeos'], ['chromeos', 'chromeos'], ['linux', 'linux'],
]);

// Apple's platforms, where a shortcut is ⌘ (macOS, and iPadOS or iOS with a keyboard).
const APPLE = new Set(['macos', 'ipados', 'ios']);

// The platform, from Chromium's client hints where there are some, otherwise the user agent
// (Safari, Firefox). iPadOS asks for desktop pages as a Mac, so a Mac with a touch screen is an
// iPad: no Mac has one.
export function detectPlatform({ userAgentData = null, userAgent = '', maxTouchPoints = 0 } = {}) {
  const hinted = CLIENT_HINTS.get(String(userAgentData?.platform ?? '').trim().toLowerCase());
  if (hinted) { return hinted; }
  const ua = String(userAgent ?? '');
  // An iPhone's says "like Mac OS X", and Android's says Linux: those go first.
  if (/iPhone|iPod/.test(ua)) { return 'ios'; }
  if (/iPad/.test(ua)) { return 'ipados'; }
  if (/Android/.test(ua)) { return 'android'; }
  if (/CrOS/.test(ua)) { return 'chromeos'; }
  if (/Macintosh|Mac OS X/.test(ua)) { return Number(maxTouchPoints) > 1 ? 'ipados' : 'macos'; }
  if (/Windows/.test(ua)) { return 'windows'; }
  if (/Linux/.test(ua)) { return 'linux'; }
  return 'unknown';
}

// Installed and opened as an app: display-mode where the browser has it, and iOS's own flag,
// which older iOS sets where display-mode says nothing.
export function detectStandalone({ matchMedia = null, standalone = undefined } = {}) {
  try {
    if (matchMedia?.('(display-mode: standalone)')?.matches) { return true; }
  } catch { /* a browser without the query is not standalone by it */ }
  return standalone === true;
}

export function readPlatform(win = globalThis) {
  const nav = win.navigator ?? {};
  return {
    platform: detectPlatform({ userAgentData: nav.userAgentData, userAgent: nav.userAgent, maxTouchPoints: nav.maxTouchPoints }),
    standalone: detectStandalone({ matchMedia: typeof win.matchMedia === 'function' ? (q) => win.matchMedia(q) : null, standalone: nav.standalone }),
  };
}

let current = null;
// This page's platform, read once.
export function platform() {
  current ??= readPlatform();
  return current;
}

// An app installed on iOS or iPadOS: the one place the page draws under the notch and the home
// indicator, so the app bar, the bottom navigation and the reply box keep out of them (app.css).
export function installedOnApple(info = platform()) {
  return info.standalone && (info.platform === 'ios' || info.platform === 'ipados');
}

// What a hint says for each shortcut: Send's (ui.md §6.5) and search's (§9).
export function shortcutHints(p = platform().platform) {
  return APPLE.has(p)
    ? { send: '\u2318\u21a9', search: '\u2318K', searchKeys: 'Meta+K' }
    : { send: 'Ctrl+Enter', search: 'Ctrl+K', searchKeys: 'Control+K' };
}

// Send: Ctrl+Enter or ⌘↩ on every platform, so a platform taken for another still sends. Enter
// alone is a new line.
export function isSendKey(e) {
  return e.key === 'Enter' && (e.ctrlKey === true || e.metaKey === true);
}

// Search: Ctrl+K or ⌘K on every platform, likewise.
export function isSearchKey(e) {
  return String(e.key ?? '').toLowerCase() === 'k' && (e.ctrlKey === true || e.metaKey === true) && !e.altKey && !e.shiftKey;
}

// Sets <html data-platform data-standalone>, which app.css and tokens.css key on. Everywhere but
// an installed app on iOS or iPadOS the viewport stops asking to draw under the notch and the
// home indicator (viewport-fit=cover, in index.html for iOS from the first paint): there the
// browser keeps the page out of them itself, so no padding is needed for them, and a platform
// taken for another still has its safe areas kept clear by the browser.
export function markPlatform({ root = globalThis.document?.documentElement, doc = globalThis.document, info = platform() } = {}) {
  if (!root) { return info; }
  root.dataset.platform = info.platform;
  root.dataset.standalone = info.standalone ? 'true' : 'false';
  const viewport = doc?.querySelector?.('meta[name="viewport"]');
  if (viewport && !installedOnApple(info)) {
    const parts = viewport.content.split(',').map((s) => s.trim()).filter((s) => s && !/^viewport-fit\s*=/i.test(s));
    viewport.content = parts.join(', ');
  }
  return info;
}

// iOS gives an installed app no back gesture of its own: a swipe in from the left edge goes back,
// as Safari's does, through the same history as Android's Back (the session page's stack: a
// delegate, then its session, then the list). Not from inside something that scrolls sideways (a
// wide code block or table), whose own swipe it is.
export function edgeSwipeBack(target, back, { edge = 20, distance = 64 } = {}) {
  let start = null;
  const begin = (e) => {
    start = null;
    if (e.touches?.length !== 1) { return; }
    const t = e.touches[0];
    if (t.clientX > edge || scrollsSideways(e.target)) { return; }
    start = { x: t.clientX, y: t.clientY, id: t.identifier };
  };
  const end = (e) => {
    const s = start;
    start = null;
    if (!s) { return; }
    const t = Array.from(e.changedTouches ?? []).find((c) => c.identifier === s.id);
    if (!t) { return; }
    const dx = t.clientX - s.x;
    if (dx >= distance && dx > 2 * Math.abs(t.clientY - s.y)) { back(); }
  };
  const cancel = () => { start = null; };
  const opts = { passive: true };
  target.addEventListener('touchstart', begin, opts);
  target.addEventListener('touchend', end, opts);
  target.addEventListener('touchcancel', cancel, opts);
  return () => {
    target.removeEventListener('touchstart', begin, opts);
    target.removeEventListener('touchend', end, opts);
    target.removeEventListener('touchcancel', cancel, opts);
  };
}

function scrollsSideways(el) {
  for (let n = el; n && n.nodeType === 1; n = n.parentElement) {
    const x = globalThis.getComputedStyle?.(n).overflowX;
    if ((x === 'auto' || x === 'scroll') && n.scrollWidth > n.clientWidth) { return true; }
  }
  return false;
}
