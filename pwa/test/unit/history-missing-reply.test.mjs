import { test } from 'node:test';
import assert from 'node:assert/strict';
import { HistoryStore } from '../../js/history-store.js';
import { HISTORY, readHistoryResult } from '../../js/history.js';
import { shownParts } from '../../js/ui/history-view.js';
import { readSession } from '../../js/protocol.js';

const A = { id: 'a', at: '2026-11-11T06:37:19Z', prompt: { text: 'Question A', from: 'person' },
  parts: [0, 1, 2, 3].map((turnId) => ({ id: `a-${turnId}`, turnId, kind: 'response', text: turnId === 3 ? 'Answer A' : '' })) };
const B = { id: 'b', at: '2026-11-11T06:37:59Z', prompt: { text: 'Scheduled B', from: 'schedule', label: 'Watch' },
  parts: [0, 1].map((turnId) => ({ id: `b-${turnId}`, turnId, kind: 'response', text: turnId === 1 ? 'Answer B' : '' })) };
const said = (t, response = null, responseTurn = t.id) => ({ id: 's', agent: 'copilot',
  activity: { turn: t.id, responseTurn, userMessage: t.prompt.text, userMessageFrom: t.prompt.from, response } });
const words = (store) => store.get('h', 's').turns.flatMap((t) => [t.prompt.text, ...shownParts(t).filter((p) => p.kind === 'response' && p.text).map((p) => p.text)]);
async function setup() {
  let page = [];
  let calls = 0;
  let pending = null;
  const client = { capabilities: [HISTORY.capability], history: async () => { calls++; await pending; return readHistoryResult({ turns: page }); } };
  const store = new HistoryStore({ current: () => ({ client }) });
  await store.open('h', 's');
  return { store, serve: (turns) => { page = turns; }, calls: () => calls,
    hold: () => { let release; pending = new Promise((resolve) => { release = resolve; }); return () => { pending = null; release(); }; } };
}
const settle = () => new Promise(setImmediate);

test('the sanitized 26-event order keeps both notice replies and the queued owners own answer', async () => {
  const { store, serve } = await setup();
  // Client projections of CopilotNoticeTests' prelude and zero-based checkpoints 3/13/21,
  // not a second transcript parser or a claim that these four snapshots replay all 26 events.
  const reply = (id, text, ms) => ({ id, kind: 'response', at: new Date(Date.parse(A.at) + ms).toISOString(), text });
  const earlier = { ...A, id: 'earlier-owner', kind: 'user', prompt: { text: 'Earlier owner question.', from: 'person' },
    parts: [reply('earlier-answer', 'Earlier owner answer.', 2)] };
  const notice = { id: 'notice', kind: 'notification', at: reply('', '', 3).at,
    prompt: { text: 'Synthetic completion notice.', from: 'notification' },
    parts: [reply('older-notice-answer', 'Older notification reply.', 5)] };
  const queued = { id: 'fixture-3', kind: 'user', at: reply('', '', 10186).at,
    prompt: { text: 'Queued owner question.', from: 'person' }, parts: [] };
  const progress = reply('fixture-13', 'Previous run progress.', 18809);
  const answer = reply('fixture-21', 'Real owner answer.', 26979);
  const live = (current, responseTurn, response, carried) => store.live('h', readSession({ id: 's',
    activity: { kind: 'user', turn: current.id, userMessage: current.prompt.text, responseTurn, response, notice: carried } }));
  serve([earlier]);
  await store.open('h', 's');
  live(earlier, earlier.id, earlier.parts[0].text, notice);
  for (const checkpoint of [3, 13, 21]) {
    const carried = checkpoint < 13 ? notice : { ...notice, parts: [progress] };
    live(queued, checkpoint === 21 ? queued.id : earlier.id,
      checkpoint === 21 ? answer.text : earlier.parts[0].text, carried);
    const held = store.get('h', 's').turns;
    assert.deepEqual(held.map((t) => t.id), [earlier.id, notice.id, queued.id]);
    assert.equal(held[1].kind, 'notification');
    assert.equal(held[1].prompt.from, 'notification');
    assert.deepEqual(shownParts(held[1]).map((p) => p.text), checkpoint < 13
      ? ['Older notification reply.'] : ['Older notification reply.', 'Previous run progress.']);
    assert.equal(held[2].live?.response ?? null, checkpoint === 21 ? answer.text : null);
    assert.equal(shownParts(held[0])[0].text, 'Earlier owner answer.');
  }
  // A refetch missing the newer notice part cannot erase it; replay cannot reparent it.
  serve([earlier, notice, { ...queued, parts: [answer] }]);
  await store.open('h', 's');
  live(queued, queued.id, answer.text, notice);
  const expected = ['Earlier owner question.', 'Earlier owner answer.', 'Synthetic completion notice.',
    'Older notification reply.', 'Previous run progress.', 'Queued owner question.', 'Real owner answer.'];
  assert.deepEqual(words(store), expected);
  const reloaded = await setup();
  reloaded.serve([earlier, { ...notice, parts: [...notice.parts, progress] }, { ...queued, parts: [answer] }]);
  await reloaded.store.open('h', 's');
  assert.deepEqual(words(reloaded.store), expected);
  reloaded.serve([earlier, notice, { ...queued, parts: [] }]);
  await reloaded.store.open('h', 's');
  assert.deepEqual(words(reloaded.store), expected, 'a stale page cannot erase stored-only answers');
});

