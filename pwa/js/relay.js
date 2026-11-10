// One WebSocket to the relay: sign in with the account token, prove possession of this
// device's key, then receive presence and route opaque frames to and from other devices
// of the same account. The relay sees routing ids and ciphertext only. Wire format:
// spikes/s4-relay/src/S4.Protocol/Wire.cs.
//
// Runs in browsers and in Node 22+ (the E2E fixture host uses it as a host).
import { hex, unhex } from './noise-p256.js';
import { concat, utf8 } from './bytes.js';

export const DATA = 1;
export const ID_BYTES = 16;
export const CLOSE_CODES = Object.freeze({ authFailed: 4001, proofFailed: 4003, replaced: 4009 });

const OPEN = 1;
// A probe's payload: no channel kind (channels.js KIND starts at 1).
const PROBE = 0;

export class Relay {
  #ws = null;
  #chain = Promise.resolve();
  // Blob and feedback requests waiting for their result, by id.
  #storageRequests = new Map();
  #nextStorage = 0;
  // Forget requests waiting for their forget-result, by id.
  #forgets = new Map();
  #nextForget = 0;
  // Liveness probes waiting for any frame from the relay (probe()).
  #probes = new Set();
  // When the latest frame of any kind came from the relay (Date.now()), 0 before the first.
  lastReceivedAt = 0;
  // { max } from ready when the relay has a blob store (blobs.js); null otherwise.
  blobs = null;
  // Largest stored feedback envelope the relay accepts, or 0 when feedback is disabled.
  feedbackMaxBytes = 0;
  onData = null;      // (fromDeviceId, payload: Uint8Array) => Promise|void
  // (devices: {id, name, kind, since}[], known: {id, name, kind, lastSeen}[] | null) => void.
  // known: the account's machines that are offline, from the relay's roster
  // (docs/protocol/roster.md); null from a relay that keeps none.
  onPresence = null;
  onClose = null;     // ({code, reason}) => void, once, after ready
  onReceive = null;   // () => void, as each frame arrives, before it is handled
  onLog = null;

  constructor({ base, key, deviceId, name, kind = 'pwa' }) {
    Object.assign(this, { base, key, deviceId, name, kind });
  }

