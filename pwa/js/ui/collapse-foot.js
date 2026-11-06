// A phone's reply area, compact while reading (docs/design/ui.md §6.5, #935): the header's
// collapse (collapse-header.js, §6.2) mirrored at the conversation's foot.
//
// The reply area floats over the conversation's foot as the header floats over its top. The same
// Fold runs it, on the distance to the end rather than from the top: near the end it rides down
// with the scroll, and past its room it is compact, a pinned 48 bar holding a drawn one-line copy
// of the box; away from the end, 32 up sends it away and 64 down brings it back, with its own
// count; the end always shows it in full. CollapsingHeader drives it, so the two follow one rule:
// the same scroll, the same app-made scrolls, the same pause on other tabs, and the same fold on a
// screen shorter than twice the header. From 760 it holds nothing and leaves no style behind.
//
// The pure half (unit tests: test/unit/collapse-foot.test.mjs) is above CollapsingFoot.

import { BAR, TITLE_IN, Fold, clamp } from './collapse-header.js';
import { timing } from './motion.js';

// How far the conversation is from its end: the foot's "top".
export const toEnd = (top, max) => Math.max(max, 0) - clamp(top, max);

// The foot's parts for how much of it is hidden (fy). Its room is often less than the header's
// title point (73 less the 48 bar, at rest), so the bar's fill and copy come in at that point or
// once it is compact, whichever comes first.
export function footParts(fy, room, end) {
  const solid = fy > 0 && fy >= Math.min(TITLE_IN, room);
  const state = fy === 0 ? (end > 0 ? 'returned' : 'rest') : fy >= room ? 'compact' : 'riding';
  return { solid, state };
}

// The compact bar's copy of the box: the draft's first line, or the placeholder. A draft is
// never hidden (ui.md §6.5).
export function copyOf(value, placeholder) {
  const line = String(value ?? '').split(/\r\n|[\n\r\u2028\u2029]/).map((l) => l.trim()).find(Boolean) ?? '';
  return line ? { text: line, draft: true } : { text: placeholder ?? '', draft: false };
}

// The focused control in the conversation that the whole reply area would cover.
export function footCovers(rect, { bottom, footH }) {
  return footH > 0 && rect.bottom > bottom - footH && rect.top < bottom;
}

// What a scroll of the conversation's own content, not of the person, leaves: the foot as it was.
// The end moving away (words arriving below, the box growing) is not 32 up.
export function resync(fold, end, max, room) {
  const before = fold.hidden({ room });
  fold.sync(end, max);
  if (fold.last <= 0) {
    fold.returned = false;
    fold.held = null;
  } else if (before === 0) {
    fold.returned = true;
    fold.held = null;
  } else if (before < room) {
    fold.returned = false;
    fold.held = before;
  }
}

const REDUCED = '(prefers-reduced-motion: reduce)';
const FOCUSABLE = 'textarea, input, button, select, a[href], [tabindex]:not([tabindex="-1"])';

export class CollapsingFoot {
  #view; #slot; #bar; #copy; #scroller; #win; #doc; #reduced;
  #fold = new Fold();
  #on = false;
  #focusInside = false;
  #paused = false;
  #fading = false;
  #footH = 0;
  #barH = BAR;
  #room = 0;
  #wasFull = null; // while the app is scrolling: whether it was whole
  #timers = new Set();
  #shown = {};

  // view: the session's view, which carries the properties the CSS reads. slot: the reply area.
  // bar: the pinned bar, and copy, the drawn copy of the box it holds. scroller: the conversation.
  constructor({ view, slot, bar, copy, scroller, win = globalThis.window }) {
    this.#view = view;
    this.#slot = slot;
    this.#bar = bar;
    this.#copy = copy;
    this.#scroller = scroller;
    this.#win = win;
    this.#doc = view.ownerDocument;
    this.#reduced = win.matchMedia(REDUCED);
  }

