// The history loader (#292 slice 1): session.history paged by its cursor into one turn store per
// session, merged with live activity by turn id. Run against the demo host, which serves pages the
// way a host does, through a real HostClient.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { DemoHost } from '../../js/demo.js';
import { HostClient } from '../../js/host-client.js';
import { HistoryStore } from '../../js/history-store.js';
import { HISTORY } from '../../js/history.js';
import { readSession } from '../../js/protocol.js';

const ID = 's1';
const ids = (snapshot) => snapshot.turns.map((t) => t.id);
const nums = (snapshot) => snapshot.turns.map((t) => Number(t.id.split('-').at(-1)));
const range = (from, to) => Array.from({ length: to - from + 1 }, (_, i) => from + i);

async function setup(t, { count = 45, history = true, ...options } = {}) {
  const host = new DemoHost({ sessions: [{ id: ID, agent: 'copilot', status: 'idle', updatedAt: new Date().toISOString(), activity: { turnCount: count } }] }, { answerDelayMs: 0, history });
  const client = new HostClient(host.connect(), { timeoutMs: 2000 });
  t.after(() => client.close());
  await client.hello();
  // first: null asks the host's default page, which the older tests were written for.
  const store = new HistoryStore({ current: () => ({ client }), first: null, ...options });
  return { host, client, store };
}

test('opening a session asks for a small newest page, and earlier pages are the full size (#1622)', async (t) => {
  const { store } = await setup(t, { count: 100, first: HISTORY.firstTurns });
  const first = await store.open('h', ID);
  assert.deepEqual(nums(first), range(96, 100));
  assert.equal(first.more, true);
  const second = await store.loadEarlier('h', ID);
  assert.deepEqual(nums(second), range(76, 100));
});

// A session as the sessions store gives it, whose latest turn is `turn`.
const session = (activity) => readSession({ id: ID, agent: 'copilot', status: 'working', updatedAt: new Date().toISOString(), activity });

test('opening a session asks for the newest page, and paging back follows the cursor to the start, oldest turn first', async (t) => {
  const { host, store } = await setup(t);
  const seen = [];
  store.watch('h', ID, (snapshot, change) => seen.push({ kind: change.kind, above: change.above, below: change.below, n: snapshot.turns.length }));
  const first = await store.open('h', ID);
  assert.deepEqual(nums(first), range(26, 45));
  assert.equal(first.more, true);
  assert.equal(first.start, null);
  const second = await store.loadEarlier('h', ID);
  assert.deepEqual(nums(second), range(6, 45));
  const third = await store.loadEarlier('h', ID);
  assert.deepEqual(nums(third), range(1, 45));
  assert.equal(third.more, false);
  assert.equal(third.start.at, third.turns[0].at);
  assert.equal(third.phase, 'ready');
  assert.deepEqual(host.historyLog, [{ id: ID }, { id: ID, before: 'h1.25' }, { id: ID, before: 'h1.5' }]);
  // What a view keeping its place needs: how many turns came in above the first one it had.
  assert.deepEqual(seen.filter((c) => c.kind === 'earlier').map((c) => [c.above, c.below]), [[20, 0], [5, 0]]);
});

test('a turn loaded twice is held once, and the newer copy replaces it in place', async (t) => {
  const { host, store } = await setup(t, { count: 25 });
  await store.open('h', ID);
  await store.loadEarlier('h', ID);
  const before = (await store.open('h', ID)).turns;
  assert.deepEqual(nums({ turns: before }), range(1, 25));
  host.historyTurns.set(ID, before.map((turn) => (turn.id === `${ID}-turn-25` ? { ...turn, prompt: { text: 'edited' } } : turn)));
  const after = await store.open('h', ID);
  assert.deepEqual(ids(after), before.map((turn) => turn.id));
  assert.equal(after.turns.at(-1).prompt.text, 'edited');
});

test('a refresh puts the turns that came since after the ones held, and keeps the earlier ones', async (t) => {
  const { host, store } = await setup(t, { count: 25 });
  await store.open('h', ID);
  await store.loadEarlier('h', ID);
  const all = store.get('h', ID).turns;
  host.historyTurns.set(ID, [...all, ...[26, 27].map((n) => ({ ...all.at(-1), id: `${ID}-turn-${n}`, parts: [] }))]);
  const after = await store.open('h', ID);
  assert.deepEqual(nums(after), range(1, 27));
});

