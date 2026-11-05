import { test } from 'node:test';
import assert from 'node:assert/strict';
import { ReceiptClock, ReceiptTiming, answeredValue, describeInFlight, receiptText } from '../../js/ui/receipt.js';

// The receipt's timing (ui.md §6.5, decided on #617: quiet unless something's wrong), on a mocked
// clock: nothing in the quiet window, the normal path heard and not seen, a line only for an
// exception, an outcome at once, each stall, and the announcements, coalesced.

let n = 0;
const outcome = (kind) => ({ key: 'delivered', text: '', more: null, tone: 'room', line: false, speech: `${kind === 'answer' ? 'Answer' : 'Reply'} delivered` });

const mocked = new WeakSet();
function setup(t, kind = 'reply') {
  if (!mocked.has(t)) { t.mock.timers.enable({ apis: ['setTimeout', 'Date'], now: 1_000_000 }); mocked.add(t); }
  const drawn = [];
  const said = [];
  const checks = [];
  const clock = new ReceiptClock({
    describe: (entry, { stalled }) => (entry.state === 'done' ? outcome(kind) : describeInFlight(kind, entry, { machineName: 'ULTRA', stalled })),
    draw: (d) => drawn.push(receiptText(d)),
    say: (text) => said.push(text),
    delivered: () => checks.push(entry.request),
  });
  const entry = { request: `r${++n}`, state: 'sending', stage: null, attempts: 1, reason: null, progress: null };
  const show = (change = {}) => { Object.assign(entry, change); clock.show({ ...entry }); };
  const shown = () => drawn.at(-1);
  return { clock, show, shown, drawn, said, checks, tick: (ms) => t.mock.timers.tick(ms) };
}

test('the normal path shows no line: Sending, Sent, Passed to the agent and Up next are heard, not seen', (t) => {
  const { show, drawn, said, tick } = setup(t);
  show();
  tick(ReceiptTiming.quietMs);
  assert.deepEqual(said, ['Sending reply']);
  show({ stage: 'received' });
  tick(ReceiptTiming.announceGapMs);
  show({ stage: 'queued' });
  tick(ReceiptTiming.announceGapMs);
  assert.deepEqual(said, ['Sending reply', 'Reply sent', "Reply up next, after the agent's current step"]);
  assert.ok(drawn.every((d) => d === ''));
});

test('nothing is shown or spoken for the first 400 ms, then the furthest stage reached is spoken, so a fast delivery never says Sent', (t) => {
  const { show, said, tick } = setup(t);
  show();
  tick(100);
  show({ stage: 'received' });
  tick(200);
  show({ stage: 'typed' });
  assert.deepEqual(said, []);
  tick(100);
  assert.deepEqual(said, ['Reply passed to the agent']);
});

test('an outcome is said at once, past the one-second limit and any stage not yet said, and only the view that saw it go shows Send\'s check', (t) => {
  const { show, said, checks, drawn, tick } = setup(t);
  show();
  tick(ReceiptTiming.quietMs);
  tick(100);
  show({ stage: 'received' });
  show({ state: 'done', outcome: 'delivered' });
  assert.deepEqual(said, ['Sending reply', 'Reply delivered']);
  assert.equal(checks.length, 1);
  assert.ok(drawn.every((d) => d === ''));
  tick(ReceiptTiming.announceGapMs);
  assert.deepEqual(said, ['Sending reply', 'Reply delivered']);
  // A view drawn after the delivery (a card drawn afresh) shows no check, and says nothing again.
  const late = [];
  const after = new ReceiptClock({ describe: () => outcome('reply'), draw: () => {}, say: (s) => late.push(s), delivered: () => late.push('check') });
  after.show({ request: `r${n}`, state: 'done', outcome: 'delivered', attempts: 1 });
  assert.deepEqual(late, []);
  assert.deepEqual(said, ['Sending reply', 'Reply delivered']);
});

test('nothing heard for 3 s puts Still sending… on the line, words only', (t) => {
  const { show, shown, tick } = setup(t);
  show();
  tick(ReceiptTiming.quietMs);
  assert.equal(shown(), '');
  tick(ReceiptTiming.stillSendingMs - ReceiptTiming.quietMs - 1);
  assert.equal(shown(), '');
  tick(1);
  assert.equal(shown(), 'Still sending…');
});

test('Sent with nothing after it for 5 s says it is still waiting on the machine', (t) => {
  const { show, shown, tick } = setup(t);
  show({ stage: 'received' });
  tick(ReceiptTiming.quietMs);
  tick(ReceiptTiming.sentStallMs - ReceiptTiming.quietMs - 1);
  assert.equal(shown(), '');
  tick(1);
  assert.equal(shown(), 'Sent · still waiting on ULTRA');
});

