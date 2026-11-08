// This device's roster signing key (design §3.2, `sign`; #55 R1d): an ECDSA P-256 key generated
// non-extractable and kept in IndexedDB beside the device's ECDH key (identity.js), so script can
// sign with it but never read its private bytes back out. A separate key from the device key:
// the roster names both, and neither is used for the other's job.
//
// WebCrypto signs in IEEE P1363 (r ‖ s); a roster signature is minimal DER with a low S, so each
// is converted (p256.js fromRaw), S lowered to n − S where it is the high one.
import { fromRaw } from './p256.js';
import { kvDelete, kvGet, kvPut } from '../identity.js';

export const SIGNING_RECORD = 'roster-sign';

const browserStore = { get: kvGet, put: kvPut, delete: kvDelete };

export class SigningKey {
  constructor(privateKey, publicPoint) {
    this.privateKey = privateKey;
    // The uncompressed point, as the roster's `sign` carries it.
    this.public = publicPoint;
  }

  // ECDSA over SHA-256(message), as minimal low-S DER.
  async sign(message) {
    const raw = await crypto.subtle.sign({ name: 'ECDSA', hash: 'SHA-256' }, this.privateKey, message);
    return fromRaw(new Uint8Array(raw));
  }
}

export async function generateSigningKey() {
  const kp = await crypto.subtle.generateKey({ name: 'ECDSA', namedCurve: 'P-256' }, false, ['sign', 'verify']);
  return { privateKey: kp.privateKey, public: new Uint8Array(await crypto.subtle.exportKey('raw', kp.publicKey)) };
}

// The key this device keeps, made the first time. `store` is IndexedDB's (identity.js) unless a
// test passes its own { get, put, delete }.
// Check-then-create must be one step: two first uses at once (two tabs, or two calls) would each
// generate a key and the later put would replace the earlier, leaving the roster naming a key
// nobody holds. Web Locks serialise across tabs; the in-page chain covers where they are absent.
let chain = Promise.resolve();
const exclusive = (fn) => {
  const run = () => (globalThis.navigator?.locks ? navigator.locks.request(SIGNING_RECORD, fn) : fn());
  const next = chain.then(run, run);
  chain = next.catch(() => {});
  return next;
};

export function loadSigningKey(store = browserStore) {
  return exclusive(async () => {
    let rec = await store.get(SIGNING_RECORD);
    if (!rec) {
      rec = await generateSigningKey();
      await store.put(SIGNING_RECORD, rec);
    }
    return new SigningKey(rec.privateKey, new Uint8Array(rec.public));
  });
}

// Forgetting the device: the key goes with it.
export const forgetSigningKey = (store = browserStore) => store.delete(SIGNING_RECORD);
