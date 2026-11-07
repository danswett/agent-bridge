import { PANES, STEP, PaneWidths, settle, limits, pick } from '../pane-widths.js';

const LABELS = { list: 'Resize the session list', inspector: 'Resize the delegate inspector' };
const TAP_MS = 350;
const TAP_SLOP = 6;

// The two dividers of ui.md §5.2 (#1030): list | session and session | inspector, each a
// role=separator that takes a drag, Left and Right, and a double-click or double-tap to reset.
// A drag only writes a CSS variable (--list-w, --inspector-w) once a frame, on the section that
// holds the panes: nothing is read back while it moves, so the panes never re-measure per move.
export class PaneDividers {
  // root: the #sessions section; list and inspector: its two divider elements.
  // inspectorShown(): whether the inspector is a pane of its own now. onChange(): after a width
  // changes, for what has to follow the panes' sizes.
  constructor({ root, list, inspector, inspectorShown = () => false, onChange = () => {}, storage, win = globalThis }) {
    Object.assign(this, { root, inspectorShown, onChange, win });
    this.widths = new PaneWidths(storage === undefined ? {} : { storage });
    this.els = { list, inspector };
    this.total = 0;
    this.frame = 0;
    this.shownNow = { list: this.widths.list, inspector: 0 };
    for (const which of ['list', 'inspector']) { this.#wire(which); }
    this.win.addEventListener?.('resize', () => this.refresh());
    if (typeof this.win.ResizeObserver === 'function') { new this.win.ResizeObserver(() => this.refresh()).observe(root); }
    this.refresh();
  }

  get #state() {
    return { total: this.total, list: this.widths.list, inspector: this.widths.inspector, inspectorShown: this.inspectorShown() };
  }

  // Measured again from the window, the inspector toggle and the section's own size.
  refresh() {
    this.total = this.root.clientWidth;
    this.#apply();
  }

  #apply() {
    if (!this.total) { return; }
    const state = this.#state;
    const now = settle(state);
    this.shownNow = now;
    this.root.style.setProperty('--list-w', `${now.list}px`);
    this.root.style.setProperty('--inspector-w', `${now.inspector || PANES.inspector}px`);
    for (const which of ['list', 'inspector']) {
      const el = this.els[which];
      const { min, max } = limits(which, state);
      el.setAttribute('aria-valuemin', String(min));
      el.setAttribute('aria-valuemax', String(max));
      el.setAttribute('aria-valuenow', String(now[which] || PANES[which]));
    }
  }

  #wire(which) {
    const el = this.els[which];
    if (!el) { return; }
    el.setAttribute('role', 'separator');
    el.setAttribute('aria-orientation', 'vertical');
    el.setAttribute('aria-label', LABELS[which]);
    el.tabIndex = 0;
    // The pane to the right of the inspector's divider grows as the divider moves left.
    const sign = which === 'list' ? 1 : -1;
    let drag = null;
    let tap = null;

    el.addEventListener('pointerdown', (e) => {
      if (e.button > 0) { return; }
      this.total = this.root.clientWidth;
      drag = { id: e.pointerId, x: e.clientX, from: this.shownNow[which], width: this.shownNow[which], moved: false, kept: this.widths.kept(which) };
      el.setPointerCapture?.(e.pointerId);
      el.dataset.dragging = 'true';
      e.preventDefault();
    });
    el.addEventListener('pointermove', (e) => {
      if (!drag || e.pointerId !== drag.id) { return; }
      if (Math.abs(e.clientX - drag.x) > TAP_SLOP) { drag.moved = true; }
      if (!drag.moved) { return; }
      drag.width = pick(which, drag.from + sign * (e.clientX - drag.x), this.#state);
      this.#queue(which, drag.width);
    });
    const end = (e) => {
      if (!drag || e.pointerId !== drag.id) { return; }
      const drag0 = drag;
      const { moved } = drag0;
      drag = null;
      delete el.dataset.dragging;
      el.releasePointerCapture?.(e.pointerId);
      if (moved) {
        tap = null;
        if (this.frame) { this.win.cancelAnimationFrame?.(this.frame); this.frame = 0; }
        this.#pending = null;
        this.#set(which, drag0.width);
        return;
      }
      // Two taps close together reset, as a double-click does: a touch screen sends none.
      const now = this.win.performance?.now?.() ?? Date.now();
      if (tap && now - tap.at < TAP_MS && Math.abs(e.clientX - tap.x) < TAP_SLOP * 3) { tap = null; this.#reset(which); }
      else { tap = { at: now, x: e.clientX }; }
    };
    el.addEventListener('pointerup', end);
    el.addEventListener('pointercancel', (e) => {
      if (!drag || e.pointerId !== drag.id) { return; }
      const { moved, kept } = drag;
      drag = null;
      delete el.dataset.dragging;
      el.releasePointerCapture?.(e.pointerId);
      // The drag never happened: no queued frame may commit its width, and the one before shows again.
      // The drag never happened: no queued frame may commit its width, and the one before shows again.
      if (this.frame) { this.win.cancelAnimationFrame?.(this.frame); this.frame = 0; }
      this.#pending = null;
      if (moved) { this.widths.restore(which, kept); }
      this.#apply();
    });
    el.addEventListener('dblclick', () => this.#reset(which));
    el.addEventListener('keydown', (e) => {
      const now = this.shownNow[which] || PANES[which];
      const { min, max } = limits(which, this.#state);
      let next = null;
      // Left and Right move the divider, so the inspector, to its right, grows going left.
      if (e.key === 'ArrowLeft') { next = now - sign * STEP; }
      else if (e.key === 'ArrowRight') { next = now + sign * STEP; }
      else if (e.key === 'Home') { next = min; }
      else if (e.key === 'End') { next = max; }
      if (next === null) { return; }
      e.preventDefault();
      this.#set(which, pick(which, next, this.#state));
    });
  }

  #pending = null;

  // One write a frame while a pointer moves.
  #queue(which, width) {
    this.#pending = { which, width };
    if (this.frame) { return; }
    const raf = this.win.requestAnimationFrame?.bind(this.win);
    if (!raf) { this.#flush(); return; }
    this.frame = raf(() => { this.frame = 0; this.#flush(); });
  }

  #flush() {
    if (!this.#pending) { return; }
    const { which, width } = this.#pending;
    this.#pending = null;
    this.widths.set(which, width, false);
    this.#apply();
  }

  #set(which, width) {
    this.widths.set(which, width);
    this.#apply();
    this.onChange();
  }

  #reset(which) {
    this.widths.reset(which);
    this.#apply();
    this.onChange();
  }
}
