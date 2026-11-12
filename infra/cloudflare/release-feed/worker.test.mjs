// Run: node infra/cloudflare/release-feed/worker.test.mjs
import assert from 'node:assert/strict';
import { test } from 'node:test';
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
globalThis.fetch = async (u, options) => {
  calls.push({ url: u, headers: new Headers(options.headers) });
  const next = upstream.shift();
  if (next instanceof Error) throw next;
  if (next.bodyError) return { status: 200, text: async () => { throw new Error('stream reset'); } };
  return new Response(next.body, { status: next.status, headers: next.headers });
};
const get = async (path, headers = {}, env = {}) => {
  const res = await worker.fetch(new Request(`https://releases.slipgates.com${path}`, { headers }), env, ctx);
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

function reset(t) {
  store.clear();
  calls = [];
  upstream = [];
  let now = 1800000000000;
  t.mock.method(Date, 'now', () => now);
  return (seconds) => { now += seconds * 1000; };
}

test('a current list answers matching conditional requests without a body or another GitHub call', async (t) => {
  reset(t);
  upstream = [ok('[{"tag_name":"v0.1.1"}]')];
  const first = await get(P);
  const etag = first.headers.get('etag');
  assert.ok(etag, 'the list has an ETag');
  for (const match of [etag, `W/${etag}`, `"other", W/${etag}`, '*']) {
    const response = await get(P, { 'if-none-match': match });
    assert.equal(response.status, 304, match);
    assert.equal(await response.text(), '');
    assert.equal(response.headers.get('etag'), etag);
    assert.equal(response.headers.get('age'), '0');
    assert.equal(response.headers.get('cache-control'), 'no-cache');
  }
  const miss = await get(P, { 'if-none-match': '"other"' });
  assert.equal(miss.status, 200);
  assert.equal(await miss.text(), '[{"tag_name":"v0.1.1"}]');
  assert.equal(calls.length, 1);
});

test('changed lists get a new validator and stale conditional answers keep their real Age', async (t) => {
  const advance = reset(t);
  upstream = [ok('[{"tag_name":"v0.1.1"}]')];
  const first = await get(P);
  const oldTag = first.headers.get('etag');
  advance(300);
  upstream = [ok('[{"tag_name":"v0.1.2"}]')];
  const changed = await get(P, { 'if-none-match': oldTag });
  assert.equal(changed.status, 200);
  assert.notEqual(changed.headers.get('etag'), oldTag);
  assert.equal(await changed.text(), '[{"tag_name":"v0.1.2"}]');
  advance(3601);
  upstream = [{ status: 403, body: '{}' }];
  const stale = await get(P, { 'if-none-match': changed.headers.get('etag') });
  assert.equal(stale.status, 304);
  assert.equal(await stale.text(), '');
  assert.equal(stale.headers.get('etag'), changed.headers.get('etag'));
  assert.equal(stale.headers.get('age'), '3601', 'clients can still reject a list over one hour old');
});

test('a changed GitHub list replaces an expired copy that holds an upstream ETag', async (t) => {
  const advance = reset(t);
  upstream = [{ ...ok('[{"tag_name":"v0.1.1"}]'), headers: { etag: '"github-old"' } }];
  const first = await get(P);
  const oldTag = first.headers.get('etag');
  advance(300);
  upstream = [{ ...ok('[{"tag_name":"v0.1.2"}]'), headers: { etag: '"github-new"' } }];
  const changed = await get(P, { 'if-none-match': oldTag });
  assert.equal(calls[1].headers.get('if-none-match'), '"github-old"');
  assert.equal(changed.status, 200);
  assert.equal(await changed.text(), '[{"tag_name":"v0.1.2"}]');
  assert.notEqual(changed.headers.get('etag'), oldTag);
  assert.equal(changed.headers.get('age'), '0');
  advance(1);
  const hit = await get(P);
  assert.equal(await hit.text(), '[{"tag_name":"v0.1.2"}]');
  assert.equal(hit.headers.get('etag'), changed.headers.get('etag'));
  assert.equal(hit.headers.get('age'), '1');
  assert.equal(calls.length, 2, 'the replacement is cached');
  advance(299);
  upstream = [{ status: 304, body: null }];
  const validated = await get(P, { 'if-none-match': changed.headers.get('etag') });
  assert.equal(calls[2].headers.get('if-none-match'), '"github-new"');
  assert.equal(validated.status, 304, 'the replacement upstream ETag is cached too');
});

for (const [label, failure] of [
  ['403', { status: 403, body: '{}' }],
  ['500', { status: 500, body: 'unavailable' }],
  ['network error', new Error('network')],
]) {
  test(`a ${label} with an upstream ETag keeps the stale list and its growing Age`, async (t) => {
    const advance = reset(t);
    const body = '[{"tag_name":"v0.1.1"}]';
    upstream = [{ ...ok(body), headers: { etag: '"github-list"' } }];
    const first = await get(P);
    const etag = first.headers.get('etag');
    advance(300);
    for (const [age, headers, status] of [[300, {}, 200], [3601, { 'if-none-match': etag }, 304]]) {
      upstream = [failure];
      const stale = await get(P, headers);
      assert.equal(calls.at(-1).headers.get('if-none-match'), '"github-list"');
      assert.equal(stale.status, status);
      assert.equal(await stale.text(), status === 200 ? body : '');
      assert.equal(stale.headers.get('etag'), etag);
      assert.equal(stale.headers.get('age'), String(age));
      advance(3301);
    }
    assert.equal(calls.length, 3, 'failed refreshes do not renew freshness');
  });
}

test('client Authorization is not forwarded and secrets and upstream validators stay out of responses', async (t) => {
  reset(t);
  upstream = [
    { ...ok('[]'), headers: { etag: '"github-one"' } },
    { ...ok('[]'), headers: { etag: '"github-two"' } },
  ];
  const anonymous = await get(P, { authorization: 'Bearer untrusted-client-token' });
  assert.equal(calls[0].headers.get('authorization'), null);
  const authenticated = await get(`${P}?page=2`, { authorization: 'Bearer untrusted-client-token' }, { GITHUB_TOKEN: 'worker-only-token' });
  assert.equal(calls[1].headers.get('authorization'), 'Bearer worker-only-token');
  for (const response of [anonymous, authenticated]) {
    assert.equal(response.headers.get('authorization'), null);
    assert.equal(response.headers.get('x-upstream-etag'), null);
    assert.doesNotMatch([...response.headers.values()].join('\n'), /untrusted-client-token|worker-only-token|github-(one|two)/);
  }
});

for (const [label, env, seconds] of [
  ['without a token', {}, 300],
  ['with an empty token', { GITHUB_TOKEN: '' }, 300],
  ['with a token secret', { GITHUB_TOKEN: 'test-read-only-token' }, 60],
]) {
  test(`freshness is ${seconds} seconds ${label} and upstream revalidation preserves the list`, async (t) => {
    const advance = reset(t);
    upstream = [{ ...ok('[{"tag_name":"v0.1.1"}]'), headers: { etag: '"github-list"' } }];
    const first = await get(P, {}, env);
    const etag = first.headers.get('etag');
    assert.equal(calls[0].headers.get('authorization'), env.GITHUB_TOKEN ? `Bearer ${env.GITHUB_TOKEN}` : null);
    advance(seconds - 1);
    const hit = await get(P, {}, env);
    assert.equal(calls.length, 1, 'fresh list does not call GitHub');
    assert.equal(hit.headers.get('age'), String(seconds - 1));
    advance(1);
    upstream = [{ status: 304, body: null }];
    const validated = await get(P, { 'if-none-match': etag }, env);
    assert.equal(calls.length, 2, 'expired list is revalidated');
    assert.equal(calls[1].headers.get('if-none-match'), '"github-list"');
    assert.equal(validated.status, 304);
    assert.equal(validated.headers.get('age'), '0', 'GitHub revalidated freshness');
    assert.equal(validated.headers.get('etag'), etag);
    const full = await get(P, {}, env);
    assert.equal(await full.text(), '[{"tag_name":"v0.1.1"}]');
    assert.equal(calls.length, 2, 'revalidation is cached');
  });
}
