import { test } from 'node:test';
import assert from 'node:assert/strict';
import { ReceiptClock, ReceiptTiming, answeredValue, describeInFlight, receiptText } from '../../js/ui/receipt.js';

// The receipt's timing (ui.md §6.5, decided on #617), on a mocked clock: the quiet window, the
// dwell, an outcome at once, each stall, and the announcements, coalesced.

let n = 0;
const outcome = (kind) => ({ key: 'delivered', text: 'Delivered', more: null, tone: 'ok', mark: 'check', speech: `${kind === 'answer' ? 'Answer' : 'Reply'} delivered` });

const mocked = new WeakSet();
function setup(t, kind = 'reply') {
  if (!mocked.has(t)) { t.mock.timers.enable({ apis: ['setTimeout', 'Date'], now: 1_000_000 }); mocked.add(t); }
  const drawn = [];
  const said = [];
  const clock = new ReceiptClock({
    describe: (entry, { stalled }) => (entry.state === 'done' ? outcome(kind) : describeInFlight(kind, entry, { machineName: 'ULTRA', stalled })),
    draw: (d) => drawn.push(receiptText(d)),
    say: (text) => said.push(text),
  });
  const entry = { request: `r${++n}`, state: 'sending', stage: null, attempts: 1, reason: null, progress: null };
  const show = (change = {}) => { Object.assign(entry, change); clock.show({ ...entry }); };
  const shown = () => drawn.at(-1);
  return { clock, show, shown, drawn, said, tick: (ms) => t.mock.timers.tick(ms) };
}

test('nothing shows for the first 400 ms, then the furthest stage reached, so a fast delivery never flashes Sent', (t) => {
  const { show, shown, drawn, tick } = setup(t);
  show();
  assert.equal(shown(), '');
  tick(100);
  show({ stage: 'received' });
  tick(200);
  show({ stage: 'typed' });
  assert.equal(shown(), '');
  tick(100);
  assert.equal(shown(), 'Passed to the agent');
  assert.ok(!drawn.includes('Sent'));
});

test('a stage shown stays at least 400 ms before the next replaces it', (t) => {
  const { show, shown, tick } = setup(t);
  show();
  tick(100);
  show({ stage: 'received' });
  tick(ReceiptTiming.quietMs - 100);
  assert.equal(shown(), 'Sent');
  tick(100);
  show({ stage: 'typed' });
  assert.equal(shown(), 'Sent');
  tick(299);
  assert.equal(shown(), 'Sent');
  tick(1);
  assert.equal(shown(), 'Passed to the agent');
});

test('an outcome shows at once, past the quiet window and any stage not yet shown', (t) => {
  const { show, shown, drawn, tick } = setup(t);
  show();
  tick(50);
  show({ stage: 'received' });
  tick(50);
  show({ state: 'done', outcome: 'delivered' });
  assert.equal(shown(), 'Delivered');
  assert.deepEqual(drawn, ['', 'Delivered']);
});

test('nothing heard for 3 s says Still sending…, words only', (t) => {
  const { show, shown, tick } = setup(t);
  show();
  tick(ReceiptTiming.quietMs);
  assert.equal(shown(), 'Sending…');
  tick(ReceiptTiming.stillSendingMs - ReceiptTiming.quietMs - 1);
  assert.equal(shown(), 'Sending…');
  tick(1);
  assert.equal(shown(), 'Still sending…');
});

test('Sent with nothing after it for 5 s says it is still waiting on the machine', (t) => {
  const { show, shown, tick } = setup(t);
  show({ stage: 'received' });
  tick(ReceiptTiming.sentStallMs - 1);
  assert.equal(shown(), 'Sent');
  tick(1);
  assert.equal(shown(), 'Sent · still waiting on ULTRA');
});

test('Passed to an idle agent with nothing after it for 10 s says not picked up yet, and Up next never stalls', (t) => {
  const { show, shown, tick } = setup(t);
  show({ stage: 'typed' });
  tick(ReceiptTiming.passedStallMs);
  assert.equal(shown(), 'Passed to the agent · not picked up yet');

  const busy = setup(t);
  busy.show({ stage: 'queued' });
  busy.tick(10 * ReceiptTiming.passedStallMs);
  assert.equal(busy.shown(), "Up next · after the agent's current step");
});

