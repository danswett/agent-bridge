// The sealed roaming records (docs/protocol/roaming-records.md, #759 S2): the reference encoder, sealer
// and opener, replayed against docs/protocol/vectors/roaming-records.json: the oracle other clients port,
// not shipped code. UPDATE_PROTOCOL_VECTORS=1 rewrites the file; the committed bytes are what clients match.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, writeFileSync } from 'node:fs';
import { mergeOrder } from '../../js/order-merge.js';

const FILE = new URL('../../../docs/protocol/vectors/roaming-records.json', import.meta.url);
const enc = new TextEncoder();
const hex = (b) => Buffer.from(b).toString('hex');
const unhex = (h) => new Uint8Array(Buffer.from(h, 'hex'));
const plain = (o) => JSON.parse(JSON.stringify(o));
const BUCKETS = [1024, 2048, 4096, 8192, 16384, 32768, 65500];
const KINDS = { order: 1, marker: 2 };
const [HEADER, TAG, MAX_SEALED, MAX_ROWS, MAX_GROUPS, MAX_MARKERS] = [20, 16, 65536, 500, 32, 800];

class RecordError extends Error { constructor(code) { super(code); this.code = code; } }
const fail = (code) => { throw new RecordError(code); };
const refusal = async (fn) => { try { await fn(); } catch (e) { if (e instanceof RecordError) { return e.code; } throw e; } return null; };
const bad = (ok) => ok || fail('bad-schema');
const isInt = (n) => Number.isSafeInteger(n) && n >= 0;
const keyOk = (k) => /^[\x20-\x7e\n]{1,200}$/.test(k);
const idOk = (k) => typeof k === 'string' && /^[0-9a-z-]{1,36}$/.test(k);
const nameOk = (s) => typeof s === 'string' && s === s.trim() && [...s].length >= 1 && [...s].length <= 40;

// Canonical JSON: members sorted by key, no whitespace, JSON.stringify's escaping, non-ASCII literal UTF-8.
const canon = (v) => (Array.isArray(v) ? `[${v.map(canon).join(',')}]`
  : v !== null && typeof v === 'object' ? `{${Object.keys(v).sort().map((k) => `${JSON.stringify(k)}:${canon(v[k])}`).join(',')}}`
    : JSON.stringify(v));

export function encodeOrder(order) {
  const regs = [...Object.values(order.rows), ...Object.values(order.membership), ...Object.values(order.groups)];
  const d = [...new Set(regs.map((r) => r.deviceTag))].sort();
  const ref = (r) => [r.counter, d.indexOf(r.deviceTag)];
  const ids = Object.keys(order.groups).sort();
  const g = ids.map((id) => [id, ...ref(order.groups[id]), ...(order.groups[id].tombstone ? [] : [order.groups[id].value])]);
  const r = {};
  for (const [k, p] of Object.entries(order.rows)) { (r[k] ??= {}).p = p.tombstone ? ref(p) : [...ref(p), p.value.key, p.value.pinned === true ? 1 : 0]; }
  for (const [k, m] of Object.entries(order.membership)) { (r[k] ??= {}).m = m.tombstone ? ref(m) : [...ref(m), ids.includes(m.value) ? ids.indexOf(m.value) : null]; }
  return canon({ v: 1, d, g, r });
}

function parse(text) {
  let j;
  try { j = JSON.parse(text); } catch { return fail('bad-schema'); }
  bad(j !== null && typeof j === 'object' && Number.isInteger(j.v));
  return j.v > 1 ? fail('unsupported-version') : bad(j.v === 1) && j;
}

