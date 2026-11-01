// This device's identity: a P-256 key generated non-extractable and kept in IndexedDB, so
// script can use it for ECDH but never read its private bytes back out. Its device id is
// the first 16 bytes of SHA-256 of the public key, which makes ids self-certifying.
import { P256KeyPair, hex } from './noise-p256.js';

// Its own database: the spike PWA at /s4/ shares this origin (danswett.github.io) and keeps
// its key in 'agent-bridge-s4'. The two are different devices to the relay.
const DB = 'agent-bridge-app';
const STORE = 'kv';

function open() {
  return new Promise((resolve, reject) => {
    const r = indexedDB.open(DB, 1);
    r.onupgradeneeded = () => r.result.createObjectStore(STORE);
    r.onsuccess = () => resolve(r.result);
    r.onerror = () => reject(r.error);
  });
}

async function kv(mode, key, value) {
  const db = await open();
  try {
    return await new Promise((resolve, reject) => {
      const t = db.transaction(STORE, mode === 'delete' ? 'readwrite' : mode);
      const store = t.objectStore(STORE);
      const r = mode === 'readonly' ? store.get(key) : mode === 'delete' ? store.delete(key) : store.put(value, key);
      r.onsuccess = () => resolve(r.result);
      r.onerror = () => reject(r.error);
    });
  } finally { db.close(); }
}

// Other things this device keeps beside its key, in the same store: the account's usage readings
// (#172), which outlive the machines that read them.
export const kvGet = (key) => kv('readonly', key);
export const kvPut = (key, value) => kv('readwrite', key, value);
export const kvDelete = (key) => kv('delete', key);

export async function loadIdentity() {
  let rec = await kv('readonly', 'device');
  if (!rec) {
    const kp = await P256KeyPair.generate();
    rec = { privateKey: kp.privateKey, public: kp.public };
    await kv('readwrite', 'device', rec);
  }
  const key = new P256KeyPair(rec.privateKey, new Uint8Array(rec.public));
  return { key, deviceId: await deviceIdOf(key.public) };
}

export const deviceIdOf = async (pub) =>
  hex(new Uint8Array(await crypto.subtle.digest('SHA-256', pub)).slice(0, 16));

// What other devices see this one as, in presence and in a channel's introduction.
export function deviceName(ua = globalThis.navigator?.userAgent ?? '') {
  const platform = /iPhone/.test(ua) ? 'iPhone' : /iPad/.test(ua) ? 'iPad' : /Android/.test(ua) ? 'Android'
    : /Mac/.test(ua) ? 'Mac' : /Windows/.test(ua) ? 'Windows' : /Linux/.test(ua) ? 'Linux' : 'Browser';
  return `${platform} · Agent Bridge`;
}
