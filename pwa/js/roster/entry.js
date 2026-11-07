// An entry of the device roster (docs/protocol/devices.md): the wrapper, its body and their checks,
// in the order the C# library (src/AgentBridge.Roster/Entry.cs, EntryBody.cs) runs them, so an
// input with one defect is refused with the same code by both. Async only because WebCrypto is.
import { RosterFormatError, REFUSAL } from './refusal.js';
import { base64Url, fromBase64Url, tryBase64Url, isId, hex, compare } from './codec.js';
import { frame, utf8, sha256, LABEL } from './framing.js';
import { point, verify, checkRosterSignature, checkAssertionSignature } from './p256.js';
import { parseStrict, members, object, string, bool, integer, scalars, utf8Bytes, hasControl } from './strict-json.js';

export const MAX_BYTES = 8 * 1024;
export const MAX_REMOVE_BYTES = 46 * 1024;
export const MAX_KEEP = 512;
export const MAX_NAME_SCALARS = 64;
export const MAX_NAME_BYTES = 256;
export const MAX_ACCOUNT_BYTES = 128;
export const MAX_RP_ID_LENGTH = 253;
export const MAX_CREDENTIAL_ID_BYTES = 1023;
export const SIGN_ALGORITHM = 'p256-sha256-der';

// The person's kinds. Adding a class later means adding it here and in the C# RosterKinds.
export const PERSON_KINDS = Object.freeze(['app', 'pwa', 'phone']);
export const isPerson = (kind) => PERSON_KINDS.includes(kind);
const KINDS = ['host', ...PERSON_KINDS];
const PLATFORMS = ['windows', 'macos', 'linux', 'ios', 'ipados', 'android'];
const PRESENCE = ['key', 'webauthn', 'none'];

const ORDER = ['v', 'op', 'account', 'generation', 'seq', 'prev', 'by', 'cosigner', 'at', 'device', 'approval', 'target', 'keep', 'name', 'adopt'];
const COMMON = ['v', 'op', 'account', 'generation', 'seq', 'by', 'at'];
const FIELDS = {
  genesis: [[...COMMON, 'device', 'approval'], []],
  add: [[...COMMON, 'prev', 'device', 'approval'], []],
  remove: [[...COMMON, 'prev', 'target', 'keep'], ['cosigner']],
  rename: [[...COMMON, 'prev', 'target', 'name'], []],
  confirm: [[...COMMON, 'prev', 'target'], []],
  merge: [[...COMMON, 'prev'], ['adopt']],
};

// SHA-256 of the body's exact bytes.
export const entryHash = (body) => sha256(body);

// hex(SHA-256(dh)[0..15]).
export const deviceId = async (dh) => hex((await sha256(dh)).subarray(0, 16));

// The first 64 bits of the genesis hash, as four groups of four hex digits.
export function fingerprint(genesisHash) {
  const h = hex(genesisHash.subarray(0, 8));
  return `${h.slice(0, 4)} ${h.slice(4, 8)} ${h.slice(8, 12)} ${h.slice(12)}`;
}

export const signedBy = (body) => frame(utf8(LABEL.entry), body);
export const signedByCosigner = (body) => frame(utf8(LABEL.cosign), body);

// SHA-256(F("agent-bridge presence v1", role, body)), role 'by' or 'cosigner'.
export const presenceChallenge = (role, body) => sha256(frame(utf8(LABEL.presence), utf8(role), body));

