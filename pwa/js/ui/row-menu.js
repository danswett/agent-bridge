// A small menu of actions for one row of the sessions list: Move up, Move down, Pin. Opened by
// a long press on a phone, a right-click, or the context-menu key (Shift+F10) from a keyboard.
// Arrow keys move between items, Escape closes it, and focus goes back where it came from.
import { h } from './dom.js';

export class RowMenu {
  #el = null;
  #back = null;
  #off = null;

  get open() { return !!this.#el; }

  // anchor: the element the menu is drawn in (positioned). items: [{ label, disabled, run }].
  // back: what gets focus when the menu closes.
  show(anchor, items, { back = null, label = 'Row actions' } = {}) {
    this.close();
    this.#back = back;
    const buttons = items.map((it) => h('button', {
      type: 'button', role: 'menuitem', class: 'row-menu-item', disabled: it.disabled ? true : null,
      onclick: () => { this.close(); it.run(); },
    }, it.label));
    const el = h('div', { class: 'row-menu', role: 'menu', 'aria-label': label }, ...buttons);
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
    const outside = (e) => { if (!el.contains(e.target)) { this.close(false); } };
    document.addEventListener('pointerdown', outside, true);
    this.#off = () => document.removeEventListener('pointerdown', outside, true);
    (buttons.find((b) => !b.disabled) ?? el).focus?.();
  }

  close(refocus = true) {
    if (!this.#el) { return; }
    this.#off?.();
    this.#off = null;
    const had = this.#el.contains(document.activeElement);
    this.#el.remove();
    this.#el = null;
    if (refocus && had && this.#back?.isConnected) { this.#back.focus({ preventScroll: true }); }
    this.#back = null;
  }
}
