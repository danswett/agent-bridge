// Noise_XX_P256_AESGCM_SHA256 on WebCrypto alone: no WASM, no libraries. It runs in
// browsers (including Safari) and in Node 20+ (globalThis.crypto.subtle). It mirrors
// dotnet/Noise.cs line for line, and the comment there defines the P-256 DH function.

export const PROTOCOL_NAME = 'Noise_XX_P256_AESGCM_SHA256';
const HASHLEN = 32, PUBLEN = 65, TAGLEN = 16, MAX_MESSAGE = 65535;
const subtle = globalThis.crypto.subtle;
const enc = new TextEncoder();

const concat = (...parts) => {
  const out = new Uint8Array(parts.reduce((n, p) => n + p.length, 0));
  let at = 0;
  for (const p of parts) { out.set(p, at); at += p.length; }
  return out;
};
const sha256 = async (data) => new Uint8Array(await subtle.digest('SHA-256', data));
const hmac = async (key, data) => {
  const k = await subtle.importKey('raw', key, { name: 'HMAC', hash: 'SHA-256' }, false, ['sign']);
  return new Uint8Array(await subtle.sign('HMAC', k, data));
};
export const hex = (b) => Array.from(b, (x) => x.toString(16).padStart(2, '0')).join('');
export const unhex = (s) => new Uint8Array(s.match(/../g)?.map((h) => parseInt(h, 16)) ?? []);
const b64u = (b) => btoa(String.fromCharCode(...b)).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');

export class P256KeyPair {
  constructor(privateKey, publicRaw) { this.privateKey = privateKey; this.public = publicRaw; }

  // The private key is generated non-extractable: it can be stored in IndexedDB and
  // used for ECDH, but its bytes can never be read back out by script.
  static async generate() {
    const kp = await subtle.generateKey({ name: 'ECDH', namedCurve: 'P-256' }, false, ['deriveBits']);
    return new P256KeyPair(kp.privateKey, new Uint8Array(await subtle.exportKey('raw', kp.publicKey)));
  }

  // Test vectors only: import fixed key material.
  static async fromParameters(d, x, y) {
    const jwk = { kty: 'EC', crv: 'P-256', d: b64u(d), x: b64u(x), y: b64u(y), ext: false };
    const priv = await subtle.importKey('jwk', jwk, { name: 'ECDH', namedCurve: 'P-256' }, false, ['deriveBits']);
    return new P256KeyPair(priv, concat(new Uint8Array([4]), x, y));
  }

  async dh(peerPublic) {
    assertOnCurve(peerPublic);
    const peer = await subtle.importKey('raw', peerPublic, { name: 'ECDH', namedCurve: 'P-256' }, false, []);
    return new Uint8Array(await subtle.deriveBits({ name: 'ECDH', public: peer }, this.privateKey, 256));
  }
}

// Checked here rather than left to importKey, because not every WebCrypto does it:
// WebKit on Linux (Playwright 1.56, WebKitGTK) accepted an off-curve point that
// Chromium, Windows WebKit, Node and .NET all refuse. ECDH against an unchecked point
// is the invalid-curve attack, which leaks bits of a long-lived static key. P-256's
// cofactor is 1, so a point on the curve is in the prime-order group.
const P = 0xffffffff00000001000000000000000000000000ffffffffffffffffffffffffn;
const B = 0x5ac635d8aa3a93e7b3ebbd55769886bc651d06b0cc53b0f63bce3c3e27d2604bn;
const toBig = (bytes) => BigInt('0x' + hex(bytes));
export function assertOnCurve(pub) {
  if (pub.length !== PUBLEN || pub[0] !== 4) { throw new Error('bad public key encoding'); }
  const x = toBig(pub.subarray(1, 33)), y = toBig(pub.subarray(33, 65));
  if (x >= P || y >= P) { throw new Error('public key coordinate out of range'); }
  const rhs = (x * x * x - 3n * x + B) % P;
  // BigInt % keeps the dividend's sign, so normalise before comparing with zero.
  if ((((y * y - rhs) % P) + P) % P !== 0n) { throw new Error('public key is not on P-256'); }
}

export class CipherState {
  constructor() { this.k = null; this.n = 0n; }
  hasKey() { return this.k !== null; }
  async initializeKey(key) {
    this.k = key ? await subtle.importKey('raw', key, 'AES-GCM', false, ['encrypt', 'decrypt']) : null;
    this.n = 0n;
  }
  nonce() {
    const iv = new Uint8Array(12);
    new DataView(iv.buffer).setBigUint64(4, this.n, false);
    return iv;
  }
  async encryptWithAd(ad, plaintext) {
    if (!this.k) { return plaintext; }
    if (this.n === 0xffffffffffffffffn) { throw new Error('nonce exhausted'); }
    const c = new Uint8Array(await subtle.encrypt({ name: 'AES-GCM', iv: this.nonce(), additionalData: ad, tagLength: 128 }, this.k, plaintext));
    this.n++;
    return c;
  }
  async decryptWithAd(ad, ciphertext) {
    if (!this.k) { return ciphertext; }
    if (ciphertext.length < TAGLEN) { throw new Error('short ciphertext'); }
    // On a bad tag this rejects and n is not advanced, as the spec requires.
    const p = new Uint8Array(await subtle.decrypt({ name: 'AES-GCM', iv: this.nonce(), additionalData: ad, tagLength: 128 }, this.k, ciphertext));
    this.n++;
    return p;
  }
}

