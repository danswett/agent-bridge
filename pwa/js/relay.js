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

export class Relay {
  #ws = null;
  #chain = Promise.resolve();
  // Blob store requests waiting for their blob-result, by id.
  #blobRequests = new Map();
  #nextBlob = 0;
  // Forget requests waiting for their forget-result, by id.
  #forgets = new Map();
  #nextForget = 0;
  // { max } from ready when the relay has a blob store (blobs.js); null otherwise.
  blobs = null;
  onData = null;      // (fromDeviceId, payload: Uint8Array) => Promise|void
  // (devices: {id, name, kind, since}[], known: {id, name, kind, lastSeen}[] | null) => void.
  // known: the account's machines that are offline, from the relay's roster
  // (docs/protocol/roster.md); null from a relay that keeps none.
  onPresence = null;
  onClose = null;     // ({code, reason}) => void, once, after ready
  onLog = null;

  constructor({ base, key, deviceId, name, kind = 'pwa' }) {
    Object.assign(this, { base, key, deviceId, name, kind });
  }

  get open() { return this.#ws?.readyState === OPEN; }

  // Resolves once the relay has accepted the proof; rejects if the socket closes first.
  connect(token) {
    return new Promise((resolve, reject) => {
      let ready = false;
      const ws = new WebSocket(`${this.base.replace(/^http/, 'ws')}/v1/ws`);
      this.#ws = ws;
      ws.binaryType = 'arraybuffer';
      ws.onopen = () => ws.send(JSON.stringify({ t: 'hello', token, pub: hex(this.key.public), name: this.name, kind: this.kind }));
      ws.onerror = () => {};
      ws.onclose = (e) => {
        const info = { code: e.code, reason: e.reason ?? '' };
        for (const [, p] of this.#blobRequests) { p.reject(new Error('the relay connection ended')); }
        this.#blobRequests.clear();
        for (const [, p] of this.#forgets) { p.reject(new Error('the relay connection ended')); }
        this.#forgets.clear();
        if (!ready) { reject(Object.assign(new Error(`the relay closed the connection (${e.code}${e.reason ? ` ${e.reason}` : ''})`), info)); }
        else { this.onClose?.(info); }
      };
      // Strictly in order: Noise transport messages must be decrypted in the order sent.
      ws.onmessage = (ev) => {
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
        const mac = new Uint8Array(await crypto.subtle.sign('HMAC', k,
          concat(utf8.encode('agent-bridge-s4-relay-proof'), unhex(m.nonce), utf8.encode(m.account))));
        this.#ws.send(JSON.stringify({ t: 'proof', mac: hex(mac) }));
      } else if (m.t === 'ready') {
        if (m.device !== this.deviceId) { this.close(4000, 'wrong device'); throw new Error('the relay assigned a different device id'); }
        this.login = m.login;
        this.blobs = m.blobs && Number.isFinite(m.blobs.max) && m.blobs.max > 0 ? { max: m.blobs.max } : null;
        ready();
      } else if (m.t === 'presence') {
        this.onPresence?.(Array.isArray(m.devices) ? m.devices : [], Array.isArray(m.known) ? m.known : null);
      } else if (m.t === 'forget-result') {
        const p = this.#forgets.get(m.id);
        if (p) {
          this.#forgets.delete(m.id);
          p.resolve(typeof m.error === 'string' ? m.error : null);
        }
      } else if (m.t === 'blob-result') {
        const p = this.#blobRequests.get(m.id);
        if (p) {
          this.#blobRequests.delete(m.id);
          if (typeof m.error === 'string') { p.reject(Object.assign(new Error(`the relay refused the blob request: ${m.error}`), { relayReason: m.error })); } else { p.resolve(m); }
        }
      } else if (m.t === 'undeliverable') {
        this.onLog?.(`undeliverable to ${String(m.to).slice(0, 8)}`);
      }
      return;
    }
    const b = new Uint8Array(data);
    if (b.length > 1 + ID_BYTES && b[0] === DATA) {
      await this.onData?.(hex(b.subarray(1, 1 + ID_BYTES)), b.slice(1 + ID_BYTES));
    }
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

  #blobRequest(message, timeoutMs = 30000) {
    if (!this.open) { return Promise.reject(new Error('not connected to the relay')); }
    const id = ++this.#nextBlob;
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => { if (this.#blobRequests.delete(id)) { reject(new Error('the relay did not answer the blob request')); } }, timeoutMs);
      this.#blobRequests.set(id, { resolve: (r) => { clearTimeout(timer); resolve(r); }, reject: (e) => { clearTimeout(timer); reject(e); } });
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
