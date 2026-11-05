// Which machines the app is looking at, and keeping a live subscription to each across relay
// reconnects. This is where a channel becomes a HostClient feeding that machine's own
// SessionsStore:
//
//   connection.openChannel(hostId) -> Channel (Noise, end to end)
//   new HostClient(channel, { store }) -> hello() -> subscribe()
//   host events (session.upsert / session.remove / sessions.synced) -> store -> views
//
// The choice is one machine, or every machine at once (ALL), as the Windows app's picker
// offers ("All machines"). Each machine shown has a link of its own: one channel, its own
// store, what it said in hello, and its own retries, so one that fails or is slow says so on
// its own and holds up none of the others. A machine is connected to only while it is shown,
// and at most a few handshakes are under way at once, so opening the app with ten machines
// does not start ten handshakes in the same instant.
//
// The choice is remembered, so opening the app from the Home Screen goes straight back to
// it, and a link with ?host=<device id> (a notification tap) shows that machine. With nothing
// remembered the app shows every machine.
//
// A machine that leaves presence keeps its link and its last list, offline and stale (#269,
// ui.md §2.2): a host's relay connection drops for a few seconds on a restart, an update or a
// network blip, and taking its sessions with it made them vanish and come back, grouped by
// machine, every few minutes. A link whose channel is down for any other reason (this device's
// relay link dropped, the host restarted) is stale too, as the Windows app treats it: what it
// last showed may have changed. So is one whose channel lost events (a gap, #427), until the
// snapshot it asks for again is whole. Nothing is sent to a stale machine (route() is null, the one
// gate every send goes through) until a channel opened since has delivered a whole snapshot.
import { HostClient } from './host-client.js';
import { Capabilities, Events, Ops, PROTOCOL_VERSION } from './protocol.js';
import { SessionsStore } from './sessions-store.js';
import { UNSUPPORTED_SESSIONS } from './brand.js';

const LAST_HOST = 'agent-bridge.app.lastHost';

// The choice that shows every machine.
export const ALL = 'all';

// How long an offline machine's last list stays on screen (#269). Long enough to ride out a host
// restart or an update, which take a minute or two; short enough that a machine put away for the
// day doesn't leave its sessions looking current, after which the Machines page's offline list
// (#50) still has it, with when it was last seen. The Windows app keeps them as long.
export const OFFLINE_KEEP_MS = 15 * 60 * 1000;

// What a machine's channels going says: the app put away, or the relay lost.
const RESTING = 'Paused while the app is in the background.';
const DROPPED = 'The relay connection dropped; reconnecting.';

// One machine shown: its channel and what it has said.
export class HostLink {
  client = null;
  channel = null;
  // 'waiting' (not online, or no relay) | 'opening' | 'open' | 'unsupported' | 'failed'
  phase = 'waiting';
  machine = null;
  // What the machine said it supports in hello. Kept while reconnecting to it, so an answer
  // form being filled in doesn't turn read-only and back.
  capabilities = [];
  error = '';
  // The attempt to open a channel under way, the retry waiting, and how many in a row failed.
  attempt = null;
  timer = null;
  failures = 0;
  // When this device saw the machine leave presence (ms), or null while it is online; and the
  // timer that drops its last list once it has been away too long.
  offlineSince = null;
  expiry = null;
  // Away so long its list went (the one machine chosen, which stays chosen): not kept on screen
  // with every machine, should every machine be chosen again before it is back.
  expired = false;
  // Its list may be out of date: no channel open now has delivered a whole snapshot (none has
  // opened yet, its channel went, or the machine left presence). Nothing is sent to it meanwhile.
  stale = true;
  // Stale only because the app rested (#610): nothing is known to have gone wrong with it, so it
  // is confirming rather than reconnecting, and its questions still count in the app badge (a
  // rest that emptied the badge took the count away just when the app is out of sight). Until a
  // whole list is in, its channel fails, or the relay doesn't come back.
  rested = false;
  // Its hello changed (hello.changed) while its channel was still being set up: asked for again
  // once open, as the host says so only once.
  helloChanged = false;

  constructor(id, store) {
    this.id = id;
    this.store = store;
  }
}

