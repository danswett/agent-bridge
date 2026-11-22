// #1628: every turn the person starts stays its own entry, in order, whether activity said it first,
// it arrived in a page, or the view was scrolled back when it began.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { DemoHost } from '../../js/demo.js';
import { HostClient } from '../../js/host-client.js';
import { HistoryStore } from '../../js/history-store.js';
import { readSession } from '../../js/protocol.js';
import { currentReply, shownParts } from '../../js/ui/history-view.js';
import { HISTORY, readHistoryResult } from '../../js/history.js';

const ID = 's1';
const turn = (n) => `${ID}-turn-${n}`;
const nums = (snapshot) => snapshot.turns.map((t) => Number(t.id.split('-').at(-1)));
const prompts = (snapshot) => snapshot.turns.map((t) => t.prompt?.text);

test('successive assistant messages keep both replies in order and a third appends without duplicating updates', async (t) => {
  const { store } = await setup(t);
  await store.open('h', ID);
  const say = (responseId, response) => store.live('h', readSession({
    id: ID, agent: 'copilot', activity: { turn: turn(6), responseTurn: turn(6), responseId, response, userMessage: 'Question' },
  }));
  const replies = () => shownParts(store.get('h', ID).turns.at(-1)).filter((p) => p.kind === 'response').map((p) => p.text);
  say('answer', 'Original answer.');
  say('pulse', 'Scheduled pulse.');
  assert.deepEqual(replies(), ['Original answer.', 'Scheduled pulse.']);
  say('pulse', 'Scheduled pulse, complete.');
  say('completion', 'Background task completed.');
  say('completion', 'Background task completed.');
  assert.deepEqual(replies(), ['Original answer.', 'Scheduled pulse, complete.', 'Background task completed.']);
  const current = readSession({ id: ID, activity: { turn: turn(6), responseTurn: turn(6), responseId: 'completion', response: 'Background task completed.' } });
  assert.equal(currentReply(store.get('h', ID), current), 'Original answer.\n\nScheduled pulse, complete.\n\nBackground task completed.');
});

test('an old hosts missing reply identity keeps the legacy single streaming slot', async (t) => {
  const { store } = await setup(t);
  await store.open('h', ID);
  let session;
  for (const response of ['Partial', 'Complete answer']) {
    session = readSession({ id: ID, activity: { turn: turn(6), responseTurn: turn(6), response } });
    store.live('h', session);
  }
  const snapshot = store.get('h', ID);
  assert.equal(currentReply(snapshot, session), 'Complete answer');
  assert.deepEqual(shownParts(snapshot.turns.at(-1)).map((p) => p.text), ['Complete answer']);
});

test('finished history parts replace live slots by identity without duplicating equal replies', async () => {
  let page = [];
  const store = new HistoryStore({ current: () => ({ client: {
    capabilities: [HISTORY.capability],
    history: async () => readHistoryResult({ turns: page }),
  } }) });
  await store.open('h', ID);
  const session = (responseId) => readSession({ id: ID, activity: { turn: 'owner', responseTurn: 'owner', responseId, response: 'Same answer' } });
  store.live('h', session('first'));
  store.live('h', session('second'));
  page = [{ id: 'owner', at: '2026-11-21T22:00:00Z', parts: ['first', 'second'].map((id) => ({
    id, kind: 'response', text: 'Same answer', at: '2026-11-21T22:00:01Z',
  })) }];
  await store.open('h', ID);
  store.live('h', session('second'));
  const snapshot = store.get('h', ID);
  assert.deepEqual(snapshot.turns[0].parts.map((p) => p.id), ['first', 'second']);
  assert.equal(currentReply(snapshot, session('second')), 'Same answer\n\nSame answer');
  assert.ok(snapshot.turns[0].parts.every((p) => p.at === '2026-11-21T22:00:01Z'));
  const stale = session('second');
  store.live('h', { ...stale, activity: { ...stale.activity, response: 'Stale partial', responseTruncated: true } });
  assert.equal(currentReply(store.get('h', ID), stale), 'Same answer\n\nSame answer');
  page = [{ ...page[0], parts: [page[0].parts[0]] }];
  await store.open('h', ID);
  assert.equal(currentReply(store.get('h', ID), session('second')), 'Same answer\n\nSame answer');
});

