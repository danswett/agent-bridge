// The phone session header's collapse (docs/design/ui.md §6.2 and the Collapse motion of §5.3).
//
// Two halves. The first is pure: the thresholds, the hysteresis, what the person's scroll does
// and what an app-made one leaves alone (unit tests: test/unit/collapse-header.test.mjs). The
// second, CollapsingHeader, is the DOM: it reads the scroll, sets the header's offset, and exists
// only below 760 px; from there up it holds no listener and leaves no style behind.
//
// The header floats over the conversation's top, so collapsing never moves the conversation, and
// the question guard (§5.3) never sees it move.

import { timing } from './motion.js';

export const BAR = 48; // the pinned bar's least height
export const DOWN = 32; // scrolled down this far in one direction, mid-conversation, it goes
export const UP = 64; // scrolled up this far in one direction, it comes back
export const TITLE_IN = 40; // the bar's fill and title appear once this much of the header is under it

export const PHONE = '(max-width: 759px)';
const REDUCED = '(prefers-reduced-motion: reduce)';
const QUIET_MS = 120; // an app-made scroll is over once its scroll events have been quiet this long
const APP_SCROLL_MAX_MS = 2000;
const PAN_AXIS_PX = 6;

export const clamp = (top, max) => Math.min(Math.max(top, 0), Math.max(max, 0));

export class Fold {
  last = 0; // the scroll position last seen, clamped
  dir = 0;
  run = 0; // how far the person has scrolled in the current direction
  returned = false; // the whole header is showing over the conversation
  held = null; // how much is hidden while an app-made scroll leaves it as it was

  sync(top, max) { this.last = clamp(top, max); }

  set(returned) {
    if (this.returned === returned) { return; }
    this.returned = returned;
    this.run = 0;
    this.held = null;
  }

  // The person scrolled to `top`. room: the header's height less the bar.
  scroll({ top, max, room, focusInside = false, fading = false, coversFocus = false }) {
    const out = { action: null, cancelFade: false, stopSlide: false };
    const s = clamp(top, max);
    const from = this.last;
    const d = s - from;
    this.last = s;
    if (d === 0) { return out; }
    const dir = d > 0 ? 1 : -1;
    if (dir !== this.dir) { this.dir = dir; this.run = 0; }
    this.run += Math.abs(d);
    if (this.held !== null) {
      if (d > 0) {
        this.held = Math.min(room, this.held + d);
        if (this.held >= room) { this.held = null; }
      } else if (s <= this.held) {
        this.held = null;
      }
    }
    if (from > room && s <= room && (!this.returned || fading)) {
      out.stopSlide = true;
      if (fading) {
        this.returned = false;
        out.cancelFade = true;
      }
    }
    if (s <= 0) {
      this.returned = false;
      this.run = 0;
      this.held = null;
    } else if (focusInside && !this.returned) {
      this.returned = true;
    } else if (this.returned && dir > 0 && this.run >= DOWN && !focusInside && !fading) {
      this.set(false);
      out.action = 'hide';
    } else if ((!this.returned || fading) && dir < 0 && this.run >= UP && s > room && !coversFocus) {
      this.set(true);
      out.action = 'show';
    }
    return out;
  }

  // An app-made scroll is about to start: the header stays as it is. hidden: how much is hidden now.
  hold(hidden) {
    const wasFull = hidden === 0;
    this.returned = wasFull;
    this.held = wasFull ? null : hidden;
    return wasFull;
  }

  // It is over: the header stays as it was, and the person carries on from there.
  release(wasFull) {
    this.returned = wasFull && this.last > 0;
    this.dir = 0;
    this.run = 0;
    if (this.last <= 0) { this.held = null; }
  }

  // How much of the header is under the bar: near the top it rides the scroll 1:1, to the bar.
  hidden({ room, focusInside = false }) {
    if (this.returned || focusInside) { return 0; }
    if (this.held !== null) { return this.held; }
    return Math.min(this.last, room);
  }
}

export function parts(hidden, room, scrolled = false) {
  const shown = hidden >= TITLE_IN;
  const state = hidden === 0 ? (scrolled ? 'returned' : 'rest') : hidden >= room ? 'compact' : 'riding';
  return { solid: shown, titled: shown, state };
}

// A screen shorter than twice the header: a scroll to a question folds it to the bar first.
export function landscapeFold({ stageH, fullH, hidden, room }) {
  return hidden < room && stageH - Math.max(BAR, fullH - hidden) < stageH / 2;
}

