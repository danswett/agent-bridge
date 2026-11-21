import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';
import * as launch from '../../js/launch.js';
import * as follow from '../../js/launch-follow.js';
import { ago } from '../../js/ui/time.js';
import { LINGER_READ_MS } from '../../js/ui/receipt.js';
import { LaunchService } from '../../js/launch-service.js';
import { HostClient, HostError } from '../../js/host-client.js';
import { checkLaunch, flattenPrompt, readLaunchOptions, readLaunchResult, readTrustResult, MAX_PROMPT } from '../../js/launch.js';
import { describeLaunch, describeTrust, launchPending, RESUMABLE_INCOMPLETE_NOTE } from '../../js/ui/launch-view.js';

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

test('launch options keep well-formed resumable omissions without requiring a known reason', () => {
  for (const reason of ['deadline', 'a future reason', undefined]) {
    const raw = { omitted: 2, reason, title: 'private title', path: 'private path' };
    assert.deepEqual(readLaunchOptions({ resumableIncomplete: raw }).resumableIncomplete,
      { omitted: 2, reason: reason ?? null });
  }
});

test('the incomplete resumable notice matches the shared client wording', () => {
  assert.equal(RESUMABLE_INCOMPLETE_NOTE, "Some sessions weren't checked in time. Reload choices to try again.");
});

