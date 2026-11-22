import { createHash, createPublicKey, randomBytes, timingSafeEqual, verify } from 'node:crypto';
import { frame, utf8 } from '../../pwa/js/roster/framing.js';
import { checkRosterSignature } from '../../pwa/js/roster/p256.js';
import { parseStrict } from '../../pwa/js/roster/strict-json.js';

const hash = b => createHash('sha256').update(b).digest();
const requireThat = (ok, reason) => { if (!ok) throw new Error(`Refused: ${reason}.`); };
const equal = (a, b) => a.length === b.length && timingSafeEqual(a, b);
export function enrollmentChallenge(id) {
  requireThat(typeof id === 'string' && /^[a-f0-9]{32}$/.test(id), 'enrollment nonce');
  return hash(frame(utf8('agent-bridge phone enrollment spike v1'), Buffer.from(id, 'hex')));
}
function decode(value) {
  requireThat(typeof value === 'string' && value.length <= 131072 && /^[A-Za-z0-9_-]+$/.test(value), 'invalid base64url');
  const b = Buffer.from(value, 'base64url');
  requireThat(b.toString('base64url') === value, 'noncanonical base64url');
  return b;
}
export function challenge(c) {
  requireThat(typeof c.account === 'string' && c.account.length > 0 && c.account.length <= 128, 'account');
  requireThat(typeof c.generation === 'string' && /^[a-f0-9]{32}$/.test(c.generation), 'hex generation');
  requireThat(typeof c.genesisHash === 'string' && typeof c.sessionId === 'string' && /^[a-f0-9]{64}$/.test(c.genesisHash) && /^[a-f0-9]{32}$/.test(c.sessionId), 'hash or session id');
  const bytes = decode(c.body); requireThat(bytes.length <= 8192, 'body length'); parseStrict(bytes, 'body');
  const body = JSON.parse(bytes.toString('utf8'));
  requireThat(body.account === c.account && body.generation === c.generation && hash(bytes).toString('hex') === c.genesisHash, 'body binding');
  return hash(frame(utf8('agent-bridge presence v1'), utf8('by'), bytes));
}
export function qrPayload(base, c) {
  challenge(c);
  const u = new URL(base);
  requireThat(u.protocol === 'https:' && !u.username && !u.password && !u.search && !u.hash, 'QR origin');
  u.hash = new URLSearchParams({ session: c.sessionId, fingerprint: c.genesisHash.slice(0, 16).match(/.{4}/g).join(' ') }).toString();
  return u.href;
}
function reader(bytes) {
  let offset = 0;
  const take = n => {
    requireThat(Number.isSafeInteger(n) && n >= 0 && offset + n <= bytes.length, 'truncated CBOR');
    const b = bytes.subarray(offset, offset + n); offset += n; return b;
  };
  function read(depth = 0) {
    requireThat(depth < 12, 'CBOR nesting');
    const first = take(1)[0], major = first >> 5, ai = first & 31;
    requireThat(ai < 27, 'unsupported CBOR encoding');
    const n = ai < 24 ? ai : take(2 ** (ai - 24)).readUIntBE(0, 2 ** (ai - 24));
    if (major === 0 || major === 1) return major === 0 ? n : -1 - n;
    if (major === 2) return take(n);
    if (major === 3) return new TextDecoder('utf-8', { fatal: true }).decode(take(n));
    requireThat(major === 5 && n <= 64, 'unsupported CBOR type');
    const m = new Map();
    for (let i = 0; i < n; i++) {
      const k = read(depth + 1);
      requireThat((typeof k === 'string' || Number.isInteger(k)) && !m.has(k), 'duplicate or invalid CBOR key');
      m.set(k, read(depth + 1));
    }
    return m;
  }
  return { read, end: () => requireThat(offset === bytes.length, 'trailing CBOR') };
}
function parseCbor(bytes) { const r = reader(bytes), value = r.read(); r.end(); return value; }
function authData(b, rpId, registration) {
  requireThat(b.length >= 37 && equal(b.subarray(0, 32), hash(rpId)), 'rpIdHash');
  const flags = b[32], BE = !!(flags & 8), BS = !!(flags & 16);
  requireThat((flags & 5) === 5, 'UP and UV required');
  requireThat(!BS || BE, 'BS without BE');
  requireThat(!(flags & 0xa2) && !!(flags & 64) === registration, 'unsupported authenticator flags');
  const facts = { BE, BS, UP: true, UV: true, signCount: b.readUInt32BE(33) };
  if (!registration) { requireThat(b.length === 37, 'trailing authData'); return { facts }; }
  requireThat(b.length >= 55, 'truncated attested credential');
  const length = b.readUInt16BE(53);
  requireThat(length > 0 && length <= 1023 && b.length > 55 + length, 'credential length');
  const id = b.subarray(55, 55 + length), key = parseCbor(b.subarray(55 + length));
  requireThat(key instanceof Map && key.get(1) === 2 && key.get(3) === -7 && key.get(-1) === 1, 'ES256 key required');
  const x = key.get(-2), y = key.get(-3);
  requireThat(Buffer.isBuffer(x) && x.length === 32 && Buffer.isBuffer(y) && y.length === 32, 'EC coordinates');
  const publicKey = createPublicKey({ format: 'jwk', key: { kty: 'EC', crv: 'P-256', x: x.toString('base64url'), y: y.toString('base64url') } });
  const a = b.subarray(37, 53).toString('hex');
  return { id, publicKey, facts: { ...facts, aaguid: `${a.slice(0, 8)}-${a.slice(8, 12)}-${a.slice(12, 16)}-${a.slice(16, 20)}-${a.slice(20)}`, credentialIdLength: length } };
}
export class Sessions {
  #sessions = new Map();
  #credentials = new Map();
  constructor({ origin, rpId, now = Date.now, monotonic = () => performance.now() }) {
    const u = new URL(origin);
    requireThat(u.protocol === 'https:' && u.origin === origin && u.hostname === rpId, 'origin and exact rpId');
    this.origin = origin; this.rpId = rpId; this.now = now; this.monotonic = monotonic;
  }
  create(generation, body) {
    const bytes = Buffer.from(body), c = { account: 'spike', generation, body: bytes.toString('base64url'), genesisHash: hash(bytes).toString('hex'), sessionId: randomBytes(16).toString('hex'), expiry: this.now() + 300000 };
    this.open(c); return c;
  }
  createEnrollment() { const c = { account: 'spike', enrollment: true, sessionId: randomBytes(16).toString('hex'), expiry: this.now() + 300000 }; this.open(c); return c; }
  open(c) {
    if (c.enrollment === true) enrollmentChallenge(c.sessionId); else challenge(c);
    requireThat(c.account === 'spike' && Number.isSafeInteger(c.expiry) && c.expiry > this.now() && c.expiry <= this.now() + 300000, 'account or five-minute expiry');
    requireThat(!this.#sessions.has(c.sessionId), 'session already exists');
    requireThat(c.enrollment === true || ![...this.#sessions.values()].some(s => !s.enrollment && s.genesisHash === c.genesisHash), 'body already has a session');
    this.#sessions.set(c.sessionId, { ...c, ended: false, expired: false, monotonicDeadline: this.monotonic() + c.expiry - this.now() });
  }
  close(id) { const s = this.#sessions.get(id); requireThat(s && !s.ended, 'session ended'); s.ended = true; }
  credentialSpki(id) {
    const c = this.#credentials.get(id); requireThat(c, 'unregistered credential'); return c.publicKey.export({ type: 'spki', format: 'der' });
  }
  snapshot() {
    return { sessions: [...this.#sessions].map(([id, s]) => [id, { ...s }]), credentials: [...this.#credentials].map(([id, c]) => [id, structuredClone(c.facts)]) };
  }
  #client(id, account, response, type) {
    const s = this.#sessions.get(id);
    requireThat(s && !s.ended, 'session ended or unknown');
    if (this.now() >= s.expiry || this.monotonic() >= s.monotonicDeadline) s.expired = true;
    requireThat(!s.expired, 'session expired');
    requireThat(account === s.account, 'different account');
    requireThat((type === 'webauthn.create') === (s.enrollment === true), 'wrong session kind');
    const bytes = decode(response.clientDataJSON), c = JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(bytes));
    parseStrict(bytes, 'clientDataJSON');
    requireThat(c.type === type && c.origin === this.origin && (c.crossOrigin === undefined || c.crossOrigin === false) && c.topOrigin === undefined, 'client origin or type');
    requireThat(equal(decode(c.challenge), s.enrollment ? enrollmentChallenge(s.sessionId) : challenge(s)), 'wrong challenge');
    return { s, bytes };
  }
  register(id, account, response) {
    const { s, bytes } = this.#client(id, account, response, 'webauthn.create');
    const obj = parseCbor(decode(response.attestationObject));
    requireThat(obj instanceof Map && Buffer.isBuffer(obj.get('authData')) && obj.get('attStmt') instanceof Map, 'attestation object');
    const data = obj.get('authData'), credential = authData(data, this.rpId, true), fmt = obj.get('fmt'), stmt = obj.get('attStmt');
    requireThat(fmt === 'none' || fmt === 'packed', 'unsupported attestation format');
    if (fmt === 'none') requireThat(stmt.size === 0, 'invalid none attestation');
    else {
      requireThat(stmt.size === 2 && stmt.get('alg') === -7 && Buffer.isBuffer(stmt.get('sig')), 'only ES256 self attestation supported');
      requireThat(verify('sha256', Buffer.concat([data, hash(bytes)]), credential.publicKey, stmt.get('sig')), 'attestation signature');
    }
    requireThat(equal(decode(response.id), credential.id) && !this.#credentials.has(response.id), 'credential id');
    requireThat(Array.isArray(response.transports) && response.transports.every(t => ['usb', 'nfc', 'ble', 'internal', 'hybrid', 'smart-card'].includes(t)), 'reported transports');
    credential.facts = { ...credential.facts, fmt, transports: [...response.transports] };
    this.#credentials.set(response.id, credential); s.ended = true;
    return structuredClone(credential.facts);
  }
  verify(id, account, response) {
    const { s, bytes } = this.#client(id, account, response, 'webauthn.get'), credential = this.#credentials.get(response.id);
    requireThat(credential, 'unregistered credential');
    const data = decode(response.authenticatorData), { facts } = authData(data, this.rpId, false);
    requireThat(facts.BE === credential.facts.BE, 'backup eligibility changed');
    const signature = decode(response.signature); checkRosterSignature(signature, 'signature');
    requireThat(verify('sha256', Buffer.concat([data, hash(bytes)]), credential.publicKey, signature), 'bad signature');
    credential.facts = { ...credential.facts, ...facts }; s.ended = true;
    return structuredClone(credential.facts);
  }
}
