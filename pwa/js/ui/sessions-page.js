// The sessions on screen and everything a card or the launch panel sends, for the machine
// chosen or for every machine at once: the same wiring in the live app and in the demo
// (../main.js). Each card is its own machine's: what it shows it may do comes from that
// machine's hello (capabilities, read-only and why), and what it sends goes to that machine's
// channel and no other (../answer-service.js, current(hostId)).
//
// With every machine shown (ALL, ../host-session.js), as the Windows app's "All machines":
//   * every online machine's sessions in one list, in the person's order (../session-order.js),
//     each card naming its machine;
//   * a machine that can't be reached says so in a line of its own, and the others go on;
//   * the launch panel asks which machine to launch on, starting from the one last used.
import { h } from './dom.js';
import { SessionListView, renderMachines, renderMachineTabs } from './views.js';
import { LaunchPanel } from './launch-view.js';
import { AnswerService } from '../answer-service.js';
import { ReplyService } from '../reply-service.js';
import { StopService } from '../stop-service.js';
import { LaunchService } from '../launch-service.js';
import { LAUNCH, TRUST } from '../launch.js';
import { TRANSFER } from '../transfer.js';
import { Capabilities } from '../protocol.js';
import { AttachmentLimits } from '../attachments.js';
import { maxPlain } from '../blobs.js';
import { SessionOrder, SORTS, SORT_LABELS } from '../session-order.js';
import { NeedsYouLane } from './needs-you-view.js';
import { AppBadge } from '../needs-you.js';

// The machine last launched on, or picked to launch on, with every machine shown.
export const LAUNCH_HOST = 'agent-bridge.app.launchHost';
// Whether the person hid the inspector, at the three-pane width (ui.md §5.2).
export const INSPECTOR = 'agent-bridge.app.inspector';

// The widths of ui.md §5.2: three panes from 1280, two from 760, one column below.
export const WIDE = '(min-width: 1280px)';
export const PHONE = '(max-width: 759px)';

const plural = (n, one, many) => `${n} ${n === 1 ? one : many}`;

// The largest file a reply can carry through the blob store: what the machine and the relay
// both take, and never over the protocol's 100 MiB; 0 when either has none.
export function largeLimit(capabilities, relayMax) {
  if (!capabilities?.includes(Capabilities.sessionsAttachBlob) || !(relayMax > 0)) { return 0; }
  return Math.min(AttachmentLimits.blobBytes, maxPlain(relayMax));
}

// What the sessions list says about a machine shown that isn't open, or '' when it is.
// named: with other machines shown, so every line says which machine it is about.
export function hostNote(link, name, { online, named = false }) {
  const about = (text) => (named ? `${name}: ${text}` : text);
  switch (link.phase) {
    case 'waiting':
      return link.error ? about(link.error) : online ? `Connecting to ${name}…` : `${name} is offline. Its sessions will show when it is back.`;
    case 'opening': return `Opening an end-to-end encrypted channel to ${name}…`;
    case 'unsupported': return about(link.error);
    case 'failed': return `Couldn't reach ${name}: ${link.error}. Trying again.`;
    default: return '';
  }
}

const OVER_LIST = Object.freeze({ overList: true });

// A delegate opened in a session pushed over the list (a phone's, ui.md §5.2): one more entry, so
// the system's Back (Android's button or gesture, iOS's edge swipe, platform.js) closes it before
// it leaves the session, and the session before it leaves the list (#168).
const overDelegate = (id) => Object.freeze({ overList: true, delegate: id });
const DELEGATE = 'details.delegate-detail';

export class SessionsPage {
  #shownCaps = new Map();
  // The session the address names (#/sessions/<machine>/<session>), until it has arrived.
  #want = null;
  // Whether the address names a session: on a phone, its view is pushed over the list.
  #open = false;
  #inspectorHidden = false;
  // What had focus in the list when a session was opened on a phone.
  #returnTo = null;
  // The machine last launched on or picked to launch on: kept here, and on the device as far as
  // its storage allows, so a picker never depends on a write that may fail.
  #launchHost = null;
  #unroute = null;
  // The delegate a phone's back stack has an entry for (#168), and the listeners that keep it.
  #delegateShown = null;
  #listening = null;
  #stepping = false;