export function decodeOrder(text) {
  const j = parse(text);
  bad(Array.isArray(j.d) && j.d.length <= 255 && j.d.every((t) => /^[0-9a-f]{32}$/.test(t)) && Array.isArray(j.g) && j.r && typeof j.r === 'object' && !Array.isArray(j.r));
  const stamp = (a, n) => {
    bad(Array.isArray(a) && (a.length === 2 || a.length === n) && isInt(a[0]) && isInt(a[1]) && a[1] < j.d.length);
    return { counter: a[0], deviceTag: j.d[a[1]] };
  };
  const out = { rows: {}, membership: {}, groups: {} };
  let last = '';
  for (const [id, ...a] of j.g) {
    bad(idOk(id) && id > last);
    last = id;
    const s = stamp(a, 3);
    out.groups[id] = a.length === 2 ? { tombstone: true, ...s } : (bad(nameOk(a[2])), { value: a[2], ...s });
  }
  for (const [k, e] of Object.entries(j.r)) {
    bad(keyOk(k) && e !== null && typeof e === 'object' && (e.p || e.m) && Object.keys(e).every((f) => f === 'p' || f === 'm'));
    if (e.p) {
      const s = stamp(e.p, 4);
      out.rows[k] = e.p.length === 2 ? { tombstone: true, ...s }
        : (bad(/^[0-9A-Za-z]{1,32}$/.test(e.p[2]) && (e.p[3] === 0 || e.p[3] === 1)), { value: { key: e.p[2], pinned: e.p[3] === 1 }, ...s });
    }
    if (e.m) {
      const s = stamp(e.m, 3);
      out.membership[k] = e.m.length === 2 ? { tombstone: true, ...s }
        : (bad(e.m[2] === null || (isInt(e.m[2]) && e.m[2] < j.g.length)), { value: e.m[2] === null ? null : j.g[e.m[2]][0], ...s });
    }
  }
  bad(Object.values(out.rows).filter((r) => !r.tombstone).length <= MAX_ROWS && Object.values(out.groups).filter((r) => !r.tombstone).length <= MAX_GROUPS);
  return out;
}

export const encodeMarkers = (m) => canon({ v: 1, m });
export function decodeMarkers(text) {
  const { m } = parse(text);
  bad(m && typeof m === 'object' && Object.keys(m).length <= MAX_MARKERS && Object.entries(m).every(([k, t]) => keyOk(k) && isInt(t)));
  return m;
}

const slotKey = async (key, slot) => crypto.subtle.deriveKey(
  { name: 'HKDF', hash: 'SHA-256', salt: enc.encode('agent-bridge roaming v1'), info: enc.encode(slot) },
  await crypto.subtle.importKey('raw', key, 'HKDF', false, ['deriveKey']), { name: 'AES-GCM', length: 256 }, false, ['encrypt', 'decrypt']);

// tweak(framed) lets a vector break the padding or length prefix; a real writer never does.
export async function seal(json, { key, generation, slot, nonce, tweak }) {
  const body = enc.encode(json);
  const framed = new Uint8Array(BUCKETS.find((b) => b >= body.length + 4) ?? fail('too-large'));
  new DataView(framed.buffer).setUint32(0, body.length);
  framed.set(body, 4);
  tweak?.(framed);
  const header = Uint8Array.from([0x41, 0x42, 0x53, 0x31, KINDS[slot.split('/')[2]], 1, generation >> 8, generation & 255]);
  const ct = await crypto.subtle.encrypt({ name: 'AES-GCM', iv: nonce, additionalData: header }, await slotKey(key, slot), framed);
  return Uint8Array.from([...header, ...nonce, ...new Uint8Array(ct)]);
}

export async function open(sealed, { keys, slot }) {
  if (sealed.length > MAX_SEALED) { fail('too-large'); }
  if (sealed.length < HEADER + TAG || hex(sealed.slice(0, 4)) !== '41425331') { fail('bad-magic'); }
  if (sealed[5] > 1) { fail('unsupported-version'); }
  if (sealed[5] !== 1 || sealed[4] !== KINDS[slot.split('/')[2]]) { fail('wrong-kind'); }
  const generation = (sealed[6] << 8) | sealed[7];
  if (!keys[generation]) { fail('locked'); }
  if (!BUCKETS.includes(sealed.length - HEADER - TAG)) { fail('bad-length'); }
  let framed;
  try {
    framed = new Uint8Array(await crypto.subtle.decrypt({ name: 'AES-GCM', iv: sealed.slice(8, 20), additionalData: sealed.slice(0, 8) },
      await slotKey(unhex(keys[generation]), slot), sealed.slice(HEADER)));
  } catch { fail('bad-seal'); }
  const length = new DataView(framed.buffer).getUint32(0);
  if (length > framed.length - 4) { fail('bad-length'); }
  if (framed.slice(4 + length).some((b) => b !== 0)) { fail('bad-padding'); }
  try { return new TextDecoder('utf-8', { fatal: true }).decode(framed.slice(4, 4 + length)); } catch { return fail('bad-schema'); }
}

