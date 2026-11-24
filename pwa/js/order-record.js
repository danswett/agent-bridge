// The sealed order record's plaintext (docs/protocol/order-records.md, #759 S2): what a reader accepts,
// the compact form's exact expansion to the merge form of order-merge.js and back, and what leaves a
// record that is over its size. Pure: no storage, no network, no key, no clock. A record arrives from
// another device, so anything out of shape is refused whole (never read in part) with a code.
const LF = String.fromCharCode(10);

export const LIMITS = Object.freeze({ bytes: 65536, overhead: 106, rows: 500, memberships: 500, groups: 32, devices: 64, hosts: 64, tombstoneDays: 30, maxDay: 100000 });

export class OrderRecordError extends Error {
  constructor(code) { super(code); this.name = 'OrderRecordError'; this.code = code; }
}
const refuse = (code) => { throw new OrderRecordError(code); };

// Canonical JSON: no whitespace, object keys in UTF-16 code unit order at every level.
export function canonical(v) {
  if (Array.isArray(v)) { return `[${v.map(canonical).join(',')}]`; }
  if (v !== null && typeof v === 'object') { return `{${Object.keys(v).sort().map((k) => `${JSON.stringify(k)}:${canonical(v[k])}`).join(',')}}`; }
  return JSON.stringify(v);
}

const isObject = (x) => x !== null && typeof x === 'object' && !Array.isArray(x);
const int = (x) => Number.isSafeInteger(x) && x >= 0;
// A control character (U+0000-001F, 007F-009F) or a lone surrogate: neither is in any string of a record.
const badText = (s) => [...s].some((c) => { const p = c.codePointAt(0); return p < 32 || (p >= 127 && p < 160) || (p >= 0xd800 && p <= 0xdfff); });
// JavaScript's trim set, which the doc spells out (§3): whether either end of a string is one of those characters.
const SPACE = new Set([9, 10, 11, 12, 13, 32, 0xa0, 0x1680, 0x2028, 0x2029, 0x202f, 0x205f, 0x3000, 0xfeff]);
const isSpace = (c) => SPACE.has(c) || (c >= 0x2000 && c <= 0x200a);
const untrimmed = (s) => isSpace(s.charCodeAt(0)) || isSpace(s.charCodeAt(s.length - 1));
const HEX32 = /^[0-9a-f]{32}$/;
const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;
const FRACTION = /^[0-9a-z]{1,32}$/;
const bytesOf = (text) => new TextEncoder().encode(text).length;

function table(list, max, ok) {
  if (!Array.isArray(list)) { refuse('bad-shape'); }
  if (list.length > max) { refuse('limit'); }
  for (const e of list) { if (typeof e !== 'string' || !ok(e)) { refuse('range'); } }
  for (let i = 1; i < list.length; i += 1) { if (list[i - 1] >= list[i]) { refuse(list[i - 1] === list[i] ? 'duplicate' : 'unsorted'); } }
}

// A register of `length` fields: counter, device index, then the value fields.
function register(a, length, devices) {
  if (!Array.isArray(a) || a.length !== length) { refuse('bad-shape'); }
  if (!int(a[0]) || !int(a[1])) { refuse('range'); }
  if (a[1] >= devices) { refuse('index'); }
}

// The host index of a row key, which is that index, a line feed and the session id.
function rowHost(key, hosts) {
  const at = key.indexOf(LF);
  const host = key.slice(0, at);
  if (at < 1 || !/^(0|[1-9][0-9]*)$/.test(host)) { refuse('bad-key'); }
  const session = key.slice(at + 1);
  if (!session || [...session].length > 128 || badText(session)) { refuse('bad-key'); }
  if (Number(host) >= hosts) { refuse('index'); }
  return Number(host);
}