test('Passed to an idle agent with nothing after it for 10 s says not picked up yet, and Up next never stalls', (t) => {
  const { show, shown, tick } = setup(t);
  show({ stage: 'typed' });
  tick(ReceiptTiming.quietMs);
  tick(ReceiptTiming.passedStallMs - ReceiptTiming.quietMs);
  assert.equal(shown(), 'Passed to the agent · not picked up yet');

  const busy = setup(t);
  busy.show({ stage: 'queued' });
  busy.tick(10 * ReceiptTiming.passedStallMs);
  assert.equal(busy.shown(), '');
});

test('a wait says why on the line: the first copy still being delivered, no answer yet, or no machine to send to', (t) => {
  const { show, shown, tick } = setup(t);
  show({ stage: 'typed' });
  tick(ReceiptTiming.quietMs);
  show({ state: 'waiting', reason: 'still being delivered' });
  assert.equal(shown(), 'Passed to the agent · still being delivered');
  show({ state: 'waiting', reason: 'timed out' });
  assert.equal(shown(), 'Still sending…');
  show({ state: 'waiting', reason: 'not connected' });
  assert.equal(shown(), 'Waiting for ULTRA · it goes when ULTRA is back');
  assert.equal(describeInFlight('reply', { state: 'waiting', reason: 'not connected' }).still, true);
});

test('the line goes when the next stage makes it untrue', (t) => {
  const { show, shown, tick } = setup(t);
  show({ stage: 'received' });
  tick(ReceiptTiming.quietMs);
  tick(ReceiptTiming.sentStallMs - ReceiptTiming.quietMs);
  assert.equal(shown(), 'Sent · still waiting on ULTRA');
  show({ stage: 'typed' });
  assert.equal(shown(), '');
});

test('a resend that meets the first copy in flight keeps saying still being delivered, and says so once, as it goes again and again', (t) => {
  const { show, shown, drawn, said, tick } = setup(t);
  show({ stage: 'received' });
  tick(ReceiptTiming.quietMs);
  tick(ReceiptTiming.sentStallMs - ReceiptTiming.quietMs);
  show({ state: 'waiting', reason: 'still being delivered' });
  assert.equal(shown(), 'Sent · still being delivered');
  for (let i = 0; i < 3; i++) {
    tick(3000);
    show({ state: 'sending', reason: null, attempts: 2 + i });
    tick(100);
    show({ state: 'waiting', reason: 'still being delivered' });
  }
  tick(ReceiptTiming.announceGapMs);
  assert.equal(shown(), 'Sent · still being delivered');
  assert.equal(drawn.filter((d) => d === 'Sent · still waiting on ULTRA').length, 1);
  assert.equal(said.filter((s) => s === 'Reply still being delivered').length, 1);
  assert.equal(said.at(-1), 'Reply still being delivered');
});

test('a machine without progress says nothing of Sent or Passed, puts Still sending… on the line for a resend, never Sending again…', (t) => {
  const { show, shown, drawn, said, tick } = setup(t);
  show();
  tick(ReceiptTiming.quietMs);
  assert.equal(shown(), '');
  show({ state: 'waiting', reason: 'timed out' });
  show({ state: 'sending', attempts: 2 });
  assert.equal(shown(), 'Still sending…');
  show({ state: 'done', outcome: 'delivered' });
  assert.equal(shown(), '');
  assert.ok(drawn.every((d) => !/again|Sent|Passed/.test(d)));
  assert.ok(said.every((s) => !/sent|passed/.test(s)));
});

test('each change is heard once, prefixed, at most one a second, the later of two winning, and the outcome at once', (t) => {
  const { show, said, tick } = setup(t);
  show();
  tick(100);
  show({ stage: 'received' });
  assert.deepEqual(said, []);
  tick(ReceiptTiming.quietMs - 100);
  assert.deepEqual(said, ['Reply sent']);
  tick(400);
  show({ stage: 'queued' });
  show({ state: 'done', outcome: 'delivered' });
  // The Up next inside the second is passed over for the outcome after it, which doesn't wait.
  assert.deepEqual(said, ['Reply sent', 'Reply delivered']);
  // Drawn again (a card redrawn), nothing is said twice.
  show({ state: 'done', outcome: 'delivered' });
  tick(ReceiptTiming.announceGapMs);
  assert.deepEqual(said, ['Reply sent', 'Reply delivered']);
});

