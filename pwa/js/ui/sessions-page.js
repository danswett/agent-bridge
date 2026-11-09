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
import { LaunchOptionsCache } from '../launch-options-cache.js';
import { LAUNCH, TRUST } from '../launch.js';
import { TRANSFER } from '../transfer.js';
import { Capabilities } from '../protocol.js';
import { AttachmentLimits } from '../attachments.js';
import { maxPlain } from '../blobs.js';
import { UNSUPPORTED_SESSIONS } from '../brand.js';
import { SessionOrder, SORTS, SORT_LABELS } from '../session-order.js';
import { NeedsYouLane } from './needs-you-view.js';
import { SignInBanners } from './sign-in-view.js';
import { AppBadge } from '../needs-you.js';
import { enter, scrollBehavior, watchLane } from './motion.js';
import { QUESTION_ARRIVAL_GUARD_MS, watchMoves } from './session-card.js';
import { stopReceipts } from './receipt.js';
import { CUE_MS } from '../return-grace.js';
import { PaneDividers } from './pane-dividers.js';
import { UPDATING } from './hold.js';

// Needs you's answers (Approve once, Deny, Resend) wait as a question's do (ui.md §5.3): any
// change to the lane can put Approve once under a pointer aimed at something else (an entry
// appearing, the entries moving, those after a removed one shifting up into its place), and so
// can the lane coming back on screen. For QUESTION_ARRIVAL_GUARD_MS after either, a pointer's
// click on them is dropped before any handler sees it; a key's (detail 0) goes through. The lane
// says so (data-arriving) while it lasts. The Windows app's lane does the same (GuardLane).
// scroller: what the lane scrolls in. While the guard runs, its scrolling (a row brought into
// sight, smoothly) moves the lane under the pointer too, so each step runs the wait again from
// there; once it has run out, the person's own scrolling holds nothing back.
const LANE_ANSWERS = '.needs-approve, .needs-deny, .needs-resend';
function guardLane(list, scroller = null) {
  const now = () => globalThis.performance?.now() ?? Date.now();
  let until = 0;
  let timer = 0;
  const arm = () => {
    until = now() + QUESTION_ARRIVAL_GUARD_MS;
    list.setAttribute('data-arriving', '');
    clearTimeout(timer);
    timer = setTimeout(() => list.removeAttribute('data-arriving'), QUESTION_ARRIVAL_GUARD_MS);
  };
  // The entries themselves coming, going or moving; and anything that moves their answers without
  // that (watchMoves, #491): an answer's status appearing or wrapping in an entry above, the note
  // or machine tabs above the lane changing height, a banner or the app bar above the list
  // appearing, the window's width, and the rows below getting shorter or going while the list is
  // scrolled to its end, where the browser pulls the scroll back and the lane slides down with
  // it. Where they sit is read on screen; a scroll alone moves nothing under a pointer.
  const lane = list.parentElement ?? list;
  const moves = watchMoves({
    holder: lane,
    controls: () => [...list.querySelectorAll(LANE_ANSWERS)],
    also: [...list.children],
    moved: arm,
  });
  const changes = typeof MutationObserver === 'function' ? new MutationObserver((records) => {
    for (const r of records) {
      for (const n of r.removedNodes) { if (n.nodeType === 1) { moves.unobserve(n); } }
      for (const n of r.addedNodes) { if (n.nodeType === 1) { moves.observe(n); } }
    }
    arm();
    moves.settle();
  }) : null;
  changes?.observe(list, { childList: true });
  const click = (e) => {
    if (e.detail === 0 || !e.target.closest?.(LANE_ANSWERS)) { return; }
    // Moved since the last frame was read: found now, before the click is let through (#491). A
    // read that fails can't say the answers stayed put, so that click is dropped too (#642).
    let read = true;
    try { moves.check(); } catch { read = false; }
    if (read && now() >= until) { return; }
    e.preventDefault();
    e.stopImmediatePropagation();
  };
  list.addEventListener('click', click, true);
  const scrolled = () => { if (now() < until) { arm(); } };
  scroller?.addEventListener('scroll', scrolled, { passive: true });
  return {
    arm,
    stop() {
      changes?.disconnect();
      moves.stop();
      clearTimeout(timer);
      list.removeEventListener('click', click, true);
      scroller?.removeEventListener('scroll', scrolled);
    },
  };
}

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

const OVER_LIST = Object.freeze({ overList: true });

// A delegate opened in a session pushed over the list (a phone's, ui.md §5.2): one more entry, so
// the system's Back (Android's button or gesture, iOS's edge swipe, platform.js) closes it before
// it leaves the session, and the session before it leaves the list (#168).
const overDelegate = (id) => Object.freeze({ overList: true, delegate: id });
// The delegate a session shows on a phone: the one open, pushed in the Delegates tab's list's
// place (ui.md §6.3), and not behind another tab.
const shownDelegate = (card) => (card?.openDelegateId && card.view.dataset.tab === 'delegates' ? card.openDelegateId : null);

