// What a session row says: docs/design/ui.md §2.2, one test (or more) per rule, named after it.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readSession } from '../../js/protocol.js';
import { sessionStatus, statusLabel } from '../../js/status.js';

// A session as a host sends it, read as the views read it.
const session = (fields = {}) => readSession({ id: 's1', agent: 'copilot', title: 'Plan Migration to Windows App', status: 'idle', ...fields });
const said = (fields) => {
  const { rule, glyph, text, tone } = sessionStatus(session(fields));
  return { rule, glyph, text, tone };
};

const delegate = (id, fields = {}) => ({ id, kind: 'background', name: `Delegate ${id}`, status: 'working', ...fields });

// The protocol's worked example (docs/protocol/v1-sessions.md, examples/session-subagents.json):
// a delegate's permission prompt while another delegate works and a nested task has finished.
const example = {
  status: 'waiting',
  activity: { background: { agents: 2, shells: 0 } },
  question: { id: 'q1', kind: 'approval', message: 'Allow **witness-logic** to run `gh pr checks 182 --watch`?', fields: [], agent: 'agent-c204' },
  subagents: [
    { id: 'agent-c204', kind: 'background', name: 'Read witness shell logic', status: 'waiting' },
    { id: 'agent-3a91', kind: 'background', name: 'Run full test suite', status: 'working' },
    { id: 'agent-7e55', parent: 'agent-c204', kind: 'task', name: 'Review the witness change', status: 'ended', outcome: 'done' },
  ],
  subagentsMore: 2,
  waitingOn: 'agent-c204',
};

test('Rule 1: the session\'s own question reads "Waiting on you" with the needs glyph, in needs\' words', () => {
  assert.deepEqual(said({ status: 'waiting', question: { id: 'q', message: 'Go?' } }), { rule: 1, glyph: 'needs', text: 'Waiting on you', tone: 'needs' });
});

test('Rule 1: delegates still running beside the question are counted, with a number before the noun', () => {
  assert.equal(said({ status: 'waiting', question: { id: 'q' }, activity: { background: { agents: 2, shells: 1 } } }).text, 'Waiting on you · 2 delegates still running');
  assert.equal(said({ status: 'waiting', question: { id: 'q' }, subagents: [delegate('a', { kind: 'task' }), delegate('b', { status: 'ended' })] }).text, 'Waiting on you · 1 delegate still running');
  // An idle background delegate waits to be woken: it is not running.
  assert.equal(said({ status: 'waiting', question: { id: 'q' }, subagents: [delegate('a', { status: 'idle' })] }).text, 'Waiting on you');
  // The host counts background agents, untrimmed; running tasks are added from the list, and a
  // listed background agent is never counted twice. Ten background agents and a task, with the
  // task and seven of the ten listed, are eleven.
  const trimmed = [delegate('t', { kind: 'task' }), ...Array.from({ length: 7 }, (_, i) => delegate(`b${i}`))];
  assert.equal(said({ status: 'waiting', question: { id: 'q' }, activity: { background: { agents: 10, shells: 0 } }, subagents: trimmed, subagentsMore: 3 }).text, 'Waiting on you · 11 delegates still running');
});

test('Rule 1: a question only the machine can answer says to answer it there', () => {
  assert.equal(said({ status: 'waiting', question: { id: 'q', answerAtMachine: true } }).text, 'Waiting on you · answer at the machine');
  assert.equal(said({ status: 'waiting', question: { id: 'q', answerAtMachine: true }, activity: { background: { agents: 1, shells: 0 } } }).text, 'Waiting on you · 1 delegate still running · answer at the machine');
});

test('Rule 1 wins over rule 2: the session\'s own question is the session\'s even while a delegate waits too', () => {
  const own = { ...example, waitingOn: undefined, question: { id: 'q', message: 'Mine' } };
  assert.equal(said(own).rule, 1);
  assert.equal(said(own).text, 'Waiting on you · 2 delegates still running');
});

