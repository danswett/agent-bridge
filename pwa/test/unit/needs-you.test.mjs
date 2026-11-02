// The Needs you lane's logic (ui.md §6.1, #165): what it lists, in what order, and what each
// entry offers; and the app badge that carries its count.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { needsYou, questionLine, plainLine, delegateOf, wholePrompt, PROMPT_MAX, AppBadge } from '../../js/needs-you.js';
import { readSession } from '../../js/protocol.js';
import { prepareAnswer, tapState, answerRequest } from '../../js/answers.js';

const ANSWERS = ['sessions.read', 'sessions.answer', 'sessions.approve'];
const at = (m) => `2026-11-01T12:${String(m).padStart(2, '0')}:00.000Z`;
const approval = (id, extra = {}) => ({
  id, kind: 'approval', message: 'Allow **shell**?\n\nPush the branch\n\n```\ngit push origin main\n```', askedAt: at(1),
  fields: [{ name: 'decision', type: 'choice', required: true, options: [{ value: 'approve', label: 'Approve once' }, { value: 'deny', label: 'Deny' }] }],
  ...extra,
});
const form = (id, extra = {}) => ({ id, kind: 'form', message: 'Which **database**?\nPick one.', askedAt: at(1), fields: [{ name: 'db', type: 'choice', options: [{ value: 'pg', label: 'PostgreSQL' }] }], ...extra });
const session = (id, question, extra = {}) => readSession({ id, agent: 'copilot', title: `Session ${id}`, status: question ? 'waiting' : 'idle', statusSince: at(0), updatedAt: at(0), activity: {}, question, ...extra });
const entry = (hostId, s) => ({ hostId, session: s });
const lane = (entries, opts = {}) => needsYou(entries, { capabilities: () => ANSWERS, nameOf: (h) => ({ h1: 'Laptop', h2: 'Studio PC' }[h]), ...opts });

test('the lane lists every pending question across machines, oldest first, and nothing else', () => {
  const list = lane([
    entry('h1', session('a', form('qa', { askedAt: at(5) }))),
    entry('h2', session('b', approval('qb', { askedAt: at(2) }))),
    entry('h1', session('idle', null)),
    entry('h2', session('c', form('qc', { askedAt: at(9) }))),
  ]);
  assert.deepEqual(list.map((e) => [e.hostId, e.sessionId, e.questionId]), [['h2', 'b', 'qb'], ['h1', 'a', 'qa'], ['h2', 'c', 'qc']]);
});

test('questions asked at the same moment are ordered by machine, then session, so the lane never shuffles', () => {
  const list = lane([
    entry('h2', session('a', form('q1'))),
    entry('h1', session('b', form('q2'))),
    entry('h1', session('a', form('q3'))),
  ]);
  assert.deepEqual(list.map((e) => `${e.machine || e.sortName}/${e.sessionId}`), ['Laptop/a', 'Laptop/b', 'Studio PC/a']);
});

test('an entry shows the title, machine and agent, when it was asked, and the question\'s first line', () => {
  const [e] = lane([entry('h1', session('a', form('q', { askedAt: at(7) }), { title: 'Rotate the certificates', agent: 'codex' }))], { labelled: true });
  assert.equal(e.title, 'Rotate the certificates');
  assert.equal(e.machine, 'Laptop');
  assert.equal(e.agent, 'Codex');
  assert.equal(e.askedAt, at(7));
  assert.equal(e.line, 'Which database?');
});

test('one machine shown, entries leave the machine out, as the cards do', () => {
  const [e] = lane([entry('h1', session('a', form('q')))], { labelled: false });
  assert.equal(e.machine, '');
});

test('an approval\'s line is its first line of prose, never a code fence', () => {
  assert.equal(questionLine(approval('q')), 'Allow shell?');
  assert.equal(questionLine({ kind: 'approval', message: '```\ngit status\n```' }), 'git status');
});

test('an approval answered in the lane shows its whole prompt as written, so Approve once never allows what is not on screen', () => {
  const message = 'Allow **shell**?\n\n````\nrm -rf build/**/cache && python __init__.py "a  b" ```\n````';
  const [e] = lane([entry('h1', session('a', approval('q', { message })))]);
  assert.equal(e.mode, 'approve');
  assert.equal(e.prompt, message);
});

