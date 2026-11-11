// A small menu of actions for one row of the sessions list: Move up, Move down, Pin. Opened by
// a long press on a phone, a right-click, or the context-menu key (Shift+F10) from a keyboard.
// Arrow keys move between items, Escape closes it, and focus goes back where it came from.
import { h } from './dom.js';
import { enter, leave } from './motion.js';

let nextDescription = 0;

export class RowMenu {
  #el = null;
  #back = null;
  #off = null;
  #trigger = null;

  get open() { return !!this.#el; }

  // anchor: the element the menu is drawn in (positioned).
  // items: [{ label, disabled, shortcut, reason, run }]; a reason keeps a disabled item focusable.
  // back: what gets focus when the menu closes.
  // trigger: the button that opened it, if it is one (aria-expanded follows the menu, and a press
  // on it is its own to handle, not an outside press that closes the menu just before it reopens).
  show(anchor, items, { back = null, label = 'Row actions', trigger = null } = {}) {
    this.close();
    this.#back = back;
    this.#trigger = trigger;
    trigger?.setAttribute('aria-expanded', 'true');
    const buttons = [];
    const entries = items.map((it) => {
      const keyId = it.shortcut ? `row-menu-description-${++nextDescription}` : null;
      const reasonId = it.reason ? `row-menu-description-${++nextDescription}` : null;
      const button = h('button', {
        type: 'button', role: 'menuitem', class: 'row-menu-item',
        disabled: it.disabled && !it.reason ? true : null,
        'aria-disabled': it.disabled ? 'true' : null,
        'aria-describedby': [keyId, reasonId].filter(Boolean).join(' ') || null,
        onclick: () => { if (it.disabled) { return; } this.close(); it.run(); },
      }, h('span', { class: 'row-menu-label' }, it.label));
      buttons.push(button);
      return h('div', { class: 'row-menu-entry', role: 'none' }, button,
      keyId ? h('span', { class: 'row-menu-key', id: keyId, 'aria-hidden': 'true' }, it.shortcut) : null,
      reasonId ? h('span', { class: 'row-menu-reason', id: reasonId, 'aria-hidden': 'true' }, it.reason) : null);
    });
    const el = h('div', { class: 'row-menu', role: 'menu', 'aria-label': label }, ...entries);
    el.addEventListener('keydown', (e) => {
      const live = buttons.filter((b) => !b.disabled);
      const i = live.indexOf(document.activeElement);
      if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); this.close(); return; }
      if (e.key === 'Tab') { this.close(false); return; }
      const step = e.key === 'ArrowDown' ? 1 : e.key === 'ArrowUp' ? -1 : 0;
      if (step && live.length) {
        e.preventDefault();
        live[(i + step + live.length) % live.length].focus();
      }
    });
    anchor.append(el);
    this.#el = el;
    // A press anywhere else closes it, as a native menu does.
    const outside = (e) => { if (!el.contains(e.target) && !trigger?.contains(e.target)) { this.close(false); } };
    document.addEventListener('pointerdown', outside, true);
    this.#off = () => document.removeEventListener('pointerdown', outside, true);
    // A row near the foot of a phone's list would put its menu under the bottom navigation, where
    // a tap on Pin lands on the bar instead: it opens upward where there is room above.
    const r = el.getBoundingClientRect();
    const bar = document.getElementById('nav');
    const fixed = bar && !bar.hidden && getComputedStyle(bar).position === 'fixed';
    const floor = fixed ? bar.getBoundingClientRect().top : window.innerHeight;
    const up = r.bottom > floor && anchor.getBoundingClientRect().top - r.height > 0;
    if (up) { el.classList.add('up'); }
    // Menu (ui.md §5.3): it settles in from its anchor, and is usable as it does.
    enter(el, 'menu', { dy: up ? 1 : -1 });
    el.scrollIntoView?.({ block: 'nearest' });
    (buttons.find((b) => !b.disabled && b.getAttribute('aria-disabled') !== 'true') ?? buttons.find((b) => !b.disabled) ?? el).focus?.();
  }

  close(refocus = true) {
    if (!this.#el) { return; }
    this.#off?.();
    this.#off = null;
    this.#trigger?.setAttribute('aria-expanded', 'false');
    this.#trigger = null;
    const had = this.#el.contains(document.activeElement);
    // What was chosen acts at once; the menu fades out where it was.
    leave(this.#el, 'menu');
    this.#el.remove();
    this.#el = null;
    if (refocus && had && this.#back?.isConnected) { this.#back.focus({ preventScroll: true }); }
    this.#back = null;
  }
}
