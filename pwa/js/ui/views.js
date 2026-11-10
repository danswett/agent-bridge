// The two lists on screen: online machines, and the sessions of the machines shown.
import { h, icon } from './dom.js';
import { ALL } from '../host-session.js';
import { SessionCard } from './session-card.js';
import { RowMenu } from './row-menu.js';
import { SessionOrder } from '../session-order.js';
import { ago } from './time.js';
import { whileVisible } from './ticker.js';
import { enter, leave, seen, select as showSelection, slide } from './motion.js';
import { updateLine, lastLine, canCheck, canInstall, canUpdateNow, updateNowReason, RESTARTING } from '../update.js';
import { openUpdateNowSheet } from './update-now-sheet.js';
import { signInDevicesLines } from '../signin-devices.js';
import { BRAND } from '../brand.js';

const LONG_PRESS_MS = 500;
// A finger that moves further than this is scrolling, not pressing.
const LONG_PRESS_SLOP = 10;
const titleOf = (card) => card.session.title || 'Untitled session';

// Keeps one row per session, from every machine shown, in the person's order (session-order.js):
// Pinned, then the rest, then an Ended group (ui.md §6.1); and each session's view in the session
// pane, the selected one shown (./session-card.js). Both are updated in place so an open
// disclosure, scroll position or focus survives an upsert, and an upsert never moves a row. A
// session is its machine's: two machines may each have a session of the same id, and each acts
// only on its own machine.
//
// Rows move only when the person moves them: Alt+Up/Down on a row, a drag by the handle that
// shows on hover, or the row menu (a long press on a phone, a right-click, Shift+F10). Up and
// Down alone go from row to row.
export class SessionListView {
  #cards = new Map();
  #scheduled = false;
  #menu = new RowMenu();
  #heads = {};
  #ended = null;
  #press = null;
  #swallowClick = false;
  #selected = null;
  #grab = null;
  #held = false;
  // Drawn once: what comes after is news, and may move.
  #drawn = false;

