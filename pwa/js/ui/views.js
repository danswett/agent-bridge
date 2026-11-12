// The two lists on screen: online machines, and the sessions of the machines shown.
import { h, icon } from './dom.js';
import { ALL } from '../host-session.js';
import { SessionCard } from './session-card.js';
import { RowMenu } from './row-menu.js';
import { splitTabs } from './machine-tab-fit.js';
import { SessionOrder } from '../session-order.js';
import { RecentOrder } from '../recent-order.js';
import { ReadDwell } from '../read-dwell.js';
import { ago } from './time.js';
import { whileVisible } from './ticker.js';
import { enter, leave, seen, select as showSelection, slide } from './motion.js';
import { updateLine, updateDetails, lastLine, checkHeld, canCheck, canInstall, canUpdateNow, updateNowReason, RESTARTING } from '../update.js';
import { openUpdateNowSheet } from './update-now-sheet.js';
import { signInDevicesLines } from '../signin-devices.js';
import { BRAND } from '../brand.js';
import { sessionStatus } from '../status.js';
import { glyph } from './glyph.js';
import { loadBars } from '../load-bars.js';

const LONG_PRESS_MS = 500;
// A finger that moves further than this is scrolling, not pressing.
const LONG_PRESS_SLOP = 10;
const titleOf = (card) => card.session.title || 'Untitled session';

// Keeps one row per session, from every machine shown, in the person's order (session-order.js):
// Pinned, then the rest, then an Ended group (ui.md §6.1); and each session's view in the session
// pane, the selected one shown (./session-card.js). Both are updated in place so an open
// disclosure, scroll position or focus survives an upsert. Most recent batches activity moves; a
// session is its machine's: two machines may each have a session of the same id, and each acts
// only on its own machine.
//
// In Your order, rows move only when the person moves them: Alt+Up/Down, a drag by the handle that
// shows on hover, or the row menu (a long press on a phone, a right-click, Shift+F10). Up and
// Down alone go from row to row.
export class SessionListView {
  #cards = new Map();
  #scheduled = false;
  #menu = new RowMenu();
  #heads = {};
  #groups = new Map();
  #groupEditor = null;
  #groupMenu = null;
  #groupLeaving = null;
  #ended = null;
  #press = null;
  #swallowClick = false;
  #selected = null;
  #grab = null;
  #held = false;
  // Drawn once: what comes after is news, and may move.
  #drawn = false;
  #recent = new RecentOrder();
  #recentTimer = null;
  #scrollTimer = null;
  #recentHolds = new Set();
  #recentFocus = null;
  #recentListeners = new AbortController();

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
  constructor(container, sessions, { history = null, fetchDetail, fetchDelegate = null, machine = () => ({ name: '', show: false }), answering = () => null, order = new SessionOrder(), shows = () => true, settled = () => true, views = null, layout = () => ({ inspector: false }), viewing = () => true, stale = () => null, heading = () => null, awake = () => globalThis.document?.hasFocus?.() !== false, showEmptyGroups = () => true, timers = null } = {}) {
    Object.assign(this, { container, sessions, history, fetchDetail, fetchDelegate, machine, answering, order, shows, settled, layout, viewing, stale, heading, awake, showEmptyGroups });
    // A session is read once open, visible and focused for about a second (session-order-roaming.md section 8).
    this.dwell = new ReadDwell({ mark: (key, at) => { this.order.markSeen(key, at); this.render(); }, timers });
    for (const [target, event] of [[globalThis.window, 'focus'], [globalThis.window, 'blur'], [globalThis.document, 'visibilitychange']]) { target?.addEventListener?.(event, () => this.schedule()); }
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
    this.#listenRecent();
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
  needsOnly = false;

  setNeedsOnly(on) { this.needsOnly = !!on; this.render(); }

  #matches(hostId, s) {
    if (!this.filter) { return true; }
    const m = this.machine(hostId);
    return [s.title, s.repository, s.branch, s.cwd, s.agent, m.name].some((v) => typeof v === 'string' && v.toLowerCase().includes(this.filter));
  }

