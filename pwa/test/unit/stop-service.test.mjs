import { test } from 'node:test';
import assert from 'node:assert/strict';
import { StopService } from '../../js/stop-service.js';
import { HostClient, HostError } from '../../js/host-client.js';
import { readStopResult, readStatus } from '../../js/protocol.js';
import { describeStop } from '../../js/ui/stop-view.js';

const flush = () => new Promise((r) => setImmediate(r));

// A HostClient stand-in: each stop() call waits for the test to settle it.
function fakeClient() {
  const c = {
    closed: false,
    calls: [],
    stop(data) { return new Promise((resolve, reject) => { c.calls.push({ data, resolve, reject }); }); },
    last() { return c.calls[c.calls.length - 1]; },
  };
  return c;
}

function setup() {
  let n = 0;
  const conn = { client: fakeClient(), hostId: 'h1' };
  const service = new StopService({ current: () => conn.client && { client: conn.client, hostId: conn.hostId }, uuid: () => `r${++n}`, retryMs: 5 });
  const press = (confirm = null) => service.submit({ hostId: 'h1', sessionId: 's1', confirm });
  return { conn, service, press };
}

test('a first press sends no confirmation, and a confirm outcome keeps the token for the second', async () => {
  const { conn, service, press } = setup();
  press();
  press(); // a second tap while the first is on its way sends nothing
  assert.deepEqual(conn.client.calls.map((c) => c.data), [{ id: 's1', request: 'r1' }]);
  conn.client.last().resolve(readStopResult({ outcome: 'confirm', reason: 'this session is still working; the confirmation lapses in 10 s', confirm: 'tok_1', confirmSeconds: 10 }));
  await flush();
  const first = service.get('h1', 's1');
  assert.equal(first.outcome, 'confirm');
  assert.equal(first.detail.confirm, 'tok_1');

  // The second press is a new request carrying the token.
  press(first.detail.confirm);
  assert.deepEqual(conn.client.last().data, { id: 's1', request: 'r2', confirm: 'tok_1' });
  conn.client.last().resolve(readStopResult({ outcome: 'ended' }));
  await flush();
  assert.equal(service.get('h1', 's1').outcome, 'ended');
});

test('a second press whose response is lost is resent with the same request and the same token', async () => {
  const { conn, service, press } = setup();
  press('tok_1');
  conn.client.last().reject(new HostError('session.stop', 'timed out'));
  await new Promise((r) => setTimeout(r, 20));
  assert.deepEqual(conn.client.calls.map((c) => c.data), [{ id: 's1', request: 'r1', confirm: 'tok_1' }, { id: 's1', request: 'r1', confirm: 'tok_1' }]);
  conn.client.last().resolve(readStopResult({ outcome: 'ended', forced: true }));
  await flush();
  assert.equal(service.get('h1', 's1').detail.forced, true);
});

test('the host client sends session.stop and reads what comes back', async () => {
  const sent = [];
  const t = { send(m) { sent.push(m); return Promise.resolve(); }, close() {}, onmessage: null, onclose: null };
  const client = new HostClient(t, { timeoutMs: 1000 });
  const p = client.stop({ id: 's1', request: 'r1' });
  await flush();
  assert.equal(sent[0].op, 'session.stop');
  t.onmessage({ re: sent[0].id, ok: true, result: { outcome: 'confirm', confirm: 'abc', confirmSeconds: 10, reason: 'x' } });
  assert.deepEqual(await p, { outcome: 'confirm', reason: 'x', confirm: 'abc', confirmSeconds: 10, forced: false });
});

test('an unknown outcome or a confirm with no usable token reads as failed, never ended or confirm', () => {
  assert.equal(readStopResult({ outcome: 'paused' }).outcome, 'failed');
  assert.equal(readStopResult({ outcome: 'confirm' }).outcome, 'failed');
  assert.equal(readStopResult({ outcome: 'confirm', confirm: '<b>' }).outcome, 'failed');
  assert.equal(readStopResult({ outcome: 'confirm', confirm: 'ok', confirmSeconds: -1 }).confirmSeconds, 10);
  assert.equal(readStopResult(null).outcome, 'failed');
  assert.equal(readStatus('ending'), 'ending');
});

test('what a press says: the second press asked for with the time left, then a lapse, as on the old dashboard', () => {
  const entry = { state: 'done', outcome: 'confirm', reason: 'this session is still working; the confirmation lapses in 10 s', detail: { confirm: 't', confirmSeconds: 10 } };
  assert.equal(describeStop(entry, { armed: { left: 7 } }).text, 'Press End session again to end it: this session is still working; the confirmation lapses in 10 s. 7 s left.');
  assert.equal(describeStop(entry, { lapsed: true }).text, 'End session NOT confirmed: no second press within 10 s. The session is still running.');
  assert.equal(describeStop({ state: 'done', outcome: 'ended', detail: { forced: true } }).text, 'Session ended. It did not exit on its own, so the machine terminated it.');
  assert.equal(describeStop({ state: 'done', outcome: 'refused', reason: 'the session runs as another user; end it at the machine' }).text,
    'The machine refused to end this session: the session runs as another user; end it at the machine.');
  assert.equal(describeStop({ state: 'done', outcome: 'whatever' }).tone, 'bad');
});