// Where Escape is the field's (search clears and closes on it, a text box ends a composition or
// its own popup), never a step back.
const TEXT_FIELD = 'textarea, select, input:not([type="button"], [type="checkbox"], [type="radio"], [type="submit"], [type="reset"], [type="range"], [type="color"], [type="file"], [type="image"])';
// Open over the screen, each Escape's to close first (ui.md §9.1 A5).
const POPUP = '[role="menu"], [role="dialog"], [role="alertdialog"], dialog[open], [aria-modal="true"], [aria-haspopup][aria-expanded="true"]';

export class SessionsPage {
  #shownCaps = new Map();
  #notWholeSince = null;
  #slotTimer = null;
  #wasReady = false;
  // The session the address names (#/sessions/<machine>/<session>), until it has arrived.
  #want = null;
  // Whether the address names a session: on a phone, its view is pushed over the list.
  #open = false;
  #navigating = false;
  #painted = false;
  #frame = 0;
  #unwatchLane = null;
  #unwatchNews = null;
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
  #shown = null;
  #laneGuard = null;
  #stepping = false;
  // While a popstate brings the delegates in line with the entry gone to: their own open and close
  // would otherwise step the history from inside the browser's own step.
  #syncing = false;
  // From Escape's pop until its popstate lands: a second press then would pop past the list.
  #escaping = false;

