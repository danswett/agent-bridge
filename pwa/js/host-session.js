// Which host the app is looking at, and keeping a live subscription to it across relay
// reconnects. This is where a channel becomes a HostClient feeding the SessionsStore:
//
//   connection.openChannel(hostId) -> Channel (Noise, end to end)
//   new HostClient(channel, { store }) -> hello() -> subscribe()
//   host events (session.upsert / session.remove / sessions.synced) -> store -> views
//
// The target host is remembered, so opening the app from the Home Screen goes straight
// back to it, and a link with ?host=<device id> (a notification tap) selects that one.
import { HostClient } from './host-client.js';
import { Capabilities, PROTOCOL_VERSION } from './protocol.js';

const LAST_HOST = 'agent-bridge.app.lastHost';

export class HostSession {
  #listeners = new Set();
  #opening = null;
  client = null;
  channel = null;
  targetId = null;
  // 'none' | 'waiting' (host not online) | 'opening' | 'open' | 'unsupported' | 'failed'
  phase = 'none';
  machine = null;
  error = '';

  constructor({ connection, store, storage = globalThis.localStorage, makeClient = (ch, store) => new HostClient(ch, { store }) }) {
    Object.assign(this, { connection, store, storage, makeClient });
    connection.on('ready', () => this.#maybeOpen());
    connection.on('presence', () => this.#onPresence());
    connection.on('lost', () => this.#detach('The relay connection dropped; reconnecting.'));
  }

  onChange(fn) { this.#listeners.add(fn); return () => this.#listeners.delete(fn); }

  #emit() {
    for (const fn of this.#listeners) {
      try { fn(this); } catch (e) { console.error(e); }
    }
  }

  #set(phase, error = '') {
    this.phase = phase;
    this.error = error;
    this.#emit();
  }

  restoreTarget(fromLink) {
    this.targetId = fromLink ?? this.#read(LAST_HOST);
    if (this.targetId) { this.#set('waiting'); }
  }

  get targetOnline() {
    return !!this.targetId && this.connection.presence.some((d) => d.id === this.targetId);
  }

  // Ends everything tied to the signed-in account: the channel, the sessions shown and the
  // remembered machine, which belongs to that account and must not carry over to the next.
  forget() {
    this.#opening = null; // any attempt in flight is now stale and closes its own channel
    this.#detach();
    this.targetId = null;
    this.machine = null;
    this.store.clear();
    try { this.storage?.removeItem(LAST_HOST); } catch { /* a nicety */ }
    this.#set('none');
  }

  select(deviceId) {
    if (deviceId === this.targetId && (this.phase === 'open' || this.phase === 'opening')) { return; }
    this.#detach();
    this.store.clear();
    this.machine = null;
    this.targetId = deviceId;
    this.#write(LAST_HOST, deviceId);
    this.#set('waiting');
    this.#maybeOpen();
  }

  #onPresence() {
    if (!this.targetId) { this.#emit(); return; }
    if (!this.targetOnline && (this.client || this.#opening)) {
      // The host left: its end of the channel is gone. Keep the last list on screen.
      this.#detach();
      this.#set('waiting');
      return;
    }
    this.#maybeOpen();
    this.#emit();
  }

  #detach(reason) {
    const c = this.client;
    this.client = null;
    this.channel = null;
    c?.close();
    if (reason && this.targetId) { this.#set('waiting', reason); }
  }

  #maybeOpen() {
    // An attempt for another machine (the user switched while it was pending) does not
    // block this one; it closes its own channel when it finds it is no longer wanted.
    if (!this.targetId || this.client || this.#opening?.target === this.targetId || !this.connection.ready || !this.targetOnline) { return; }
    const attempt = { target: this.targetId };
    this.#opening = attempt;
    this.#set('opening');
    this.#open(attempt).finally(() => { if (this.#opening === attempt) { this.#opening = null; } });
  }

  async #open(attempt) {
    const target = attempt.target;
    // Superseded by a later choice, or by a later attempt for the same machine.
    const stale = () => this.targetId !== target || this.#opening !== attempt;
    let client;
    try {
      const channel = await this.connection.openChannel(target);
      if (stale()) { channel.close(); return; }
      client = this.makeClient(channel, this.store);
      this.channel = channel;
      this.client = client;
      client.on('close', () => {
        if (this.client !== client) { return; }
        this.client = null;
        this.channel = null;
        this.#set('waiting', 'The channel to this machine closed; reopening.');
        // A close without a relay drop (the host restarted) reopens as soon as it can.
        setTimeout(() => this.#maybeOpen(), 1000);
      });
      const hello = await client.hello().catch((e) => {
        // Hosts from before this protocol (the S4 demo host) answer "unknown op". Only such
        // an answer means unsupported; a timeout or a dropped channel is retried below.
        throw Object.assign(e, { unsupported: e.fromHost === true });
      });
      this.machine = { ...hello.machine, name: hello.machine.name ?? channel.peerName };
      if (hello.protocol !== PROTOCOL_VERSION || !hello.capabilities.includes(Capabilities.sessionsRead)) {
        this.#set('unsupported', `${this.machine.name} speaks protocol ${hello.protocol ?? '?'} without session reading.`);
        return;
      }
      await client.subscribe();
      if (this.client === client) { this.#set('open'); }
    } catch (e) {
      if (stale()) { client?.close(); return; }
      if (e.unsupported) {
        this.#set('unsupported', `This machine doesn't support Agent Bridge sessions yet (${e.reason ?? e.message}).`);
        return;
      }
      if (this.client === client) { this.client = null; this.channel = null; }
      client?.close();
      this.#set('failed', e.message);
      setTimeout(() => this.#maybeOpen(), 5000);
    }
  }

  #read(key) { try { return this.storage?.getItem(key) ?? null; } catch { return null; } }
  #write(key, value) { try { this.storage?.setItem(key, value); } catch { /* a nicety */ } }
}
