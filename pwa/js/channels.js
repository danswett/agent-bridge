// End-to-end channels to other devices, as the initiator: Noise_XX_P256 through the relay
// (spikes/s2-noise-interop, spikes/s4-relay ChannelHub.cs), or Noise_IK_P256 to a host an earlier
// channel taught the key of and that said it takes IK (#603). Each channel carries JSON
// messages; HostClient speaks the sessions protocol over one.
//
// Channel payload, inside a relay data frame: [kind:1][channel id:8][body].
import { IKHandshake, XXHandshake, hex } from './noise-p256.js';
import { concat, fromUtf8, utf8 } from './bytes.js';
import { deviceIdOf } from './identity.js';

export const KIND = Object.freeze({ msg1: 1, msg2: 2, msg3: 3, transport: 4, close: 5, ik1: 6, ik2: 7 });
export const HEADER = 9;
const EMPTY = new Uint8Array(0);

// Per-message deflate, as src/AgentBridge.Transport/MessageDeflate.cs (#603): a message's
// plaintext is JSON, which starts with '{', or MARKER and raw DEFLATE. Each message on its own,
// compressed and then encrypted, and only to a host whose introduction said it inflates.
export const MARKER = 1;
export const MIN_DEFLATE = 512;
// What a sender may send uncompressed (a Noise message less its tag), and so the most a deflated
// one may inflate to.
const MAX_MESSAGE = 65535 - 16;
export const DEFLATES = (() => {
  try { return typeof CompressionStream === 'function' && typeof DecompressionStream === 'function' && !!new CompressionStream('deflate-raw'); } catch { return false; }
})();

const drain = async (readable, max = Infinity) => {
  const reader = readable.getReader();
  const parts = [];
  let total = 0;
  for (;;) {
    const { done, value } = await reader.read();
    if (done) { return concat(...parts); }
    total += value.length;
    if (total > max) {
      await reader.cancel().catch(() => {});
      throw new Error(`a deflated message inflates to more than ${max} bytes`);
    }
    parts.push(value);
  }
};

// The message to send: deflated, or as it is where that is not smaller.
export async function pack(plain) {
  if (plain.length < MIN_DEFLATE) { return plain; }
  const z = await drain(new Blob([plain]).stream().pipeThrough(new CompressionStream('deflate-raw')));
  return z.length + 1 < plain.length ? concat(new Uint8Array([MARKER]), z) : plain;
}

// Fed to the inflater 1 KiB at a time, as it asks: one chunk of deflate inflates to about 1 MiB at
// most, so a bomb is stopped there rather than after its whole 64 KB has inflated (to ~64 MiB).
const slices = (bytes, size = 1024) => {
  let at = 0;
  return new ReadableStream({
    pull(c) {
      if (at >= bytes.length) { c.close(); return; }
      c.enqueue(bytes.slice(at, at += size));
    },
  });
};

// The JSON a received message carries; a deflated one may inflate to no more than a sender may
// send uncompressed, so a small message cannot unpack into a large one.
export async function unpack(message, max = MAX_MESSAGE) {
  if (message.length === 0 || message[0] !== MARKER) { return message; }
  return drain(slices(message.subarray(1)).pipeThrough(new DecompressionStream('deflate-raw')), max);
}

// Binds the handshake to both routing ids and the channel id, so a relay cannot splice one
// channel's messages into another or present a channel to a different device.
export const prologue = (initiator, responder, channelId) =>
  concat(utf8.encode(`agent-bridge-s4|${initiator}|${responder}|`), channelId);

export class Channel {
  #sendChain = Promise.resolve();
  onmessage = null;
  onclose = null;
  closed = false;
  // An IK opening's first request ({op, data as sent, response}), until the first send.
  early = null;

