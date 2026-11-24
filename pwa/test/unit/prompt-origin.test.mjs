// A prompt the person didn't type (#489, ui.md §6.4): who sent it, how its label line reads, and
// when a schedule's prompt folds as the same as last time.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { aloud } from '../../js/attribution.js';
import { readSession } from '../../js/protocol.js';
import { LABEL_MAX_BYTES, PromptMemory, disclosureName, historyPromptOrigin, promptFact, promptOrigin } from '../../js/prompt-origin.js';

const MONITOR = 'Check the build.\nIf it is red, look at the failing job\n\nand say which test.';
const session = ({ from, label, text = MONITOR, status = 'idle', turns = 3, since = '2026-11-03T19:00:00Z', ...rest } = {}) => readSession({
  id: 's', status, statusSince: since, updatedAt: since, ...rest,
  activity: { userMessage: text, turnCount: turns, ...(from === undefined ? {} : { userMessageFrom: from }), ...(label === undefined ? {} : { userMessageLabel: label }) },
});
const bytes = (s) => new TextEncoder().encode(s).length;

test('the person\'s own prompt, and one from a host that doesn\'t say who sent it, has no origin and stays a bubble', () => {
  assert.equal(promptOrigin(session()), null);
  assert.equal(promptOrigin(session({ from: 'person' })), null);
  assert.equal(promptOrigin(session({ from: 'person', label: 'Build helper' })), null);
  assert.equal(promptOrigin(session({ from: 'schedule', text: null })), null);
});

test('a schedule\'s prompt is labelled Scheduled with its number, under a clock, and named so aloud', () => {
  const o = promptOrigin(session({ from: 'schedule', label: '4' }));
  assert.deepEqual([o.from, o.icon, o.facts, o.by, o.spoken], ['schedule', 'clock', ['Scheduled', '#4'], null, 'Scheduled 4']);
  assert.equal(disclosureName(o, false), 'Scheduled 4, Show prompt');
  assert.equal(disclosureName(o, true), 'Scheduled 4, Hide prompt');
  // Without a label, the words alone.
  const bare = promptOrigin(session({ from: 'schedule' }));
  assert.deepEqual([bare.facts, bare.spoken], [['Scheduled'], 'Scheduled']);
});

test('host turn kind keeps automatic prompts out of owner bubbles and names their origin exactly', () => {
  for (const [kind, label] of [['notification', 'Notification'], ['scheduled', 'Scheduled']]) {
    const s = readSession({ id: 's', activity: { kind, userMessage: 'Automatic' } });
    const live = promptOrigin(s);
    const stored = historyPromptOrigin({ text: 'Automatic', from: 'person' }, kind);
    assert.ok(live);
    assert.ok(stored);
    assert.deepEqual(live.facts, [label]);
    assert.deepEqual(stored.facts, [label]);
  }
});

test('an agent\'s prompt is labelled Sent by an agent under the driven diamond, with the agent driving as secondary text when known', () => {
  const o = promptOrigin(session({ from: 'agent', driver: 'agent', driverPrincipal: 'homeassistant:0f1e2d3c' }));
  assert.deepEqual([o.icon, o.facts, o.by, o.spoken], ['driven', ['Sent by an agent'], 'homeassistant:0f1e2d3c', 'Prompt sent by an agent']);
  assert.equal(disclosureName(o, false), 'Prompt sent by an agent, Show prompt');
  assert.equal(promptOrigin(session({ from: 'agent' })).by, null);
  // A sender the host knows only as not the person.
  const other = promptOrigin(session({ from: 'other', label: 'x' }));
  assert.deepEqual([other.icon, other.facts, other.spoken], ['driven', ['Automatic prompt'], 'Automatic prompt']);
});

test('a live agent-sent prompt names its sending session on the agent line, never the person bubble', () => {
  const o = promptOrigin(session({ from: 'agent', label: 'Build helper', driver: 'agent', driverPrincipal: 'agent:sender' }));
  assert.ok(o);
  assert.deepEqual([o.from, o.icon, o.facts, o.by, o.spoken],
    ['agent', 'driven', ['From: Build helper'], 'agent:sender', 'From: Build helper']);
  assert.equal(disclosureName(o, false), 'From: Build helper, Show prompt');
  assert.equal(disclosureName(o, true), 'From: Build helper, Hide prompt');
});

