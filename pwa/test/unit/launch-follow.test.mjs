import { test } from 'node:test';
import assert from 'node:assert/strict';
import { LaunchFollow, sameQuestion, samePrompt, sameFolder, BUSY_FOR_MS, BUSY_SLACK_MS, BUSY_AT_LEAST_MS, LATE_FOLLOW_FOR_MS, LATE_START_SLACK_MS } from '../../js/launch-follow.js';

const flush = () => new Promise((r) => setImmediate(r));
const T0 = Date.parse('2026-05-01T12:00:00.000Z');
const iso = (ms) => new Date(ms).toISOString();
const question = (over = {}) => ({ id: 'q1', agent: 'claude', folder: 'C:\\repos\\bridge', askedAt: iso(T0), ...over });

// A LaunchFollow on a clock and timers the test moves, for a Launch page showing h1.
function setup({ showing = true } = {}) {
  const t = { now: T0, timers: [], relists: 0, started: [], loads: 0, showing, pending: [] };
  // The page, in front until the test hides it.
  const heard = [];
  t.doc = { hidden: false, addEventListener: (type, fn) => { if (type === 'visibilitychange') { heard.push(fn); } } };
  t.setHidden = (hidden) => { t.doc.hidden = hidden; for (const fn of heard) { fn(); } };
  t.follow = new LaunchFollow({
    doc: t.doc,
    showing: (hostId) => t.showing && hostId === 'h1',
    relist: () => { t.relists++; t.loads++; return new Promise((resolve) => { t.pending.push(resolve); }); },
    loads: () => t.loads,
    started: (hostId, late) => t.started.push({ hostId, ...late }),
    now: () => t.now,
    setTimer: (fn, ms) => { const timer = { fn, ms, at: t.now + ms, cleared: false }; t.timers.push(timer); return timer; },
    clearTimer: (timer) => { if (timer) { timer.cleared = true; } },
  });
  t.armed = () => t.timers.filter((x) => !x.cleared && !x.fired);
  // Moves the clock on, firing what falls due.
  t.advance = (ms) => {
    t.now += ms;
    for (const x of t.armed()) { if (x.at <= t.now) { x.fired = true; x.fn(); } }
  };
  return t;
}

const busy = (trust = []) => ({ busy: 'Claude in Bridge is asking whether to trust its folder. New launches wait until it\'s answered.', trust });
const unconfirmed = (over = {}) => ({ state: 'done', outcome: 'unconfirmed', request: 'r1', detail: { session: 's9', trust: question() }, ...over });

test('a busy question asked 30 seconds ago is listed again a second after its two minutes, not before', () => {
  const t = setup();
  t.now = T0 + 30000;
  assert.equal(t.follow.listed('h1', busy([question()]), ['s1']), 91000);
  t.advance(90999);
  assert.equal(t.relists, 0);
  t.advance(1);
  assert.equal(t.relists, 1);
});

test('the newest listed question sets the wait when the machine lists more than one', () => {
  const t = setup();
  t.now = T0 + 60000;
  assert.equal(t.follow.listed('h1', busy([question(), question({ id: 'q2', askedAt: iso(T0 + 50000) })]), []), 111000);
});

test('a Bypass Permissions busy, which lists no question, is listed again after two minutes and a second', () => {
  const t = setup();
  assert.equal(t.follow.listed('h1', busy([]), []), BUSY_FOR_MS + BUSY_SLACK_MS);
  // A question whose time can't be read says no more than none does.
  assert.equal(t.follow.listed('h1', busy([question({ askedAt: null })]), []), BUSY_FOR_MS + BUSY_SLACK_MS);
});

test('a question whose two minutes are already up is listed again after five seconds once, then after two minutes and a second', () => {
  const t = setup();
  t.now = T0 + 10 * 60000;
  assert.equal(t.follow.listed('h1', busy([question()]), []), BUSY_AT_LEAST_MS);
  assert.equal(t.follow.listed('h1', busy([question()]), []), BUSY_FOR_MS + BUSY_SLACK_MS);
  // Not busy any more: the next overdue question is listed again soon once more.
  assert.equal(t.follow.listed('h1', { busy: null, trust: [] }, []), null);
  assert.equal(t.follow.listed('h1', busy([question()]), []), BUSY_AT_LEAST_MS);
});