  constructor(set, peerId, id, hs) {
    Object.assign(this, { set, peerId, id, hs, peerName: '', peerKind: '', peerInflates: false });
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
    // The first request only: if it is exactly the one IK's first message carried, the host's
    // answer came in its reply, and is given as the response without asking again. Any other
    // first request drops that answer, which would be older than what it asks.
    const early = this.early;
    this.early = null;
    if (early?.response && message?.op === early.op && JSON.stringify(message.data ?? null) === early.data) {
      queueMicrotask(() => { if (!this.closed) { this.onmessage?.({ re: message.id, ...early.response }); } });
      return Promise.resolve();
    }
    const p = this.#sendChain.then(async () => {
      const plain = utf8.encode(JSON.stringify(message));
      // Refused here, before a nonce is spent, as the host's channel does: its JSON must fit one
      // transport message deflated or not, and a host refuses one that inflates past that, which
      // would end the channel. Before deflate it was larger than a relay frame may be.
      if (plain.length > MAX_MESSAGE) { throw new Error(`a ${plain.length}-byte message does not fit one channel message (at most ${MAX_MESSAGE} bytes)`); }
      this.frame(KIND.transport, await this.hs.send.encryptWithAd(EMPTY, this.peerInflates ? await pack(plain) : plain));
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
  // Hosts that said they take IK: peer id -> { static, early }, the key their XX proved and the
  // ops they answer from IK's first message. Kept across relay connections, so a channel reopened
  // after the PWA comes back opens in one round trip with its hello answered (#603).
  known = new Map();

  // ikPatienceMs: how long an IK opening waits before trying XX instead, for a host that no longer
  // takes IK (an older version put back), which ignores it.
  constructor({ key, deviceId, name, ikPatienceMs = 5000 }) {
    Object.assign(this, { key, deviceId, name, ikPatienceMs });
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
  // handshake is given up, and the host told, rather than left to time out. early: the request
  // the caller makes first ({op, data}, hello): to a host that takes it early, IK's first message
  // carries it and its reply the answer, so it needs no round trip of its own.
  async open(peerId, { timeoutMs = 20000, signal = null, early = null } = {}) {
    if (!this.relay?.open) { throw new Error('not connected to the relay'); }
    const known = this.known.get(peerId);
    if (known) {
      const started = Date.now();
      try {
        return await this.#open(peerId, Math.min(timeoutMs, this.ikPatienceMs), signal, known, early);
      } catch (e) {
        if (signal?.aborted) { throw e; }
        // Unanswered, closed by the host, or a reply that does not decrypt: XX finds out afresh,
        // and learns the key again if it still holds.
        if (this.known.get(peerId) === known) { this.known.delete(peerId); }
        this.onLog?.(`IK to ${peerId.slice(0, 8)} failed (${e.message}); opening with XX`);
      }
      timeoutMs -= Date.now() - started;
      if (timeoutMs <= 0) { throw new Error('the handshake timed out'); }
    }
    return this.#open(peerId, timeoutMs, signal, null, null);
  }

  async #open(peerId, timeoutMs, signal, known, early) {
    const id = crypto.getRandomValues(new Uint8Array(8));
    const p = prologue(this.deviceId, peerId, id);
    const ch = new Channel(this, peerId, id, known ? await IKHandshake.create(true, this.key, p, known.static) : await XXHandshake.create(true, this.key, p));
    this.channels.set(ch.key, ch);
    const timer = setTimeout(() => {
      // The host may still answer an IK opening: told to let go of it now, since XX follows.
      if (known) { try { ch.frame(KIND.close, EMPTY); } catch { /* the relay is gone anyway */ } }
      ch.fail(new Error('the handshake timed out'));
    }, timeoutMs);
    const abandon = () => ch.close();
    if (signal?.aborted) { abandon(); } else { signal?.addEventListener('abort', abandon, { once: true }); }
    try {
      let kind = KIND.msg1, payload = EMPTY;
      if (known) {
        kind = KIND.ik1;
        const intro = { name: this.name, kind: 'pwa', ...(DEFLATES ? { deflate: true } : {}) };
        if (early && known.early.includes(early.op)) {
          const data = JSON.stringify(early.data ?? null);
          ch.early = { op: early.op, data, response: null };
          intro.early = early.data == null ? { op: early.op } : { op: early.op, data: early.data };
        }
        payload = utf8.encode(JSON.stringify(intro));
      }
      const msg1 = await ch.hs.writeMessage(payload);
      // Given up while it was being written: nothing goes to the host.
      if (ch.closed) { await ch.ready; }
      ch.frame(kind, msg1);
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

  // What a host said of IK: kept for its next channel if it takes it, forgotten if not.
  #learn(ch, intro) {
    if (Array.isArray(intro.ik)) {
      this.known.set(ch.peerId, { static: ch.hs.remoteStatic, early: intro.ik.filter((op) => typeof op === 'string') });
    } else {
      this.known.delete(ch.peerId);
    }
  }

  async #onData(from, payload) {
    if (payload.length < HEADER) { return; }
    const ch = this.channels.get(`${from}:${hex(payload.subarray(1, HEADER))}`);
    if (!ch) { return; } // this side only initiates
    const kind = payload[0];
    const body = payload.slice(HEADER);
    try {
      if (kind === KIND.msg2 && ch.hs instanceof XXHandshake && !ch.hs.isComplete) {
        const intro = JSON.parse(fromUtf8.decode(await ch.hs.readMessage(body)));
        // Self-certifying ids: the key that answered must hash to the id we addressed.
        if (await deviceIdOf(ch.hs.remoteStatic) !== ch.peerId) { throw new Error("the peer's key does not match its device id"); }
        ch.peerName = String(intro.name ?? '');
        ch.peerKind = String(intro.kind ?? '');
        ch.peerInflates = DEFLATES && intro.deflate === true;
        ch.frame(KIND.msg3, await ch.hs.writeMessage(utf8.encode(JSON.stringify({ name: this.name, kind: 'pwa', ...(DEFLATES ? { deflate: true } : {}) }))));
        this.#learn(ch, intro);
        ch.resolveReady(ch);
      } else if (kind === KIND.ik2 && ch.hs instanceof IKHandshake && !ch.hs.isComplete) {
        const intro = JSON.parse(fromUtf8.decode(await ch.hs.readMessage(body)));
        // The key is the one an earlier XX proved, kept by the id it hashed to; checked again all
        // the same, as for XX.
        if (await deviceIdOf(ch.hs.remoteStatic) !== ch.peerId) { throw new Error("the peer's key does not match its device id"); }
        ch.peerName = String(intro.name ?? '');
        ch.peerKind = String(intro.kind ?? '');
        ch.peerInflates = DEFLATES && intro.deflate === true;
        if (ch.early && intro.early && typeof intro.early === 'object') { ch.early.response = intro.early; }
        this.#learn(ch, intro);
        ch.resolveReady(ch);
      } else if (kind === KIND.transport && ch.hs.isComplete) {
        const message = JSON.parse(fromUtf8.decode(await unpack(await ch.hs.receive.decryptWithAd(EMPTY, body))));
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