test('Rule 2: a delegate\'s question reads "<name> asks", named from the delegate list', () => {
  assert.deepEqual(said(example), { rule: 2, glyph: 'needs', text: 'Read witness shell logic asks', tone: 'needs' });
  // The question's agent names it as waitingOn does.
  assert.equal(said({ ...example, waitingOn: undefined }).text, 'Read witness shell logic asks');
});

test('Rule 2: a delegate\'s question only the machine can answer reads "<name> asks · answer at the machine"', () => {
  assert.equal(said({ ...example, question: { ...example.question, answerAtMachine: true } }).text, 'Read witness shell logic asks · answer at the machine');
});

test('Rule 2: a delegate the list left out still asks, without a name the host never gave', () => {
  assert.equal(said({ ...example, subagents: [], waitingOn: 'agent-gone', question: { ...example.question, agent: 'agent-gone' } }).text, 'A delegate asks');
});

test('Rule 3: the main agent\'s turn reads "Working" with the working glyph, in plain words', () => {
  assert.deepEqual(said({ status: 'working', activity: { background: { agents: 1, shells: 2 } } }), { rule: 3, glyph: 'working', text: 'Working', tone: 'plain' });
});

test('Rule 3: a foreground task agent running reads "Working · task: <name>"', () => {
  const subagents = [
    { id: 't2', parent: 't1', kind: 'task', name: 'Bisect RestartAfterResume', status: 'working' },
    { id: 't1', kind: 'task', name: 'Find the flaky test', status: 'working' },
    { id: 'b1', kind: 'background', name: 'Run full test suite', status: 'working' },
  ];
  assert.equal(said({ status: 'working', subagents }).text, 'Working · task: Bisect RestartAfterResume');
});

test('Rule 3: a task a background delegate started, or one that has finished, is not the main agent\'s', () => {
  const subagents = [
    { id: 't9', parent: 'b1', kind: 'task', name: 'Check the witness', status: 'working' },
    { id: 'b1', kind: 'background', name: 'Read witness shell logic', status: 'working' },
    { id: 't1', kind: 'task', name: 'Done already', status: 'ended', outcome: 'done' },
  ];
  assert.equal(said({ status: 'working', subagents }).text, 'Working');
  // A parent the list left out can't be shown to be the main agent's.
  assert.equal(said({ status: 'working', subagents: [{ id: 't3', parent: 'missing', kind: 'task', name: 'Orphan', status: 'working' }] }).text, 'Working');
});

test('Rule 4: the main agent idle with background agents reads "Delegating to N agents" with the delegating glyph', () => {
  assert.deepEqual(said({ activity: { background: { agents: 3, shells: 1 } }, subagents: [delegate('a'), delegate('b'), delegate('c')] }),
    { rule: 4, glyph: 'delegating', text: 'Delegating to 3 agents', tone: 'plain' });
  assert.equal(said({ activity: { background: { agents: 1, shells: 0 } }, subagents: [delegate('a')] }).text, 'Delegating to 1 agent');
});

test('Rule 4: a nested delegate counts once, under its parent, and a list cut short still counts them all', () => {
  const nested = [delegate('a'), delegate('b', { parent: 'a' })];
  assert.equal(said({ activity: { background: { agents: 2, shells: 0 } }, subagents: nested }).text, 'Delegating to 2 agents');
  assert.equal(said({ activity: { background: { agents: 5, shells: 0 } }, subagents: [], subagentsMore: 5 }).text, 'Delegating to 5 agents');
});

test('Rule 5: the main agent idle with only background commands reads "N background commands"', () => {
  assert.deepEqual(said({ activity: { background: { agents: 0, shells: 2 } } }), { rule: 5, glyph: 'delegating', text: '2 background commands', tone: 'plain' });
  assert.equal(said({ activity: { background: { agents: 0, shells: 1 } } }).text, '1 background command');
});

