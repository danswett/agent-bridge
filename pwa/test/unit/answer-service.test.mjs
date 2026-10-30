import { test } from 'node:test';
import assert from 'node:assert/strict';
import { AnswerService } from '../../js/answer-service.js';
import { HostClient, HostError } from '../../js/host-client.js';

const flush = () => new Promise((r) => setImmediate(r));
const ANSWER = { fields: { answer: 'a' } };

// A HostClient stand-in: each answer() call waits for the test to settle it.
function fakeClient() {
  const c = {
    closed: false,
    calls: [],
    answer(data) {
      return new Promise((resolve, reject) => { c.calls.push({ data, resolve, reject }); });
    },
    last() { return c.calls[c.calls.length - 1]; },
    close() {
      c.closed = true;
      for (const call of c.calls) { call.reject(new HostError('session.answer', 'the channel closed')); }
    },
  };
  return c;
}

function setup(opts = {}) {
  let n = 0;
  const conn = { client: fakeClient(), hostId: 'h1' };
  const service = new AnswerService({ current: () => conn.client && { client: conn.client, hostId: conn.hostId }, uuid: () => `r${++n}`, retryMs: 5, ...opts });
  const seen = [];
  service.watch('h1', 's1', 'q1', (e) => seen.push(`${e.state}${e.outcome ? `:${e.outcome}` : ''}`));
  const submit = (answer = ANSWER) => service.submit({ hostId: 'h1', sessionId: 's1', questionId: 'q1', answer });
  return { conn, service, seen, submit };
}

test('an answer is sent once with a fresh request, and its outcome is kept', async () => {
  const { conn, service, seen, submit } = setup();
  submit();
  submit(); // a second tap while the first is on its way sends nothing
  assert.equal(conn.client.calls.length, 1);
  assert.deepEqual(conn.client.last().data, { id: 's1', question: 'q1', request: 'r1', fields: { answer: 'a' } });
  conn.client.last().resolve({ outcome: 'delivered', reason: null });
  await flush();
  assert.deepEqual(seen, ['sending', 'done:delivered']);
  assert.equal(service.get('h1', 's1', 'q1').outcome, 'delivered');
  // Delivered is final: answering again sends nothing.
  submit({ fields: { answer: 'b' } });
  assert.equal(conn.client.calls.length, 1);
});

test('a lost response is resent with the same request until an outcome arrives', async (t) => {
  // The resend waits on a timer, mocked here: with a real 5 ms one, a CI runner that came
  // back to this test more than 5 ms late found the resend already sending, not waiting.
  t.mock.timers.enable({ apis: ['setTimeout'] });
  const { conn, service, submit } = setup();
  submit();
  conn.client.last().reject(new HostError('session.answer', 'timed out'));
  await flush();
  assert.equal(service.get('h1', 's1', 'q1').state, 'waiting');
  assert.equal(conn.client.calls.length, 1);
  t.mock.timers.tick(5);
  assert.equal(conn.client.calls.length, 2);
  assert.equal(conn.client.calls[1].data.request, 'r1');
  conn.client.last().resolve({ outcome: 'delivered' });
  await flush();
  assert.equal(service.get('h1', 's1', 'q1').outcome, 'delivered');
});

test('after a reconnect the waiting answer goes out on the new channel with the same request', async () => {
  const { conn, service, submit } = setup();
  submit();
  const old = conn.client;
  conn.client = null;
  old.close();
  await flush();
  await new Promise((r) => setTimeout(r, 20));
  assert.equal(service.get('h1', 's1', 'q1').state, 'waiting');
  assert.equal(old.calls.length, 1, 'nothing is retried on a closed channel');
  conn.client = fakeClient();
  service.ready();
  assert.equal(conn.client.calls.length, 1);
  assert.equal(conn.client.last().data.request, 'r1');
  conn.client.last().resolve({ outcome: 'stale', reason: 'gone' });
  await flush();
  assert.deepEqual([service.get('h1', 's1', 'q1').outcome, service.get('h1', 's1', 'q1').reason], ['stale', 'gone']);
});

test('a reconnect that beats the old channel\'s failure resends at once', async () => {
  const { conn, submit } = setup();
  submit();
  const old = conn.client;
  conn.client = fakeClient();
  old.close();
  await flush();
  assert.equal(conn.client.calls.length, 1);
  assert.equal(conn.client.last().data.request, 'r1');
});

test('an answer for one machine is never sent to another', async () => {
  const { conn, service, submit } = setup();
  conn.client = null;
  submit();
  conn.client = fakeClient();
  conn.hostId = 'h2';
  service.ready();
  assert.equal(conn.client.calls.length, 0);
  assert.equal(service.get('h1', 's1', 'q1').state, 'waiting');
});