test('a stored agent-sent prompt has the same From label and agent origin as its live line', () => {
  const stored = historyPromptOrigin({ text: MONITOR, from: 'agent', label: 'Build helper' });
  assert.ok(stored);
  assert.deepEqual(stored, promptOrigin(session({ from: 'agent', label: 'Build helper' })));
  assert.deepEqual([stored.from, stored.icon, stored.facts, stored.spoken],
    ['agent', 'driven', ['From: Build helper'], 'From: Build helper']);
});

test('agent sender facts are sanitized and capped in live and stored prompts, with a fallback for empty labels', () => {
  for (const origin of [
    (label) => promptOrigin(session({ from: 'agent', label })),
    (label) => historyPromptOrigin({ text: MONITOR, from: 'agent', label }),
  ]) {
    const o = origin('Build\u202E\u200B\nhelper \u00b7 owner, expanded');
    assert.deepEqual(o.facts, ['From: Build helper - owner, expanded']);
    assert.equal(o.spoken, 'From: Build helper - owner expanded');
    const long = origin('\u00e9'.repeat(100)).facts[0].slice('From: '.length);
    assert.ok(bytes(long) <= LABEL_MAX_BYTES);
    assert.ok(long.endsWith('\u2026'));
    for (const label of [undefined, '', '\u202E\u200B']) {
      assert.deepEqual(origin(label).facts, ['Sent by an agent']);
      assert.equal(origin(label).spoken, 'Prompt sent by an agent');
    }
  }
});

test('the preview is the prompt on one line, and all of it is kept for Show prompt', () => {
  const o = promptOrigin(session({ from: 'schedule', label: '4' }));
  assert.equal(o.preview, 'Check the build. If it is red, look at the failing job and say which test.');
  assert.equal(o.text, MONITOR);
});

test('hidden and bidi characters in a prompt are drawn as escapes, never as nothing', () => {
  const o = promptOrigin(session({ from: 'agent', text: 'run \u202Etxt.exe\u202C now\u200B' }));
  assert.equal(o.text, 'run \\u202Etxt.exe\\u202C now\\u200B');
  assert.equal(o.preview, 'run \\u202Etxt.exe\\u202C now\\u200B');
});

test('a label can\'t hide characters, add a fact of its own, read as the main agent, or run past 64 bytes', () => {
  assert.equal(promptFact('4\u202E\u200B\n'), '4');
  // A middle dot would have drawn "Scheduled · 4 · same as last time" for a prompt that changed.
  assert.deepEqual(promptOrigin(session({ from: 'schedule', label: '4 \u00b7 same as last time' })).facts, ['Scheduled', '\u201c4 - same as last time\u201d']);
  assert.deepEqual(promptOrigin(session({ from: 'schedule', label: 'Main agent' })).facts, ['Scheduled', '\u201cMain agent\u201d']);
  assert.deepEqual(promptOrigin(session({ from: 'schedule', label: 'Main\u201d agent' })).facts, ['Scheduled', '\u201cMain\' agent\u201d']);
  // Any label but a number is quoted, so a look-alike no check knows still can't read as the line's own words.
  assert.deepEqual(promptOrigin(session({ from: 'schedule', label: 'rnain agent' })).facts, ['Scheduled', '\u201crnain agent\u201d']);
  // Read aloud, a comma of its own would add a part to the button's name.
  assert.equal(promptOrigin(session({ from: 'schedule', label: 'x, expanded' })).spoken, 'Scheduled \u201cx expanded\u201d');
  const long = promptFact('é'.repeat(100));
  assert.ok(bytes(long) <= LABEL_MAX_BYTES, `${bytes(long)} bytes`);
  assert.ok(long.endsWith('\u2026'));
  assert.equal(promptFact('a'.repeat(64)), 'a'.repeat(64));
  // A grapheme is never split: a family emoji is kept whole or left out.
  assert.ok(!promptFact(`${'a'.repeat(60)}\u{1F468}\u200D\u{1F469}`).includes('\u{1F468}'));
});

