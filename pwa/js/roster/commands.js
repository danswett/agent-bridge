// Vetoes (design §5.6), person commands (§7.2) and head attestations (§6.3): the PWA's port of
// src/AgentBridge.Roster/Commands.cs and Instant.cs, which docs/protocol/vectors/roster-commands.json
// checks. The same checks in the same order, so one defect has one code everywhere.
import { RosterFormatError, REFUSAL } from './refusal.js';
import { base64Url, compare, fromBase64Url, isId } from './codec.js';
import { LABEL, frame, hmacSha256, utf8 } from './framing.js';
import { MAX_ACCOUNT_BYTES, MAX_BYTES, hashes, id, isPerson, isUtc, parseAssertion, signature } from './entry.js';
import { members, object, parseStrict, string, utf8Bytes, hasControl } from './strict-json.js';
import { verify } from './p256.js';
import { INVALID } from './invalid.js';
import { presenceProof } from './presence.js';

export const ROLE = Object.freeze({ veto: 'veto', command: 'command' });

// ---- instants ----

// An RFC 3339 UTC time (isUtc) as an exact instant: whole seconds since the Unix epoch and the
// nanoseconds after them, so a boundary falls the same way whatever a clock's resolution.
export function instant(at) {
  if (!isUtc(at)) throw new RangeError('not an RFC 3339 UTC time');
  const n = (start, count) => Number(at.slice(start, start + count));
  const seconds = daysFromCivil(n(0, 4), n(5, 2), n(8, 2)) * 86400 + n(11, 2) * 3600 + n(14, 2) * 60 + n(17, 2);
  const nanos = at.length > 20 ? Number(at.slice(20, -1).padEnd(9, '0')) : 0;
  return { seconds, nanos };
}

// A time in milliseconds since the epoch (Date.now()) as an instant.
export function instantOfMs(ms) {
  const seconds = Math.floor(ms / 1000);
  return { seconds, nanos: Math.round((ms - seconds * 1000) * 1e6) };
}

export const compareInstants = (a, b) => (a.seconds !== b.seconds ? a.seconds - b.seconds : a.nanos - b.nanos);
const plus = (t, s) => ({ seconds: t.seconds + s, nanos: t.nanos });

function daysFromCivil(year, month, day) {
  const y = month <= 2 ? year - 1 : year;
  const era = Math.floor(y / 400);
  const yoe = y - era * 400;
  const doy = Math.floor((153 * (month + (month > 2 ? -3 : 9)) + 2) / 5) + day - 1;
  const doe = yoe * 365 + Math.floor(yoe / 4) - Math.floor(yoe / 100) + doy;
  return era * 146097 + doe - 719468;
}

// ---- what vetoes and person commands share ----

const WRAPPER_FIELDS = ['body', 'sig', 'uv'];

function signedObject(wrapper, what) {
  const root = object(parseStrict(wrapper, what), what);
  members(root, WRAPPER_FIELDS, ['body', 'sig'], what);
  const body = fromBase64Url(string(root.get('body'), `${what}.body`), `${what}.body`);
  const sig = signature(root.get('sig'), `${what}.sig`);
  let uv = null;
  if (root.has('uv')) {
    const u = object(root.get('uv'), `${what}.uv`);
    members(u, ['by'], [], `${what}.uv`);
    if (!u.has('by')) throw new RosterFormatError(REFUSAL.value, `${what}.uv is empty`);
    uv = parseAssertion(u.get('by'), `${what}.uv.by`);
  }
  const size = body.length + sig.length + (uv ? uv.authenticatorData.length + uv.clientDataJSON.length + uv.signature.length : 0);
  if (size > MAX_BYTES) throw new RosterFormatError(REFUSAL.size, what);
  return { body, sig, uv };
}

function version(root) {
  if (!root.has('v')) throw new RosterFormatError(REFUSAL.missing, 'body.v');
  const v = root.get('v');
  if (v.t !== 'number' || v.raw !== '1') throw new RosterFormatError(REFUSAL.version, 'body.v');
}

function time(node, what) {
  const at = string(node, what);
  if (!isUtc(at)) throw new RosterFormatError(REFUSAL.time, what);
  return at;
}

const assertionJson = (a) => `{"authenticatorData":"${base64Url(a.authenticatorData)}","clientDataJSON":"${base64Url(a.clientDataJSON)}","signature":"${base64Url(a.signature)}"}`;

export function canonicalSigned(body, sig, uv = null) {
  let out = `{"body":"${base64Url(body)}","sig":"${base64Url(sig)}"`;
  if (uv) out += `,"uv":{"by":${assertionJson(uv)}}`;
  return `${out}}`;
}

// The active person's device `by` names in a verifier's applied state, or null.
function personAdd(roster, by) {
  const add = roster.state.activeAdd(by);
  return add !== null && isPerson(roster.graph.at(add).body.device.kind) ? add : null;
}

// ---- vetoes ----

const VETO_FIELDS = ['v', 'op', 'account', 'generation', 'reset', 'by', 'at'];