export function parseOrder(text) {
  if (typeof text !== 'string') { refuse('bad-json'); }
  if (bytesOf(text) > LIMITS.bytes - LIMITS.overhead) { refuse('too-large'); }
  let v;
  try { v = JSON.parse(text); } catch { refuse('bad-json'); }
  let again;
  try { again = canonical(v); } catch { refuse('not-canonical'); } // nested past what it can write
  if (again !== text) { refuse('not-canonical'); }
  if (!isObject(v)) { refuse('bad-shape'); }
  const fields = ['d', 'g', 'h', 'm', 'r', 'v', 'x'];
  if (Object.keys(v).some((k) => !fields.includes(k))) { refuse('unknown-field'); }
  if (fields.some((k) => !Object.hasOwn(v, k))) { refuse('missing-field'); }
  if (v.v !== 1) { refuse('bad-version'); }
  table(v.d, LIMITS.devices, (e) => HEX32.test(e));
  table(v.h, LIMITS.hosts, (e) => [...e].length <= 64 && !badText(e));
  if (![v.g, v.m, v.r, v.x].every(isObject)) { refuse('bad-shape'); }
  if (Object.keys(v.x).some((k) => !['g', 'm', 'r'].includes(k))) { refuse('unknown-field'); }
  if (!['g', 'm', 'r'].every((k) => Object.hasOwn(v.x, k))) { refuse('missing-field'); }
  if (!['g', 'm', 'r'].every((k) => isObject(v.x[k]))) { refuse('bad-shape'); }
  const devices = v.d.length;
  const usedDevice = new Set();
  const usedHost = new Set();
  const each = (map, length, check, rowKeyed) => {
    for (const [key, reg] of Object.entries(map)) {
      if (rowKeyed) { usedHost.add(rowHost(key, v.h.length)); } else if (!UUID.test(key)) { refuse('bad-key'); }
      register(reg, length, devices);
      usedDevice.add(reg[1]);
      check(reg);
    }
  };
  each(v.r, 4, (r) => { if (typeof r[2] !== 'string' || !FRACTION.test(r[2]) || (r[3] !== 0 && r[3] !== 1)) { refuse('range'); } }, true);
  each(v.m, 3, (r) => { if (r[2] !== null && !(typeof r[2] === 'string' && UUID.test(r[2]))) { refuse('range'); } }, true);
  each(v.g, 3, (r) => { if (typeof r[2] !== 'string' || ![...r[2]].length || [...r[2]].length > 40 || untrimmed(r[2]) || badText(r[2])) { refuse('range'); } }, false);
  const day = (r) => { if (!int(r[2]) || r[2] > LIMITS.maxDay) { refuse('range'); } };
  each(v.x.r, 3, day, true);
  each(v.x.m, 3, day, true);
  each(v.x.g, 3, day, false);
  for (const k of ['r', 'm', 'g']) { if (Object.keys(v[k]).some((key) => Object.hasOwn(v.x[k], key))) { refuse('both-maps'); } }
  if (Object.keys(v.r).length > LIMITS.rows || Object.keys(v.m).length > LIMITS.memberships || Object.keys(v.g).length > LIMITS.groups) { refuse('limit'); }
  if (usedDevice.size !== devices || usedHost.size !== v.h.length) { refuse('unused'); }
  return v;
}

// The merge form of order-merge.js: a row key is the host, a line feed and the session; a tombstone keeps its day.
export function toMergeForm(rec) {
  const real = (key) => { const at = key.indexOf(LF); return rec.h[Number(key.slice(0, at))] + LF + key.slice(at + 1); };
  const out = { rows: {}, membership: {}, groups: {} };
  const live = (map, into, rowKeyed, value) => {
    for (const [k, r] of Object.entries(map)) { into[rowKeyed ? real(k) : k] = { value: value(r), counter: r[0], deviceTag: rec.d[r[1]] }; }
  };
  const dead = (map, into, rowKeyed) => {
    for (const [k, r] of Object.entries(map)) { into[rowKeyed ? real(k) : k] = { tombstone: true, counter: r[0], deviceTag: rec.d[r[1]], day: r[2] }; }
  };
  live(rec.r, out.rows, true, (r) => ({ key: r[2], pinned: r[3] === 1 }));
  live(rec.m, out.membership, true, (r) => r[2]);
  live(rec.g, out.groups, false, (r) => r[2]);
  dead(rec.x.r, out.rows, true);
  dead(rec.x.m, out.membership, true);
  dead(rec.x.g, out.groups, false);
  return out;
}

// The compact form of a merge state: its devices and hosts in code unit order, so one state is one record.
export function fromMergeForm(merge, { nowDay }) {
  const maps = [['r', merge.rows, true], ['m', merge.membership, true], ['g', merge.groups, false]];
  const d = new Set();
  const h = new Set();
  for (const [, map, rowKeyed] of maps) {
    for (const [k, r] of Object.entries(map)) { d.add(r.deviceTag); if (rowKeyed) { h.add(k.slice(0, k.indexOf(LF))); } }
  }
  const rec = { d: [...d].sort(), g: {}, h: [...h].sort(), m: {}, r: {}, v: 1, x: { g: {}, m: {}, r: {} } };
  for (const [name, map, rowKeyed] of maps) {
    for (const [k, r] of Object.entries(map)) {
      const at = rowKeyed ? k.indexOf(LF) : 0;
      const key = rowKeyed ? `${rec.h.indexOf(k.slice(0, at))}${LF}${k.slice(at + 1)}` : k;
      const head = [r.counter, rec.d.indexOf(r.deviceTag)];
      if (r.tombstone === true) { rec.x[name][key] = [...head, r.day ?? nowDay]; }
      else if (name === 'r') { rec[name][key] = [...head, r.value.key, r.value.pinned === true ? 1 : 0]; }
      else { rec[name][key] = [...head, r.value ?? null]; }
    }
  }
  return rec;
}