test('the timing belongs to the request, not the drawing: a question card drawn afresh keeps its stall and says nothing again', (t) => {
  const first = setup(t, 'answer');
  first.show({ stage: 'received' });
  first.tick(ReceiptTiming.quietMs);
  assert.deepEqual(first.said, ['Answer sent']);
  const entry = { request: `r${n}`, state: 'sending', stage: 'received', attempts: 1, reason: null, progress: null };
  first.clock.destroy();
  const drawn = [];
  const said = [];
  const again = new ReceiptClock({
    describe: (e, { stalled }) => describeInFlight('answer', e, { machineName: 'ULTRA', stalled }),
    draw: (d) => drawn.push(receiptText(d)),
    say: (text) => said.push(text),
  });
  again.show(entry);
  assert.deepEqual(drawn, ['']);
  first.tick(ReceiptTiming.sentStallMs - ReceiptTiming.quietMs);
  assert.equal(drawn.at(-1), 'Sent · still waiting on ULTRA');
  assert.deepEqual(said, ['Answer sent, still waiting on ULTRA']);
});

test('a stage still waiting out the one-second limit when its view is drawn afresh is said by the new view, after the same second', (t) => {
  const first = setup(t, 'answer');
  first.show({ stage: 'received' });
  first.tick(ReceiptTiming.quietMs);
  first.show({ stage: 'typed' });
  assert.deepEqual(first.said, ['Answer sent']);
  first.clock.destroy();
  const said = [];
  const again = new ReceiptClock({ describe: (e, { stalled }) => describeInFlight('answer', e, { machineName: 'ULTRA', stalled }), draw: () => {}, say: (text) => said.push(text) });
  const entry = { request: `r${n}`, state: 'sending', stage: 'typed', attempts: 1, reason: null, progress: null };
  again.show(entry);
  assert.deepEqual(said, []);
  first.tick(ReceiptTiming.announceGapMs);
  assert.deepEqual(said, ['Answer passed to the agent']);
  again.show(entry);
  first.tick(ReceiptTiming.announceGapMs);
  assert.deepEqual([...first.said, ...said], ['Answer sent', 'Answer passed to the agent']);
});

test('of two views of a request the one on screen says it, and a line moved on to the next request never says the last one\'s', (t) => {
  const { tick } = setup(t);
  const said = [];
  const view = (name, heard) => new ReceiptClock({
    describe: (e, { stalled }) => describeInFlight('reply', e, { machineName: 'ULTRA', stalled }),
    draw: () => {},
    say: (text) => said.push(`${name}: ${text}`),
    heard: () => heard,
  });
  // A session's card while another is open, and Needs you.
  const card = view('card', false);
  const lane = view('lane', true);
  const first = { request: `v${++n}`, state: 'sending', stage: 'received', attempts: 1, reason: null, progress: null };
  card.show(first);
  lane.show(first);
  tick(ReceiptTiming.quietMs);
  assert.deepEqual(said, ['lane: Reply sent']);
  // Passed to the agent inside the second, and the lane moves on to the next reply before it is
  // said: it goes to the page's own line (none here), never onto the lane now showing the next.
  lane.show({ ...first, stage: 'typed' });
  lane.show({ request: `v${++n}`, state: 'sending', stage: null, attempts: 1, reason: null, progress: null });
  card.destroy();
  tick(ReceiptTiming.announceGapMs);
  // The lane says only its own request's: the next reply's Sending, never the last one's stage.
  assert.deepEqual(said, ['lane: Reply sent', 'lane: Sending reply']);
});

test('an answer never says Up next', () => {
  assert.equal(describeInFlight('answer', { state: 'sending', stage: 'queued' }).speech, 'Answer passed to the agent');
});

test("a confirmed answer's value: a tap's option by its label, free text's first line cut at a word break within 40 characters, and none for a form", () => {
  const tap = { fields: [{ name: 'answer', type: 'choice', options: [{ value: 'pg', label: 'PostgreSQL (Recommended)' }] }] };
  assert.equal(answeredValue(tap, { fields: { answer: 'pg' } }), 'PostgreSQL (Recommended)');
  assert.equal(answeredValue(tap, { fields: { answer: { other: 'MariaDB\nbecause' } } }), 'MariaDB…');
  const text = { fields: [] };
  assert.equal(answeredValue(text, { text: 'Fixes   first,\tthen tests' }), 'Fixes first, then tests');
  assert.equal(answeredValue(text, { text: 'Fix the flaky retry test before the release goes out' }), 'Fix the flaky retry test before the…');
  // No break after the 20th character: cut at 40.
  assert.equal(answeredValue(text, { text: 'x'.repeat(60) }), `${'x'.repeat(40)}…`);
  assert.equal(answeredValue(text, { text: `ab ${'x'.repeat(60)}` }), `${`ab ${'x'.repeat(60)}`.slice(0, 40)}…`);
  const form = { fields: [{ name: 'a', type: 'text' }, { name: 'b', type: 'boolean' }] };
  assert.equal(answeredValue(form, { fields: { a: 'x', b: true } }), null);
});