export async function parseBody(bytes) {
  const root = object(parseStrict(bytes, 'body'), 'body');
  if (!root.has('v')) throw new RosterFormatError(REFUSAL.missing, 'body.v');
  const v = root.get('v');
  // Version 2 is a host's add carrying a `hostSign` key (agent-principals.md §8.10, V1).
  if (v.t !== 'number' || (v.raw !== '1' && v.raw !== '2')) throw new RosterFormatError(REFUSAL.version, 'body.v');
  const version = v.raw === '2' ? 2 : 1;
  if (!root.has('op')) throw new RosterFormatError(REFUSAL.missing, 'body.op');
  const op = string(root.get('op'), 'body.op');
  if (!Object.hasOwn(FIELDS, op)) throw new RosterFormatError(REFUSAL.value, 'body.op');
  if (version === 2 && op !== 'add') throw new RosterFormatError(REFUSAL.version, 'body.v: 2 is only for a host\'s add');
  const [required, optional] = FIELDS[op];
  members(root, [...required, ...optional], ORDER.filter((f) => required.includes(f)), 'body');

  const account = string(root.get('account'), 'body.account');
  if (account.length === 0 || utf8Bytes(account) > MAX_ACCOUNT_BYTES || hasControl(account)) throw new RosterFormatError(REFUSAL.account, 'body.account');
  const generation = string(root.get('generation'), 'body.generation');
  if (!isId(generation)) throw new RosterFormatError(REFUSAL.id, 'body.generation');
  const seq = integer(root.get('seq'), 'body.seq');
  if (seq === null || (op === 'genesis') !== (seq === 0)) throw new RosterFormatError(REFUSAL.seq, 'body.seq');
  const prev = root.has('prev') ? hashes(root.get('prev'), 'body.prev', op === 'merge' ? 2 : 1, op === 'merge' ? 4 : 1) : [];
  const by = id(root.get('by'), 'body.by');
  const cosigner = root.has('cosigner') ? id(root.get('cosigner'), 'body.cosigner') : null;
  const at = string(root.get('at'), 'body.at');
  if (!isUtc(at)) throw new RosterFormatError(REFUSAL.time, 'body.at');
  const device = root.has('device') ? await parseDevice(root.get('device'), version, account, generation) : null;
  const approval = root.has('approval') ? parseApproval(root.get('approval'), op) : null;
  const target = root.has('target') ? parseTarget(root.get('target')) : null;
  const keep = root.has('keep') ? hashes(root.get('keep'), 'body.keep', 0, MAX_KEEP) : [];
  const name = root.has('name') ? deviceName(root.get('name'), 'body.name') : null;
  let adopt = false;
  if (root.has('adopt')) {
    adopt = bool(root.get('adopt'), 'body.adopt');
    if (!adopt) throw new RosterFormatError(REFUSAL.value, 'body.adopt');
  }

  if (op === 'genesis') {
    if (!isPerson(device.kind)) throw new RosterFormatError(REFUSAL.device, 'a genesis adds a person\'s device');
    if (by !== device.id) throw new RosterFormatError(REFUSAL.signer, 'a genesis is signed by the device it adds');
  }
  if (op === 'remove') {
    if (target.id === by && (keep.length > 0 || cosigner !== null)) throw new RosterFormatError(REFUSAL.signer, 'a self-removal keeps nothing and has no cosigner');
    if (cosigner !== null && (cosigner === by || cosigner === target.id)) throw new RosterFormatError(REFUSAL.signer, 'a cosigner is neither the signer nor the target');
  }
  return { op, version, account, generation, seq, prev, by, cosigner, at, device, approval, target, keep, name, adopt };
}

export function id(node, what) {
  const value = string(node, what);
  if (!isId(value)) throw new RosterFormatError(REFUSAL.id, what);
  return value;
}

export function hashes(node, what, min, max) {
  if (node.t !== 'array') throw new RosterFormatError(REFUSAL.type, what);
  if (node.items.length < min || node.items.length > max) throw new RosterFormatError(REFUSAL.count, what);
  const list = node.items.map((item) => fromBase64Url(string(item, what), what, 32));
  for (let i = 1; i < list.length; i++) {
    if (compare(list[i - 1], list[i]) >= 0) throw new RosterFormatError(REFUSAL.order, what);
  }
  return list;
}