test('the wait is never under five seconds nor over two minutes and a second, whatever the clocks say', () => {
  const t = setup();
  t.now = T0 + BUSY_FOR_MS - 1000;
  assert.equal(t.follow.listed('h1', busy([question()]), []), BUSY_AT_LEAST_MS);
  t.follow.stop();
  t.now = T0 - 10 * 60000;
  assert.equal(t.follow.listed('h1', busy([question()]), []), BUSY_FOR_MS + BUSY_SLACK_MS);
});

test('a machine that is not busy arms nothing, and a listing that is not busy any more cancels the wait', () => {
  const t = setup();
  assert.equal(t.follow.listed('h1', { busy: null, trust: [] }, []), null);
  assert.equal(t.follow.listed('h1', null, []), null);
  assert.equal(t.armed().length, 0);
  t.follow.listed('h1', busy(), []);
  t.follow.listed('h1', { busy: null, trust: [] }, []);
  assert.equal(t.armed().length, 0);
  t.advance(BUSY_FOR_MS * 2);
  assert.equal(t.relists, 0);
});

test('the wait lists nothing once another listing came since, or the page no longer shows that machine', () => {
  const t = setup();
  t.follow.listed('h1', busy(), []);
  t.loads++;
  t.advance(BUSY_FOR_MS + BUSY_SLACK_MS);
  assert.equal(t.relists, 0);
  t.follow.listed('h1', busy(), []);
  t.showing = false;
  t.advance(BUSY_FOR_MS + BUSY_SLACK_MS);
  assert.equal(t.relists, 0);
});

test('stopping cancels the wait and forgets the sessions the machine had', () => {
  const t = setup();
  t.follow.listed('h1', busy(), ['s1']);
  t.follow.stop();
  assert.equal(t.armed().length, 0);
  t.follow.session('h1', { id: 's2', agent: 'claude', status: 'working' });
  assert.equal(t.relists, 0);
});

test('a session new since the machine said it was busy lists it again, and one it already had does not', () => {
  const t = setup();
  t.follow.listed('h1', busy(), ['s1']);
  t.follow.session('h1', { id: 's1', agent: 'claude', status: 'working' });
  assert.equal(t.relists, 0);
  t.follow.session('h2', { id: 's2', agent: 'claude', status: 'working' });
  assert.equal(t.relists, 0);
  t.follow.session('h1', { id: 's2', agent: 'claude', status: 'working' });
  assert.equal(t.relists, 1);
});

test('a new session on a busy machine the page does not show lists nothing', () => {
  const t = setup({ showing: false });
  t.follow.listed('h1', busy(), ['s1']);
  t.follow.session('h1', { id: 's2', agent: 'claude', status: 'working' });
  assert.equal(t.relists, 0);
});

test('sessions registering during a listing make one more listing after it, not one each', async () => {
  const t = setup();
  t.follow.listed('h1', busy(), []);
  t.follow.session('h1', { id: 's1', agent: 'claude', status: 'working' });
  t.follow.session('h1', { id: 's2', agent: 'claude', status: 'working' });
  t.follow.session('h1', { id: 's3', agent: 'claude', status: 'working' });
  assert.equal(t.relists, 1);
  t.pending.shift()();
  await flush();
  assert.equal(t.relists, 2);
  t.pending.shift()();
  await flush();
  assert.equal(t.relists, 2);
});

test('a busy machine is not listed again while the app is in the background, and once when it comes back after its busy can have ended', () => {
  const t = setup();
  t.setHidden(true);
  t.follow.listed('h1', busy([]), ['s1']);
  assert.equal(t.armed().length, 0);
  t.advance(BUSY_FOR_MS + BUSY_SLACK_MS + 60000);
  // Nor does a session registering there list it while hidden.
  t.follow.session('h1', { id: 's2', agent: 'claude', status: 'working' });
  assert.equal(t.relists, 0);
  t.setHidden(false);
  assert.equal(t.relists, 1);
  // Shown again with that listing still on its way: no second one.
  t.setHidden(false);
  assert.equal(t.relists, 1);
  assert.equal(t.armed().length, 0);
});

test('a busy machine hidden partway through its wait is listed again only after what is left of it, once back', () => {
  const t = setup();
  t.now = T0 + 30000;
  t.follow.listed('h1', busy([question()]), []);
  t.advance(30000);
  t.setHidden(true);
  assert.equal(t.armed().length, 0);
  t.advance(10000);
  t.setHidden(false);
  assert.equal(t.relists, 0);
  t.advance(50999);
  assert.equal(t.relists, 0);
  t.advance(1);
  assert.equal(t.relists, 1);
});