test('live activity makes the latest turn at the end, and a page then replaces it without losing what activity said', async (t) => {
  const { store } = await setup(t, { count: 5 });
  await store.open('h', ID);
  store.live('h', session({ turn: `${ID}-turn-5`, responseTurn: `${ID}-turn-5`, userMessage: 'now', response: 'Almost.', reasoning: 'Thinking.' }));
  const latest = store.get('h', ID).turns.at(-1);
  assert.equal(latest.id, `${ID}-turn-5`);
  assert.deepEqual(latest.live, { response: 'Almost.', responseTruncated: false, reasoning: 'Thinking.', currentTool: null });
  assert.equal(latest.provisional, undefined);

  // A turn the host has not served yet: after every turn held, from activity alone.
  store.live('h', session({ turn: `${ID}-turn-6`, responseTurn: `${ID}-turn-5`, userMessage: 'Then this', response: 'Old reply.', currentTool: { id: 'c', name: 'grep', status: 'running' } }));
  const snap = store.get('h', ID);
  assert.deepEqual(nums(snap), [1, 2, 3, 4, 5, 6]);
  const six = snap.turns.at(-1);
  assert.equal(six.provisional, true);
  assert.equal(six.at, null);
  assert.equal(six.prompt.text, 'Then this');
  // Its reply is the turn responseTurn names, never under the new prompt.
  assert.equal(six.live.response, null);
  assert.equal(snap.turns[4].live.response, 'Old reply.');
  assert.equal(snap.latest, `${ID}-turn-6`);
});

test('the turn that was latest keeps its words but not the tool it was running', async (t) => {
  const { store } = await setup(t, { count: 3 });
  await store.open('h', ID);
  store.live('h', session({ turn: `${ID}-turn-3`, responseTurn: `${ID}-turn-3`, response: 'Done.', currentTool: { id: 'c', name: 'grep', status: 'running' } }));
  assert.equal(store.get('h', ID).turns.at(-1).live.currentTool.name, 'grep');
  store.live('h', session({ turn: `${ID}-turn-4`, responseTurn: `${ID}-turn-3`, response: 'Done.' }));
  const three = store.get('h', ID).turns[2];
  assert.equal(three.live.currentTool, null);
  assert.equal(three.live.response, 'Done.');
});

test('live activity before the first page is replaced by the page, which puts its turns above it', async (t) => {
  const { host, store } = await setup(t, { count: 4 });
  host.historyDelayMs = 20;
  await store.open('h', ID, { session: session({ turn: `${ID}-turn-4`, responseTurn: `${ID}-turn-4`, userMessage: 'live prompt', response: 'live reply' }) });
  const snap = store.get('h', ID);
  assert.deepEqual(nums(snap), [1, 2, 3, 4]);
  assert.equal(snap.turns[3].provisional, undefined);
  assert.equal(snap.turns[3].live.response, 'live reply');
  assert.ok(snap.turns[3].parts.length > 0);
});

test('a host that says no turn id leaves the turns as they are', async (t) => {
  const { store } = await setup(t, { count: 3 });
  await store.open('h', ID);
  const before = store.get('h', ID).turns;
  store.live('h', session({ userMessage: 'no turn id here', response: 'x' }));
  assert.deepEqual(store.get('h', ID).turns, before);
  assert.equal(store.get('h', ID).latest, null);
});

test('a stale cursor keeps what is drawn, asks again without it, and walks back to the turn it held', async (t) => {
  const { host, store } = await setup(t, { count: 70 });
  await store.open('h', ID);
  await store.loadEarlier('h', ID);
  await store.loadEarlier('h', ID);
  assert.deepEqual(nums(store.get('h', ID)), range(11, 70));
  host.historyStale = 1;
  host.historyLog.length = 0;
  const after = await store.loadEarlier('h', ID);
  // The stale answer, then the newest page, then pages back to the page that starts at the turn held.
  assert.deepEqual(host.historyLog.map((r) => r.before ?? null), ['h1.10', null, 'h1.50', 'h1.30']);
  assert.deepEqual(nums(after), range(11, 70));
  assert.equal(after.error, null);
  assert.equal(after.more, true);
  assert.deepEqual(nums(await store.loadEarlier('h', ID)), range(1, 70));
});