// ---- The cases. This is the generator; the JSON file is what is replayed.
const ACCOUNT = '4815162342';
const [A, B] = ['a1'.repeat(16), 'b2'.repeat(16)];
const reg = (value, counter, deviceTag) => ({ value, counter, deviceTag });
const tomb = (counter, deviceTag) => ({ tombstone: true, counter, deviceTag });
const slotOf = (kind, id) => `records/${ACCOUNT}/${kind}/${id}`;
const CTX = { key: unhex('00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff'), generation: 3, nonce: unhex('000102030405060708090a0b') };
const ORDER_SLOT = slotOf('order', 'your-order');
const ORDER = {
  rows: { 'host1\nsess-pin': reg({ key: 'a', pinned: true }, 4, A), 'host1\nsess-one': reg({ key: 'm', pinned: false }, 5, B), 'host2\nsess-two': reg({ key: 'n', pinned: false }, 5, A), 'host2\nsess-gone': tomb(6, B) },
  membership: { 'host1\nsess-pin': reg('g-work', 7, A), 'host2\nsess-two': reg('g-dead', 3, B), 'host9\nsess-away': reg(null, 2, A), 'host2\nsess-gone': tomb(8, B) },
  groups: { 'g-work': reg('Work', 6, A), 'g-dead': tomb(9, B), 'g-cafe': reg('Café ☕', 1, B) },
};
const MARKERS = { 'host1\nsess-one': 12, 'host2\nsess-two': 0 };
const groupCase = (name, aGroup, bGroup) => {
  const base = { rows: { 'h\ns': reg({ key: 'a', pinned: false }, 1, A) }, membership: { 'h\ns': reg('g1', 2, A) }, groups: { g1: reg('Work', 3, A) } };
  const [a, b] = [structuredClone(base), structuredClone(base)];
  a.groups.g1 = aGroup;
  b.groups.g1 = bGroup;
  return { name, a, b, expected: encodeOrder(mergeOrder(a, b)) };
};
const MUTATIONS = {
  'one flipped tag bit': ['bad-seal', (s) => { s[s.length - 1] ^= 1; return s; }],
  'one flipped ciphertext bit': ['bad-seal', (s) => { s[HEADER + 5] ^= 1; return s; }],
  'the magic is not ABS1': ['bad-magic', (s) => { s[3] = 0x32; return s; }],
  'a newer schema byte': ['unsupported-version', (s) => { s[5] = 2; return s; }],
  'the kind byte says marker in an order slot': ['wrong-kind', (s) => { s[4] = 2; return s; }],
  'a generation this device holds no key for': ['locked', (s) => { s[7] = 9; return s; }],
  'a ciphertext one byte short': ['bad-length', (s) => s.slice(0, -1)],
  'more than 64 KiB': ['too-large', () => new Uint8Array(MAX_SEALED + 1).fill(0x41)],
};
const G = (...a) => `{"v":1,"d":["${A}"],"g":[["g1",${a.join(',')}]],"r":{}}`;
const PLAINTEXT = {
  'trailing padding that is not zero': ['bad-padding', undefined, (f) => { f[f.length - 1] = 1; }],
  'a length prefix past the padded body': ['bad-length', undefined, (f) => new DataView(f.buffer).setUint32(0, f.length - 3)],
  'invalid JSON': ['bad-schema', '{"v":1,'],
  'a device tag that is not 32 lowercase hex': ['bad-schema', '{"v":1,"d":["AA"],"g":[],"r":{}}'],
  'a register naming a device past the table': ['bad-schema', G(1, 1, '"x"')],
  'a negative counter': ['bad-schema', G(-1, 0, '"x"')],
  'a fractional counter': ['bad-schema', G(1.5, 0, '"x"')],
  'a counter past 2^53-1': ['bad-schema', G(9007199254740992, 0, '"x"')],
  'a group name of 41 characters': ['bad-schema', G(1, 0, `"${'x'.repeat(41)}"`)],
  'a blank group name': ['bad-schema', G(1, 0, '" "')],
  'an entry with neither position nor membership': ['bad-schema', '{"v":1,"d":[],"g":[],"r":{"h\\ns":{}}}'],
  'a fractional key outside 0-9A-Za-z': ['bad-schema', `{"v":1,"d":["${A}"],"g":[],"r":{"h\\ns":{"p":[1,0,"a-b",0]}}}`],
  'a schema 2 body that is otherwise valid': ['unsupported-version', '{"v":2,"d":[],"g":[],"r":{}}'],
};

