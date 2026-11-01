// What each machine is to the app beside its name, and every machine's agent usage
// (docs/protocol/v1-usage.md):
//   * each machine shown (the one chosen, or every one with All): its hello, its live session
//     count, and a usage subscription on the channel already open to it;
//   * every other online machine: a short channel (hello, usage.get, close) once a minute
//     each while the app is in front, and never more often, so the list says
//     "3 sessions · 0.1.0 (dev)" for each and usage is merged across machines as
//     agent-ha-bridge's card merged them. With every machine shown there are none of these:
//     one channel per machine is all it takes.
// And each machine's updates (#51, docs/protocol/v1-update.md), the same ways: followed on the
// channel of a machine shown, and read with the once-a-minute look at the others, which comes
// every few seconds instead while an update there is under way.
import { HostClient } from './host-client.js';
import { USAGE, machineStatus } from './usage.js';
import { UPDATE, underWay } from './update.js';

export class FleetStatus {
  #listeners = new Set();
  #machines = new Map();
  // Bumped when a machine is forgotten, so a look at it already on its way changes nothing.
  #epochs = new Map();
  #queue = Promise.resolve();
  #queued = new Set();
  // Each machine shown: the client its usage is followed on, and when it was last asked for its
  // hello again.
  #viewed = new Map();
  #viewedAt = new Map();
  #timer = null;
  #fastTimer = null;
  #started = false;
  // What each machine's updater is doing, for the machines that offer update.*.
  #updates = new Map();
  // Each machine shown whose updates are followed, by the client following them; and the clients
  // listening for update.state, so one that stops and starts offering updates is not heard twice.
  #updateFollowed = new Map();
  #updateHeard = new WeakSet();
  // A press accepted on a machine: looked at every fastMs until then, even before its state says
  // anything is under way (the answer is the state from before the app took the press).
  #watchUntil = new Map();