test('Rule 6: nothing running reads "Idle" with the idle glyph, and so does a status a client doesn\'t know', () => {
  assert.deepEqual(said({}), { rule: 6, glyph: 'idle', text: 'Idle', tone: 'plain' });
  assert.equal(said({ activity: { background: { agents: 0, shells: 0 } } }).rule, 6);
  assert.equal(said({ status: 'paused' }).rule, 6);
  // An unknown status with background work is still delegating.
  assert.equal(said({ status: 'paused', activity: { background: { agents: 0, shells: 1 } } }).rule, 5);
});

test('Rule 8: delegation the host counts but can\'t list reads "Delegating to N agents · details not reported"', () => {
  assert.deepEqual(said({ agent: 'claude', activity: { background: { agents: 2, shells: 0 } } }),
    { rule: 8, glyph: 'delegating', text: 'Delegating to 2 agents · details not reported', tone: 'plain' });
  // It refines rule 4 only: commands alone, or work alongside a question or a turn, are their own rules.
  assert.equal(said({ agent: 'claude', activity: { background: { agents: 0, shells: 1 } } }).rule, 5);
  assert.equal(said({ agent: 'codex', status: 'working', activity: { background: { agents: 1, shells: 0 } } }).rule, 3);
});

// Rule 7 is an overlay: whatever rules 1-6 (and 8) chose stays, the badge is added, and the
// words turn to the agent role.
const RULES = {
  1: { status: 'waiting', question: { id: 'q' } },
  2: example,
  3: { status: 'working' },
  4: { activity: { background: { agents: 2, shells: 0 } }, subagents: [delegate('a'), delegate('b')] },
  5: { activity: { background: { agents: 0, shells: 1 } } },
  6: {},
  8: { agent: 'claude', activity: { background: { agents: 1, shells: 0 } } },
};
for (const [rule, fields] of Object.entries(RULES)) {
  test(`Rule 7 over rule ${rule}: an agent driving keeps rule ${rule}'s glyph and words, adds the driven badge, and puts the words in agent colour`, () => {
    const person = sessionStatus(session(fields));
    const agent = sessionStatus(session({ ...fields, driver: 'agent', driverPrincipal: 'homeassistant:0f1e2d3c' }));
    assert.equal(person.rule, Number(rule));
    assert.equal(person.driven, false);
    assert.deepEqual({ rule: agent.rule, glyph: agent.glyph, text: agent.text }, { rule: person.rule, glyph: person.glyph, text: person.text });
    assert.equal(agent.driven, true);
    assert.equal(agent.tone, 'agent');
  });
}

test('A session the host is ending reads "Ending session…" with the ended glyph, in muted words, whatever it was doing', () => {
  const { rule, glyph, text, tone } = sessionStatus(session({ status: 'ending', question: null, activity: { background: { agents: 2, shells: 0 } } }));
  assert.deepEqual({ rule, glyph, text, tone }, { rule: 'ending', glyph: 'ended', text: 'Ending session…', tone: 'muted' });
  assert.equal(sessionStatus(session({ status: 'ending', driver: 'agent' })).driven, true);
});

test('The glyph is named with its state, as ui.md §9 reads a row: title, status, machine', () => {
  const name = (fields, machine = 'DEV-VM1') => statusLabel(sessionStatus(session(fields)), { title: session(fields).title, machine });
  assert.equal(name(RULES[4]), 'Plan Migration to Windows App, delegating to 2 agents, DEV-VM1');
  assert.equal(name(RULES[2]), 'Plan Migration to Windows App, Read witness shell logic asks, DEV-VM1');
  assert.equal(name(RULES[5]), 'Plan Migration to Windows App, 1 background command, DEV-VM1');
  // The diamond is only a shape, so the name says it in words.
  assert.equal(name({ status: 'working', driver: 'agent' }), 'Plan Migration to Windows App, working, driven by an agent, DEV-VM1');
  assert.equal(statusLabel(sessionStatus(session({ title: null })), { title: null }), 'Untitled session, idle');
});