test('Codex live revisions after history replace the same part without accepting stale snapshots', async () => {
  let page = [];
  let pending = null;
  const store = new HistoryStore({ current: () => ({ client: { capabilities: [HISTORY.capability],
    history: async () => { await pending; return readHistoryResult({ turns: page }); },
  } }) });
  await store.open('h', ID);
  const session = (response, seconds, responseTruncated = false) => readSession({ id: ID, agent: 'codex',
    updatedAt: `2026-11-22T11:00:0${seconds}Z`, activity: { turn: 'owner', responseTurn: 'owner',
      responseId: 'answer', response, responseTruncated } });
  store.live('h', session('Partial answer', 0));
  page = [{ id: 'owner', at: '2026-11-22T11:00:00Z',
    parts: [{ id: 'answer', kind: 'response', text: 'Partial answer', at: '2026-11-22T11:00:00Z' }] }];
  await store.open('h', ID);
  store.live('h', session('Complete answer', 2));
  assert.equal(currentReply(store.get('h', ID), session('Complete answer', 2)), 'Complete answer');
  store.live('h', session('Partial answer', 1));
  assert.equal(currentReply(store.get('h', ID), session('Partial answer', 1)), 'Complete answer');
  store.live('h', session('Cut', 3, true));
  assert.equal(currentReply(store.get('h', ID), session('Cut', 3, true)), 'Complete answer');
  assert.deepEqual(store.get('h', ID).turns[0].parts.map((p) => p.id), ['answer']);
  let release;
  pending = new Promise((resolve) => { release = resolve; });
  const loading = store.open('h', ID);
  await new Promise(setImmediate);
  store.live('h', session('Complete revised answer', 4));
  release();
  await loading;
  assert.equal(currentReply(store.get('h', ID), session('Complete revised answer', 4)), 'Complete revised answer');
});

for (const [reviseSecond, budget] of [[false, false], [true, false], [false, true], [true, true]]) {
  test(`Codex delayed pages preserve each revised item: second revised ${reviseSecond}, budget ${budget}`, async () => {
    let pending = null;
    const page = { id: 'owner', at: '2026-11-22T11:00:00Z', parts: ['first', 'second'].map((id) => ({
      id, kind: 'response', at: '2026-11-22T11:00:00Z', text: id === 'first' ? 'First old' : 'Second old',
    })) };
    const store = new HistoryStore({ current: () => ({ client: { capabilities: [HISTORY.capability],
      history: async () => { await pending; return readHistoryResult({ turns: [
        budget && pending ? { ...page, parts: [page.parts[1]], truncated: true, partsMore: 1 } : page,
      ] }); },
    } }) });
    await store.open('h', ID);
    const say = (responseId, response, seconds) => store.live('h', readSession({ id: ID, agent: 'codex',
      updatedAt: `2026-11-22T11:00:0${seconds}Z`, activity: { turn: 'owner', responseTurn: 'owner', responseId, response } }));
    say('first', 'First old', 0);
    say('second', 'Second old', 1);
    let release;
    pending = new Promise((resolve) => { release = resolve; });
    const loading = store.open('h', ID);
    await new Promise(setImmediate);
    say('first', 'First new', 2);
    say('second', reviseSecond ? 'Second new' : 'Second old', 3);
    release();
    await loading;
    assert.deepEqual(store.get('h', ID).turns[0].parts.map((p) => p.text), ['First new', reviseSecond ? 'Second new' : 'Second old']);
    assert.deepEqual(store.get('h', ID).turns[0].parts.map((p) => p.id), ['first', 'second']);
  });
}

for (const stale of [false, true]) {
  test(`a rejected Codex reply still updates reasoning and tools: stale ${stale}`, async (t) => {
    const { store } = await setup(t);
    await store.open('h', ID);
    const session = readSession({ id: ID, agent: 'codex', updatedAt: '2026-11-22T11:00:02Z',
      activity: { turn: turn(6), responseTurn: turn(6), responseId: 'answer', response: 'Full answer', reasoning: 'Before' } });
    store.live('h', session);
    store.live('h', { ...session, updatedAt: stale ? '2026-11-22T11:00:01Z' : '2026-11-22T11:00:03Z',
      activity: { ...session.activity, response: 'Cut', responseTruncated: !stale,
        reasoning: 'After', currentTool: { id: 'tool', name: 'shell', status: 'running' } } });
    const held = store.get('h', ID).turns.at(-1);
    assert.equal(held.parts[0].text, 'Full answer');
    assert.equal(held.live.reasoning, 'After');
    assert.equal(held.live.currentTool.name, 'shell');
  });
}