test('a schedule\'s prompt that repeats its last one, as seen here, folds to same as last time once its turn is done', () => {
  const m = new PromptMemory();
  // Seen first: nothing to compare with, so never folded.
  assert.deepEqual(m.observe(session({ from: 'schedule', label: '4', turns: 3 })), { repeat: false, at: null, fresh: false });
  assert.equal(m.observe(session({ from: 'schedule', label: '4', turns: 3 })).fresh, false);
  // The schedule fires again with the same words: while its turn runs it looks like any turn of
  // the same prompt, so nothing changes yet.
  assert.deepEqual(m.observe(session({ from: 'schedule', label: '4', status: 'working', turns: 3, since: '2026-11-03T19:01:00Z' })), { repeat: false, at: null, fresh: false });
  // Its turn done, the host counts one prompt more: a repeat, dated by when its turn began.
  assert.deepEqual(m.observe(session({ from: 'schedule', label: '4', turns: 4 })), { repeat: true, at: '2026-11-03T19:01:00Z', fresh: true });
  assert.deepEqual(m.observe(session({ from: 'schedule', label: '4', turns: 4 })), { repeat: true, at: '2026-11-03T19:01:00Z', fresh: false });
  assert.deepEqual(promptOrigin(session({ from: 'schedule', label: '4' }), { repeat: true }).facts, ['Scheduled', '#4', 'same as last time']);
});

test('a turn of the same prompt that no prompt started, such as a delegate reporting back, is not a repeat', () => {
  const m = new PromptMemory();
  // Seen first while its own turn runs, then that turn done.
  assert.deepEqual(m.observe(session({ from: 'schedule', label: '4', status: 'working', turns: 6, since: '2026-11-03T19:01:00Z' })), { repeat: false, at: '2026-11-03T19:01:00Z', fresh: false });
  m.observe(session({ from: 'schedule', label: '4', turns: 7 }));
  // A turn the host doesn't count (Claude's for a notice): working with the count unchanged.
  assert.equal(m.observe(session({ from: 'schedule', label: '4', status: 'working', turns: 7, since: '2026-11-03T19:05:00Z' })).fresh, false);
  assert.deepEqual(m.observe(session({ from: 'schedule', label: '4', turns: 7 })), { repeat: false, at: '2026-11-03T19:01:00Z', fresh: false });
});

test('a schedule\'s prompt with new words, another schedule\'s, or the person\'s in between is not folded wrongly', () => {
  const m = new PromptMemory();
  m.observe(session({ from: 'schedule', label: '4', turns: 3 }));
  assert.equal(m.observe(session({ from: 'schedule', label: '4', status: 'working', turns: 3, text: 'Something else' })).repeat, false);
  // New words seen arriving: the turn that ends them is theirs, not one more prompt.
  assert.equal(m.observe(session({ from: 'schedule', label: '4', turns: 4, text: 'Something else' })).fresh, false);
  assert.equal(m.observe(session({ from: 'schedule', label: '5', status: 'working', turns: 4, text: MONITOR })).repeat, false);
  // The person types, then schedule 5 fires again with its own last words.
  m.observe(session({ status: 'working', turns: 5, text: 'Thanks' }));
  assert.equal(m.observe(session({ from: 'schedule', label: '5', status: 'working', turns: 6, text: MONITOR })).repeat, true);
});

test('turns that end before a new prompt has settled are not put down to it, even with an earlier prompt\'s end missed', () => {
  const m = new PromptMemory();
  m.observe(session({ from: 'schedule', label: '4', turns: 3, text: 'A' }));
  // A's words sent again and running; B arrives before this view sees A's turn end.
  m.observe(session({ from: 'schedule', label: '4', status: 'working', turns: 3, text: 'A' }));
  m.observe(session({ from: 'schedule', label: '4', status: 'working', turns: 4, text: 'B' }));
  // Two more turns done, one of them A's: B was sent once.
  assert.deepEqual(m.observe(session({ from: 'schedule', label: '4', turns: 5, text: 'B' })), { repeat: false, at: '2026-11-03T19:00:00Z', fresh: false });
});

