import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { readSession, readHello } from '../../js/protocol.js';

const here = (p) => fileURLToPath(new URL(p, import.meta.url));

test('the demo fixture is the protocol\'s worked example, unchanged', () => {
  const fixture = readFileSync(here('../../fixtures/sessions-list.json'), 'utf8').replace(/\r\n/g, '\n');
  const example = readFileSync(here('../../../docs/protocol/examples/sessions-list.json'), 'utf8').replace(/\r\n/g, '\n');
  assert.equal(fixture, example);
});

test('every session in the worked example reads with its fields intact', () => {
  const { sessions } = JSON.parse(readFileSync(here('../../fixtures/sessions-list.json'), 'utf8'));
  const [copilot, claude, codex] = sessions.map(readSession);
  assert.equal(copilot.agent, 'copilot');
  assert.equal(copilot.status, 'waiting');
  assert.equal(copilot.question.fields.length, 5);
  assert.deepEqual(copilot.question.fields.map((f) => f.type), ['choice', 'multi', 'boolean', 'text', 'number']);
  assert.equal(copilot.question.fields[1].options[0].label, 'host', 'an option without a label shows its value');
  assert.equal(claude.activity.currentTool.name, 'Bash');
  assert.equal(codex.status, 'idle');
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
});