test('a stale cursor with new turns past what was drawn walks back to the turns held and keeps them', async (t) => {
  const { host, store } = await setup(t, { count: 70 });
  await store.open('h', ID);
  await store.loadEarlier('h', ID);
  await store.loadEarlier('h', ID);
  assert.deepEqual(nums(store.get('h', ID)), range(11, 70));
  host.sessions.get(ID).activity.turnCount = 110;
  host.historyStale = 1;
  host.historyLog.length = 0;
  const after = await store.loadEarlier('h', ID);
  assert.deepEqual(host.historyLog.map((r) => r.before ?? null), ['h1.10', null, 'h1.90', 'h1.70', 'h1.50', 'h1.30']);
  assert.deepEqual(nums(after), range(11, 110));
  assert.equal(after.more, true);
});

test('a stale cursor with nothing held in reach lets what the host serves take its place', async (t) => {
  const { host, store } = await setup(t, { count: 70, limit: 10 });
  await store.open('h', ID);
  await store.loadEarlier('h', ID);
  host.historyTurns.set(ID, range(201, 230).map((n) => ({ id: `${ID}-turn-${n}`, at: new Date().toISOString(), prompt: { text: `p${n}` }, parts: [] })));
  host.historyStale = 1;
  const after = await store.loadEarlier('h', ID);
  assert.deepEqual(nums(after), range(201, 230));
  assert.equal(after.more, false);
  assert.ok(after.start.at);
});

test('a page that shares only a turn activity said, not one the host served, does not join the turns held', async (t) => {
  const { host, store } = await setup(t, { count: 5 });
  await store.open('h', ID);
  store.live('h', session({ turn: `${ID}-turn-24`, responseTurn: `${ID}-turn-24`, userMessage: 'new', response: 'reply' }));
  host.sessions.get(ID).activity.turnCount = 29;
  const snap = await store.open('h', ID);
  assert.deepEqual(nums(snap), range(10, 29));
  assert.equal(snap.turns.at(-1).live, undefined);
  assert.equal(snap.turns.find((turn) => turn.id === `${ID}-turn-24`).live.response, 'reply');
  assert.equal(snap.more, true);
});

test('a latest turn running a tool stops running it when activity names no latest turn', async (t) => {
  const { store } = await setup(t, { count: 3 });
  await store.open('h', ID);
  store.live('h', session({ turn: `${ID}-turn-3`, responseTurn: `${ID}-turn-3`, currentTool: { id: 'c', name: 'grep', status: 'running' } }));
  store.live('h', session({ userMessage: 'a turn the host could not read' }));
  assert.equal(store.get('h', ID).turns[2].live.currentTool, null);
  store.live('h', session({ turn: `${ID}-turn-4`, userMessage: 'next' }));
  assert.equal(store.get('h', ID).turns[2].live.currentTool, null);
});

test('a latest turn only activity said, left behind when the host has gone many turns on, does not stay after the newest page', async (t) => {
  const { host, store } = await setup(t, { count: 13 });
  await store.open('h', ID);
  store.live('h', session({ turn: `${ID}-turn-14`, userMessage: 'fourteen' }));
  host.sessions.get(ID).activity.turnCount = 48;
  store.live('h', session({ turn: `${ID}-turn-48`, userMessage: 'forty-eight' }));
  const snap = await store.open('h', ID);
  assert.deepEqual(nums(snap), range(29, 48));
  assert.equal(snap.turns.some((turn) => turn.provisional), false);
});

