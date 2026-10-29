import { test } from 'node:test';
import assert from 'node:assert/strict';
import { LaunchService } from '../../js/launch-service.js';
import { HostClient, HostError } from '../../js/host-client.js';
import { checkLaunch, flattenPrompt, readLaunchOptions, readLaunchResult, MAX_PROMPT } from '../../js/launch.js';
import { describeLaunch } from '../../js/ui/launch-view.js';

const flush = () => new Promise((r) => setImmediate(r));
const REQUEST = '2b6e1f0a-7c3d-4e5f-8a9b-0c1d2e3f4a5b';

// A HostClient stand-in: each launch() call waits for the test to settle it.
function fakeClient() {
  const c = {
    closed: false,
    calls: [],
    launch(data) { return new Promise((resolve, reject) => { c.calls.push({ data, resolve, reject }); }); },
    last() { return c.calls[c.calls.length - 1]; },
  };
  return c;
}

function setup() {
  let n = 0;
  const conn = { client: fakeClient(), hostId: 'h1' };
  const service = new LaunchService({ current: () => conn.client && { client: conn.client, hostId: conn.hostId }, uuid: () => `r${++n}`, retryMs: 5 });
  const press = (launch = { agent: 'copilot', workspace: 'Bridge', prompt: 'hi' }) => service.submit({ hostId: 'h1', launch });
  return { conn, service, press };
}

test('a launch is sent once with its request, and a second press while it is on its way sends nothing', async () => {
  const { conn, service, press } = setup();
  press();
  press({ agent: 'codex', workspace: 'Other' });
  assert.deepEqual(conn.client.calls.map((c) => c.data), [{ agent: 'copilot', workspace: 'Bridge', prompt: 'hi', request: 'r1' }]);
  conn.client.last().resolve(readLaunchResult({ outcome: 'started', session: 's9', launched: { agent: 'copilot', workspace: 'Bridge', cwd: 'C:\\b', worktree: true } }));
  await flush();
  const e = service.get('h1');
  assert.equal(e.outcome, 'started');
  assert.equal(e.detail.session, 's9');
  assert.equal(e.detail.launched.worktree, true);
});

test('a launch whose response is lost is resent with the same request, so the machine starts it at most once', async () => {
  const { conn, service, press } = setup();
  press();
  conn.client.last().reject(new HostError('session.launch', 'timed out'));
  await new Promise((r) => setTimeout(r, 20));
  assert.deepEqual(conn.client.calls.map((c) => c.data.request), ['r1', 'r1']);
  conn.client.last().resolve(readLaunchResult({ outcome: 'started', session: 's9' }));
  await flush();
  assert.equal(service.get('h1').outcome, 'started');
});

test('busy on a resend is the launch\'s own outcome, shown rather than resent, since a host returns the first copy\'s outcome to a repeat', async () => {
  const { conn, service, press } = setup();
  press();
  conn.client.last().reject(new HostError('session.launch', 'timed out'));
  await new Promise((r) => setTimeout(r, 20));
  conn.client.last().resolve(readLaunchResult({ outcome: 'busy', reason: 'a session is waiting at its trust prompt' }));
  await new Promise((r) => setTimeout(r, 20));
  assert.equal(conn.client.calls.length, 2);
  assert.equal(service.get('h1').outcome, 'busy');
});

test('a press after any outcome is a new launch with a new request', async () => {
  const { conn, service, press } = setup();
  press();
  conn.client.last().resolve(readLaunchResult({ outcome: 'started', session: 's9' }));
  await flush();
  press();
  assert.equal(conn.client.last().data.request, 'r2');
  assert.equal(service.get('h1').state, 'sending');
});

test('the host client asks for launch.options and sends session.launch, reading what comes back', async () => {
  const sent = [];
  const t = { send(m) { sent.push(m); return Promise.resolve(); }, close() {}, onmessage: null, onclose: null };
  const client = new HostClient(t, { timeoutMs: 1000 });
  const o = client.launchOptions();
  await flush();
  assert.equal(sent[0].op, 'launch.options');
  t.onmessage({ re: sent[0].id, ok: true, result: { agents: [{ id: 'copilot', label: 'Copilot', kind: 'copilot', models: ['m1'] }], workspaces: [{ label: 'B', path: 'C:\\b' }], defaults: { agent: 'copilot', workspace: 'B' } } });
  assert.equal((await o).agents[0].models[0], 'm1');
  const p = client.launch({ request: REQUEST, agent: 'copilot' });
  await flush();
  assert.equal(sent[1].op, 'session.launch');
  t.onmessage({ re: sent[1].id, ok: true, result: { outcome: 'started', session: 's1' } });
  assert.equal((await p).session, 's1');
});

test('an outcome from a newer host reads as unconfirmed, never as refused, since something may have started', () => {
  assert.equal(readLaunchResult({ outcome: 'queued' }).outcome, 'unconfirmed');
  assert.equal(readLaunchResult(null).outcome, 'unconfirmed');
  assert.equal(readLaunchResult({ outcome: 'refused', reason: 'no' }).outcome, 'refused');
});

