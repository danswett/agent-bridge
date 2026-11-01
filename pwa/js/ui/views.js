// The two lists on screen: online machines, and the sessions of the machines shown.
import { h, icon } from './dom.js';
import { ALL } from '../host-session.js';
import { SessionCard } from './session-card.js';
import { FoldStore } from './fold-store.js';
import { RowMenu } from './row-menu.js';
import { SessionOrder } from '../session-order.js';
import { ago } from './time.js';
import { updateLine, lastLine, canCheck, canInstall } from '../update.js';

const LONG_PRESS_MS = 500;
// A finger that moves further than this is scrolling, not pressing.
const LONG_PRESS_SLOP = 10;

// Keeps one card per session, from every machine shown, in the person's order (session-order.js):
// Pinned, then the rest, then an Ended group (ui.md §6.1). Cards are updated in place so an open
// disclosure, scroll position or focus survives an upsert, and an upsert never moves one. A card
// is its machine's and session's: two machines may each have a session of the same id, and each
// card acts only on its own machine.
//
// Rows move only when the person moves them: Alt+Up/Down on a card's title, a drag by the handle
// that shows on hover, or the row menu (a long press on a phone, a right-click, Shift+F10).
export class SessionListView {
  #cards = new Map();
  #scheduled = false;
  #menu = new RowMenu();
  #heads = {};
  #ended = null;
  #press = null;
  #swallowClick = false;

