import { test } from 'node:test';
import assert from 'node:assert/strict';
import { lostFinalLoopback } from '../lost-final-loopback.mjs';

function fixture(phase) {
  const pending = [], received = [], delivered = [];
  const transport = {
    send: async (frame) => received.push(frame),
    deliver: (frame) => delivered.push(frame),
  };
  const stats = lostFinalLoopback(transport, phase, (fn, ms) => pending.push({ fn, ms }));
  const advance = async () => {
    const batch = pending.splice(0);
    assert.ok(batch.every(({ ms }) => ms === 200));
    for (const { fn } of batch) { fn(); }
    await Promise.resolve();
  };
  return { transport, stats, received, delivered, advance };
}

for (const phase of ['baseline', 'faults']) {
  test(`${phase} delays both directions and preserves progress, unrelated results and retries`, async () => {
    const f = fixture(phase), t = f.transport;
    const send = t.send({ id: 1, op: 'session.reply', data: { request: 'uuid-a' } });
    assert.equal(f.received.length, 0);
    await f.advance(); await send;
    t.deliver({ ev: 'sessions.progress', data: { request: 'uuid-a', stage: 'typed' } });
    t.deliver({ re: 99, ok: true, result: { outcome: 'delivered' } });
    t.deliver({ re: 1, ok: true, result: { outcome: 'delivered' } });
    assert.equal(f.delivered.length, 0);
    await f.advance();
    assert.deepEqual(f.delivered.map((m) => m.re ?? m.ev),
      phase === 'faults' ? ['sessions.progress', 99] : ['sessions.progress', 99, 1]);
    const retry = t.send({ id: 2, op: 'session.reply', data: { request: 'uuid-a' } });
    await f.advance(); await retry;
    t.deliver({ re: 2, ok: true, result: { outcome: 'delivered' } });
    await f.advance();
    assert.equal(f.delivered.at(-1).re, 2);
    assert.equal(f.stats.dropped, phase === 'faults' ? 1 : 0);
    assert.deepEqual(f.stats.requests, ['uuid-a', 'uuid-a']);
    const next = t.send({ id: 3, op: 'session.reply', data: { request: 'uuid-b' } });
    await f.advance(); await next;
    t.deliver({ re: 3, ok: true, result: { outcome: 'refused' } });
    await f.advance();
    assert.equal(f.stats.dropped, phase === 'faults' ? 2 : 0);
  });
}

for (const ok of [true, false]) {
  test(`correlated progress passes before the first final response with ok=${ok} is dropped`, async () => {
    const f = fixture('faults'), t = f.transport;
    const sent = t.send({ id: 1, op: 'session.reply', data: { request: 'uuid-a' } });
    await f.advance(); await sent;
    const progress = { re: 1, progress: { stage: 'typed' } };
    t.deliver(progress);
    await f.advance();
    assert.deepEqual(f.delivered, [progress]);
    assert.equal(f.stats.dropped, 0);
    t.deliver(ok ? { re: 1, ok, result: { outcome: 'delivered' } } : { re: 1, ok, error: 'refused' });
    await f.advance();
    assert.deepEqual(f.delivered, [progress]);
    assert.equal(f.stats.dropped, 1);
  });
}

test('an unknown phase is refused without replacing the transport', () => {
  const transport = { send() {}, deliver() {} }, original = { ...transport };
  assert.throws(() => lostFinalLoopback(transport, 'production'), /phase/);
  assert.deepEqual(transport, original);
});
