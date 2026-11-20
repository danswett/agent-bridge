// The person's capSign key (docs/protocol/devices.md, "Signer and enrollment" and "Custody"): a
// distinct, purpose-named P-256 statement key, never the generic `sign` (signing-key.js), the
// device's DH key or a WebAuthn key. Generated non-extractable and kept in this origin's IndexedDB
// beside them, so script can sign with it but never read the private key back out.
//
// There is no local re-key: loss or rotation needs a person-approved enrollment of a new device.
// So a damaged record, or a cleared origin while the roster names a capSign for this device, is
// the one fixed failure 'no-capsign', never a fresh key.
import { fromRaw, isPoint } from './p256.js';
import { kvDelete, kvGet, kvPut } from '../identity.js';

export const CAP_SIGN_RECORD = 'roster-cap-sign';
export const CAP_SIGN_ALGORITHM = 'p256-sha256-der';

const browserStore = { get: kvGet, put: kvPut, delete: kvDelete };

// A fixed code only: never a key, path or raw exception text.
export class CapSignUnavailable extends Error {
  constructor() { super('no-capsign'); this.code = 'no-capsign'; }
}

class CapSignKey {
  // Private, so neither JSON nor a log of the object carries the CryptoKey.
  #privateKey;

  constructor(privateKey, publicPoint) {
    this.#privateKey = privateKey;
    this.algorithm = CAP_SIGN_ALGORITHM;
    this.tier = 'webcrypto';
    this.public = publicPoint;
  }

  // ECDSA over SHA-256(message), as minimal low-S DER.
  async sign(message) {
    const raw = await crypto.subtle.sign({ name: 'ECDSA', hash: 'SHA-256' }, this.#privateKey, message);
    return fromRaw(new Uint8Array(raw));
  }
}

const sameBytes = (a, b) => a.length === b.length && a.every((x, i) => x === b[i]);

const sound = (rec) => rec?.privateKey instanceof CryptoKey && rec.privateKey.extractable === false
  && rec.privateKey.type === 'private' && rec.privateKey.algorithm?.name === 'ECDSA'
  && rec.privateKey.algorithm?.namedCurve === 'P-256' && rec.public instanceof Uint8Array && isPoint(rec.public);

// Check-then-create is one step across tabs (Web Locks) and within a page (the chain), so two
// first uses never each make a key.
let chain = Promise.resolve();
const exclusive = (fn, shared) => {
  const locks = globalThis.navigator?.locks;
  if (!locks && shared) { return Promise.reject(new CapSignUnavailable()); }
  const run = () => (locks ? locks.request(CAP_SIGN_RECORD, fn) : fn());
  const next = chain.then(run, run);
  chain = next.catch(() => {});
  return next;
};

// Opens this device's capSign key, creating it on first need. `enrolled` is the capSign point the
// account roster names for this device, if any: without that exact key, refuse rather than make one.
export function openCapSignKey(store = browserStore, { enrolled } = {}) {
  return exclusive(async () => {
    let rec;
    try { rec = await store.get(CAP_SIGN_RECORD); } catch { throw new CapSignUnavailable(); }
    if (rec === undefined) {
      if (enrolled !== undefined) { throw new CapSignUnavailable(); }
      const kp = await crypto.subtle.generateKey({ name: 'ECDSA', namedCurve: 'P-256' }, false, ['sign']);
      rec = { privateKey: kp.privateKey, public: new Uint8Array(await crypto.subtle.exportKey('raw', kp.publicKey)) };
      try { await store.put(CAP_SIGN_RECORD, rec); } catch { throw new CapSignUnavailable(); }
    }
    if (!sound(rec) || (enrolled !== undefined && !sameBytes(enrolled, rec.public))) { throw new CapSignUnavailable(); }
    return new CapSignKey(rec.privateKey, new Uint8Array(rec.public));
  }, store === browserStore);
}
