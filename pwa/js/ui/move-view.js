// The move strip on a session (#1715, ui.md 6.6.2): 'Moving from X to Y', where the move is in
// words, and the five steps each with an icon and a word for its state. Built once with every
// part in place, and only the words and icons change, so it is the same height at every step.
// Phones show the one line ('Step 3 of 5') and the steps open from it; 760 px and wider show them.
import { h } from './dom.js';
import { glyph, setGlyph } from './glyph.js';
import { PHONE } from './collapse-header.js';
import { moveView } from '../move.js';

const ICON = { done: 'done', current: 'working', failed: 'failed', notYet: 'idle', notReached: 'idle' };

export class MoveStrip {
  constructor({ onGoTo = null } = {}) {
    this.head = h('p', { class: 'move-head' });
    this.summary = h('summary', { class: 'move-summary' });
    this.items = [];
    this.list = h('ol', { class: 'move-steps' });
    this.note = h('p', { class: 'move-note' });
    this.go = h('button', { type: 'button', class: 'move-go', disabled: !onGoTo, onclick: () => onGoTo?.() }, 'Go to it');
    this.details = h('details', { class: 'move-details' }, this.summary, this.list);
    this.element = h('section', { class: 'move-strip', hidden: true, 'aria-label': 'Move to another machine' }, this.head, this.details, this.note, this.go);
    this.media = window.matchMedia?.(PHONE);
    this.details.open = !this.media?.matches;
    this.media?.addEventListener?.('change', (e) => { this.details.open = !e.matches; });
  }

  set(move) {
    this.element.hidden = !move;
    if (!move) { return; }
    const v = moveView(move);
    this.element.dataset.state = v.moved ? 'moved' : v.failed ? 'failed' : 'moving';
    this.head.textContent = `${v.moved ? 'Moved' : 'Moving'} from ${move.from} to ${move.to}`;
    this.summary.textContent = v.summary;
    v.steps.forEach((s, i) => {
      if (!this.items[i]) {
        this.items[i] = { li: h('li', { class: 'move-step-item' }), icon: glyph('idle', { size: 16 }), label: h('span', { class: 'move-label' }), word: h('span', { class: 'move-word' }) };
        this.items[i].li.append(this.items[i].icon, ' ', this.items[i].label, ' ', this.items[i].word);
        this.list.append(this.items[i].li);
      }
      const it = this.items[i];
      it.li.dataset.state = s.state;
      setGlyph(it.icon, ICON[s.state], { size: 16 });
      it.label.textContent = s.label;
      it.word.textContent = s.word;
    });
    this.note.textContent = v.note;
    this.go.style.visibility = v.moved ? 'visible' : 'hidden';
  }
}