  // $: getElementById. hosts: a HostSession. connection: presence, known and ready.
  // blobs(): the relay's blob store, or null; relayMax(): the largest blob it takes.
  // transfers(hostId): what launching on that machine may bring from the others, or null.
  // trust(hostId): how a folder-trust question on that machine is answered from here, or null.
  // status(id): a machine's status line. forget: a ForgetControl for offline machines, or null.
  // updates: an UpdateControl for the machines that offer updates (#51), or null.
  // shell: the pages and app bar (./shell.js), or null: where a session shown goes, and where
  // machines online and a waiting folder-trust question are said.
  constructor({ $, hosts, connection, me = null, shell = null, blobs = () => null, relayMax = () => 0, retryMs = 0, transfers = () => null, trust = () => null, status = () => '', facts = null, forget = null, updates = null, storage = globalThis.localStorage, badge = new AppBadge(), onRendered = () => {} }) {
    Object.assign(this, { $, hosts, connection, me, shell, relayMax, transfers, trust, status, facts, forget, updates, storage, onRendered });
    const current = (hostId) => hosts.route(hostId);
    const timing = retryMs ? { retryMs } : {};
    this.answers = new AnswerService({ current, ...timing });
    this.replies = new ReplyService({ current, ...timing, blobs });
    this.stops = new StopService({ current, ...timing });
    this.launches = new LaunchService({ current, ...timing });
    this.launchPanel = new LaunchPanel($('launch'), {
      service: this.launches,
      jump: (id, hostId) => this.showSession(id, hostId),
      pick: (hostId) => { this.#rememberLaunch(hostId); this.#drawLaunch(); },
      // A launch is remembered by the machine it was pressed for: the next one starts there.
      // At the press, not when it is sent: one resent after its machine comes back must not
      // take the picker from a machine picked since.
      launched: (hostId) => this.#rememberLaunch(hostId),
      waiting: (on) => this.shell?.setAttention('launch', on),
    });
    this.order = new SessionOrder({ storage });
    const media = (q) => globalThis.matchMedia?.(q) ?? { matches: false, addEventListener() {} };
    this.wide = media(WIDE);
    this.phone = media(PHONE);
    this.#inspectorHidden = this.#read(INSPECTOR) === 'hidden';
    this.view = new SessionListView($('session-list'), hosts.sessions, {
      views: $('session-views'),
      layout: () => ({ inspector: this.wide.matches && !this.#inspectorHidden }),
      // Only while Sessions shows: news that comes while another page does waits for it.
      viewing: () => (!shell || shell.route === 'sessions') && (!this.phone.matches || this.#open),
      fetchDetail: (hostId, id) => {
        const client = hosts.link(hostId)?.client;
        return client ? client.getSession(id) : Promise.reject(new Error('not connected'));
      },
      fetchDelegate: (hostId, id, agent) => {
        const client = hosts.link(hostId)?.client;
        return client ? client.getSubagent(id, agent) : Promise.reject(new Error('not connected'));
      },
      machine: (hostId) => ({ name: hosts.nameOf(hostId) ?? 'another machine', show: this.#several() }),
      answering: (hostId) => this.answering(hostId),
      order: this.order,
      shows: (hostId) => hosts.shows(hostId),
      settled: (hostId) => hosts.link(hostId)?.client?.synced !== false,
    });
    this.view.onRender = (n) => { this.#drawCount(n); this.#settleSelection(); };
    this.view.onSelect = (card) => this.#choose(card);
    this.view.onBack = () => this.#back();
    this.view.onInspector = () => this.#toggleInspector();
    for (const m of [this.wide, this.phone]) { m.addEventListener('change', () => this.#resized()); }
    this.#drawPanes();
    // Needs you · N (#165): above the list, and apart from it. Its count is the app's badge.
    this.lane = new NeedsYouLane($('needs-you'), hosts.sessions, {
      answering: (hostId) => this.answering(hostId),
      machine: (hostId) => ({ name: hosts.nameOf(hostId) ?? 'another machine' }),
      labelled: () => this.#several(),
      open: (hostId, id) => this.showSession(id, hostId),
      badge,
    });
    this.#sortMenu($('session-sort'));
    // A new channel to a machine: what waits to be resent to it goes now, and only to it.
    hosts.onOpen((id) => {
      for (const s of [this.answers, this.replies, this.stops, this.launches]) { s.ready(id); }
      this.launchPanel.reopened(id);
    });
    hosts.onChange(() => this.refresh());
    if (shell) {
      // Search narrows the sessions list; the launch page's panel is open while it shows, and
      // asks its machine again each time it opens.
      shell.onSearch((q) => this.view.setFilter(q));
      // On a phone a pushed session hides the list and its search: back to the list first.
      shell.beforeSearch = () => {
        if (!this.phone.matches || !this.#open || shell.route !== 'sessions') { return false; }
        const popped = this.#overList() && globalThis.history?.length > 1;
        this.#back();
        return popped;
      };
      // Kept, so a page retired with its account (clear()) stops answering routes: a second
      // sign-in in the same tab would otherwise have two pages rendering into one list.
      this.#unroute = shell.onRoute(({ route, rest }) => {
        this.launchPanel.setOpen(route === 'launch');
        // Back on Sessions, what it shows is seen now.
        if (route === 'sessions') { this.#route(rest); this.view.schedule(); this.view.layoutChanged(); }
      });
      this.launchPanel.setOpen(shell.route === 'launch');
      if (shell.route === 'sessions') { this.#route(shell.rest); }
      this.view.setFilter(shell.query);
      this.#delegateBack($('session-views'));
    }
  }

  // The delegate level of a phone's back stack: opening a delegate in a pushed session adds an
  // entry (one, however many are opened), closing it by hand takes the entry back, and going back
  // from it closes the delegate. The entry is the session's own address, so a reload or a link
  // never lands on a delegate: a reload on one goes back to its session.
  #delegateBack(views) {
    const win = globalThis.window;
    if (!views || !win) { return; }
    this.#listening = new AbortController();
    const { signal } = this.#listening;
    if (this.#delegateLevel()) { this.#stepBack(); }
    // toggle doesn't bubble: it is heard on its way down.
    views.addEventListener('toggle', (e) => {
      const d = e.target;
      if (!d?.matches?.(DELEGATE)) { return; }
      const id = d.closest('.delegate')?.dataset.id ?? '';
      const state = win.history.state;
      if (d.open) {
        if (state?.delegate === id || !this.#pushed()) { return; }
        const url = win.location.href;
        if (this.#delegateLevel()) { win.history.replaceState(overDelegate(id), '', url); } else { win.history.pushState(overDelegate(id), '', url); }
        this.#delegateShown = id;
      } else if (state?.delegate === id && this.phone.matches) {
        this.#stepBack();
      }
    }, { capture: true, signal });
    win.addEventListener('popstate', () => {
      this.#stepping = false;
      const want = this.#delegateLevel() ? win.history.state.delegate : null;
      const had = this.#delegateShown;
      this.#delegateShown = want;
      if ((had === null && want === null) || !this.phone.matches) { return; }
      // Every view's, not only the selected one's: going back from a delegate to the list (the
      // header's Back) has deselected its session by now, and a delegate left open there would be
      // open without its step when the session is opened again or gone forward to.
      for (const d of views.querySelectorAll(`${DELEGATE}[open]`)) {
        if (d.closest('.delegate')?.dataset.id !== want || !this.view.selected?.view?.contains(d)) { d.open = false; }
      }
      // Forward again: the delegate it was on, where it still is.
      const v = this.view.selected?.view;
      const row = want && v ? [...v.querySelectorAll('.delegate')].find((el) => el.dataset.id === want)?.querySelector(DELEGATE) : null;
      if (row && !row.open) {
        this.view.selected.setTab('delegates');
        row.open = true;
      }
    }, { signal });
  }

  // On a phone, a session pushed over the list: where a delegate is a step of its own.
  #pushed() { return this.phone.matches && this.#open && this.shell?.route === 'sessions' && this.#overList(); }

  #delegateLevel() { return typeof globalThis.history?.state?.delegate === 'string'; }

  // Back one step from a delegate's entry, once: both width queries report one resize, and a
  // second back before the first lands would go past the session to the list.
  #stepBack() {
    if (this.#stepping) { return; }
    this.#stepping = true;
    globalThis.history.back();
  }

  // Every machine shown, more than one of them: the cards say which machine each is on.
  #several() { return this.hosts.all && this.hosts.shownIds().length > 1; }

  // Who answers a machine's questions, sends its replies and ends its sessions, and whether
  // it takes each: null for a machine not shown.
  answering(hostId) {
    const link = this.hosts.link(hostId);
    if (!link) { return null; }
    return {
      service: this.answers, replies: this.replies, stops: this.stops, hostId,
      capabilities: link.capabilities, readOnly: link.machine?.readOnly ?? null,
      largeAttachments: () => largeLimit(link.capabilities, this.relayMax()),
    };
  }

  // A session, once it has arrived: selected on the sessions page, its row scrolled to and its
  // view shown (pushed, on a phone). False until then.
  showSession(id, hostId = null) {
    const card = this.view.card(hostId, id);
    if (!card) { return false; }
    // A launch whose session search would hide: the search goes, and the session shows.
    if (this.view.hidden(card)) { this.shell?.closeSearch(); }
    // Its question, or what it was opened for, is in the conversation, which a tab may hide.
    card.setTab('conversation');
    this.#choose(card);
    for (const el of [card.element, card.view]) {
      el.classList.add('linked');
      setTimeout(() => el.classList.remove('linked'), 4000);
    }
    card.element.scrollIntoView({ block: 'nearest' });
    card.els.titleText.tabIndex = -1;
    card.els.titleText.focus({ preventScroll: true });
    // A question it was opened for is in sight, however far its conversation was scrolled.
    card.view.querySelector('.question-slot .question')?.scrollIntoView({ block: 'nearest' });
    return true;
  }

  // A row chosen: the address names it, so a reload, a resize or the browser's back keeps it.
  // On a phone that is a new entry (Back returns to the list); on a desktop it replaces the last.
  #choose(card) {
    const rest = [card.hostId ?? '', card.id];
    // What had focus in the list (a row, a Needs you entry) gets it back when a phone returns there.
    const had = globalThis.document?.activeElement;
    this.#returnTo = had && this.$('list-pane')?.contains(had) ? had : card.els.open;
    if (!this.shell) { this.view.select(SessionListView.key(card.hostId, card.id)); this.#open = true; this.#drawPanes(); return; }
    const phone = this.phone.matches;
    const onSessions = this.shell.route === 'sessions';
    const already = onSessions && this.shell.rest.join('/') === rest.join('/');
    if (already) { this.#route(rest); return; }
    if (!phone) {
      // From another page (Launch, say) a new entry, so back returns there.
      const onSession = onSessions && this.shell.rest.length >= 2;
      this.shell.go('sessions', { rest, replace: onSessions, state: onSession ? globalThis.history?.state ?? null : null });
      return;
    }
    // A phone's back from a session is always the list: one session replaces another, and from
    // another page (Launch, a notification's) the list goes in first.
    const onSession = onSessions && this.shell.rest.length >= 2;
    if (onSession && this.#delegateLevel()) {
      // Over a delegate's entry: back to its session's first, so the one chosen replaces that.
      globalThis.addEventListener('popstate', () => this.#choose(card), { once: true });
      globalThis.history.back();
      return;
    }
    if (onSession) {
      this.shell.go('sessions', { rest, replace: true, state: globalThis.history?.state ?? null });
    } else {
      if (!onSessions) { this.shell.go('sessions'); }
      this.shell.go('sessions', { rest, state: OVER_LIST });
    }
    // The row that had focus went with the list: the session's title has it now.
    const title = card.els.titleText;
    title.tabIndex = -1;
    title.focus({ preventScroll: true });
  }

  // Whether this entry is a session's with the list beneath it (history.state, so it holds
  // across a reload and the browser's back and forward).
  #overList() { return globalThis.history?.state?.overList === true; }

  // On a phone, a session's entry always has the list beneath it, so back from it is the list,
  // not out of the app: an address opened directly, or a desktop's that became a phone's.
  #listBeneath() {
    if (!this.phone.matches || !this.#open || this.shell?.route !== 'sessions' || this.#overList()) { return; }
    const rest = this.shell.rest;
    this.shell.go('sessions', { replace: true });
    this.shell.go('sessions', { rest, state: OVER_LIST });
  }

  // The address said which session, or none.
  #route(rest) {
    if (rest.length >= 2) {
      this.#want = SessionListView.key(rest[0], rest[1]);
      this.#open = true;
      // A machine not shown (a link, or a reload after another was picked) is shown on its own,
      // as a notification's is, without changing what is remembered; until the account's
      // machines are known, startup does the same with what the address names.
      if (this.hosts.selection !== null && rest[0] && !this.hosts.shows(rest[0])) { this.hosts.visit(rest[0]); }
    } else {
      const was = this.#open;
      this.#want = null;
      this.#open = false;
      this.#settleSelection();
      this.#drawPanes();
      // On a phone the view that had focus went: focus goes back where it came from in the list,
      // or to the list's heading.
      // Only focus the session took with it: focus already in a field (search, typed into on the
      // way here) or in the list stays. (WebKit gives a pressed button's focus to the nearest
      // focusable ancestor, the Sessions section, so that counts as lost too.)
      const active = globalThis.document?.activeElement;
      const lost = !(active?.matches?.('input, textarea, select') || this.$('list-pane')?.contains(active));
      if (was && this.phone.matches && lost) {
        const back = this.#returnTo?.isConnected && this.#returnTo.getClientRects().length ? this.#returnTo : this.$('sessions-h');
        if (back && !back.matches('button, a, [tabindex]')) { back.tabIndex = -1; }
        back?.focus({ preventScroll: false });
      }
      this.#returnTo = null;
      return;
    }
    this.#settleSelection();
    this.#drawPanes();
    this.#listBeneath();
  }

  // What is selected, after the list or the address changed: the session the address names,
  // once it has arrived; one that went is let go. With none named, a desktop shows the first
  // row's (without saying so in the address), and a phone shows the list.
  #settleSelection() {
    this.#pickSelection();
    this.#drawPanes();
  }

  #pickSelection() {
    const view = this.view;
    if (!view) { return; }
    if (this.#want) {
      const card = view.card(this.#want.split('\n')[0], this.#want.split('\n')[1]);
      if (card) {
        if (view.selected !== card) { view.select(this.#want); }
        return;
      }
      const [hostId] = this.#want.split('\n');
      // Named but gone, once its machine has sent its whole list: back to the list.
      if (this.hosts.link(hostId)?.client?.synced === true) {
        this.#want = null;
        if (this.shell?.route === 'sessions' && this.shell.rest.length) { this.#back(); return; }
      } else {
        // Still on its way: nothing shown meanwhile, so another session's Reply and End session
        // never stand under an address that names this one.
        if (view.selected) { view.select(null); }
        return;
      }
    }
    if (this.phone.matches) {
      if (view.selected) { view.select(null); }
      return;
    }
    const shown = view.shownCards();
    if (!view.selected || !shown.includes(view.selected)) {
      const first = shown[0];
      if (first || view.selected) { view.select(first ? SessionListView.key(first.hostId, first.id) : null); }
    }
  }

  // Back to the list: the entry beneath, when it is the list, so the browser's own back and this
  // agree; otherwise the list replaces the session's entry. From a delegate's entry, the list is
  // two beneath.
  #back() {
    if (this.#overList() && globalThis.history?.length > 1) {
      globalThis.history.go(this.#delegateLevel() ? -2 : -1);
      return;
    }
    this.shell?.go('sessions', { replace: true });
  }

  // The window crossed a width of §5.2. The session named stays selected; a phone that now shows
  // one gets an entry under it, so its back goes to the list rather than out of the app. A
  // delegate's entry goes with the phone's width: wider, a delegate is not a step of its own.
  #resized() {
    if (!this.phone.matches && this.#delegateLevel()) { this.#stepBack(); }
    this.#listBeneath();
    this.#settleSelection();
    this.view.layoutChanged();
    this.#drawPanes();
  }

  #toggleInspector() {
    this.#inspectorHidden = !this.#inspectorHidden;
    this.#write(INSPECTOR, this.#inspectorHidden ? 'hidden' : 'shown');
    this.view.layoutChanged();
    this.#drawPanes();
  }

  // Which panes show: on a phone, the list or the session pushed over it; the inspector, as its
  // own pane at the three-pane width unless hidden.
  #drawPanes() {
    const body = globalThis.document?.body;
    if (!body) { return; }
    body.dataset.sessionOpen = this.#open && this.view?.selected ? 'true' : 'false';
    body.dataset.inspector = this.#inspectorHidden ? 'hidden' : 'shown';
    const none = this.$('no-session');
    if (none) { none.hidden = !!this.view?.selected; }
  }

  // Everything tied to the signed-in account goes with it.
  clear() {
    this.#unroute?.();
    this.#unroute = null;
    this.#listening?.abort();
    this.#listening = null;
    this.answers.clear();
    this.replies.clear();
    this.stops.clear();
    this.launches.clear();
    this.order.clearEnded();
    this.launchPanel.setMachines(null);
    this.launchPanel.setTarget(null);
  }

  // Your order, Most recent or Machine. Where the order can't be kept on this device, the list
  // is Most recent and Your order can't be picked.
  #sortMenu(select) {
    if (!select) { return; }
    select.replaceChildren(...SORTS.map((s) => h('option', { value: s, disabled: s === 'yours' && !this.order.usable ? true : null }, SORT_LABELS[s])));
    const show = () => {
      select.value = this.order.sort;
      select.options[0].disabled = !this.order.usable;
    };
    select.addEventListener('change', () => { this.view.setSort(select.value); show(); });
    this.view.onSorted = show;
    show();
  }

  // A line in place of the sessions, for something that stopped the page altogether.
  setHostState(text) {
    const s = this.$('host-state');
    s.replaceChildren(...(text ? [h('span', { class: 'host-note' }, text)] : []));
    s.hidden = !text;
    this.#drawCount(this.hosts.sessions.size);
  }

  refresh() {
    const { hosts } = this;
    const links = hosts.links();
    for (const id of [...this.#shownCaps.keys()]) { if (!hosts.link(id)) { this.#shownCaps.delete(id); } }
    for (const link of links) {
      const caps = `${link.capabilities.join(' ')}:${link.machine?.readOnly ?? ''}`;
      if (this.#shownCaps.get(link.id) !== caps) { this.#shownCaps.set(link.id, caps); this.view.refreshQuestions(link.id); }
    }
    // What a machine takes may have changed, and machine names with it: the lane says so too.
    this.lane.render();
    this.view.refreshMachines();
    this.$('sessions').hidden = hosts.selection === null;
    // The page is Sessions; which machines it shows is said to a screen reader in the heading,
    // and to the eye by the machine tabs.
    const heading = this.#heading();
    const on = heading.replace(/^Sessions/, '');
    this.$('sessions-h').replaceChildren('Sessions', ...(on ? [h('span', { class: 'sr-only' }, on)] : []));
    this.#drawNotes(links);
    this.#drawLaunch();
    this.drawMachines();
  }

  #heading() {
    const { hosts } = this;
    if (hosts.selection === null) { return 'Sessions'; }
    const ids = hosts.shownIds();
    if (hosts.all && ids.length !== 1) { return ids.length ? 'Sessions on all machines' : 'Sessions'; }
    return `Sessions on ${hosts.nameOf(ids[0]) ?? 'your machine'}`;
  }

  #drawNotes(links) {
    const { hosts, connection } = this;
    let lines;
    if (hosts.all && links.length > 1 && !connection.ready) {
      // One line for the relay, not one per machine.
      lines = [[null, 'The relay connection dropped; reconnecting.']];
    } else {
      const named = this.#several();
      lines = links.map((l) => [l.id, hostNote(l, hosts.nameOf(l.id) ?? 'your machine', { online: hosts.isOnline(l.id), named })]).filter(([, text]) => text);
    }
    const s = this.$('host-state');
    s.replaceChildren(...lines.map(([id, text]) => h('span', { class: 'host-note', dataset: id ? { hostId: id } : null }, text)));
    s.hidden = lines.length === 0;
    this.#drawCount(hosts.sessions.size);
  }

  #drawCount(n) {
    const { hosts, $ } = this;
    const several = this.#several();
    const machines = new Set(several ? hosts.sessions.list().map((e) => e.hostId) : []).size;
    // One machine shown: named here, since the machine tabs may be gone and the rows don't say
    // which machine they are on, yet it is the one answers and replies go to.
    const ids = hosts.shownIds();
    const one = !several && ids.length === 1 ? hosts.nameOf(ids[0]) : null;
    $('session-count').textContent = !n ? '' : several ? `${n} live on ${plural(machines, 'machine', 'machines')}` : one ? `${n} live on ${one}` : `${n} live`;
    const links = hosts.links();
    const synced = (l) => l.phase === 'open' && l.client?.synced !== false;
    // Only once every machine shown has said what it has, or that it can't: an empty list is
    // not news while one is still connecting.
    const settled = links.length
      ? links.some(synced) && links.every((l) => synced(l) || l.phase === 'unsupported')
      : hosts.all && this.connection.ready;
    const empty = $('sessions-empty');
    const query = this.view.filter;
    if (query && n > 0) {
      // Search finds nothing among sessions that are there.
      empty.hidden = this.view.shownCount !== 0;
      empty.textContent = 'No sessions match your search.';
      this.onRendered(n);
      return;
    }
    empty.hidden = n !== 0 || !settled;
    empty.textContent = hosts.all && links.length !== 1
      ? links.length ? 'No live sessions on any of your machines.' : 'No live sessions: none of your machines is online.'
      : `No live sessions on ${hosts.nameOf(links[0]?.id) ?? 'this machine'}.`;
    this.onRendered(n);
  }

  // The launch panel: for the machine chosen, or with every machine shown, for the one picked
  // in it. While a machine reconnects its panel stays, with any launch still waiting to be
  // resent; one that comes back without sessions.launch takes it away. A machine that takes no
  // launches now (read-only, or agent-ha-bridge runs there) still gets a panel, to show a
  // folder-trust question waiting on it read-only, while it reconnects too; but with every
  // machine shown, only when none of them launches, so the picker offers only machines that do.
  #drawLaunch() {
    const { hosts, launchPanel: panel } = this;
    if (hosts.selection === null) { panel.setMachines(null); panel.setTarget(null); return; }
    const links = hosts.links();
    const launches = links.filter((l) => this.#launches(l));
    const inspect = hosts.all && launches.length ? [] : links.filter((l) => !this.#launches(l) && this.#inspectOnly(l));
    const can = [...launches, ...inspect];
    const has = (id) => !!id && can.some((l) => l.id === id);
    const pick = hosts.all ? [this.#launchHost, this.#read(LAUNCH_HOST), panel.hostId].find(has) ?? can[0]?.id ?? null : can[0]?.id ?? null;
    panel.setMachines(hosts.all ? can.map((l) => ({ hostId: l.id, name: hosts.nameOf(l.id) ?? l.id.slice(0, 8) })) : null, pick);
    panel.setTarget(pick ? this.launchTarget(pick) : null);
    // The others shown only for their questions: a question on one lights the Launch way too.
    panel.probeOthers(inspect.filter((l) => l.id !== pick && l.phase === 'open').map((l) => ({ hostId: l.id, load: this.launchTarget(l.id).load })));
  }

  #launches(l) {
    const panel = this.launchPanel;
    return l.capabilities.includes(LAUNCH.capability) && (l.phase === 'open' || (l.id === panel.hostId && !panel.inspectOnly));
  }

  #inspectOnly(l) {
    const panel = this.launchPanel;
    return l.phase === 'open' ? !!l.machine?.readOnly : l.id === panel.hostId && panel.inspectOnly;
  }

  // What the launch panel is told about a machine: a new object each time, for the same machine,
  // as a presence update hands in.
  launchTarget(id) {
    const { hosts, launchPanel: panel } = this;
    const link = hosts.link(id);
    const base = {
      hostId: id,
      machineName: hosts.nameOf(id) ?? 'your machine',
      load: () => (hosts.link(id)?.client ? hosts.link(id).client.launchOptions() : Promise.reject(new Error('not connected'))),
    };
    if (link && !this.#launches(link) && this.#inspectOnly(link)) {
      return { ...base, transfers: null, trust: null, inspectOnly: true, readOnly: link.phase === 'open' ? link.machine?.readOnly ?? null : panel.readOnly };
    }
    return {
      ...base,
      // The account's other machines' sessions, each signed for when picked: only where this one
      // takes them, and only what they hand on.
      transfers: link?.capabilities.includes(TRANSFER.capability) ? this.transfers(id) : null,
      trust: link?.capabilities.includes(TRUST.capability) ? this.trust(id) : null,
    };
  }

  drawMachines() {
    const { hosts, connection, $ } = this;
    const presence = connection.presence;
    // A machine picked on the Machines page shows its sessions.
    const n = renderMachines($('machine-list'), {
      presence, me: this.me, current: hosts.selection,
      onSelect: (id) => { hosts.select(id); this.shell?.go('sessions'); $('sessions').focus(); },
      status: (id) => this.status(id),
      facts: this.facts,
      known: connection.known ?? null,
      forget: this.forget,
      updates: this.updates,
      allStatus: hosts.all ? plural(hosts.sessions.size, 'session', 'sessions') : '',
    });
    $('machines-empty').hidden = n !== 0 || !connection.ready;
    // The machine tabs above the sessions: picking one there keeps focus on the tabs. The machine
    // chosen stays named after it drops out of presence: the host session holds on to its metadata.
    renderMachineTabs($('machine-tabs'), {
      presence, me: this.me, current: hosts.selection, controls: 'session-list',
      nameOf: (id) => hosts.nameOf(id),
      onSelect: (id) => this.#pickMachine(id),
    });
    this.shell?.setOnline(n);
  }

  // A machine picked in the tabs: a session named on another stops being the one shown, so the
  // first of the machine's rows is, rather than a pane waiting for a session it no longer lists.
  #pickMachine(id) {
    this.hosts.select(id);
    const host = this.#want?.split('\n')[0];
    if (host === undefined || this.hosts.shows(host)) { return; }
    this.#want = null;
    if (this.shell?.route === 'sessions' && this.shell.rest.length) { this.shell.go('sessions', { replace: true }); } else { this.#route([]); }
  }

  // A session route's machine, for startup: shown on its own when it isn't the one remembered.
  get routedHost() { return this.shell?.route === 'sessions' && this.shell.rest.length >= 2 ? this.shell.rest[0] || null : null; }

  #rememberLaunch(hostId) {
    this.#launchHost = hostId;
    this.#write(LAUNCH_HOST, hostId);
  }

  #read(key) { try { return this.storage?.getItem(key) ?? null; } catch { return null; } }
  #write(key, value) { try { this.storage?.setItem(key, value); } catch { /* a nicety */ } }
}

