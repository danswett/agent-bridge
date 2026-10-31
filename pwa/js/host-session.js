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
import { HostClient } from './host-client.js';
import { Capabilities, PROTOCOL_VERSION } from './protocol.js';
import { SessionsStore } from './sessions-store.js';

const LAST_HOST = 'agent-bridge.app.lastHost';

// The choice that shows every machine.
export const ALL = 'all';

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
  // null (nothing shown: signed out), ALL, or one machine's device id.
  selection = null;

  // me: this device's id, never a machine to show. maxOpening: handshakes under way at once.
  // retryMs: the wait after a failed attempt, doubling to maxRetryMs; reopenMs: after a channel
  // that was open closes (the host restarted).
  constructor({
    connection, me = null, storage = globalThis.localStorage,
    makeClient = (ch, store) => new HostClient(ch, { store }), makeStore = () => new SessionsStore(),
    maxOpening = 3, retryMs = 5000, reopenMs = 1000, maxRetryMs = 60000,
  }) {
    Object.assign(this, { connection, me, storage, makeClient, makeStore, maxOpening, retryMs, reopenMs, maxRetryMs });
    connection.on('ready', () => this.#pump());
    connection.on('presence', () => this.#onPresence());
    connection.on('lost', () => this.#detachAll('The relay connection dropped; reconnecting.'));
    // The sessions on screen, from every machine shown, as one list.
    const self = this;
    this.sessions = {
      // [{ hostId, session }], most recently updated first, as the app and each store order them.
      list: () => self.#sessionList(),
      get size() { let n = 0; for (const l of self.links()) { n += l.store.size; } return n; },
      get: (hostId, id) => self.#links.get(hostId)?.store.get(id) ?? null,
      onChange: (fn) => { self.#sessionListeners.add(fn); return () => self.#sessionListeners.delete(fn); },
    };
  }

  onChange(fn) { this.#listeners.add(fn); return () => this.#listeners.delete(fn); }

  // Calls fn(hostId) each time a channel to a machine shown is open and subscribed: whatever
  // waits to be resent to that machine can go.
  onOpen(fn) { this.#openListeners.add(fn); return () => this.#openListeners.delete(fn); }

  // Where something sent from a card goes: its own machine's channel, open now, or null. Never
  // another machine's, whichever is chosen.
  route(hostId) {
    const link = this.#links.get(hostId);
    return link?.phase === 'open' && link.client ? { client: link.client, hostId } : null;
  }

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

  // The machines shown, in order: the one chosen, or every one online.
  shownIds() {
    if (this.selection === null) { return []; }
    return this.selection === ALL ? this.online().map((d) => d.id) : [this.selection];
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
  // or stops there. Listeners hear only of a change.
  async refresh(id = this.targetId) {
    const link = this.#links.get(id);
    const client = link?.client;
    if (!link || link.phase !== 'open' || !client) { return; }
    const hello = await client.hello();
    if (link.client !== client || link.phase !== 'open') { return; }
    const machine = { ...hello.machine, name: hello.machine.name ?? link.machine?.name ?? null };
    const key = (m, c) => JSON.stringify([m?.name, m?.hostVersion, m?.dev, m?.readOnly ?? null, c]);
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
  restoreTarget(fromLink) {
    const saved = this.#read(LAST_HOST);
    this.selection = saved === null || saved === ALL ? ALL : fromLink ?? saved;
    this.#reconcile();
    this.#emit();
  }

  // Ends everything tied to the signed-in account: the channels, the sessions shown and the
  // remembered choice, which belongs to that account and must not carry over to the next.
  forget() {
    for (const id of [...this.#links.keys()]) { this.#drop(id); }
    this.selection = null;
    try { this.storage?.removeItem(LAST_HOST); } catch { /* a nicety */ }
    this.#emit();
  }

  // One machine's device id, or ALL.
  select(choice) {
    if (choice === this.selection) {
      // Chosen again while it can't be reached: try now rather than at the next retry.
      for (const l of this.links()) {
        if (!l.client && !l.attempt && l.phase !== 'unsupported') { clearTimeout(l.timer); l.timer = null; }
      }
      this.#pump();
      return;
    }
    this.selection = choice;
    this.#write(LAST_HOST, choice);
    this.#reconcile();
    this.#emit();
  }

  #onPresence() {
    this.#reconcile();
    const single = this.#single;
    if (single && !this.isOnline(single.id) && (single.client || single.attempt)) {
      // The host left: its end of the channel is gone. Keep the last list on screen.
      single.attempt = null;
      this.#detach(single);
      this.#set(single, 'waiting');
    }
    this.#emit();
  }

  // Links for the machines shown, and none for any other. A machine no longer online goes
  // from every-machine view with its sessions; the one machine chosen stays, offline, with
  // its last list.
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
    link.attempt = null; // an attempt in flight is now stale and closes its own channel
    clearTimeout(link.timer);
    link.timer = null;
    this.#detach(link);
    // Cleared while still listed, so whoever draws the sessions hears they went.
    link.store.clear();
    this.#links.delete(id);
  }

  #detach(link) {
    const c = link.client;
    link.client = null;
    link.channel = null;
    c?.close();
  }

  #detachAll(reason) {
    for (const link of this.#links.values()) {
      link.attempt = null;
      clearTimeout(link.timer);
      link.timer = null;
      this.#detach(link);
      link.phase = 'waiting';
      link.error = reason;
    }
    if (this.#links.size) { this.#emit(); }
  }

  // Opens what needs opening, in the order shown, a few at a time.
  #pump() {
    if (!this.connection.ready) { return; }
    for (const link of this.links()) {
      if (this.#opening >= this.maxOpening) { return; }
      if (link.client || link.attempt || link.timer || link.phase === 'unsupported' || !this.isOnline(link.id)) { continue; }
      const attempt = {};
      link.attempt = attempt;
      this.#opening++;
      this.#set(link, 'opening');
      this.#open(link, attempt).finally(() => {
        this.#opening--;
        if (link.attempt === attempt) { link.attempt = null; }
        this.#pump();
      });
    }
  }

  // Tries the link again after a wait that doubles with each failure in a row.
  #later(link, base) {
    clearTimeout(link.timer);
    const delay = Math.min(base * 2 ** link.failures, this.maxRetryMs);
    link.failures++;
    link.timer = setTimeout(() => { link.timer = null; this.#pump(); }, delay);
  }

  async #open(link, attempt) {
    // Superseded: the machine is no longer shown, or a later attempt took over.
    const stale = () => this.#links.get(link.id) !== link || link.attempt !== attempt;
    let client;
    try {
      const channel = await this.connection.openChannel(link.id);
      if (stale()) { channel.close(); return; }
      client = this.makeClient(channel, link.store);
      link.channel = channel;
      link.client = client;
      client.on('close', () => {
        if (link.client !== client) { return; }
        link.client = null;
        link.channel = null;
        this.#set(link, 'waiting', 'The channel to this machine closed; reopening.');
        // A close without a relay drop (the host restarted) reopens as soon as it can.
        this.#later(link, this.reopenMs);
      });
      const hello = await client.hello().catch((e) => {
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
      await client.subscribe();
      if (link.client === client && !stale()) {
        link.failures = 0;
        this.#set(link, 'open');
        for (const fn of this.#openListeners) {
          try { fn(link.id); } catch (e) { console.error(e); }
        }
      }
    } catch (e) {
      if (stale()) { client?.close(); return; }
      if (e.unsupported) {
        this.#set(link, 'unsupported', `This machine doesn't support Agent Bridge sessions yet (${e.reason ?? e.message}).`);
        return;
      }
      if (link.client === client) { link.client = null; link.channel = null; }
      client?.close();
      this.#set(link, 'failed', e.message);
      this.#later(link, this.retryMs);
    }
  }

  #read(key) { try { return this.storage?.getItem(key) ?? null; } catch { return null; } }
  #write(key, value) { try { this.storage?.setItem(key, value); } catch { /* a nicety */ } }
}