  // sessions: { list() -> [{ hostId, session }], onChange } (HostSession.sessions).
  // fetchDetail(hostId, id) -> Promise<Session|null>; machine(hostId) -> { name, show }: what to
  // call the machine, and whether its cards say so (with every machine shown).
  // answering(hostId) -> { service, replies, stops, hostId, capabilities, readOnly } | null.
  // order: the person's order (SessionOrder). shows(hostId): whether a machine is shown.
  // settled(hostId): whether a machine has sent its whole list yet.
  constructor(container, sessions, { fetchDetail, machine = () => ({ name: '', show: false }), folds = new FoldStore(), answering = () => null, order = new SessionOrder(), shows = () => true, settled = () => true } = {}) {
    Object.assign(this, { container, sessions, fetchDetail, machine, folds, answering, order, shows, settled });
    sessions.onChange((change) => {
      this.order.observe(change);
      if (change?.type === 'synced' && change.hostId) {
        this.order.synced(change.hostId, new Set(sessions.list().filter((e) => e.hostId === change.hostId).map((e) => e.session.id)));
      }
      this.schedule();
    });
    // Relative times move on without any event from the host.
    this.ticker = setInterval(() => { for (const c of this.#cards.values()) { c.refreshTimes(); } }, 15000);
    // What a move did, said to a screen reader.
    this.said = h('p', { class: 'sr-only', role: 'status', 'aria-live': 'polite' });
    container.after(this.said);
    this.#listen();
  }

  static key(hostId, id) { return `${hostId}\n${id}`; }

  // Bursts of events (a snapshot is one upsert per session) render once.
  schedule() {
    if (this.#scheduled) { return; }
    this.#scheduled = true;
    queueMicrotask(() => { this.#scheduled = false; this.render(); });
  }

  setSort(sort) {
    this.order.setSort(sort);
    this.render();
  }

  render() {
    const entries = this.sessions.list();
    const { pinned, sessions, ended } = this.order.arrange(entries, { nameOf: (id) => this.machine(id).name, shows: this.shows, settled: this.settled });
    this.container.dataset.sort = this.order.sort;
    this.onSorted?.();
    const live = new Set();
    // Moving a card takes focus out of it, and a card being answered moves when its
    // session updates in Most recent: the text box being typed in keeps focus and the caret.
    const focused = this.container.contains(document.activeElement) ? document.activeElement : null;
    const caret = focused && typeof focused.selectionStart === 'number' ? [focused.selectionStart, focused.selectionEnd] : null;
    const want = [];
    const cards = (list, isPinned) => {
      for (const { hostId, session: s } of list) {
        const key = SessionListView.key(hostId, s.id);
        live.add(key);
        let card = this.#cards.get(key);
        if (!card) {
          const m = this.machine(hostId);
          card = new SessionCard(s, {
            hostId, folds: this.folds, fetchDetail: (id) => this.fetchDetail(hostId, id),
            machineName: m.name, labelled: m.show, answering: () => this.answering(hostId),
            // Opened, by its title, a notification or a launch, its news is seen.
            onFold: () => this.schedule(),
          });
          card.element.dataset.key = key;
          card.els.frame.prepend(h('span', { class: 'drag-handle', 'aria-hidden': 'true', title: 'Drag to move' }));
          this.#cards.set(key, card);
        } else if (card.session !== s) {
          card.update(s);
        }
        card.setPinned(isPinned);
        // Open on screen, its news is seen as it comes; folded, it gets the dot.
        if (!card.folded) { this.order.markSeen(key, s.updatedAt); }
        card.setUnread(this.order.unread(key, s.updatedAt));
        want.push(card.element);
      }
    };
    if (pinned.length) { want.push(this.#head('pinned', 'Pinned')); }
    cards(pinned, true);
    if (pinned.length && sessions.length) { want.push(this.#head('sessions', 'Sessions')); }
    cards(sessions, false);
    if (ended.length) { want.push(this.#endedGroup(ended)); }
    // Move only what is out of place, so a card being read is not torn out and put back.
    let prev = null;
    for (const el of want) {
      const at = prev ? prev.nextSibling : this.container.firstChild;
      if (at !== el) { this.container.insertBefore(el, at); }
      prev = el;
    }
    while (prev ? prev.nextSibling : this.container.firstChild) { (prev ? prev.nextSibling : this.container.firstChild).remove(); }
    for (const [key, card] of this.#cards) {
      if (!live.has(key)) { card.dispose(); card.element.remove(); this.#cards.delete(key); }
    }
    if (focused && focused.isConnected && document.activeElement !== focused) {
      focused.focus({ preventScroll: true });
      if (caret) { try { focused.setSelectionRange(...caret); } catch { /* not a text control */ } }
    }
    this.onRender?.(entries.length);
  }

  #head(id, text) {
    this.#heads[id] ??= h('h3', { class: 'list-group', dataset: { group: id } }, text);
    return this.#heads[id];
  }

  // Ended · N, folded unless opened: the sessions the host removed while this page was open.
  #endedGroup(ended) {
    if (!this.#ended) {
      this.#ended = { summary: h('summary'), list: h('ul', { class: 'ended-list' }) };
      this.#ended.el = h('details', { class: 'ended-group' }, this.#ended.summary, this.#ended.list);
    }
    const { el, summary, list } = this.#ended;
    summary.textContent = `Ended \u00b7 ${ended.length}`;
    const now = Date.now();
    list.replaceChildren(...ended.map((e) => {
      const m = this.machine(e.hostId);
      const when = ago(e.endedAt, now);
      return h('li', { class: 'ended', dataset: { id: e.session.id, hostId: e.hostId } },
        h('span', { class: 'ended-title' }, e.session.title || 'Untitled session'),
        h('span', { class: 'ended-meta' }, [m.show ? m.name : null, when ? `ended ${when}` : null].filter(Boolean).join(' \u00b7 ')));
    }));
    return el;
  }

  #cardOf(el) {
    const node = el?.closest?.('.session');
    return node ? this.#cards.get(node.dataset.key) ?? null : null;
  }

  #keyOf(card) { return SessionListView.key(card.hostId, card.id); }

  // Moves a row and keeps the move in view and in focus.
  #move(card, delta) {
    if (!this.order.move(this.#keyOf(card), delta)) { return false; }
    this.render();
    card.els.fold.focus({ preventScroll: true });
    card.element.scrollIntoView({ block: 'nearest' });
    this.#say(delta < 0 ? 'Moved up' : 'Moved down');
    return true;
  }

  #pin(card, pinned) {
    this.order.setPinned(this.#keyOf(card), pinned);
    this.render();
    card.els.fold.focus({ preventScroll: true });
    this.#say(pinned ? 'Pinned' : 'Unpinned');
  }

  #say(text) { this.said.textContent = text; }

  openMenu(card) {
    const key = this.#keyOf(card);
    const pinned = this.order.isPinned(key);
    this.#menu.show(card.element, [
      { label: 'Move up', disabled: !this.order.canMove(key, -1), run: () => this.#move(card, -1) },
      { label: 'Move down', disabled: !this.order.canMove(key, 1), run: () => this.#move(card, 1) },
      { label: pinned ? 'Unpin' : 'Pin', run: () => this.#pin(card, !pinned) },
    ], { back: card.els.fold, label: `${card.session.title || 'Session'}: actions` });
  }

  #listen() {
    const c = this.container;
    c.addEventListener('keydown', (e) => {
      const card = e.target.closest?.('.fold') ? this.#cardOf(e.target) : null;
      if (!card) { return; }
      // The context-menu key, or Shift+F10: Safari has neither as a contextmenu event.
      if (e.key === 'ContextMenu' || (e.key === 'F10' && e.shiftKey && !e.altKey && !e.ctrlKey && !e.metaKey)) {
        e.preventDefault();
        this.openMenu(card);
        return;
      }
      if (!e.altKey || e.ctrlKey || e.metaKey || e.shiftKey || (e.key !== 'ArrowUp' && e.key !== 'ArrowDown')) { return; }
      e.preventDefault();
      this.#move(card, e.key === 'ArrowUp' ? -1 : 1);
    });
    c.addEventListener('contextmenu', (e) => {
      const card = e.target.closest?.('.fold') ? this.#cardOf(e.target) : null;
      if (!card) { return; }
      e.preventDefault();
      this.#cancelPress();
      this.openMenu(card);
    });
    // The tap that ends a long press doesn't fold the card.
    c.addEventListener('click', (e) => {
      if (this.#swallowClick && e.target.closest?.('.fold')) { this.#swallowClick = false; e.preventDefault(); e.stopPropagation(); }
    }, true);
    c.addEventListener('pointerdown', (e) => {
      if (e.target.closest?.('.drag-handle')) { this.#drag(e); return; }
      if (e.pointerType === 'touch' && e.target.closest?.('.fold')) { this.#longPress(e); }
    });
  }

  // A long press on a card's title opens its menu; the tap it ends with doesn't fold the card.
  #longPress(e) {
    const card = this.#cardOf(e.target);
    if (!card) { return; }
    this.#cancelPress();
    const press = { id: e.pointerId, x: e.clientX, y: e.clientY };
    press.timer = setTimeout(() => {
      // Its listeners go with it: #cancelPress() finds no press once this one has fired.
      press.off();
      this.#press = null;
      this.#swallowClick = true;
      // A click may never come (the finger slid off): don't let one swallow the next tap.
      setTimeout(() => { this.#swallowClick = false; }, 800);
      if (!this.#menu.open) { this.openMenu(card); }
    }, LONG_PRESS_MS);
    const moved = (ev) => {
      if (ev.pointerId === press.id && Math.hypot(ev.clientX - press.x, ev.clientY - press.y) > LONG_PRESS_SLOP) { this.#cancelPress(); }
    };
    const up = (ev) => { if (ev.pointerId === press.id) { this.#cancelPress(); } };
    document.addEventListener('pointermove', moved);
    document.addEventListener('pointerup', up);
    document.addEventListener('pointercancel', up);
    press.off = () => {
      document.removeEventListener('pointermove', moved);
      document.removeEventListener('pointerup', up);
      document.removeEventListener('pointercancel', up);
    };
    this.#press = press;
  }

  #cancelPress() {
    if (!this.#press) { return; }
    clearTimeout(this.#press.timer);
    this.#press.off();
    this.#press = null;
  }

  // A drag by the handle, with a mouse or pen, in Your order: a line shows where the row will
  // go, and Escape puts it back.
  #drag(e) {
    const card = this.#cardOf(e.target);
    if (!card || e.button !== 0 || e.pointerType === 'touch' || this.order.sort !== 'yours') { return; }
    e.preventDefault();
    const el = card.element;
    el.classList.add('dragging');
    let drop = null;
    const mark = (next) => {
      if (drop?.el === next?.el && drop?.after === next?.after) { return; }
      drop?.el.classList.remove('drop-before', 'drop-after');
      drop = next;
      drop?.el.classList.add(drop.after ? 'drop-after' : 'drop-before');
    };
    const move = (ev) => {
      if (ev.pointerId !== e.pointerId) { return; }
      // Rows are a grid of columns on a wide screen: the row under the pointer, in both directions.
      const over = [...this.container.querySelectorAll(':scope > .session')].find((s) => {
        const r = s.getBoundingClientRect();
        return ev.clientX >= r.left - 7 && ev.clientX <= r.right + 7 && ev.clientY >= r.top - 7 && ev.clientY <= r.bottom + 7;
      });
      if (!over || over === el) { mark(null); return; }
      const r = over.getBoundingClientRect();
      mark({ el: over, after: ev.clientY > r.top + r.height / 2 });
    };
    const finish = (commit) => {
      document.removeEventListener('pointermove', move);
      document.removeEventListener('pointerup', up);
      document.removeEventListener('pointercancel', cancel);
      document.removeEventListener('keydown', esc, true);
      el.classList.remove('dragging');
      const target = drop;
      mark(null);
      if (commit && target && this.order.place(this.#keyOf(card), target.el.dataset.key, target.after)) {
        this.render();
        this.#say('Moved');
      }
    };
    const up = (ev) => { if (ev.pointerId === e.pointerId) { finish(true); } };
    const cancel = (ev) => { if (ev.pointerId === e.pointerId) { finish(false); } };
    const esc = (ev) => { if (ev.key === 'Escape') { ev.preventDefault(); finish(false); } };
    document.addEventListener('pointermove', move);
    document.addEventListener('pointerup', up);
    document.addEventListener('pointercancel', cancel);
    document.addEventListener('keydown', esc, true);
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

// The update block under a machine that offers updates (#51): what its app is doing, how the
// last update ended, Check and Install, and a refused press's reason.
function updateRow(updates, id, name, now) {
  const s = updates?.state(id);
  if (!s) { return { key: null, el: null }; }
  const { busy, note } = updates.stateOf(id);
  const said = updateLine(s, now);
  const last = lastLine(s, now);
  const check = canCheck(s) && !busy;
  const install = canInstall(s) && !busy;
  const key = [said, last, note, busy, check, install];
  // Not disabled when it cannot be pressed: a disabled button loses focus, and the next draw
  // could not give it back. A press then does nothing (UpdateControl, and the host's own rules).
  const button = (cls, label, aria, can, kind) => h('button', {
    type: 'button', class: `button secondary ${cls}`, 'aria-label': aria,
    'aria-disabled': can ? null : 'true',
    onclick: () => { if (can) { updates.press(id, kind); } },
  }, label);
  return {
    key,
    el: h('div', { class: 'machine-update' },
      h('p', { class: 'machine-update-line' }, said),
      last ? h('p', { class: 'machine-update-last' }, last) : null,
      note ? h('p', { class: 'machine-error', role: 'alert' }, note) : null,
      s.installer ? h('div', { class: 'machine-update-actions' },
        button('machine-update-check', busy ? 'Asking…' : 'Check', `Check ${name} for updates`, check, 'check'),
        s.available ? button('machine-update-install', 'Install', `Install ${s.available} on ${name} when no session there is busy`, install, 'install') : null) : null),
  };
}

export function renderMachines(list, { presence, me, current, onSelect, status = () => '', known = null, forget = null, updates = null, now = Date.now(), allStatus = '' }) {
  const hosts = presence.filter((d) => d.id !== me && d.kind === 'host');
  const all = current === ALL;
  const offline = offlineMachines({ presence, known, me });
  const rows = new Map(hosts.map((d) => [d.id, updateRow(updates, d.id, d.name, now)]));
  const updating = hosts.filter((d) => rows.get(d.id).el);
  // Drawn again only when something in it changes. It is asked to redraw on every session
  // update of every machine shown, and a button replaced between a tap's touch and its release
  // takes no click: a tap at startup, with every machine sending its sessions, was lost.
  const key = JSON.stringify([current, allStatus, hosts.map((d) => [d.id, d.name, status(d.id), rows.get(d.id).key]),
    // lastSeen itself, not only how it reads: Forget sends it, to tell a machine that came back.
    offline.map((k) => [k.id, k.name, k.lastSeen, ago(k.lastSeen, now), forget?.stateOf(k.id) ?? null]), !!forget]);
  if (drawnAs.get(list) === key) { return hosts.length; }
  drawnAs.set(list, key);
  // Every change redraws the list, so the button that had focus is replaced: Forget's first
  // press redraws it armed, and a keyboard's second press must land on the new one.
  const active = list.ownerDocument?.activeElement;
  const had = active && list.contains(active) ? { id: active.closest('li')?.dataset.deviceId, cls: ['machine-forget', 'machine-button', 'machine-update-check', 'machine-update-install', 'machine-check-all'].find((c) => active.classList.contains(c)) } : null;
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
      h('span', { class: 'machine-state' }, selected ? 'Viewing' : 'Online')),
      rows.get(d.id).el);
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
  }), ...(updating.length ? [h('li', { class: 'machine machine-updates', dataset: { deviceId: 'check-all' } },
    h('button', {
      type: 'button', class: 'button secondary machine-check-all',
      onclick: () => updates.checkAll(updating.map((d) => d.id)),
    }, updating.length > 1 ? 'Check all machines for updates' : 'Check for updates'))] : []));
  if (had?.id && had.cls) {
    const again = [...list.children].find((li) => li.dataset.deviceId === had.id)?.querySelector(`.${had.cls}`);
    again?.focus();
  }
  return hosts.length;
}
