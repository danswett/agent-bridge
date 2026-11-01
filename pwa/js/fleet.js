// What each machine is to the app beside its name, and every machine's agent usage
// (docs/protocol/v1-usage.md):
//   * each machine shown (the one chosen, or every one with All): its hello, its live session
//     count, and a usage subscription on the channel already open to it;
//   * every other online machine: a short channel (hello, usage.get, close) once a minute
//     each while the app is in front, and never more often, so the list says
//     "3 sessions · 0.1.0 (dev)" for each and usage is merged across machines as
//     agent-ha-bridge's card merged them. With every machine shown there are none of these:
//     one channel per machine is all it takes.
import { HostClient } from './host-client.js';
import { USAGE, machineStatus } from './usage.js';

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
  #started = false;

  // hosts: a HostSession (host-session.js): links() for the machines shown, link(id), shows(id)
  // and refresh(id).
  constructor({ connection, hosts, book, me, probeEveryMs = 60000, now = () => Date.now(), doc = globalThis.document, makeClient = (ch) => new HostClient(ch) }) {
    Object.assign(this, { connection, hosts, book, me, probeEveryMs, now, doc, makeClient });
    connection.on('presence', () => this.probe());
    connection.on('ready', () => this.probe());
    hosts.onChange(() => this.#onHosts());
    hosts.sessions.onChange(() => this.#emit());
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
    this.book.delete(id);
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
      if (!shown.has(id)) { this.#viewed.delete(id); this.#viewedAt.delete(id); }
    }
    this.#emit();
    this.probe();
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
    }
    for (const d of this.connection.presence) {
      if (d.kind !== 'host' || d.id === this.me || this.#followed(d.id) || this.#queued.has(d.id)) { continue; }
      const seen = this.#machines.get(d.id);
      if (seen && this.now() - seen.at < this.probeEveryMs) { continue; }
      this.#queued.add(d.id);
      this.#queue = this.#queue.then(() => this.#probeOne(d.id)).finally(() => this.#queued.delete(d.id));
    }
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