  get slot() { return this.#slot; }

  // signal: the header's listeners', so the foot's go with them.
  enable(signal) {
    this.#on = true;
    const on = (target, type, fn) => target.addEventListener(type, fn, { signal });
    on(this.#slot, 'focusin', () => this.#focusIn());
    on(this.#slot, 'focusout', (e) => this.#focusOut(e));
    on(this.#slot, 'input', () => this.#drawCopy());
    on(this.#copy, 'click', () => this.#bringBack());
    // Focus already in the reply area (a window narrowed to a phone's) holds it, as focus coming in does.
    this.#focusInside = this.#slot.contains(this.#doc.activeElement);
  }

  disable() {
    this.#on = false;
    for (const t of this.#timers) { clearTimeout(t); }
    this.#timers.clear();
    this.#fold = new Fold();
    this.#focusInside = false;
    this.#paused = false;
    this.#fading = false;
    this.#wasFull = null;
    this.#shown = {};
    const v = this.#view;
    for (const p of ['--fy', '--fcovered', '--foot-h']) { v.style.removeProperty(p); }
    for (const a of ['data-fsolid', 'data-foot']) { v.removeAttribute(a); }
    v.classList.remove('fanimating', 'ffading');
  }

  // What the reply area covers of the conversation now, in px: its bar, or the whole of it.
  covered() {
    if (!this.#on || this.#footH === 0) { return 0; }
    return Math.max(this.#barH, this.#footH - this.#hidden());
  }

  #hidden() {
    if (this.#footH === 0) { return 0; }
    return Math.min(this.#room, this.#fold.hidden({ room: this.#room, focusInside: this.#focusInside }));
  }

  #end() {
    const sc = this.#scroller;
    return { top: sc.scrollTop, max: sc.scrollHeight - sc.clientHeight };
  }

  // Shown again, or back from another tab: whole, as a session opens (ui.md §6.5).
  whole() {
    const f = this.#fold;
    const { top, max } = this.#end();
    f.sync(toEnd(top, max), max);
    f.returned = f.last > 0;
    f.held = null;
    f.dir = 0;
    f.run = 0;
  }

  // Its size and the conversation's, measured; app: whether the app is scrolling.
  measure(app) {
    if (!this.#on) { return; }
    this.#footH = this.#slot.offsetHeight;
    this.#barH = Math.max(BAR, this.#bar.offsetHeight);
    this.#room = this.#footH > 0 ? Math.max(0, this.#footH - this.#barH) : 0;
    this.#set('--foot-h', `${this.#footH}px`);
    const { top, max } = this.#end();
    if (app) { this.#fold.sync(toEnd(top, max), max); } else { resync(this.#fold, toEnd(top, max), max, this.#room); }
  }

  // A scroll the app makes: the foot keeps up with it and stays as it is.
  sync() {
    const { top, max } = this.#end();
    this.#fold.sync(toEnd(top, max), max);
  }

  // The person scrolled the conversation.
  scroll() {
    if (!this.#on) { return; }
    const { top, max } = this.#end();
    const r = this.#fold.scroll({
      top: toEnd(top, max),
      max,
      room: this.#room,
      focusInside: this.#focusInside,
      fading: this.#fading,
      coversFocus: this.#coversFocus(),
    });
    if (r.cancelFade) { this.#endFade(); }
    if (r.stopSlide) { this.#view.classList.remove('fanimating'); }
    if (r.action === 'hide') { this.#go(true); } else if (r.action === 'show') { this.#go(false); }
  }

  // An app-made scroll is about to start: the foot stays as it is, or, on a screen shorter than
  // twice the header, folds to its bar with the header.
  hold(fold) {
    if (!this.#on) { return; }
    const f = this.#fold;
    if (this.#fading) { this.#endFade(); }
    if (fold && !this.#focusInside && this.#room > 0) {
      this.#view.classList.remove('fanimating');
      f.returned = false;
      f.held = this.#room;
      f.run = 0;
    }
    this.#wasFull = f.hold(this.#hidden());
  }

  // It is over: as it was, unless it landed at the end, where the foot is whole.
  release() {
    if (!this.#on || this.#wasFull === null) { return; }
    const wasFull = this.#wasFull;
    this.#wasFull = null;
    this.sync();
    const hidden = this.#hidden();
    this.#fold.release(wasFull);
    if (hidden > 0 && this.#fold.last <= 0) { this.#animate(); }
  }

  render(paused = this.#paused) {
    if (!this.#on) { return; }
    this.#paused = paused;
    const v = this.#view;
    const fy = paused ? 0 : this.#hidden();
    const p = footParts(fy, this.#room, paused ? 0 : this.#fold.last);
    this.#set('--fy', `${fy}px`);
    this.#set('--fcovered', `${this.#footH > 0 ? Math.max(this.#barH, this.#footH - fy) : 0}px`);
    if (this.#shown.solid !== p.solid) { this.#shown.solid = p.solid; v.toggleAttribute('data-fsolid', p.solid); }
    if (this.#shown.state !== p.state) { this.#shown.state = p.state; v.dataset.foot = p.state; }
    if (p.solid) { this.#drawCopy(); }
  }

  // The box's words changed (typed, put in, or sent).
  changed() { if (this.#on) { this.#drawCopy(); } }

  #set(name, value) {
    if (this.#shown[name] !== value) { this.#shown[name] = value; this.#view.style.setProperty(name, value); }
  }

  #drawCopy() {
    const input = this.#slot.querySelector('textarea');
    const c = copyOf(input?.value, input?.placeholder);
    const span = this.#copy.firstElementChild ?? this.#copy;
    if (span.textContent !== c.text) { span.textContent = c.text; }
    this.#copy.classList.toggle('draft', c.draft);
  }

  #coversFocus() {
    const a = this.#doc.activeElement;
    if (!a || a === this.#scroller || !this.#scroller.contains(a)) { return false; }
    return footCovers(a.getBoundingClientRect(), { bottom: this.#scroller.getBoundingClientRect().bottom, footH: this.#footH });
  }

  // Going or coming back with the collapse motion, or, under reduced motion, a cross-fade with no
  // travel. Near the end it rides the scroll and has no motion of its own.
  #go(hiding) {
    if (hiding && this.#fold.last <= this.#room) { return; }
    if (this.#fading) { this.#endFade(); }
    if (hiding && this.#reduced.matches) {
      this.#fold.returned = true;
      this.#fading = true;
      this.#view.classList.add('ffading');
      this.#later(() => {
        if (!this.#fading) { return; }
        this.#endFade();
        this.#fold.returned = false;
        this.render();
      }, timing('collapse').duration);
      return;
    }
    this.#animate();
  }

  #animate() {
    const cl = this.#view.classList;
    cl.add('fanimating');
    this.#later(() => cl.remove('fanimating'), timing('collapse').duration + 60);
  }

  #endFade() {
    this.#fading = false;
    this.#view.classList.remove('ffading');
  }

  #later(fn, ms) {
    const t = setTimeout(() => { this.#timers.delete(t); fn(); }, ms);
    this.#timers.add(t);
    return t;
  }

  // A tap on the bar's copy: the box, in full and focused. The words while they take any; else the
  // notice's link, or the first control that takes focus.
  #bringBack() {
    const usable = (el) => !el.disabled && el.getClientRects().length > 0;
    const input = this.#slot.querySelector('textarea');
    const target = input && usable(input) ? input : [...this.#slot.querySelectorAll(FOCUSABLE)].find(usable);
    target?.focus({ preventScroll: true });
  }

  // Focus anywhere in the reply area brings it back at once and holds it (ui.md §6.5): focus is
  // never on a hidden control.
  #focusIn() {
    if (this.#fading) { this.#endFade(); }
    this.#focusInside = true;
    this.#fold.returned = this.#fold.last > 0;
    this.#fold.held = null;
    this.#view.classList.remove('fanimating');
    this.render();
  }

  #focusOut(e) {
    if (e.relatedTarget && this.#slot.contains(e.relatedTarget)) { return; }
    this.#focusInside = false;
    this.render();
  }
}