test('an accepted truncated Codex revision survives an older full page', async () => {
  let release;
  let pending = null;
  let page = [];
  const store = new HistoryStore({ current: () => ({ client: { capabilities: [HISTORY.capability],
    history: async () => { await pending; return readHistoryResult({ turns: page }); },
  } }) });
  await store.open('h', ID);
  const say = (response, seconds) => store.live('h', readSession({ id: ID, agent: 'codex',
    updatedAt: `2026-11-22T11:00:0${seconds}Z`, activity: { turn: 'owner', responseTurn: 'owner',
      responseId: 'answer', response, responseTruncated: true } }));
  say('Partial old', 1);
  page = [{ id: 'owner', at: '2026-11-22T11:00:00Z',
    parts: [{ id: 'answer', kind: 'response', text: 'Older full answer', at: '2026-11-22T11:00:00Z' }] }];
  pending = new Promise((resolve) => { release = resolve; });
  const loading = store.open('h', ID);
  await new Promise(setImmediate);
  say('Partial new', 2);
  release();
  await loading;
  assert.equal(store.get('h', ID).turns[0].parts[0].text, 'Partial new');
  assert.equal(store.get('h', ID).turns[0].parts[0].truncated, true);
});

for (const cut of [{ truncated: true }, { partsMore: 1 }]) {
  test(`a late budgeted page cannot hide the current identified streaming reply: ${JSON.stringify(cut)}`, async () => {
    const page = { id: 'owner', at: '2026-11-21T22:00:00Z', ...cut,
      parts: [{ id: 'answer', kind: 'response', text: 'Original answer', at: '2026-11-21T22:00:01Z' }] };
    let release;
    let pending = null;
    const store = new HistoryStore({ current: () => ({ client: { capabilities: [HISTORY.capability],
      history: async () => { await pending; return readHistoryResult({ turns: [page] }); },
    } }) });
    await store.open('h', ID);
    pending = new Promise((resolve) => { release = resolve; });
    const fetch = store.open('h', ID);
    await new Promise(setImmediate);
    const session = readSession({ id: ID, activity: { turn: 'owner', responseTurn: 'owner', responseId: 'pulse', response: 'Scheduled pulse' } });
    store.live('h', session);
    release();
    await fetch;
    assert.equal(currentReply(store.get('h', ID), session), 'Original answer\n\nScheduled pulse');
    store.live('h', readSession({ id: ID, activity: { turn: 'next', userMessage: 'Next question' } }));
    assert.deepEqual(shownParts(store.get('h', ID).turns[0]).filter((p) => p.kind === 'response').map((p) => p.text),
      ['Original answer', 'Scheduled pulse']);
  });
}

async function setup(t, { count = 5, ...options } = {}) {
  const host = new DemoHost({ sessions: [{ id: ID, agent: 'copilot', status: 'idle', updatedAt: new Date().toISOString(), activity: { turnCount: count } }] }, { answerDelayMs: 0, history: true });
  const client = new HostClient(host.connect(), { timeoutMs: 2000 });
  t.after(() => client.close());
  await client.hello();
  const store = new HistoryStore({ current: () => ({ client }), ...options });
  return { host, store };
}

const said = (n, text, responseTurn = n - 1) => readSession({
  id: ID, agent: 'copilot', status: 'working', updatedAt: new Date().toISOString(),
  activity: { turn: turn(n), responseTurn: turn(responseTurn), userMessage: text },
});

// What the host serves once it has read the turns, each with the words activity said.
const served = (host, base, upTo, words) => {
  host.historyTurns.set(ID, [...base, ...Object.entries(words).map(([n, text]) => ({ id: turn(n), at: new Date().toISOString(), prompt: { text, truncated: false, from: 'person' }, parts: [], partsMore: 0, truncated: false, unreadable: false }))]);
  host.sessions.get(ID).activity.turnCount = upTo;
};