test('a repeat is dated by when its turn began, not by a question answered within it', () => {
  const m = new PromptMemory();
  m.observe(session({ from: 'schedule', label: '4', turns: 3 }));
  m.observe(session({ from: 'schedule', label: '4', status: 'working', turns: 3, since: '2026-11-03T19:01:00Z' }));
  m.observe(session({ from: 'schedule', label: '4', status: 'waiting', turns: 3, since: '2026-11-03T19:02:00Z' }));
  m.observe(session({ from: 'schedule', label: '4', status: 'working', turns: 3, since: '2026-11-03T19:03:00Z' }));
  assert.deepEqual(m.observe(session({ from: 'schedule', label: '4', turns: 4 })), { repeat: true, at: '2026-11-03T19:01:00Z', fresh: true });
  // Its words sent again and seen only once done: no age, not the last one's.
  assert.deepEqual(m.observe(session({ from: 'schedule', label: '4', turns: 5 })), { repeat: true, at: null, fresh: true });
});
test('a host that doesn\'t count turns never has a repeat folded', () => {
  const m = new PromptMemory();
  m.observe(session({ from: 'schedule', label: '4', turns: null }));
  m.observe(session({ from: 'schedule', label: '4', status: 'working', turns: null }));
  assert.equal(m.observe(session({ from: 'schedule', label: '4', turns: null })).repeat, false);
});