test('a session registering in the background during a listing asked before it is still listed for once back', async () => {
  const t = setup();
  t.follow.listed('h1', busy([]), ['s1']);
  t.advance(BUSY_FOR_MS + BUSY_SLACK_MS);
  assert.equal(t.relists, 1);
  t.setHidden(true);
  t.follow.session('h1', { id: 's2', agent: 'claude', status: 'working' });
  // The listing asked before s2 registered comes back still busy.
  t.follow.listed('h1', busy([]), ['s1', 's2']);
  t.pending.shift()();
  await flush();
  assert.equal(t.relists, 1);
  t.setHidden(false);
  assert.equal(t.relists, 2);
});

test('a newer launch on the same machine replaces the one followed, however it goes', () => {
  const t = setup();
  t.follow.follow('h1', unconfirmed(), () => false);
  t.follow.follow('h1', { state: 'sending', outcome: null, request: 'r2', detail: null }, () => false);
  t.follow.session('h1', { id: 's9', agent: 'claude', status: 'working' });
  assert.equal(t.started.length, 0);
  // Each machine follows its own: one on h2 leaves h1's alone.
  t.follow.follow('h1', unconfirmed({ request: 'r3' }), () => false);
  t.follow.follow('h2', unconfirmed({ request: 'r4', detail: { session: 's8' } }), () => false);
  t.follow.session('h1', { id: 's9', agent: 'claude', status: 'working' });
  assert.deepEqual(t.started.map((s) => s.request), ['r3']);
});

test('an unconfirmed launch whose session registers with a Claude agent has started, and its machine is listed again', () => {
  const t = setup();
  assert.equal(t.follow.follow('h1', unconfirmed(), () => false), true);
  t.follow.session('h1', { id: 's9', agent: 'claude', status: 'working' });
  assert.deepEqual(t.started.map((s) => [s.hostId, s.request, s.session]), [['h1', 'r1', 's9']]);
  assert.equal(t.relists, 1);
  // Followed once: its session's next update says nothing more.
  t.follow.session('h1', { id: 's9', agent: 'claude', status: 'idle' });
  assert.equal(t.started.length, 1);
});

test('a late start on a machine the page does not show is reported without listing it again', () => {
  const t = setup({ showing: false });
  t.follow.follow('h1', unconfirmed(), () => false);
  t.follow.session('h1', { id: 's9', agent: 'codex', status: 'working' });
  assert.equal(t.started.length, 1);
  assert.equal(t.relists, 0);
});

test('a Copilot or Agency session registering proves nothing, as it registers before it is ready', () => {
  const t = setup();
  t.follow.follow('h1', unconfirmed(), () => false);
  t.follow.session('h1', { id: 's9', agent: 'copilot', status: 'working' });
  assert.equal(t.started.length, 0);
  // Let go, as the app does: only a Trust answered here for the question its launch met (which
  // the panel keeps) can start it now.
  t.follow.session('h1', { id: 's9', agent: 'claude', status: 'working' });
  assert.equal(t.started.length, 0);
});

test('an unconfirmed launch is not followed when its session was already listed, it named none and met no question, or it is not unconfirmed', () => {
  const t = setup();
  assert.equal(t.follow.follow('h1', unconfirmed(), (id) => id === 's9'), false);
  assert.equal(t.follow.follow('h1', unconfirmed({ request: 'r2', detail: {} }), () => false), false);
  assert.equal(t.follow.follow('h1', unconfirmed({ request: 'r3', outcome: 'busy' }), () => false), false);
  assert.equal(t.follow.follow('h1', unconfirmed({ request: 'r4', state: 'sending' }), () => false), false);
  t.follow.session('h1', { id: 's9', agent: 'claude', status: 'working' });
  assert.equal(t.started.length, 0);
});

// A Codex launch the machine could not confirm, which names no session: Codex has none until it
// registers, as the host follows it by its process.
const codex = (over = {}) => unconfirmed({ detail: { launched: { agent: 'codex', workspace: 'bridge', cwd: 'C:\\repos\\bridge', worktree: false, permissions: 'default' } }, ...over });
const codexSession = (over = {}) => ({ id: 'c1', agent: 'codex', status: 'working', cwd: 'C:\\repos\\bridge', startedAt: iso(T0), ...over });

test('a launch that named no session has started when a new session of its agent registers in its folder', () => {
  const t = setup();
  assert.equal(t.follow.follow('h1', codex(), () => false, () => ['old']), true);
  t.follow.session('h1', codexSession({ cwd: 'c:/repos/bridge/' }));
  assert.deepEqual(t.started.map((s) => [s.hostId, s.request, s.session]), [['h1', 'r1', 'c1']]);
  assert.equal(t.relists, 1);
});