test('five turns said by activity in ten seconds are five entries in order, and the page that follows keeps them', async (t) => {
  const { host, store } = await setup(t, { count: 5 });
  const base = (await store.open('h', ID)).turns;
  const words = {};
  for (let n = 6; n <= 10; n++) {
    words[n] = `ok ${n}`;
    store.live('h', said(n, `ok ${n}`));
  }
  assert.deepEqual(nums(store.get('h', ID)), [1, 2, 3, 4, 5, 6, 7, 8, 9, 10]);
  assert.deepEqual(prompts(store.get('h', ID)).slice(5), ['ok 6', 'ok 7', 'ok 8', 'ok 9', 'ok 10']);
  const all = (await store.open('h', ID)).turns;
  assert.equal(new Set(all.map((x) => x.id)).size, all.length);
  served(host, base, 10, words);
  const after = await store.open('h', ID);
  assert.deepEqual(nums(after), [1, 2, 3, 4, 5, 6, 7, 8, 9, 10]);
  assert.deepEqual(prompts(after).slice(5), ['ok 6', 'ok 7', 'ok 8', 'ok 9', 'ok 10']);
});

test('turns activity said while a page was behind stay, in order, when the page arrives without them', async (t) => {
  const { store } = await setup(t, { count: 5 });
  await store.open('h', ID);
  for (let n = 6; n <= 8; n++) { store.live('h', said(n, `ok ${n}`)); }
  // The host still serves only the five it had read when it answered.
  const after = await store.open('h', ID);
  assert.deepEqual(nums(after), [1, 2, 3, 4, 5, 6, 7, 8]);
});

test('a reply that arrives while the view is scrolled back is there when the view returns to the latest', async (t) => {
  const { host, store } = await setup(t, { count: 60, keep: 20 });
  await store.open('h', ID);
  await store.loadEarlier('h', ID);
  await store.loadEarlier('h', ID);
  assert.equal(store.get('h', ID).turns.some((x) => x.id === turn(60)), false);
  store.live('h', said(61, 'while scrolled back'));
  served(host, [], 61, { 61: 'while scrolled back' });
  const back = await store.open('h', ID);
  assert.equal(back.turns.at(-1).id, turn(61));
  assert.equal(back.turns.at(-1).prompt.text, 'while scrolled back');
});

test('a turn activity skipped over is filled in by the next page, in its place', async (t) => {
  const { host, store } = await setup(t, { count: 5 });
  const base = (await store.open('h', ID)).turns;
  store.live('h', said(8, 'ok 8'));
  served(host, base, 8, { 6: 'ok 6', 7: 'ok 7', 8: 'ok 8' });
  const after = await store.open('h', ID);
  assert.deepEqual(nums(after), [1, 2, 3, 4, 5, 6, 7, 8]);
  assert.deepEqual(prompts(after).slice(5), ['ok 6', 'ok 7', 'ok 8']);
});

// The owner's PWA, 21:16 PT 9 Nov: A is streaming, B and C arrive within a second, then A's final text.
test('late text of turn A stays under A, in order, while B and C arrive; B and C hold none of it', async (t) => {
  const { store } = await setup(t, { count: 5 });
  await store.open('h', ID);
  const say = (n, replyTo, response, userMessage) => readSession({
    id: ID, agent: 'copilot', status: 'working', updatedAt: new Date().toISOString(),
    activity: { turn: turn(n), responseTurn: turn(replyTo), userMessage, response },
  });
  store.live('h', say(6, 6, 'Messages are conf', 'did my message arrive?'));
  store.live('h', say(7, 6, 'Messages are confirmed once', 'and this one'));
  store.live('h', say(8, 6, 'Messages are confirmed once the host reads them.', 'and this'));
  const held = store.get('h', ID).turns.slice(-3);
  assert.deepEqual(held.map((x) => x.id), [turn(6), turn(7), turn(8)]);
  assert.equal(held[0].live.response, 'Messages are confirmed once the host reads them.');
  assert.equal(held[1].live.response ?? null, null);
  assert.equal(held[2].live.response ?? null, null);
});
