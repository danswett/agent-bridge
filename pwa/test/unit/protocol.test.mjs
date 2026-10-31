import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { readSession, readHello, readBackground } from '../../js/protocol.js';
import { cardState } from '../../js/ui/session-card.js';

const here = (p) => fileURLToPath(new URL(p, import.meta.url));

test('the demo fixture is the protocol\'s worked example, unchanged', () => {
  const fixture = readFileSync(here('../../fixtures/sessions-list.json'), 'utf8').replace(/\r\n/g, '\n');
  const example = readFileSync(here('../../../docs/protocol/examples/sessions-list.json'), 'utf8').replace(/\r\n/g, '\n');
  assert.equal(fixture, example);
});

test('every session in the worked example reads with its fields intact', () => {
  const { sessions } = JSON.parse(readFileSync(here('../../fixtures/sessions-list.json'), 'utf8'));
  const [copilot, claude, codex, delegating] = sessions.map(readSession);
  assert.equal(copilot.agent, 'copilot');
  assert.equal(copilot.status, 'waiting');
  assert.equal(copilot.question.fields.length, 5);
  assert.deepEqual(copilot.question.fields.map((f) => f.type), ['choice', 'multi', 'boolean', 'text', 'number']);
  assert.equal(copilot.question.fields[1].options[0].label, 'host', 'an option without a label shows its value');
  assert.equal(claude.activity.currentTool.name, 'Bash');
  assert.equal(codex.status, 'idle');
  assert.equal(codex.activity.background, null);
  assert.equal(delegating.status, 'idle');
  assert.deepEqual(delegating.activity.background, { agents: 2, shells: 1 });
});

test('background is read tolerantly: anything but whole counts above zero is none, and none at all is absent', () => {
  assert.deepEqual(readBackground({ agents: 1, shells: 0 }), { agents: 1, shells: 0 });
  assert.deepEqual(readBackground({ shells: 3, extra: true }), { agents: 0, shells: 3 });
  assert.deepEqual(readBackground({ agents: -2, shells: 1.5 }), null);
  assert.deepEqual(readBackground({ agents: '2', shells: Infinity }), null);
  assert.equal(readBackground({ agents: 0, shells: 0 }), null);
  assert.equal(readBackground([1, 2]), null);
  assert.equal(readBackground(undefined), null);
  assert.equal(readSession({ id: 'x', activity: { background: 'lots' } }).activity.background, null);
});

test('only an idle session with background work reads as delegating, naming agents before commands', () => {
  const s = (status, background) => readSession({ id: 'x', status, activity: { background } });
  assert.deepEqual(cardState(s('idle', { agents: 2, shells: 1 })), { view: 'delegating', text: 'Waiting for background agents' });
  assert.deepEqual(cardState(s('idle', { agents: 0, shells: 1 })), { view: 'delegating', text: 'Waiting for background commands' });
  assert.deepEqual(cardState(s('idle', { agents: 0, shells: 0 })), { view: 'idle', text: 'Idle' });
  assert.deepEqual(cardState(s('working', { agents: 1 })), { view: 'working', text: 'Working' });
  assert.deepEqual(cardState(s('waiting', { shells: 1 })), { view: 'waiting', text: 'Waiting on you' });
  // A newer status a client doesn't know reads as idle, and then background still counts.
  assert.deepEqual(cardState(s('paused', { agents: 1 })), { view: 'delegating', text: 'Waiting for background agents' });
});

test('a session is the person\'s unless the host says an agent drives it, and only then has a principal', () => {
  // An older host sends no driver at all.
  assert.deepEqual([readSession({ id: 'x' }).driver, readSession({ id: 'x' }).driverPrincipal], ['person', null]);
  assert.equal(readSession({ id: 'x', driver: 'robot' }).driver, 'person');
  assert.equal(readSession({ id: 'x', driver: 3 }).driver, 'person');
  const agent = readSession({ id: 'x', driver: 'agent', driverPrincipal: 'homeassistant:0f1e2d3c' });
  assert.deepEqual([agent.driver, agent.driverPrincipal], ['agent', 'homeassistant:0f1e2d3c']);
  assert.equal(readSession({ id: 'x', driver: 'person', driverPrincipal: 'homeassistant:0f1e2d3c' }).driverPrincipal, null);
  assert.equal(readSession({ id: 'x', driver: 'agent', driverPrincipal: 7 }).driverPrincipal, null);
});

test('unknown enum values read as the C# reader reads them', () => {
  const s = readSession({
    id: 'x', agent: 'gemini', status: 'thinking', updatedAt: '',
    activity: { currentTool: { id: 't', name: 'n', status: 'queued' }, history: [{ id: 'h', name: 'n', status: 'cancelled' }] },
    question: { id: 'q', kind: 'wizard', message: 'm', fields: [{ name: 'f', type: 'date' }] },
  });
  assert.equal(s.agent, 'unknown');
  assert.equal(s.status, 'idle');
  assert.equal(s.activity.currentTool.status, 'done');
  assert.equal(s.activity.history[0].status, 'done');
  assert.equal(s.question.kind, 'form');
  assert.equal(s.question.fields[0].type, 'text');
});

test('fields of the wrong type read as absent, and unknown fields are dropped', () => {
  const s = readSession({ id: 'x', title: { html: '<b>' }, processId: 'NaN', extra: 1, activity: { response: 5, turnCount: '3' }, question: { id: 'q', truncated: 'yes', fields: 'none' } });
  assert.equal(s.title, null);
  assert.equal(s.processId, null);
  assert.equal(s.activity.response, null);
  assert.equal(s.activity.turnCount, null);
  assert.equal(s.question.truncated, false);
  assert.deepEqual(s.question.fields, []);
  assert.equal('extra' in s, false);
});

test('a session without an id cannot be filed and reads as null', () => {
  assert.equal(readSession({ title: 'x' }), null);
  assert.equal(readSession(null), null);
  assert.equal(readSession([]), null);
});

test('a truncated question is read as truncated', () => {
  assert.equal(readSession({ id: 'x', question: { id: 'q', message: 'm', fields: [], truncated: true } }).question.truncated, true);
});

test('hello keeps only string capabilities', () => {
  const h = readHello({ protocol: 1, machine: { name: 'M' }, capabilities: ['sessions.read', 3, null] });
  assert.deepEqual(h.capabilities, ['sessions.read']);
  assert.equal(h.machine.name, 'M');
  assert.equal(h.machine.readOnly, null);
});

test('hello carries why a machine is read-only, when it says', () => {
  assert.equal(readHello({ protocol: 1, machine: { readOnly: 'agent-ha-bridge is running for this user' }, capabilities: [] }).machine.readOnly, 'agent-ha-bridge is running for this user');
  assert.equal(readHello({ protocol: 1, machine: { readOnly: 7 }, capabilities: [] }).machine.readOnly, null);
});
