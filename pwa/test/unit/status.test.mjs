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

test('Rule 2: a delegate\'s question reads "Delegate <name> asks", named from the delegate list', () => {
  assert.deepEqual(said(example), { rule: 2, glyph: 'needs', text: 'Delegate Read witness shell logic asks', tone: 'needs' });
  // The question's agent names it as waitingOn does.
  assert.equal(said({ ...example, waitingOn: undefined }).text, 'Delegate Read witness shell logic asks');
});

test('Rule 2: a delegate\'s question only the machine can answer reads "Delegate <name> asks · answer at the machine"', () => {
  assert.equal(said({ ...example, question: { ...example.question, answerAtMachine: true } }).text, 'Delegate Read witness shell logic asks · answer at the machine');
});

test('Rule 2: a delegate the list left out still asks, without a name the host never gave', () => {
  assert.equal(said({ ...example, subagents: [], waitingOn: 'agent-gone', question: { ...example.question, agent: 'agent-gone' } }).text, 'A delegate asks');
});

test('Rule 3: the main agent\'s turn reads "Working" with the working glyph, in plain words', () => {
  assert.deepEqual(said({ status: 'working', activity: { background: { agents: 1, shells: 2 } } }), { rule: 3, glyph: 'working', text: 'Working', tone: 'plain' });
});

test('Rule 3: a foreground task agent running reads "Working · task: Delegate <name>"', () => {
  const subagents = [
    { id: 't2', parent: 't1', kind: 'task', name: 'Bisect RestartAfterResume', status: 'working' },
    { id: 't1', kind: 'task', name: 'Find the flaky test', status: 'working' },
    { id: 'b1', kind: 'background', name: 'Run full test suite', status: 'working' },
  ];
  assert.equal(said({ status: 'working', subagents }).text, 'Working · task: Delegate Bisect RestartAfterResume');
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
  assert.equal(name(RULES[2]), 'Plan Migration to Windows App, Delegate Read witness shell logic asks, DEV-VM1');
  assert.equal(name(RULES[5]), 'Plan Migration to Windows App, 1 background command, DEV-VM1');
  // The diamond is only a shape, so the name says it in words.
  assert.equal(name({ status: 'working', driver: 'agent' }), 'Plan Migration to Windows App, working, driven by an agent, DEV-VM1');
  assert.equal(statusLabel(sessionStatus(session({ title: null })), { title: null }), 'Untitled session, idle');
});

test('Rules 2 and 3: a delegate named "Main agent" is named as a delegate, never as the main agent', () => {
  const impostor = { id: 'x1', kind: 'task', status: 'waiting', name: '\u2067Main agent\u2069' };
  assert.equal(said({ status: 'waiting', waitingOn: 'x1', subagents: [impostor] }).text, 'Delegate \u201cMain agent\u201d asks');
  assert.equal(said({ status: 'working', subagents: [{ ...impostor, status: 'working', name: 'MAIN AGENT' }] }).text, 'Working · task: Delegate \u201cMAIN AGENT\u201d');
  assert.equal(said({ status: 'working', subagents: [{ ...impostor, status: 'working', name: '\u2c99\u2c81\u2c93\u2c9b agent' }] }).text, 'Working · task: Delegate \u2c99\u2c81\u2c93\u2c9b agent');
});

test('Rule 2: a delegate\'s name cannot add a fact of its own, so the row\'s first fact always says who asks', () => {
  const asks = (name) => said({ status: 'waiting', waitingOn: 'x1', subagents: [{ id: 'x1', kind: 'task', status: 'waiting', name }] }).text;
  assert.equal(asks('Main agent asks \u00b7 Read logs'), 'Delegate \u201cMain agent asks - Read logs\u201d asks');
  assert.equal(asks('\u041c\u0430\u0456n agent'), 'Delegate \u201c\u041c\u0430\u0456n agent\u201d asks');
  assert.equal(asks('\u2c99\u2c81\u2c93\u2c9b agent'), 'Delegate \u2c99\u2c81\u2c93\u2c9b agent asks');
  assert.equal(asks('Idle \u00b7 Read logs'), 'Delegate Idle - Read logs asks');
});