test('launch options leave out launchers this app does not know and anything unreadable, and fall back to what is offered', () => {
  const o = readLaunchOptions({
    agents: [
      { id: 'gemini', label: 'Gemini', kind: 'gemini' },
      { id: 'codex', label: 'Codex', kind: 'codex', models: ['a', 'a', 7, ''], needsPrompt: true },
      { id: 'agency', label: 'Agency', kind: 'copilot', profiles: ['home'], defaults: { profile: 'home', model: 3 } },
    ],
    workspaces: [{ label: 'B', path: 'C:\\b', isolate: true }, { path: 'no label' }],
    resumable: [{ id: 'x', agent: 'copilot', launcher: 'agency', title: 'T' }, { id: 'y', launcher: 'gemini' }],
    defaults: { agent: 'gemini', workspace: 'gone', permissions: 'yolo' },
  });
  assert.deepEqual(o.agents.map((a) => a.id), ['codex', 'agency']);
  assert.deepEqual(o.agents[0].models, ['a']);
  assert.equal(o.agents[0].needsPrompt, true);
  assert.equal(o.agents[0].profiles, null);
  assert.deepEqual(o.agents[1].profiles, ['home']);
  assert.equal(o.agents[1].defaults.model, null);
  assert.deepEqual(o.workspaces, [{ label: 'B', path: 'C:\\b', isolate: true }]);
  assert.deepEqual(o.resumable.map((r) => r.id), ['x']);
  assert.deepEqual(o.defaults, { agent: 'codex', workspace: 'B', permissions: 'ask' });
  assert.equal(readLaunchOptions(undefined).defaults.agent, null);
});

test('a launch the machine would refuse is caught before it is sent, in the machine\'s words', () => {
  assert.equal(checkLaunch({ agent: 'copilot', workspace: 'B', prompt: 'hello' }), null);
  assert.equal(checkLaunch({ agent: 'copilot', resume: 'x', prompt: '' }), null);
  assert.match(checkLaunch({ agent: 'gemini' }), /unknown agent 'gemini'/);
  assert.match(checkLaunch({ workspace: 'B', resume: 'x' }), /not both/);
  assert.match(checkLaunch({ workspace: 'B', permissions: 'yolo' }), /unknown permissions/);
  assert.match(checkLaunch({ workspace: 'B', model: '--yolo' }), /not a model/);
  assert.equal(checkLaunch({ workspace: 'B', model: '', effort: 'high', context: 'claude-opus-4.6[1m]' }), null);
  assert.match(checkLaunch({ workspace: 'B', prompt: '  /clear' }), /cannot start with \//);
  assert.match(checkLaunch({ workspace: 'B', prompt: '\n!rm -rf' }), /cannot start with !/);
  assert.match(checkLaunch({ workspace: 'B', prompt: 'x'.repeat(MAX_PROMPT + 1) }), /longer than 16 KiB/);
  assert.equal(checkLaunch({ workspace: 'B', prompt: 'x'.repeat(MAX_PROMPT) }), null);
  assert.match(checkLaunch({ workspace: 'B\u0007' }), /control characters/);
  assert.match(checkLaunch({ workspace: 'B', attachments: [{ id: 'nope' }] }), /UUID/);
  assert.match(checkLaunch({ workspace: 'B', attachments: [{ id: REQUEST }, { id: REQUEST.toUpperCase() }] }), /twice/);
});

test('a prompt reaches the agent on one line, as the machine flattens it', () => {
  assert.equal(flattenPrompt('  first\nsecond\u2028third\t '), 'first second third');
  assert.equal(flattenPrompt(undefined), '');
});

test('what a launch says matches its outcome, and a started session says where it went', () => {
  const done = (outcome, extra = {}) => ({ state: 'done', outcome, ...extra });
  assert.deepEqual(describeLaunch(done('started', { detail: { launched: { worktree: true } } })), { text: 'Session started in a new worktree.', tone: 'ok' });
  assert.equal(describeLaunch(done('unconfirmed', { reason: 'it is asking whether to trust its folder; answer that at the machine' })).tone, 'warn');
  assert.match(describeLaunch(done('busy', { reason: 'a session is waiting at its trust prompt' })).text, /^Not launched: a session is waiting at its trust prompt\. Try again/);
  assert.equal(describeLaunch(done('refused', { reason: 'no workspace is approved' })).text, 'Not launched: no workspace is approved.');
  assert.equal(describeLaunch(done('failed', { reason: 'it exited' })).tone, 'bad');
  assert.equal(describeLaunch({ state: 'waiting' }, { machineName: 'Desk' }).tone, 'progress');
  assert.match(describeLaunch({ state: 'sending' }, { machineName: 'Desk' }).text, /on Desk/);
});