test('an approval too long to show whole, an edit\'s diff say, opens its session rather than approving in the lane', () => {
  const diff = Array.from({ length: PROMPT_MAX.lines }, (_, i) => `+line ${i}`).join('\n');
  const [lines] = lane([entry('h1', session('a', approval('q', { message: `Allow **edit**?\n\n\`\`\`diff\n${diff}\n\`\`\`` })))]);
  assert.equal(lines.mode, 'open');
  assert.equal(lines.prompt, null);
  const [chars] = lane([entry('h1', session('a', approval('q', { message: `Allow **shell**?\n\n\`\`\`\n${'x'.repeat(PROMPT_MAX.chars)}\n\`\`\`` })))]);
  assert.equal(chars.mode, 'open');
  assert.equal(wholePrompt({ message: 'Allow **shell**?\n\n```\nls\n```' }), true);
});

test('a long first line is cut with an ellipsis, and markdown marks are dropped', () => {
  assert.equal(plainLine('## **Ship** the `relay` _now_?'), 'Ship the relay now?');
  const long = plainLine('x'.repeat(400));
  assert.equal(long.length, 140);
  assert.ok(long.endsWith('\u2026'));
});

test('an approval this device can answer offers Approve once and Deny inline', () => {
  const [e] = lane([entry('h1', session('a', approval('q')))]);
  assert.equal(e.mode, 'approve');
  assert.deepEqual([e.decisions.approve.value, e.decisions.deny.value], ['approve', 'deny']);
});

test('an approval with a table, which scrolls sideways, or from Codex, whose command may be cut short, opens its session', () => {
  const table = 'Allow **sync**?\n\n| path | action |\n|---|---|\n| a | delete |';
  const [withTable] = lane([entry('h1', session('a', approval('q', { message: table })))]);
  assert.equal(withTable.mode, 'open');
  const [codex] = lane([entry('h1', session('a', approval('q'), { agent: 'codex' }))]);
  assert.equal(codex.mode, 'open');
});

test('a form opens its session rather than answering in the lane', () => {
  const [e] = lane([entry('h1', session('a', form('q')))]);
  assert.equal(e.mode, 'open');
  assert.equal(e.decisions, null);
});

test('a prompt marked answerAtMachine says so, with nothing to press, whatever the machine takes', () => {
  const [e] = lane([entry('h1', session('a', approval('q', { answerAtMachine: true })))]);
  assert.equal(e.mode, 'atMachine');
  assert.equal(e.decisions, null);
});

test('an approval on a machine that takes no answers from here, or one cut short, opens its session instead', () => {
  const [readOnly] = lane([entry('h1', session('a', approval('q')))], { capabilities: () => ['sessions.read'] });
  assert.equal(readOnly.mode, 'open');
  const [cut] = lane([entry('h1', session('a', approval('q', { truncated: true })))]);
  assert.equal(cut.mode, 'open');
});

test('a delegate\'s question is labelled with the delegate\'s name from subagents', () => {
  const s = session('a', approval('q', { agent: 'agent-7' }), { waitingOn: 'agent-7', subagents: [{ id: 'agent-3', kind: 'task', status: 'ended', name: 'Explore' }, { id: 'agent-7', kind: 'background', status: 'waiting', name: 'Read witness shell logic' }] });
  const [e] = lane([entry('h1', s)]);
  assert.equal(e.delegate, 'Read witness shell logic');
});

test('the session\'s own question names no delegate, and one the list left out is still a delegate', () => {
  assert.equal(delegateOf(session('a', form('q'))), null);
  assert.equal(delegateOf(session('a', form('q', { agent: 'agent-9' }), { subagentsMore: 3 })), 'A delegate');
});

test('Approve once from the lane is the request the card\'s Approve once button sends', () => {
  const q = session('a', approval('r1#0123')).question;
  const { answer, problem } = prepareAnswer(q, tapState(q, 'approve'), 'a');
  assert.equal(problem, null);
  assert.deepEqual(answerRequest({ sessionId: 'a', questionId: q.id, request: 'r', answer }), { id: 'a', question: 'r1#0123', request: 'r', fields: { decision: 'approve' } });
});

test('the badge follows the lane: set to its count, cleared at none, and only when it changes', async () => {
  const calls = [];
  const nav = { setAppBadge: async (n) => { calls.push(['set', n]); }, clearAppBadge: async () => { calls.push(['clear']); } };
  const badge = new AppBadge(nav);
  badge.set(2);
  badge.set(2);
  badge.set(3);
  badge.set(0);
  assert.deepEqual(calls, [['set', 2], ['set', 3], ['clear']]);
});

test('without the Badging API, or when the browser refuses it, the badge does nothing and throws nothing', async () => {
  const none = new AppBadge({});
  assert.equal(none.supported, false);
  none.set(4);
  const refusing = new AppBadge({ setAppBadge: () => Promise.reject(new Error('NotAllowedError')), clearAppBadge: () => { throw new Error('nope'); } });
  refusing.set(1);
  refusing.set(0);
  await new Promise((r) => setTimeout(r, 0));
});
