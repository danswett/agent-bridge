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
  onData = null;      // (fromDeviceId, payload: Uint8Array) => Promise|void
  onPresence = null;  // (devices: {id, name, kind, since}[]) => void
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
        ready();
      } else if (m.t === 'presence') {
        this.onPresence?.(Array.isArray(m.devices) ? m.devices : []);
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

  close(code = 1000, reason = '') {
    try { this.#ws?.close(code, reason); } catch { /* already closing */ }
  }
}
