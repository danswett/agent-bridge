import { authenticationString } from './sas.mjs';
import { frame, utf8, sha256 } from '../../pwa/js/roster/framing.js';
import { fromBase64Url, fromHex, hex, isId } from '../../pwa/js/roster/codec.js';
import { parseStrict } from '../../pwa/js/roster/strict-json.js';
import { readDer, fromRaw } from '../../pwa/js/roster/p256.js';
export const origin = 'https://proof.slipgates.com';
const requireThat = (ok, reason) => { if (!ok) throw new Error(`Refused: ${reason}.`); };
function bodyBytes(c) {
  requireThat(c.account === 'spike' && isId(c.generation) && typeof c.genesisHash === 'string' && /^[a-f0-9]{64}$/.test(c.genesisHash), 'account, generation or hash');
  requireThat(typeof c.body === 'string' && c.body.length > 0 && c.body.length <= 10924, 'body length');
  const bytes = fromBase64Url(c.body, 'body'); requireThat(bytes.length <= 8192, 'body length'); parseStrict(bytes, 'body');
  const body = JSON.parse(new TextDecoder().decode(bytes));
  requireThat(body.account === c.account && body.generation === c.generation, 'body binding'); return bytes;
}
export function plan(bundle, fragment, now, actualOrigin) {
  requireThat(actualOrigin === origin, 'wrong origin');
  const b = structuredClone(bundle), params = new URLSearchParams(fragment.replace(/^#/, ''));
  for (const c of [b.create, b.get]) {
    requireThat(c?.account === 'spike' && isId(c.sessionId), 'account or session id');
    requireThat(Number.isSafeInteger(c.expiry) && c.expiry > now && c.expiry <= now + 300000, 'session expired or invalid expiry');
  }
  requireThat(b.create.enrollment === true && b.create.body === undefined && b.create.genesisHash === undefined && b.create.generation === undefined && b.get.enrollment !== true && b.create.sessionId !== b.get.sessionId, 'nonce-first enrollment');
  bodyBytes(b.get);
  const fingerprint = b.get.genesisHash.slice(0, 16).match(/.{4}/g).join(' ');
  requireThat([...params.keys()].join(',') === 'session,fingerprint' && params.get('session') === b.get.sessionId && params.get('fingerprint') === fingerprint, 'QR id or fingerprint');
  return { ...b, fingerprint };
}
export async function challenge(c) {
  requireThat(isId(c.sessionId), 'session id');
  if (c.enrollment === true) return sha256(frame(utf8('agent-bridge phone enrollment spike v1'), fromHex(c.sessionId)));
  const bytes = bodyBytes(c); requireThat(hex(await sha256(bytes)) === c.genesisHash, 'body hash');
  return sha256(frame(utf8('agent-bridge presence v1'), utf8('by'), bytes));
}
const encode = b => {
  requireThat(b instanceof ArrayBuffer && b.byteLength > 0 && b.byteLength <= 65536, 'credential response bytes');
  return btoa(Array.from(new Uint8Array(b), v => String.fromCharCode(v)).join('')).replaceAll('+', '-').replaceAll('/', '_').replace(/=+$/, '');
};
function signature(bytes) {
  const rs = readDer(new Uint8Array(bytes)); requireThat(rs, 'invalid assertion DER');
  const raw = new Uint8Array(64);
  for (let i = 0; i < 2; i++) {
    let v = i === 0 ? rs.r : rs.s;
    for (let j = 31; j >= 0; j--) { raw[i * 32 + j] = Number(v & 255n); v >>= 8n; }
  }
  return fromRaw(raw).buffer;
}
export class Approval {
  #plan; #started = false; #closed = false; #abort = new AbortController(); #now; #monotonic; #deadline;
  constructor(p, now = Date.now, monotonic = () => performance.now()) { this.#plan = structuredClone(p); this.#now = now; this.#monotonic = monotonic; this.#deadline = monotonic() + Math.min(p.create.expiry, p.get.expiry) - now(); }
  close() { this.#closed = true; this.#abort.abort(); }
  #remaining() { return Math.min(this.#plan.create.expiry - this.#now(), this.#plan.get.expiry - this.#now(), this.#deadline - this.#monotonic()); }
  #check() {
    requireThat(!this.#closed, 'session closed');
    const remaining = this.#remaining(); if (remaining <= 0) this.close();
    requireThat(remaining > 0, 'session expired');
  }
  async run(nav, compared, showEnrollmentCode) {
    requireThat(!this.#started && !this.#closed, 'session already started or closed');
    requireThat(compared === true, 'compare all four fingerprint groups first');
    requireThat(typeof showEnrollmentCode === 'function', 'enrollment code display required');
    this.#check(); this.#started = true;
    const p = this.#plan, signal = this.#abort.signal;
    const assertionChallenge = await challenge(p.get); this.#check();
    const creationChallenge = await challenge(p.create); this.#check();
    const created = await nav.credentials.create({ signal, publicKey: { rp: { id: 'proof.slipgates.com', name: 'Slipgates credential-possession spike' }, user: { id: new TextEncoder().encode('spike'), name: 'spike', displayName: 'Spike account (test only)' }, pubKeyCredParams: [{ type: 'public-key', alg: -7 }], challenge: creationChallenge, authenticatorSelection: { authenticatorAttachment: 'platform', userVerification: 'required', residentKey: 'required' }, attestation: 'none', timeout: this.#remaining() } });
    this.#check(); requireThat(created && created.authenticatorAttachment === 'platform', 'platform attachment required (not device provenance)');
    const registration = { id: encode(created.rawId), clientDataJSON: encode(created.response.clientDataJSON), attestationObject: encode(created.response.attestationObject), transports: created.response.getTransports() };
    requireThat(typeof created.response.getPublicKey === 'function', 'browser public-key export required');
    const enrollmentCode = await authenticationString(created.response.getPublicKey(), assertionChallenge);
    this.#check(); showEnrollmentCode(enrollmentCode);
    const got = await nav.credentials.get({ signal, publicKey: { rpId: 'proof.slipgates.com', challenge: assertionChallenge, allowCredentials: [{ type: 'public-key', id: created.rawId, transports: ['internal'] }], userVerification: 'required', timeout: this.#remaining() } });
    this.#check(); requireThat(got && encode(got.rawId) === registration.id, 'assertion credential mismatch');
    const assertion = { id: encode(got.rawId), clientDataJSON: encode(got.response.clientDataJSON), authenticatorData: encode(got.response.authenticatorData), signature: encode(signature(got.response.signature)) };
    return { registration, assertion, phone: { path: 'direct platform passkey on phone (self-reported)', OS: nav.platform, browser: nav.userAgent, authenticatorAttachment: created.authenticatorAttachment } };
  }
}