test('after busy or refused, answering again uses a new request', async () => {
  const { conn, service, submit } = setup();
  submit();
  conn.client.last().resolve({ outcome: 'busy', reason: 'another answer' });
  await flush();
  assert.equal(service.get('h1', 's1', 'q1').outcome, 'busy');
  submit();
  assert.equal(conn.client.last().data.request, 'r2');
  conn.client.last().resolve({ outcome: 'refused', reason: 'elevated' });
  await flush();
  submit({ fields: { answer: 'b' } });
  assert.equal(conn.client.last().data.request, 'r3');
  assert.deepEqual(conn.client.last().data.fields, { answer: 'b' });
});

test('unconfirmed and stale are final: nothing is sent again', async () => {
  for (const outcome of ['unconfirmed', 'stale']) {
    const { conn, service, submit } = setup();
    submit();
    conn.client.last().resolve({ outcome, reason: 'r' });
    await flush();
    submit();
    service.ready();
    await new Promise((r) => setTimeout(r, 20));
    assert.equal(conn.client.calls.length, 1, outcome);
  }
});

test('a host error is not an outcome: it can be resent as is, with the same request', async () => {
  const { conn, service, submit } = setup();
  submit();
  conn.client.last().reject(new HostError('session.answer', 'unknown op session.answer', { fromHost: true }));
  await flush();
  assert.deepEqual([service.get('h1', 's1', 'q1').state, service.get('h1', 's1', 'q1').reason], ['failed', 'unknown op session.answer']);
  service.ready();
  assert.equal(conn.client.calls.length, 1, 'a failed answer is not resent on its own');
  service.resend('h1', 's1', 'q1');
  assert.equal(conn.client.calls.length, 2);
  assert.equal(conn.client.last().data.request, 'r1');
});

test('busy for a resend is our own first copy still being delivered: ask again, then give its outcome', async () => {
  const { conn, service, submit } = setup();
  submit();
  conn.client.last().reject(new HostError('session.answer', 'timed out'));
  await new Promise((r) => setTimeout(r, 20));
  conn.client.last().resolve({ outcome: 'busy', reason: 'being delivered' });
  await new Promise((r) => setTimeout(r, 20));
  assert.equal(conn.client.calls.length, 3);
  assert.ok(conn.client.calls.every((c) => c.data.request === 'r1'));
  conn.client.last().resolve({ outcome: 'delivered' });
  await flush();
  assert.equal(service.get('h1', 's1', 'q1').outcome, 'delivered');
});

test('busy on resend after resend is bounded, and ends as unconfirmed', async () => {
  const { conn, service, submit } = setup();
  submit();
  conn.client.last().reject(new HostError('session.answer', 'timed out'));
  for (let k = 0; k < 10 && service.get('h1', 's1', 'q1').state !== 'done'; k++) {
    await new Promise((r) => setTimeout(r, 15));
    conn.client.last().resolve({ outcome: 'busy', reason: 'being delivered' });
    await flush();
  }
  assert.equal(service.get('h1', 's1', 'q1').outcome, 'unconfirmed');
});

test('after the host\'s memory of requests may have gone, an answer is reported unconfirmed, not resent', async () => {
  let t = 0;
  const { conn, service, submit } = setup({ now: () => t, giveUpMs: 1000 });
  submit();
  const old = conn.client;
  conn.client = null;
  old.close();
  await flush();
  t = 5000;
  conn.client = fakeClient();
  service.ready();
  assert.equal(conn.client.calls.length, 0);
  assert.equal(service.get('h1', 's1', 'q1').outcome, 'unconfirmed');
});

test('an answer given while disconnected is sent however long the connection takes to come back', async () => {
  let t = 0;
  const { conn, service, submit } = setup({ now: () => t, giveUpMs: 1000 });
  conn.client = null;
  submit();
  // Never sent, so no host can have typed it: the clock for giving up hasn't started.
  t = 60 * 60 * 1000;
  conn.client = fakeClient();
  service.ready();
  assert.equal(conn.client.calls.length, 1);
  assert.equal(service.get('h1', 's1', 'q1').state, 'sending');
  conn.client.last().resolve({ outcome: 'delivered' });
  await flush();
  assert.equal(service.get('h1', 's1', 'q1').outcome, 'delivered');
});

test('clear forgets every answer', async () => {
  const { conn, service, submit } = setup();
  conn.client = null;
  submit();
  service.clear();
  conn.client = fakeClient();
  service.ready();
  assert.equal(conn.client.calls.length, 0);
  assert.equal(service.get('h1', 's1', 'q1'), null);
});

test('HostClient.answer sends session.answer and reads an unknown outcome as unconfirmed', async () => {
  const sent = [];
  const t = { send(m) { sent.push(m); return Promise.resolve(); }, close() {} };
  const client = new HostClient(t, { timeoutMs: 500 });
  const p = client.answer({ id: 's1', question: 'q1', request: 'r1', text: 'hi' });
  await flush();
  assert.deepEqual(sent[0], { id: 1, op: 'session.answer', data: { id: 's1', question: 'q1', request: 'r1', text: 'hi' } });
  t.onmessage({ re: 1, ok: true, result: { outcome: 'typed-later', reason: 'newer host' } });
  assert.deepEqual(await p, { outcome: 'unconfirmed', reason: 'newer host' });
});
