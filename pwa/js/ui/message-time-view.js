// The time drawn on a conversation message (#1619, ui.md §6.4 "Time on messages"): a button whose
// accessible name is the exact time, whose text is message-time.js's, and which shows the exact
// time in one small popover on hover, focus or tap. The popover floats over the page, so it never
// moves a message.
import { h } from './dom.js';
import { messageTime, exactTime, nextChange, messageInstant } from './message-time.js';

let popover = null;
let openFor = null;
let pinned = false;

function pop() {
  if (!popover) {
    popover = h('div', { class: 'msg-time-pop', 'aria-hidden': 'true', hidden: true });
    document.body.append(popover);
    document.addEventListener('keydown', (e) => { if (e.key === 'Escape') { close(); } });
    document.addEventListener('pointerdown', (e) => { if (openFor && e.target !== openFor && !openFor.contains(e.target)) { close(); } });
  }
  return popover;
}

function open(button, stay) {
  if (openFor && openFor !== button) { close(); }
  const p = pop();
  p.textContent = exactTime(button.dataset.at);
  p.hidden = false;
  const r = button.getBoundingClientRect();
  const w = p.offsetWidth;
  p.style.left = `${Math.max(8, Math.min(r.left, document.documentElement.clientWidth - w - 8))}px`;
  p.style.top = `${r.top - p.offsetHeight - 4 < 8 ? r.bottom + 4 : r.top - p.offsetHeight - 4}px`;
  openFor = button;
  pinned = stay;
}

function close() {
  if (popover) { popover.hidden = true; }
  openFor = null;
  pinned = false;
}

// The button for a message's time, or null when it has none to show.
// The name starts with the words drawn (WCAG 2.5.3; axe label-content-name-mismatch), then the exact time.
function timeName(shown, iso) { return `${shown}, ${exactTime(iso)}`; }

export function timeButton(iso, now = Date.now()) {
  if (messageInstant(iso) === null) { return null; }
  const shown = messageTime(iso, now);
  const b = h('button', { type: 'button', class: 'msg-time', 'aria-label': timeName(shown, iso), dataset: { at: iso } },
    h('time', { datetime: iso }, shown));
  b.addEventListener('pointerenter', (e) => { if (e.pointerType === 'mouse') { open(b, false); } });
  b.addEventListener('pointerleave', () => { if (openFor === b && !pinned) { close(); } });
  b.addEventListener('focus', () => { if (openFor !== b) { open(b, false); } });
  b.addEventListener('blur', () => { if (openFor === b && !pinned) { close(); } });
  b.addEventListener('click', (e) => {
    e.stopPropagation();
    if (openFor === b && pinned) { close(); } else { open(b, true); }
  });
  return b;
}

// The always-there row under a message: empty until a time is known, so one arriving later cannot
// move what is below it (rule 4).
export function timeRow(iso, cls = '') {
  return h('div', { class: `msg-time-row ${cls}`.trim() }, timeButton(iso));
}

// Brings every time under root to its text for `now`; returns the wait before the next could
// change, or null when none will.
export function refreshTimes(root, now = Date.now()) {
  let wait = null;
  // A redraw can drop the button the popover is for, and a removed button gets no leave or blur.
  if (openFor && !openFor.isConnected) { close(); }
  for (const b of root.querySelectorAll('.msg-time[data-at]')) {
    const iso = b.dataset.at;
    const t = b.firstElementChild;
    const text = messageTime(iso, now);
    if (t.textContent !== text) { t.textContent = text; b.setAttribute('aria-label', timeName(text, iso)); }
    const w = nextChange(iso, now);
    if (w !== null && (wait === null || w < wait)) { wait = w; }
  }
  return wait;
}

// Refreshes root's times when they could change and not otherwise, and not while the page is
// hidden; on show it catches up at once. refresh(now) returns the wait for the next one.
export function timeTicker(root, { now = Date.now, setTimer = setTimeout, clearTimer = clearTimeout, doc = globalThis.document } = {}) {
  let timer = null;
  const hidden = () => doc?.visibilityState === 'hidden';
  const run = () => {
    timer = null;
    if (hidden()) { return; }
    const wait = refreshTimes(root, now());
    if (wait !== null) { timer = setTimer(run, wait); }
  };
  const changed = () => { clearTimer(timer); timer = null; run(); };
  doc?.addEventListener?.('visibilitychange', changed);
  return {
    refresh() { clearTimer(timer); timer = null; run(); },
    stop() { clearTimer(timer); timer = null; doc?.removeEventListener?.('visibilitychange', changed); },
  };
}
