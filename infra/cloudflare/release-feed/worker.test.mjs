// Run: node infra/cloudflare/release-feed/worker.test.mjs
import assert from 'node:assert/strict';
import worker from './worker.js';

const store = new Map();
globalThis.caches = {
  default: {
    async match(req) { const r = store.get(req.url); return r ? r.clone() : undefined; },
    async put(req, res) { store.set(req.url, res); },
  },
};
const pending = [];
const ctx = { waitUntil: (p) => pending.push(p) };
let upstream = [];
let calls = [];
globalThis.fetch = async (u) => {
  calls.push(u);
  const next = upstream.shift();
  if (next instanceof Error) throw next;
  if (next.bodyError) return { status: 200, text: async () => { throw new Error('stream reset'); } };
  return new Response(next.body, { status: next.status });
};
const get = async (path) => {
  const res = await worker.fetch(new Request(`https://releases.slipgates.com${path}`), {}, ctx);
  await Promise.all(pending.splice(0));
  return res;
};
const P = '/repos/danswett/agent-bridge/releases';
const ok = (body) => ({ status: 200, body });

upstream = [ok('[{"tag_name":"a"}]')];
let r = await get(`${P}?per_page=10&page=1`);
assert.equal(r.status, 200);
assert.equal(r.headers.get('age'), '0');

r = await get(`${P}?per_page=10&page=1`);
assert.equal(calls.length, 1, 'fresh hit does not call GitHub');
assert.ok(r.headers.has('age'));

upstream = [ok('[{"tag_name":"p2"}]')];
r = await get(`${P}?per_page=10&page=2`);
assert.equal(await r.text(), '[{"tag_name":"p2"}]', 'page is part of the key');
upstream = [ok('[]')];
r = await get(`${P}?per_page=5&page=1`);
assert.equal(calls.length, 3, 'per_page is part of the key');

// age the first entry, then fail the refresh: stale copy with its real age
const key = [...store.keys()].find((k) => k.includes('per_page=10&page=1'));
const old = store.get(key);
store.set(key, new Response(await old.text(), { headers: { 'x-fetched-at': String(Date.now() - 600000) } }));
upstream = [{ status: 403, body: '{}' }];
r = await get(`${P}?per_page=10&page=1`);
assert.equal(r.status, 200);
assert.ok(Number(r.headers.get('age')) >= 600);

// no cached copy and a failing refresh: an error, never an empty success
upstream = [{ status: 500, body: 'x' }];
r = await get(`${P}?per_page=7&page=1`);
assert.equal(r.status, 502);
upstream = [new Error('network')];
r = await get(`${P}?per_page=8&page=1`);
assert.equal(r.status, 502);
upstream = [ok('{"message":"rate limit"}')];
r = await get(`${P}?per_page=9&page=1`);
assert.equal(r.status, 502, 'non-array 200 is not a list');

// a body read that rejects must take the same fallbacks as a failed fetch
upstream = [{ bodyError: true }];
r = await get(`${P}?per_page=11&page=1`);
assert.equal(r.status, 502, 'body read failure with no copy is an error');
upstream = [{ bodyError: true }];
r = await get(`${P}?per_page=10&page=1`);
assert.equal(r.status, 200, 'body read failure serves the stale copy');
assert.ok(Number(r.headers.get('age')) >= 600);

assert.equal((await get('/other')).status, 404);
assert.equal((await get(`${P}?per_page=abc`)).status, 400);
assert.equal((await get(`${P}?per_page=101`)).status, 400);
assert.equal((await worker.fetch(new Request(`https://x${P}`, { method: 'POST' }), {}, ctx)).status, 405);
console.log('ok');