test('a launch that named no session is not started by another folder, another agent, a session listed before, an old one, or another machine', () => {
  const t = setup();
  t.follow.follow('h1', codex(), () => false, () => ['old']);
  t.follow.session('h1', codexSession({ cwd: 'C:\\repos\\other' }));
  t.follow.session('h1', codexSession({ agent: 'claude' }));
  t.follow.session('h1', codexSession({ id: 'old' }));
  t.follow.session('h1', codexSession({ startedAt: iso(T0 - LATE_START_SLACK_MS - 1) }));
  t.follow.session('h1', codexSession({ status: 'ending' }));
  t.follow.session('h2', codexSession());
  assert.equal(t.started.length, 0);
  // Still followed: the one that is its own does.
  t.follow.session('h1', codexSession({ id: 'c2', startedAt: iso(T0 - LATE_START_SLACK_MS) }));
  assert.deepEqual(t.started.map((s) => s.session), ['c2']);
});

test('a launch that named no session is told by its folder only for a new Claude or Codex, never a Copilot\'s, a resumed one or a transferred one', () => {
  const t = setup();
  const launched = { agent: 'agency', workspace: 'bridge', cwd: 'C:\\repos\\bridge' };
  assert.equal(t.follow.follow('h1', unconfirmed({ detail: { launched } }), () => false), false);
  assert.equal(t.follow.follow('h1', unconfirmed({ request: 'r2', detail: { launched: { ...launched, agent: 'copilot' } } }), () => false), false);
  assert.equal(t.follow.follow('h1', unconfirmed({ request: 'r3', detail: { launched: { ...launched, agent: 'codex', resume: 'x' } } }), () => false), false);
  assert.equal(t.follow.follow('h1', unconfirmed({ request: 'r4', detail: { launched: { ...launched, agent: 'codex', transfer: { from: 'h2' } } } }), () => false), false);
  assert.equal(t.follow.follow('h1', unconfirmed({ request: 'r5', detail: { launched: { ...launched, agent: 'codex', cwd: null } } }), () => false), false);
  assert.equal(t.follow.follow('h1', unconfirmed({ request: 'r6', detail: { launched: { ...launched, agent: 'claude' } } }), () => false), true);
});

test('a launch that named no session but met a question is followed for its Trust answered here', () => {
  const t = setup();
  assert.equal(t.follow.follow('h1', unconfirmed({ detail: { trust: question(), launched: { agent: 'copilot', cwd: 'C:\\repos\\bridge' } } }), () => false), true);
  // A Copilot's registering proves nothing, so only the answer here can start it.
  t.follow.session('h1', { id: 'p1', agent: 'copilot', status: 'working', cwd: 'C:\\repos\\bridge' });
  assert.equal(t.started.length, 0);
  assert.deepEqual(t.follow.answered('h1', question(), 'started'), { request: 'r1', session: null, at: T0, question: question() });
});

test('the same folder is either slash, any case, with or without a slash at the end, and never nothing', () => {
  assert.equal(sameFolder('C:\\repos\\bridge', 'c:/Repos/Bridge/'), true);
  assert.equal(sameFolder('C:\\repos\\bridge', 'C:\\repos\\bridge2'), false);
  assert.equal(sameFolder('', ''), false);
  assert.equal(sameFolder(null, undefined), false);
});

test('a page listener goes with its owner\'s signal', () => {
  const removed = [];
  const controller = new AbortController();
  const doc = { hidden: false, addEventListener: (type, fn, options) => { options?.signal?.addEventListener('abort', () => removed.push(type)); } };
  new LaunchFollow({ doc, signal: controller.signal, showing: () => true, relist: () => {}, loads: () => 0, started: () => {} });
  controller.abort();
  assert.deepEqual(removed, ['visibilitychange']);
});

test('the same outcome seen again is not followed again', () => {
  const t = setup();
  t.follow.follow('h1', unconfirmed(), () => false);
  t.follow.session('h1', { id: 's9', agent: 'copilot', status: 'working' });
  assert.equal(t.follow.follow('h1', unconfirmed(), () => false), false);
  t.follow.session('h1', { id: 's9', agent: 'claude', status: 'working' });
  assert.equal(t.started.length, 0);
});