test('a line break in a label never makes a new prompt look the same, and words a host leaves out never make the same one look new', () => {
  const joined = new PromptMemory();
  joined.observe(session({ from: 'schedule', label: '4', text: 'B\nC' }));
  assert.equal(joined.observe(session({ from: 'schedule', label: '4\nB', status: 'working', text: 'C' })).fresh, true);
  const omitted = new PromptMemory();
  omitted.observe(session({ from: 'schedule', label: '4', text: 'A' }));
  assert.deepEqual(omitted.observe(session({ from: 'schedule', label: '4', text: null })), { repeat: false, at: null, fresh: false });
  assert.deepEqual(omitted.observe(session({ from: 'schedule', label: '4', text: 'A' })), { repeat: false, at: null, fresh: false });
});
test('labels an agent wrote apart but drawn alike are one: same as last time only when the words match the last shown under it', () => {
  for (const [one, two] of [['nightly-build', 'nightly\u00b7build'], ['\u201ca\u2019b\u201d', '\u201ca\'b\u201d']]) {
    const m = new PromptMemory();
    const row = (label, text, turns) => m.observe(session({ from: 'schedule', label, text, turns })).repeat;
    assert.deepEqual([row(one, 'A', 3), row(two, 'B', 4), row(one, 'A', 5), row(two, 'B', 6)], [false, false, false, false], one);
    assert.equal(promptOrigin(session({ from: 'schedule', label: one })).facts[1], promptOrigin(session({ from: 'schedule', label: two })).facts[1]);
  }
});
test('a repeat whose turn ends while the host leaves the words out is still a repeat once they return, dated from its turn', () => {
  const m = new PromptMemory();
  m.observe(session({ from: 'schedule', label: '4', text: 'A', turns: 3 }));
  m.observe(session({ from: 'schedule', label: '4', text: 'A', status: 'working', turns: 3, since: '2026-11-03T19:01:00Z' }));
  assert.deepEqual(m.observe(session({ from: 'schedule', label: '4', text: null, turns: 4 })), { repeat: false, at: null, fresh: false });
  assert.deepEqual(m.observe(session({ from: 'schedule', label: '4', text: 'A', turns: 4 })), { repeat: true, at: '2026-11-03T19:01:00Z', fresh: true });
});
test('a repeat whose turn began with the idle before it missed is dated from its own turn, not the last one\'s', () => {
  const m = new PromptMemory();
  m.observe(session({ from: 'schedule', label: '4', turns: 3 }));
  m.observe(session({ from: 'schedule', label: '4', status: 'working', turns: 3, since: '2026-11-03T19:01:00Z' }));
  // A question answered within it is not a new turn; a count risen while it runs is.
  m.observe(session({ from: 'schedule', label: '4', status: 'working', turns: 3, since: '2026-11-03T19:02:00Z' }));
  m.observe(session({ from: 'schedule', label: '4', status: 'working', turns: 4, since: '2026-11-03T19:05:00Z' }));
  assert.deepEqual(m.observe(session({ from: 'schedule', label: '4', turns: 5 })), { repeat: true, at: '2026-11-03T19:05:00Z', fresh: true });
});
test('a turn no prompt started, begun with the idle before it missed, does not date the repeat before it', () => {
  const m = new PromptMemory();
  m.observe(session({ from: 'schedule', label: '4', turns: 3 }));
  m.observe(session({ from: 'schedule', label: '4', status: 'working', turns: 3, since: '2026-11-03T19:01:00Z' }));
  // Claude's turn for a delegate's notice: the count rises as it runs, but it was no prompt's.
  m.observe(session({ from: 'schedule', label: '4', status: 'working', turns: 4, since: '2026-11-03T19:05:00Z' }));
  assert.deepEqual(m.observe(session({ from: 'schedule', label: '4', turns: 4 })), { repeat: true, at: '2026-11-03T19:01:00Z', fresh: true });
});
test('an earlier turn\'s prompt from an agent, a schedule or another source reads as the live one does', () => {
  const agent = historyPromptOrigin({ text: MONITOR, from: 'agent' });
  assert.deepEqual([agent.from, agent.icon, agent.facts, agent.by, agent.spoken], ['agent', 'driven', ['Sent by an agent'], null, 'Prompt sent by an agent']);
  assert.equal(agent.preview, 'Check the build. If it is red, look at the failing job and say which test.');
  const schedule = historyPromptOrigin({ text: 'Go', from: 'schedule', label: '4' });
  assert.deepEqual([schedule.icon, schedule.facts, schedule.repeat], ['clock', ['Scheduled', '#4'], false]);
  assert.deepEqual(historyPromptOrigin({ text: 'Go', from: 'other' }).facts, ['Automatic prompt']);
});

test('an earlier turn\'s prompt the person typed, or without words, has no origin', () => {
  assert.equal(historyPromptOrigin({ text: 'Hi' }), null);
  assert.equal(historyPromptOrigin({ text: 'Hi', from: 'person' }), null);
  assert.equal(historyPromptOrigin({ text: '', from: 'agent' }), null);
  assert.equal(historyPromptOrigin(null), null);
});
// A relay message from an agent (#1365): the host types its envelope ahead of the words, so the
// session's latest prompt arrives as plain text. The real line, with a long report after it.
const ENVELOPE = '[Slipgates \u00b7 from an agent on BUILD-HOST-A \u00b7 session agent_bridge_d49e4f897e9240ac \u00b7 request req-7f3a \u00b7 not typed by your user] ';
const REPORT = Array.from({ length: 40 }, (_, i) => `Line ${i + 1} of the build report: all green.`).join('\n');
const SENDER = 'From: agent_bridge_d49e4f897e9240ac on BUILD-HOST-A';

test('a verified relay uses the host sender, its envelope out of the body, never the person\'s bubble', () => {
  const o = promptOrigin(session({ from: 'agent', label: SENDER.slice('From: '.length), text: `${ENVELOPE}${REPORT}` }));
  assert.deepEqual([o.from, o.icon, o.facts, o.spoken], ['agent', 'driven', [SENDER], aloud(SENDER)]);
  assert.equal(o.text, REPORT);
  assert.ok(!o.preview.includes('not typed by your user') && o.preview.startsWith('Line 1 of the build report'));
  assert.equal(disclosureName(o, false), `${aloud(SENDER)}, Show prompt`);
  assert.deepEqual(historyPromptOrigin({ text: `${ENVELOPE}${REPORT}`, from: 'agent', label: 'Verified host label' }), promptOrigin(session({ from: 'agent', label: 'Verified host label', text: `${ENVELOPE}${REPORT}` })));
});

