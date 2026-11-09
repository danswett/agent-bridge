import { test } from 'node:test';
import assert from 'node:assert/strict';
import { AnswerService } from '../../js/answer-service.js';
import { HostClient, HostError } from '../../js/host-client.js';
import { finished, recentTimings, tapped } from '../../js/timing.js';
import { readAnswerResult } from '../../js/protocol.js';

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

test('a stale answer from a form the host cancelled never went in, so it can be given again as a new request', async () => {
  const { conn, service, submit } = setup();
  submit();
  conn.client.last().resolve({ outcome: 'stale', reason: 'cancelled the form', code: 'formReset' });
  await flush();
  assert.equal(service.get('h1', 's1', 'q1').detail.code, 'formReset');
  submit();
  assert.equal(conn.client.calls.length, 2);
  assert.equal(conn.client.calls[1].data.request, 'r2');
  // Any other stale is final: the question is no longer asked.
  conn.client.last().resolve({ outcome: 'stale', reason: 'gone' });
  await flush();
  submit();
  assert.equal(conn.client.calls.length, 2);
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
  assert.deepEqual(await p, { outcome: 'unconfirmed', reason: 'newer host', stage: null });
});

test('an answer typed but unconfirmed keeps its selections and is asked about again on reconnect, with the same request (#467)', async () => {
  const { conn, service, seen, submit } = setup();
  submit();
  conn.client.last().resolve({ outcome: 'unconfirmed', reason: 'not recorded', stage: 'typed' });
  await flush();
  assert.equal(service.get('h1', 's1', 'q1').outcome, 'unconfirmed');
  // Still final for the person: giving it again sends nothing.
  submit({ fields: { answer: 'b' } });
  assert.equal(conn.client.calls.length, 1);

  // Reconnected: the host is asked what that request came to, and nothing new is sent.
  conn.client = fakeClient();
  service.ready('h1');
  assert.equal(conn.client.calls.length, 1);
  assert.deepEqual(conn.client.last().data, { id: 's1', question: 'q1', request: 'r1', fields: { answer: 'a' } });
  assert.equal(service.get('h1', 's1', 'q1').state, 'done');
  conn.client.last().resolve({ outcome: 'delivered', reason: null, stage: null });
  await flush();
  assert.equal(service.get('h1', 's1', 'q1').outcome, 'delivered');
  assert.deepEqual(seen, ['sending', 'done:unconfirmed', 'done:delivered']);
  // Delivered is final: nothing more on the next reconnect.
  conn.client = fakeClient();
  service.ready('h1');
  assert.equal(conn.client.calls.length, 0);
});

test('of two asks about an unconfirmed answer, only the later one can change it, whichever is answered last', async () => {
  const { conn, service, submit } = setup();
  submit();
  conn.client.last().resolve({ outcome: 'unconfirmed', reason: 'not recorded', stage: 'typed' });
  await flush();

  // Reconnected twice before the first ask was answered.
  const older = fakeClient();
  conn.client = older;
  service.ready('h1');
  const newer = fakeClient();
  conn.client = newer;
  service.ready('h1');
  newer.last().resolve({ outcome: 'unconfirmed', reason: 'recorded another answer', stage: 'recorded' });
  await flush();
  older.last().resolve({ outcome: 'unconfirmed', reason: 'not recorded', stage: 'typed' });
  await flush();

  const entry = service.get('h1', 's1', 'q1');
  assert.deepEqual([entry.outcome, entry.detail.stage, entry.reason], ['unconfirmed', 'recorded', 'recorded another answer']);
});

