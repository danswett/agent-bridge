import test, { mock } from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { handle, SessionMailbox } from './worker.mjs';
import { mailboxBase, phoneOrigin, postEvidence } from '../return.mjs';

test.before(() => mock.method(globalThis, 'fetch', () => { throw new Error('Real network is forbidden in mailbox tests.'); }));
test.after(() => mock.restoreAll());
const id = 'ab'.repeat(16);
function fixture() {
  let state, clock = 0, queue = Promise.resolve(), alarms = [], alarm = null, clears = 0;
  const storage = {
    get: async () => structuredClone(state), put: async (_, v) => { state = structuredClone(v); },
    deleteAll: async () => { state = undefined; clears++; }, deleteAlarm: async () => { alarm = null; },
    setAlarm: async v => { alarms.push(v); alarm = v; },
    transaction(fn) { const work = queue.then(() => fn(storage)); queue = work.then(() => {}, () => {}); return work; },
  };
  const actor = new SessionMailbox({ storage, blockConcurrencyWhile: fn => storage.transaction(fn) }, { now: () => clock });
  const actors = new Map([[id, actor]]);
  return { env: { MAILBOX: { idFromName: name => name, get: name => { if (!actors.has(name)) actors.set(name, fixture().actor); return actors.get(name); } } }, actor, alarms, peek: () => structuredClone(state), alarm: () => alarm, clears: () => clears, setTime: v => { clock = v; } };
}
const request = (method = 'GET', body, headers = {}, suffix = id) => new Request(`${mailboxBase}/s/${suffix}`, { method, headers: { ...(method === 'POST' ? { Origin: phoneOrigin, 'Content-Type': 'application/json', 'Idempotency-Key': createHash('sha256').update(body ?? '').digest('hex').slice(0, 32) } : {}), ...headers }, ...(body === undefined ? {} : { body }) });
test('mailbox validates id and rejects unknown routes or methods', async () => {
  const { env } = fixture();
  assert.equal((await handle(request('GET', undefined, {}, 'invalid'), env)).status, 400);
  assert.equal((await handle(new Request(mailboxBase + '/other'), env)).status, 404);
  assert.equal((await handle(request('GET', undefined, {}, id + '/extra'), env)).status, 404);
  assert.equal((await handle(request('GET', undefined, {}, id.toUpperCase()), env)).status, 400);
  assert.equal((await handle(request('DELETE'), env)).status, 404);
});
test('oversize declared and actual POST bodies refuse with 413', async () => {
  const { env } = fixture();
  assert.equal((await handle(request('POST', '{}', { 'Content-Length': '4097' }), env)).status, 413);
  assert.equal((await handle(request('POST', '"' + 'x'.repeat(4096) + '"'), env)).status, 413);
  assert.equal((await handle(request('POST', '{}', { 'Content-Type': 'text/plain' }), env)).status, 415);
  const invalid = new Request(`${mailboxBase}/s/${id}`, { method: 'POST', headers: { Origin: phoneOrigin, 'Content-Type': 'application/json', 'Idempotency-Key': id }, body: new Uint8Array([255]) });
  assert.equal((await handle(invalid, env)).status, 400);
});
test('foreign POST and foreign-origin GET refuse but native GET is permitted', async () => {
  const { env } = fixture();
  assert.equal((await handle(request('POST', '{}', { Origin: 'https://wrong.example' }), env)).status, 403);
  assert.equal((await handle(request('GET', undefined, { Origin: 'https://wrong.example' }), env)).status, 403);
  assert.equal((await handle(request(), env)).status, 404);
});
test('three racing posts append atomically in order, fourth refuses and nothing overwrites', async () => {
  const { env } = fixture();
  const responses = await Promise.all([0, 1, 2, 3].map(n => handle(request('POST', JSON.stringify({ n })), env)));
  assert.deepEqual(responses.map(r => r.status), [201, 201, 201, 409]);
  const response = await handle(request(), env);
  assert.deepEqual((await response.json()).blobs.map(JSON.parse), [{ n: 0 }, { n: 1 }, { n: 2 }]);
  assert.equal(response.headers.get('Cache-Control'), 'no-store');
  assert.equal((await handle(request(), env)).status, 200);
});
test('five-minute timestamp and alarm expire all posts without extending on retries', async () => {
  const f = fixture();
  await handle(request('POST', '{}'), f.env); f.setTime(100000);
  await handle(request('POST', '{"second":true}'), f.env);
  assert.deepEqual(f.alarms, [300000, 300000]);
  f.setTime(300000); assert.equal((await handle(request(), f.env)).status, 404);
  assert.equal((await handle(request('POST', '{"retry":true}'), f.env)).status, 404);
  await f.actor.alarm(); assert.equal((await handle(request(), f.env)).status, 404);
  assert.deepEqual(f.peek(), { expiry: 300000, discard: 3600000 });
  assert.equal((await handle(request('POST', '{"retry":true}'), f.env)).status, 404);
  assert.equal(f.alarm(), 3600000); f.setTime(3600000); await f.actor.alarm();
  assert.equal(f.peek(), undefined); assert.equal(f.alarm(), null); assert.equal(f.clears(), 1);
});
test('idempotent racing retries store once; conflicting bytes refuse and full-slot retry still succeeds', async () => {
  const f = fixture(), headers = { 'Idempotency-Key': id };
  assert.deepEqual((await Promise.all([1, 2].map(() => handle(request('POST', '{}', headers), f.env)))).map(r => r.status), [201, 201]);
  assert.equal((await handle(request('POST', '{"changed":true}', headers), f.env)).status, 409);
  await handle(request('POST', '{"n":1}'), f.env); await handle(request('POST', '{"n":2}'), f.env);
  assert.equal((await handle(request('POST', '{}', headers), f.env)).status, 201);
  assert.deepEqual((await (await handle(request(), f.env)).json()).blobs, ['{}', '{"n":1}', '{"n":2}']);
  for (const key of ['', 'wrong', id.toUpperCase()]) assert.equal((await handle(request('POST', '{}', { 'Idempotency-Key': key }), f.env)).status, 400);
});
test('late requests clear all retired state and an early alarm never extends the retention bound', async () => {
  const f = fixture(); await handle(request('POST', '{}'), f.env);
  f.setTime(100000); await f.actor.alarm(); assert.equal(f.alarm(), 300000);
  f.setTime(3600000); assert.equal((await handle(request(), f.env)).status, 404);
  assert.equal(f.peek(), undefined); assert.equal(f.alarm(), null); assert.equal(f.clears(), 1);
});
test('different session ids have isolated append limits and payloads', async () => {
  const { env } = fixture(), other = 'cd'.repeat(16);
  await handle(request('POST', '{"first":true}'), env);
  await handle(request('POST', '{"second":true}', {}, other), env);
  assert.deepEqual((await (await handle(request(), env)).json()).blobs, ['{"first":true}']);
  assert.deepEqual((await (await handle(request('GET', undefined, {}, other), env)).json()).blobs, ['{"second":true}']);
});
test('OPTIONS and POST CORS headers are exact and never wildcard', async () => {
  const { env } = fixture(), response = await handle(request('OPTIONS', undefined, { Origin: phoneOrigin }), env);
  assert.equal(response.headers.get('Access-Control-Allow-Origin'), phoneOrigin);
  assert.equal(response.headers.get('Access-Control-Allow-Methods'), 'POST');
  assert.equal(response.headers.get('Access-Control-Allow-Headers'), 'Content-Type, Idempotency-Key');
  assert.equal(response.headers.get('Access-Control-Max-Age'), '600');
  assert.equal((await handle(request('POST', '{}'), env)).headers.get('Access-Control-Allow-Origin'), phoneOrigin);
  assert.equal((await handle(request('OPTIONS'), env)).status, 403);
});
test('worker never logs a request body or session id', async () => {
  const original = Object.fromEntries(['log', 'info', 'warn', 'error', 'debug'].map(k => [k, console[k]])), calls = [];
  for (const k of Object.keys(original)) console[k] = (...args) => calls.push(args);
  try { const { env } = fixture(); await handle(request('POST', '{"private":"marker"}'), env); await handle(request(), env); assert.deepEqual(calls, []); }
  finally { Object.assign(console, original); }
});
test('phone automatically returns to fixed host and retries once only on network errors', async () => {
  const calls = [], body = { opaque: 'test' }, f = fixture();
  await postEvidence(id, body, { fetch: async (url, options) => { calls.push([url, options]); const response = await handle(new Request(url, { ...options, headers: { ...options.headers, Origin: phoneOrigin } }), f.env); if (calls.length === 1) throw new DOMException('network', 'NetworkError'); return response; } });
  assert.equal(calls.length, 2); assert.equal(calls[0][0], `${mailboxBase}/s/${id}`);
  assert.equal(calls[0][1].body, calls[1][1].body); assert.equal(calls[0][1].redirect, 'error');
  assert.equal(calls[0][1].headers['Idempotency-Key'], calls[1][1].headers['Idempotency-Key']);
  assert.deepEqual((await (await handle(request(), f.env)).json()).blobs, [JSON.stringify(body)]);
  let count = 0;
  await assert.rejects(postEvidence(id, body, { fetch: async () => { count++; return new Response('', { status: 409 }); } }), /409/);
  assert.equal(count, 1);
  const local = { fetch: async () => { count++; return new Response('', { status: 201 }); } };
  await assert.rejects(postEvidence([id], body, local), /invalid mailbox/);
  await assert.rejects(postEvidence(id, { oversized: 'x'.repeat(4096) }, local), /4096/);
  assert.equal(count, 1);
});
test('ambiguous TypeError, redirect failures and cancellation are never retried', async () => {
  for (const error of [new TypeError('network'), new TypeError('redirect'), new DOMException('closed', 'AbortError')]) {
    let count = 0; await assert.rejects(postEvidence(id, {}, { fetch: async () => { count++; throw error; } }));
    assert.equal(count, 1);
  }
});