// RFC 3339 in UTC to the second, up to nine fraction digits, on a real calendar date.
export function isUtc(at) {
  const m = /^([0-9]{4})-([0-9]{2})-([0-9]{2})T([0-9]{2}):([0-9]{2}):([0-9]{2})(?:\.[0-9]{1,9})?Z$/.exec(at);
  if (!m) return false;
  const [year, month, day, hour, minute, second] = m.slice(1).map(Number);
  if (year < 1 || month < 1 || month > 12 || hour > 23 || minute > 59 || second > 59) return false;
  const leap = (year % 4 === 0 && year % 100 !== 0) || year % 400 === 0;
  const days = [31, leap ? 29 : 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31][month - 1];
  return day >= 1 && day <= days;
}

function deviceName(node, what) {
  const name = string(node, what);
  const n = scalars(name);
  if (n < 1 || n > MAX_NAME_SCALARS || utf8Bytes(name) > MAX_NAME_BYTES || hasControl(name)) throw new RosterFormatError(REFUSAL.name, what);
  return name;
}

const DEVICE_ORDER = ['id', 'dh', 'sign', 'signAlg', 'presence', 'webauthn', 'kind', 'platform', 'name'];

const HOST_V2_ORDER = ['id', 'dh', 'hostSign', 'hostSignAlg', 'hostSignProof', 'kind', 'platform', 'name'];

// What `hostSignProof` signs: F("agent-bridge host key binding v1", account, generation, dh).
export const hostKeyBinding = (account, generation, dh) => frame(utf8(LABEL.hostKeyBinding), utf8(account), utf8(generation), dh);

async function parseDevice(node, version, account, generation) {
  const d = object(node, 'body.device');
  if (!d.has('kind')) throw new RosterFormatError(REFUSAL.missing, 'body.device.kind');
  const kind = string(d.get('kind'), 'body.device.kind');
  if (!KINDS.includes(kind)) throw new RosterFormatError(REFUSAL.value, 'body.device.kind');
  const person = isPerson(kind);
  if (version === 2 && person) throw new RosterFormatError(REFUSAL.version, 'body.v: 2 is only for a host\'s add');
  let presence = null;
  if (person) {
    if (!d.has('presence')) throw new RosterFormatError(REFUSAL.missing, 'body.device.presence');
    presence = string(d.get('presence'), 'body.device.presence');
    if (!PRESENCE.includes(presence)) throw new RosterFormatError(REFUSAL.value, 'body.device.presence');
  }
  const fields = !person ? (version === 2 ? HOST_V2_ORDER : ['id', 'dh', 'kind', 'platform', 'name'])
    : presence === 'webauthn' ? DEVICE_ORDER
      : ['id', 'dh', 'sign', 'signAlg', 'presence', 'kind', 'platform', 'name'];
  members(d, fields, fields, 'body.device');

  const deviceIdText = id(d.get('id'), 'body.device.id');
  const dh = point(string(d.get('dh'), 'body.device.dh'), 'body.device.dh');
  if (await deviceId(dh) !== deviceIdText) throw new RosterFormatError(REFUSAL.device, 'body.device.id is not its dh key\'s');
  let sign = null;
  let signAlg = null;
  let webauthn = null;
  if (person) {
    sign = point(string(d.get('sign'), 'body.device.sign'), 'body.device.sign');
    signAlg = string(d.get('signAlg'), 'body.device.signAlg');
    if (signAlg !== SIGN_ALGORITHM) throw new RosterFormatError(REFUSAL.value, 'body.device.signAlg');
    if (presence === 'webauthn') webauthn = parseCredential(d.get('webauthn'));
  }
  let hostSign = null;
  let hostSignAlg = null;
  let hostSignProof = null;
  if (version === 2) {
    hostSign = point(string(d.get('hostSign'), 'body.device.hostSign'), 'body.device.hostSign');
    hostSignAlg = string(d.get('hostSignAlg'), 'body.device.hostSignAlg');
    if (hostSignAlg !== SIGN_ALGORITHM) throw new RosterFormatError(REFUSAL.value, 'body.device.hostSignAlg');
    hostSignProof = fromBase64Url(string(d.get('hostSignProof'), 'body.device.hostSignProof'), 'body.device.hostSignProof');
    checkRosterSignature(hostSignProof, 'body.device.hostSignProof');
  }
  const platform = string(d.get('platform'), 'body.device.platform');
  if (!PLATFORMS.includes(platform)) throw new RosterFormatError(REFUSAL.value, 'body.device.platform');
  const name = deviceName(d.get('name'), 'body.device.name');
  if (hostSign && !(await verify(hostSign, hostKeyBinding(account, generation, dh), hostSignProof))) throw new RosterFormatError(REFUSAL.hostProof, 'body.device.hostSignProof');
  return { id: deviceIdText, dh, sign, signAlg, presence, webauthn, kind, platform, name, hostSign, hostSignAlg, hostSignProof };
}

