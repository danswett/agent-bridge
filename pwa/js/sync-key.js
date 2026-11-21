// Dormant K1 vector checker, not a sync client: no storage, signing, key creation or I/O.
import { equal, fromBase64Url } from './roster/codec.js';
import { frame, sha256, utf8 } from './roster/framing.js';
import { hashes, id, isPerson, parseAssertion, signature } from './roster/entry.js';
import { members, object, parseStrict, string } from './roster/strict-json.js';
import { verify } from './roster/p256.js';
import { presenceProof } from './roster/presence.js';

const MANIFEST = 'agent-bridge sync key manifest v1';
const COMMITMENT = 'agent-bridge sync key commitment v1';
const WRAP = 'agent-bridge sync key wrap v1';
const AAD = 'agent-bridge sync key wrap aad v1';
const fail = (code) => { throw new Error(code); };
const b64 = (s, n = 32) => fromBase64Url(s, 'sync-key', n);
const read = (bytes, fields, optional = []) => {
  const root = object(parseStrict(bytes, 'sync-key'), 'sync-key');
  members(root, [...fields, ...optional], fields, 'sync-key');
  return root;
};
const text = (root, name) => string(root.get(name), `sync-key.${name}`);
const json = (bytes) => JSON.parse(new TextDecoder().decode(bytes));
function version(root, kind) {
  if (root.get('v').t !== 'number' || root.get('v').raw !== '1' || text(root, 'kind') !== kind) fail('version');
}
function admission(roster, deviceId, addHash) {
  const add = roster.state.activeAdd(deviceId);
  if (add === null) fail('admission');
  const entry = roster.graph.at(add);
  if (!equal(entry.hash, b64(addHash)) || !isPerson(entry.body.device.kind)
      || roster.onProbation(add) || roster.held.has(deviceId) || roster.signerKeyCollides(add)) fail('admission');
  return { add, device: entry.body.device };
}
export const manifestSigned = (body) => frame(utf8(MANIFEST), body);
const commitment = (key, b) => sha256(frame(utf8(COMMITMENT), utf8(b.account), utf8(b.generation), utf8(b.epoch), key));
async function checkCommitment(key, body) {
  if (key.length !== 32 || !equal(await commitment(key, body), b64(body.commitment))) fail('commitment');
}

export async function checkManifest(bytes, roster, { previous = null, key } = {}) {
  const wrapper = read(bytes, ['body', 'sig'], ['uv']);
  const bodyBytes = fromBase64Url(text(wrapper, 'body'), 'manifest.body');
  const sig = signature(wrapper.get('sig'), 'manifest.sig');
  let uv = null;
  if (wrapper.has('uv')) {
    const u = object(wrapper.get('uv'), 'manifest.uv');
    members(u, ['by'], ['by'], 'manifest.uv');
    uv = parseAssertion(u.get('by'), 'manifest.uv.by');
  }
  if (bodyBytes.length + sig.length + (uv ? uv.authenticatorData.length + uv.clientDataJSON.length + uv.signature.length : 0) > 8192) fail('size');
  const root = read(bodyBytes, ['v', 'kind', 'account', 'generation', 'epoch', 'previous', 'heads', 'by', 'byAdd', 'commitment']);
  version(root, 'manifest');
  const body = json(bodyBytes);
  if (text(root, 'account') !== roster.account || id(root.get('generation'), 'generation') !== roster.generation) fail('scope');
  id(root.get('epoch'), 'epoch');
  id(root.get('by'), 'by');
  b64(text(root, 'byAdd'));
  b64(text(root, 'commitment'));
  if (root.get('previous').t !== 'null') b64(text(root, 'previous'));
  if (body.previous !== previous) fail('previous');
  const heads = hashes(root.get('heads'), 'heads', 1, 4);
  if (heads.some((h) => !roster.holds(h) || roster.isQuarantined(roster.graph.find(h)))) fail('heads');
  const { add, device } = admission(roster, body.by, body.byAdd);
  if (roster.stateOf(heads.map((h) => roster.graph.find(h))).activeAdd(body.by) !== add) fail('admission');
  if (!(await verify(device.sign, manifestSigned(bodyBytes), sig))) fail('signature');
  if (await presenceProof(bodyBytes, device, 'sync-key-manifest', uv) !== null) fail('presence');
  if (key !== undefined) await checkCommitment(key, body);
  return { body, hash: await sha256(bodyBytes) };
}

const wrapContext = (w) => frame(utf8(w.account), utf8(w.generation), utf8(w.epoch),
  utf8(w.holder), b64(w.holderAdd), utf8(w.target), b64(w.targetAdd), b64(w.manifest));
export const wrapAad = (w) => frame(utf8(AAD), wrapContext(w));
export async function deriveWrapKey(secret, wrap) {
  if (secret.length !== 32) fail('key');
  const ikm = await crypto.subtle.importKey('raw', secret, 'HKDF', false, ['deriveBits']);
  return new Uint8Array(await crypto.subtle.deriveBits({
    name: 'HKDF', hash: 'SHA-256', salt: utf8(WRAP), info: wrapContext(wrap),
  }, ikm, 256));
}
export async function openWrap(bytes, manifest, roster, secret, recipient, options = {}) {
  const root = read(bytes, ['v', 'kind', 'account', 'generation', 'epoch', 'manifest', 'holder', 'holderAdd', 'target', 'targetAdd', 'nonce', 'ciphertext']);
  version(root, 'keywrap');
  const w = json(bytes);
  const checked = await checkManifest(manifest, roster, options);
  for (const field of ['account', 'generation', 'epoch']) {
    if (text(root, field) !== checked.body[field]) fail('binding');
  }
  if (!equal(b64(text(root, 'manifest')), checked.hash)) fail('binding');
  id(root.get('holder'), 'holder');
  id(root.get('target'), 'target');
  admission(roster, w.holder, text(root, 'holderAdd'));
  admission(roster, w.target, text(root, 'targetAdd'));
  if (w.target !== recipient.id || w.targetAdd !== recipient.add) fail('recipient');
  if (w.holder === w.target) fail('admission');
  const nonce = b64(text(root, 'nonce'), 12);
  const ciphertext = b64(text(root, 'ciphertext'), 48);
  const key = await crypto.subtle.importKey('raw', await deriveWrapKey(secret, w), 'AES-GCM', false, ['decrypt']);
  let plain;
  try {
    plain = new Uint8Array(await crypto.subtle.decrypt({ name: 'AES-GCM', iv: nonce, additionalData: wrapAad(w), tagLength: 128 }, key, ciphertext));
  } catch (e) {
    if (e.name === 'OperationError') fail('ciphertext');
    throw e;
  }
  await checkCommitment(plain, checked.body);
  return plain;
}