test('a wait says why: the first copy still being delivered, no answer yet, or no machine to send to', (t) => {
  const { show, shown, tick } = setup(t);
  show({ stage: 'typed' });
  // Each tick ends where a timer is due: a mocked tick runs what falls due at the time it ends.
  tick(ReceiptTiming.quietMs);
  tick(ReceiptTiming.dwellMs);
  show({ state: 'waiting', reason: 'still being delivered' });
  assert.equal(shown(), 'Passed to the agent · still being delivered');
  tick(ReceiptTiming.dwellMs);
  show({ state: 'waiting', reason: 'timed out' });
  assert.equal(shown(), 'Still sending…');
  tick(ReceiptTiming.dwellMs);
  show({ state: 'waiting', reason: 'not connected' });
  assert.equal(shown(), 'Waiting for ULTRA · it goes when ULTRA is back');
});

test('a resend that meets the first copy in flight keeps saying still being delivered, and says so once, as it goes again and again', (t) => {
  const { show, shown, drawn, said, tick } = setup(t);
  show({ stage: 'received' });
  tick(ReceiptTiming.quietMs);
  tick(ReceiptTiming.sentStallMs);
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

test('a machine without progress shows Sending…, Still sending… after 3 s, and a resend reads Still sending…, never Sending again…', (t) => {
  const { show, shown, drawn, tick } = setup(t);
  show();
  tick(ReceiptTiming.quietMs);
  assert.equal(shown(), 'Sending…');
  show({ state: 'waiting', reason: 'timed out' });
  tick(ReceiptTiming.dwellMs);
  show({ state: 'sending', attempts: 2 });
  assert.equal(shown(), 'Still sending…');
  show({ state: 'done', outcome: 'delivered' });
  assert.equal(shown(), 'Delivered');
  assert.ok(drawn.every((d) => !/again|Sent|Passed/.test(d)));
});

test('each change is announced once, prefixed, at most one a second, the later of two winning; the outcome always; Sending… never', (t) => {
  const { show, said, tick } = setup(t);
  show();
  tick(100);
  show({ stage: 'received' });
  assert.deepEqual(said, []);
  tick(ReceiptTiming.quietMs - 100);
  assert.deepEqual(said, ['Reply sent']);
  tick(ReceiptTiming.dwellMs);
  show({ stage: 'queued' });
  show({ state: 'done', outcome: 'delivered' });
  // The Up next inside the second is passed over for the outcome after it.
  assert.deepEqual(said, ['Reply sent']);
  tick(ReceiptTiming.announceGapMs);
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
  assert.deepEqual(drawn, ['Sent']);
  first.tick(ReceiptTiming.sentStallMs);
  assert.equal(drawn.at(-1), 'Sent · still waiting on ULTRA');
  assert.deepEqual(said, ['Answer sent, still waiting on ULTRA']);
});

test('an answer never says Up next', () => {
  assert.equal(describeInFlight('answer', { state: 'sending', stage: 'queued' }).text, 'Passed to the agent');
});

test("a confirmed answer's value: a tap's option by its label, free text's first line cut to about 40 characters, and none for a form", () => {
  const tap = { fields: [{ name: 'answer', type: 'choice', options: [{ value: 'pg', label: 'PostgreSQL (Recommended)' }] }] };
  assert.equal(answeredValue(tap, { fields: { answer: 'pg' } }), 'PostgreSQL (Recommended)');
  assert.equal(answeredValue(tap, { fields: { answer: { other: 'MariaDB\nbecause' } } }), 'MariaDB…');
  const text = { fields: [] };
  assert.equal(answeredValue(text, { text: 'Fixes first' }), 'Fixes first');
  assert.equal(answeredValue(text, { text: 'x'.repeat(60) }), `${'x'.repeat(39)}…`);
  const form = { fields: [{ name: 'a', type: 'text' }, { name: 'b', type: 'boolean' }] };
  assert.equal(answeredValue(form, { fields: { a: 'x', b: true } }), null);
});

test("a confirmed answer's value draws its hidden and bidi characters as escapes (#546)", () => {
  assert.equal(answeredValue({ fields: [] }, { text: 'ok\u202Egnp.exe' }), 'ok\\u202Egnp.exe');
});