const CREDENTIAL_FIELDS = ['rpId', 'credentialId', 'key', 'attested', 'backupEligible'];

export function parseCredential(node) {
  const w = object(node, 'body.device.webauthn');
  members(w, CREDENTIAL_FIELDS, CREDENTIAL_FIELDS, 'body.device.webauthn');
  const rpId = string(w.get('rpId'), 'body.device.webauthn.rpId');
  if (!isRpId(rpId)) throw new RosterFormatError(REFUSAL.value, 'body.device.webauthn.rpId');
  const credentialId = fromBase64Url(string(w.get('credentialId'), 'body.device.webauthn.credentialId'), 'body.device.webauthn.credentialId');
  if (credentialId.length === 0 || credentialId.length > MAX_CREDENTIAL_ID_BYTES) throw new RosterFormatError(REFUSAL.length, 'body.device.webauthn.credentialId');
  const key = point(string(w.get('key'), 'body.device.webauthn.key'), 'body.device.webauthn.key');
  const attested = bool(w.get('attested'), 'body.device.webauthn.attested');
  const backupEligible = bool(w.get('backupEligible'), 'body.device.webauthn.backupEligible');
  return { rpId, credentialId, key, attested, backupEligible };
}

export function isRpId(rpId) {
  if (rpId.length === 0 || rpId.length > MAX_RP_ID_LENGTH) return false;
  return rpId.split('.').every((label) => /^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$/.test(label));
}

function parseApproval(node, op) {
  const fail = (what) => new RosterFormatError(REFUSAL.approval, `body.approval${what}`);
  if (node.t !== 'object' || !node.has('method') || node.get('method').t !== 'string') throw fail('.method');
  const method = node.get('method').v;
  const fields = { code: ['method', 'transcript'], qr: ['method', 'transcript', 'shown'], grandfather: ['method'], genesis: ['method'] }[method];
  if (!fields || !Object.hasOwn({ code: 1, qr: 1, grandfather: 1, genesis: 1 }, method)) throw fail('.method');
  if ((op === 'genesis') !== (method === 'genesis')) throw fail('.method');
  for (const [name] of node.members) if (!fields.includes(name)) throw fail(`.${name}`);
  if (node.members.length !== fields.length) throw fail('');
  let transcript = null;
  if (node.has('transcript')) {
    const t = node.get('transcript');
    transcript = t.t === 'string' ? tryBase64Url(t.v) : null;
    if (!transcript || transcript.length !== 32) throw fail('.transcript');
  }
  let shown = null;
  if (node.has('shown')) {
    const s = node.get('shown');
    shown = s.t === 'string' ? s.v : null;
    if (shown !== 'approver' && shown !== 'joiner') throw fail('.shown');
  }
  return { method, transcript, shown };
}

const TARGET_FIELDS = ['id', 'add'];

function parseTarget(node) {
  const t = object(node, 'body.target');
  members(t, TARGET_FIELDS, TARGET_FIELDS, 'body.target');
  return { id: id(t.get('id'), 'body.target.id'), add: fromBase64Url(string(t.get('add'), 'body.target.add'), 'body.target.add', 32) };
}

const WRAPPER_FIELDS = ['body', 'sig', 'cosig', 'uv'];
const UV_FIELDS = ['by', 'cosigner'];
const ASSERTION_FIELDS = ['authenticatorData', 'clientDataJSON', 'signature'];