export const vetoSigned = (body) => frame(utf8(LABEL.veto), body);

// The wrapper's UTF-8 bytes as received. Rejects with a RosterFormatError naming the first defect.
export function parseVeto(wrapper) {
  const { body, sig, uv } = signedObject(wrapper, 'veto');
  const root = object(parseStrict(body, 'body'), 'body');
  version(root);
  if (!root.has('op')) throw new RosterFormatError(REFUSAL.missing, 'body.op');
  if (string(root.get('op'), 'body.op') !== 'veto') throw new RosterFormatError(REFUSAL.value, 'body.op');
  members(root, VETO_FIELDS, VETO_FIELDS, 'body');
  const account = string(root.get('account'), 'body.account');
  if (account.length === 0 || utf8Bytes(account) > MAX_ACCOUNT_BYTES || hasControl(account)) throw new RosterFormatError(REFUSAL.account, 'body.account');
  const generation = string(root.get('generation'), 'body.generation');
  if (!isId(generation)) throw new RosterFormatError(REFUSAL.id, 'body.generation');
  const reset = fromBase64Url(string(root.get('reset'), 'body.reset'), 'body.reset', 32);
  const by = id(root.get('by'), 'body.by');
  const at = time(root.get('at'), 'body.at');
  return { bodyBytes: body, body: { account, generation, reset, by, at }, sig, uv };
}

// Whether the relay takes this veto of the reset whose genesis it holds: null if so, else the
// code. `current` is its copy of the current generation (a RosterVerifier).
export async function checkVeto(veto, current, held) {
  const b = veto.body;
  if (b.account !== current.account) return INVALID.account;
  if (b.generation !== current.generation) return INVALID.generation;
  if (compare(b.reset, held) !== 0) return INVALID.reset;
  const add = personAdd(current, b.by);
  if (add === null) return INVALID.signer;
  const d = current.graph.at(add).body.device;
  if (!(await verify(d.sign, vetoSigned(veto.bodyBytes), veto.sig))) return INVALID.signature;
  const noPresence = await presenceProof(veto.bodyBytes, d, ROLE.veto, veto.uv);
  if (noPresence !== null) return noPresence;
  return current.signerKeyCollides(add) ? INVALID.hostKey : null;
}

// ---- person commands ----

export const ACTION = Object.freeze({
  transfer: 'transfer',
  folderTrustAnswer: 'folder-trust-answer',
  update: 'update',
  forget: 'forget',
  attest: 'attest',
});

// The members each action has besides the common ones, in the body and in its target; every one
// is required, and no other allowed.
const ACTION_MEMBERS = {
  transfer: [['to'], ['session']],
  'folder-trust-answer': [['choice', 'nonce'], ['question']],
  update: [['nonce'], []],
  forget: [[], []],
  attest: [['nonce'], []],
};

export const needsNonce = (action) => Object.hasOwn(ACTION_MEMBERS, action) && ACTION_MEMBERS[action][0].includes('nonce');

export const MAX_LIFETIME_S = 600;
export const SKEW_S = 120;
const COMMON = ['v', 'action', 'request', 'by', 'target', 'issuedAt', 'expiresAt', 'heads'];
const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;
const QUESTION = /^[A-Za-z0-9._:-]{1,128}$/;

export const commandSigned = (body) => frame(utf8(LABEL.personCommand), body);

export function parsePersonCommand(wrapper) {
  const { body, sig, uv } = signedObject(wrapper, 'command');
  const root = object(parseStrict(body, 'body'), 'body');
  version(root);
  if (!root.has('action')) throw new RosterFormatError(REFUSAL.missing, 'body.action');
  const action = string(root.get('action'), 'body.action');
  if (!Object.hasOwn(ACTION_MEMBERS, action)) throw new RosterFormatError(REFUSAL.value, 'body.action');
  const [extraBody, extraTarget] = ACTION_MEMBERS[action];
  const fields = [...COMMON, ...extraBody];
  members(root, fields, fields, 'body');

  const request = string(root.get('request'), 'body.request');
  if (!UUID.test(request)) throw new RosterFormatError(REFUSAL.value, 'body.request');
  const by = id(root.get('by'), 'body.by');
  const t = object(root.get('target'), 'body.target');
  const targetFields = ['machine', ...extraTarget];
  members(t, targetFields, targetFields, 'body.target');
  const machine = id(t.get('machine'), 'body.target.machine');
  let session = null;
  let question = null;
  if (t.has('session')) {
    session = string(t.get('session'), 'body.target.session');
    if (!UUID.test(session)) throw new RosterFormatError(REFUSAL.value, 'body.target.session');
  }
  if (t.has('question')) {
    question = string(t.get('question'), 'body.target.question');
    if (!QUESTION.test(question)) throw new RosterFormatError(REFUSAL.value, 'body.target.question');
  }
  const to = root.has('to') ? id(root.get('to'), 'body.to') : null;
  let choice = null;
  if (root.has('choice')) {
    choice = string(root.get('choice'), 'body.choice');
    if (choice.length === 0 || utf8Bytes(choice) > 256 || hasControl(choice)) throw new RosterFormatError(REFUSAL.value, 'body.choice');
  }
  const nonce = root.has('nonce') ? fromBase64Url(string(root.get('nonce'), 'body.nonce'), 'body.nonce', 32) : null;
  const issuedAt = time(root.get('issuedAt'), 'body.issuedAt');
  const expiresAt = time(root.get('expiresAt'), 'body.expiresAt');
  const heads = hashes(root.get('heads'), 'body.heads', 1, 4);
  return { bodyBytes: body, body: { action, request, by, target: { machine, session, question }, to, choice, nonce, issuedAt, expiresAt, heads }, sig, uv };
}