  get moving() { return !this.filter && !this.needsOnly && this.order.sort === 'yours'; }

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
    const held = this.#recentHolds.size > 0 || !!this.#recentFocus?.isConnected;
    const arranged = this.#recent.arrange(this.order.arrange(entries, { nameOf: (id) => this.machine(id).name, shows: this.shows, settled: this.settled }), { sort, held });
    clearTimeout(this.#recentTimer);
    if (this.#recent.wait !== null) { this.#recentTimer = setTimeout(() => this.schedule(), this.#recent.wait); }
    const before = new Map();
    if (this.#recent.moved.size || this.#groupLeaving) {
      for (const el of this.container.querySelectorAll('.session')) {
        if (!el.getClientRects().length) { continue; }
        before.set(el, el.getBoundingClientRect());
      }
    }
    if (this.#groupLeaving) { leave(this.#groupLeaving, 'listItem'); this.#groupLeaving = null; }
    const pane = this.container.closest('#list-pane');
    const scroller = !before.size ? null : pane && /auto|scroll/.test(getComputedStyle(pane).overflowY) ? pane : this.container.ownerDocument.scrollingElement;
    const anchor = scroller?.scrollTop > 0 ? [...before.keys()].find((el) => seen(el)) : null;
    for (const el of before.keys()) {
      for (const a of el.getAnimations?.() ?? []) { if (/^ab-motion-(reorder|shift)$/.test(a.id)) { a.cancel(); } }
    }
    const matches = (e) => this.#matches(e.hostId, e.session);
    const keep = (e) => matches(e) && (!this.needsOnly || sessionStatus(e.session).glyph === 'needs');
    const grouped = this.order.groupRows(arranged.sessions, { matches, filtered: !!this.filter, needsOnly: this.needsOnly, showEmpty: this.showEmptyGroups() });
    const ended = this.needsOnly ? [] : arranged.ended.filter(keep);
    this.container.dataset.sort = this.order.sort;
    this.container.dataset.filtered = this.filter || this.needsOnly ? 'true' : 'false';
    this.onSorted?.();
    const live = new Set([...arranged.pinned, ...arranged.sessions].map((e) => SessionListView.key(e.hostId, e.session.id)));
    if (this.#groupEditor && !live.has(this.#groupEditor.key)) { this.#groupEditor.close(); }
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
    let reading = false;
    const cards = (list, isPinned, emit = true) => {
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
        const responseTurn = s.activity?.responseTurn;
        if (card.selected) { reading = true; this.dwell.watch(key, responseTurn, this.order.unread(key, responseTurn) && this.viewing() && this.awake()); }
        card.setUnread(this.order.unread(key, responseTurn));
        if (!emit || !keep(entry)) { continue; }
        want.push(card.element);
        shown += 1;
      }
    };
    const pinnedShown = arranged.pinned.some(keep);
    const sessionsShown = grouped.sessions.some(keep);
    if (pinnedShown) { want.push(this.#head('pinned', 'Pinned')); }
    cards(arranged.pinned, true);
    cards(arranged.sessions, false, false);
    for (const group of grouped.groups) {
      want.push(this.#groupHead(group));
      for (const entry of group.visible) {
        want.push(this.#cards.get(SessionListView.key(entry.hostId, entry.session.id)).element);
        shown++;
      }
    }
    if (this.#groupEditor?.form) { want.push(this.#groupEditor.form); }
    if ((pinnedShown || grouped.groups.length) && sessionsShown) { want.push(this.#head('sessions', 'Sessions')); }
    for (const entry of grouped.sessions.filter(keep)) {
      want.push(this.#cards.get(SessionListView.key(entry.hostId, entry.session.id)).element);
      shown++;
    }
    if (!reading) { this.dwell.cancel(); }
    for (const id of this.#groups.keys()) {
      if (!this.order.groups.some((g) => g.id === id)) { this.#groups.delete(id); }
    }
    if (ended.length) { want.push(this.#endedGroup(ended)); }
    // Move only what is out of place, so a card being read is not torn out and put back.
    let prev = null;
    for (const el of want) {
      const at = prev ? prev.nextSibling : this.container.firstChild;
      if (at !== el) { this.container.insertBefore(el, at); }
      prev = el;
    }
    while (prev ? prev.nextSibling : this.container.firstChild) { (prev ? prev.nextSibling : this.container.firstChild).remove(); }
    if (this.#menu.open && this.#groupMenu && !this.#groupMenu.element.isConnected) {
      const card = this.#groupMenu;
      this.#menu.close(false);
      this.#refocus(rowsWere, rowsWere.indexOf(card.element), this.#groups.get(this.order.groupFor(this.#keyOf(card))));
    }
    if (!this.#menu.open) { this.#groupMenu = null; }
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
    if (focused && !focused.isConnected) {
      const row = rowsWere.find((r) => r.contains(focused));
      if (row && live.has(row.dataset.key)) {
        this.#refocus(rowsWere, rowsWere.indexOf(row), this.#groups.get(this.order.groupFor(row.dataset.key)));
      }
    }
    if (anchor?.isConnected) {
      const delta = anchor.getBoundingClientRect().top - before.get(anchor).top;
      if (delta) { this.onAnchor?.(); scroller.scrollTop += delta; }
    }
    for (const [el, was] of before) {
      if (!el.isConnected) { continue; }
      const at = el.getBoundingClientRect();
      slide(el, this.#recent.moved.has(el.dataset.key) ? 'reorder' : 'shift', was.left - at.left, was.top - at.top);
    }
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

  // A focused session ended, or its group now hides its row, leaving focus on the page
  // (§9.1 A4, #369). It goes to the retained group header, otherwise to
  // the row that takes its place, or the one before it if it was last, or else the list's
  // heading. Only to one in sight: on a phone the list is under the session's view, and going
  // back to it (sessions-page.js) gives focus back there.
  #refocus(rowsWere, at, preferred = null) {
    const doc = this.container.ownerDocument;
    // Unless focus went somewhere real meanwhile. The list's own ancestors don't count: WebKit
    // can give lost focus to the Sessions section.
    const now = doc.activeElement;
    if (now && now !== doc.body && now.isConnected && !now.contains(this.container)) { return; }
    const shown = (r) => this.container.contains(r) && r.getClientRects().length > 0;
    const next = rowsWere.slice(at + 1).find(shown) ?? rowsWere.slice(0, Math.max(at, 0)).reverse().find(shown);
    let to = preferred && shown(preferred) ? preferred : next?.querySelector('.row-main');
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

  #groupHead(group) {
    let head = this.#groups.get(group.id);
    if (!head) {
      head = h('button', { type: 'button', class: 'session-group-toggle', dataset: { group: group.id } },
        icon('chevron', 'group-chevron'), h('span', { class: 'group-name' }),
        h('span', { class: 'group-count' }),
        h('span', { class: 'group-needs', hidden: true }, glyph('needs'), h('span')),
        h('span', { class: 'unread-dot', 'aria-hidden': 'true', hidden: true }));
      const toggle = (collapsed) => {
        if (this.filter || this.needsOnly) { return; }
        this.order.setGroupCollapsed(group.id, collapsed);
        this.render();
        head.focus({ preventScroll: true });
      };
      head.addEventListener('click', () => toggle(!this.order.isGroupCollapsed(group.id)));
      this.#groups.set(group.id, head);
    }
    const needs = !group.expanded && group.needs ? `${group.needs} needs you` : '';
    head.setAttribute('aria-expanded', String(group.expanded));
    head.setAttribute('aria-disabled', String(!!(this.filter || this.needsOnly)));
    head.setAttribute('aria-label', `${group.name}, ${group.count} ${group.count === 1 ? 'session' : 'sessions'}${needs ? `, ${needs}` : !group.expanded && group.unread ? ', New activity' : ''}`);
    head.querySelector('.group-name').textContent = group.name;
    head.querySelector('.group-count').textContent = `\u00b7 ${group.count}`;
    head.querySelector('.group-needs').hidden = !needs;
    head.querySelector('.group-needs > span:last-child').textContent = needs;
    head.querySelector('.unread-dot').hidden = group.expanded || !!needs || !group.unread;
    return head;
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
    if (!this.moving || !this.order.move(this.#keyOf(card), delta)) { return false; }
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

  #listenRecent() {
    const c = this.container, doc = c.ownerDocument;
    const signal = this.#recentListeners.signal;
    const hold = (key, on) => {
      const was = this.#recentHolds.has(key);
      if (on) { this.#recentHolds.add(key); } else { this.#recentHolds.delete(key); }
      if (was !== on && this.order.sort === 'recent') { this.schedule(); }
    };
    c.addEventListener('pointerenter', (e) => { if (e.pointerType !== 'touch') { hold('pointer', true); } }, { signal });
    c.addEventListener('pointerleave', (e) => { if (e.pointerType !== 'touch') { hold('pointer', false); } }, { signal });
    // Scrolling cancels a pointer before its finger lifts; touch events keep that hold.
    const touching = new Set(), touchEnds = ['touchend', 'touchcancel'];
    const touchEnd = (e) => {
      if (e.target !== e.currentTarget || e.targetTouches?.length) { return; }
      touching.delete(e.target);
      for (const event of touchEnds) { e.target.removeEventListener(event, touchEnd); }
      hold('touch', touching.size > 0);
    };
    c.addEventListener('touchstart', (e) => {
      touching.add(e.target);
      for (const event of touchEnds) { e.target.addEventListener(event, touchEnd, { passive: true, signal }); }
      hold('touch', true);
    }, { passive: true, signal });
    const focus = (target) => {
      this.#recentFocus = target && (c.contains(target) || target.closest?.('#needs-you, .reply, .question')) ? target : null;
      if (this.order.sort === 'recent') { this.schedule(); }
    };
    focus(doc.activeElement);
    doc.addEventListener('focusin', (e) => focus(e.target), { signal });
    doc.addEventListener('focusout', (e) => focus(e.relatedTarget), { signal });
    doc.addEventListener('scroll', (e) => {
      if (!e.target.contains?.(c)) { return; }
      hold('scroll', true);
      clearTimeout(this.#scrollTimer);
      this.#scrollTimer = setTimeout(() => hold('scroll', false), 150);
    }, { capture: true, passive: true, signal });
  }

  stopRecent() {
    this.#menu.close(false);
    this.#groupMenu = null;
    this.#groupEditor?.close(false, false);
    this.#recentListeners.abort();
    clearTimeout(this.#recentTimer);
    clearTimeout(this.#scrollTimer);
  }

  openMenu(card) {
    this.#groupMenu = null;
    const key = this.#keyOf(card);
    const pinned = this.order.isPinned(key);
    const moveItem = (delta) => {
      const disabled = !this.moving || !this.order.canMove(key, delta);
      const neighbour = this.order.neighbours(key)?.[delta < 0 ? 'prev' : 'next'];
      const loading = this.order.isPending(key) || this.order.isPending(neighbour);
      const reason = !disabled ? null : loading ? 'Wait for the session list to finish loading'
        : this.order.sort !== 'yours' ? 'Switch to Your order to move sessions'
        : this.filter || this.needsOnly ? 'Clear the search or machine filter to move sessions'
        : delta < 0 ? 'Already at the top' : 'Already at the bottom';
      return {
        label: delta < 0 ? 'Move up' : 'Move down', shortcut: delta < 0 ? 'Alt+\u2191' : 'Alt+\u2193',
        disabled, reason, run: () => this.#move(card, delta),
      };
    };
    this.#menu.show(card.element, [
      moveItem(-1), moveItem(1),
      { label: pinned ? 'Unpin' : 'Pin', run: () => this.#pin(card, !pinned) },
      { label: 'Move to group', disabled: !!this.#groupReason(card), reason: this.#groupReason(card), run: () => this.#chooseGroup(card) },
      ...(this.order.groupFor(key) === null ? [] : [
        { label: 'Remove from group', disabled: !!this.#groupReason(card), reason: this.#groupReason(card), run: () => this.#setGroup(card, null) },
      ]),
    ], { back: card.els.open, label: `${card.session.title || 'Session'}: actions` });
  }

  #groupReason(card) {
    const key = this.#keyOf(card);
    return this.#cards.get(key) !== card ? 'The session is no longer in this list.'
      : this.order.isPending(key) ? 'Wait for the session list to finish loading'
      : this.order.sort !== 'yours' ? 'Switch to Your order to move sessions'
      : this.filter || this.needsOnly ? 'Clear the search or machine filter to move sessions' : null;
  }

  #setGroup(card, id) {
    const reason = this.#groupReason(card);
    if (reason) { this.#say(reason); return; }
    const old = this.order.groups.find((g) => g.id === this.order.groupFor(this.#keyOf(card)));
    if (id !== null && this.order.isGroupCollapsed(id) && !this.order.isPinned(this.#keyOf(card)) && sessionStatus(card.session).glyph !== 'needs') {
      this.#groupLeaving = card.element;
    }
    this.order.setGroup(this.#keyOf(card), id);
    this.render();
    if (!this.order.usable) { this.#say('Your order could not be saved on this device.'); return; }
    if (!card.element.isConnected) { this.#groups.get(id)?.focus(); this.#say(''); }
    else {
      card.els.open.focus({ preventScroll: true });
      card.element.scrollIntoView({ block: 'nearest' });
      this.#say(id === null ? `Removed from ${old.name}` : `Moved to ${this.order.groups.find((g) => g.id === id).name}`);
    }
  }

  #chooseGroup(card) {
    this.#groupMenu = card;
    const current = this.order.groupFor(this.#keyOf(card));
    this.#menu.show(card.element, [
      ...this.order.groups.map((g) => ({ label: g.name, checked: g.id === current, run: () => this.#setGroup(card, g.id) })),
      { label: 'New group\u2026', run: () => this.#newGroup(card) },
    ], { back: card.els.open, label: 'Move to group', sheet: matchMedia('(max-width: 759px)').matches });
  }

  #newGroup(card) {
    this.#groupEditor?.close();
    const phone = matchMedia('(max-width: 759px)').matches;
    const id = `group-name-${crypto.randomUUID()}`;
    const input = h('input', { id, class: 'text-input', 'aria-describedby': `${id}-error` });
    const error = h('p', { id: `${id}-error`, class: 'answer-error', role: 'alert', hidden: true });
    const form = h('form', { class: 'group-editor' },
      h('label', { for: id }, 'Group name'), input, error,
      h('button', { type: 'submit', class: 'button' }, 'Create group'),
      h('button', { type: 'button', class: 'button secondary', onclick: () => close() }, 'Cancel'));
    const dialog = phone ? h('dialog', { class: 'row-menu-sheet', 'aria-label': 'New group' }, h('h2', {}, 'New group'), form) : null;
    const close = (refocus = true, redraw = true) => {
      this.#groupEditor = null;
      dialog?.close(); dialog?.remove(); form.remove();
      if (redraw) { this.schedule(); }
      if (refocus) { (card.els.open.isConnected ? card.els.open : this.heading())?.focus({ preventScroll: true }); }
    };
    form.addEventListener('keydown', (e) => { if (e.key === 'Escape') { e.preventDefault(); close(); } });
    dialog?.addEventListener('cancel', (e) => { e.preventDefault(); close(); });
    form.addEventListener('submit', (e) => {
      e.preventDefault();
      try {
        const reason = this.#groupReason(card);
        if (reason) { throw new RangeError(reason); }
        const group = this.order.createGroup(input.value);
        if (!this.order.usable) { throw new RangeError('Your order could not be saved on this device.'); }
        close(false);
        this.#setGroup(card, group);
      } catch (e) {
        if (!(e instanceof RangeError)) { throw e; }
        error.textContent = e.message; error.hidden = false;
        input.setAttribute('aria-invalid', 'true');
        input.focus();
      }
    });
    this.#groupEditor = { key: this.#keyOf(card), form: phone ? null : form, close };
    if (dialog) { document.body.append(dialog); dialog.showModal(); } else { this.render(); }
    input.focus();
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
      const head = e.target.closest?.('.session-group-toggle');
      const card = e.target.closest?.('.row-main') ? this.#cardOf(e.target) : null;
      if (!card && !head) { return; }
      // The context-menu key, or Shift+F10: Safari has neither as a contextmenu event.
      if (card && (e.key === 'ContextMenu' || (e.key === 'F10' && e.shiftKey && !e.altKey && !e.ctrlKey && !e.metaKey))) {
        e.preventDefault();
        this.openMenu(card);
        return;
      }
      if (e.ctrlKey || e.metaKey || e.shiftKey) { return; }
      if (!e.altKey && (e.key === 'ArrowLeft' || e.key === 'ArrowRight')) {
        if (head) {
          e.preventDefault();
          const expanded = head.getAttribute('aria-expanded') === 'true';
          if (e.key === 'ArrowRight' && expanded) {
            const first = head.nextElementSibling;
            if (first?.matches('.session')) { first.querySelector('.row-main')?.focus(); }
          } else if (expanded === (e.key === 'ArrowLeft')) { head.click(); }
        } else if (e.key === 'ArrowLeft' && !this.order.isPinned(this.#keyOf(card))) {
          const group = this.#groups.get(this.order.groupFor(this.#keyOf(card)));
          if (group?.isConnected) { e.preventDefault(); group.focus(); }
        }
        return;
      }
      if (e.key !== 'ArrowUp' && e.key !== 'ArrowDown') { return; }
      if (e.altKey) {
        if (card) { e.preventDefault(); if (!this.#grab) { this.#move(card, e.key === 'ArrowUp' ? -1 : 1); } }
        return;
      }
      e.preventDefault();
      const items = [...this.container.querySelectorAll('.session-group-toggle, .session > .row-main')];
      items[items.indexOf(head ?? card.els.open) + (e.key === 'ArrowUp' ? -1 : 1)]?.focus();
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
// keys, Home and End move between them, choosing as they go, as a SelectorBar does. Below 760 px
// overflow stays under "N more" (#351); wide layouts scroll whole tabs with edge fades (#1894).

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
  const s = machineBar(bar);
  s.onSelect = onSelect;
  if (s.key === key) { return; }
  s.key = key;
  s.menu.close(false);
  // Taken before the bar can hide: a hidden tab loses focus.
  const doc = bar.ownerDocument;
  const had = bar.contains(doc?.activeElement) ? doc.activeElement : null;
  s.had = had?.dataset.deviceId ?? (had === s.more ? MORE : null);
  const was = bar.hidden ? null : bar.querySelector('[aria-selected="true"]');
  const from = seen(was);
  bar.hidden = !show;
  s.tabs = tabs;
  s.chosen = tabs.findIndex(chosen);
  s.els = tabs.map((t) => {
    const on = chosen(t);
    const name = t.away ? `${t.label}, offline` : t.label;
    return h('button', {
      type: 'button', role: 'tab', class: `machine-tab${t.away ? ' away' : ''}`,
      'aria-selected': on ? 'true' : 'false', tabindex: on ? '0' : '-1',
      'aria-controls': controls, 'aria-label': name, title: name,
      dataset: { deviceId: t.id, deviceName: t.label },
      // Picked again, a machine that can't be reached is tried again at once.
      onclick: () => s.onSelect(t.id),
    }, h('span', { class: 'machine-tab-name' }, t.name));
  });
  layoutMachineTabs(bar, s);
  // Selection (ui.md §5.3): the bar under the tab slides from the one chosen before, or fades in
  // when that one was scrolled out of the bar.
  const now = bar.querySelector('[aria-selected="true"]');
  if (was && now && was.dataset.deviceId !== now.dataset.deviceId) {
    showSelection(now, 'selection', from, { pseudo: '::after', axis: 'x', scaleX: true });
  }
  // When the bar itself went, focus goes to what it controlled (the sessions), not to the document.
  if (s.had && !show) { doc.getElementById('sessions')?.focus({ preventScroll: true }); s.had = null; }
}

const MORE = '\0more';
const machineBars = new WeakMap();

// The bar's parts, made once: the tab list (a tablist holds only tabs, so More sits beside it, not
// in it), More, and the menu under it. The bar is drawn again when it is resized.
function machineBar(bar) {
  let s = machineBars.get(bar);
  if (s) { return s; }
  bar.removeAttribute('role');
  bar.removeAttribute('aria-label');
  const list = h('div', { class: 'machine-tablist', role: 'tablist', 'aria-label': 'Machines' });
  s = { key: null, list, more: null, menu: new RowMenu(), tabs: [], els: [], chosen: -1, shown: [], hidden: [], width: -1, had: null, onSelect: () => {} };
  s.more = h('button', {
    type: 'button', class: 'machine-tab machine-more', 'aria-haspopup': 'menu', 'aria-expanded': 'false',
    onclick: () => {
      if (s.menu.open) { s.menu.close(); return; }
      s.menu.show(bar, s.hidden.map((t) => ({
        label: t.away ? `${t.label} · offline` : t.id === ALL ? t.label : `${t.label} · online`,
        run: () => {
          s.onSelect(t.id);
          // Drawn again by now with the machine among the tabs.
          bar.querySelector(`[role="tab"][data-device-id="${CSS.escape(t.id)}"]`)?.focus();
        },
      })), { back: s.more, trigger: s.more, label: 'More machines' });
    },
  });
  bar.replaceChildren(list);
  machineBars.set(bar, s);
  s.wide = matchMedia('(min-width: 760px)');
  s.wide.addEventListener('change', () => { s.menu.close(false); layoutMachineTabs(bar, s); });
  list.addEventListener('scroll', () => machineTabEdges(bar, list));
  bar.addEventListener('keydown', (e) => {
    const tabs = [...list.querySelectorAll('[role="tab"]')];
    const i = tabs.indexOf(e.target);
    if (i < 0) { return; }
    const to = { ArrowRight: i + 1, ArrowLeft: i - 1, Home: 0, End: tabs.length - 1 }[e.key];
    if (to === undefined) { return; }
    e.preventDefault();
    const next = tabs[(to + tabs.length) % tabs.length];
    next.focus();
    next.click();
  });
  if (typeof ResizeObserver === 'function') {
    new ResizeObserver(() => {
      if (!bar.hidden && bar.clientWidth !== s.width && s.els.length) { s.menu.close(false); layoutMachineTabs(bar, s); }
    }).observe(bar);
  }
  return s;
}

// Draws the tabs that fit, in order, and More for the rest (machine-tab-fit.js). Every tab is
// measured at its own width (a name cut at 128 px by CSS), so they go in first and the hidden ones
// come out. A tab with focus keeps it, or More does when its machine went under it.
function layoutMachineTabs(bar, s) {
  const { list, els, more, tabs } = s;
  const doc = bar.ownerDocument;
  const focus = s.had ?? (bar.contains(doc.activeElement) ? doc.activeElement.dataset.deviceId ?? (doc.activeElement === more ? MORE : null) : null);
  els.forEach((el, i) => {
    el.style.maxWidth = '';
    const name = Array.from(tabs[i].name);
    el.firstElementChild.textContent = s.wide.matches && name.length > 16 ? `${name.slice(0, 16).join('')}\u2026` : tabs[i].name;
  });
  list.replaceChildren(...els);
  more.remove();
  const room = bar.clientWidth;
  s.width = room;
  s.shown = tabs; s.hidden = [];
  if (bar.hidden || !room) { return; }
  if (s.wide.matches) {
    if (focus) { (els.find((el) => el.dataset.deviceId === focus) ?? els[s.chosen])?.focus({ preventScroll: true }); }
    const chosen = els[s.chosen];
    if (chosen) {
      const outer = list.getBoundingClientRect(), inner = chosen.getBoundingClientRect();
      const inset = parseFloat(getComputedStyle(list).scrollPaddingLeft);
      // Relative subpixel scrolling can advance to the next snap point after a resize.
      list.scrollTo({ left: list.scrollLeft + inner.left - outer.left - inset, behavior: 'instant' });
    }
    machineTabEdges(bar, list);
    s.had = null;
    return;
  }
  machineTabEdges(bar, list);
  // Measured with the widest count, so a count that grows a digit cannot push it off the bar.
  more.textContent = `${Math.max(1, tabs.length - 1)} more`;
  bar.append(more);
  const gap = parseFloat(getComputedStyle(bar).columnGap) || 0;
  const widths = els.map((el) => Math.ceil(el.getBoundingClientRect().width));
  const split = splitTabs(widths, s.chosen, room, Math.ceil(more.getBoundingClientRect().width), gap);
  s.shown = split.shown.map((i) => tabs[i]);
  s.hidden = split.more.map((i) => tabs[i]);
  list.replaceChildren(...split.shown.map((i) => els[i]));
  if (split.chosenWidth !== null) { els[s.chosen].style.maxWidth = `${split.chosenWidth}px`; }
  if (split.more.length) { more.textContent = `${split.more.length} more`; } else { more.remove(); }
  if (!list.querySelector('[tabindex="0"]')) { list.firstElementChild?.setAttribute('tabindex', '0'); }
  if (focus === MORE) { (split.more.length ? more : list.querySelector('[tabindex="0"]'))?.focus(); }
  else if (focus) { [...list.children].find((b) => b.dataset.deviceId === focus)?.focus(); }
  s.had = null;
}

function machineTabEdges(bar, list) {
  bar.toggleAttribute('data-scroll-start', list.scrollLeft > 1);
  bar.toggleAttribute('data-scroll-end', list.scrollWidth - list.clientWidth - list.scrollLeft > 1);
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
  const updateNow = updates.isUpdateNow?.(s.last?.request) ?? false;
  const idle = s.installer && s.phase === 'idle' && !s.waiting;
  const checkFailed = idle && !updateNow && checkHeld(s);
  let said = updateLine(s, now);
  if (idle) {
    said = s.available ? `Update available \u00b7 ${s.available} (running ${s.current ?? 'an older release'})`
      : Number.isFinite(Date.parse(s.checkedAt)) ? `Up to date \u00b7 ${s.current ?? 'unknown'}` : 'Not checked yet';
  }
  const checking = s.phase === 'checking';
  if (checking && !s.waiting) { said = 'Checking\u2026'; }
  const last = checkFailed || (idle && s.last?.outcome === 'current') ? null : lastLine(s, now, { updateNow });
  const check = canCheck(s) && !busy && !asking;
  const install = canInstall(s) && !busy && !asking;
  const offerNow = updates.signs === true && canUpdateNow(s);
  const pressNow = offerNow && !busy && !asking;
  const why = updates.signs === true ? updateNowReason(s) : null;
  const details = updateDetails(s, now);
  const key = [said, last, note, busy, asking, check, install, offerNow, pressNow, why, ...details];
  const checkLabel = checking ? 'Checking\u2026' : busy ? 'Asking\u2026' : 'Check now';
  // Not disabled when it cannot be pressed: a disabled button loses focus, and the next draw
  // could not give it back. A press then does nothing (UpdateControl, and the host's own rules).
  const button = (cls, label, aria, can, onPress) => h('button', {
    type: 'button', class: `button ${cls === 'machine-update-now' ? 'primary' : 'secondary'} small ${cls}`, 'aria-label': aria,
    'aria-disabled': can ? null : 'true',
    'aria-describedby': cls === 'machine-update-check' && checkFailed ? `update-check-${id}` : null,
    onclick: (e) => { if (can) { onPress(e); } },
  }, cls === 'machine-update-check' && checking ? glyph('working') : null, label);
  // Back to Update now once its confirmation closes, or to the row's first action: the row has
  // been drawn again meanwhile.
  const refocus = (doc) => () => {
    const row = [...(doc.getElementById('machine-list')?.children ?? [])].find((li) => li.dataset.deviceId === id);
    (row?.querySelector('.machine-update-now') ?? row?.querySelector('.machine-update-check'))?.focus();
  };
  return {
    key,
    el: h('div', { class: 'machine-update' },
      h('p', { class: 'machine-update-line' },
        glyph(['checking', 'downloading', 'installing', 'waiting'].includes(s.phase) ? 'working'
          : s.available ? 'needs' : idle && s.checkedAt ? 'done' : 'idle'), h('span', null, said)),
      last ? h('p', { class: 'machine-update-last' }, last) : null,
      why ? h('p', { class: 'machine-update-why' }, why) : null,
      ...details.map((text) => h('p', { class: 'machine-update-detail', id: checkFailed ? `update-check-${id}` : null }, text)),
      note ? h('p', { class: 'machine-error', role: 'alert' }, note) : null,
      s.installer ? h('div', { class: 'machine-actions machine-update-actions' },
        !offerNow ? button('machine-update-check', checkLabel, `${checkLabel} on ${name}`, check, () => updates.press(id, 'check')) : null,
        s.available && !offerNow ? button('machine-update-install', 'Install', `Install ${s.available} on ${name} when no session there is busy`, install, () => updates.press(id, 'install')) : null,
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
// sent anywhere from here; the person pastes it where they choose. A known withholding reason
// is shown only for a machine with an existing problem, never a button that cannot act.
function diagnoseRow(diagnose, id, name) {
  if (!diagnose) { return { key: null, el: null }; }
  const offered = diagnose.offered(id);
  if (offered === false) {
    const why = new Map([
      ['pinning-off', ['Diagnose needs device approval turned on', 'turn it on']],
      ['no-person-lease', ['Diagnose needs this device approved', 'approve it']],
      ['not-offered', ['Diagnose is not offered here', 'check its settings']],
    ]).get(diagnose.reason(id));
    const text = why ? `${why[0]} \u00B7 the owner of ${name} can ${why[1]}` : null;
    return { key: text, el: text ? h('p', { class: 'machine-diagnose-hint' }, text) : null };
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

const drawnMachineLoads = new WeakMap();

function measurementAge(bars) {
  if (bars?.ageMs == null || bars.state === 'offline') { return ''; }
  const seconds = Math.floor(bars.ageMs / 1000);
  const age = seconds < 60 ? `${seconds} s` : seconds < 3600 ? `${Math.floor(seconds / 60)} min` : `${Math.floor(seconds / 3600)} h`;
  return `${['fresh', 'partial'].includes(bars.state) ? 'Updated' : 'Last measured'} ${age} ago`;
}

function machineLoadBar(bar, label, bars) {
  const missing = bar?.percent == null;
  const text = missing ? 'No figure' : `${Math.round(bar.percent)}%`;
  const fill = missing ? null : h('span', { class: 'machine-load-fill' });
  if (fill) { fill.style.width = `${bar.percent}%`; }
  return h('div', { class: `machine-load${missing ? ' missing' : ''}` },
    h('span', { class: 'machine-load-label' }, label),
    h('div', {
      class: 'machine-load-track', role: missing ? null : 'meter', 'aria-hidden': missing ? 'true' : null,
      'aria-label': missing ? null : label, 'aria-valuemin': missing ? null : 0,
      'aria-valuemax': missing ? null : 100, 'aria-valuenow': bar?.percent, 'aria-valuetext': missing ? null : text,
    }, fill),
    h('span', { class: 'machine-load-value' }, text),
    h('span', { class: 'machine-load-note' },
      h('span', { class: 'machine-load-state' }, h('span', { 'aria-hidden': 'true' }, bars?.icon ?? '\u25cb'), ` ${bars?.word ?? 'Unavailable'}`),
      bar?.free ? h('span', null, bar.free) : null));
}

function machineHealth(bars) {
  return h('p', { class: 'tile-health' },
    h('span', { class: 'tile-label' }, 'Health: '),
    h('span', { class: 'tile-health-icon', 'aria-hidden': 'true' }, bars?.state === 'offline' ? '\u2298' : '\u25cb'),
    h('span', { class: 'tile-health-word' }, bars?.state === 'offline' ? 'Offline' : 'Not measured yet'));
}

// A machine's tile (#1147 S1), its sections in one order: Health, Can run, Running, Actions. Only
// what a host reports is shown; freshness is not a health diagnosis.
function machineTile({ name, raw, bars, onSelect, onLaunch, more, expanded = false }) {
  const why = raw?.held ? `Discovery is held: ${raw.held}` : raw?.readOnly ? `This machine is view only: ${raw.readOnly}` : null;
  const running = !raw?.sessions ? 'Not reported yet' : raw.sessions.startsWith('0 ') ? 'No sessions running' : raw.sessions;
  const contents = h('div', { hidden: !expanded },
    h('button', { type: 'button', class: 'button secondary small machine-running-view', 'aria-label': `View sessions on ${name}`, onclick: onSelect }, 'View sessions'));
  const toggle = h('button', { type: 'button', class: 'tile-running-toggle', 'aria-expanded': String(expanded), onclick: () => {
    contents.hidden = !contents.hidden;
    toggle.setAttribute('aria-expanded', String(!contents.hidden));
  } }, h('span', { class: 'tile-label' }, 'Running, '), h('span', null, running), icon('chevron'));
  return h('section', { class: 'machine-tile', 'aria-label': name },
    machineHealth(bars),
    h('p', { class: 'tile-measured' }, measurementAge(bars)),
    machineLoadBar(bars?.cpu, 'CPU', bars), machineLoadBar(bars?.memory, 'Memory', bars),
    h('div', { class: 'tile-running' }, toggle, contents),
    h('div', { class: 'tile-actions' },
      why ? h('p', { class: 'tile-actions-note' }, why)
        : h('button', { type: 'button', class: 'button secondary small machine-launch', 'aria-label': `Launch here on ${name}`, onclick: onLaunch }, 'Launch here'),
      h('button', { type: 'button', class: 'button secondary small machine-view', 'aria-label': `Show sessions on ${name}`, onclick: onSelect }, 'Show sessions')),
    h('div', { class: 'machine-more' }, more.filter(Boolean)));
}

// One row: the icon and the trailing state on line 1 with the name, the facts on line 2, and
// anything more (updates, Forget, an error) under them in the same text column.
function machineRow({ id, name, select = null, selected = false, facts, state, offline = false, more = [], tile = null }) {
  const parts = [
    icon('machine'),
    h('span', { class: 'machine-name' }, name),
    h('span', { class: 'machine-state' }, ['Online', 'Viewing'].includes(state) ? [h('span', { 'aria-hidden': 'true' }, state === 'Viewing' ? '\u25c9' : '\u25cf'), ' '] : null, state),
    h('span', { class: 'machine-sub' }, facts),
  ];
  const head = select
    ? h('button', { type: 'button', class: `machine-button${selected ? ' selected' : ''}`, 'aria-current': selected ? 'true' : null, onclick: select }, parts)
    : h('div', { class: 'machine-row' }, parts);
  const extra = more.filter(Boolean);
  const block = h('div', { class: 'machine-block' }, head, extra.length ? h('div', { class: 'machine-more' }, extra) : null);
  // A refusal line is drawn under the block, on the row's plain surface and not on a selected row's
  // tint (#858). It stays in the row, and the buttons that can cause it still point at it.
  const within = [block, tile].filter(Boolean);
  const errors = within.flatMap((el) => [...el.querySelectorAll('.machine-error')]);
  errors.forEach((p, i) => { p.id = `machine-error-${id}-${i}`; });
  if (errors.length) {
    const ids = errors.map((p) => p.id).join(' ');
    for (const b of within.flatMap((el) => [...el.querySelectorAll('.machine-more button')])) {
      b.setAttribute('aria-describedby', [b.getAttribute('aria-describedby'), ids].filter(Boolean).join(' '));
    }
  }
  return h('li', { class: `machine${offline ? ' offline' : ''}${selected ? ' selected' : ''}`, dataset: { deviceId: id, deviceName: name } }, block, errors, tile);
}

export function renderMachines(list, { presence, me, current, onSelect, onLaunch = () => {}, status = () => '', facts = null, load = () => null, known = null, forget = null, updates = null, signInDevices = null, diagnose = null, now = Date.now() }) {
  const hosts = presence.filter((d) => d.id !== me && d.kind === 'host');
  const all = current === ALL;
  const offline = offlineMachines({ presence, known, me });
  // The accessor may retain a record after its machine leaves; presence, not the record, is online truth.
  const loads = new Map([...hosts, ...offline].map((d) => {
    const online = hosts.some((host) => host.id === d.id), record = load(d.id);
    const bars = online && record == null ? null : loadBars(record, { online });
    const free = bars?.memory.percent == null ? null : `${(record.memory.availableBytes / 1e9).toLocaleString(undefined, { maximumFractionDigits: 1 })} GB free`;
    return [d.id, bars ? { ...bars, memory: { ...bars.memory, free } } : null];
  }));
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
  const summary = list.parentElement?.querySelector(':scope > .machine-summary');
  if (summary) {
    const total = hosts.length + offline.length;
    summary.textContent = total ? `${total} machine${total === 1 ? '' : 's'} \u00b7 ${hosts.length} online` : '';
    summary.hidden = total === 0;
  }
  // Drawn again only when something in it changes. It is asked to redraw on every session
  // update of every machine shown, and a button replaced between a tap's touch and its release
  // takes no click: a tap at startup, with every machine sending its sessions, was lost.
  const key = JSON.stringify([current, hosts.map((d) => [d.id, d.name, lines.get(d.id).key, rows.get(d.id).key, signInRows.get(d.id).key, diagnoses.get(d.id).key]),
    // lastSeen itself, not only how it reads: Forget sends it, to tell a machine that came back.
    offline.map((k) => [k.id, k.name, k.lastSeen, k.start ?? null, ago(k.lastSeen, now), forget?.stateOf(k.id) ?? null]), !!forget, [...restarting]]);
  const loadKey = JSON.stringify([...loads].map(([id, bars]) => [id, bars?.state, bars?.cpu, bars?.memory, measurementAge(bars)]));
  if (drawnAs.get(list) === key) {
    if (drawnMachineLoads.get(list) !== loadKey) {
      // Load-only changes must not remove a button between pointer down and release.
      for (const row of list.children) {
        const bars = loads.get(row.dataset.deviceId);
        const age = row.querySelector('.tile-measured');
        if (age) { age.textContent = measurementAge(bars); }
        const tracks = row.querySelectorAll('.machine-load');
        tracks[0]?.replaceWith(machineLoadBar(bars?.cpu, 'CPU', bars));
        tracks[1]?.replaceWith(machineLoadBar(bars?.memory, 'Memory', bars));
      }
      drawnMachineLoads.set(list, loadKey);
    }
    return hosts.length;
  }
  drawnMachineLoads.set(list, loadKey);
  drawnAs.set(list, key);
  // Every change redraws the list, so the button that had focus is replaced: Forget's first
  // press redraws it armed, and a keyboard's second press must land on the new one.
  const active = list.ownerDocument?.activeElement;
  const had = active && list.contains(active) ? { id: active.closest('li')?.dataset.deviceId, cls: ['machine-forget', 'machine-button', 'machine-update-check', 'machine-update-install', 'machine-update-now', 'machine-check-all', 'machine-diagnose', 'machine-diagnose-close', 'machine-diagnose-review', 'machine-diagnose-copy', 'machine-diagnostics', 'machine-launch', 'machine-view', 'tile-running-toggle', 'machine-running-view', 'machine'].find((c) => active.classList.contains(c)) } : null;
  const expanded = new Set([...list.querySelectorAll('.tile-running-toggle[aria-expanded="true"]')].map((el) => el.closest('li').dataset.deviceId));
  list.replaceChildren(...hosts.map((d) => {
    const selected = d.id === current || (all && hosts.length === 1);
    return machineRow({
      id: d.id, name: d.name, select: () => onSelect(d.id), selected,
      facts: lines.get(d.id).el, state: selected ? 'Viewing' : 'Online',
      tile: machineTile({
        name: d.name, raw: facts?.(d.id) ?? null, bars: loads.get(d.id), onSelect: () => onSelect(d.id), onLaunch: () => onLaunch(d.id),
        more: [rows.get(d.id).el, signInRows.get(d.id).el, diagnoses.get(d.id).el], expanded: expanded.has(d.id),
      }),
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
        machineHealth(loads.get(k.id)),
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
    const row = [...list.children].find((li) => li.dataset.deviceId === had.id);
    const again = (had.cls === 'machine' ? row : row?.querySelector(`.${had.cls}`)) ?? row?.querySelector('.machine-update-now')
      ?? row?.querySelector('.machine-update-check') ?? row;
    if (again === row && row) { row.tabIndex = -1; }
    again?.focus();
  }
  return hosts.length;
}