test('a walk after a stale cursor that ends on a page with no turns takes that page\'s start and says it was read in part', async (t) => {
  const { host, client, store } = await setup(t, { count: 45 });
  await store.open('h', ID);
  await store.loadEarlier('h', ID);
  const ask = client.history.bind(client);
  client.history = async (id, options) => (options.before === 'h1.25' ? { turns: [], before: null, start: { at: '2026-11-08T00:00:00.000Z', limited: false }, partial: true } : ask(id, options));
  host.historyStale = 1;
  const snap = await store.loadEarlier('h', ID);
  assert.deepEqual(nums(snap), range(6, 45));
  assert.equal(snap.more, false);
  assert.deepEqual(snap.start, { at: '2026-11-08T00:00:00.000Z', limited: false });
  assert.equal(snap.partial, true);
});

test('a stale cursor leaves what is drawn when the walk cannot go on', async (t) => {
  const { host, client, store } = await setup(t, { count: 70 });
  await store.open('h', ID);
  host.historyStale = 1;
  const ask = client.history.bind(client);
  let calls = 0;
  client.history = (...args) => (++calls === 2 ? Promise.reject(new Error('timed out')) : ask(...args));
  const failed = await store.loadEarlier('h', ID);
  assert.deepEqual(nums(failed), range(51, 70));
  assert.equal(failed.error.on, 'earlier');
  assert.equal(failed.loading, null);
  client.history = ask;
  const retried = await store.loadEarlier('h', ID);
  assert.equal(retried.error, null);
  assert.ok(retried.turns.length > 20);
  assert.equal(new Set(ids(retried)).size, retried.turns.length);
});

test('a session the host no longer lists is missing, and what was drawn stays', async (t) => {
  const { host, store } = await setup(t, { count: 5 });
  await store.open('h', ID);
  host.sessions.delete(ID);
  const snap = await store.open('h', ID);
  assert.equal(snap.phase, 'missing');
  assert.equal(snap.turns.length, 5);
});

test('a host with no history for the session says why, and a page after it still reads', async (t) => {
  const { host, store } = await setup(t, { count: 5 });
  host.historyUnavailable = 'this session\'s transcript can\'t be found';
  const snap = await store.open('h', ID);
  assert.equal(snap.phase, 'unavailable');
  assert.equal(snap.why, 'this session\'s transcript can\'t be found');
  assert.equal(snap.unsupported, false);
  host.historyUnavailable = null;
  assert.equal((await store.open('h', ID)).phase, 'ready');
});

test('a host that does not list sessions.history is sent nothing and is unavailable', async (t) => {
  const { host, store } = await setup(t, { history: false });
  const snap = await store.open('h', ID);
  assert.equal(snap.phase, 'unavailable');
  assert.equal(snap.unsupported, true);
  assert.equal(snap.why, null);
  assert.deepEqual(host.historyLog, []);
  assert.equal(host.calls.includes('session.history'), false);
  assert.equal((await store.loadEarlier('h', ID)).phase, 'unavailable');
});

test('a failed first page is failed with nothing to show, and a failed earlier page keeps the turns and can be asked again', async (t) => {
  const { client, store } = await setup(t, { count: 45 });
  const ask = client.history.bind(client);
  client.history = () => Promise.reject(new Error('timed out'));
  const failed = await store.open('h', ID);
  assert.equal(failed.phase, 'failed');
  assert.equal(failed.error.on, 'newest');
  client.history = ask;
  await store.open('h', ID);
  client.history = () => Promise.reject(new Error('timed out'));
  const kept = await store.loadEarlier('h', ID);
  assert.equal(kept.phase, 'ready');
  assert.equal(kept.error.on, 'earlier');
  assert.equal(kept.turns.length, 20);
  assert.equal(kept.more, true);
  client.history = ask;
  const again = await store.loadEarlier('h', ID);
  assert.equal(again.error, null);
  assert.equal(again.turns.length, 40);
});

test('a turn the host could not read, or cut short, makes the history partial and is kept in its place', async (t) => {
  const { host, store } = await setup(t, { count: 3 });
  const turns = (await store.open('h', ID)).turns;
  host.historyTurns.set(ID, [turns[0], { id: turns[1].id, at: turns[1].at, parts: [], unreadable: true }, { ...turns[2], truncated: true, partsMore: 5 }]);
  const snap = await store.open('h', ID);
  assert.deepEqual(ids(snap), ids({ turns }));
  assert.equal(snap.turns[1].unreadable, true);
  assert.equal(snap.turns[2].truncated, true);
  assert.equal(snap.turns[2].partsMore, 5);
  assert.equal(snap.partial, true);
});

