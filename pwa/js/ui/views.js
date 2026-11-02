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

  // What search narrows the list to (the app bar's search, ui.md §7): a session whose title,
  // repository, branch, folder, agent or machine has it. While it narrows, rows don't move: a
  // move among the rows shown would also pass rows hidden between them.
  setFilter(query) {
    const q = String(query ?? '').trim().toLowerCase();
    if (q === this.filter) { return; }
    this.filter = q;
    this.render();
  }

  filter = '';

  #matches(hostId, s) {
    if (!this.filter) { return true; }
    const m = this.machine(hostId);
    return [s.title, s.repository, s.branch, s.cwd, s.agent, m.name].some((v) => typeof v === 'string' && v.toLowerCase().includes(this.filter));
  }

  get moving() { return !this.filter && this.order.sort === 'yours'; }

  render() {
    const entries = this.sessions.list();
    const sort = this.order.sort;
    const arranged = this.order.arrange(entries, { nameOf: (id) => this.machine(id).name, shows: this.shows, settled: this.settled });
    const keep = (e) => this.#matches(e.hostId, e.session);
    const ended = arranged.ended.filter(keep);
    this.container.dataset.sort = this.order.sort;
    this.container.dataset.filtered = this.filter ? 'true' : 'false';
    this.onSorted?.();
    const live = new Set();
    // Moving a card takes focus out of it, and a card being answered moves when its
    // session updates in Most recent: the text box being typed in keeps focus and the caret.
    const focused = this.container.contains(document.activeElement) ? document.activeElement : null;
    const caret = focused && typeof focused.selectionStart === 'number' ? [focused.selectionStart, focused.selectionEnd] : null;
    const want = [];
    // Search only hides a card: one it leaves out keeps its card, with an unsent reply, files
    // attached or a question half answered, kept current for when search lets it back in.
    let shown = 0;
    const cards = (list, isPinned) => {
      for (const entry of list) {
        const { hostId, session: s } = entry;
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
        if (!keep(entry)) { continue; }
        // Open on screen, its news is seen as it comes; folded, it gets the dot.
        if (!card.folded) { this.order.markSeen(key, s.updatedAt); }
        card.setUnread(this.order.unread(key, s.updatedAt));
        want.push(card.element);
        shown += 1;
      }
    };
    const pinnedShown = arranged.pinned.some(keep);
    const sessionsShown = arranged.sessions.some(keep);
    if (pinnedShown) { want.push(this.#head('pinned', 'Pinned')); }
    cards(arranged.pinned, true);
    if (pinnedShown && sessionsShown) { want.push(this.#head('sessions', 'Sessions')); }
    cards(arranged.sessions, false);
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
    this.shownCount = shown;
    this.onRender?.(entries.length);
    // A save that failed while drawing (storage full) turned the list to Most recent: draw it so.
    if (this.order.sort !== sort) { this.schedule(); }
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
    if (this.filter || !this.order.move(this.#keyOf(card), delta)) { return false; }
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
      { label: 'Move up', disabled: !!this.filter || !this.order.canMove(key, -1), run: () => this.#move(card, -1) },
      { label: 'Move down', disabled: !!this.filter || !this.order.canMove(key, 1), run: () => this.#move(card, 1) },
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
      // The tap the finger ends with must not fold the card, however long it is held; once it has
      // lifted, a click that never came (the finger slid off) mustn't swallow the next tap.
      const lifted = (ev) => {
        if (ev.pointerId !== press.id) { return; }
        document.removeEventListener('pointerup', lifted, true);
        document.removeEventListener('pointercancel', lifted, true);
        setTimeout(() => { this.#swallowClick = false; }, 400);
      };
      document.addEventListener('pointerup', lifted, true);
      document.addEventListener('pointercancel', lifted, true);
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
    if (!card || e.button !== 0 || e.pointerType === 'touch' || !this.moving) { return; }
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
  // Whether search is hiding a card: it exists, and is not in the list.
  hidden(card) { return !!card && !card.element.isConnected; }

  card(hostId, id) {
    if (hostId) { return this.#cards.get(SessionListView.key(hostId, id)) ?? null; }
    for (const c of this.#cards.values()) { if (c.id === id) { return c; } }
    return null;
  }
}

// The machine tabs above the sessions (ui.md §6.1, Windows and the PWA: SelectorBar-style text
// tabs): All, once more than one machine is online, then each online machine, and a machine
// still chosen while it is away. Hidden when there is nothing to choose between. Arrow keys,
// Home and End move between them, choosing as they go, as a SelectorBar does.
const tabsDrawnAs = new WeakMap();

export function renderMachineTabs(bar, { presence, me, current, nameOf = () => null, onSelect, controls = null }) {
  const hosts = presence.filter((d) => d.id !== me && d.kind === 'host');
  const all = current === ALL;
  const tabs = [
    ...(hosts.length > 1 ? [{ id: ALL, name: 'All', label: 'All machines' }] : []),
    ...hosts.map((d) => ({ id: d.id, name: d.name, label: d.name })),
    ...(current && !all && !hosts.some((d) => d.id === current) ? [{ id: current, name: nameOf(current) ?? 'This machine', label: nameOf(current) ?? 'This machine', away: true }] : []),
  ];
  const chosen = (t) => t.id === current || (all && hosts.length === 1 && t.id === hosts[0].id);
  // One machine and it is the one shown: there is nothing to choose.
  const show = tabs.length > 1 || (tabs.length === 1 && !chosen(tabs[0]));
  const key = JSON.stringify([show, current, tabs.map((t) => [t.id, t.name, !!t.away])]);
  if (tabsDrawnAs.get(bar) === key) { return; }
  tabsDrawnAs.set(bar, key);
  bar.hidden = !show;
  const had = bar.contains(bar.ownerDocument?.activeElement) ? bar.ownerDocument.activeElement.dataset.deviceId : null;
  bar.replaceChildren(...tabs.map((t) => {
    const on = chosen(t);
    return h('button', {
      type: 'button', role: 'tab', class: `machine-tab${t.away ? ' away' : ''}`,
      'aria-selected': on ? 'true' : 'false', tabindex: on ? '0' : '-1',
      'aria-controls': controls, 'aria-label': t.away ? `${t.label}, offline` : t.label,
      dataset: { deviceId: t.id, deviceName: t.label },
      // Picked again, a machine that can't be reached is tried again at once.
      onclick: () => onSelect(t.id),
    }, t.name);
  }));
  if (!bar.querySelector('[tabindex="0"]')) { bar.firstElementChild?.setAttribute('tabindex', '0'); }
  if (had) { [...bar.children].find((b) => b.dataset.deviceId === had)?.focus(); }
  if (!bar.dataset.keys) {
    bar.dataset.keys = '1';
    bar.addEventListener('keydown', (e) => {
      const list = [...bar.querySelectorAll('[role="tab"]')];
      const i = list.indexOf(e.target);
      if (i < 0) { return; }
      const to = { ArrowRight: i + 1, ArrowLeft: i - 1, Home: 0, End: list.length - 1 }[e.key];
      if (to === undefined) { return; }
      e.preventDefault();
      const next = list[(to + list.length) % list.length];
      next.focus();
      next.click();
    });
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

// The update block inside a machine's row, under its two lines (#51, #213): what its app is
// doing, how the last update ended, Check and Install, and a refused press's reason.
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
    type: 'button', class: `button secondary small ${cls}`, 'aria-label': aria,
    'aria-disabled': can ? null : 'true',
    onclick: () => { if (can) { updates.press(id, kind); } },
  }, label);
  return {
    key,
    el: h('div', { class: 'machine-update' },
      h('p', { class: 'machine-update-line' }, said),
      last ? h('p', { class: 'machine-update-last' }, last) : null,
      note ? h('p', { class: 'machine-error', role: 'alert' }, note) : null,
      s.installer ? h('div', { class: 'machine-actions machine-update-actions' },
        button('machine-update-check', busy ? 'Asking…' : 'Check', `Check ${name} for updates`, check, 'check'),
        s.available ? button('machine-update-install', 'Install', `Install ${s.available} on ${name} when no session there is busy`, install, 'install') : null) : null),
  };
}

// A machine's second line (ui.md §6.1, #213): at most two facts, never a sentence or an id. Its
// sessions, then its version, or in its place "View only" or "Discovery held", whose reasons the
// machine gives in full are in the line's tooltip and said to a screen reader.
// facts: { sessions, version, readOnly, held } (usage.js machineFacts), or null.
function factsLine(facts) {
  if (!facts) { return { el: h('span', { class: 'machine-status' }), key: '' }; }
  const flag = facts.held ? ['held', 'Discovery held', `discovery held: ${facts.held}`]
    : facts.readOnly ? ['view-only', 'View only', `view only: ${facts.readOnly}`] : null;
  const shown = [facts.sessions, flag ? null : facts.version].filter(Boolean);
  const detail = [facts.sessions, facts.version, facts.readOnly ? `view only: ${facts.readOnly}` : null, facts.held ? `discovery held: ${facts.held}` : null].filter(Boolean).join(' \u00b7 ');
  const el = h('span', { class: 'machine-status', title: detail || null },
    shown.join(' \u00b7 '),
    flag ? [shown.length ? ' \u00b7 ' : '', h('span', { class: `machine-flag ${flag[0]}` }, flag[1], h('span', { class: 'sr-only' }, `: ${flag[2].replace(/^[^:]+: /, '')}`))] : null);
  return { el, key: detail };
}

// One row: the icon and the trailing state on line 1 with the name, the facts on line 2, and
// anything more (updates, Forget, an error) under them in the same text column.
function machineRow({ id, name, select = null, selected = false, facts, state, offline = false, more = [] }) {
  const parts = [
    icon('machine'),
    h('span', { class: 'machine-name' }, name),
    h('span', { class: 'machine-state' }, state),
    h('span', { class: 'machine-sub' }, facts),
  ];
  const head = select
    ? h('button', { type: 'button', class: `machine-button${selected ? ' selected' : ''}`, 'aria-current': selected ? 'true' : null, onclick: select }, parts)
    : h('div', { class: 'machine-row' }, parts);
  const extra = more.filter(Boolean);
  return h('li', { class: `machine${offline ? ' offline' : ''}${selected ? ' selected' : ''}`, dataset: { deviceId: id, deviceName: name } },
    head, extra.length ? h('div', { class: 'machine-more' }, extra) : null);
}

export function renderMachines(list, { presence, me, current, onSelect, status = () => '', facts = null, known = null, forget = null, updates = null, now = Date.now(), allStatus = '' }) {
  const hosts = presence.filter((d) => d.id !== me && d.kind === 'host');
  const all = current === ALL;
  const offline = offlineMachines({ presence, known, me });
  const rows = new Map(hosts.map((d) => [d.id, updateRow(updates, d.id, d.name, now)]));
  const updating = hosts.filter((d) => rows.get(d.id).el);
  // A machine's facts, from facts(id) where given, else its status line as one fact.
  const lineOf = (id) => {
    if (facts) { return factsLine(facts(id)); }
    const said = status(id);
    return { el: h('span', { class: 'machine-status' }, said), key: said };
  };
  const lines = new Map(hosts.map((d) => [d.id, lineOf(d.id)]));
  // Drawn again only when something in it changes. It is asked to redraw on every session
  // update of every machine shown, and a button replaced between a tap's touch and its release
  // takes no click: a tap at startup, with every machine sending its sessions, was lost.
  const key = JSON.stringify([current, allStatus, hosts.map((d) => [d.id, d.name, lines.get(d.id).key, rows.get(d.id).key]),
    // lastSeen itself, not only how it reads: Forget sends it, to tell a machine that came back.
    offline.map((k) => [k.id, k.name, k.lastSeen, ago(k.lastSeen, now), forget?.stateOf(k.id) ?? null]), !!forget]);
  if (drawnAs.get(list) === key) { return hosts.length; }
  drawnAs.set(list, key);
  // Every change redraws the list, so the button that had focus is replaced: Forget's first
  // press redraws it armed, and a keyboard's second press must land on the new one.
  const active = list.ownerDocument?.activeElement;
  const had = active && list.contains(active) ? { id: active.closest('li')?.dataset.deviceId, cls: ['machine-forget', 'machine-button', 'machine-update-check', 'machine-update-install', 'machine-check-all'].find((c) => active.classList.contains(c)) } : null;
  const everyMachine = hosts.length > 1 ? [machineRow({
    id: ALL, name: 'All machines', select: () => onSelect(ALL), selected: all,
    facts: h('span', { class: 'machine-status' }, allStatus || `${hosts.length} machines`),
    state: all ? 'Viewing' : '',
  })] : [];
  list.replaceChildren(...everyMachine, ...hosts.map((d) => {
    const selected = d.id === current || (all && hosts.length === 1);
    return machineRow({
      id: d.id, name: d.name, select: () => onSelect(d.id), selected,
      facts: lines.get(d.id).el, state: selected ? 'Viewing' : 'Online', more: [rows.get(d.id).el],
    });
  }), ...offline.map((k) => {
    const name = typeof k.name === 'string' && k.name ? k.name : 'unnamed';
    const seen = ago(k.lastSeen, now);
    const s = forget?.stateOf(k.id) ?? { armed: false, busy: false, error: null };
    return machineRow({
      id: k.id, name, offline: true,
      facts: h('span', { class: 'machine-seen' }, `Last seen ${seen || 'a while ago'}`),
      state: 'Offline',
      more: [
        forget ? h('div', { class: 'machine-actions' }, h('button', {
          type: 'button', class: `button secondary small machine-forget${s.armed ? ' armed' : ''}`,
          'aria-label': s.armed ? `Forget ${name}: press again to confirm` : `Forget ${name}`,
          // Not disabled while it goes: a disabled button loses focus, and the next draw could not
          // give it back. A press meanwhile does nothing (forget.js).
          'aria-disabled': s.busy ? 'true' : null,
          onclick: () => forget.press(k.id, k.lastSeen),
        }, s.busy ? 'Forgetting\u2026' : s.armed ? 'Press again to forget' : 'Forget')) : null,
        s.error ? h('p', { class: 'machine-error', role: 'alert' }, s.error) : null,
      ],
    });
  }), ...(updating.length ? [h('li', { class: 'machine-updates', dataset: { deviceId: 'check-all' } },
    h('button', {
      type: 'button', class: 'button secondary small machine-check-all',
      onclick: () => updates.checkAll(updating.map((d) => d.id)),
    }, updating.length > 1 ? 'Check all machines for updates' : 'Check for updates'))] : []));
  if (had?.id && had.cls) {
    const again = [...list.children].find((li) => li.dataset.deviceId === had.id)?.querySelector(`.${had.cls}`);
    again?.focus();
  }
  return hosts.length;
}
