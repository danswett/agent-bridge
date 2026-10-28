// The two lists on screen: online machines, and the connected machine's sessions.
import { h, icon } from './dom.js';
import { SessionCard } from './session-card.js';
import { FoldStore } from './fold-store.js';

// Keeps one card per session, in store order (most recently updated first), updating
// cards in place so an open disclosure, scroll position or focus survives an upsert.
export class SessionListView {
  #cards = new Map();
  #scheduled = false;

  constructor(container, store, { fetchDetail, machineName = () => '', folds = new FoldStore() } = {}) {
    Object.assign(this, { container, store, fetchDetail, machineName, folds });
    store.onChange(() => this.schedule());
    // Relative times move on without any event from the host.
    this.ticker = setInterval(() => { for (const c of this.#cards.values()) { c.refreshTimes(); } }, 15000);
  }

  // Bursts of events (a snapshot is one upsert per session) render once.
  schedule() {
    if (this.#scheduled) { return; }
    this.#scheduled = true;
    queueMicrotask(() => { this.#scheduled = false; this.render(); });
  }

  render() {
    const sessions = this.store.list();
    const live = new Set();
    let prev = null;
    for (const s of sessions) {
      live.add(s.id);
      let card = this.#cards.get(s.id);
      if (!card) {
        card = new SessionCard(s, { folds: this.folds, fetchDetail: this.fetchDetail, machineName: this.machineName() });
        this.#cards.set(s.id, card);
      } else if (card.session !== s) {
        card.update(s);
      }
      // Move only what is out of place, so a card being read is not torn out and put back.
      const want = prev ? prev.nextSibling : this.container.firstChild;
      if (want !== card.element) { this.container.insertBefore(card.element, want); }
      prev = card.element;
    }
    for (const [id, card] of this.#cards) {
      if (!live.has(id)) { card.element.remove(); this.#cards.delete(id); }
    }
    this.onRender?.(sessions.length);
  }

  card(id) { return this.#cards.get(id) ?? null; }
}

export function renderMachines(list, { presence, me, current, onSelect }) {
  const hosts = presence.filter((d) => d.id !== me && d.kind === 'host');
  list.replaceChildren(...hosts.map((d) => {
    const selected = d.id === current;
    return h('li', { class: 'machine', dataset: { deviceId: d.id, deviceName: d.name } },
      h('button', {
        type: 'button', class: `machine-button${selected ? ' selected' : ''}`,
        'aria-current': selected ? 'true' : null,
        onclick: () => onSelect(d.id),
      },
      icon('machine'),
      h('span', { class: 'machine-name' }, d.name),
      h('span', { class: 'machine-id' }, d.id.slice(0, 8)),
      h('span', { class: 'machine-state' }, selected ? 'Viewing' : 'Online')));
  }));
  return hosts.length;
}