// The worst cases the design budgets (section 4): real-length keys (16-hex host id, newline, 36-character
// session id), every row grouped by a UUID-length id, 32 groups of 40 characters.
const rowKey = (i) => `${(i % 7).toString(16).repeat(16)}\n${i.toString(16).padStart(8, '0')}-0000-4000-8000-000000000000`;
const worstOrder = () => {
  const o = { rows: {}, membership: {}, groups: {} };
  for (let g = 0; g < MAX_GROUPS; g++) { o.groups[`${g.toString(16).padStart(8, '0')}-0000-4000-8000-000000000000`] = reg('G'.repeat(40), 1000 + g, A); }
  const ids = Object.keys(o.groups);
  for (let i = 0; i < MAX_ROWS; i++) {
    o.rows[rowKey(i)] = reg({ key: i.toString(36).padStart(8, 'k'), pinned: i % 9 === 0 }, 100000 + i, i % 2 ? A : B);
    o.membership[rowKey(i)] = reg(ids[i % MAX_GROUPS], 100000 + i, i % 2 ? B : A);
  }
  return o;
};
const worstMarkers = () => Object.fromEntries(Array.from({ length: MAX_MARKERS }, (_, i) => [rowKey(i), 1000000 + i]));

async function build() {
  const sealed = await seal(encodeOrder(ORDER), { ...CTX, slot: ORDER_SLOT });
  const refused = Object.entries(MUTATIONS).map(([name, [code, mutate]]) => ({ name, slot: ORDER_SLOT, code, sealed: hex(mutate(Uint8Array.from(sealed))) }));
  refused.push({ name: 'a record moved to another order id', slot: slotOf('order', 'other'), code: 'bad-seal', sealed: hex(sealed) });
  refused.push({ name: 'a record moved to another account', slot: 'records/1/order/your-order', code: 'bad-seal', sealed: hex(sealed) });
  for (const [name, [code, text, tweak]] of Object.entries(PLAINTEXT)) {
    refused.push({ name, slot: ORDER_SLOT, code, sealed: hex(await seal(text ?? encodeOrder(ORDER), { ...CTX, slot: ORDER_SLOT, tweak })) });
  }
  const marker = { name: 'a marker record', slot: slotOf('marker', 'markers'), kind: 'marker', record: MARKERS, canonical: encodeMarkers(MARKERS), paddedBytes: 1024 };
  const order = { name: 'an order with pins, a group, a tombstoned group, a deleted row and a membership with no row', slot: ORDER_SLOT, kind: 'order', record: ORDER, canonical: encodeOrder(ORDER), paddedBytes: 1024 };
  for (const v of [order, marker]) { v.sealed = hex(await seal(v.canonical, { ...CTX, slot: v.slot })); }
  return {
    about: 'Sealed roaming records (#759 S2). One fixed key and nonce so every byte is reproducible; a real writer draws a fresh random nonce per seal. Replayed by pwa/test/unit/roaming-records.test.mjs.',
    account: ACCOUNT, generation: CTX.generation, key: hex(CTX.key), nonce: hex(CTX.nonce), valid: [order, marker],
    converge: [
      groupCase('rename against delete of a group: the delete is newer, so the session falls back to Sessions', reg('Work 2', 4, A), tomb(5, B)),
      groupCase('rename against delete of a group: the rename is newer, so the group stays', reg('Work 2', 6, A), tomb(5, B)),
    ],
    refused,
    sizes: { order500Rows32GroupsJsonBytes: enc.encode(encodeOrder(worstOrder())).length, markers800JsonBytes: enc.encode(encodeMarkers(worstMarkers())).length },
  };
}