test('a malformed or unattributed notice never becomes the owners answer or prompt', () => {
  for (const notice of [{ id: 1 }, { id: 'n', prompt: { text: 'Automatic', from: 4 } }]) {
    const activity = readSession({ id: 's', activity: { response: 'Owner answer', notice } }).activity;
    assert.equal(activity.notice, null);
    assert.equal(activity.response, 'Owner answer');
  }
  const activity = readSession({ id: 's', activity: {
    notice: { id: 'unknown', kind: 'future', truncated: true, prompt: { text: 'Unattributed' }, parts: [] },
  } }).activity;
  assert.equal(activity.notice.kind, 'unknown');
  assert.notEqual(activity.notice.prompt.from, 'person');
  assert.equal(activity.notice.truncated, true);
});

for (const known of [false, true]) {
  test(`a budgeted notice fetches its own history once without completing the queued owner; older part=${known}`, async () => {
    const { store, serve, calls } = await setup();
    const owner = { ...A, parts: [] };
    const notice = { ...B, kind: 'notification', prompt: { text: 'Synthetic completion notice.', from: 'notification' } };
    if (known) {
      serve([owner, { ...notice, parts: [{ id: 'prior', kind: 'response', text: 'Older notification reply.' }] }]);
      await store.open('h', 's');
    }
    serve([owner, notice]);
    const activity = { turn: owner.id, responseTurn: null, userMessage: owner.prompt.text,
      notice: { id: notice.id, at: notice.at, kind: notice.kind, truncated: true } };
    const update = () => store.live('h', readSession({ id: 's', activity }));
    update();
    await settle();
    update();
    await settle();
    assert.equal(calls(), 2 + Number(known), 'initial opens and one event-driven recovery, not a watcher');
    const held = store.get('h', 's').turns;
    assert.equal(held.find((t) => t.id === owner.id).live?.response ?? null, null);
    assert.deepEqual(shownParts(held.find((t) => t.id === notice.id)).filter((p) => p.kind === 'response' && p.text).map((p) => p.text),
      [...(known ? ['Older notification reply.'] : []), 'Answer B']);
  });
}

