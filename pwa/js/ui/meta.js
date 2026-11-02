// Lines of facts and the paths in them (docs/design/ui.md §4.2, §6.1, #214, #215).
import { h } from './dom.js';

// A middot between two facts: in the text face, with a no-break space before it and an ordinary
// one after, so it stays with the fact it follows. A line can end with one, and never starts
// with one (#214). Not read aloud: the facts are.
export function sep() {
  return h('span', { class: 'sep', 'aria-hidden': 'true' }, '\u00a0\u00b7 ');
}

// Fills el with the facts given (nodes or strings; empty ones are left out), a separator between
// each two.
export function setFacts(el, facts) {
  const kept = facts.filter((f) => f !== null && f !== undefined && f !== false && f !== '');
  const children = [];
  kept.forEach((f, i) => {
    if (i) { children.push(sep()); }
    children.push(f);
  });
  el.replaceChildren(...children);
  return kept.length;
}

// The words of a status (js/status.js) that are several facts in one ("Waiting on you · 2
// delegates still running"), with real separators between them.
export function factsOf(text) {
  return String(text ?? '').split(' \u00b7 ');
}

// A path in mono that, too long for its line, gives up its middle rather than breaking mid-word:
// the start shortens to an ellipsis and the last part stays whole. The whole path is its tooltip.
export function pathView(path, cls = '') {
  const p = String(path ?? '');
  const cut = Math.max(p.lastIndexOf('/'), p.lastIndexOf('\\'));
  const head = cut > 0 && cut < p.length - 1 ? p.slice(0, cut) : '';
  const tail = head ? p.slice(cut) : p;
  return h('span', { class: `path mono${cls ? ` ${cls}` : ''}`, title: p },
    head ? h('span', { class: 'path-head' }, head) : null,
    h('span', { class: 'path-tail' }, tail));
}

export function lastSegment(path) {
  if (!path) { return null; }
  const parts = path.split(/[\\/]/).filter(Boolean);
  return parts[parts.length - 1] ?? null;
}
