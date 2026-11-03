// A prompt the person didn't type (#489, ui.md §6.4): who sent it, how its label line reads, and
// when a schedule's prompt folds as the same as last time.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readSession } from '../../js/protocol.js';
import { LABEL_MAX_BYTES, PromptMemory, disclosureName, promptFact, promptOrigin } from '../../js/prompt-origin.js';

const MONITOR = 'Check the build.\nIf it is red, look at the failing job\n\nand say which test.';
const session = ({ from, label, text = MONITOR, status = 'idle', turns = 3, since = '2026-11-03T19:00:00Z', ...rest } = {}) => readSession({
  id: 's', status, statusSince: since, updatedAt: since, ...rest,
  activity: { userMessage: text, turnCount: turns, ...(from === undefined ? {} : { userMessageFrom: from }), ...(label === undefined ? {} : { userMessageLabel: label }) },
});
const bytes = (s) => new TextEncoder().encode(s).length;

test('the person\'s own prompt, and one from a host that doesn\'t say who sent it, has no origin and stays a bubble', () => {
  assert.equal(promptOrigin(session()), null);
  assert.equal(promptOrigin(session({ from: 'person' })), null);
  assert.equal(promptOrigin(session({ from: 'schedule', text: null })), null);
});

test('a schedule\'s prompt is labelled Scheduled prompt with its number, under a clock, and named so aloud', () => {
  const o = promptOrigin(session({ from: 'schedule', label: '4' }));
  assert.deepEqual([o.from, o.icon, o.facts, o.by, o.spoken], ['schedule', 'clock', ['Scheduled prompt', '#4'], null, 'Scheduled prompt 4']);
  assert.equal(disclosureName(o, false), 'Scheduled prompt 4, Show prompt');
  assert.equal(disclosureName(o, true), 'Scheduled prompt 4, Hide prompt');
  // Without a label, the words alone.
  const bare = promptOrigin(session({ from: 'schedule' }));
  assert.deepEqual([bare.facts, bare.spoken], [['Scheduled prompt'], 'Scheduled prompt']);
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
  // A middle dot would have drawn "Scheduled prompt · 4 · same as last time" for a prompt that changed.
  assert.deepEqual(promptOrigin(session({ from: 'schedule', label: '4 \u00b7 same as last time' })).facts, ['Scheduled prompt', '\u201c4 - same as last time\u201d']);
  assert.deepEqual(promptOrigin(session({ from: 'schedule', label: 'Main agent' })).facts, ['Scheduled prompt', '\u201cMain agent\u201d']);
  assert.deepEqual(promptOrigin(session({ from: 'schedule', label: 'Main\u201d agent' })).facts, ['Scheduled prompt', '\u201cMain\' agent\u201d']);
  // Any label but a number is quoted, so a look-alike no check knows still can't read as the line's own words.
  assert.deepEqual(promptOrigin(session({ from: 'schedule', label: 'rnain agent' })).facts, ['Scheduled prompt', '\u201crnain agent\u201d']);
  // Read aloud, a comma of its own would add a part to the button's name.
  assert.equal(promptOrigin(session({ from: 'schedule', label: 'x, expanded' })).spoken, 'Scheduled prompt \u201cx expanded\u201d');
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
  assert.deepEqual(promptOrigin(session({ from: 'schedule', label: '4' }), { repeat: true }).facts, ['Scheduled prompt', '#4', 'same as last time']);
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

test('a host saying idle a moment before it counts the turn done is not a new prompt', () => {
  const m = new PromptMemory();
  m.observe(session({ from: 'schedule', label: '4', turns: 3 }));
  m.observe(session({ from: 'schedule', label: '4', status: 'working', turns: 3, text: 'Other words', since: '2026-11-03T19:01:00Z' }));
  assert.equal(m.observe(session({ from: 'schedule', label: '4', turns: 3, text: 'Other words' })).fresh, false);
  assert.deepEqual(m.observe(session({ from: 'schedule', label: '4', turns: 4, text: 'Other words' })), { repeat: false, at: '2026-11-03T19:01:00Z', fresh: false });
});

test('a host that doesn\'t count turns never has a repeat folded', () => {
  const m = new PromptMemory();
  m.observe(session({ from: 'schedule', label: '4', turns: null }));
  m.observe(session({ from: 'schedule', label: '4', status: 'working', turns: null }));
  assert.equal(m.observe(session({ from: 'schedule', label: '4', turns: null })).repeat, false);
});
