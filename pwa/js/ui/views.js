// The two lists on screen: online machines, and the connected machine's sessions.
import { h, icon } from './dom.js';
import { SessionCard } from './session-card.js';
import { FoldStore } from './fold-store.js';
import { ago } from './time.js';

// Keeps one card per session, in store order (most recently updated first), updating
// cards in place so an open disclosure, scroll position or focus survives an upsert.
export class SessionListView {
  #cards = new Map();
  #scheduled = false;

  // answering() -> { service, hostId, capabilities } | null, for answering questions.
  constructor(container, store, { fetchDetail, machineName = () => '', folds = new FoldStore(), answering = () => null } = {}) {
    Object.assign(this, { container, store, fetchDetail, machineName, folds, answering });
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
    // Moving a card takes focus out of it, and a card being answered moves when its
    // session updates: the text box being typed in keeps focus and the caret.
    const focused = this.container.contains(document.activeElement) ? document.activeElement : null;
    const caret = focused && typeof focused.selectionStart === 'number' ? [focused.selectionStart, focused.selectionEnd] : null;
    let prev = null;
    for (const s of sessions) {
      live.add(s.id);
      let card = this.#cards.get(s.id);
      if (!card) {
        card = new SessionCard(s, { folds: this.folds, fetchDetail: this.fetchDetail, machineName: this.machineName(), answering: this.answering });
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
      if (!live.has(id)) { card.dispose(); card.element.remove(); this.#cards.delete(id); }
    }
    if (focused && focused.isConnected && document.activeElement !== focused) {
      focused.focus({ preventScroll: true });
      if (caret) { try { focused.setSelectionRange(...caret); } catch { /* not a text control */ } }
    }
    this.onRender?.(sessions.length);
  }

  // Whether the machine takes answers may have changed: every question is drawn again,
  // keeping what was typed.
  refreshQuestions() { for (const c of this.#cards.values()) { c.refreshQuestion(); } }

  card(id) { return this.#cards.get(id) ?? null; }
}

// The Machines panel folds to one line, as the usage panel does: with several machines the
// list pushes the sessions below the fold on a phone. Open unless folded, kept across reloads.
const MACHINES_OPEN = 'agent-bridge.app.machines.open';

export class MachinesFold {
  #count = 0;
  #viewing = null;

  constructor(section, { storage = globalThis.localStorage } = {}) {
    Object.assign(this, { section, storage });
    this.toggle = section.querySelector('.machines-toggle');
    this.summary = section.querySelector('.machines-summary');
    this.body = section.querySelector('.machines-body');
    let saved = null;
    try { saved = storage?.getItem(MACHINES_OPEN) ?? null; } catch { /* a nicety */ }
    this.open = saved !== '0';
    this.toggle.addEventListener('click', () => {
      this.open = !this.open;
      try { this.storage?.setItem(MACHINES_OPEN, this.open ? '1' : '0'); } catch { /* a nicety */ }
      this.render();
    });
    this.render();
  }

  // count: machines listed; viewing: the name of the one whose sessions are shown, if any.
  update(count, viewing) {
    this.#count = count;
    this.#viewing = viewing ?? null;
    this.render();
  }

  render() {
    this.toggle.setAttribute('aria-expanded', String(this.open));
    this.body.hidden = !this.open;
    this.summary.hidden = this.open;
    const online = this.#count === 0 ? 'No machines online' : `${this.#count} online`;
    this.summary.textContent = this.#viewing ? `Viewing ${this.#viewing} \u00b7 ${online}` : online;
  }
}

// The machines offline, from the relay's roster (docs/protocol/roster.md): not this device,
// hosts only, and never one that is online.
export function offlineMachines({ presence, known, me }) {
  return (known ?? []).filter((k) => k && typeof k.id === 'string' && k.id !== me && k.kind === 'host' && !presence.some((d) => d.id === k.id));
}

// status(id) -> "3 sessions · 0.1.0 (dev)", or '' while nothing is known (fleet.js).
// known: the account's machines that are offline (connection.known), each listed after the
// online ones with when it was last seen and a two-press Forget (forget.js); null lists none.
// Returns how many are online.
export function renderMachines(list, { presence, me, current, onSelect, status = () => '', known = null, forget = null, now = Date.now() }) {
  const hosts = presence.filter((d) => d.id !== me && d.kind === 'host');
  const offline = offlineMachines({ presence, known, me });
  // Every change redraws the list, so the button that had focus is replaced: Forget's first
  // press redraws it armed, and a keyboard's second press must land on the new one.
  const active = list.ownerDocument?.activeElement;
  const had = active && list.contains(active) ? { id: active.closest('li')?.dataset.deviceId, cls: ['machine-forget', 'machine-button'].find((c) => active.classList.contains(c)) } : null;
  list.replaceChildren(...hosts.map((d) => {
    const selected = d.id === current;
    const said = status(d.id);
    return h('li', { class: 'machine', dataset: { deviceId: d.id, deviceName: d.name } },
      h('button', {
        type: 'button', class: `machine-button${selected ? ' selected' : ''}`,
        'aria-current': selected ? 'true' : null,
        onclick: () => onSelect(d.id),
      },
      icon('machine'),
      h('span', { class: 'machine-name' }, d.name),
      h('span', { class: 'machine-sub' },
        h('span', { class: 'machine-id' }, d.id.slice(0, 8)),
        said ? h('span', { class: 'machine-status' }, ` \u00b7 ${said}`) : null),
      h('span', { class: 'machine-state' }, selected ? 'Viewing' : 'Online')));
  }), ...offline.map((k) => {
    const name = typeof k.name === 'string' && k.name ? k.name : 'unnamed';
    const seen = ago(k.lastSeen, now);
    const s = forget?.stateOf(k.id) ?? { armed: false, busy: false, error: null };
    return h('li', { class: 'machine offline', dataset: { deviceId: k.id, deviceName: name } },
      h('div', { class: 'machine-row' },
        icon('machine'),
        h('span', { class: 'machine-name' }, name),
        h('span', { class: 'machine-sub' },
          h('span', { class: 'machine-id' }, k.id.slice(0, 8)),
          h('span', { class: 'machine-seen' }, ` \u00b7 last seen ${seen || 'a while ago'}`)),
        forget ? h('button', {
          type: 'button', class: `button secondary machine-forget${s.armed ? ' armed' : ''}`,
          'aria-label': s.armed ? `Forget ${name}: press again to confirm` : `Forget ${name}`,
          // Not disabled while it goes: a disabled button loses focus, and the next draw could not
          // give it back. A press meanwhile does nothing (forget.js).
          'aria-disabled': s.busy ? 'true' : null,
          onclick: () => forget.press(k.id, k.lastSeen),
        }, s.busy ? 'Forgetting\u2026' : s.armed ? 'Press again to forget' : 'Forget') : h('span', { class: 'machine-state' }, 'Offline')),
      s.error ? h('p', { class: 'machine-error', role: 'alert' }, s.error) : null);
  }));
  if (had?.id && had.cls) {
    const again = [...list.children].find((li) => li.dataset.deviceId === had.id)?.querySelector(`.${had.cls}`);
    again?.focus();
  }
  return hosts.length;
}
