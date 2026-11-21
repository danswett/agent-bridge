import { authenticationString } from './sas.mjs';
export const origin = 'https://proof.slipgates.com';
const requireThat = (ok, reason) => { if (!ok) throw new Error(`Refused: ${reason}.`); };
export function plan(bundle, fragment, now, actualOrigin) {
  requireThat(actualOrigin === origin, 'wrong origin');
  const b = structuredClone(bundle), params = new URLSearchParams(fragment.replace(/^#/, ''));
  for (const c of [b.create, b.get]) {
    requireThat(c?.account === 'spike' && Number.isSafeInteger(c.generation) && c.generation > 0, 'account or generation');
    requireThat(typeof c.genesisHash === 'string' && typeof c.sessionId === 'string' && /^[a-f0-9]{64}$/.test(c.genesisHash) && /^[a-f0-9]{32}$/.test(c.sessionId), 'genesis hash or session id');
    requireThat(Number.isSafeInteger(c.expiry) && c.expiry > now && c.expiry <= now + 300000, 'session expired or invalid expiry');
  }
  requireThat(b.create.sessionId !== b.get.sessionId && b.create.genesisHash === b.get.genesisHash && b.create.generation === b.get.generation && b.create.expiry === b.get.expiry, 'session binding');
  const fingerprint = b.get.genesisHash.slice(0, 16).match(/.{4}/g).join(' ');
  requireThat([...params.keys()].join(',') === 'session,fingerprint' && params.get('session') === b.get.sessionId && params.get('fingerprint') === fingerprint, 'QR id or fingerprint');
  return { ...b, fingerprint };
}
export async function challenge(c) {
  const bytes = new TextEncoder().encode(JSON.stringify(['Slipgates/WebAuthn/v1', 'roster-genesis-presence', c.account, c.generation, c.genesisHash, c.sessionId, c.expiry]));
  return new Uint8Array(await crypto.subtle.digest('SHA-256', bytes));
}
const encode = b => {
  requireThat(b instanceof ArrayBuffer && b.byteLength > 0 && b.byteLength <= 65536, 'credential response bytes');
  return btoa(Array.from(new Uint8Array(b), v => String.fromCharCode(v)).join('')).replaceAll('+', '-').replaceAll('/', '_').replace(/=+$/, '');
};
export class Approval {
  #plan; #started = false; #closed = false; #abort = new AbortController(); #now;
  constructor(p, now = Date.now) { this.#plan = structuredClone(p); this.#now = now; }
  close() { this.#closed = true; this.#abort.abort(); }
  #check() {
    requireThat(!this.#closed, 'session closed');
    requireThat(this.#now() < this.#plan.get.expiry, 'session expired');
  }
  async run(nav, compared, showEnrollmentCode) {
    requireThat(!this.#started && !this.#closed, 'session already started or closed');
    requireThat(compared === true, 'compare all four fingerprint groups first');
    requireThat(typeof showEnrollmentCode === 'function', 'enrollment code display required');
    this.#check(); this.#started = true;
    const p = this.#plan, signal = this.#abort.signal;
    const creationChallenge = await challenge(p.create); this.#check();
    const created = await nav.credentials.create({ signal, publicKey: { rp: { id: 'proof.slipgates.com', name: 'Slipgates presence spike' }, user: { id: new TextEncoder().encode('spike'), name: 'spike', displayName: 'Spike account (test only)' }, pubKeyCredParams: [{ type: 'public-key', alg: -7 }], challenge: creationChallenge, authenticatorSelection: { authenticatorAttachment: 'platform', userVerification: 'required', residentKey: 'required' }, attestation: 'none', timeout: p.create.expiry - this.#now() } });
    this.#check(); requireThat(created && created.authenticatorAttachment === 'platform', 'platform phone credential required');
    const registration = { id: encode(created.rawId), clientDataJSON: encode(created.response.clientDataJSON), attestationObject: encode(created.response.attestationObject), transports: created.response.getTransports() };
    const assertionChallenge = await challenge(p.get); this.#check();
    requireThat(typeof created.response.getPublicKey === 'function', 'browser public-key export required');
    const enrollmentCode = await authenticationString(created.response.getPublicKey(), assertionChallenge);
    this.#check(); showEnrollmentCode(enrollmentCode);
    const got = await nav.credentials.get({ signal, publicKey: { rpId: 'proof.slipgates.com', challenge: assertionChallenge, allowCredentials: [{ type: 'public-key', id: created.rawId, transports: ['internal'] }], userVerification: 'required', timeout: p.get.expiry - this.#now() } });
    this.#check(); requireThat(got && encode(got.rawId) === registration.id, 'assertion credential mismatch');
    const assertion = { id: encode(got.rawId), clientDataJSON: encode(got.response.clientDataJSON), authenticatorData: encode(got.response.authenticatorData), signature: encode(got.response.signature) };
    return { registration, assertion, phone: { path: 'direct platform passkey on phone (self-reported)', OS: nav.platform, browser: nav.userAgent, authenticatorAttachment: created.authenticatorAttachment } };
  }
}