test('pages walked back after a stale cursor that repeat a turn between them hold it once', async (t) => {
  const { host, client, store } = await setup(t, { count: 45 });
  await store.open('h', ID);
  const turn = (n) => ({ id: `${ID}-turn-${n}`, at: new Date().toISOString(), prompt: { text: `p${n}` }, parts: [], partsMore: 0, truncated: false, unreadable: false });
  host.historyStale = 1;
  const ask = client.history.bind(client);
  client.history = async (id, options) => {
    if (!options.before) { return { turns: [turn(102), turn(103)], before: 'k1', start: null, partial: false }; }
    if (options.before === 'k1') { return { turns: [turn(101), turn(102)], before: null, start: { at: null, limited: true }, partial: false }; }
    return ask(id, options);
  };
  const snap = await store.loadEarlier('h', ID);
  assert.deepEqual(nums(snap), [101, 102, 103]);
});

test('about 500 turns are kept: the host\'s whole window loads, and its start is said to be limited', async (t) => {
  const { store } = await setup(t, { count: 600, limit: 50 });
  let snap = await store.open('h', ID);
  while (snap.more) { snap = await store.loadEarlier('h', ID); }
  assert.equal(snap.turns.length, 500);
  assert.deepEqual(nums(snap), range(101, 600));
  assert.deepEqual(snap.start, { at: null, limited: true });
});

test('past the limit the end farthest from where turns came in is let go, and loaded again by scrolling back', async (t) => {
  const { host, store } = await setup(t, { count: 12, keep: 6, limit: 3 });
  await store.open('h', ID);
  await store.loadEarlier('h', ID);
  assert.deepEqual(nums(store.get('h', ID)), range(7, 12));
  // Earlier turns coming in past the limit let go of the newest.
  const over = await store.loadEarlier('h', ID);
  assert.deepEqual(nums(over), range(4, 9));
  assert.equal(over.atLatest, false);
  assert.equal(over.more, true);
  // The newest are not there to add a new turn to.
  store.live('h', session({ turn: `${ID}-turn-13`, responseTurn: `${ID}-turn-13`, userMessage: 'new' }));
  assert.deepEqual(nums(store.get('h', ID)), range(4, 9));
  // Opening brings the newest back, and what was held no longer follows from them.
  const back = await store.open('h', ID);
  assert.deepEqual(nums(back), range(10, 12));
  assert.equal(back.atLatest, true);

  // Turns coming in at the new end let go of the old, in whole pages, which load again.
  await store.loadEarlier('h', ID);
  assert.deepEqual(nums(store.get('h', ID)), range(7, 12));
  host.historyTurns.set(ID, undefined);
  store.live('h', session({ turn: `${ID}-turn-13`, responseTurn: `${ID}-turn-13`, userMessage: 'new' }));
  const trimmed = store.get('h', ID);
  assert.deepEqual(nums(trimmed), range(10, 13));
  assert.equal(trimmed.more, true);
  assert.equal(trimmed.start, null);
  // Loading them again lets go of the newest, to keep to the limit.
  const again = await store.loadEarlier('h', ID);
  assert.deepEqual(nums(again), range(7, 12));
  assert.equal(again.atLatest, false);
});

test('the limit holds with no cursor to cut at: the oldest turns go and the history reads as ending there', async (t) => {
  const { store } = await setup(t, { count: 8, keep: 5 });
  const snap = await store.open('h', ID);
  assert.deepEqual(nums(snap), range(4, 8));
  assert.equal(snap.more, false);
  assert.deepEqual(snap.start, { at: null, limited: true });
});

test('turns stay in the order the host gave them, whatever order the pages are asked for', async (t) => {
  const { store } = await setup(t, { count: 100, limit: 7 });
  let snap = await store.open('h', ID);
  for (let i = 0; i < 5; i++) {
    snap = await store.loadEarlier('h', ID);
    await store.open('h', ID);
  }
  snap = store.get('h', ID);
  assert.deepEqual(nums(snap), range(100 - snap.turns.length + 1, 100));
});

