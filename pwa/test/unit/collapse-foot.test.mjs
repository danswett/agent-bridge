// A phone's reply area, compact while reading (docs/design/ui.md §6.5, #935): the header's fold
// (§6.2) run on the distance to the conversation's end. The arithmetic, and CollapsingFoot on a
// stand-in for the page; the page itself is in test/compact-reply.spec.mjs.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { Fold } from '../../js/ui/collapse-header.js';
import { toEnd, footParts, copyOf, footCovers, resync, CollapsingFoot } from '../../js/ui/collapse-foot.js';

const ROOM = 25; // the reply area at rest (73) less the 48 bar
const MAX = 2000;

// The fold scrolled so the conversation is `e` from its end, as one scroll event.
const to = (f, e, extra = {}) => f.scroll({ top: e, max: MAX, room: ROOM, ...extra });
const at = (e) => { const f = new Fold(); f.sync(e, MAX); return f; };
const hidden = (f, extra = {}) => f.hidden({ room: ROOM, ...extra });

test('its "top" is how far the conversation is from its end, clamped', () => {
  assert.equal(toEnd(MAX, MAX), 0);
  assert.equal(toEnd(MAX - 41, MAX), 41);
  assert.equal(toEnd(MAX + 60, MAX), 0);
  assert.equal(toEnd(-10, MAX), MAX);
  assert.equal(toEnd(0, -5), 0);
});

test('near the end it rides down with the scroll, to its bar and no further', () => {
  const f = new Fold();
  for (const e of [0, 1, 10, 24, 25, 26, 400]) {
    to(f, e);
    assert.equal(hidden(f), Math.min(e, ROOM), `at ${e} from the end`);
  }
});

test('away from the end, 32 up sends it away and 64 down brings it back', () => {
  const f = at(400);
  f.returned = true;
  assert.equal(to(f, 431).action, null);
  assert.equal(hidden(f), 0);
  assert.equal(to(f, 432).action, 'hide');
  assert.equal(hidden(f), ROOM);
  assert.equal(to(f, 369).action, null);
  assert.equal(hidden(f), ROOM);
  assert.equal(to(f, 368).action, 'show');
  assert.equal(hidden(f), 0);
});

test('the end always shows it in full, however it got there', () => {
  const f = at(400);
  to(f, 0);
  assert.equal(hidden(f), 0);
  assert.equal(f.returned, false);
  const g = at(400);
  g.held = 12;
  to(g, 0);
  assert.equal(hidden(g), 0);
});

test('its own count: reading up brings the header back after 64 while the reply area goes after 32', () => {
  const head = new Fold();
  head.sync(3000, 5000);
  const foot = new Fold();
  foot.sync(2000, 5000);
  const e = (top) => toEnd(top, 5000);
  const step = (top) => [head.scroll({ top, max: 5000, room: 209 }).action, foot.scroll({ top: e(top), max: 5000, room: ROOM }).action];
  foot.returned = true;
  assert.deepEqual(step(2968), [null, 'hide']);
  assert.deepEqual(step(2936), ['show', null]);
  // Back down: 32 sends the header away, the reply area needs 64.
  assert.deepEqual(step(2968), ['hide', null]);
  assert.deepEqual(step(3000), [null, 'show']);
});

test('it never expands over the focused control: it waits, still counting', () => {
  const f = at(400);
  to(f, 336, { coversFocus: true });
  assert.equal(hidden(f), ROOM);
  assert.equal(to(f, 300, { coversFocus: true }).action, null);
  assert.equal(to(f, 299).action, 'show');
});

test('focus in the reply area holds it whole', () => {
  const f = at(400);
  assert.equal(hidden(f, { focusInside: true }), 0);
  to(f, 900, { focusInside: true });
  assert.equal(hidden(f, { focusInside: true }), 0);
});