export function signature(node, what) {
  const der = fromBase64Url(string(node, what), what);
  checkRosterSignature(der, what);
  return der;
}

export function parseAssertion(node, what) {
  const a = object(node, what);
  members(a, ASSERTION_FIELDS, ASSERTION_FIELDS, what);
  const authenticatorData = fromBase64Url(string(a.get('authenticatorData'), `${what}.authenticatorData`), `${what}.authenticatorData`);
  const clientDataJSON = fromBase64Url(string(a.get('clientDataJSON'), `${what}.clientDataJSON`), `${what}.clientDataJSON`);
  const sig = fromBase64Url(string(a.get('signature'), `${what}.signature`), `${what}.signature`);
  checkAssertionSignature(sig, `${what}.signature`);
  return { authenticatorData, clientDataJSON, signature: sig };
}

const sized = (a) => (a ? a.authenticatorData.length + a.clientDataJSON.length + a.signature.length : 0);

// The wrapper's UTF-8 bytes as received. Resolves to the entry, with its hash, size and canonical
// form; rejects with a RosterFormatError naming the first defect.
export async function parseEntry(wrapper) {
  const root = object(parseStrict(wrapper, 'entry'), 'entry');
  members(root, WRAPPER_FIELDS, ['body', 'sig'], 'entry');
  const body = fromBase64Url(string(root.get('body'), 'entry.body'), 'entry.body');
  const sig = signature(root.get('sig'), 'entry.sig');
  const cosig = root.has('cosig') ? signature(root.get('cosig'), 'entry.cosig') : null;
  let uvBy = null;
  let uvCosigner = null;
  if (root.has('uv')) {
    const u = object(root.get('uv'), 'entry.uv');
    members(u, UV_FIELDS, [], 'entry.uv');
    if (u.members.length === 0) throw new RosterFormatError(REFUSAL.value, 'entry.uv is empty');
    if (u.has('by')) uvBy = parseAssertion(u.get('by'), 'entry.uv.by');
    if (u.has('cosigner')) uvCosigner = parseAssertion(u.get('cosigner'), 'entry.uv.cosigner');
  }
  const size = body.length + sig.length + (cosig?.length ?? 0) + sized(uvBy) + sized(uvCosigner);
  if (size > MAX_REMOVE_BYTES) throw new RosterFormatError(REFUSAL.size, 'entry');

  const parsed = await parseBody(body);
  if (parsed.op !== 'remove' && size > MAX_BYTES) throw new RosterFormatError(REFUSAL.size, 'entry');
  if ((cosig === null) !== (parsed.cosigner === null)) throw new RosterFormatError(REFUSAL.cosigner, 'entry.cosig');
  if (uvCosigner !== null && parsed.cosigner === null) throw new RosterFormatError(REFUSAL.cosigner, 'entry.uv.cosigner');
  return { bodyBytes: body, body: parsed, sig, cosig, uvBy, uvCosigner, hash: await entryHash(body), size, canonical: canonical(body, sig, cosig, uvBy, uvCosigner) };
}

const assertionJson = (a) => `{"authenticatorData":"${base64Url(a.authenticatorData)}","clientDataJSON":"${base64Url(a.clientDataJSON)}","signature":"${base64Url(a.signature)}"}`;

// The wrapper as every sender and forwarder emits it: members in order, no whitespace.
export function canonical(body, sig, cosig = null, uvBy = null, uvCosigner = null) {
  let out = `{"body":"${base64Url(body)}","sig":"${base64Url(sig)}"`;
  if (cosig) out += `,"cosig":"${base64Url(cosig)}"`;
  if (uvBy || uvCosigner) {
    const parts = [];
    if (uvBy) parts.push(`"by":${assertionJson(uvBy)}`);
    if (uvCosigner) parts.push(`"cosigner":${assertionJson(uvCosigner)}`);
    out += `,"uv":{${parts.join(',')}}`;
  }
  return `${out}}`;
}
