// S2 self-test, shared by the browser page and Node. Every check returns
// { name, ok, detail } so both runners report the same thing.
import { P256KeyPair, XXHandshake, hex, unhex } from './noise-p256.js';

const eq = (a, b) => a.length === b.length && a.every((x, i) => x === b[i]);
const rejects = async (fn) => { try { await fn(); return false; } catch { return true; } };

async function keyFrom(k) { return P256KeyPair.fromParameters(unhex(k.d), unhex(k.x), unhex(k.y)); }

// Replays the .NET-generated transcript with the same fixed keys: every message this
// implementation writes must equal the .NET bytes, and every .NET message must read.
export async function vectorChecks(v) {
  const out = [];
  const prologue = unhex(v.prologue);
  const ini = await XXHandshake.create(true, await keyFrom(v.keys.initStatic), prologue, await keyFrom(v.keys.initEphemeral));
  const res = await XXHandshake.create(false, await keyFrom(v.keys.respStatic), prologue, await keyFrom(v.keys.respEphemeral));
  for (const [i, m] of v.handshake.entries()) {
    const [w, r] = m.from === 'i' ? [ini, res] : [res, ini];
    const written = await w.writeMessage(unhex(m.payload));
    out.push({ name: `handshake message ${i + 1} is byte-identical to .NET`, ok: hex(written) === m.message, detail: `${written.length} bytes` });
    const read = await r.readMessage(unhex(m.message));
    out.push({ name: `handshake message ${i + 1} from .NET decrypts to its payload`, ok: hex(read) === m.payload });
  }
  out.push({ name: 'handshake hash matches .NET (channel binding)', ok: hex(ini.handshakeHash) === v.handshakeHash && hex(res.handshakeHash) === v.handshakeHash });
  out.push({ name: 'each side learned the other\'s static key', ok: hex(ini.remoteStatic) === v.keys.respStatic.pub && hex(res.remoteStatic) === v.keys.initStatic.pub });
  for (const [i, m] of v.transport.entries()) {
    const [w, r] = m.from === 'i' ? [ini, res] : [res, ini];
    const written = await w.send.encryptWithAd(new Uint8Array(0), unhex(m.payload));
    const read = await r.receive.decryptWithAd(new Uint8Array(0), unhex(m.message));
    out.push({ name: `transport message ${i + 1} (${m.payload.length / 2} bytes, from ${m.from}) matches .NET both ways`, ok: hex(written) === m.message && hex(read) === m.payload });
  }
  return out;
}

export async function negativeChecks(v) {
  const out = [];
  const prologue = unhex(v.prologue);
  const fresh = async () => [
    await XXHandshake.create(true, await keyFrom(v.keys.initStatic), prologue, await keyFrom(v.keys.initEphemeral)),
    await XXHandshake.create(false, await keyFrom(v.keys.respStatic), prologue, await keyFrom(v.keys.respEphemeral)),
  ];

  let [ini] = await fresh();
  await ini.writeMessage(unhex(v.handshake[0].payload));
  const m2 = unhex(v.handshake[1].message); m2[100] ^= 1; // inside the encrypted static key
  out.push({ name: 'a tampered handshake message is rejected', ok: await rejects(() => ini.readMessage(m2)) });

  const other = await XXHandshake.create(false, await keyFrom(v.keys.respStatic), new TextEncoder().encode('a different prologue'), await keyFrom(v.keys.respEphemeral));
  [ini] = await fresh();
  await other.readMessage(await ini.writeMessage(new Uint8Array(0)));
  out.push({ name: 'a different prologue fails the handshake', ok: await rejects(async () => ini.readMessage(await other.writeMessage(new Uint8Array(0)))) });

  let res;
  [ini, res] = await fresh();
  for (const m of v.handshake) { const [w, r] = m.from === 'i' ? [ini, res] : [res, ini]; await r.readMessage(await w.writeMessage(unhex(m.payload))); }
  const t = unhex(v.transport[0].message); t[3] ^= 0x80;
  out.push({ name: 'a tampered transport message is rejected', ok: await rejects(() => res.receive.decryptWithAd(new Uint8Array(0), t)) });
  const replayFirst = unhex(v.transport[0].message);
  await res.receive.decryptWithAd(new Uint8Array(0), replayFirst);
  out.push({ name: 'a replayed transport message is rejected', ok: await rejects(() => res.receive.decryptWithAd(new Uint8Array(0), replayFirst)) });

  const bad = unhex(v.keys.respStatic.pub); bad[64] ^= 1; // a point off the curve
  const kp = await P256KeyPair.generate();
  out.push({ name: 'a public key that is not on the curve is refused', ok: await rejects(() => kp.dh(bad)) });
  const big = new Uint8Array(65); big[0] = 4; big.set(unhex('ffffffff00000001000000000000000000000000ffffffffffffffffffffffff'), 1);
  out.push({ name: 'a public key with a coordinate outside the field is refused', ok: await rejects(() => kp.dh(big)) });
  return out;
}

export async function randomChecks() {
  const out = [];
  const t0 = performance.now();
  const a = await XXHandshake.create(true, await P256KeyPair.generate(), new Uint8Array(0));
  const b = await XXHandshake.create(false, await P256KeyPair.generate(), new Uint8Array(0));
  await b.readMessage(await a.writeMessage(new Uint8Array(0)));
  await a.readMessage(await b.writeMessage(new Uint8Array(0)));
  await b.readMessage(await a.writeMessage(new Uint8Array(0)));
  const ms = performance.now() - t0;
  out.push({ name: 'a random-key handshake completes', ok: eq(a.handshakeHash, b.handshakeHash), detail: `${ms.toFixed(1)} ms incl. key generation` });
  const kp = await P256KeyPair.generate();
  out.push({ name: 'a generated private key cannot be exported', ok: await rejects(() => globalThis.crypto.subtle.exportKey('pkcs8', kp.privateKey)) });
  return out;
}

// Browser only: a device key must survive being stored and reloaded, still unexportable.
export async function indexedDbCheck() {
  if (typeof indexedDB === 'undefined') { return []; }
  const open = () => new Promise((ok, fail) => {
    const r = indexedDB.open('s2-keys', 1);
    r.onupgradeneeded = () => r.result.createObjectStore('k');
    r.onsuccess = () => ok(r.result); r.onerror = () => fail(r.error);
  });
  const tx = (db, mode, fn) => new Promise((ok, fail) => {
    const t = db.transaction('k', mode); const req = fn(t.objectStore('k'));
    t.oncomplete = () => ok(req.result); t.onerror = () => fail(t.error);
  });
  try {
    const kp = await P256KeyPair.generate();
    let db = await open();
    await tx(db, 'readwrite', (s) => s.put({ priv: kp.privateKey, pub: kp.public }, 'device'));
    db.close();
    db = await open();
    const back = await tx(db, 'readonly', (s) => s.get('device'));
    db.close();
    const restored = new P256KeyPair(back.priv, back.pub);
    const peer = await P256KeyPair.generate();
    const ok = eq(await restored.dh(peer.public), await peer.dh(kp.public));
    const stillLocked = await rejects(() => globalThis.crypto.subtle.exportKey('pkcs8', back.priv));
    return [{ name: 'a device key stored in IndexedDB still works after reload, and stays unexportable', ok: ok && stillLocked }];
  } catch (e) {
    return [{ name: 'a device key stored in IndexedDB still works after reload, and stays unexportable', ok: false, detail: String(e) }];
  }
}