test('its parts: the fill and the copy come in at its room when that is under the title point', () => {
  assert.deepEqual(footParts(0, ROOM, 0), { solid: false, state: 'rest' });
  assert.deepEqual(footParts(0, ROOM, 300), { solid: false, state: 'returned' });
  assert.deepEqual(footParts(10, ROOM, 10), { solid: false, state: 'riding' });
  assert.deepEqual(footParts(ROOM, ROOM, 400), { solid: true, state: 'compact' });
  // A box grown taller has more room: the title point again.
  assert.deepEqual(footParts(39, 99, 39), { solid: false, state: 'riding' });
  assert.deepEqual(footParts(40, 99, 40), { solid: true, state: 'riding' });
});

test('the copy is the draft\u2019s first line, or the placeholder', () => {
  assert.deepEqual(copyOf('Ship it\nand tell the team', 'Reply'), { text: 'Ship it', draft: true });
  assert.deepEqual(copyOf('\n\r\n   first words  \nmore', 'Reply'), { text: 'first words', draft: true });
  assert.deepEqual(copyOf('', 'Reply'), { text: 'Reply', draft: false });
  assert.deepEqual(copyOf('  \n\t\n', 'Reply'), { text: 'Reply', draft: false });
  assert.deepEqual(copyOf(undefined, undefined), { text: '', draft: false });
});

test('what the whole reply area would cover', () => {
  assert.equal(footCovers({ top: 700, bottom: 744 }, { bottom: 800, footH: 0 }), false);
  assert.equal(footCovers({ top: 700, bottom: 744 }, { bottom: 800, footH: 73 }), true);
  assert.equal(footCovers({ top: 680, bottom: 727 }, { bottom: 800, footH: 73 }), false);
  assert.equal(footCovers({ top: 810, bottom: 850 }, { bottom: 800, footH: 73 }), false);
});

test('content growing below is not 32 up: the reply area stays as it was', () => {
  // Whole at the end, words arrive: still whole.
  const f = at(0);
  resync(f, 300, MAX, ROOM);
  assert.equal(hidden(f), 0);
  // Riding, the box grows: it holds where it was.
  const g = new Fold();
  to(g, 10);
  resync(g, 60, MAX, ROOM);
  assert.equal(hidden(g), 10);
  // Compact stays compact.
  const c = at(400);
  resync(c, 700, MAX, ROOM);
  assert.equal(hidden(c), ROOM);
  // And landing at the end shows it whole.
  const d = at(400);
  resync(d, 0, MAX, ROOM);
  assert.equal(hidden(d), 0);
});

// CollapsingFoot on a stand-in page: the conversation, the reply area, its bar and copy.
function page({ top = MAX, slotH = 73, reduced = false, value = '' } = {}) {
  const on = {};
  const doc = { activeElement: null };
  const props = new Map();
  const attrs = new Set();
  const classes = new Set();
  const listen = (name) => (type, fn) => { on[`${name}:${type}`] = fn; };
  const textarea = {
    value, placeholder: 'Reply to the session', disabled: false, getClientRects: () => [{}],
    focus() { doc.activeElement = textarea; on['slot:focusin']?.({}); },
  };
  const slot = { offsetHeight: slotH, contains: (el) => el === textarea, querySelector: () => textarea, querySelectorAll: () => [textarea], addEventListener: listen('slot') };
  const span = { textContent: '' };
  const copyClasses = new Set();
  const copy = { firstElementChild: span, classList: { toggle: (c, yes) => (yes ? copyClasses.add(c) : copyClasses.delete(c)) }, addEventListener: listen('copy') };
  const view = {
    ownerDocument: doc,
    dataset: {},
    style: { setProperty: (k, v) => props.set(k, v), removeProperty: (k) => props.delete(k) },
    classList: { add: (c) => classes.add(c), remove: (...c) => c.forEach((x) => classes.delete(x)), contains: (c) => classes.has(c) },
    toggleAttribute: (a, yes) => (yes ? attrs.add(a) : attrs.delete(a)),
    removeAttribute: (a) => { attrs.delete(a); delete view.dataset[a.replace(/^data-/, '')]; },
  };
  const scroller = { scrollTop: top, scrollHeight: MAX + 700, clientHeight: 700, contains: () => false, getBoundingClientRect: () => ({ top: 0, bottom: 700 }) };
  const win = { matchMedia: () => ({ matches: reduced }) };
  const foot = new CollapsingFoot({ view, slot, bar: { offsetHeight: 48 }, copy, scroller, win });
  const ctl = new AbortController();
  foot.enable(ctl.signal);
  foot.measure(false);
  foot.render(false);
  const fy = () => parseFloat(props.get('--fy'));
  // The person scrolls to `y` from the top.
  const scroll = (y) => { scroller.scrollTop = y; foot.scroll(); foot.render(false); };
  return { foot, fy, scroll, scroller, textarea, span, copyClasses, classes, props, attrs, view, on, doc };
}