async function hkdf2(chainingKey, ikm) {
  const temp = await hmac(chainingKey, ikm);
  const o1 = await hmac(temp, new Uint8Array([1]));
  const o2 = await hmac(temp, concat(o1, new Uint8Array([2])));
  return [o1, o2];
}

export class SymmetricState {
  static async create(protocolName) {
    const s = new SymmetricState();
    const name = enc.encode(protocolName);
    if (name.length <= HASHLEN) { s.h = new Uint8Array(HASHLEN); s.h.set(name); }
    else { s.h = await sha256(name); }
    s.ck = s.h;
    s.cipher = new CipherState();
    return s;
  }
  async mixKey(ikm) {
    const [ck, tempK] = await hkdf2(this.ck, ikm);
    this.ck = ck;
    await this.cipher.initializeKey(tempK);
  }
  async mixHash(data) { this.h = await sha256(concat(this.h, data)); }
  async encryptAndHash(plaintext) {
    const c = await this.cipher.encryptWithAd(this.h, plaintext);
    await this.mixHash(c);
    return c;
  }
  async decryptAndHash(ciphertext) {
    const p = await this.cipher.decryptWithAd(this.h, ciphertext);
    await this.mixHash(ciphertext);
    return p;
  }
  async split() {
    const [k1, k2] = await hkdf2(this.ck, new Uint8Array(0));
    const c1 = new CipherState(); await c1.initializeKey(k1);
    const c2 = new CipherState(); await c2.initializeKey(k2);
    return [c1, c2];
  }
}

export class XXHandshake {
  static async create(initiator, staticKey, prologue, fixedEphemeral = null) {
    const hs = new XXHandshake();
    hs.initiator = initiator; hs.s = staticKey; hs.e = fixedEphemeral; hs.re = null;
    hs.remoteStatic = null; hs.step = 0; hs.send = null; hs.receive = null;
    hs.ss = await SymmetricState.create(PROTOCOL_NAME);
    await hs.ss.mixHash(prologue);
    return hs;
  }
  get handshakeHash() { return this.ss.h; }
  get isComplete() { return this.step === 3; }
  myTurn() { return this.initiator ? this.step % 2 === 0 : this.step % 2 === 1; }

  async writeMessage(payload) {
    if (!this.myTurn() || this.isComplete) { throw new Error("not this side's turn"); }
    const parts = [];
    const ss = this.ss;
    if (this.step === 0) {
      this.e ??= await P256KeyPair.generate();
      parts.push(this.e.public); await ss.mixHash(this.e.public);
    } else if (this.step === 1) {
      this.e ??= await P256KeyPair.generate();
      parts.push(this.e.public); await ss.mixHash(this.e.public);
      await ss.mixKey(await this.e.dh(this.re));
      parts.push(await ss.encryptAndHash(this.s.public));
      await ss.mixKey(await this.s.dh(this.re));
    } else {
      parts.push(await ss.encryptAndHash(this.s.public));
      await ss.mixKey(await this.s.dh(this.re));
    }
    parts.push(await ss.encryptAndHash(payload));
    await this.advance();
    const msg = concat(...parts);
    if (msg.length > MAX_MESSAGE) { throw new Error('message too long'); }
    return msg;
  }

  async readMessage(message) {
    if (this.myTurn() || this.isComplete) { throw new Error("not the peer's turn"); }
    let at = 0;
    const take = (n) => {
      if (message.length - at < n) { throw new Error('short handshake message'); }
      const r = message.slice(at, at + n); at += n; return r;
    };
    const ss = this.ss;
    if (this.step === 0) {
      this.re = take(PUBLEN); await ss.mixHash(this.re);
    } else if (this.step === 1) {
      this.re = take(PUBLEN); await ss.mixHash(this.re);
      await ss.mixKey(await this.e.dh(this.re));
      this.remoteStatic = await ss.decryptAndHash(take(PUBLEN + TAGLEN));
      await ss.mixKey(await this.e.dh(this.remoteStatic));
    } else {
      this.remoteStatic = await ss.decryptAndHash(take(PUBLEN + TAGLEN));
      await ss.mixKey(await this.e.dh(this.remoteStatic));
    }
    const payload = await ss.decryptAndHash(message.slice(at));
    await this.advance();
    return payload;
  }

  async advance() {
    this.step++;
    if (this.step === 3) {
      const [c1, c2] = await this.ss.split();
      [this.send, this.receive] = this.initiator ? [c1, c2] : [c2, c1];
    }
  }
}
