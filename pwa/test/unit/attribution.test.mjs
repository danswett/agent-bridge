// Who said what: the main agent's own words and its delegates (js/attribution.js), read from
// what a host sends (docs/protocol/v1-sessions.md, Activity and Subagent).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { readSession } from '../../js/protocol.js';
import { delegateList, delegateTime, mainVoice } from '../../js/attribution.js';

const here = (p) => fileURLToPath(new URL(p, import.meta.url));
const session = (fields = {}) => readSession({ id: 's1', agent: 'copilot', status: 'idle', ...fields });
const voice = (activity, fields = {}) => mainVoice(session({ activity, ...fields }));

test('a delegate\'s response is never shown as the main agent\'s: its own text is', () => {
  const v = voice({ response: 'Bisect over the last 10 commits.', source: { response: 'agent-3a91' }, own: { response: 'Started 2 background agents.' } });
  assert.equal(v.response, 'Started 2 background agents.');
  assert.equal(v.attributed, true);
});

test('a delegate\'s reasoning beside the main agent\'s response leaves each in its place', () => {
  const v = voice({ response: 'HEAD: 1 failing test.', reasoning: 'Check CI before saying so.', source: { reasoning: 'agent-c204' }, own: { reasoning: 'Two jobs: delegate both.' } });
  assert.equal(v.response, 'HEAD: 1 failing test.');
  assert.equal(v.reasoning, 'Two jobs: delegate both.');
});

test('a field the host attributes to a delegate, with no own text, means the main agent has none', () => {
  const v = voice({ response: 'Bisect over the last 10 commits.', reasoning: 'Check CI.', source: { response: 'agent-3a91', reasoning: 'agent-c204' } });
  assert.equal(v.response, null);
  assert.equal(v.reasoning, null);
  assert.equal(v.responseDropped, false);
});

test('a main-agent response the budget dropped says so rather than showing the delegate\'s', () => {
  const v = voice({ response: 'Bisect over the last 10 commits.', source: { response: 'agent-3a91' }, own: { responseTruncated: true } });
  assert.equal(v.response, null);
  assert.equal(v.responseDropped, true);
  const cut = voice({ response: 'Bisect.', source: { response: 'agent-3a91' }, own: { response: '…the end of it.', responseTruncated: true } });
  assert.deepEqual([cut.response, cut.responseTruncated, cut.responseDropped], ['…the end of it.', true, false]);
});

test('an older host with no source shows the top-level text, unchanged and unlabelled', () => {
  const v = voice({ response: 'Done. The test now waits for the socket to drain.', responseTruncated: true, reasoning: 'Plan.' });
  assert.deepEqual(v, { response: 'Done. The test now waits for the socket to drain.', responseTruncated: true, responseDropped: false, reasoning: 'Plan.', attributed: false });
});

test('the main agent\'s own text fills in where the top-level text is absent', () => {
  const v = voice({ own: { response: 'From the skimmed transcript.', reasoning: 'Skimmed too.' } });
  assert.deepEqual([v.response, v.reasoning], ['From the skimmed transcript.', 'Skimmed too.']);
});

test('source and own of the wrong shape are read as absent', () => {
  const s = session({ activity: { response: 'Top.', source: 'agent-3a91', own: ['x'] } });
  assert.deepEqual([s.activity.source, s.activity.own], [null, null]);
  assert.equal(mainVoice(s).response, 'Top.');
  const empty = session({ activity: { response: 'Top.', source: { response: 4 }, own: { response: {} } } });
  assert.deepEqual([empty.activity.source, empty.activity.own, mainVoice(empty).response], [null, null, 'Top.']);
});

test('the worked example shows the main agent\'s own words, not either delegate\'s', () => {
  const example = readSession(JSON.parse(readFileSync(here('../../../docs/protocol/examples/session-subagents.json'), 'utf8')));
  const v = mainVoice(example);
  assert.equal(v.response, 'Started 2 background agents: run-suite and witness-logic.');
  assert.equal(v.reasoning, 'Two independent jobs: delegate both.');
});

const delegate = (id, fields = {}) => ({ id, kind: 'background', name: `Delegate ${id}`, status: 'working', ...fields });
const rows = (list) => list.map((r) => `${'  '.repeat(r.depth)}${r.id}:${r.glyph}`);

test('delegates list running ones first, then idle ones, each nested under its parent, with the finished apart', () => {
  const d = delegateList(session({
    subagents: [
      delegate('idle', { status: 'idle', outcome: 'done' }),
      delegate('a'),
      delegate('a1', { parent: 'a', kind: 'task' }),
      delegate('b', { status: 'waiting' }),
      delegate('a1x', { parent: 'a1', kind: 'task' }),
      delegate('done', { status: 'ended', outcome: 'done', kind: 'task' }),
      delegate('done1', { status: 'ended', outcome: 'failed', parent: 'done', kind: 'task' }),
      delegate('orphan', { status: 'ended', parent: 'a', kind: 'task' }),
    ],
    subagentsMore: 2,
  }));
  assert.deepEqual(rows(d.active), ['a:working', '  a1:working', '    a1x:working', 'b:needs', 'idle:idle']);
  assert.deepEqual(rows(d.finished), ['done:done', '  done1:failed', 'orphan:ended']);
  assert.equal(d.more, 2);
});

test('a finished delegate\'s glyph comes from its outcome', () => {
  const d = delegateList(session({ subagents: [delegate('x', { status: 'ended', outcome: 'failed' }), delegate('y', { status: 'ended', outcome: 'done' })] }));
  assert.deepEqual(d.finished.map((r) => [r.glyph, r.words]), [['failed', 'Failed'], ['done', 'Done']]);
});

test('a parent loop from a broken host still lists every delegate once', () => {
  const d = delegateList(session({ subagents: [delegate('a', { parent: 'b' }), delegate('b', { parent: 'a' }), delegate('c', { parent: 'c' })] }));
  assert.deepEqual(d.active.map((r) => r.id).sort(), ['a', 'b', 'c']);
});

test('a host that lists no delegates gets no Delegates block, whatever its background count', () => {
  assert.equal(delegateList(session({ activity: { background: { agents: 2, shells: 0 } } })), null);
  assert.deepEqual(delegateList(session({ subagents: [], subagentsMore: 3 })), { active: [], finished: [], more: 3 });
  // The budget can drop every entry and keep only the count: still a host that lists them.
  assert.deepEqual(delegateList(session({ subagentsMore: 3 })), { active: [], finished: [], more: 3 });
});

test('a running delegate says how long it has run, a finished one that it finished', () => {
  const now = Date.parse('2026-11-01T20:00:00Z');
  assert.equal(delegateTime({ status: 'working', startedAt: '2026-11-01T19:56:00Z' }, now), '4m');
  assert.equal(delegateTime({ status: 'ended', endedAt: '2026-11-01T19:57:00Z' }, now), 'finished 3m ago');
  assert.equal(delegateTime({ status: 'idle', endedAt: null }, now), 'finished');
});