  // $: getElementById. hosts: a HostSession. connection: presence, known and ready.
  // blobs(): the relay's blob store, or null; relayMax(): the largest blob it takes.
  // transfers(hostId): what launching on that machine may bring from the others, or null.
  // trust(hostId): how a folder-trust question on that machine is answered from here, or null.
  // status(id): a machine's status line. forget: a ForgetControl for offline machines, or null.
  // updates: an UpdateControl for the machines that offer updates (#51), or null.
  // diagnose: a DiagnoseControl for each machine's Diagnose (#802), or null.
  // shell: the pages and app bar (./shell.js), or null: where a session shown goes, and where
  // machines online and a waiting folder-trust question are said.
  constructor({ $, hosts, connection, me = null, shell = null, blobs = () => null, relayMax = () => 0, retryMs = 0, transfers = () => null, trust = () => null, confirm = () => null, status = () => '', facts = null, forget = null, updates = null, signInDevices = null, diagnose = null, storage = globalThis.localStorage, badge = new AppBadge(), onRendered = () => {}, opened = () => {} }) {
    Object.assign(this, { $, hosts, connection, me, shell, relayMax, transfers, trust, confirm, status, facts, forget, updates, signInDevices, diagnose, storage, onRendered, opened });
    const current = (hostId) => hosts.route(hostId);
    const timing = retryMs ? { retryMs } : {};
    // One gate for every send (#269): nothing new is given for a machine with no route now (its
    // channel is down, or its snapshot since is not whole); its controls are disabled, and this
    // holds even if one is pressed anyway. What was sent before stays to be resent: the machine
    // checks it against the live question or session.
    const accepts = (hostId) => !!hosts.route(hostId);
    this.answers = new AnswerService({ current, accepts, ...timing, marks: 'answer' });
    this.replies = new ReplyService({ current, accepts, ...timing, blobs });
    this.stops = new StopService({ current, accepts, ...timing });
    this.launches = new LaunchService({ current, accepts, ...timing });
    // Each machine's last launch.options, asked for in the background, so Start a session shows at once (#1527).
    this.launchOptions = new LaunchOptionsCache();
    this.launchPanel = new LaunchPanel($('launch'), {
      service: this.launches,
      jump: (id, hostId) => this.showSession(id, hostId),
      pick: (hostId) => { this.#rememberLaunch(hostId); this.#drawLaunch(); },
      // A launch is remembered by the machine it was pressed for: the next one starts there.
      // At the press, not when it is sent: one resent after its machine comes back must not
      // take the picker from a machine picked since.
      launched: (hostId) => this.#rememberLaunch(hostId),
      waiting: (on) => this.shell?.setAttention('launch', on),
      sessions: (hostId) => hosts.sessions.list().filter((e) => e.hostId === hostId).map((e) => e.session.id),
      session: (hostId, id) => hosts.sessions.get(hostId, id),
    });
    this.launchOptions.watch((hostId) => this.launchPanel.cacheChanged(hostId));
    this.order = new SessionOrder({ storage });
    const media = (q) => globalThis.matchMedia?.(q) ?? { matches: false, addEventListener() {} };
    this.wide = media(WIDE);
    this.phone = media(PHONE);
    this.#inspectorHidden = this.#read(INSPECTOR) === 'hidden';
    this.view = new SessionListView($('session-list'), hosts.sessions, {
      views: $('session-views'),
      layout: () => ({ inspector: this.wide.matches && !this.#inspectorHidden, wide: this.wide.matches }),
      // Only while Sessions shows, in a page that does: news that comes while another page, or
      // the tab or app in the background, waits for it, with no scroll and no question's wait
      // spent unseen.
      viewing: () => (!shell || shell.route === 'sessions') && (!this.phone.matches || this.#open)
        && globalThis.document?.visibilityState !== 'hidden',
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
      stale: (hostId) => hosts.staleOf(hostId),
      heading: () => $('sessions-h'),
    });
    this.view.onRender = (n) => { this.#drawCount(n); this.#settleSelection(); this.#delegateStep(); };
    this.view.onSelect = (card) => this.#choose(card);
    this.view.onBack = () => this.#back();
    this.view.onInspector = () => this.#toggleInspector();
    for (const m of [this.wide, this.phone]) { m.addEventListener('change', () => this.#resized()); }
    this.#drawPanes();
    // The widths of the panes from 760 up (#1030): the dividers only draw on those widths.
    this.dividers = $('list-divider') && $('sessions')
      ? new PaneDividers({
        root: $('sessions'), list: $('list-divider'), inspector: $('inspector-divider'), storage,
        inspectorShown: () => this.wide.matches && !this.#inspectorHidden,
        onChange: () => { this.view.layoutChanged(); this.view.selected?.reshow(); },
      })
      : null;
    // Needs you · N (#165): above the list, and apart from it. Its count is the app's badge.
    this.lane = new NeedsYouLane($('needs-you'), hosts.sessions, {
      answering: (hostId) => this.answering(hostId),
      machine: (hostId) => ({ name: hosts.nameOf(hostId) ?? 'another machine' }),
      labelled: () => this.#several(),
      open: (hostId, id) => this.showSession(id, hostId),
      badge,
    });
    // An agent that needs sign-in (#800): a banner for each machine, agent and account, above the lane.
    this.signIns = $('sign-in-banners') ? new SignInBanners($('sign-in-banners'), hosts, {
      live: $('sign-in-said'),
      open: (hostId, id) => this.showSession(id, hostId),
      fallback: () => $('sessions-h'),
    }) : null;
    // An entry is news, and its mark fades in, as a row is (views.js): once the list was drawn
    // before the change that brought it, and its machine has sent its whole list. Read as the
    // change comes, since the list draws before the lane does. A machine whose list is current
    // again (#269: back, and its snapshot whole) brings back questions that were there all along,
    // hidden while it was stale: they show at once, as a machine's first list does, until the
    // next change to the sessions.
    let news = false;
    let listed = new Set();
    let returned = new Set();
    const unwatchSessions = hosts.sessions.onChange((c) => {
      news = this.view.drawn;
      returned = new Set();
      // A session registered may be the one a launch's machine was busy with, or the one a launch
      // reported unconfirmed started (#896).
      if (c?.type === 'upsert') { this.launchPanel.sessionSeen(c.hostId, hosts.sessions.get(c.hostId, c.id)); }
      // A question that came or went in the session shown is said again (sessions.focus).
      if (c?.type === 'upsert') { this.#reportOpen(); }
    });
    const unwatchHosts = hosts.onChange(() => {
      const now = new Set((hosts.shownIds?.() ?? []).filter((id) => !hosts.staleOf?.(id)));
      returned = new Set([...returned, ...[...now].filter((id) => !listed.has(id))]);
      listed = now;
    });
    this.#unwatchNews = () => { unwatchSessions(); unwatchHosts?.(); };
    this.#unwatchLane = watchLane(this.lane.list, { fresh: (el) => news && this.view.settled(el.dataset.hostId) && !returned.has(el.dataset.hostId) });
    this.#laneGuard = this.lane.list ? guardLane(this.lane.list, $('list-pane')) : null;
    this.#sortMenu($('session-sort'));
    // A new channel to a machine: what waits to be resent to it goes now, and only to it.
    hosts.onOpen((id) => {
      for (const s of [this.answers, this.replies, this.stops, this.launches]) { s.ready(id); }
      this.launchOptions.stale(id);
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
      let was = shell.route;
      this.#unroute = shell.onRoute(({ route, rest }) => {
        this.launchPanel.setOpen(route === 'launch');
        const back = route === 'sessions' && was !== 'sessions';
        was = route;
        // Sessions reports once its selection is settled, below; another page shows no session.
        if (route !== 'sessions') { this.#reportOpen(); }
        // Back on Sessions, what it shows is seen now.
        if (route === 'sessions') {
          this.#navigate(() => this.#route(rest));
          this.view.schedule();
          this.view.layoutChanged();
          // Back from another page: a question that came meanwhile is in sight now, where a
          // click may already be headed, the same wait as a question arriving; so is the lane.
          // Not a move within Sessions (a row chosen), which brings its own wait where it does.
          if (back) {
            // The session it shows is shown again: its header whole, as a row chosen brings it.
            this.view.selected?.collapse?.shown();
            this.view.selected?.reshow();
            this.#laneGuard?.arm();
          }
        }
      });
      // Back from the background (another tab, the app switcher, the back-forward cache, another
      // window over a browser still partly in sight, which leaves it visible): what Sessions
      // shows is seen now, and a question in sight waits as one arriving does, as does the lane.
      const doc = globalThis.document;
      if (doc) {
        this.#shown = new AbortController();
        const back = () => {
          if (doc.visibilityState === 'hidden' || shell.route !== 'sessions') { return; }
          this.view.schedule();
          this.view.selected?.reshow();
          this.#laneGuard?.arm();
        };
        doc.addEventListener('visibilitychange', back, { signal: this.#shown.signal });
        globalThis.addEventListener?.('pageshow', (e) => { if (e.persisted) { back(); } }, { signal: this.#shown.signal });
        // The window's own focus only (an element's doesn't reach a listener here that isn't capturing).
        globalThis.addEventListener?.('focus', back, { signal: this.#shown.signal });
        // Bubbling, so a menu, search or drag that takes Escape has it first (ui.md §9.1 A5).
        doc.addEventListener('keydown', (e) => this.#escape(e), { signal: this.#shown.signal });
      }
      this.launchPanel.setOpen(shell.route === 'launch');
      if (shell.route === 'sessions') { this.#route(shell.rest); }
      this.view.setFilter(shell.query);
      this.#delegateBack($('session-views'));
    }
  }

  // The delegate level of a phone's back stack: opening a delegate in a pushed session adds an
  // entry (one: opening another replaces the one open, and its entry with it), closing it by hand
  // takes the entry back, and going back from it closes the delegate. The entry is the session's
  // own address, so a reload or a link never lands on a delegate: a reload on one goes back to its
  // session.
  #delegateBack(views) {
    const win = globalThis.window;
    if (!views || !win) { return; }
    this.#listening = new AbortController();
    const { signal } = this.#listening;
    if (this.#delegateLevel()) { this.#stepBack(); }
    // Opened or closed from the strip, the Delegates tab, a nested list, the conversation's events
    // or its own "All delegates".
    this.view.onDelegate = () => this.#delegateStep();
    win.addEventListener('popstate', () => {
      this.#stepping = false;
      const want = this.#delegateLevel() ? win.history.state.delegate : null;
      const had = this.#delegateShown;
      this.#delegateShown = want;
      if ((had === null && want === null) || !this.phone.matches) { return; }
      const selected = this.view.selected;
      // Every session's, not only the selected one's: going back from a delegate to the list (the
      // header's Back) has deselected its session by now, and a delegate left open there would be
      // open without its step when the session is opened again or gone forward to.
      this.#syncing = true;
      try {
        for (const card of this.view.cards) {
          // Focus in the pane going goes back to its row, as the pane's own All delegates does;
          // left there, it would fall to the page. Another card's or tab's focus is left be.
          if (card.openDelegateId && (card !== selected || card.openDelegateId !== want)) { card.closeDelegate({ focus: card === selected && card.detail.element.contains(document.activeElement) }); }
        }
        // Forward again: the delegate it was on, finished since or not, where it is still listed.
        if (want && selected && shownDelegate(selected) !== want) { selected.openDelegate(want); }
      } finally {
        this.#syncing = false;
      }
      // Forward to a delegate its machine has stopped listing shows nothing: no step.
      this.#delegateStep();
    }, { signal });
    // Another tab hides an open delegate as surely as closing it does.
    const tabs = new MutationObserver(() => this.#delegateStep());
    tabs.observe(views, { subtree: true, attributes: true, attributeFilter: ['data-tab'] });
    signal.addEventListener('abort', () => { tabs.disconnect(); this.view.onDelegate = null; });
  }

  // The delegate step matches what the selected session shows: one entry while a delegate is open
  // and in view in it, pushed over the list, and none otherwise. A delegate can close, hide or go
  // without anyone pressing Back: closed by hand, behind another tab, removed by its machine's
  // update, or left open across a phone → wider → phone resize, and each would leave Back an
  // invisible step or skip a visible one.
  #delegateStep() {
    const win = globalThis.window;
    if (!this.#listening || this.#stepping || this.#syncing || !win) { return; }
    const level = this.#delegateLevel();
    if (!this.phone.matches) {
      if (level) { this.#stepBack(); }
      return;
    }
    if (!this.#pushed()) { return; }
    const id = shownDelegate(this.view.selected);
    if (!id) {
      if (level) { this.#stepBack(); }
      return;
    }
    const was = win.history.state?.delegate;
    if (level && was === id) { return; }
    if (level) { win.history.replaceState(overDelegate(id), '', win.location.href); } else { win.history.pushState(overDelegate(id), '', win.location.href); }
    this.#delegateShown = id;
  }

  // On a phone, a session pushed over the list: where a delegate is a step of its own.
  #pushed() { return this.phone.matches && this.#open && this.shell?.route === 'sessions' && this.#overList(); }

  #delegateLevel() { return typeof globalThis.history?.state?.delegate === 'string'; }

  // Escape pops one pushed screen on a phone, as the system's Back does (ui.md §5.3, §9.1 A5): a
  // delegate back to its session, a session back to the list, with focus on the row that pushed
  // it. Only when nothing nearer takes it: a menu, a popup or a dialog, a drag, search or a text
  // field (where Escape is the field's, or ends a composition); and only once a pop already on its
  // way has landed, so a key held down or pressed twice never goes past the list and out of the app.
  #escape(e) {
    if (e.key !== 'Escape' || e.defaultPrevented || e.repeat || e.isComposing || e.keyCode === 229) { return; }
    if (e.altKey || e.ctrlKey || e.metaKey || e.shiftKey) { return; }
    if (!this.phone.matches || !this.#open || this.shell?.route !== 'sessions' || this.#escaping || this.#stepping) { return; }
    const doc = globalThis.document;
    const at = e.target instanceof Element ? e.target : doc.activeElement;
    if (at?.closest?.(TEXT_FIELD) || at?.isContentEditable) { return; }
    if ([...doc.querySelectorAll(POPUP)].some((el) => el.getClientRects().length > 0)) { return; }
    const card = this.view.selected;
    if (!card) { return; }
    e.preventDefault();
    if (shownDelegate(card)) {
      // As the delegate's own Back: its row has focus, and its step in the history goes with it.
      card.closeDelegate({ focus: true });
      return;
    }
    if (this.#overList() && globalThis.history?.length > 1) {
      this.#escaping = true;
      globalThis.addEventListener('popstate', () => { this.#escaping = false; }, { once: true });
    }
    this.#back();
  }

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
  // it takes each: null for a machine not shown. stale: why nothing can be given for it now
  // (HostSession.staleOf), or null.
  answering(hostId) {
    const link = this.hosts.link(hostId);
    if (!link) { return null; }
    return {
      service: this.answers, replies: this.replies, stops: this.stops, hostId,
      capabilities: link.capabilities, readOnly: link.machine?.readOnly ?? null,
      stale: this.hosts.staleOf(hostId),
      largeAttachments: () => largeLimit(link.capabilities, this.relayMax()),
      // Signs an escaped approval's confirmation for this machine (#803), or null.
      confirm: this.confirm(hostId),
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
      // Marked for a moment, and drawn as nothing: a ring around its header or its row looked
      // like focus the person never moved (A12, #373).
      el.classList.add('linked');
      setTimeout(() => el.classList.remove('linked'), 4000);
    }
    // Scroll to a question (ui.md §5.3): smoothly, or at once under reduced motion.
    const behavior = scrollBehavior();
    // The list scrolling to its row slides Needs you under a pointer still on the entry clicked:
    // the lane waits from now, and from each step of that scroll.
    if (this.$('list-pane')?.getClientRects().length) { this.#laneGuard?.arm(); }
    card.element.scrollIntoView({ block: 'nearest', behavior });
    card.els.titleText.tabIndex = -1;
    // Focus lands for a screen reader; js/ui/focus.js draws a ring only if a key brought it (A12).
    card.els.titleText.focus({ preventScroll: true });
    // A question it was opened for is in sight, however far its conversation was scrolled.
    card.revealQuestion(behavior);
    return true;
  }

  // A row chosen: the address names it, so a reload, a resize or the browser's back keeps it.
  // On a phone that is a new entry (Back returns to the list); on a desktop it replaces the last.
  #choose(card) {
    const rest = [card.hostId ?? '', card.id];
    // What had focus in the list (a row, a Needs you entry) gets it back when a phone returns there.
    const had = globalThis.document?.activeElement;
    this.#returnTo = had && this.$('list-pane')?.contains(had) ? had : card.els.open;
    if (!this.shell) { this.view.select(SessionListView.key(card.hostId, card.id)); this.#open = true; this.#navigate(() => this.#drawPanes()); return; }
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
      this.#stepBack();
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
      // On a phone the list, and its lane, are back under a pointer that was on the session.
      if (was && this.phone.matches) { this.#laneGuard?.arm(); }
      this.#settleSelection();
      this.#drawPanes();
      // On a phone the view that had focus went: focus goes back where it came from in the list,
      // or to the list's heading.
      // Only focus the session took with it: focus already in a field (search, typed into on the
      // way here) or in the list stays; a field in the session going (a question's choice, left
      // with Escape) goes with it, so counts as lost. (WebKit gives a pressed button's focus to the
      // nearest focusable ancestor, the Sessions section, so that counts as lost too.)
      const active = globalThis.document?.activeElement;
      const field = active?.matches?.('input, textarea, select') && !this.$('session-views')?.contains(active);
      const lost = !(field || this.$('list-pane')?.contains(active));
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
      // Its popstate is on its way: nothing else steps back from the delegate's entry meanwhile.
      if (this.#delegateLevel()) { this.#stepping = true; }
      globalThis.history.go(this.#delegateLevel() ? -2 : -1);
      return;
    }
    this.shell?.go('sessions', { replace: true });
  }

  // The window crossed a width of §5.2. The session named stays selected; a phone that now shows
  // one gets an entry under it, so its back goes to the list rather than out of the app. A
  // delegate's entry goes with the phone's width and comes back with it: wider, a delegate is not a
  // step of its own.
  #resized() {
    this.#listBeneath();
    this.#settleSelection();
    this.view.layoutChanged();
    this.#drawPanes();
    this.#delegateStep();
    // Across a width of §5.2 the conversation reflows, or comes into sight as Tools moves into the
    // inspector: its question waits as one arriving does, if it is on screen.
    this.view.selected?.reshow();
  }

  #toggleInspector() {
    this.#inspectorHidden = !this.#inspectorHidden;
    this.#write(INSPECTOR, this.#inspectorHidden ? 'hidden' : 'shown');
    this.view.layoutChanged();
    this.#drawPanes();
    // The conversation reflows to its new width, or comes into sight (Tools picked moves into the
    // inspector): its question waits as one arriving does, if it is on screen.
    this.view.selected?.reshow();
    // Pane and inspector (ui.md §5.3): its column opens at once, and its content slides in from
    // the trailing edge. Hiding closes the column at once.
    if (!this.#inspectorHidden && this.wide.matches) { enter(this.view.selected?.els.inspector, 'pane', { dx: 1 }); }
  }

  // While the address moves (a row opened, Back): what #drawPanes shows then is a push or a pop.
  #navigate(draw) {
    const was = this.#navigating;
    this.#navigating = true;
    try { draw(); } finally { this.#navigating = was; }
  }

  // Whether the panes shown now have been painted: false from a change until the next frame.
  #paint() {
    this.#painted = false;
    const frame = this.#frame = (this.#frame ?? 0) + 1;
    const raf = globalThis.requestAnimationFrame;
    if (typeof raf !== 'function') { this.#painted = true; return; }
    raf(() => raf(() => { if (this.#frame === frame) { this.#painted = true; } }));
  }

  // The session shown now, if any, said to `opened` (main.js: sessions.focus and the notices of
  // that session, #1190): the one selected on the Sessions page, which on a phone shows only once
  // pushed over the list. Another page, or the list, is none.
  #reportOpen() {
    const card = this.view?.selected;
    const showing = card && (!this.shell || this.shell.route === 'sessions') && (!this.phone.matches || this.#open);
    // The question its card shows is the session's own, as the store has it now.
    const question = showing ? this.hosts.sessions.get(card.hostId, card.id)?.question?.id ?? null : null;
    try { this.opened(showing ? { hostId: card.hostId, id: card.id, question } : null); } catch (e) { console.error(e); }
  }

  // Which panes show: on a phone, the list or the session pushed over it; the inspector, as its
  // own pane at the three-pane width unless hidden.
  #drawPanes() {
    this.#reportOpen();
    const body = globalThis.document?.body;
    if (!body) { return; }
    const was = body.dataset.sessionOpen;
    body.dataset.sessionOpen = this.#open && this.view?.selected ? 'true' : 'false';
    body.dataset.inspector = this.#inspectorHidden ? 'hidden' : 'shown';
    this.dividers?.refresh();
    const none = this.$('no-session');
    if (none) { none.hidden = !!this.view?.selected; }
    // Push and pop (ui.md §5.3): on a phone the screen that comes covers the other at once, and
    // only its content slides in, from the trailing side going in and the leading side coming
    // back. Only for a move the address made (a row opened, Back) from a screen that was on show
    // for a frame at least: a session the address names at startup or on a reload, and the list
    // put beneath it then (#listBeneath), are the app starting, not a move.
    const changed = was !== body.dataset.sessionOpen;
    const moved = changed && this.#navigating && this.#painted && was !== undefined && this.phone.matches;
    if (changed) { this.#paint(); }
    if (moved) {
      const opened = body.dataset.sessionOpen === 'true';
      if (opened) {
        const content = this.view.selected?.view;
        // A session asking a question fades in where it is: an approval's command never slides
        // or starts partly off the screen.
        enter(content, 'push', { dx: content?.querySelector('.question-slot > *') ? 0 : 1 });
      } else {
        // The list's content comes back from the leading side, all but Needs you, which is there
        // at once: its commands never move.
        for (const part of this.$('list-pane')?.children ?? []) {
          if (!part.matches('.needs-you') && part.getClientRects().length) { enter(part, 'push', { dx: -1 }); }
        }
      }
    }
  }

  // Everything tied to the signed-in account goes with it.
  clear() {
    // The account's session is no longer shown: said before the page lets go of what says it.
    try { this.opened(null); } catch (e) { console.error(e); }
    this.#unroute?.();
    this.#unroute = null;
    this.#listening?.abort();
    this.#listening = null;
    this.#shown?.abort();
    this.#shown = null;
    this.#laneGuard?.stop();
    this.#laneGuard = null;
    this.#unwatchLane?.();
    this.#unwatchLane = null;
    this.#unwatchNews?.();
    this.#unwatchNews = null;
    this.signIns?.stop();
    this.signIns = null;
    this.answers.clear();
    this.replies.clear();
    this.stops.clear();
    this.launches.clear();
    stopReceipts();
    this.order.clearEnded();
    this.launchPanel.setMachines(null);
    this.launchPanel.setTarget(null);
    this.launchPanel.clear();
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
      const caps = `${link.capabilities.join(' ')}:${link.machine?.readOnly ?? ''}:${hosts.staleOf(link.id)?.reason ?? ''}`;
      if (this.#shownCaps.get(link.id) !== caps) { this.#shownCaps.set(link.id, caps); this.view.refreshQuestions(link.id); }
    }
    // What a machine takes may have changed, and machine names with it: the lane says so too.
    this.lane.render();
    this.view.refreshMachines();
    this.view.refreshStale();
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

  // The title row's slot and the connection strip (#950). Nothing here is above the list, so nothing
  // pushes a row. The slot is not announced; the strip is, once, as it comes.
  #drawNotes(links) {
    const { hosts, connection } = this;
    const grace = hosts.grace;
    const away = !grace?.active;
    const name = (l) => hosts.nameOf(l.id) ?? 'your machine';
    if (connection.ready) { this.#wasReady = true; }
    const online = links.filter((l) => hosts.isOnline(l.id));
    const whole = (l) => (l.phase === 'open' && l.client?.synced !== false) || l.phase === 'unsupported' || l.phase === 'failed';
    const notWhole = online.filter((l) => !whole(l) || (l.phase === 'open' && hosts.staleOf(l.id)?.reason === 'confirming'));
    if (!notWhole.length) { this.#notWholeSince = null; } else if (this.#notWholeSince === null) { this.#notWholeSince = grace?.active ? grace.since : Date.now(); }
    let text = '';
    const errors = away ? online.filter((l) => l.phase === 'failed' || l.phase === 'unsupported') : [];
    if (errors.length) {
      const first = errors[0];
      const line = first.phase === 'failed' ? `Couldn't reach ${name(first)}` : `${this.#several() ? `${name(first)}: ` : ''}${first.error}`;
      text = errors.length > 1 ? `${line} and ${errors.length - 1} more` : line;
    } else if (notWhole.length) {
      const wait = CUE_MS - (Date.now() - this.#notWholeSince);
      if (wait <= 0) {
        const bare = notWhole.filter((l) => !l.store?.size);
        text = bare.length ? `Connecting to ${plural(bare.length, 'machine', 'machines')}…` : UPDATING;
      } else {
        clearTimeout(this.#slotTimer);
        this.#slotTimer = setTimeout(() => this.#drawNotes(this.hosts.links()), wait);
      }
    }
    const slot = this.$('return-slot');
    if (slot.textContent !== text) { slot.textContent = text; }
    const strip = away && !connection.ready && this.#wasReady && !!hosts.selection;
    const stripEl = this.$('connection-strip');
    if (stripEl.hidden === strip) {
      const head = stripEl.closest('.sessions-head');
      const active = head.ownerDocument.activeElement;
      // The head's controls are hidden under the strip: focus on one goes to the strip, not the page.
      const moveFocus = strip && !!active && head.contains(active);
      stripEl.hidden = !strip;
      stripEl.textContent = strip ? 'Reconnecting to the relay…' : '';
      stripEl.closest('.sessions-head').dataset.strip = strip ? 'true' : 'false';
      if (moveFocus) { stripEl.tabIndex = -1; stripEl.focus(); }
    }
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
    // not news while one is still connecting. One that is offline has said all it will for now.
    const live = links.filter((l) => hosts.isOnline(l.id));
    const settled = live.length
      ? live.some(synced) && live.every((l) => synced(l) || l.phase === 'unsupported')
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
    const among = hosts.all ? live : links;
    empty.textContent = hosts.all && among.length !== 1
      ? among.length ? 'No live sessions on any of your machines.' : 'No live sessions: none of your machines is online.'
      : `No live sessions on ${hosts.nameOf(among[0]?.id) ?? 'this machine'}.`;
    this.onRendered(n);
  }

  // The launch panel: for the machine chosen, or with every machine shown, for the one picked
  // in it. While a machine reconnects its panel stays, with any launch still waiting to be
  // resent; one that comes back without sessions.launch takes it away. A machine that takes no
  // launches now (read-only, or agent-ha-bridge runs there) still gets a panel, to show a
  // folder-trust question waiting on it read-only, while it reconnects too; but with every
  // machine shown, only when none of them launches, so the picker offers only machines that do.
  // A machine whose channel is down, or whose snapshot since is not whole (#269), keeps its place
  // in the panel, held: nothing can be launched or resumed on it, nor its folder-trust questions
  // answered, until it is current again (launchTarget, held).
  #drawLaunch() {
    const { hosts, launchPanel: panel } = this;
    if (hosts.selection === null) { panel.setMachines(null); panel.setTarget(null); return; }
    const links = hosts.links();
    const launches = links.filter((l) => this.#launches(l));
    const inspect = hosts.all && launches.length ? [] : links.filter((l) => !this.#launches(l) && this.#inspectOnly(l));
    const can = [...launches, ...inspect];
    const has = (id) => !!id && can.some((l) => l.id === id);
    const pick = hosts.all ? [this.#launchHost, this.#read(LAUNCH_HOST), panel.hostId].find(has) ?? can[0]?.id ?? null : can[0]?.id ?? null;
    // Every machine that launches and is open is asked in the background, so its form is ready before it is picked.
    // Raw ask, not launchTarget().load: that one goes through the cache, which would wait on itself.
    this.launchOptions.sync(launches.filter((l) => l.phase === 'open').map((l) => ({ hostId: l.id, load: () => this.#askMachine(l.id) })));
    panel.setMachines(hosts.all ? can.map((l) => ({ hostId: l.id, name: hosts.nameOf(l.id) ?? l.id.slice(0, 8) })) : null, pick);
    panel.setTarget(pick ? this.launchTarget(pick) : null);
    // The others shown only for their questions: a question on one lights the Launch way too.
    panel.probeOthers(inspect.filter((l) => l.id !== pick && l.phase === 'open').map((l) => ({ hostId: l.id, load: this.launchTarget(l.id).load })));
  }

  // A machine that launches, by what its last hello offered: open now, or kept through a gap
  // (#269), when it stays in the picker held, so picking another machine does not take it away.
  // The one shown only for its questions, while it reconnects, stays so.
  #launches(l) {
    const panel = this.launchPanel;
    if (!l.capabilities.includes(LAUNCH.capability) || l.phase === 'unsupported') { return false; }
    return l.phase === 'open' || !(l.id === panel.hostId && panel.inspectOnly);
  }

  #inspectOnly(l) {
    const panel = this.launchPanel;
    return l.phase === 'open' ? !!l.machine?.readOnly : l.id === panel.hostId && panel.inspectOnly;
  }

  #askMachine(id) {
    const client = this.hosts.link(id)?.client;
    return client ? client.launchOptions() : Promise.reject(new Error('not connected'));
  }

  // What the launch panel is told about a machine: a new object each time, for the same machine,
  // as a presence update hands in. held: why nothing can be sent to it now (#269), or null.
  launchTarget(id) {
    const { hosts, launchPanel: panel } = this;
    const link = hosts.link(id);
    const stale = hosts.staleOf(id);
    const name = hosts.nameOf(id) ?? 'your machine';
    const held = !stale ? null : stale.reason === 'offline'
      ? `${name} is offline. Sessions can be started on it once it is back.`
      : stale.reason === 'unsupported'
        ? `${name} ${UNSUPPORTED_SESSIONS} now, so sessions can't be started on it from here.`
        : `${name} ${stale.reason === 'confirming' ? 'is back' : 'is reconnecting'}. Sessions can be started on it once it has sent its sessions again.`;
    const base = {
      hostId: id,
      machineName: name,
      held,
      // Through the cache: one ask at a time per machine, and its answer is kept for the next time the page shows.
      load: ({ fresh = false } = {}) => this.launchOptions.ask(id, () => this.#askMachine(id), { fresh }),
      cached: () => this.launchOptions.get(id),
    };
    if (link && !this.#launches(link) && this.#inspectOnly(link)) {
      return { ...base, transfers: null, trust: null, inspectOnly: true, readOnly: link.phase === 'open' ? link.machine?.readOnly ?? null : panel.readOnly };
    }
    return {
      ...base,
      // The account's other machines' sessions, each signed for when picked: only where this one
      // takes them, and only what they hand on.
      transfers: link?.capabilities.includes(TRANSFER.capability) ? this.transfers(id) : null,
      // A held machine's folder-trust questions are shown, not answered.
      trust: !held && link?.capabilities.includes(TRUST.capability) ? this.trust(id) : null,
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
      signInDevices: this.signInDevices,
      diagnose: this.diagnose,
      allStatus: hosts.all ? plural(hosts.sessions.size, 'session', 'sessions') : '',
    });
    $('machines-empty').hidden = n !== 0 || !connection.ready;
    // The machine tabs above the sessions: picking one there keeps focus on the tabs. The machine
    // chosen stays named after it drops out of presence: the host session holds on to its metadata.
    renderMachineTabs($('machine-tabs'), {
      presence, me: this.me, current: hosts.selection, controls: 'session-list',
      nameOf: (id) => hosts.nameOf(id),
      // With every machine shown, one that left keeps its tab, offline, while its rows stay (#269).
      away: hosts.away(),
      // The tabs look as last seen through the grace, and change with the rows (#950).
      quiet: hosts.grace?.active === true,
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

  // The list on screen, for a sign-in push's banner, which is on it (#969): the sessions page from
  // any other, and on a phone the list rather than a session over it. A new entry, so back returns
  // to where the person was. A desktop shows the list beside a session.
  showList() {
    if (!this.shell || (this.shell.route === 'sessions' && !(this.phone.matches && this.shell.rest.length))) { return; }
    this.shell.go('sessions');
  }

  #rememberLaunch(hostId) {
    this.#launchHost = hostId;
    this.#write(LAUNCH_HOST, hostId);
  }

  #read(key) { try { return this.storage?.getItem(key) ?? null; } catch { return null; } }
  #write(key, value) { try { this.storage?.setItem(key, value); } catch { /* a nicety */ } }
}