// Focused content the header covers, or that is off the conversation's area.
export function focusHidden(rect, { stage, covered }) {
  return rect.top < stage.top + covered || rect.bottom > stage.bottom;
}

export class CollapsingHeader {
  #view; #head; #top; #bar; #scroller; #stream; #win; #doc; #foot;
  #phone; #reduced;
  #fold = new Fold();
  #on = false;
  #paused = false;
  #focusInside = false;
  #fading = false;
  #again = false; // shown again (shown()): whole at the first measure that finds it drawn
  #againFrame = 0;
  #app = null; // { wasFull, quiet, limit } while the app is scrolling
  #fullH = 0;
  #barH = BAR;
  #room = 0;
  #stageH = 0;
  #listeners = null;
  #observer = null;
  #timers = new Set();
  #frame = 0;
  #pan = null;
  #shown = {};

  // view: the session's view, which carries the properties the CSS reads. head: the floating header.
  // top: what slides under the bar (title, meta, tabs). bar: the pinned bar. scroller: the conversation.
  // foot: the reply area's CollapsingFoot (collapse-foot.js), which mirrors it at the conversation's
  // foot, or null.
  constructor({ view, head, top, bar, scroller, foot = null, win = globalThis.window }) {
    this.#view = view;
    this.#foot = foot;
    this.#head = head;
    this.#top = top;
    this.#bar = bar;
    this.#scroller = scroller;
    this.#stream = scroller.firstElementChild;
    this.#win = win;
    this.#doc = view.ownerDocument;
    this.#phone = win.matchMedia(PHONE);
    this.#reduced = win.matchMedia(REDUCED);
    this.#phone.addEventListener('change', this.#onMedia);
    this.#onMedia();
  }