test('what is on its way or queued when the session is forgotten is neither sent nor applied, and tells no one', async (t) => {
  const { host, store } = await setup(t, { count: 45 });
  await store.open('h', ID);
  host.historyDelayMs = 30;
  const events = [];
  store.watch('h', ID, (snapshot, change) => events.push(change.kind));
  store.onAny(({ change }) => events.push(`any ${change.kind}`));
  const running = store.loadEarlier('h', ID);
  const queued = store.open('h', ID);
  await new Promise((resolve) => { setTimeout(resolve, 10); });
  const asked = host.historyLog.length;
  events.length = 0;
  store.forget('h', ID);
  await Promise.all([running, queued]);
  await new Promise((resolve) => { setTimeout(resolve, 50); });
  assert.equal(asked, 2);
  assert.equal(host.historyLog.length, 2);
  assert.deepEqual(events, []);
  assert.equal(store.get('h', ID).phase, 'idle');
});

test('asking for the same page before the one ahead has started sends one request', async (t) => {
  const { host, store } = await setup(t, { count: 45 });
  const [a, b] = await Promise.all([store.open('h', ID), store.open('h', ID)]);
  assert.equal(host.historyLog.length, 1);
  assert.deepEqual(ids(a), ids(b));
});

test('a refresh asked for while the newest page is on its way is asked again, so a turn that ended meanwhile comes whole', async (t) => {
  const { host, store } = await setup(t, { count: 3 });
  host.historyDelayMs = 20;
  const first = store.open('h', ID);
  await new Promise((resolve) => { setTimeout(resolve, 5); });
  const second = store.open('h', ID);
  const one = await first;
  host.historyTurns.set(ID, one.turns.map((turn) => (turn.id === `${ID}-turn-3` ? { ...turn, parts: [...turn.parts, { id: 'late', kind: 'response', at: turn.at, text: 'Finished.' }] } : turn)));
  const two = await second;
  assert.equal(host.historyLog.length, 2);
  assert.equal(two.turns.at(-1).parts.at(-1).text, 'Finished.');
});

test('an earlier page asked for while one is on its way is that page, not another', async (t) => {
  const { host, store } = await setup(t, { count: 100 });
  await store.open('h', ID);
  host.historyDelayMs = 20;
  const a = store.loadEarlier('h', ID);
  await new Promise((resolve) => { setTimeout(resolve, 5); });
  const b = store.loadEarlier('h', ID);
  const [x, y] = await Promise.all([a, b]);
  assert.equal(host.historyLog.length, 2);
  assert.equal(x.turns.length, 40);
  assert.equal(y.turns.length, 40);
});

test('a page with no turns and no cursor ends the history there, with its start and that it was read in part', async (t) => {
  const { client, store } = await setup(t, { count: 45 });
  await store.open('h', ID);
  const ask = client.history.bind(client);
  client.history = async (id, options) => (options.before ? { turns: [], before: null, start: { at: null, limited: true }, partial: true } : ask(id, options));
  const snap = await store.loadEarlier('h', ID);
  assert.equal(snap.more, false);
  assert.deepEqual(snap.start, { at: null, limited: true });
  assert.equal(snap.partial, true);
  assert.equal(snap.turns.length, 20);
});

test('the turn that was latest stops running its tool when the next one is already held from a page', async (t) => {
  const { host, store } = await setup(t, { count: 5 });
  await store.open('h', ID);
  store.live('h', session({ turn: `${ID}-turn-5`, responseTurn: `${ID}-turn-5`, currentTool: { id: 'c', name: 'grep', status: 'running' } }));
  host.historyTurns.set(ID, [...store.get('h', ID).turns.map(({ live, ...turn }) => turn), { id: `${ID}-turn-6`, at: new Date().toISOString(), prompt: { text: 'next' }, parts: [] }]);
  await store.open('h', ID);
  store.live('h', session({ turn: `${ID}-turn-6`, responseTurn: `${ID}-turn-5` }));
  assert.equal(store.get('h', ID).turns[4].live.currentTool, null);
});