  get drawn() { return this.#drawn; }

  // sessions: { list() -> [{ hostId, session }], onChange } (HostSession.sessions).
  // fetchDetail(hostId, id) -> Promise<Session|null>; fetchDelegate(hostId, id, agent) ->
  // Promise<Subagent|null>, one delegate's own words; machine(hostId) -> { name, show }: what to
  // call the machine, and whether its rows say so (with every machine shown).
  // answering(hostId) -> { service, replies, stops, hostId, capabilities, readOnly } | null.
  // order: the person's order (SessionOrder). shows(hostId): whether a machine is shown.
  // settled(hostId): whether a machine has sent its whole list yet. views: where the sessions'
  // views go (the session pane). layout() -> { inspector }: whether the inspector is a pane now.
  // viewing() -> whether the selected session's view is on screen (on a phone, only once pushed).
  // stale(hostId) -> why a machine's sessions can't be acted on now ({ reason, since }), or null.
  // heading() -> the list's heading, where focus goes when a session that had it ends with no
  // row left to take it.
  // history: the HistoryStore the cards draw their earlier conversation from, or null.
  constructor(container, sessions, { history = null, fetchDetail, fetchDelegate = null, machine = () => ({ name: '', show: false }), answering = () => null, order = new SessionOrder(), shows = () => true, settled = () => true, views = null, layout = () => ({ inspector: false }), viewing = () => true, stale = () => null, heading = () => null } = {}) {
    Object.assign(this, { container, sessions, history, fetchDetail, fetchDelegate, machine, answering, order, shows, settled, layout, viewing, stale, heading });
    this.views = views ?? h('div');
    sessions.onChange((change) => {
      this.order.observe(change);
      if (change?.type === 'synced' && change.hostId) {
        this.order.synced(change.hostId, new Set(sessions.list().filter((e) => e.hostId === change.hostId).map((e) => e.session.id)));
      }
      this.schedule();
    });
    // Relative times move on without any event from the host, while anyone can see them.
    this.ticker = whileVisible(() => { for (const c of this.#cards.values()) { c.refreshTimes(); } }, 15000);
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

  // While a row is grabbed nothing else rearranges: a redraw waits for the drop. The one thing
  // that doesn't wait is the grabbed session ending, or the list no longer being movable.
  render() {
    if (this.#grab) {
      const gone = !this.sessions.list().some((e) => SessionListView.key(e.hostId, e.session.id) === this.#grab.key);
      if (!gone && this.moving) { this.#held = true; return; }
      this.#grab = null;
      this.#say('Move cancelled.');
    }
    this.#draw();
  }

  #draw() {
    const entries = this.sessions.list();
    const sort = this.order.sort;
    const arranged = this.order.arrange(entries, { nameOf: (id) => this.machine(id).name, shows: this.shows, settled: this.settled });
    const keep = (e) => this.#matches(e.hostId, e.session);
    const ended = arranged.ended.filter(keep);
    this.container.dataset.sort = this.order.sort;
    this.container.dataset.filtered = this.filter ? 'true' : 'false';
    this.onSorted?.();
    const live = new Set([...arranged.pinned, ...arranged.sessions].map((e) => SessionListView.key(e.hostId, e.session.id)));
    // Moving a card takes focus out of it, and a card being answered moves when its
    // session updates in Most recent: the text box being typed in keeps focus and the caret.
    const focused = this.container.contains(document.activeElement) ? document.activeElement : null;
    const caret = focused && typeof focused.selectionStart === 'number' ? [focused.selectionStart, focused.selectionEnd] : null;
    // The rows as they were, for where focus goes if the session that had it ends (below).
    const active = document.activeElement;
    const rowsWere = [...this.container.querySelectorAll('.session')];
    let goneAt = null;
    const want = [];
    // Search only hides a card: one it leaves out keeps its card, with an unsent reply, files
    // attached or a question half answered, kept current for when search lets it back in.
    let shown = 0;
    // Arriving and leaving (ui.md §5.3): a session that came or went after its machine's list was
    // placed fades, where it is; its neighbours take their places at once. A machine's first list,
    // a machine still sending its list, a machine no longer shown, and search (which only hides a
    // card) are none of that.
    const arrived = [];
    for (const [key, card] of this.#cards) {
      if (!live.has(key) && this.#drawn && this.shows(card.hostId) && this.settled(card.hostId)) { leave(card.element, 'listItem'); }
    }
    const cards = (list, isPinned) => {
      for (const entry of list) {
        const { hostId, session: s } = entry;
        const key = SessionListView.key(hostId, s.id);
        let card = this.#cards.get(key);
        if (!card) {
          if (this.#drawn && this.settled(hostId)) { arrived.push(key); }
          const m = this.machine(hostId);
          card = new SessionCard(s, {
            hostId, history: this.history, fetchDetail: (id) => this.fetchDetail(hostId, id),
            fetchDelegate: this.fetchDelegate ? (id, agent) => this.fetchDelegate(hostId, id, agent) : null,
            machineName: m.name, labelled: m.show, answering: () => this.answering(hostId),
            stale: this.stale(hostId),
            onSelect: (c) => this.onSelect?.(c),
            onPin: (c, pinned) => this.#pin(c, pinned, c.els.pin),
            onBack: (c) => this.onBack?.(c),
            layout: this.layout,
            viewing: () => this.viewing(),
          });
          card.onInspector = () => this.onInspector?.();
          card.onDelegate = () => this.onDelegate?.(card);
          card.element.dataset.key = key;
          card.view.dataset.key = key;
          // A real button, right after the row's button in the tab order: Space or Enter grabs the row.
          card.handle = h('button', { type: 'button', class: 'drag-handle', title: 'Drag to move' });
          card.element.append(card.handle);
          this.views.append(card.view);
          this.#cards.set(key, card);
        } else if (card.session !== s) {
          card.update(s);
        }
        card.setPinned(isPinned);
        card.handle.setAttribute('aria-label', `Move ${titleOf(card)}`);
        card.setStale(this.stale(hostId));
        // On screen, its news is seen as it comes; otherwise its row gets the dot. A session's
        // view stays on screen while search hides its row, so this comes before search's say.
        if (card.selected && this.viewing()) { this.order.markSeen(key, s.updatedAt); }
        if (!keep(entry)) { continue; }
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
      if (!live.has(key)) {
        if (active && (card.element.contains(active) || card.view.contains(active))) { goneAt = rowsWere.indexOf(card.element); }
        card.dispose();
        this.history?.forget(card.hostId, card.id);
        card.element.remove();
        card.view.remove();
        this.#cards.delete(key);
        if (this.#selected === key) { this.#selected = null; }
      }
    }
    if (focused && focused.isConnected && document.activeElement !== focused) {
      focused.focus({ preventScroll: true });
      if (caret) { try { focused.setSelectionRange(...caret); } catch { /* not a text control */ } }
    }
    if (goneAt !== null) { this.#refocus(rowsWere, goneAt); }
    for (const key of arrived) {
      const el = this.#cards.get(key)?.element;
      if (el?.isConnected) { enter(el, 'listItem'); }
    }
    this.#drawn = true;
    // An ended session that matches is a result too: Ended shows it.
    this.shownCount = shown + ended.length;
    this.onRender?.(entries.length);
    // A save that failed while drawing (storage full) turned the list to Most recent: draw it so.
    if (this.order.sort !== sort) { this.schedule(); }
  }

  // A session that had focus (its row, or its view: End session pressed twice) has ended, and its
  // focus went with it to the page, so the next Tab began at the top (§9.1 A4, #369). It goes to
  // the row that takes its place, or the one before it if it was last, or else the list's
  // heading. Only to one in sight: on a phone the list is under the session's view, and going
  // back to it (sessions-page.js) gives focus back there.
  #refocus(rowsWere, at) {
    const doc = this.container.ownerDocument;
    // Unless focus went somewhere real meanwhile. The list's own ancestors don't count: WebKit
    // can give lost focus to the Sessions section.
    const now = doc.activeElement;
    if (now && now !== doc.body && now.isConnected && !now.contains(this.container)) { return; }
    const shown = (r) => this.container.contains(r) && r.getClientRects().length > 0;
    const next = rowsWere.slice(at + 1).find(shown) ?? rowsWere.slice(0, Math.max(at, 0)).reverse().find(shown);
    let to = next?.querySelector('.row-main');
    if (!to) {
      const heading = this.heading();
      if (!heading?.getClientRects().length) { return; }
      if (!heading.hasAttribute('tabindex')) { heading.tabIndex = -1; }
      to = heading;
    }
    to.focus({ preventScroll: true });
    to.scrollIntoView?.({ block: 'nearest' });
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
    // Into view before the slide is measured: the slide's first frame puts the row back where it
    // was, and a scroll then would find it already in sight and leave its new place clipped.
    this.#moved(card.element, () => { this.render(); card.element.scrollIntoView({ block: 'nearest' }); });
    card.els.open.focus({ preventScroll: true });
    this.#say(delta < 0 ? 'Moved up' : 'Moved down');
    return true;
  }

  // Reorder (ui.md §5.3): the row moved slides to its new place; the rows it passed take theirs
  // at once, so nothing but the person's row moves.
  #moved(el, draw) {
    const before = el.isConnected ? el.getBoundingClientRect() : null;
    // A move made while the last one still slides goes on from where the row is seen now: the
    // last slide goes, so the new place is measured without its translation.
    for (const a of el.getAnimations?.() ?? []) { if (a.id === 'ab-motion-reorder') { a.cancel(); } }
    draw();
    if (!before || !el.isConnected) { return; }
    const after = el.getBoundingClientRect();
    slide(el, 'reorder', before.left - after.left, before.top - after.top);
  }

  // back: what has focus after, the row's own button unless the header's Pin was pressed.
  #pin(card, pinned, back = card.els.open) {
    this.order.setPinned(this.#keyOf(card), pinned);
    this.render();
    back.focus({ preventScroll: true });
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
    ], { back: card.els.open, label: `${card.session.title || 'Session'}: actions` });
  }

  #listen() {
    const c = this.container;
    c.addEventListener('keydown', (e) => {
      const handle = e.target.closest?.('.drag-handle');
      if (handle) {
        const card = this.#cardOf(handle);
        if (!card || !this.#grabbed(card)) { return; }
        if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); this.#endGrab(card, false); return; }
        if ((e.key === 'ArrowUp' || e.key === 'ArrowDown') && !e.altKey && !e.ctrlKey && !e.metaKey && !e.shiftKey) {
          e.preventDefault();
          this.#grabStep(card, e.key === 'ArrowUp' ? -1 : 1);
        }
        return;
      }
      const card = e.target.closest?.('.row-main') ? this.#cardOf(e.target) : null;
      if (!card) { return; }
      // The context-menu key, or Shift+F10: Safari has neither as a contextmenu event.
      if (e.key === 'ContextMenu' || (e.key === 'F10' && e.shiftKey && !e.altKey && !e.ctrlKey && !e.metaKey)) {
        e.preventDefault();
        this.openMenu(card);
        return;
      }
      if (e.ctrlKey || e.metaKey || e.shiftKey || (e.key !== 'ArrowUp' && e.key !== 'ArrowDown')) { return; }
      e.preventDefault();
      if (e.altKey) { if (!this.#grab) { this.#move(card, e.key === 'ArrowUp' ? -1 : 1); } return; }
      // Up and Down alone go between rows, as a list does (ui.md §9).
      const rows = [...this.container.querySelectorAll('.session > .row-main')];
      rows[rows.indexOf(card.els.open) + (e.key === 'ArrowUp' ? -1 : 1)]?.focus();
    });
    // Enter, and Space on its key-up, arrive as a click with no pointer (detail 0). A mouse click is
    // not a grab: the handle is also where a drag starts.
    c.addEventListener('click', (e) => {
      const handle = e.target.closest?.('.drag-handle');
      const card = handle ? this.#cardOf(handle) : null;
      if (card && e.detail === 0) { this.#toggleGrab(card); }
    });
    // Focus going elsewhere drops the row where it is, so a grab never holds the list for good.
    c.addEventListener('focusout', (e) => {
      const handle = e.target.closest?.('.drag-handle');
      const card = handle ? this.#cardOf(handle) : null;
      if (!card || !this.#grabbed(card)) { return; }
      setTimeout(() => {
        const now = document.activeElement;
        if (this.#grabbed(card) && now !== handle && now !== document.body) { this.#endGrab(card, true); }
      }, 0);
    });
    c.addEventListener('contextmenu', (e) => {
      const card = e.target.closest?.('.row-main') ? this.#cardOf(e.target) : null;
      if (!card) { return; }
      e.preventDefault();
      this.#cancelPress();
      this.openMenu(card);
    });
    // The tap that ends a long press doesn't open the session.
    c.addEventListener('click', (e) => {
      if (this.#swallowClick && e.target.closest?.('.row-main')) { this.#swallowClick = false; e.preventDefault(); e.stopPropagation(); }
    }, true);
    c.addEventListener('pointerdown', (e) => {
      if (e.target.closest?.('.drag-handle')) { this.#drag(e); return; }
      if (e.pointerType === 'touch' && e.target.closest?.('.row-main')) { this.#longPress(e); }
    });
  }

  // A long press on a row opens its menu; the tap it ends with doesn't open the session.
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
      // The tap the finger ends with must not open the session, however long it is held; once it has
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
        this.#moved(el, () => this.render());
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

  // Grab, move, drop (ui.md §6.1): Space or Enter on the Move button grabs its row, Up and Down
  // move it one place in its group, Space or Enter drops it, Escape puts it back. Focus stays
  // on the button, and each step is said once, politely.
  #grabbed(card) { return this.#grab?.key === this.#keyOf(card); }

  #where(card) {
    const p = this.order.position(this.#keyOf(card));
    return p ? { ...p, text: `position ${p.at} of ${p.of}` } : null;
  }

  #toggleGrab(card) {
    if (this.#grabbed(card)) { this.#endGrab(card, true); return; }
    const key = this.#keyOf(card);
    const where = this.#where(card);
    if (this.#grab || !this.moving || !where) { return; }
    this.#grab = { key, from: this.order.neighbours(key), edge: null };
    this.#say(`Grabbed ${titleOf(card)}. Position ${where.at} of ${where.of} in ${where.group}. Use Up and Down to move, Space to drop, Escape to cancel.`);
  }

  #grabStep(card, delta) {
    if (!this.order.move(this.#keyOf(card), delta)) {
      const where = this.#where(card);
      const edge = delta < 0 || where?.of === 1 ? 'First' : 'Last';
      if (this.#grab.edge !== edge) { this.#say(`${edge} in ${where?.group ?? 'Sessions'}.`); }
      this.#grab.edge = edge;
      return;
    }
    this.#grab.edge = null;
    this.#moved(card.element, () => { this.#draw(); card.element.scrollIntoView({ block: 'nearest' }); });
    card.handle.focus({ preventScroll: true });
    const where = this.#where(card);
    this.#say(`Position ${where.at} of ${where.of}.`);
  }

  #endGrab(card, drop) {
    const { key, from } = this.#grab;
    this.#grab = null;
    if (!drop && from) {
      if (from.next) { this.order.place(key, from.next, false); } else if (from.prev) { this.order.place(key, from.prev, true); }
    }
    // The redraw the grab held off and this one are the same.
    this.#held = false;
    this.#moved(card.element, () => this.#draw());
    card.handle.focus({ preventScroll: true });
    const where = this.#where(card);
    if (!where) { return; }
    this.#say(drop ? `Dropped ${titleOf(card)} at ${where.text} in ${where.group}.` : `Move cancelled. ${titleOf(card)} is back at ${where.text}.`);
  }

  // Whether a machine takes answers may have changed: every question of its sessions (of every
  // session, without one) is drawn again, keeping what was typed.
  refreshQuestions(hostId = null) {
    for (const c of this.#cards.values()) { if (hostId === null || c.hostId === hostId) { c.refreshQuestion(); } }
  }

  // A machine's name, or whether rows name their machine, may have changed.
  refreshMachines() {
    for (const c of this.#cards.values()) { const m = this.machine(c.hostId); c.setMachine(m.name, m.show); }
    // Search matches machine names too: a renamed machine's sessions come into it or leave it.
    if (this.filter) { this.schedule(); }
  }

  // A machine went offline, came back, or sent a whole new list since (#269): its rows say so.
  refreshStale() {
    for (const c of this.#cards.values()) { c.setStale(this.stale(c.hostId)); }
  }

  // Whether search is hiding a session's row: it exists, and is not in the list.
  hidden(card) { return !!card && !card.element.isConnected; }

  // The session selected, by its key, or none: its row marked, its view the one shown.
  select(key) {
    const next = key && this.#cards.has(key) ? key : null;
    if (next !== this.#selected) {
      const old = this.selected?.els.open;
      const from = old?.isConnected ? seen(old) : null;
      this.#selected = next;
      for (const [k, c] of this.#cards) { c.setSelected(k === next); }
      // Selection (ui.md §5.3): the pill slides from the row it left when that row is in sight,
      // and fades in when it was scrolled away or hidden. A first selection has no pill to move.
      const to = next ? this.#cards.get(next).els.open : null;
      if (old?.isConnected && to) { showSelection(to, 'selection', from, { pseudo: '::before' }); }
      // Shown now, its news is seen.
      this.schedule();
    }
    return next ? this.#cards.get(next) : null;
  }

  get selected() { return this.#selected ? this.#cards.get(this.#selected) ?? null : null; }

  // Every session's card, shown or not.
  get cards() { return [...this.#cards.values()]; }

  // The rows shown, in order: what a desktop selects when nothing is chosen yet.
  shownCards() {
    return [...this.container.querySelectorAll(':scope > .session')].map((el) => this.#cards.get(el.dataset.key)).filter(Boolean);
  }

  // The inspector became a pane, or stopped being one.
  layoutChanged() { for (const c of this.#cards.values()) { c.layoutChanged(); } }

  // A machine's session; without a machine, the first for that session id.
  card(hostId, id) {
    if (hostId) { return this.#cards.get(SessionListView.key(hostId, id)) ?? null; }
    for (const c of this.#cards.values()) { if (c.id === id) { return c; } }
    return null;
  }
}

// The machine tabs above the sessions (ui.md §6.1, Windows and the PWA: SelectorBar-style text
// tabs): All, once more than one machine is online or kept on screen, then each online machine,
// then each machine kept on screen since it left (away: with every machine shown, #269), and a
// machine still chosen while it is away. Hidden when there is nothing to choose between. Arrow
// keys, Home and End move between them, choosing as they go, as a SelectorBar does.
const tabsDrawnAs = new WeakMap();

export function renderMachineTabs(bar, { presence, me, current, nameOf = () => null, onSelect, controls = null, away = [], quiet = false }) {
  const hosts = presence.filter((d) => d.id !== me && d.kind === 'host');
  const all = current === ALL;
  const awayIds = away.filter((id) => id !== me && !hosts.some((d) => d.id === id));
  const kept = all ? awayIds : [];
  // Quiet (the return grace): an away machine's tab keeps the look it had, not yet marked offline.
  const awayTab = (id) => ({ id, name: nameOf(id) ?? 'This machine', label: nameOf(id) ?? 'This machine', away: !quiet });
  const tabs = [
    // A machine chosen while it is away counts too, so All stays offered to go back to (#269).
    ...(hosts.length + awayIds.length > 1 ? [{ id: ALL, name: 'All', label: 'All machines' }] : []),
    ...hosts.map((d) => ({ id: d.id, name: d.name, label: d.name })),
    ...kept.map(awayTab),
    ...(current && !all && !hosts.some((d) => d.id === current) ? [awayTab(current)] : []),
  ];
  const sole = hosts.length + kept.length === 1 ? hosts[0]?.id ?? kept[0] : null;
  const chosen = (t) => t.id === current || (all && t.id === sole);
  // One machine and it is the one shown: there is nothing to choose.
  const show = tabs.length > 1 || (tabs.length === 1 && !chosen(tabs[0]));
  const key = JSON.stringify([show, current, tabs.map((t) => [t.id, t.name, !!t.away])]);
  if (tabsDrawnAs.get(bar) === key) { return; }
  tabsDrawnAs.set(bar, key);
  // Taken before the bar can hide: a hidden tab loses focus.
  const had = bar.contains(bar.ownerDocument?.activeElement) ? bar.ownerDocument.activeElement.dataset.deviceId : null;
  const was = bar.hidden ? null : bar.querySelector('[aria-selected="true"]');
  const from = seen(was);
  bar.hidden = !show;
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
  // Selection (ui.md §5.3): the bar under the tab slides from the one chosen before, or fades in
  // when that one was scrolled out of the bar.
  const now = bar.querySelector('[aria-selected="true"]');
  if (was && now && was.dataset.deviceId !== now.dataset.deviceId) {
    showSelection(now, 'selection', from, { pseudo: '::after', axis: 'x', scaleX: true });
  }
  // A tab with focus is drawn again with it; when the bar itself went, focus goes to what it
  // controlled (the sessions), not to the document.
  if (had && show) { [...bar.children].find((b) => b.dataset.deviceId === had)?.focus(); }
  if (had && !show) { bar.ownerDocument.getElementById('sessions')?.focus({ preventScroll: true }); }
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

// What an offline machine's row says. The relay passes on how the host said it starts (hello's
// start); a machine that never said is plainly offline.
export function startWords(start) {
  if (start === 'boot') { return 'Signed out: waiting for sign-in'; }
  if (start === 'logon') { return 'Logon only: starts when you sign in'; }
  return 'Offline';
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
// doing, how the last update ended, Check, Install and Update now (or why Update now can't be
// pressed there, #1159), and a refused press's reason.
function updateRow(updates, id, name, now) {
  const s = updates?.state(id);
  if (!s) { return { key: null, el: null }; }
  const { busy, asking, note } = updates.stateOf(id);
  // Seen leaving for an Update now while online: its row reads so once it has gone (renderMachines).
  updates.restarting?.(id, true);
  const said = updateLine(s, now);
  const last = lastLine(s, now, { updateNow: updates.isUpdateNow?.(s.last?.request) ?? false });
  const check = canCheck(s) && !busy && !asking;
  const install = canInstall(s) && !busy && !asking;
  const offerNow = updates.signs === true && canUpdateNow(s);
  const pressNow = offerNow && !busy && !asking;
  const why = updates.signs === true ? updateNowReason(s) : null;
  const key = [said, last, note, busy, asking, check, install, offerNow, pressNow, why];
  // Not disabled when it cannot be pressed: a disabled button loses focus, and the next draw
  // could not give it back. A press then does nothing (UpdateControl, and the host's own rules).
  const button = (cls, label, aria, can, onPress) => h('button', {
    type: 'button', class: `button secondary small ${cls}`, 'aria-label': aria,
    'aria-disabled': can ? null : 'true',
    onclick: (e) => { if (can) { onPress(e); } },
  }, label);
  // Back to Update now once its confirmation closes, or to the row's first action: the row has
  // been drawn again meanwhile.
  const refocus = (doc) => () => {
    const row = [...(doc.getElementById('machine-list')?.children ?? [])].find((li) => li.dataset.deviceId === id);
    (row?.querySelector('.machine-update-now') ?? row?.querySelector('.machine-update-check'))?.focus();
  };
  return {
    key,
    el: h('div', { class: 'machine-update' },
      h('p', { class: 'machine-update-line' }, said),
      last ? h('p', { class: 'machine-update-last' }, last) : null,
      why ? h('p', { class: 'machine-update-why' }, why) : null,
      note ? h('p', { class: 'machine-error', role: 'alert' }, note) : null,
      s.installer ? h('div', { class: 'machine-actions machine-update-actions' },
        button('machine-update-check', busy ? 'Asking…' : 'Check', `Check ${name} for updates`, check, () => updates.press(id, 'check')),
        s.available ? button('machine-update-install', 'Install', `Install ${s.available} on ${name} when no session there is busy`, install, () => updates.press(id, 'install')) : null,
        offerNow ? button('machine-update-now', 'Update now', `Update now: ${s.available} on ${name}, without waiting`, pressNow, (e) => {
          const doc = e.currentTarget.ownerDocument;
          updates.pressNow(id, { machine: name, confirm: (sheet, { signal } = {}) => openUpdateNowSheet(sheet, { doc, returnFocus: refocus(doc), signal }) });
        }) : null) : null),
  };
}

// Who can sign Copilot in there (#972, ui.md §6.6), inside the row like its updates, only where the
// machine has it on: read-only, with no Add, Remove or disabled button.
function signInRow(info) {
  const said = signInDevicesLines(info);
  if (!said) { return { key: null, el: null }; }
  return {
    key: [said.line, said.note, said.all],
    el: h('div', { class: 'machine-signin' },
      h('p', { class: 'machine-signin-line', title: said.all },
        said.line, said.all ? h('span', { class: 'sr-only' }, `: ${said.all}`) : null),
      said.note ? h('p', { class: 'machine-signin-note' }, said.note) : null),
  };
}

// "Diagnose this machine" inside a machine's row (#802, diagnostics.js, ui.md §6.6): the snapshot
// shown as plain text to read, a box to say it was reviewed, and Copy only once it is. Nothing is
// sent anywhere from here; the person pastes it where they choose. A machine whose host does not
// offer it shows a line saying so in its place, never a button that cannot act.
function diagnoseRow(diagnose, id, name) {
  if (!diagnose) { return { key: null, el: null }; }
  const offered = diagnose.offered(id);
  if (offered === false) {
    return { key: 'not-offered', el: h('p', { class: 'machine-diagnose-hint' }, `Can't be diagnosed from this device \u00B7 update ${BRAND.name} on ${name}`) };
  }
  if (offered !== true) { return { key: null, el: null }; }
  const s = diagnose.stateOf(id);
  const key = [s.phase, s.text, s.reviewed, s.copied, s.note];
  const review = s.phase === 'review';
  const box = review ? h('input', {
    type: 'checkbox', class: 'machine-diagnose-review', id: `diagnose-review-${id}`, checked: s.reviewed,
    onchange: (e) => diagnose.review(id, e.target.checked),
  }) : null;
  // Close is the report's default: Escape anywhere in it closes it. Focus moves to Diagnose first,
  // so the redraw gives it back there.
  const close = (e) => {
    e.currentTarget.closest('li')?.querySelector('.machine-diagnose')?.focus();
    diagnose.close(id);
  };
  return {
    key,
    el: h('div', {
      class: 'machine-diagnosis',
      onkeydown: review ? (e) => { if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); close(e); } } : null,
    },
      h('div', { class: 'machine-actions' },
        h('button', {
          type: 'button', class: 'button secondary small machine-diagnose', 'aria-label': `Diagnose ${name}`,
          'aria-disabled': s.phase === 'asking' ? 'true' : null,
          onclick: () => diagnose.press(id, name),
        }, s.phase === 'asking' ? 'Diagnosing\u2026' : review ? 'Diagnose again' : 'Diagnose'),
        review ? h('button', { type: 'button', class: 'button secondary small machine-diagnose-close', 'aria-label': `Close ${name}'s diagnostics`, onclick: close }, 'Close') : null),
      review ? h('pre', { class: 'machine-diagnostics plain', tabindex: '0', 'aria-label': `${name}'s diagnostics` }, s.text) : null,
      review ? h('label', { class: 'machine-diagnose-check', for: `diagnose-review-${id}` }, box,
        ' I have read this and it is fine to share') : null,
      review ? h('div', { class: 'machine-actions' }, h('button', {
        type: 'button', class: 'button secondary small machine-diagnose-copy',
        'aria-disabled': s.reviewed ? null : 'true',
        onclick: () => { if (s.reviewed) { diagnose.copyReviewed(id); } },
      }, s.copied ? 'Copied' : 'Copy')) : null,
      s.note ? h('p', { class: 'machine-error', role: 'alert' }, s.note) : null),
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

// What an Update now is doing, said politely once a step (update.js nowAnnouncement): the row's line
// is redrawn with the list, so it can't be the live region itself. The region sits beside the list.
function announceUpdates(list, updates, hosts) {
  if (!updates?.announce || !list.parentElement) { return; }
  const said = hosts.map((d) => [d.name, updates.announce(d.id)]).filter(([, text]) => text).map(([name, text]) => `${name}: ${text}`);
  if (!said.length) { return; }
  let region = list.parentElement.querySelector(':scope > .machine-update-announce');
  if (!region) {
    region = h('p', { class: 'sr-only machine-update-announce', 'aria-live': 'polite' });
    list.after(region);
  }
  region.textContent = said.join('. ');
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
  const block = h('div', { class: 'machine-block' }, head, extra.length ? h('div', { class: 'machine-more' }, extra) : null);
  // A refusal line is drawn under the block, on the row's plain surface and not on a selected row's
  // tint (#858). It stays in the row, and the buttons that can cause it still point at it.
  const errors = [...block.querySelectorAll('.machine-error')];
  errors.forEach((p, i) => { p.id = `machine-error-${id}-${i}`; });
  if (errors.length) {
    const ids = errors.map((p) => p.id).join(' ');
    for (const b of block.querySelectorAll('.machine-more button')) { b.setAttribute('aria-describedby', ids); }
  }
  return h('li', { class: `machine${offline ? ' offline' : ''}${selected ? ' selected' : ''}`, dataset: { deviceId: id, deviceName: name } }, block, errors);
}

export function renderMachines(list, { presence, me, current, onSelect, status = () => '', facts = null, known = null, forget = null, updates = null, signInDevices = null, diagnose = null, now = Date.now(), allStatus = '' }) {
  const hosts = presence.filter((d) => d.id !== me && d.kind === 'host');
  const all = current === ALL;
  const offline = offlineMachines({ presence, known, me });
  const rows = new Map(hosts.map((d) => [d.id, updateRow(updates, d.id, d.name, now)]));
  // A machine that left for an Update now reads "Restarting to update…" for a while (#1159, until
  // #815's planned restart is negotiated), in its offline row, or in a row of its own while the
  // relay doesn't list it yet.
  const restarting = new Set([...offline.map((k) => k.id), ...(updates?.leavingIds?.() ?? [])]
    .filter((id) => id !== me && !presence.some((d) => d.id === id) && updates?.restarting?.(id, false)));
  const leftNow = [...restarting].filter((id) => !offline.some((k) => k.id === id));
  announceUpdates(list, updates, hosts);
  const signInRows = new Map(hosts.map((d) => [d.id, signInRow(signInDevices?.(d.id))]));
  const diagnoses = new Map(hosts.map((d) => [d.id, diagnoseRow(diagnose, d.id, d.name)]));
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
  const key = JSON.stringify([current, allStatus, hosts.map((d) => [d.id, d.name, lines.get(d.id).key, rows.get(d.id).key, signInRows.get(d.id).key, diagnoses.get(d.id).key]),
    // lastSeen itself, not only how it reads: Forget sends it, to tell a machine that came back.
    offline.map((k) => [k.id, k.name, k.lastSeen, k.start ?? null, ago(k.lastSeen, now), forget?.stateOf(k.id) ?? null]), !!forget, [...restarting]]);
  if (drawnAs.get(list) === key) { return hosts.length; }
  drawnAs.set(list, key);
  // Every change redraws the list, so the button that had focus is replaced: Forget's first
  // press redraws it armed, and a keyboard's second press must land on the new one.
  const active = list.ownerDocument?.activeElement;
  const had = active && list.contains(active) ? { id: active.closest('li')?.dataset.deviceId, cls: ['machine-forget', 'machine-button', 'machine-update-check', 'machine-update-install', 'machine-update-now', 'machine-check-all', 'machine-diagnose', 'machine-diagnose-close', 'machine-diagnose-review', 'machine-diagnose-copy', 'machine-diagnostics'].find((c) => active.classList.contains(c)) } : null;
  const everyMachine = hosts.length > 1 ? [machineRow({
    id: ALL, name: 'All machines', select: () => onSelect(ALL), selected: all,
    facts: h('span', { class: 'machine-status' }, allStatus || `${hosts.length} machines`),
    state: all ? 'Viewing' : '',
  })] : [];
  list.replaceChildren(...everyMachine, ...hosts.map((d) => {
    const selected = d.id === current || (all && hosts.length === 1);
    return machineRow({
      id: d.id, name: d.name, select: () => onSelect(d.id), selected,
      facts: lines.get(d.id).el, state: selected ? 'Viewing' : 'Online', more: [rows.get(d.id).el, signInRows.get(d.id).el, diagnoses.get(d.id).el],
    });
  }), ...leftNow.map((id) => machineRow({
    id, name: updates.nameOf?.(id) ?? 'This machine', offline: true,
    facts: h('span', { class: 'machine-seen' }), state: RESTARTING,
  })), ...offline.map((k) => {
    const name = typeof k.name === 'string' && k.name ? k.name : 'unnamed';
    const seen = ago(k.lastSeen, now);
    const s = forget?.stateOf(k.id) ?? { armed: false, busy: false, error: null };
    return machineRow({
      id: k.id, name, offline: true,
      facts: h('span', { class: 'machine-seen' }, `Last seen ${seen || 'a while ago'}`),
      state: restarting.has(k.id) ? RESTARTING : startWords(k.start),
      more: [
        forget ? h('div', { class: 'machine-actions' }, h('button', {
          type: 'button', class: `button secondary small machine-forget${s.armed ? ' armed' : ''}`,
          // The name starts with the words shown, so a person who says what they see presses it
          // (WCAG 2.5.3, #356).
          'aria-label': s.armed ? `Press again to forget ${name}` : `Forget ${name}`,
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