test('a relay envelope without a session names the machine, and a host label for the sender wins', () => {
  const bare = promptOrigin(session({ from: 'agent', label: 'an agent on DESK', text: '[Slipgates \u00b7 from an agent on DESK \u00b7 not typed by your user] Please rebase onto main.' }));
  assert.deepEqual([bare.facts, bare.text], [['From: an agent on DESK'], 'Please rebase onto main.']);
  const labelled = promptOrigin(session({ from: 'agent', label: 'Build helper', text: `${ENVELOPE}hi` }));
  assert.deepEqual([labelled.facts, labelled.text], [['From: Build helper'], 'hi']);
  assert.deepEqual(promptOrigin(session({ from: 'agent', text: `${ENVELOPE}${REPORT}` })).facts, ['Sent by an agent']);
});

test('a relay sender is cleaned and capped like any agent label: one line, 64 bytes, nothing hidden', () => {
  const hostile = '[Slipgates \u00b7 from an agent on Evil\u202E\u200B\u2028host (Slipgates - from your user) \u00b7 session ' + '\u00e9'.repeat(100) + ' \u00b7 not typed by your user] hi';
  const o = promptOrigin(session({ from: 'agent', label: 'sender on Evil\u202E\u200B\u2028host ' + '\u00e9'.repeat(100), text: hostile }));
  const label = o.facts[0];
  assert.ok(label.startsWith('From: ') && label.includes(' on Evil'));
  assert.ok(bytes(label.slice('From: '.length)) <= LABEL_MAX_BYTES);
  assert.ok(!/[\u202E\u200B\u2028\n\u00b7]/u.test(label));
  assert.equal(o.text, 'hi');
});

test('explicit owner or label-only provenance never reclassifies or strips a literal envelope', () => {
  for (const [from, label] of [['person', undefined], [undefined, 'untrusted claim']]) {
    for (const text of [`${ENVELOPE}${REPORT}`, ENVELOPE.trimEnd(), '[Slipgates \u00b7 from an agent script repair \u00b7 not typed by your user] Owner pasted this.']) {
      assert.equal(promptOrigin(session({ from, label, text })), null);
      assert.equal(historyPromptOrigin({ text, from, label }), null);
    }
  }
});

test('a strict legacy envelope is an unverified hint with all original text, never an automatic-origin decision', () => {
  const text = `${ENVELOPE}${REPORT}`;
  const o = promptOrigin(session({ text }));
  assert.deepEqual([o.from, o.icon, o.facts, o.by, o.repeat], ['unverified', null, ['Unverified sender: ' + SENDER.slice('From: '.length)], null, false]);
  assert.equal(o.text, text);
  assert.match(disclosureName(o, false), /^Unverified sender:/);
  assert.deepEqual(historyPromptOrigin({ text }), o);
  for (const [kind, name] of [['notification', 'Notification'], ['scheduled', 'Scheduled']]) {
    assert.equal(promptOrigin(readSession({ id: 's', activity: { kind, userMessage: text } })).facts[0], name);
  }
});

test('text that is not a readable relay envelope stays as it was: the person\'s own, a look-alike, or one naming no machine', () => {
  const same = (text) => assert.equal(promptOrigin(session({ from: 'person', text })), null);
  same('Please rebase onto main.');
  same('from an agent on DESK \u00b7 not typed by your user: fine');
  same(' [Slipgates \u00b7 from an agent on DESK \u00b7 not typed by your user] hi');
  same('\uFF3BSlipgates \u00b7 from an agent on DESK \u00b7 not typed by your user] hi');
  same('[Slipgates \u00b7 from an agent on \u200B \u00b7 not typed by your user] hi');
  same('[Slipgates \u00b7 from an agent on DESK] hi');
});