test('a tap on the copy brings the box back in full and focuses it; focus in it brings it back at once', () => {
  const p = page();
  p.scroll(MAX - 400);
  assert.equal(p.fy(), ROOM);
  assert.equal(p.span.textContent, 'Reply to the session');
  p.on['copy:click']();
  assert.equal(p.doc.activeElement, p.textarea);
  assert.equal(p.fy(), 0);
  assert.equal(p.classes.has('fanimating'), false);
  // Held while focus stays: reading on up leaves it whole.
  p.scroll(MAX - 900);
  assert.equal(p.fy(), 0);
  p.foot.disable();
});

test('a draft is never hidden: the copy follows its first line', () => {
  const p = page({ value: 'Ship it\nand tell the team', slotH: 147 });
  p.scroll(MAX - 400);
  assert.equal(p.fy(), 99);
  assert.equal(p.span.textContent, 'Ship it');
  assert.equal(p.copyClasses.has('draft'), true);
  p.textarea.value = '';
  p.foot.changed();
  assert.equal(p.span.textContent, 'Reply to the session');
  assert.equal(p.copyClasses.has('draft'), false);
  p.foot.disable();
});

test('a scroll the app makes keeps it as it was, unless it lands at the end', () => {
  const p = page();
  p.scroll(MAX - 400);
  p.foot.hold(false);
  p.scroller.scrollTop = MAX - 1200;
  p.foot.sync();
  p.foot.release();
  p.foot.render(false);
  assert.equal(p.fy(), ROOM);
  p.foot.hold(false);
  p.scroller.scrollTop = MAX;
  p.foot.sync();
  p.foot.release();
  p.foot.render(false);
  assert.equal(p.fy(), 0);
  // Whole, it stays whole however far the app takes it.
  p.scroll(MAX - 300);
  p.foot.whole();
  p.foot.hold(false);
  p.scroller.scrollTop = 100;
  p.foot.sync();
  p.foot.release();
  p.foot.render(false);
  assert.equal(p.fy(), 0);
  p.foot.disable();
});

test('on a screen shorter than twice the header it folds with the header, unless focus is in it', () => {
  const p = page({ top: 600 });
  assert.equal(p.fy(), 0);
  p.foot.hold(true);
  p.foot.render(false);
  assert.equal(p.fy(), ROOM);
  p.foot.release();
  p.foot.disable();
  const q = page({ top: 600 });
  q.textarea.focus();
  q.foot.hold(true);
  q.foot.render(false);
  assert.equal(q.fy(), 0);
  q.foot.disable();
});

test('under reduced motion it leaves with a cross-fade, not a slide', async () => {
  const p = page({ reduced: true, top: 600 });
  p.scroll(568);
  assert.equal(p.classes.has('ffading'), true);
  assert.equal(p.fy(), 0);
  await new Promise((r) => setTimeout(r, 400));
  assert.equal(p.classes.has('ffading'), false);
  assert.equal(p.fy(), ROOM);
  p.foot.disable();
});

test('disabled, it leaves nothing behind', () => {
  const p = page();
  p.scroll(MAX - 400);
  p.foot.disable();
  assert.equal(p.props.size, 0);
  assert.equal(p.attrs.size, 0);
  assert.equal(p.classes.size, 0);
  assert.equal(p.view.dataset.foot, undefined);
  assert.equal(p.foot.covered(), 0);
});
