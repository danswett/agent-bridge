// The two lists on screen: online machines, and the sessions of the machines shown.
import { h, icon } from './dom.js';
import { ALL } from '../host-session.js';
import { SessionCard } from './session-card.js';
import { FoldStore } from './fold-store.js';
import { ago } from './time.js';

// Keeps one card per session, from every machine shown, in the order given (most recently
// updated first), updating cards in place so an open disclosure, scroll position or focus
// survives an upsert. A card is its machine's and session's: two machines may each have a
// session of the same id, and each card acts only on its own machine.
export class SessionListView {
  #cards = new Map();
  #scheduled = false;

  // sessions: { list() -> [{ hostId, session }], onChange } (HostSession.sessions).
  // fetchDetail(hostId, id) -> Promise<Session|null>; machine(hostId) -> { name, show }: what to
  // call the machine, and whether its cards say so (with every machine shown).
  // answering(hostId) -> { service, replies, stops, hostId, capabilities, readOnly } | null.
  constructor(container, sessions, { fetchDetail, machine = () => ({ name: '', show: false }), folds = new FoldStore(), answering = () => null } = {}) {
    Object.assign(this, { container, sessions, fetchDetail, machine, folds, answering });
    sessions.onChange(() => this.schedule());
    // Relative times move on without any event from the host.
    this.ticker = setInterval(() => { for (const c of this.#cards.values()) { c.refreshTimes(); } }, 15000);
  }

  static key(hostId, id) { return `${hostId}\n${id}`; }

  // Bursts of events (a snapshot is one upsert per session) render once.
  schedule() {
    if (this.#scheduled) { return; }
    this.#scheduled = true;
    queueMicrotask(() => { this.#scheduled = false; this.render(); });
  }

  render() {
    const entries = this.sessions.list();
    const live = new Set();
    // Moving a card takes focus out of it, and a card being answered moves when its
    // session updates: the text box being typed in keeps focus and the caret.
    const focused = this.container.contains(document.activeElement) ? document.activeElement : null;
    const caret = focused && typeof focused.selectionStart === 'number' ? [focused.selectionStart, focused.selectionEnd] : null;
    let prev = null;
    for (const { hostId, session: s } of entries) {
      const key = SessionListView.key(hostId, s.id);
      live.add(key);
      let card = this.#cards.get(key);
      if (!card) {
        const m = this.machine(hostId);
        card = new SessionCard(s, {
          hostId, folds: this.folds, fetchDetail: (id) => this.fetchDetail(hostId, id),
          machineName: m.name, labelled: m.show, answering: () => this.answering(hostId),
        });
        this.#cards.set(key, card);
      } else if (card.session !== s) {
        card.update(s);
      }
      // Move only what is out of place, so a card being read is not torn out and put back.
      const want = prev ? prev.nextSibling : this.container.firstChild;
      if (want !== card.element) { this.container.insertBefore(card.element, want); }
      prev = card.element;
    }
    for (const [key, card] of this.#cards) {
      if (!live.has(key)) { card.dispose(); card.element.remove(); this.#cards.delete(key); }
    }
    if (focused && focused.isConnected && document.activeElement !== focused) {
      focused.focus({ preventScroll: true });
      if (caret) { try { focused.setSelectionRange(...caret); } catch { /* not a text control */ } }
    }
    this.onRender?.(entries.length);
  }

  // Whether a machine takes answers may have changed: every question of its cards (of every
  // card, without one) is drawn again, keeping what was typed.
  refreshQuestions(hostId = null) {
    for (const c of this.#cards.values()) { if (hostId === null || c.hostId === hostId) { c.refreshQuestion(); } }
  }

  // A machine's name, or whether cards name their machine, may have changed.
  refreshMachines() {
    for (const c of this.#cards.values()) { const m = this.machine(c.hostId); c.setMachine(m.name, m.show); }
  }

  // A machine's session's card; without a machine, the first card for that session.
  card(hostId, id) {
    if (hostId) { return this.#cards.get(SessionListView.key(hostId, id)) ?? null; }
    for (const c of this.#cards.values()) { if (c.id === id) { return c; } }
    return null;
  }
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

  // count: machines listed; viewing: the name of the one whose sessions are shown, or 'all'
  // with every machine's, if any.
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

// current: the machine chosen, or ALL. With more than one machine online, All machines is listed
// first, as the Windows app's picker lists it; allStatus says what it shows. With one, that one
// is the one viewed when every machine is.
// status(id) -> "3 sessions · 0.1.0 (dev)", or '' while nothing is known (fleet.js).
// known: the account's machines that are offline (connection.known), each listed after the
// online ones with when it was last seen and a two-press Forget (forget.js); null lists none.
// Returns how many are online.
// What each list was last drawn from.
const drawnAs = new WeakMap();

export function renderMachines(list, { presence, me, current, onSelect, status = () => '', known = null, forget = null, now = Date.now(), allStatus = '' }) {
  const hosts = presence.filter((d) => d.id !== me && d.kind === 'host');
  const all = current === ALL;
  const offline = offlineMachines({ presence, known, me });
  // Drawn again only when something in it changes. It is asked to redraw on every session
  // update of every machine shown, and a button replaced between a tap's touch and its release
  // takes no click: a tap at startup, with every machine sending its sessions, was lost.
  const key = JSON.stringify([current, allStatus, hosts.map((d) => [d.id, d.name, status(d.id)]),
    // lastSeen itself, not only how it reads: Forget sends it, to tell a machine that came back.
    offline.map((k) => [k.id, k.name, k.lastSeen, ago(k.lastSeen, now), forget?.stateOf(k.id) ?? null]), !!forget]);
  if (drawnAs.get(list) === key) { return hosts.length; }
  drawnAs.set(list, key);
  // Every change redraws the list, so the button that had focus is replaced: Forget's first
  // press redraws it armed, and a keyboard's second press must land on the new one.
  const active = list.ownerDocument?.activeElement;
  const had = active && list.contains(active) ? { id: active.closest('li')?.dataset.deviceId, cls: ['machine-forget', 'machine-button'].find((c) => active.classList.contains(c)) } : null;
  const everyMachine = hosts.length > 1 ? [h('li', { class: 'machine all-machines', dataset: { deviceId: ALL, deviceName: 'All machines' } },
    h('button', {
      type: 'button', class: `machine-button${all ? ' selected' : ''}`,
      'aria-current': all ? 'true' : null,
      onclick: () => onSelect(ALL),
    },
    icon('machine'),
    h('span', { class: 'machine-name' }, 'All machines'),
    h('span', { class: 'machine-sub' }, allStatus || `Sessions from all ${hosts.length} machines together`),
    h('span', { class: 'machine-state' }, all ? 'Viewing' : '')))] : [];
  list.replaceChildren(...everyMachine, ...hosts.map((d) => {
    const selected = d.id === current || (all && hosts.length === 1);
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