test('an ask about an unconfirmed answer that brings a new reason at the same stage shows the new reason', async () => {
  const { conn, service, seen, submit } = setup();
  submit();
  conn.client.last().resolve({ outcome: 'unconfirmed', reason: 'the session recorded a different answer', stage: 'recorded' });
  await flush();

  // Asked again: the same stage, and what the host now knows (a late completion's error).
  conn.client = fakeClient();
  service.ready('h1');
  conn.client.last().resolve({ outcome: 'unconfirmed', reason: 'the call failed after it was recorded', stage: 'recorded' });
  await flush();
  const entry = service.get('h1', 's1', 'q1');
  assert.deepEqual([entry.outcome, entry.detail.stage, entry.reason], ['unconfirmed', 'recorded', 'the call failed after it was recorded']);

  // The same again changes nothing.
  conn.client = fakeClient();
  service.ready('h1');
  conn.client.last().resolve({ outcome: 'unconfirmed', reason: 'the call failed after it was recorded', stage: 'recorded' });
  await flush();
  assert.deepEqual(seen, ['sending', 'done:unconfirmed', 'done:unconfirmed']);
});

test('an unconfirmed answer the host never typed, or one too old for the host to remember, is not asked about again', async () => {
  let t = 0;
  const { conn, service, submit } = setup({ now: () => t, giveUpMs: 1000 });
  submit();
  // No stage: this app gave up on it, or an older host; the host may not hold the request.
  conn.client.last().resolve({ outcome: 'unconfirmed', reason: 'no outcome', stage: null });
  await flush();
  conn.client = fakeClient();
  service.ready('h1');
  assert.equal(conn.client.calls.length, 0);

  const other = setup({ now: () => t, giveUpMs: 1000 });
  other.submit();
  other.conn.client.last().resolve({ outcome: 'unconfirmed', reason: 'not recorded', stage: 'typed' });
  await flush();
  t = 5000;
  other.conn.client = fakeClient();
  other.service.ready('h1');
  assert.equal(other.conn.client.calls.length, 0);
  assert.equal(service.get('h1', 's1', 'q1').outcome, 'unconfirmed');
});

// ---- progress (sessions.progress, #600) ----

// fakeClient's, with what the machine says of its progress played through the onProgress the
// service passed, and the request taken by its transport at once (onSent).
function progressClient() {
  const c = fakeClient();
  c.answer = (data, { onProgress, onSent } = {}) => new Promise((resolve, reject) => { c.calls.push({ data, resolve, reject, progress: onProgress }); onSent?.(); });
  return c;
}

test("an answer's stages from the machine are shown in order: Sent, then Typed, then its outcome", async () => {
  const { conn, service, seen, submit } = setup();
  conn.client = progressClient();
  const stages = [];
  service.watch('h1', 's1', 'q1', (e) => stages.push(e.stage));
  submit();
  const call = conn.client.last();
  call.progress({ stage: 'received', working: false });
  call.progress({ stage: 'typed', working: false });
  call.resolve({ outcome: 'delivered', reason: null, timings: { typed: 40, confirmed: 90 } });
  await flush();
  assert.deepEqual(stages, [null, 'received', 'typed', 'typed']);
  assert.deepEqual(seen, ['sending', 'sending', 'sending', 'done:delivered']);
  assert.deepEqual(service.get('h1', 's1', 'q1').detail.timings, { typed: 40, confirmed: 90 });
});

test('typed into a working session is Queued, and a stage behind one already reached, or unknown, changes nothing', async () => {
  const { conn, service, submit } = setup();
  conn.client = progressClient();
  submit();
  const call = conn.client.last();
  call.progress({ stage: 'typed', working: true });
  assert.equal(service.get('h1', 's1', 'q1').stage, 'queued');
  // A resend's repeat replays the stages reached, received first: what was typed stays typed.
  call.progress({ stage: 'received', working: false });
  assert.equal(service.get('h1', 's1', 'q1').stage, 'queued');
  // Nor does a stage this app doesn't know, from a newer host.
  call.progress({ stage: 'pondering', working: false });
  assert.equal(service.get('h1', 's1', 'q1').stage, 'queued');
});