for (const mode of ['jumped', 'in-order', 'null-gap']) {
  test(`${mode} snapshots retain the question reply before the scheduled prompt`, async () => {
    const { store, serve, calls } = await setup();
    store.live('h', said(A));
    if (mode === 'in-order') { store.live('h', said(A, 'Answer A')); }
    if (mode === 'null-gap') { store.live('h', { id: 's', activity: { turn: null } }); }
    serve([A, B]);
    store.live('h', said(B, 'Answer B'));
    store.live('h', said(B, 'Answer B'));
    await settle();
    assert.deepEqual(words(store), ['Question A', 'Answer A', 'Scheduled B', 'Answer B']);
    assert.equal(calls(), mode === 'in-order' ? 1 : 2, 'one catch-up request, not one per update');
  });
}

test('a provisional turn stays visible while its catch-up page is in flight', async () => {
  const { store, serve, hold, calls } = await setup();
  const release = hold();
  store.live('h', said(A));
  serve([A, B]);
  store.live('h', said(B, 'Answer B'));
  await settle();
  assert.equal(calls(), 2);
  assert.deepEqual(words(store), ['Question A', 'Scheduled B', 'Answer B']);
  release();
  await settle();
  assert.deepEqual(words(store), ['Question A', 'Answer A', 'Scheduled B', 'Answer B']);
});

test('stored-only history retains replies with restarted assistant indexes and empty earlier messages', async () => {
  const { store, serve } = await setup();
  serve([A, B]);
  await store.open('h', 's');
  assert.deepEqual(words(store), ['Question A', 'Answer A', 'Scheduled B', 'Answer B']);
});

test('empty current or earlier activity never replaces reply text', async () => {
  const { store } = await setup();
  store.live('h', said(A, 'Answer A'));
  store.live('h', said(A, ''));
  store.live('h', said(B, '', A.id));
  assert.equal(store.get('h', 's').turns[0].live.response, 'Answer A');
});

test('colliding ids on separate pages keep both replies and retain their keys on refresh', async (t) => {
  const warnings = [];
  t.mock.method(console, 'warn', (...args) => warnings.push(args));
  const a = { ...A, id: 'same' };
  const b = { ...B, id: 'same' };
  let earlier = false;
  const client = { capabilities: [HISTORY.capability], history: async (_id, options) => readHistoryResult(
    options.before ? { turns: [a] } : { turns: earlier ? [a, b] : [b], before: 'older' }) };
  const store = new HistoryStore({ current: () => ({ client }) });
  await store.open('h', 's');
  await store.loadEarlier('h', 's');
  assert.deepEqual(words(store), ['Question A', 'Answer A', 'Scheduled B', 'Answer B']);
  const keys = store.get('h', 's').turns.map((t) => t.id);
  assert.equal(new Set(keys).size, 2);
  assert.deepEqual(warnings, [['Duplicate history turn ids: 1']]);
  earlier = true;
  await store.open('h', 's');
  assert.deepEqual(store.get('h', 's').turns.map((t) => t.id), keys);
});

for (const [both, between] of [[false, false], [true, false], [false, true], [false, 'provisional'], [true, 'latest']]) {
  test(`newest colliding page, overlap=${both}, intervening=${between}, retains reply order`, async (t) => {
    const warnings = [];
    t.mock.method(console, 'warn', (...args) => warnings.push(args));
    const { store, serve } = await setup();
    const middle = { id: 'm', at: '2026-11-11T06:37:39Z', prompt: { text: 'Middle' }, parts: [] };
    serve([{ ...A, id: 'same' }, ...(between === true ? [middle] : [])]);
    await store.open('h', 's');
    if (typeof between === 'string') { store.live('h', said(middle)); }
    serve([...(both ? [{ ...A, id: 'same' }] : []), { ...B, id: 'same' }]);
    if (between === 'provisional') { store.live('h', said({ ...B, id: 'same' })); }
    await store.open('h', 's');
    assert.deepEqual(words(store), ['Question A', 'Answer A', ...(between && between !== 'latest' ? ['Middle'] : []),
      'Scheduled B', 'Answer B', ...(between === 'latest' ? ['Middle'] : [])]);
    assert.deepEqual(warnings, [['Duplicate history turn ids: 1']]);
  });
}