test('Every rule gives its facts as a list, so a row takes the first and nothing is found by cutting words at a separator', () => {
  const facts = (fields) => sessionStatus(session(fields)).facts;
  assert.deepEqual(facts({ status: 'waiting', question: { id: 'q', answerAtMachine: true }, activity: { background: { agents: 2, shells: 0 } } }), ['Waiting on you', '2 delegates still running', 'answer at the machine']);
  assert.deepEqual(facts({ ...example, question: { ...example.question, answerAtMachine: true } }), ['Delegate Read witness shell logic asks', 'answer at the machine']);
  assert.deepEqual(facts({ status: 'working', subagents: [delegate('t', { kind: 'task', name: 'Bisect' })] }), ['Working', 'task: Delegate Bisect']);
  assert.deepEqual(facts({ activity: { background: { agents: 2, shells: 0 } } }), ['Delegating to 2 agents', 'details not reported']);
  assert.deepEqual(facts({}), ['Idle']);
  // The words the header shows are the facts joined, and nothing else.
  const s = sessionStatus(session(example));
  assert.equal(s.text, s.facts.join(' \u00b7 '));
});

test('Read aloud, a delegate\'s name has no comma of its own, so it can\'t add facts to the glyph\'s name; drawn, it keeps them', () => {
  const asking = (name) => sessionStatus(session({ status: 'waiting', waitingOn: 'x1', subagents: [{ id: 'x1', kind: 'task', status: 'waiting', name }] }));
  const label = (name) => statusLabel(asking(name), { title: 'Fix', machine: 'DEV-VM1' });
  assert.equal(label('x, idle, DEV-VM2'), 'Fix, Delegate x idle DEV-VM2 asks, DEV-VM1');
  assert.equal(asking('x, idle, DEV-VM2').text, 'Delegate x, idle, DEV-VM2 asks');
  // The commas other scripts and forms draw like it.
  for (const comma of ['\u055d', '\u060c', '\u066b', '\u07f8', '\u1363', '\u1802', '\u1808', '\u201a', '\u2e41', '\u2e4c', '\u3001', '\ua4fe', '\ua60d', '\ua6f5', '\ufe10', '\ufe11', '\ufe50', '\ufe51', '\uff0c', '\uff64']) {
    assert.equal(label(`x${comma} idle${comma} DEV-VM2`), 'Fix, Delegate x idle DEV-VM2 asks, DEV-VM1', `U+${comma.codePointAt(0).toString(16)}`);
  }
  // Rule 3's task too.
  const working = sessionStatus(session({ status: 'working', subagents: [delegate('t', { kind: 'task', name: 'Lint, idle' })] }));
  assert.equal(statusLabel(working, { title: 'Fix' }), 'Fix, working · task: Delegate Lint idle');
});

test('Read aloud, a title cannot end the line early or add a fact: its commas, semicolons and stops are spaces', () => {
  const idle = sessionStatus(session({}));
  assert.equal(statusLabel(idle, { title: 'Fix. Idle; done, ok!', machine: 'DEV-VM1' }), 'Fix Idle done ok, idle, DEV-VM1');
  assert.equal(statusLabel(idle, { title: 'v1.2 fix', machine: 'DEV-VM1' }), 'v1.2 fix, idle, DEV-VM1');
});

// #800: Session.attention, shown over a truthful status: an agent that can't run until a person
// signs it in, a turn ended on an error, and a working session the host has heard nothing from.
// None of them ever reads as idle or working.
const signIn = (fields = {}) => ({ kind: 'signInNeeded', since: '2026-11-04T23:00:00.000Z', agent: 'copilot', ...fields });

test('Rule 10: a session that needs sign-in reads "Needs sign-in" with its agent and the needs glyph, never idle', () => {
  assert.deepEqual(said({ status: 'idle', attention: signIn() }), { rule: 10, glyph: 'needs', text: 'Needs sign-in · Copilot', tone: 'needs' });
  assert.equal(said({ status: 'idle', attention: signIn({ account: 'octocat' }) }).text, 'Needs sign-in · Copilot · @octocat');
  // Its own question still comes first: the agent asked before it stopped.
  assert.equal(said({ status: 'waiting', question: { id: 'q' }, attention: signIn() }).rule, 1);
  assert.equal(statusLabel(sessionStatus(session({ attention: signIn() })), { title: 'Fix', machine: 'DEV-VM1' }), 'Fix, needs sign-in · Copilot, DEV-VM1');
  // A row shows the first fact only.
  assert.equal(sessionStatus(session({ attention: signIn({ account: 'octocat' }) })).facts[0], 'Needs sign-in');
});

