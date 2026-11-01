// End-to-end channels to other devices, as the initiator: Noise_XX_P256 through the relay
// (spikes/s2-noise-interop, spikes/s4-relay ChannelHub.cs). Each channel carries JSON
// messages; HostClient speaks the sessions protocol over one.
//
// Channel payload, inside a relay data frame: [kind:1][channel id:8][body].
import { XXHandshake, hex } from './noise-p256.js';
import { concat, fromUtf8, utf8 } from './bytes.js';
import { deviceIdOf } from './identity.js';

export const KIND = Object.freeze({ msg1: 1, msg2: 2, msg3: 3, transport: 4, close: 5 });
export const HEADER = 9;
const EMPTY = new Uint8Array(0);

// Binds the handshake to both routing ids and the channel id, so a relay cannot splice one
// channel's messages into another or present a channel to a different device.
export const prologue = (initiator, responder, channelId) =>
  concat(utf8.encode(`agent-bridge-s4|${initiator}|${responder}|`), channelId);

export class Channel {
  #sendChain = Promise.resolve();
  onmessage = null;
  onclose = null;
  closed = false;

  constructor(set, peerId, id, hs) {
    Object.assign(this, { set, peerId, id, hs, peerName: '', peerKind: '' });
    this.ready = new Promise((res, rej) => { this.resolveReady = res; this.rejectReady = rej; });
    // Nobody may be awaiting `ready` when a channel fails; that is not an unhandled error.
    this.ready.catch(() => {});
  }

  get key() { return `${this.peerId}:${hex(this.id)}`; }
  get handshakeHash() { return this.hs.handshakeHash; }

  frame(kind, body) { this.set.relay.send(this.peerId, concat(new Uint8Array([kind]), this.id, body)); }

  // Encrypt and send in one chain, so frames leave in nonce order. A failed send rejects
  // this call only; the chain itself must not stay rejected.
  send(message) {
    if (this.closed) { return Promise.reject(new Error('the channel is closed')); }
    const p = this.#sendChain.then(async () => {
      this.frame(KIND.transport, await this.hs.send.encryptWithAd(EMPTY, utf8.encode(JSON.stringify(message))));
    });
    this.#sendChain = p.catch(() => {});
    return p;
  }

  // Tells the host this channel is finished, so it can drop it now rather than when this
  // device leaves presence.
  close() {
    if (this.closed) { return; }
    try { this.frame(KIND.close, EMPTY); } catch { /* the relay is gone anyway */ }
    this.fail(new Error('closed'));
  }

  fail(error) {
    if (this.closed) { return; }
    this.closed = true;
    this.set.channels.delete(this.key);
    this.rejectReady(error);
    this.onclose?.(error);
  }
}

export class ChannelSet {
  channels = new Map();
  relay = null;
  onLog = null;

  constructor({ key, deviceId, name }) {
    Object.assign(this, { key, deviceId, name });
  }

  // A new relay connection: channels on the old one cannot continue (Noise state is per
  // channel, and the host has seen this device leave), so they all fail.
  attach(relay) {
    this.failAll(new Error('the relay connection changed'));
    this.relay = relay;
    if (relay) { relay.onData = (from, payload) => this.#onData(from, payload); }
  }

  failAll(error) {
    for (const ch of [...this.channels.values()]) { ch.fail(error); }
  }

  // signal: aborted when the channel is no longer wanted (its machine is no longer shown); the
  // handshake is given up, and the host told, rather than left to time out.
  async open(peerId, { timeoutMs = 20000, signal = null } = {}) {
    if (!this.relay?.open) { throw new Error('not connected to the relay'); }
    const id = crypto.getRandomValues(new Uint8Array(8));
    const ch = new Channel(this, peerId, id, await XXHandshake.create(true, this.key, prologue(this.deviceId, peerId, id)));
    this.channels.set(ch.key, ch);
    const timer = setTimeout(() => ch.fail(new Error('the handshake timed out')), timeoutMs);
    const abandon = () => ch.close();
    if (signal?.aborted) { abandon(); } else { signal?.addEventListener('abort', abandon, { once: true }); }
    try {
      const msg1 = await ch.hs.writeMessage(EMPTY);
      // Given up while it was being written: nothing goes to the host.
      if (ch.closed) { await ch.ready; }
      ch.frame(KIND.msg1, msg1);
      await ch.ready;
      return ch;
    } catch (e) {
      ch.fail(e);
      throw e;
    } finally {
      clearTimeout(timer);
      signal?.removeEventListener('abort', abandon);
    }
  }

  async #onData(from, payload) {
    if (payload.length < HEADER) { return; }
    const ch = this.channels.get(`${from}:${hex(payload.subarray(1, HEADER))}`);
    if (!ch) { return; } // this side only initiates
    const kind = payload[0];
    const body = payload.slice(HEADER);
    try {
      if (kind === KIND.msg2 && !ch.hs.isComplete) {
        const intro = JSON.parse(fromUtf8.decode(await ch.hs.readMessage(body)));
        // Self-certifying ids: the key that answered must hash to the id we addressed.
        if (await deviceIdOf(ch.hs.remoteStatic) !== ch.peerId) { throw new Error("the peer's key does not match its device id"); }
        ch.peerName = String(intro.name ?? '');
        ch.peerKind = String(intro.kind ?? '');
        ch.frame(KIND.msg3, await ch.hs.writeMessage(utf8.encode(JSON.stringify({ name: this.name, kind: 'pwa' }))));
        ch.resolveReady(ch);
      } else if (kind === KIND.transport && ch.hs.isComplete) {
        const message = JSON.parse(fromUtf8.decode(await ch.hs.receive.decryptWithAd(EMPTY, body)));
        // The channel may have been closed while this was being decrypted.
        if (!ch.closed) { ch.onmessage?.(message); }
      } else if (kind === KIND.close) {
        ch.fail(new Error('the host closed the channel'));
      }
    } catch (e) {
      // A frame that fails authentication ends the channel; Noise state cannot resync.
      this.onLog?.(`channel to ${from.slice(0, 8)} failed: ${e.message}`);
      ch.fail(e);
    }
  }
}
