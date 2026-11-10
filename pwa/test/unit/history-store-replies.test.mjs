// #1628: every turn the person starts stays its own entry, in order, whether activity said it first,
// it arrived in a page, or the view was scrolled back when it began.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { DemoHost } from '../../js/demo.js';
import { HostClient } from '../../js/host-client.js';
import { HistoryStore } from '../../js/history-store.js';
import { readSession } from '../../js/protocol.js';

const ID = 's1';
const turn = (n) => `${ID}-turn-${n}`;
const nums = (snapshot) => snapshot.turns.map((t) => Number(t.id.split('-').at(-1)));
const prompts = (snapshot) => snapshot.turns.map((t) => t.prompt?.text);

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