test('Rule 11: a turn that ended on an agent error says its class with the failed glyph, and never the host\'s message', () => {
  assert.deepEqual(said({ attention: { kind: 'error', errorClass: 'auth', message: 'Bad credentials.' } }), { rule: 11, glyph: 'failed', text: 'Not authorised', tone: 'needs' });
  assert.equal(said({ attention: { kind: 'error', errorClass: 'rate' } }).text, 'Rate limited');
  // A class without words of its own in ui.md §6.13 reads as "Agent error", as an unknown one does.
  assert.equal(said({ attention: { kind: 'error', errorClass: 'quota' } }).text, 'Agent error');
  assert.equal(said({ attention: { kind: 'error', errorClass: 'somethingNew' } }).text, 'Agent error');
  assert.equal(said({ attention: { kind: 'meteor' } }).text, 'Agent error');
  // Driven by an agent, it still says what is wrong in its own tone (rules 10-12 over rule 7).
  assert.deepEqual(said({ driver: 'agent', attention: { kind: 'error', errorClass: 'auth' } }), { rule: 11, glyph: 'failed', text: 'Not authorised', tone: 'needs' });
  assert.equal(said({ driver: 'agent' }).tone, 'agent');
  // Before rules 4 and 5: background work that survived the turn never hides the error.
  assert.deepEqual(said({ attention: { kind: 'error', errorClass: 'rate' }, activity: { background: { agents: 2, shells: 0 } } }), { rule: 11, glyph: 'failed', text: 'Rate limited · 2 delegates still running', tone: 'needs' });
});

test('Rule 10 comes after the session\'s own question and before a delegate\'s (ui.md §6.13)', () => {
  const asking = { status: 'waiting', question: { id: 'q', agent: 'd1' }, waitingOn: 'd1', subagents: [delegate('d1', { status: 'waiting' })] };
  // The main agent can't run: a delegate's question never hides it.
  assert.deepEqual(said({ ...asking, attention: signIn() }), { rule: 10, glyph: 'needs', text: 'Needs sign-in · Copilot', tone: 'needs' });
  assert.equal(said({ ...asking, attention: signIn({ recovery: 'pending' }) }).text, 'Signed in · waiting for the session to confirm');
  // Without one, the delegate's question as before.
  assert.equal(said(asking).rule, 2);
  // The session's own question still comes first.
  assert.equal(said({ status: 'waiting', question: { id: 'q' }, attention: signIn({ recovery: 'pending' }) }).rule, 1);
});

test('Rule 11 counts every delegate still running, task ones that survived the turn too', () => {
  const running = [delegate('a'), delegate('t', { kind: 'task' })];
  assert.equal(said({ attention: { kind: 'error', errorClass: 'rate' }, activity: { background: { agents: 1, shells: 0 } }, subagents: running }).text, 'Rate limited · 2 delegates still running');
  assert.equal(said({ attention: { kind: 'error', errorClass: 'auth' }, subagents: [delegate('t', { kind: 'task' })] }).text, 'Not authorised · 1 delegate still running');
});

test('Rule 10, confirming: signed in and waiting for the session to confirm is neither "Needs sign-in" nor recovered', () => {
  assert.deepEqual(said({ status: 'idle', attention: signIn({ recovery: 'pending' }) }), { rule: 10, glyph: 'idle', text: 'Signed in · waiting for the session to confirm', tone: 'plain' });
  // Failing again while confirming goes straight back to Needs sign-in.
  assert.equal(said({ status: 'idle', attention: signIn({ recovery: 'failed' }) }).text, 'Needs sign-in · Copilot');
  // No recovery value says recovered (#818): one the client doesn't know is failed, as the protocol has it.
  assert.equal(said({ status: 'idle', attention: signIn({ recovery: 'resumed' }) }).text, 'Needs sign-in · Copilot');
  // Only the host clearing the attention does: the session's own status again.
  assert.equal(said({ status: 'idle' }).text, 'Idle');
});

test('Rule 12: a working session the host has heard nothing from reads "Not responding · since" its last event, not working', () => {
  const s = said({ status: 'working', attention: { kind: 'notResponding', lastEventAt: '2026-11-04T23:00:00.000Z' } });
  assert.deepEqual([s.rule, s.glyph, s.tone], [12, 'failed', 'needs']);
  assert.match(s.text, /^Not responding · since \d{1,2}:\d{2}/);
  assert.equal(said({ status: 'working' }).rule, 3);
});