test('a session that registers already ending has not started', () => {
  const t = setup();
  t.follow.follow('h1', unconfirmed(), () => false);
  t.follow.session('h1', { id: 's9', agent: 'claude', status: 'ending' });
  assert.equal(t.started.length, 0);
  t.follow.session('h1', { id: 's9', agent: 'claude', status: 'working' });
  assert.equal(t.started.length, 1);
});

test('following stops silently after thirty minutes, as the machine stops following its launch then', () => {
  const t = setup();
  t.follow.follow('h1', unconfirmed(), () => false);
  t.now += LATE_FOLLOW_FOR_MS + 1;
  t.follow.session('h1', { id: 's9', agent: 'claude', status: 'working' });
  assert.equal(t.started.length, 0);
  t.follow.follow('h1', unconfirmed({ request: 'r2' }), () => false);
  t.now += LATE_FOLLOW_FOR_MS + 1;
  assert.equal(t.follow.answered('h1', question(), 'started'), null);
});

test('another machine\'s session with the same id does not start the launch', () => {
  const t = setup();
  t.follow.follow('h1', unconfirmed(), () => false);
  t.follow.session('h2', { id: 's9', agent: 'claude', status: 'working' });
  assert.equal(t.started.length, 0);
});

test('the same question is the same id, or the same folder, agent and time asked, never the folder alone', () => {
  const q = question();
  assert.equal(sameQuestion(q, question()), true);
  assert.equal(sameQuestion(q, question({ id: 'q2' })), true);
  assert.equal(sameQuestion(q, question({ folder: 'C:\\other', agent: 'codex', askedAt: null })), true);
  assert.equal(sameQuestion(q, question({ id: 'q2', agent: 'codex' })), false);
  assert.equal(sameQuestion(q, question({ id: 'q2', askedAt: iso(T0 + 1000) })), false);
  assert.equal(sameQuestion(q, question({ id: 'q2', folder: 'C:\\other' })), false);
  // With no time asked to tell them apart, a folder and agent are not enough.
  assert.equal(sameQuestion(question({ askedAt: null }), question({ id: 'q2', askedAt: null })), false);
  assert.equal(sameQuestion(q, null), false);
  assert.equal(sameQuestion(null, q), false);
});

test('a box of only spaces counts as empty both ways when checking the message sent is unchanged', () => {
  assert.equal(samePrompt('  ', null), true);
  assert.equal(samePrompt('', ' \n '), true);
  assert.equal(samePrompt('Fix it', 'Fix it'), true);
  assert.equal(samePrompt('Fix it ', 'Fix it'), false);
  assert.equal(samePrompt('  ', 'Fix it'), false);
  assert.equal(samePrompt('Fix it', '   '), false);
});

test('Trust answered here for the launch\'s question hands the launch back, by id or asked again under a new one', () => {
  const t = setup();
  t.follow.follow('h1', unconfirmed(), () => false);
  assert.equal(t.follow.answered('h1', question({ id: 'other', folder: 'C:\\x' }), 'started'), null);
  assert.equal(t.follow.answered('h2', question(), 'started'), null);
  assert.equal(t.follow.answered('h1', question({ id: 'q7' }), 'started')?.session, 's9');
  // Let go once answered.
  t.follow.session('h1', { id: 's9', agent: 'claude', status: 'working' });
  assert.equal(t.started.length, 0);
});

test('Quit answered here for the launch\'s question lets the launch go, and an unconfirmed answer keeps following it', () => {
  const t = setup();
  t.follow.follow('h1', unconfirmed(), () => false);
  assert.equal(t.follow.answered('h1', question(), 'unconfirmed'), null);
  t.follow.session('h1', { id: 's9', agent: 'claude', status: 'working' });
  assert.equal(t.started.length, 1);
  t.follow.follow('h1', unconfirmed({ request: 'r2', detail: { session: 's10', trust: question() } }), () => false);
  assert.equal(t.follow.answered('h1', question(), 'declined'), null);
  t.follow.session('h1', { id: 's10', agent: 'claude', status: 'working' });
  assert.equal(t.started.length, 1);
});

test('clearing forgets every launch followed and cancels the wait', () => {
  const t = setup();
  t.follow.follow('h1', unconfirmed(), () => false);
  t.follow.listed('h1', busy(), []);
  t.follow.clear();
  assert.equal(t.armed().length, 0);
  t.follow.session('h1', { id: 's9', agent: 'claude', status: 'working' });
  assert.equal(t.started.length, 0);
  assert.equal(t.relists, 0);
});
