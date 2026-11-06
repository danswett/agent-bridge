// Whether focus was moved by a key (ui.md §9.1 A12, #373). A ring is drawn only for focus a key
// moved; a press, a touch, or the app coming back (from a notification, or from the background)
// draws none, yet focus still lands, so a screen reader still says where the person is.
//
// The browser's own guess (:focus-visible) isn't enough: focus() from a script with no key
// pressed since the page loaded or came back matches :focus-visible in WebKit and Chromium, so an
// iPhone opening a session from a notification drew a ring around the session's title, and a
// tap's focus() did on iOS too, where a tap focuses no button and so leaves the guess as it was.
// So the one element focused without a key carries data-focus-quiet, which app.css draws no ring
// on, until a key is pressed or focus leaves it. Never a page-wide outline: none.
//
// The mark, not FocusOptions.focusVisible: false, which Chromium honours by leaving the element
// off :focus-visible until focus moves, so a key pressed on a notification's Approve once would
// never show where Enter goes.

// Fields keep the browser's own: a ring where typing will go, however focus came there.
const TYPED = new Set(['', 'text', 'search', 'email', 'url', 'tel', 'password', 'number', 'date', 'datetime-local', 'month', 'time', 'week']);

function typed(el) {
  if (el?.isContentEditable || el?.localName === 'textarea') { return true; }
  return el?.localName === 'input' && TYPED.has((el.getAttribute('type') ?? '').toLowerCase());
}

// What moved focus last: 'key'; 'quiet' (a press, a touch, the app starting or coming back); or
// 'browser', where focus came in from the browser's own address bar or another window with the
// page on screen all along. That one may be the browser's Tab or Shift+Tab, whose key this page
// never sees, so the browser's guess stands.
let moved = 'quiet';
let page = null;
// Whether focus has left the page for the browser or another window, and whether the page has
// been off screen since.
let away = false;
let hidden = false;

// Marks or clears the element now focused, as focus was last moved.
function mark(el) {
  if (!el?.dataset) { return; }
  if (moved === 'quiet' && !typed(el)) { el.dataset.focusQuiet = ''; } else { delete el.dataset.focusQuiet; }
}

// The app came back from elsewhere (a notification tapped, another app, the background): the
// last key pressed was before, so focus draws no ring until another key is pressed; the focus it
// already had included, which a notification for the session already open moves nowhere. The
// window taking focus as it comes back (the worker's client.focus() after its message) is part
// of coming back, not the browser's.
export function cameBack() {
  moved = 'quiet';
  away = false;
  mark(page?.activeElement);
}

export function watchFocusRing(doc = globalThis.document, win = globalThis) {
  page = doc;
  doc.addEventListener('keydown', () => {
    moved = 'key';
    // A key pressed where a press left focus shows it there, as browsers do.
    mark(doc.activeElement);
  }, true);
  let pressedAt = -Infinity;
  // Re-marked whichever came first, a window's focus or the click that gave it: Chromium sends
  // the window's focus, and focus back to what had it, before the click's pointerdown.
  const pressed = () => {
    pressedAt = Date.now();
    if (moved !== 'quiet') { moved = 'quiet'; mark(doc.activeElement); }
  };
  doc.addEventListener('pointerdown', pressed, { capture: true, passive: true });
  doc.addEventListener('touchstart', pressed, { capture: true, passive: true });
  // Focus out of the page and back while it stayed on screen is the browser's. Gone from the
  // screen and back (iOS from a notification, or from another app) is the app coming back.
  away = !doc.hasFocus?.();
  hidden = false;
  win.addEventListener?.('blur', () => { away = true; });
  win.addEventListener?.('focus', () => {
    // A press just before the page takes focus stays a press, as one just after does (pressed).
    if (away && !hidden && doc.visibilityState !== 'hidden' && Date.now() - pressedAt > 500) { moved = 'browser'; }
    away = false;
    hidden = false;
  });
  doc.addEventListener('visibilitychange', () => {
    if (doc.visibilityState === 'visible') { cameBack(); } else { hidden = true; }
  });
  // Back from the back-forward cache; a first load is quiet already, and keeps whether it began
  // with focus in the browser's own bar.
  win.addEventListener?.('pageshow', (e) => { if (e.persisted) { cameBack(); } });
  doc.addEventListener('focusin', (e) => mark(e.target), true);
  doc.addEventListener('focusout', (e) => { if (e.target?.dataset) { delete e.target.dataset.focusQuiet; } }, true);
}