export class HostSession {
  #listeners = new Set();
  #sessionListeners = new Set();
  #openListeners = new Set();
  #links = new Map();
  #opening = 0;
  // A notification's machine, shown with every other, until presence says whether it is online.
  #linked = null;
  // null (nothing shown: signed out), ALL, or one machine's device id.
  selection = null;

  // me: this device's id, never a machine to show. maxOpening: handshakes under way at once.
  // retryMs: the wait after a failed attempt, doubling to maxRetryMs; reopenMs: after a channel
  // that was open closes (the host restarted). keepOfflineMs: how long a machine that left
  // presence keeps its last list (OFFLINE_KEEP_MS).
  constructor({
    connection, me = null, storage = globalThis.localStorage,
    makeClient = (ch, store) => new HostClient(ch, { store }), makeStore = () => new SessionsStore(),
    maxOpening = 3, retryMs = 5000, reopenMs = 1000, maxRetryMs = 60000,
    keepOfflineMs = OFFLINE_KEEP_MS, now = () => Date.now(),
  }) {
    Object.assign(this, { connection, me, storage, makeClient, makeStore, maxOpening, retryMs, reopenMs, maxRetryMs, keepOfflineMs, now });
    connection.on('ready', () => this.#pump());
    connection.on('presence', () => this.#onPresence());
    connection.on('lost', (e) => this.#detachAll(e?.resting ? RESTING : DROPPED, e?.resting === true));
    // Back from a rest, the relay itself failing to come back is a drop after all, and is said as
    // one: a single machine's note would otherwise go on blaming the background while it retries.
    connection.on('state', ({ state } = {}) => {
      if (!['reconnecting', 'signed-out', 'replaced', 'stopped'].includes(state)) { return; }
      let changed = false;
      for (const link of this.#links.values()) {
        if (state === 'reconnecting' && link.error === RESTING) { link.error = DROPPED; changed = true; }
        if (link.rested) { link.rested = false; changed = true; }
      }
      if (changed) { this.#emit(); }
    });
    // The sessions on screen, from every machine shown, as one list.
    const self = this;
    this.sessions = {
      // [{ hostId, session }], most recently updated first, as each store orders them: the Most
      // recent sort. The list on screen is in the person's order (session-order.js).
      list: () => self.#sessionList(),
      get size() { let n = 0; for (const l of self.links()) { n += l.store.size; } return n; },
      get: (hostId, id) => self.#links.get(hostId)?.store.get(id) ?? null,
      onChange: (fn) => { self.#sessionListeners.add(fn); return () => self.#sessionListeners.delete(fn); },
    };
  }

  onChange(fn) { this.#listeners.add(fn); return () => this.#listeners.delete(fn); }

  // Whether a request to any machine shown awaits its response.
  get busy() {
    for (const link of this.#links.values()) { if (link.client?.pending > 0) { return true; } }
    return false;
  }

  // Calls fn(hostId) each time a channel to a machine shown is open, subscribed and its snapshot
  // whole: whatever waits to be resent to that machine can go.
  onOpen(fn) { this.#openListeners.add(fn); return () => this.#openListeners.delete(fn); }

  // Where something sent from a card goes: its own machine's channel, open now, or null. Never
  // another machine's, whichever is chosen; and never a machine's whose list may be out of date
  // (its channel went, or it went offline, and no whole snapshot has come since), so nothing is
  // sent on the strength of a question or session it showed before. The one gate: the services
  // take nothing new for a machine it gives no route to (SessionsPage, accepts).
  route(hostId) {
    const link = this.#links.get(hostId);
    return link?.phase === 'open' && link.client && !link.stale ? { client: link.client, hostId } : null;
  }

  // Why a machine's sessions can't be acted on now, or null when they can (or it isn't shown):
  // { reason, since }. 'offline': it left presence; since is when it was last seen (the relay's
  // roster says, else when this device saw it go). 'unsupported': in presence, but it says it
  // has no sessions to give; not tried again. 'reconnecting': in presence, and no channel to it
  // is open. 'confirming': open, and its snapshot (a new channel's first, or the one a gap asked
  // for again) has not finished, or the app rested and nothing has gone wrong since (rested: true,
  // whatever its channel's phase).
  staleOf(id) {
    const link = this.#links.get(id);
    if (!link || this.route(id)) { return null; }
    if (link.offlineSince === null) {
      if (link.rested && link.phase !== 'unsupported') { return { reason: 'confirming', since: null, rested: true }; }
      const reason = link.phase === 'open' ? 'confirming' : link.phase === 'unsupported' ? 'unsupported' : 'reconnecting';
      return { reason, since: null };
    }
    const seen = this.connection.known?.find?.((k) => k?.id === id)?.lastSeen;
    return { reason: 'offline', since: typeof seen === 'string' && seen ? seen : new Date(link.offlineSince).toISOString() };
  }

  // The machines shown that are not online: the one chosen while it is away, or with every
  // machine shown, each one kept on screen since it left.
  away() { return this.shownIds().filter((id) => !this.isOnline(id)); }

  get all() { return this.selection === ALL; }

  // The one machine chosen, or null when every machine (or none) is shown.
  get targetId() { return this.selection === ALL ? null : this.selection; }

  // The one machine chosen: what the single-machine view, and the tests driving it, read.
  get #single() { return this.targetId ? this.#links.get(this.targetId) ?? null : null; }
  get client() { return this.#single?.client ?? null; }
  get phase() { return this.#single?.phase ?? 'none'; }
  get machine() { return this.#single?.machine ?? null; }
  get capabilities() { return this.#single?.capabilities ?? []; }
  get error() { return this.#single?.error ?? ''; }
  get targetOnline() { return !!this.targetId && this.isOnline(this.targetId); }

  // The account's machines online, as presence lists them: hosts only, never this device.
  online() { return this.connection.presence.filter((d) => d.kind === 'host' && d.id !== this.me); }

  isOnline(id) { return this.online().some((d) => d.id === id); }

  // The machines shown, in order: the one chosen, or every one online, then every one kept on
  // screen since it left presence (#269).
  shownIds() {
    if (this.selection === null) { return []; }
    if (this.selection !== ALL) { return [this.selection]; }
    const online = this.online().map((d) => d.id);
    const kept = [...this.#links.values()].filter((l) => l.offlineSince !== null && !l.expired && !online.includes(l.id)).map((l) => l.id);
    return [...online, ...kept];
  }

  shows(id) { return this.shownIds().includes(id); }

  link(id) { return this.#links.get(id) ?? null; }

  links() { return this.shownIds().map((id) => this.#links.get(id)).filter(Boolean); }

  // What to call a machine: what it said in hello, else what presence calls it.
  nameOf(id) {
    return this.#links.get(id)?.machine?.name ?? this.connection.presence.find((d) => d.id === id)?.name ?? null;
  }

  // Asks a machine shown for its hello again, on the channel open to it: what it offers, and
  // whether it is read-only and why, change without a new channel when agent-ha-bridge starts
  // or stops there. A machine that offers hello.changed says when; others are asked now and then
  // (fleet.js). Listeners hear only of a change.
  async refresh(id = this.targetId) {
    const link = this.#links.get(id);
    const client = link?.client;
    if (!link || link.phase !== 'open' || !client) { return; }
    const hello = await client.hello({ changes: true });
    if (link.client !== client || link.phase !== 'open') { return; }
    const machine = { ...hello.machine, name: hello.machine.name ?? link.machine?.name ?? null };
    const key = (m, c) => JSON.stringify([m?.name, m?.hostVersion, m?.dev, m?.readOnly ?? null, m?.signIn ?? [], c]);
    const changed = key(machine, hello.capabilities) !== key(link.machine, link.capabilities);
    link.machine = machine;
    link.capabilities = hello.capabilities;
    if (changed) { this.#emit(); }
  }

  #emit() {
    for (const fn of this.#listeners) {
      try { fn(this); } catch (e) { console.error(e); }
    }
  }

  #emitSessions(change) {
    for (const fn of this.#sessionListeners) {
      try { fn(change); } catch (e) { console.error(e); }
    }
  }

  #set(link, phase, error = '') {
    link.phase = phase;
    link.error = error;
    this.#emit();
  }

  #sessionList() {
    const out = [];
    for (const l of this.links()) { for (const session of l.store.list()) { out.push({ hostId: l.id, session }); } }
    // Times are in one fixed format, so they compare as strings; ties by id, then machine, so
    // the order is stable.
    return out.sort((a, b) => {
      const x = a.session, y = b.session;
      if (x.updatedAt !== y.updatedAt) { return x.updatedAt < y.updatedAt ? 1 : -1; }
      if (x.id !== y.id) { return x.id < y.id ? -1 : 1; }
      return a.hostId < b.hostId ? -1 : a.hostId > b.hostId ? 1 : 0;
    });
  }

  // fromLink: the machine a notification's link names. It is shown with every other machine
  // when every machine is what was last chosen, or nothing was; otherwise it is chosen.
  // A machine that turns out to be offline is shown on its own, as a link to it always was,
  // without changing what is remembered.
  restoreTarget(fromLink) {
    const saved = this.#read(LAST_HOST);
    this.selection = saved === null || saved === ALL ? ALL : fromLink ?? saved;
    this.#linked = this.selection === ALL ? fromLink : null;
    this.#reconcile();
    this.#emit();
  }

  // Ends everything tied to the signed-in account: the channels, the sessions shown and the
  // remembered choice, which belongs to that account and must not carry over to the next.
  forget() {
    for (const id of [...this.#links.keys()]) { this.#drop(id); }
    this.selection = null;
    this.#linked = null;
    try { this.storage?.removeItem(LAST_HOST); } catch { /* a nicety */ }
    this.#emit();
  }

  // A machine forgotten (#50), here or from another device: its last list goes at once, rather
  // than staying on screen offline for a machine the account no longer has. One online now is
  // left alone: the relay forgets only a machine that is gone (roster.md, `online`), so it is
  // back, and its open channel is the live one.
  dropMachine(id) {
    if (!this.#links.has(id) || this.isOnline(id)) { return; }
    this.#drop(id);
    this.#emit();
  }

  // A notification's machine, shown on its own without changing what is remembered.
  visit(id) {
    if (id === this.selection) { this.#retryNow(); return; }
    this.selection = id;
    this.#linked = null;
    this.#reconcile();
    this.#emit();
  }

  // One machine's device id, or ALL, as picked in the Machines panel: remembered.
  select(choice) {
    if (choice === this.selection) {
      // Picked, so remembered, even when shown already (by a notification, say).
      this.#write(LAST_HOST, choice);
      this.#retryNow();
      return;
    }
    this.selection = choice;
    this.#linked = null;
    this.#write(LAST_HOST, choice);
    this.#reconcile();
    this.#emit();
  }

  // Shown again while it can't be reached: tried now rather than at the next retry.
  #retryNow() {
    for (const l of this.links()) {
      if (!l.client && !l.attempt && l.phase !== 'unsupported') { clearTimeout(l.timer); l.timer = null; }
    }
    this.#pump();
  }

  #onPresence() {
    const linked = this.#linked;
    this.#linked = null;
    if (linked && this.selection === ALL && !this.isOnline(linked)) { this.selection = linked; }
    // Before reconciling: a machine that just left is kept, not dropped.
    this.#markPresence();
    this.#reconcile();
    this.#emit();
  }

  // Every machine shown that left presence: its end of the channel is gone, so the channel goes,
  // but its last list stays on screen, stale, until it is back and has sent a whole new one, or
  // it has been away too long. One that is back is opened again (#pump), still stale.
  #markPresence() {
    for (const link of this.#links.values()) {
      if (!this.isOnline(link.id)) {
        // Still away: a relay drop meanwhile said so on it, which this presence answers.
        if (link.offlineSince !== null) { if (link.phase === 'waiting') { link.error = ''; } continue; }
        link.offlineSince = this.now();
        link.rested = false;
        this.#cancel(link);
        clearTimeout(link.timer);
        link.timer = null;
        this.#detach(link);
        link.phase = 'waiting';
        link.error = '';
        link.expiry = setTimeout(() => this.#expire(link), this.keepOfflineMs);
        // Node's tests must not wait a quarter of an hour on it.
        link.expiry.unref?.();
      } else if (link.offlineSince !== null) {
        link.offlineSince = null;
        link.expired = false;
        clearTimeout(link.expiry);
        link.expiry = null;
      }
    }
  }

  // Away too long: its sessions go. With every machine shown the machine goes from the list too
  // (the Machines page's offline list still has it); the one machine chosen stays chosen, empty.
  #expire(link) {
    link.expiry = null;
    if (this.#links.get(link.id) !== link || link.offlineSince === null) { return; }
    if (this.selection === link.id) { link.store.clear(); link.expired = true; } else { this.#drop(link.id); }
    this.#emit();
  }

  // Links for the machines shown, and none for any other. A machine that left presence stays,
  // offline and stale, with its last list, whether it is the one chosen or one of every machine.
  #reconcile() {
    const want = this.shownIds();
    for (const id of [...this.#links.keys()]) { if (!want.includes(id)) { this.#drop(id); } }
    for (const id of want) {
      if (this.#links.has(id)) { continue; }
      const link = new HostLink(id, this.makeStore());
      link.store.onChange((c) => { if (this.#links.get(id) === link) { this.#emitSessions({ ...c, hostId: id }); } });
      this.#links.set(id, link);
    }
    this.#pump();
  }

  #drop(id) {
    const link = this.#links.get(id);
    if (!link) { return; }
    this.#cancel(link);
    clearTimeout(link.timer);
    link.timer = null;
    clearTimeout(link.expiry);
    link.expiry = null;
    this.#detach(link);
    // Cleared while still listed, so whoever draws the sessions hears they went.
    link.store.clear();
    this.#links.delete(id);
  }

  #detach(link) {
    const c = link.client;
    link.client = null;
    link.channel = null;
    link.stale = true;
    c?.close();
  }

  #detachAll(reason, rested = false) {
    for (const link of this.#links.values()) {
      // Only a machine whose list was current when the app rested: one already away or failing
      // stays as it was.
      const current = this.route(link.id) !== null;
      this.#cancel(link);
      clearTimeout(link.timer);
      link.timer = null;
      this.#detach(link);
      link.phase = 'waiting';
      link.error = reason;
      link.rested = rested && current;
    }
    if (this.#links.size) { this.#emit(); }
  }

  // Opens what needs opening, in the order shown, a few at a time.
  #pump() {
    if (!this.connection.ready) { return; }
    for (const link of this.links()) {
      if (this.#opening >= this.maxOpening) { return; }
      if (link.client || link.attempt || link.timer || link.phase === 'unsupported' || !this.isOnline(link.id)) { continue; }
      const attempt = { abort: new AbortController(), released: false };
      link.attempt = attempt;
      this.#opening++;
      this.#set(link, 'opening');
      this.#open(link, attempt).finally(() => {
        this.#release(attempt);
        if (link.attempt === attempt) { link.attempt = null; }
        this.#pump();
      });
    }
  }

  // An attempt no longer wanted: its handshake is abandoned and its place among the handshakes
  // under way goes at once to a machine still shown, rather than when it would have timed out.
  #cancel(link) {
    const attempt = link.attempt;
    if (!attempt) { return; }
    link.attempt = null;
    attempt.abort?.abort();
    this.#release(attempt);
  }

  #release(attempt) {
    if (attempt.released) { return; }
    attempt.released = true;
    this.#opening--;
  }

  // Tries the link again after a wait that doubles with each failure in a row.
  #later(link, base, { failed = true } = {}) {
    clearTimeout(link.timer);
    const delay = failed ? Math.min(base * 2 ** link.failures, this.maxRetryMs) : base;
    if (failed) { link.failures++; }
    link.timer = setTimeout(() => { link.timer = null; this.#pump(); }, delay);
  }

  async #open(link, attempt) {
    // Superseded: the machine is no longer shown, or a later attempt took over.
    const stale = () => this.#links.get(link.id) !== link || link.attempt !== attempt;
    let client;
    try {
      // hello goes first: a host that takes it early answers it in the handshake (#603). Asked with
      // the very data the first hello below sends: the channel uses the handshake's answer only
      // for the same request, and any other costs every reopen a second round trip (#610 reopens
      // each shown machine's channel whenever the app comes back).
      const helloData = { changes: true };
      const channel = await this.connection.openChannel(link.id, { signal: attempt.abort?.signal, early: { op: Ops.hello, data: helloData } });
      if (stale()) { channel.close(); return; }
      client = this.makeClient(channel, link.store);
      link.channel = channel;
      link.client = client;
      // A new channel's first whole snapshot says what is there now: only then is anything sent
      // to it again.
      link.stale = true;
      client.on('synced', () => this.#confirm(link, client));
      // A gap in its events (host-client.js subscribes again on the same channel): the list may be
      // missing changes, so it is held as a new channel's is until the new snapshot is whole and
      // confirms it (#427). Before, answers and launches went on against the pre-gap list. A
      // resubscription that fails can't get there on this channel, so the channel goes and is
      // reopened rather than held for good.
      client.on('gap', ({ resync } = {}) => {
        if (link.client !== client) { return; }
        if (!link.stale) { link.stale = true; this.#emit(); }
        resync?.then((ok) => { if (!ok && link.client === client) { client.close(); } });
      });
      client.on('close', () => {
        if (link.client !== client) { return; }
        // Set up: open, or found not to support sessions (which may change once it restarts).
        const settled = link.phase === 'open' || (link.phase === 'unsupported' && !link.attempt);
        link.client = null;
        link.channel = null;
        link.stale = true;
        link.rested = false;
        // One closing while it is being set up fails that attempt, which retries it once.
        if (!settled) { return; }
        this.#set(link, 'waiting', 'The channel to this machine closed; reopening.');
        // A close without a relay drop (the host restarted) reopens as soon as it can. Not a
        // failed handshake, so the backoff is not advanced.
        this.#later(link, this.reopenMs, { failed: false });
      });
      // What it says changes without a new channel; a machine that can says when (hello.changed),
      // and is then asked again, rather than every minute on the chance (#610).
      link.helloChanged = false;
      client.on('event', (m) => {
        if (m?.ev !== Events.helloChanged || link.client !== client) { return; }
        if (link.phase === 'open') { this.refresh(link.id).catch(() => {}); } else { link.helloChanged = true; }
      });
      const hello = await client.hello(helloData).catch((e) => {
        // Hosts from before this protocol (the S4 demo host) answer "unknown op". Only such
        // an answer means unsupported; a timeout or a dropped channel is retried below.
        throw Object.assign(e, { unsupported: e.fromHost === true });
      });
      if (stale()) { client.close(); return; }
      link.machine = { ...hello.machine, name: hello.machine.name ?? channel.peerName };
      link.capabilities = hello.capabilities;
      if (hello.protocol !== PROTOCOL_VERSION || !hello.capabilities.includes(Capabilities.sessionsRead)) {
        this.#set(link, 'unsupported', `${link.machine.name} speaks protocol ${hello.protocol ?? '?'} without session reading.`);
        return;
      }
      // Patches where the machine offers them, and then a list held whole from this run of its host
      // is resumed: only what changed while the channel was down is sent again (#601).
      await client.subscribe({ patch: hello.capabilities.includes(Capabilities.sessionsPatch), instance: hello.machine.instance });
      if (stale()) { client.close(); return; }
      if (link.client !== client) { throw new Error('the channel closed'); }
      link.failures = 0;
      this.#set(link, 'open');
      if (link.helloChanged) {
        link.helloChanged = false;
        this.refresh(link.id).catch(() => {});
      }
      if (client.synced === true) { this.#confirm(link, client); }
    } catch (e) {
      if (stale()) { client?.close(); return; }
      if (e.unsupported) {
        this.#set(link, 'unsupported', `This machine ${UNSUPPORTED_SESSIONS} yet (${e.reason ?? e.message}).`);
        return;
      }
      if (link.client === client) { link.client = null; link.channel = null; link.stale = true; }
      link.rested = false;
      client?.close();
      this.#set(link, 'failed', e.message);
      this.#later(link, this.retryMs);
    }
  }

  // What waits to be resent to a machine can go: its channel is open, and its list current.
  #opened(id) {
    for (const fn of this.#openListeners) {
      try { fn(id); } catch (e) { console.error(e); }
    }
  }

  // A channel's snapshot is complete: its sessions and questions are the ones the machine has now
  // (those that ended meanwhile went with the snapshot), so they can be acted on again.
  #confirm(link, client) {
    if (this.#links.get(link.id) !== link || link.client !== client || !link.stale || link.phase !== 'open' || link.offlineSince !== null) { return; }
    link.stale = false;
    link.rested = false;
    this.#emit();
    this.#opened(link.id);
  }

  #read(key) { try { return this.storage?.getItem(key) ?? null; } catch { return null; } }
  #write(key, value) { try { this.storage?.setItem(key, value); } catch { /* a nicety */ } }
}