const equalBytes = (a, b) => {
  if (a.length !== b.length) return false;
  let d = 0;
  for (let i = 0; i < a.length; i++) d |= a[i] ^ b[i];
  return d === 0;
};

// The stateless checks of §7.2: null when the host may act (once it has recorded the request
// durably, its own obligation), else the code. `context`: { host, action, now (an instant: the
// later of the wall clock and the host's high-water mark), floor?, nonce?, previous? }.
export async function checkPersonCommand(command, roster, context) {
  const b = command.body;
  if (b.action !== context.action) return INVALID.action;
  if (b.target.machine !== context.host) return INVALID.machine;
  if (b.by === b.target.machine || b.by === b.to) return INVALID.signer;
  const add = personAdd(roster, b.by);
  if (add === null) return INVALID.signer;
  if (roster.onProbation(add) || roster.held.has(b.by)) return INVALID.probation;
  const d = roster.graph.at(add).body.device;
  if (!(await verify(d.sign, commandSigned(command.bodyBytes), command.sig))) return INVALID.signature;
  const noPresence = await presenceProof(command.bodyBytes, d, ROLE.command, command.uv);
  if (noPresence !== null) return noPresence;
  if (roster.signerKeyCollides(add)) return INVALID.hostKey;

  const issued = instant(b.issuedAt);
  const expires = instant(b.expiresAt);
  if (compareInstants(expires, issued) <= 0 || compareInstants(expires, plus(issued, MAX_LIFETIME_S)) > 0) return INVALID.lifetime;
  if (compareInstants(issued, plus(context.now, SKEW_S)) > 0) return INVALID.future;
  if (compareInstants(expires, context.now) < 0) return INVALID.expired;
  if (context.floor && compareInstants(issued, context.floor) <= 0) return INVALID.floor;

  if (needsNonce(b.action) && !(context.nonce && context.nonce.length === 32 && equalBytes(context.nonce, b.nonce))) return INVALID.nonce;
  if (b.heads.some((h) => !roster.holds(h))) return INVALID.heads;
  if (context.previous) {
    const g = roster.graph;
    const heads = b.heads.map((h) => g.find(h));
    for (const p of context.previous) {
      const pi = g.find(p);
      if (pi === null || !heads.some((h) => h === pi || g.isAncestor(pi, h))) return INVALID.regress;
    }
  }
  return null;
}

// ---- head attestations ----

// HKDF from the raw ECDH secret (the shared point's x) with the attester and the host, the key a
// signed command between them uses (js/transfer.js commandKey).
export async function attestationKey(sharedSecret, attester, host) {
  const ikm = await crypto.subtle.importKey('raw', sharedSecret, 'HKDF', false, ['deriveBits']);
  const bits = await crypto.subtle.deriveBits({ name: 'HKDF', hash: 'SHA-256', salt: utf8('agent-bridge command v1'), info: utf8(`${attester}|${host}`) }, ikm, 256);
  return new Uint8Array(bits);
}

function joined(list, what) {
  const out = new Uint8Array(list.length * 32);
  list.forEach((h, i) => {
    if (h.length !== 32) throw new RangeError(`${what}: an entry hash is 32 bytes`);
    if (i > 0 && compare(list[i - 1], h) >= 0) throw new RangeError(`${what}: hashes are in head order, each once`);
    out.set(h, i * 32);
  });
  return out;
}

// F("agent-bridge roster head v1", account, generation, heads, host id, nonce, revoked): heads and
// revoked each one part, their hashes in head order joined; revoked always there.
export function attestationMessage(account, generation, heads, host, nonce, revoked) {
  if (heads.length === 0) throw new RangeError('an attestation names at least one head');
  if (nonce.length !== 32) throw new RangeError('the nonce is 32 bytes');
  return frame(utf8(LABEL.head), utf8(account), utf8(generation), joined(heads, 'heads'), utf8(host), nonce, joined(revoked, 'revoked'));
}

export const attestationMac = (key, message) => hmacSha256(key, message);

export async function verifyAttestation(key, message, mac) {
  return mac.length === 32 && equalBytes(await attestationMac(key, message), mac);
}

// The enrollment adds a verifier's applied state revokes, in head order.
export function revokedAdds(roster) {
  const s = roster.state;
  return roster.graph.nodes.filter((n) => n.isAdd && s.isRevoked(n.index)).map((n) => n.hash).sort(compare);
}
