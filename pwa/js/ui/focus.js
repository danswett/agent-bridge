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

// Fields keep the browser's own: a ring where typing will go, however focus came there.
const TYPED = new Set(['', 'text', 'search', 'email', 'url', 'tel', 'password', 'number', 'date', 'datetime-local', 'month', 'time', 'week']);

function typed(el) {
  if (el?.isContentEditable || el?.localName === 'textarea') { return true; }
  return el?.localName === 'input' && TYPED.has((el.getAttribute('type') ?? '').toLowerCase());
}

let byKey = false;

// Marks or clears the element now focused, as the last input was a key or not.
function mark(el) {
  if (!el?.dataset) { return; }
  if (byKey || typed(el)) { delete el.dataset.focusQuiet; } else { el.dataset.focusQuiet = ''; }
}

// The app came back from elsewhere (a notification tapped, another app, the background): the
// last key pressed was before, so focus moved now draws no ring until another key is pressed.
export function cameBack() { byKey = false; }

// The mark, not FocusOptions.focusVisible: false, which Chromium honours by leaving the element
// off :focus-visible until focus moves, so a key pressed on a notification's Approve once would
// never show where Enter goes.
export function watchFocusRing(doc = globalThis.document, win = globalThis) {
  doc.addEventListener('keydown', () => {
    byKey = true;
    // A key pressed where a press left focus shows it there, as browsers do.
    mark(doc.activeElement);
  }, true);
  const pressed = () => { byKey = false; };
  doc.addEventListener('pointerdown', pressed, { capture: true, passive: true });
  doc.addEventListener('touchstart', pressed, { capture: true, passive: true });
  doc.addEventListener('visibilitychange', () => { if (doc.visibilityState === 'visible') { cameBack(); } });
  win.addEventListener?.('pageshow', cameBack);
  doc.addEventListener('focusin', (e) => mark(e.target), true);
  doc.addEventListener('focusout', (e) => { if (e.target?.dataset) { delete e.target.dataset.focusQuiet; } }, true);
}
