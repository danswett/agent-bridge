import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { base64Url, fromBase64Url, fromHex, hex } from '../../js/roster/codec.js';
import { utf8 } from '../../js/roster/framing.js';
import { parseEntry } from '../../js/roster/entry.js';
import { RosterVerifier } from '../../js/roster/verifier.js';
import { commandKey } from '../../js/transfer.js';
import { checkManifest, manifestSigned, wrapAad, deriveWrapKey, openWrap } from '../../js/sync-key.js';

const v = JSON.parse(readFileSync(new URL('../../../docs/protocol/vectors/sync-key.json', import.meta.url), 'utf8'));
const bytes = (x) => utf8(JSON.stringify(x));
const b64 = (x) => fromBase64Url(x, 'vector');
const refused = (code) => (e) => e.message === code;
async function roster(mature = true) {
  let now = 0;
  const r = new RosterVerifier(v.account, v.generation, () => now);
  const results = await r.offer(await Promise.all(v.entries.map((e) => parseEntry(utf8(e)))));
  assert.ok(results.every((x) => x.verdict === 'accepted'));
  if (mature) now = 73 * 3600 * 1000;
  return r;
}
async function secret(mine, peer) {
  const pub = b64(mine.public);
  const key = await crypto.subtle.importKey('jwk', {
    kty: 'EC', crv: 'P-256', d: mine.private, x: base64Url(pub.slice(1, 33)), y: base64Url(pub.slice(33)),
  }, { name: 'ECDH', namedCurve: 'P-256' }, false, ['deriveBits']);
  const other = await crypto.subtle.importKey('raw', b64(peer.public), { name: 'ECDH', namedCurve: 'P-256' }, false, []);
  return new Uint8Array(await crypto.subtle.deriveBits({ name: 'ECDH', public: other }, key, 256));
}
const recipient = { id: v.wrap.target, add: v.wrap.targetAdd };
const open = async (wrap = v.wrap, r) => openWrap(bytes(wrap), bytes(v.manifest), r ?? await roster(), await secret(v.target, v.holder), recipient);

test('the signed manifest preserves its exact bytes, hash and key commitment', async () => {
  assert.equal(hex(manifestSigned(b64(v.manifest.body))), v.manifestMessage);
  const checked = await checkManifest(bytes(v.manifest), await roster(), { key: fromHex(v.epochKey) });
  assert.equal(base64Url(checked.hash), v.manifestHash);
});
test('both ECDH directions produce the wrap KAT and never the CommandMac key', async () => {
  const z = await secret(v.holder, v.target);
  assert.deepEqual(z, await secret(v.target, v.holder));
  assert.equal(hex(wrapAad(v.wrap)), v.wrapAad);
  assert.equal(hex(await deriveWrapKey(z, v.wrap)), v.wrapKey);
  const command = await commandKey({ dh: async () => z }, b64(v.target.public), v.wrap.holder, v.wrap.target);
  assert.equal(hex(command), v.commandKey);
  assert.notEqual(hex(command), v.wrapKey);
  assert.equal(hex(await open()), v.epochKey);
});
test('a manifest signature under the roster-entry label is not a manifest signature', async () => {
  await assert.rejects(checkManifest(bytes({ ...v.manifest, sig: v.wrongLabelSignature }), await roster()), refused('signature'));
});
test('a different epoch key cannot satisfy the signed commitment', async () => {
  await assert.rejects(checkManifest(bytes(v.manifest), await roster(), { key: new Uint8Array(32) }), refused('commitment'));
});
for (const [name, ciphertext] of Object.entries(v.refusedCiphertexts)) {
  test(`wrap refuses ${name}`, async () => {
    await assert.rejects(open({ ...v.wrap, ciphertext }), refused('ciphertext'));
  });
}
test('a known device id with the wrong add-entry hash is not an admission', async () => {
  await assert.rejects(open({ ...v.wrap, targetAdd: v.wrap.holderAdd }), refused('admission'));
});
test('a wrap for one device cannot be opened as another device', async () => {
  await assert.rejects(openWrap(bytes(v.wrap), bytes(v.manifest), await roster(), await secret(v.target, v.holder),
    { id: v.wrap.holder, add: v.wrap.holderAdd }), refused('recipient'));
});
test('a probationary or removed target cannot receive the key', async () => {
  await assert.rejects(open(v.wrap, await roster(false)), refused('admission'));
  const r = await roster();
  assert.equal((await r.offer([await parseEntry(utf8(v.removal))]))[0].verdict, 'accepted');
  await assert.rejects(open(v.wrap, r), refused('admission'));
});
test('a tampered manifest and an unpinned predecessor are refused', async () => {
  const body = JSON.parse(new TextDecoder().decode(b64(v.manifest.body)));
  body.commitment = base64Url(new Uint8Array(32));
  await assert.rejects(checkManifest(bytes({ ...v.manifest, body: base64Url(bytes(body)) }), await roster()), refused('signature'));
  await assert.rejects(checkManifest(bytes(v.manifest), await roster(), { previous: v.manifestHash }), refused('previous'));
});