if (process.env.UPDATE_PROTOCOL_VECTORS === '1') {
  const v = await build();
  const body = Object.entries(v).map(([k, x]) => `  ${JSON.stringify(k)}: ${Array.isArray(x) ? `[\n${x.map((e) => `    ${JSON.stringify(e)}`).join(',\n')}\n  ]` : JSON.stringify(x)}`);
  writeFileSync(FILE, `{\n${body.join(',\n')}\n}\n`);
}

const vectors = JSON.parse(readFileSync(FILE, 'utf8'));
const ctx = { key: unhex(vectors.key), generation: vectors.generation, nonce: unhex(vectors.nonce) };
const opening = { keys: { [vectors.generation]: vectors.key } };

for (const v of vectors.valid) {
  test(`valid record: ${v.name}`, async () => {
    const [encode, decode] = v.kind === 'order' ? [encodeOrder, decodeOrder] : [encodeMarkers, decodeMarkers];
    assert.equal(encode(v.record), v.canonical, 'the canonical JSON is byte-exact');
    const sealed = await seal(v.canonical, { ...ctx, slot: v.slot });
    assert.equal(hex(sealed), v.sealed, 'the sealed bytes are byte-exact');
    assert.equal(sealed.length, HEADER + v.paddedBytes + TAG);
    const text = await open(unhex(v.sealed), { ...opening, slot: v.slot });
    assert.equal(text, v.canonical);
    assert.deepEqual(plain(decode(text)), plain(v.record));
    assert.equal(encode(decode(text)), v.canonical, 'decode then encode returns the same bytes');
  });
}

for (const c of vectors.converge) {
  test(`groups through sealed records: ${c.name}`, async () => {
    const seen = [];
    for (const side of [c.a, c.b]) { seen.push(decodeOrder(await open(await seal(encodeOrder(side), { ...ctx, slot: ORDER_SLOT }), { ...opening, slot: ORDER_SLOT }))); }
    assert.equal(encodeOrder(mergeOrder(seen[0], seen[1])), c.expected);
    assert.equal(encodeOrder(mergeOrder(seen[1], seen[0])), c.expected, 'the same in either order');
  });
}

for (const r of vectors.refused) {
  test(`refused: ${r.name}`, async () => {
    assert.equal(await refusal(async () => decodeOrder(await open(unhex(r.sealed), { ...opening, slot: r.slot }))), r.code);
  });
}

test('an object at the design limits seals inside 64 KiB, measured, not estimated', async () => {
  const [order, markers] = [encodeOrder(worstOrder()), encodeMarkers(worstMarkers())];
  assert.equal(enc.encode(order).length, vectors.sizes.order500Rows32GroupsJsonBytes);
  assert.equal(enc.encode(markers).length, vectors.sizes.markers800JsonBytes);
  assert.equal((await seal(order, { ...ctx, slot: ORDER_SLOT })).length, MAX_SEALED);
  assert.equal((await seal(markers, { ...ctx, slot: slotOf('marker', 'markers') })).length, MAX_SEALED);
  const over = worstOrder();
  over.rows['h\nextra'] = reg({ key: 'z', pinned: false }, 1, A);
  assert.equal(await refusal(() => decodeOrder(encodeOrder(over))), 'bad-schema', '501 live rows are refused');
});