test('a stage from an attempt given up on is not shown on the next', async (t) => {
  t.mock.timers.enable({ apis: ['setTimeout'] });
  const { conn, service, submit } = setup();
  conn.client = progressClient();
  submit();
  const first = conn.client.last();
  first.reject(new HostError('session.answer', 'timed out'));
  await flush();
  t.mock.timers.tick(5);
  await flush();
  assert.equal(conn.client.calls.length, 2);
  first.progress({ stage: 'typed', working: false });
  assert.equal(service.get('h1', 's1', 'q1').stage, null);
});

test("a stage replayed behind one reached adds nothing to the answer's timing marks", async () => {
  const { conn, submit } = setup({ marks: 'answer' });
  conn.client = progressClient();
  submit();
  const call = conn.client.last();
  call.progress({ stage: 'typed', working: true });
  call.progress({ stage: 'received', working: false });
  call.progress({ stage: 'typed', working: true });
  call.resolve({ outcome: 'delivered', reason: null, timings: { typed: 40 } });
  await flush();
  const record = recentTimings().at(-1);
  assert.deepEqual(record.client.map(([mark]) => mark), ['sent', 'queued', 'result']);
  assert.deepEqual(record.host, { typed: 40 });
});

test('frames alternating between typed and up next add one timing mark, not one each', async () => {
  const { conn, service, submit } = setup({ marks: 'answer' });
  conn.client = progressClient();
  submit();
  const call = conn.client.last();
  for (const working of [false, true, false, true]) { call.progress({ stage: 'typed', working }); }
  assert.equal(service.get('h1', 's1', 'q1').stage, 'typed');
  call.resolve({ outcome: 'delivered', reason: null });
  await flush();
  assert.deepEqual(recentTimings().at(-1).client.map(([mark]) => mark), ['sent', 'typed', 'result']);
});

test("a host's outcome or timing name with a line break cannot forge a line on the console", (t) => {
  const lines = [];
  t.mock.method(console, 'debug', (line) => lines.push(line));
  const result = readAnswerResult({ outcome: 'delivered\nFORGED', timings: { 'typed\nFORGED': 5, typed: 7, bogus: 9 } });
  assert.deepEqual(result.timings, { typed: 7 });
  tapped('reply', 'forge-1');
  finished('forge-1', 'delivered\nFORGED', result.timings);
  assert.equal(lines.length, 1);
  assert.match(lines[0], /^agent-bridge reply forge-1 other: result \d+ ms; host: typed 7 ms$/);
});

test('an answer whose request its client never sent is not marked sent', async () => {
  const { conn, submit } = setup({ marks: 'answer' });
  // A client that takes the call but whose transport never sends it (no onSent).
  conn.client = fakeClient();
  submit();
  conn.client.last().resolve({ outcome: 'unconfirmed', reason: null });
  await flush();
  assert.deepEqual(recentTimings().at(-1).client.map(([mark]) => mark), ['result']);
});

// The relay connection rests while the app is out of sight, but never with an answer on its
// way (connection.js, #610): busy says so, between resends too.
test('an answer is on its way while it is sent and while a resend waits, and not once it has an outcome or waits for a channel', async (t) => {
  t.mock.timers.enable({ apis: ['setTimeout'] });
  const { conn, service, submit } = setup();
  assert.equal(service.busy, false);
  submit();
  assert.equal(service.busy, true);
  conn.client.last().reject(new HostError('session.answer', 'timed out'));
  await flush();
  assert.equal(service.get('h1', 's1', 'q1').state, 'waiting');
  assert.equal(service.busy, true, 'a resend is due on its own');
  t.mock.timers.tick(5);
  await flush();
  conn.client.last().resolve({ outcome: 'delivered', reason: null });
  await flush();
  assert.equal(service.busy, false);

  // One given with no channel up waits for one, as well at rest as awake.
  conn.client = null;
  service.submit({ hostId: 'h1', sessionId: 's1', questionId: 'q2', answer: ANSWER });
  assert.equal(service.get('h1', 's1', 'q2').state, 'waiting');
  assert.equal(service.busy, false);
});