  // hosts: a HostSession (host-session.js): links() for the machines shown, link(id), shows(id)
  // and refresh(id).
  constructor({ connection, hosts, book, me, probeEveryMs = 60000, fastMs = 10000, now = () => Date.now(), doc = globalThis.document, makeClient = (ch) => new HostClient(ch) }) {
    Object.assign(this, { connection, hosts, book, me, probeEveryMs, fastMs, now, doc, makeClient });
    connection.on('presence', () => { this.probe(); this.#retime(); });
    connection.on('ready', () => this.probe());
    hosts.onChange(() => this.#onHosts());
    hosts.sessions.onChange(() => this.#emit());
    doc?.addEventListener?.('visibilitychange', () => {
      if (doc.visibilityState === 'hidden') { this.#stopTimer(); return; }
      this.probe();
      this.#startTimer();
      this.#retime();
    });
  }

  // Looks again every probeEveryMs while the app is in front: presence changes only when a
  // device comes or goes, so a machine left alone would otherwise never be looked at again.
  start() {
    this.#started = true;
    this.#startTimer();
  }

  #startTimer() {
    if (!this.#started || this.#timer || this.doc?.visibilityState === 'hidden') { return; }
    this.#timer = setInterval(() => this.probe(), this.probeEveryMs);
  }

  #stopTimer() {
    clearInterval(this.#timer);
    this.#timer = null;
    clearInterval(this.#fastTimer);
    this.#fastTimer = null;
  }

  // Looks every fastMs while an update is under way on a machine not shown (one shown is
  // followed on its own channel), so its progress and outcome come in time.
  #retime() {
    const fast = this.#started && this.doc?.visibilityState !== 'hidden'
      && [...new Set([...this.#updates.keys(), ...this.#watchUntil.keys()])].some((id) => this.#busy(id) && !this.#followed(id) && this.connection.presence.some((d) => d.id === id));
    if (fast && !this.#fastTimer) { this.#fastTimer = setInterval(() => this.probe(), this.fastMs); }
    if (!fast && this.#fastTimer) { clearInterval(this.#fastTimer); this.#fastTimer = null; }
  }

  // An update under way there, or a press just accepted that may start one.
  #busy(id) {
    const until = this.#watchUntil.get(id);
    if (until !== undefined && this.now() >= until) { this.#watchUntil.delete(id); }
    return underWay(this.#updates.get(id)) || this.#watchUntil.has(id);
  }

  // A press was accepted on a machine: looked at closely for a while (forMs), so its app's
  // progress and outcome come in time even before its state changes.
  watch(id, forMs = 120000) {
    this.#watchUntil.set(id, this.now() + forMs);
    this.#retime();
  }

  // What a machine's updater is doing, or null when it offers no updates (or none is known yet).
  update(id) { return this.#updates.get(id) ?? null; }

  // A machine's update state as a press's answer said it.
  noteUpdate(id, state) { this.#setUpdate(id, state); }

  #setUpdate(id, state) {
    if (state) { this.#updates.set(id, state); } else if (!this.#updates.delete(id)) { return; }
    this.#retime();
    this.#emit();
  }

  // An op on a machine: on the channel of a machine shown, else on a short one of its own.
  async call(id, op, data) {
    const link = this.#followed(id) ? this.hosts.link(id) : null;
    if (link?.phase === 'open' && link.client) { return link.client.request(op, data); }
    const channel = await this.connection.openChannel(id, { timeoutMs: 10000 });
    const client = this.makeClient(channel);
    try { return await client.request(op, data); } finally { client.close(); }
  }

  onChange(fn) { this.#listeners.add(fn); return () => this.#listeners.delete(fn); }

  #emit() { for (const fn of this.#listeners) { try { fn(); } catch (e) { console.error(e); } } }

  // "3 sessions · 0.1.0 (dev)" for a machine, or '' while nothing is known about it.
  status(id) {
    const m = this.#machines.get(id);
    if (!m) { return ''; }
    // A machine shown has its count live once its snapshot is in.
    const link = this.hosts.shows(id) ? this.hosts.link(id) : null;
    const live = link?.phase === 'open' && link.client?.synced;
    return machineStatus({ ...m, sessions: live ? link.store.size : m.sessions, held: live ? link.store.held?.reason : null });
  }

  // Whether a machine's figures come on a channel of its own: shown, and not found unable to
  // show sessions (an older host still has a status and may have usage, looked at as any other).
  #followed(id) { return this.hosts.shows(id) && this.hosts.link(id)?.phase !== 'unsupported'; }

  // Settles when every look asked for so far is done.
  idle() { return this.#queue; }

  // A machine forgotten (forget.js): what was learnt about it, and its usage, go, and a look
  // at it still on its way changes nothing when it ends.
  forget(id) {
    this.#epochs.set(id, (this.#epochs.get(id) ?? 0) + 1);
    this.#machines.delete(id);
    this.#updates.delete(id);
    this.#updateFollowed.delete(id);
    this.#watchUntil.delete(id);
    this.book.delete(id);
    this.#retime();
    this.#emit();
  }

  // Everything tied to the signed-in account goes with it.
  clear() {
    this.#started = false;
    this.#stopTimer();
    this.#machines.clear();
    this.#queued.clear();
    this.#viewed.clear();
    this.#viewedAt.clear();
    this.#updates.clear();
    this.#updateFollowed.clear();
    this.#watchUntil.clear();
    this.book.clear();
    this.#emit();
  }

  #onHosts() {
    const shown = new Set();
    for (const link of this.hosts.links()) {
      const { id, client } = link;
      shown.add(id);
      if (link.phase !== 'open' || !client) { continue; }
      // What the machine says now, refreshed hellos included (read-only, say).
      this.#note(id, link.machine);
      this.#followUpdates(link);
      if (this.#viewed.get(id) === client) { continue; }
      this.#viewed.set(id, client);
      this.#viewedAt.set(id, this.now());
      const mine = () => this.#viewed.get(id) === client;
      if (link.capabilities.includes(USAGE.capability)) {
        client.on('usage', (r) => { if (mine()) { this.book.set(id, r); } });
        client.subscribeUsage().then((r) => { if (mine()) { this.book.set(id, r); } }, () => {});
      } else { this.book.delete(id); }
    }
    // A machine no longer shown is looked at as the others are; its last report stays until then.
    for (const id of [...this.#viewed.keys()]) {
      if (!shown.has(id)) { this.#viewed.delete(id); this.#viewedAt.delete(id); this.#updateFollowed.delete(id); }
    }
    this.#retime();
    this.#emit();
    this.probe();
  }

  // A machine shown: its updates followed on its channel whenever its hello offers them, a
  // refreshed one included (agent-ha-bridge stopped there), and dropped when it stops offering
  // them (it became read-only). Not on the channel's first hello alone: that left a machine that
  // became writable with no updates until its channel was opened again.
  #followUpdates(link) {
    const { id, client } = link;
    if (!link.capabilities.includes(UPDATE.capability)) {
      this.#updateFollowed.delete(id);
      this.#updates.delete(id);
      return;
    }
    if (this.#updateFollowed.get(id) === client) { return; }
    this.#updateFollowed.set(id, client);
    const mine = () => this.#updateFollowed.get(id) === client;
    if (!this.#updateHeard.has(client)) {
      this.#updateHeard.add(client);
      client.on('update', (s) => { if (mine()) { this.#setUpdate(id, s); } });
    }
    // One that failed (timed out, say) is tried again at the machine's next minute's look.
    client.subscribeUpdate().then((s) => { if (mine()) { this.#setUpdate(id, s); } }, () => { if (mine()) { this.#updateFollowed.delete(id); } });
  }

  #note(id, machine) {
    if (!id || !machine) { return; }
    this.#machines.set(id, { hostVersion: machine.hostVersion ?? null, dev: machine.dev === true, sessions: machine.sessions ?? null, readOnly: machine.readOnly ?? null, at: this.now() });
  }

  // Looks at every online machine not shown that has not been looked at for a minute. One at
  // a time: a phone opening the app should not start a handshake with every machine at once.
  probe() {
    if (!this.connection.ready || this.doc?.visibilityState === 'hidden') { return; }
    // Each machine shown is asked again on its own channel, as often: it may have become
    // read-only, or stopped being, with nothing else to say so.
    for (const link of this.hosts.links()) {
      if (link.phase !== 'open' || !link.client || this.now() - (this.#viewedAt.get(link.id) ?? 0) < this.probeEveryMs) { continue; }
      this.#viewedAt.set(link.id, this.now());
      this.hosts.refresh?.(link.id)?.catch(() => {});
      this.#followUpdates(link);
    }
    for (const d of this.connection.presence) {
      if (d.kind !== 'host' || d.id === this.me || this.#followed(d.id) || this.#queued.has(d.id)) { continue; }
      const seen = this.#machines.get(d.id);
      const every = this.#busy(d.id) ? Math.min(this.fastMs, this.probeEveryMs) : this.probeEveryMs;
      if (seen && this.now() - seen.at < every) { continue; }
      this.#queued.add(d.id);
      this.#queue = this.#queue.then(() => this.#probeOne(d.id)).finally(() => this.#queued.delete(d.id));
    }
    // A watch that has run out stops the looks every few seconds.
    this.#retime();
  }

  async #probeOne(id) {
    if (!this.connection.ready || this.#followed(id) || !this.connection.presence.some((d) => d.id === id)) { return; }
    let channel = null;
    let client = null;
    const epoch = this.#epochs.get(id) ?? 0;
    const forgotten = () => (this.#epochs.get(id) ?? 0) !== epoch;
    try {
      channel = await this.connection.openChannel(id, { timeoutMs: 10000 });
      client = this.makeClient(channel);
      // Each answer is dropped if the machine has been shown meanwhile: its own channel then
      // has newer figures, and the book keeps one report per machine. And if it has been
      // forgotten meanwhile: nothing about it is kept.
      const viewed = () => this.#followed(id);
      const hello = await client.hello();
      if (viewed() || forgotten()) { return; }
      this.#note(id, hello.machine);
      if (hello.capabilities.includes(USAGE.capability)) {
        const report = await client.usage();
        if (!viewed() && !forgotten()) { this.book.set(id, report); }
      } else {
        // Said by the machine itself, so its last report goes; one that cannot be reached keeps it.
        this.book.delete(id);
      }
      if (hello.capabilities.includes(UPDATE.capability)) {
        const state = await client.updateState();
        if (!viewed() && !forgotten()) { this.#updates.set(id, state); }
      } else if (!viewed() && !forgotten()) { this.#updates.delete(id); }
      this.#retime();
    } catch {
      if (forgotten()) { return; }
      // Not retried before the minute is up, whatever went wrong.
      const old = this.#machines.get(id);
      this.#machines.set(id, { ...(old ?? { hostVersion: null, dev: false, sessions: null }), at: this.now() });
    } finally {
      if (client) { client.close(); } else { channel?.close(); }
      this.#emit();
    }
  }
}