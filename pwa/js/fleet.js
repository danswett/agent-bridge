// What each machine is to the app beside its name, and every machine's agent usage
// (docs/protocol/v1-usage.md):
//   * the machine being viewed: its hello, its live session count, and a usage subscription
//     on the channel already open to it;
//   * every other online machine: a short channel (hello, usage.get, close) once a minute
//     each while the app is in front, and never more often, so the list says
//     "3 sessions · 0.1.0 (dev)" for each and usage is merged across machines as
//     agent-ha-bridge's card merged them.
import { HostClient } from './host-client.js';
import { USAGE, machineStatus } from './usage.js';

export class FleetStatus {
  #listeners = new Set();
  #machines = new Map();
  #queue = Promise.resolve();
  #queued = new Set();
  #viewed = null;
  // When the machine being viewed was last asked for its hello again.
  #viewedAt = 0;
  #timer = null;
  #started = false;

  constructor({ connection, hosts, store, book, me, probeEveryMs = 60000, now = () => Date.now(), doc = globalThis.document, makeClient = (ch) => new HostClient(ch) }) {
    Object.assign(this, { connection, hosts, store, book, me, probeEveryMs, now, doc, makeClient });
    connection.on('presence', () => this.probe());
    connection.on('ready', () => this.probe());
    hosts.onChange(() => this.#onHosts());
    store.onChange(() => this.#emit());
    doc?.addEventListener?.('visibilitychange', () => {
      if (doc.visibilityState === 'hidden') { this.#stopTimer(); return; }
      this.probe();
      this.#startTimer();
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
  }

  onChange(fn) { this.#listeners.add(fn); return () => this.#listeners.delete(fn); }

  #emit() { for (const fn of this.#listeners) { try { fn(); } catch (e) { console.error(e); } } }

  // "3 sessions · 0.1.0 (dev)" for a machine, or '' while nothing is known about it.
  status(id) {
    const m = this.#machines.get(id);
    if (!m) { return ''; }
    // The viewed machine's count is live once its snapshot is in.
    const live = id === this.hosts.targetId && this.hosts.phase === 'open' && this.hosts.client?.synced;
    return machineStatus({ ...m, sessions: live ? this.store.size : m.sessions, held: live ? this.store.held?.reason : null });
  }

  // Settles when every look asked for so far is done.
  idle() { return this.#queue; }

  // Everything tied to the signed-in account goes with it.
  clear() {
    this.#started = false;
    this.#stopTimer();
    this.#machines.clear();
    this.#queued.clear();
    this.#viewed = null;
    this.book.clear();
    this.#emit();
  }

  #onHosts() {
    const { client, targetId: id } = this.hosts;
    // What the viewed machine says now, refreshed hellos included (read-only, say).
    if (this.hosts.phase === 'open' && client && client === this.#viewed) { this.#note(id, this.hosts.machine); }
    if (this.hosts.phase === 'open' && client && client !== this.#viewed) {
      this.#viewed = client;
      this.#viewedAt = this.now();
      this.#note(id, this.hosts.machine);
      if (this.hosts.capabilities.includes(USAGE.capability)) {
        client.on('usage', (r) => { if (this.#viewed === client) { this.book.set(id, r); } });
        client.subscribeUsage().then((r) => { if (this.#viewed === client) { this.book.set(id, r); } }, () => {});
      } else { this.book.delete(id); }
    }
    this.#emit();
    this.probe();
  }

  #note(id, machine) {
    if (!id || !machine) { return; }
    this.#machines.set(id, { hostVersion: machine.hostVersion ?? null, dev: machine.dev === true, sessions: machine.sessions ?? null, readOnly: machine.readOnly ?? null, at: this.now() });
  }

  // Looks at every online machine not being viewed that has not been looked at for a minute.
  // One at a time: a phone opening the app should not start a handshake with every machine
  // at once.
  probe() {
    if (!this.connection.ready || this.doc?.visibilityState === 'hidden') { return; }
    // The machine being viewed is asked again on its own channel, as often: it may have become
    // read-only, or stopped being, with nothing else to say so.
    if (this.hosts.phase === 'open' && this.hosts.client && this.now() - this.#viewedAt >= this.probeEveryMs) {
      this.#viewedAt = this.now();
      this.hosts.refresh?.().catch(() => {});
    }
    for (const d of this.connection.presence) {
      if (d.kind !== 'host' || d.id === this.me || d.id === this.hosts.targetId || this.#queued.has(d.id)) { continue; }
      const seen = this.#machines.get(d.id);
      if (seen && this.now() - seen.at < this.probeEveryMs) { continue; }
      this.#queued.add(d.id);
      this.#queue = this.#queue.then(() => this.#probeOne(d.id)).finally(() => this.#queued.delete(d.id));
    }
  }

  async #probeOne(id) {
    if (!this.connection.ready || id === this.hosts.targetId || !this.connection.presence.some((d) => d.id === id)) { return; }
    let channel = null;
    let client = null;
    try {
      channel = await this.connection.openChannel(id, { timeoutMs: 10000 });
      client = this.makeClient(channel);
      // Each answer is dropped if the machine has been chosen for viewing meanwhile: its own
      // channel then has newer figures, and the book keeps one report per machine.
      const viewed = () => id === this.hosts.targetId;
      const hello = await client.hello();
      if (viewed()) { return; }
      this.#note(id, hello.machine);
      if (hello.capabilities.includes(USAGE.capability)) {
        const report = await client.usage();
        if (!viewed()) { this.book.set(id, report); }
      } else {
        // Said by the machine itself, so its last report goes; one that cannot be reached keeps it.
        this.book.delete(id);
      }
    } catch {
      // Not retried before the minute is up, whatever went wrong.
      const old = this.#machines.get(id);
      this.#machines.set(id, { ...(old ?? { hostVersion: null, dev: false, sessions: null }), at: this.now() });
    } finally {
      if (client) { client.close(); } else { channel?.close(); }
      this.#emit();
    }
  }
}