  get open() { return this.#ws?.readyState === OPEN; }

  // Whether a blob or forget request awaits the relay's answer.
  get busy() { return this.#storageRequests.size > 0 || this.#forgets.size > 0; }

  // Resolves once the relay has accepted the proof; rejects if the socket closes first.
  connect(token) {
    return new Promise((resolve, reject) => {
      let ready = false;
      // admit=1: the relay sends its challenge as the socket opens (#603), so the proof follows the
      // hello at once and ready is one round trip away, not two. A relay from before it ignores it.
      const ws = new WebSocket(`${this.base.replace(/^http/, 'ws')}/v1/ws?admit=1`);
      this.#ws = ws;
      ws.binaryType = 'arraybuffer';
      ws.onopen = () => ws.send(JSON.stringify({ t: 'hello', token, pub: hex(this.key.public), name: this.name, kind: this.kind }));
      ws.onerror = () => {};
      ws.onclose = (e) => {
        const info = { code: e.code, reason: e.reason ?? '' };
        for (const [, p] of this.#storageRequests) { p.reject(new Error('the relay connection ended')); }
        this.#storageRequests.clear();
        for (const [, p] of this.#forgets) { p.reject(new Error('the relay connection ended')); }
        this.#forgets.clear();
        for (const settle of [...this.#probes]) { settle(false); }
        if (!ready) { reject(Object.assign(new Error(`the relay closed the connection (${e.code}${e.reason ? ` ${e.reason}` : ''})`), info)); }
        else { this.onClose?.(info); }
      };
      // Strictly in order: Noise transport messages must be decrypted in the order sent.
      ws.onmessage = (ev) => {
        // Noted as it arrives, not once the chain reaches it: a frame queued behind a slow
        // decrypt still shows the socket is alive.
        this.lastReceivedAt = Date.now();
        for (const settle of [...this.#probes]) { settle(true); }
        try { this.onReceive?.(); } catch (e) { console.error(e); }
        this.#chain = this.#chain
          .then(() => this.#handle(ev.data, () => { ready = true; resolve(this); }))
          .catch((e) => this.onLog?.(`relay message: ${e.message}`));
      };
    });
  }

  async #handle(data, ready) {
    if (typeof data === 'string') {
      const m = JSON.parse(data);
      if (m.t === 'challenge') {
        const secret = await this.key.dh(unhex(m.eph));
        const k = await crypto.subtle.importKey('raw', secret, { name: 'HMAC', hash: 'SHA-256' }, false, ['sign']);
        // An early challenge, sent before the hello, names no account and has a proof of its own
        // (Wire.EarlyProofMac).
        const signed = m.early === true
          ? concat(utf8.encode('agent-bridge-relay-early-proof'), unhex(m.nonce))
          : concat(utf8.encode('agent-bridge-s4-relay-proof'), unhex(m.nonce), utf8.encode(m.account));
        const mac = new Uint8Array(await crypto.subtle.sign('HMAC', k, signed));
        this.#ws.send(JSON.stringify({ t: 'proof', mac: hex(mac) }));
      } else if (m.t === 'ready') {
        if (m.device !== this.deviceId) { this.close(4000, 'wrong device'); throw new Error('the relay assigned a different device id'); }
        this.login = m.login;
        this.blobs = m.blobs && Number.isFinite(m.blobs.max) && m.blobs.max > 0 ? { max: m.blobs.max } : null;
        this.feedbackMaxBytes = m.feedback && Number.isSafeInteger(m.feedback.max) && m.feedback.max > 0 ? m.feedback.max : 0;
        ready();
      } else if (m.t === 'presence') {
        this.onPresence?.(Array.isArray(m.devices) ? m.devices : [], Array.isArray(m.known) ? m.known : null);
      } else if (m.t === 'forget-result') {
        const p = this.#forgets.get(m.id);
        if (p) {
          this.#forgets.delete(m.id);
          p.resolve(typeof m.error === 'string' ? m.error : null);
        }
      } else if (m.t === 'blob-result' || m.t === 'feedback-result') {
        const p = this.#storageRequests.get(m.id);
        if (p?.result === m.t) {
          this.#storageRequests.delete(m.id);
          const kind = m.t === 'feedback-result' ? 'feedback' : 'blob';
          if (typeof m.error === 'string') { p.reject(Object.assign(new Error(`the relay refused the ${kind} request: ${m.error}`), { relayReason: m.error })); } else { p.resolve(m); }
        }
      } else if (m.t === 'undeliverable') {
        this.onLog?.(`undeliverable to ${String(m.to).slice(0, 8)}`);
      }
      return;
    }
    const b = new Uint8Array(data);
    if (b.length > 1 + ID_BYTES && b[0] === DATA) {
      const from = hex(b.subarray(1, 1 + ID_BYTES));
      // A probe's echo (probe()) has done its work by arriving.
      if (from === this.deviceId && b.length === 2 + ID_BYTES && b[1 + ID_BYTES] === PROBE) { return; }
      await this.onData?.(from, b.slice(1 + ID_BYTES));
    }
  }

  // Whether the relay still answers: resolves true at the next frame of any kind, false if none
  // comes within timeoutMs or the socket closes. A phone that changed networks, or whose radio
  // slept, can hold a socket that still reads open while nothing reaches either end (#1128); the
  // relay only notices at its pong timeout, 30 s after its next ping. To be sure of an answer it
  // sends a one-byte data frame addressed to this device itself, which the relay routes back
  // like any other (RelayHub.ForwardAsync): no new message on the wire, and channels ignore it.
  probe(timeoutMs = 3000) {
    if (!this.open) { return Promise.resolve(false); }
    return new Promise((resolve) => {
      const settle = (alive) => {
        if (!this.#probes.delete(settle)) { return; }
        clearTimeout(timer);
        resolve(alive);
      };
      const timer = setTimeout(() => settle(false), timeoutMs);
      this.#probes.add(settle);
      try { this.send(this.deviceId, new Uint8Array([PROBE])); } catch { settle(false); }
    });
  }

  // WebSocket.send on a closed socket drops the data without an error. On a phone that
  // had slept, every request then just timed out (S5), so a dead connection fails loudly.
  send(peerId, payload) {
    if (!this.open) { throw new Error('not connected to the relay'); }
    this.#ws.send(concat(new Uint8Array([DATA]), unhex(peerId), payload));
  }

  // The blob store (blobs.js). Each resolves to the relay's result, or rejects with relayReason
  // set to its refusal (too-large, quota, rate-limited, not-found, ...).
  blobPut(to, size) { return this.#blobRequest({ t: 'blob-put', to, size }); }

  blobGet(blob) { return this.#blobRequest({ t: 'blob-get', blob }); }

  blobDelete(blob) { return this.#blobRequest({ t: 'blob-delete', blob }).then((r) => r.deleted === true); }

  // One create-only URL for an already encrypted feedback envelope.
  feedbackPut(size) { return this.#blobRequest({ t: 'feedback-put', size }, 'feedback-result'); }

  #blobRequest(message, result = 'blob-result', timeoutMs = 30000) {
    if (!this.open) { return Promise.reject(new Error('not connected to the relay')); }
    const id = ++this.#nextStorage;
    const kind = result === 'feedback-result' ? 'feedback' : 'blob';
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => { if (this.#storageRequests.delete(id)) { reject(new Error(`the relay did not answer the ${kind} request`)); } }, timeoutMs);
      this.#storageRequests.set(id, { result, resolve: (r) => { clearTimeout(timer); resolve(r); }, reject: (e) => { clearTimeout(timer); reject(e); } });
      this.#ws.send(JSON.stringify({ ...message, id }));
    });
  }

  // Asks the relay to forget one of the account's machines that is offline. Resolves to null
  // once forgotten, or the relay's reason (not-allowed, online, not-found, rate-limited,
  // disabled). The relay takes it only from a person's device: this one is.
  forget(machine, timeoutMs = 30000) {
    if (!this.open) { return Promise.reject(new Error('not connected to the relay')); }
    const id = ++this.#nextForget;
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => { if (this.#forgets.delete(id)) { reject(new Error('the relay did not answer')); } }, timeoutMs);
      this.#forgets.set(id, { resolve: (r) => { clearTimeout(timer); resolve(r); }, reject: (e) => { clearTimeout(timer); reject(e); } });
      this.#ws.send(JSON.stringify({ t: 'forget', id, machine }));
    });
  }

  close(code = 1000, reason = '') {
    try { this.#ws?.close(code, reason); } catch { /* already closing */ }
  }
}