test('launch options drop absent or malformed resumable omissions', () => {
  const olderHost = readLaunchOptions({});
  assert.equal(Object.hasOwn(olderHost, 'resumableIncomplete'), false);
  for (const raw of [
    undefined, null, false, 1, 'deadline', [], {}, { reason: 'deadline' },
    { omitted: 0 }, { omitted: -1 }, { omitted: 1.5 }, { omitted: '2' },
    { omitted: true }, { omitted: NaN }, { omitted: Infinity },
    { omitted: Number.MAX_SAFE_INTEGER + 1 },
  ]) {
    assert.deepEqual(readLaunchOptions({ resumableIncomplete: raw }), olderHost);
  }
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

const transfer = () => ({
  from: '0123456789abcdef0123456789abcdef',
  session: REQUEST,
  command: { body: 'aA0_-', by: 'aA0_-', mac: 'aA0_-' },
});

for (const [field, values, reason] of [
  ['from', [undefined, null, '', 'not-a-device-id', 'A'.repeat(32), `${'a'.repeat(31)}\n`, `${'a'.repeat(32)}\n`],
    'transfer.from must be the device id of the machine the session is on'],
  ['session', [undefined, null, '', 'not-a-uuid', REQUEST.replaceAll('-', '')],
    'transfer.session must be the UUID of a session on that machine'],
  ['command', [undefined, null],
    'transfer.command is missing: a transfer needs a command signed by the device asking for it'],
]) {
  test(`a transfer with an invalid ${field} is refused in the host's words`, () => {
    for (const value of values) {
      assert.equal(checkLaunch({ workspace: 'B', transfer: { ...transfer(), [field]: value } }), reason);
    }
  });
}

for (const [part, max] of [['body', 2048], ['by', 96], ['mac', 64]]) {
  test(`a transfer command's ${part} must be nonempty base64url within the host's limit`, () => {
    for (const value of [undefined, null, 1, [], '', 'a=', 'a+', 'a/', 'a b', 'a\n', 'é', 'a'.repeat(max + 1)]) {
      const t = transfer();
      t.command[part] = value;
      assert.equal(checkLaunch({ workspace: 'B', transfer: t }), 'the signed command is malformed');
    }
    const t = transfer();
    t.command[part] = 'a'.repeat(max);
    t.session = REQUEST.toUpperCase();
    assert.equal(checkLaunch({ workspace: 'B', transfer: t }), null);
  });
}

test('a transfer keeps the host validation order and accepts a well-formed signed command', () => {
  assert.equal(checkLaunch({ workspace: 'B', transfer: transfer() }), null);
  assert.equal(checkLaunch({ transfer: {} }), 'a session from another machine has no folder here; name the workspace it opens in');
  assert.equal(checkLaunch({ resume: REQUEST, transfer: {} }), 'a transferred session is brought from another machine; name a session to resume or one to transfer, not both');
  assert.equal(checkLaunch({ workspace: '', transfer: {} }), 'transfer.from must be the device id of the machine the session is on');
});

test('a prompt reaches the agent on one line, as the machine flattens it', () => {
  assert.equal(flattenPrompt('  first\nsecond\u2028third\t '), 'first second third');
  assert.equal(flattenPrompt(undefined), '');
});

test('what a launch says matches its outcome, and a started session says where it went', () => {
  const done = (outcome, extra = {}) => ({ state: 'done', outcome, ...extra });
  assert.deepEqual(describeLaunch(done('started', { detail: { session: 's1', launched: { worktree: true } } })), { text: 'Session started in a new worktree.', tone: 'ok' });
  // A start whose session is not known has no card to open: it says where to find it (#973).
  assert.equal(describeLaunch(done('started', { detail: { launched: { worktree: true } } })).text, 'Session started in a new worktree. Open it from the list.');
  assert.equal(describeLaunch(done('started'), { session: null }).text, 'Session started. Open it from the list.');
  assert.equal(describeLaunch(done('started'), { session: 's1' }).text, 'Session started.');
  assert.equal(describeLaunch(done('unconfirmed', { reason: 'it is asking whether to trust its folder; answer that at the machine' })).tone, 'warn');
  assert.match(describeLaunch(done('busy', { reason: 'a session is waiting at its trust prompt' })).text, /^Not launched: a session is waiting at its trust prompt\. Try again/);
  assert.equal(describeLaunch(done('refused', { reason: 'no workspace is approved' })).text, 'Not launched: no workspace is approved.');
  assert.equal(describeLaunch(done('failed', { reason: 'it exited' })).tone, 'bad');
  assert.equal(describeLaunch({ state: 'waiting' }, { machineName: 'Desk' }).tone, 'progress');
  assert.match(describeLaunch({ state: 'sending' }, { machineName: 'Desk' }).text, /on Desk/);
});

test('a busy reason keeps host advice once and adds advice only when absent', () => {
  for (const [reason, text] of [
    ['Busy: Codex in X is waiting for a person to answer whether to trust that folder, so nothing was launched. Try again in a couple of minutes.', 'Busy: Codex in X is waiting for a person to answer whether to trust that folder, so nothing was launched. Try again in a couple of minutes.'],
    ['Held: discovery is waiting. Try again in a minute or two.', 'Held: discovery is waiting. Try again in a minute or two.'],
    ['the host is preparing a departure', 'Not launched: the host is preparing a departure. Try again in a moment.'],
    ['Busy: another launch is running', 'Busy: another launch is running. Try again in a moment.'],
    ['the host is busy. TRY AGAIN later!', 'Not launched: the host is busy. TRY AGAIN later!'],
    [null, 'Not launched: the machine is busy with another launch. Try again in a moment.'],
    ['', 'Not launched: the machine is busy with another launch. Try again in a moment.'],
  ]) {
    assert.deepEqual(describeLaunch({ state: 'done', outcome: 'busy', reason }), { text, tone: 'warn' });
  }
});

test('a resume the machine holds back while its discovery is held shows the waiting form, and another busy launch does not', () => {
  const held = 'Held: session discovery is waiting on an unrecognised copilot process (pid 4120), so whether that session is still running cannot be told yet. Try again in a minute or two.';
  const done = (answer, reason) => ({ state: 'done', outcome: 'busy', answer, reason });
  assert.deepEqual(describeLaunch(done({ resume: 's1' }, held), { machineName: 'Desk' }), { text: 'Desk is still finding its sessions. Try Launch again in a moment.', tone: 'progress' });
  assert.deepEqual(describeLaunch(done({ workspace: 'B' }, held), { machineName: 'Desk' }), { text: held, tone: 'warn' });
  assert.match(describeLaunch(done({ resume: 's1' }, 'another launch is running'), { machineName: 'Desk' }).text, /^Not launched: another launch/);
});

test('a resume refused as held is drawn as waiting but the launch region is not busy, as it is done and nothing retries it', () => {
  const held = { state: 'done', outcome: 'busy', answer: { resume: 's1' }, reason: 'Held: discovery is held' };
  assert.equal(describeLaunch(held, { machineName: 'Desk' }).tone, 'progress');
  assert.equal(launchPending(held), false);
  assert.equal(launchPending({ state: 'sending' }), true);
  assert.equal(launchPending({ state: 'waiting' }), true);
  assert.equal(launchPending(null), false);
});

test('a folder-trust question is read whole from options and from a launch it met, and nothing unreadable is shown', () => {
  const q = { id: 'q1', agent: 'codex', kind: 'codex', label: 'Codex', workspace: 'Bridge', folder: 'C:\\Users\\me\\repos\\bridge\\web', root: 'c:\\users\\me\\repos\\bridge', rootNote: 'Note: …root:', warning: 'Trust this folder? …', askedAt: '2026-10-31T16:31:30Z', answerable: true };
  const o = readLaunchOptions({ agents: [], workspaces: [], resumable: [], defaults: {}, trust: [q, { id: 'no folder' }, 'junk'] });
  assert.deepEqual(o.trust, [{ ...q, atMachine: null }]);
  assert.deepEqual(readLaunchOptions({}).trust, []);
  const r = readLaunchResult({ outcome: 'busy', reason: 'Busy: …', trust: { ...q, answerable: 'yes' } });
  assert.equal(r.trust.answerable, false);
  assert.equal(readLaunchResult({ outcome: 'started' }).trust, null);
});

test('a folder-trust answer from a newer host is read as unconfirmed, never as refused', () => {
  assert.deepEqual(readTrustResult({ outcome: 'started', session: 's' }), { outcome: 'started', reason: null, session: 's' });
  assert.equal(readTrustResult({ outcome: 'retyped' }).outcome, 'unconfirmed');
  const q = { label: 'Codex' };
  assert.deepEqual(describeTrust({ state: 'done', choice: 'trust', result: { outcome: 'started' } }, q), { text: 'Trusted. Codex started.', tone: 'ok' });
  assert.deepEqual(describeTrust({ state: 'done', choice: 'trust', result: { outcome: 'unconfirmed', reason: 'Not confirmed: answer at the machine. The question is still in Codex\'s window.' } }, q),
    { text: 'Not confirmed: answer at the machine. The question is still in Codex\'s window.', tone: 'warn' });
  assert.equal(describeTrust({ state: 'done', choice: 'quit', result: { outcome: 'refused', reason: 'Answer at the machine' } }, q).tone, 'bad');
});

const panelSource = readFileSync(new URL('../../js/ui/launch-view.js', import.meta.url), 'utf8')
  .replace(/^import .*;\r?$/gm, '').replace(/^export /gm, '');

function timerPanel(t, hidden = false) {
  let now = 100000;
  let next = 0;
  let intervalTicks = 0;
  let timeoutTicks = 0;
  let jumps = 0;
  let ready = false;
  let entry = null;
  const pending = new Map();
  const listeners = new Set();
  const doc = {
    hidden,
    get visibilityState() { return this.hidden ? 'hidden' : 'visible'; },
    addEventListener(type, fn) { assert.equal(type, 'visibilitychange'); listeners.add(fn); },
    removeEventListener(type, fn) { listeners.delete(fn); },
  };
  const h = (tag, props, ...children) => ({
    ...props, children, dataset: {}, value: props?.value ?? '', textContent: '',
    classList: { add() {}, toggle() {} },
    setAttribute() {}, removeAttribute() {}, replaceChildren() {},
    addEventListener() {}, querySelectorAll() { return []; }, contains() { return false; },
  });
  const schedule = (fn, ms, repeat) => {
    const id = ++next;
    pending.set(id, { fn, ms, repeat, due: now + ms });
    return id;
  };
  const Panel = vm.runInNewContext(`${panelSource}\nLaunchPanel`, {
    ...launch, ...follow, ago, LINGER_READ_MS, h, document: doc, AbortController, TextEncoder,
    Date: class extends Date { static now() { return now; } },
    setInterval: (fn, ms) => schedule(fn, ms, true),
    setTimeout: (fn, ms) => schedule(fn, ms, false),
    clearInterval: (id) => pending.delete(id), clearTimeout: (id) => pending.delete(id),
  });
  const root = { ...h('section'), querySelector: () => h('div') };
  const view = new Panel(root, {
    service: { get: () => entry, watch: () => () => {} },
    jump: () => { jumps++; return ready; },
  });
  view.setTarget({ hostId: 'h1', load: () => new Promise(() => {}) });
  view.setOpen(true);
  t.after(() => view.clear());
  const tick = (ms) => {
    const until = now + ms;
    for (;;) {
      const due = [...pending].filter(([, timer]) => timer.due <= until).sort((a, b) => a[1].due - b[1].due)[0];
      if (!due) { break; }
      const [id, timer] = due;
      now = timer.due;
      if (timer.repeat) { timer.due += timer.ms; intervalTicks++; } else { pending.delete(id); timeoutTicks++; }
      timer.fn();
    }
    now = until;
  };
  const start = () => {
    entry = { hostId: 'h1', request: 'r1', state: 'done', outcome: 'started', detail: { session: 's1' } };
    view.els.open.onclick();
  };
  const show = (visible) => { doc.hidden = !visible; for (const fn of [...listeners]) { fn(); } };
  return {
    view, pending, listeners, start, tick, show, cadence: Panel.recheckMs,
    get intervalTicks() { return intervalTicks; }, get timeoutTicks() { return timeoutTicks; },
    get jumps() { return jumps; }, set ready(value) { ready = value; },
    supersede() { entry.request = 'r2'; },
  };
}

test('jump interval is not scheduled while hidden', (t) => {
  const s = timerPanel(t, true);
  s.start();
  s.tick(5000);
  assert.equal(s.intervalTicks, 0);
  assert.equal(s.jumps, 0);
  assert.equal(s.pending.size, 0);
});

test('jump resumes once on return and ends at the original 15 s deadline', (t) => {
  const s = timerPanel(t, true);
  s.start();
  s.tick(8000);
  s.show(true);
  s.show(true);
  assert.equal(s.pending.size, 1);
  s.tick(250);
  assert.equal(s.jumps, 1);
  s.show(false);
  assert.equal(s.pending.size, 0);
  s.tick(1000);
  assert.equal(s.jumps, 1);
  s.ready = true;
  s.show(true);
  s.tick(250);
  assert.equal(s.jumps, 2);
  assert.equal(s.pending.size, 0);
  assert.equal(s.listeners.size, 0);
  for (const superseded of [false, true]) {
    const late = timerPanel(t, true);
    late.start();
    late.tick(superseded ? 8000 : 15001);
    if (superseded) { late.supersede(); }
    late.ready = true;
    late.show(true);
    late.tick(1000);
    assert.equal(late.jumps, 0);
    assert.equal(late.pending.size, 0);
    assert.equal(late.listeners.size, 0);
  }
});

test('probe timer is not armed while hidden, one round on return', (t) => {
  const s = timerPanel(t, true);
  const calls = [];
  s.view.probeOthers(['h2', 'h3'].map((hostId) => ({
    hostId, load: async () => { calls.push(hostId); return { trust: [] }; },
  })));
  s.tick(60000);
  assert.equal(s.timeoutTicks, 0);
  assert.deepEqual(calls, []);
  assert.equal(s.pending.size, 0);
  s.show(true);
  assert.deepEqual(calls, ['h2', 'h3']);
  s.show(true);
  assert.deepEqual(calls, ['h2', 'h3']);
  assert.equal(s.pending.size, 1);
  s.tick(s.cadence - 1);
  assert.equal(calls.length, 2);
  s.tick(1);
  assert.deepEqual(calls, ['h2', 'h3', 'h2', 'h3']);
  s.show(false);
  assert.equal(s.pending.size, 0);
  const fired = s.timeoutTicks;
  s.tick(60000);
  assert.equal(s.timeoutTicks, fired);
});

test('visibility listener is removed on cancel and on close', (t) => {
  const s = timerPanel(t, true);
  s.start();
  assert.equal(s.listeners.size, 1);
  s.view.setOpen(false);
  assert.equal(s.listeners.size, 0);
  s.show(true);
  s.tick(16000);
  assert.equal(s.jumps, 0);
  let probes = 0;
  const others = [{ hostId: 'h2', load: async () => { probes++; return { trust: [] }; } }];
  s.show(false);
  s.view.probeOthers(others);
  assert.equal(s.listeners.size, 1);
  s.view.probeOthers([]);
  assert.equal(s.listeners.size, 0);
  s.view.setOpen(true);
  s.start();
  s.view.probeOthers(others);
  s.view.clear();
  assert.equal(s.listeners.size, 0);
  s.show(true);
  s.tick(60000);
  assert.equal(s.jumps, 0);
  assert.equal(probes, 0);
  assert.equal(s.pending.size, 0);
});

test('a visible jump succeeds on first try with no timer', (t) => {
  const s = timerPanel(t);
  s.ready = true;
  s.start();
  assert.equal(s.jumps, 1);
  assert.equal(s.pending.size, 0);
  assert.equal(s.listeners.size, 0);
});