test("a confirmed answer's value draws its hidden and bidi characters as escapes (#546)", () => {
  assert.equal(answeredValue({ fields: [] }, { text: 'ok\u202Egnp.exe' }), 'ok\\u202Egnp.exe');
});

test('a wait that ends inside the one-second limit is never said: the request is back in a state already said', (t) => {
  const { show, said, tick } = setup(t);
  show();
  tick(ReceiptTiming.quietMs);
  show({ stage: 'received' });
  tick(ReceiptTiming.announceGapMs);
  assert.deepEqual(said, ['Sending reply', 'Reply sent']);
  show({ state: 'waiting', reason: 'not connected' });
  tick(100);
  show({ state: 'sending', reason: null });
  tick(ReceiptTiming.announceGapMs * 2);
  assert.deepEqual(said, ['Sending reply', 'Reply sent']);
});

// Try again resends the same request: one that fails the same way again is heard again (a screen
// reader heard the retry go, and then nothing), once for each attempt, never twice for one.
test('Try again that fails the same way is heard again, once for each attempt', (t) => {
  t.mock.timers.enable({ apis: ['setTimeout', 'Date'], now: 1_000_000 });
  const said = [];
  const failed = { key: 'failed', text: 'Not sent', more: null, tone: 'warn', line: true, speech: 'Reply not sent: no answer from the machine' };
  const clock = new ReceiptClock({
    describe: (e, { stalled }) => (e.state === 'failed' ? failed : describeInFlight('reply', e, { machineName: 'ULTRA', stalled })),
    draw: () => {},
    say: (s) => said.push(s),
  });
  const request = `r${++n}`;
  const at = (state, attempts) => clock.show({ request, state, attempts, stage: null, reason: null, progress: null });
  at('failed', 1);
  at('failed', 1);
  at('sending', 2);
  at('failed', 2);
  assert.equal(said.filter((s) => s === failed.speech).length, 2);
  clock.destroy();
});

// A request whose views all went on its way (its session ended, its card taken away) is followed to
// its outcome on the page's line: heard, at the request's own pace, and never again by a view drawn
// afresh for it.
test('a request whose view goes while it is on its way is still heard to its outcome, once', (t) => {
  t.mock.timers.enable({ apis: ['setTimeout', 'Date'], now: 1_000_000 });
  const describe = (entry, { stalled }) => (entry.state === 'done' ? outcome('reply') : describeInFlight('reply', entry, { machineName: 'ULTRA', stalled }));
  const said = [];
  const page = [];
  let listener = null;
  const clock = new ReceiptClock({ describe, draw: () => {}, say: (s) => said.push(s), page: (s) => page.push(s), follow: (fn) => { listener = fn; return () => { listener = null; }; } });
  const entry = { request: `r${++n}`, state: 'sending', stage: null, attempts: 1, reason: null, progress: null };
  clock.show(entry);
  t.mock.timers.tick(ReceiptTiming.quietMs);
  assert.deepEqual(said, ['Sending reply']);

  clock.destroy();
  assert.ok(listener, 'followed once its view went');
  listener({ ...entry, stage: 'received' });
  t.mock.timers.tick(ReceiptTiming.announceGapMs);
  listener({ ...entry, stage: 'received', state: 'done', outcome: 'delivered' });
  assert.deepEqual(page, ['Reply sent', 'Reply delivered']);
  assert.equal(listener, null, 'and let go at its outcome');

  const again = [];
  new ReceiptClock({ describe, draw: () => {}, say: (s) => again.push(s) }).show({ ...entry, state: 'done', outcome: 'delivered' });
  assert.deepEqual(again, []);
  assert.deepEqual(said, ['Sending reply']);
});

test('a request followed after its view went is let go at its give-up however it stands', (t) => {
  t.mock.timers.enable({ apis: ['setTimeout', 'Date'], now: 1_000_000 });
  const describe = (entry, { stalled }) => describeInFlight('answer', entry, { machineName: 'ULTRA', stalled });
  let listener = null;
  const clock = new ReceiptClock({ describe, draw: () => {}, page: () => {}, follow: (fn) => { listener = fn; return () => { listener = null; }; } });
  clock.show({ request: `r${++n}`, state: 'waiting', reason: 'not connected', stage: null, attempts: 0, progress: null });
  clock.destroy();
  assert.ok(listener);
  t.mock.timers.tick(ReceiptTiming.followMs);
  assert.equal(listener, null);
});