  #onMedia = () => {
    if (this.#phone.matches && !this.#on) { this.#enable(); }
    if (!this.#phone.matches && this.#on) { this.#disable(); }
  };

  get enabled() { return this.#on; }

  // What the header covers of the conversation now, in px: the bar, or the whole header.
  covered() { return this.#on && !this.#paused ? this.#covered() : this.#on ? this.#fullH : 0; }

  #covered() { return Math.max(this.#barH, this.#fullH - this.#hidden()); }

  // What the reply area covers of the conversation's foot now, in px.
  footCovered() { return this.#on && !this.#paused ? this.#foot?.covered() ?? 0 : 0; }

  #hidden() {
    if (this.#paused) { return 0; }
    return Math.min(this.#room, this.#fold.hidden({ room: this.#room, focusInside: this.#focusInside }));
  }

  #max() { return this.#scroller.scrollHeight - this.#scroller.clientHeight; }

  #enable() {
    this.#on = true;
    const ac = new AbortController();
    const { signal } = ac;
    this.#listeners = ac;
    const sc = this.#scroller;
    const on = (target, type, fn, opts = {}) => target.addEventListener(type, fn, { signal, ...opts });
    on(sc, 'scroll', () => this.#onScroll(), { passive: true });
    on(sc, 'scrollend', () => this.#endApp());
    for (const type of ['wheel', 'touchstart', 'pointerdown', 'keydown']) { on(sc, type, () => this.#endApp(), { passive: true }); }
    // A person's touch or wheel on the conversation is done with the header: focus the app put there
    // when the session opened (the title) must not keep it whole.
    for (const type of ['wheel', 'touchstart', 'pointerdown']) { on(sc, type, () => this.#releaseHold(), { passive: true }); }
    on(sc, 'focusin', () => this.#checkFocusSoon());
    on(this.#head, 'focusin', () => this.#focusIn());
    on(this.#head, 'focusout', (e) => this.#focusOut(e));
    on(this.#bar.querySelector('.bar-title'), 'click', () => this.#bringBack());
    on(this.#top, 'wheel', (e) => this.#wheel(e), { passive: false });
    on(this.#top, 'touchstart', (e) => this.#touchStart(e), { passive: true });
    on(this.#top, 'touchmove', (e) => this.#touchMove(e), { passive: false });
    for (const end of ['touchend', 'touchcancel']) { on(this.#top, end, () => { this.#pan = null; }, { passive: true }); }
    if (typeof this.#win.ResizeObserver === 'function') {
      this.#observer = new this.#win.ResizeObserver(() => this.#measure());
      // The header's border box: its own padding growing moves what it covers as much as its content.
      for (const el of [this.#top, this.#bar, this.#view, sc, this.#stream]) { if (el) { this.#observer.observe(el, el === this.#top ? { box: 'border-box' } : undefined); } }
      if (this.#foot) { this.#observer.observe(this.#foot.slot, { box: 'border-box' }); }
    }
    this.#foot?.enable(signal);
    // Focus already in the header (a window narrowed to a phone's) holds it, as focus coming in does.
    this.#focusInside = this.#head.contains(this.#doc.activeElement);
    this.#measure();
  }

  #disable() {
    this.#on = false;
    this.#listeners?.abort();
    this.#listeners = null;
    this.#observer?.disconnect();
    this.#observer = null;
    for (const t of this.#timers) { clearTimeout(t); }
    this.#timers.clear();
    this.#win.cancelAnimationFrame(this.#frame);
    this.#win.cancelAnimationFrame(this.#againFrame);
    this.#app = null;
    this.#pan = null;
    this.#fading = false;
    this.#again = false;
    this.#focusInside = false;
    this.#fold = new Fold();
    this.#shown = {};
    this.#foot?.disable();
    const v = this.#view;
    for (const p of ['--ty', '--covered', '--head-h', '--stage-h']) { v.style.removeProperty(p); }
    for (const a of ['data-state', 'data-solid', 'data-titled']) { v.removeAttribute(a); }
    v.classList.remove('animating', 'fading');
    this.#stageH = 0;
  }

  destroy() {
    this.#phone.removeEventListener('change', this.#onMedia);
    if (this.#on) { this.#disable(); }
  }

  // On a tab other than the conversation the header is whole and stays so.
  setPaused(paused) {
    if (paused === this.#paused) { return; }
    this.#paused = paused;
    if (!this.#on) { return; }
    if (!paused) { this.#whole(); }
    this.#render();
  }

  // The session shown again (a row chosen, the browser's Back or Forward, Sessions back from another
  // page), as its owner says: the header whole, as it is back from another tab, and the
  // conversation where it was left. Said, not inferred from a hidden header a resize frame
  // measured: a quick Back and Forward can fall between two frames. Its pane may only be drawn
  // later in the same task, so a frame later too: by then it is laid out, or it was measured
  // hidden and the observer measures it as it is drawn.
  shown() {
    if (!this.#on) { return; }
    // A reduced-motion fade out still running would fold the whole header again when it ends
    // (#941 review: Back and Forward during the fade left it folded).
    if (this.#fading) { this.#endFade(); }
    this.#again = true;
    this.#measure();
    if (!this.#again) { return; }
    this.#win.cancelAnimationFrame(this.#againFrame);
    this.#againFrame = this.#win.requestAnimationFrame(() => this.#measure());
  }

  #whole() {
    if (this.#fading) { this.#endFade(); }
    const f = this.#fold;
    f.sync(this.#scroller.scrollTop, this.#max());
    f.returned = f.last > 0;
    f.held = null;
    f.dir = 0;
    f.run = 0;
    this.#foot?.whole();
  }

  #measure() {
    if (!this.#on) { return; }
    const v = this.#view;
    if (this.#top.offsetHeight === 0) { return; }
    if (this.#again) {
      this.#again = false;
      if (!this.#paused) { this.#whole(); }
    }
    // On another tab the conversation is not drawn and its scroller has no height; the header still
    // floats over that tab, whose top padding is --head-h, so the header alone is measured.
    const stageH = this.#scroller.clientHeight;
    if (stageH === 0) {
      this.#fullH = this.#top.offsetHeight;
      this.#barH = Math.max(BAR, this.#bar.offsetHeight);
      this.#room = Math.max(0, this.#fullH - this.#barH);
      v.style.setProperty('--head-h', `${this.#fullH}px`);
      this.#render();
      return;
    }
    if (stageH !== this.#stageH) {
      this.#stageH = stageH;
      v.style.setProperty('--stage-h', `${stageH}px`);
    }
    this.#fullH = this.#top.offsetHeight;
    this.#barH = Math.max(BAR, this.#bar.offsetHeight);
    this.#room = Math.max(0, this.#fullH - this.#barH);
    v.style.setProperty('--head-h', `${this.#fullH}px`);
    // The padding moved the content, and so may content that grew or shrank: not scrolling.
    if (!this.#app) { this.#fold.sync(this.#scroller.scrollTop, this.#max()); }
    this.#foot?.measure(!!this.#app);
    this.#render();
    this.#checkFocus();
  }

  #render() {
    if (!this.#on) { return; }
    const v = this.#view;
    const hidden = this.#hidden();
    const p = parts(hidden, this.#room, this.#fold.last > 0 && !this.#paused);
    const set = (name, value) => {
      if (this.#shown[name] !== value) { this.#shown[name] = value; v.style.setProperty(name, value); }
    };
    set('--ty', `${-hidden}px`);
    set('--covered', `${Math.max(this.#barH, this.#fullH - hidden)}px`);
    const flag = (name, on) => { if (this.#shown[name] !== on) { this.#shown[name] = on; v.toggleAttribute(name, on); } };
    flag('data-solid', p.solid);
    flag('data-titled', p.titled);
    if (this.#shown.state !== p.state) { this.#shown.state = p.state; v.dataset.state = p.state; }
    this.#foot?.render(this.#paused);
  }

  #coversFocus() {
    const a = this.#doc.activeElement;
    if (!a || a === this.#scroller || !this.#scroller.contains(a)) { return false; }
    const r = a.getBoundingClientRect();
    const s = this.#scroller.getBoundingClientRect();
    return r.bottom > s.top && r.top < s.top + this.#fullH;
  }

  #onScroll() {
    if (!this.#on || this.#paused) { return; }
    const sc = this.#scroller;
    if (this.#app) {
      this.#fold.sync(sc.scrollTop, this.#max());
      this.#foot?.sync();
      this.#armQuiet();
      return;
    }
    const r = this.#fold.scroll({
      top: sc.scrollTop,
      max: this.#max(),
      room: this.#room,
      focusInside: this.#focusInside,
      fading: this.#fading,
      coversFocus: this.#coversFocus(),
    });
    if (r.cancelFade) { this.#endFade(); }
    if (r.stopSlide) { this.#view.classList.remove('animating'); }
    if (r.action === 'hide') { this.#go(true); } else if (r.action === 'show') { this.#go(false); }
    this.#foot?.scroll();
    this.#render();
  }

  // The header going or coming back with the collapse motion, or, under reduced motion, a cross-fade
  // with no travel. Near the top it rides the scroll and has no motion of its own.
  #go(hiding) {
    if (hiding && this.#fold.last <= this.#room) { return; }
    if (this.#fading) { this.#endFade(); }
    const { duration } = timing('collapse');
    if (hiding && this.#reduced.matches) {
      this.#fold.returned = true;
      this.#fading = true;
      this.#view.classList.add('fading');
      this.#later(() => {
        if (!this.#fading) { return; }
        this.#endFade();
        this.#fold.returned = false;
        this.#render();
      }, duration);
      return;
    }
    this.#animate(duration);
  }

  #animate(duration) {
    const cl = this.#view.classList;
    cl.add('animating');
    this.#later(() => cl.remove('animating'), duration + 60);
  }

  #endFade() {
    this.#fading = false;
    this.#view.classList.remove('fading');
  }

  #later(fn, ms) {
    const t = setTimeout(() => { this.#timers.delete(t); fn(); }, ms);
    this.#timers.add(t);
    return t;
  }

  // A tap on the bar's title.
  #bringBack() {
    if (this.#fold.last <= 0 || this.#hidden() === 0) { return; }
    this.#fold.set(true);
    this.#animate(timing('collapse').duration);
    this.#render();
  }

  // Read before the hold, which keeps the header whole: what was hidden, or fading out under reduced
  // motion, comes back with the collapse motion (its cross-fade under reduced motion), not at once.
  #focusIn() {
    const before = this.#hidden();
    const fading = this.#fading;
    if (fading) { this.#endFade(); }
    this.#focusInside = true;
    if (this.#paused) { return; }
    this.#fold.returned = this.#fold.last > 0;
    this.#fold.held = null;
    if (before > 0 || fading) { this.#animate(timing('collapse').duration); }
    this.#render();
  }

  #focusOut(e) {
    if (e.relatedTarget && this.#head.contains(e.relatedTarget)) { return; }
    this.#focusInside = false;
    this.#render();
  }

  // A touch, wheel or pointer on the conversation ends the hold of the title's opening focus (#707)
  // and of a control a tap focused (Chrome focuses a tapped button; WebKit's :focus-visible can't
  // tell): focus a person moved into the header with keys (a tab, Back, More) holds it until it leaves.
  #releaseHold() {
    const a = this.#doc.activeElement;
    // focus.js marks focus no key moved (data-focus-quiet); keyed focus, bar the title, holds.
    if (!this.#focusInside || (a?.dataset && !('focusQuiet' in a.dataset) && !a.matches('.session-title'))) { return; }
    this.#focusInside = false;
    this.#render();
  }

  // Focused content must stay visible: whether focus came into the conversation, or the header's
  // size or the conversation's changed under it (a resize, a rotation, the keyboard).
  #checkFocusSoon() {
    this.#win.cancelAnimationFrame(this.#frame);
    this.#frame = this.#win.requestAnimationFrame(() => this.#checkFocus());
  }

  #checkFocus() {
    if (!this.#on || this.#paused || this.#focusInside) { return; }
    const a = this.#doc.activeElement;
    if (!a || a === this.#scroller || !this.#scroller.contains(a)) { return; }
    const r = a.getBoundingClientRect();
    const s = this.#scroller.getBoundingClientRect();
    if (!focusHidden(r, { stage: { top: s.top, bottom: s.bottom - (this.#foot?.covered() ?? 0) }, covered: this.#covered() })) { return; }
    this.#holdAndRun(() => a.scrollIntoView({ block: 'nearest', behavior: 'instant' }));
  }

  // A scroll the app makes (to a question) leaves the header as it was: fn makes it.
  appScroll(fn) {
    if (!this.#on || this.#paused) { fn(); return; }
    this.#measure();
    this.#holdAndRun(fn);
  }

  #holdAndRun(fn) {
    const f = this.#fold;
    if (!this.#app) {
      if (!this.#focusInside && landscapeFold({ stageH: this.#stageH, fullH: this.#fullH, hidden: this.#hidden(), room: this.#room })) {
        f.returned = false;
        f.held = this.#room;
      }
      const wasFull = f.hold(this.#hidden());
      // The reply area folds with it on a screen shorter than twice the header (ui.md §6.5), judged on
      // the whole header: an already compact header still folds a whole reply area.
      this.#foot?.hold(this.#stageH < 2 * this.#fullH);
      this.#app = { wasFull, quiet: 0, limit: this.#later(() => this.#endApp(), APP_SCROLL_MAX_MS) };
      this.#render();
    }
    fn();
    this.#armQuiet();
  }

  #armQuiet() {
    if (!this.#app) { return; }
    clearTimeout(this.#app.quiet);
    this.#timers.delete(this.#app.quiet);
    this.#app.quiet = this.#later(() => this.#endApp(), QUIET_MS);
  }

  #endApp() {
    const app = this.#app;
    if (!app) { return; }
    this.#app = null;
    for (const t of [app.quiet, app.limit]) { clearTimeout(t); this.#timers.delete(t); }
    this.#fold.sync(this.#scroller.scrollTop, this.#max());
    this.#fold.release(app.wasFull);
    this.#foot?.release();
    this.#render();
  }

  // A scroll on the floating header is the conversation's: a wheel or a swipe forwards to it, after
  // the header's own (where it is capped at the conversation's height it scrolls within itself).
  #panBy(dy) {
    const t = this.#top;
    const before = t.scrollTop;
    t.scrollTop = before + dy;
    const rest = dy - (t.scrollTop - before);
    if (rest !== 0) {
      // The person is scrolling the conversation, as with a touch or wheel on it: the app's
      // scroll is over, and the title's opening focus no longer holds the header whole.
      this.#endApp();
      this.#releaseHold();
      this.#scroller.scrollTop += rest;
    }
  }

  #wheel(e) {
    if (e.ctrlKey || Math.abs(e.deltaY) < Math.abs(e.deltaX)) { return; }
    e.preventDefault();
    const unit = e.deltaMode === 1 ? 16 : e.deltaMode === 2 ? this.#stageH : 1;
    this.#panBy(e.deltaY * unit);
  }

  #touchStart(e) {
    const t = e.touches[0];
    this.#pan = e.touches.length === 1 ? { x: t.clientX, y: t.clientY, axis: null } : null;
  }

  #touchMove(e) {
    const p = this.#pan;
    if (!p || e.touches.length !== 1) { return; }
    const t = e.touches[0];
    if (p.axis === null) {
      const dx = Math.abs(t.clientX - p.x);
      const dy = Math.abs(t.clientY - p.y);
      if (Math.max(dx, dy) < PAN_AXIS_PX) { return; }
      p.axis = dy > dx ? 'y' : 'x';
    }
    if (p.axis !== 'y') { return; }
    if (e.cancelable) { e.preventDefault(); }
    this.#panBy(p.y - t.clientY);
    p.y = t.clientY;
  }
}
