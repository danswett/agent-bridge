import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import * as readers from '../../js/load-feed.js';

const records = JSON.parse(readFileSync(new URL('../../../docs/protocol/vectors/machine-load.json', import.meta.url))).records;
const full = () => structuredClone(records.MachineLoad.valid.find((v) => v.name === 'full').json);

test('the shared 201-session input retains exactly the largest 200 working sets and becomes partial', () => {
  const vector = records.MachineLoad.valid.find((v) => v.json.sessions?.length === 201);
  assert.ok(vector, 'the shared #1876 cap vector must exist, not a local replacement');
  const read = readers.readMachineLoad(vector.json);
  assert.deepEqual(read, vector.reads);
  assert.equal(read.state, 'partial');
  assert.equal(read.sessions.length, 200);
  assert.deepEqual(read.sessions.map((s) => s.session),
    [...vector.json.sessions].sort((a, b) => b.workingSetBytes - a.workingSetBytes).slice(0, 200).map((s) => s.session));
});

test('capping sessions never improves a worse state, even when state follows sessions on the wire', () => {
  const sessions = Array.from({ length: 201 }, (_, i) => ({ session: `s-${i}`, workingSetBytes: i }));
  for (const [state, expected] of [['fresh', 'partial'], ['partial', 'partial'], ['stale', 'stale'],
    ['unavailable', 'unavailable'], ['future', 'unavailable'], [undefined, 'unavailable']]) {
    const raw = { sessions, ...(state === undefined ? {} : { state }) };
    const before = structuredClone(raw);
    const read = readers.readMachineLoad(raw);
    assert.equal(read.state, expected);
    assert.equal(read.sessions.length, 200);
    assert.equal(read.sessions[0].session, 's-200');
    assert.equal(read.sessions.at(-1).session, 's-1');
    assert.deepEqual(raw, before, 'decoding must not sort or truncate the input');
  }
});

test('malformed nested records and strict session identities are omitted without invented figures', () => {
  const raw = { ageMs: 'many', staleAfterMs: null, sampledAt: 'not a time', cpu: [], memory: {},
    unattributed: { processes: 'many', workingSetBytes: 0 },
    sessions: [null, [], {}, { session: 42 }, { session: 's-1', handles: 'many', agent: 'future', state: 'future', handleKind: 'future' }] };
  assert.deepEqual(readers.readMachineLoad(raw), { state: 'unavailable', memory: {}, unattributed: { workingSetBytes: 0 },
    sessions: [{ session: 's-1', agent: 'other', state: 'unavailable', handleKind: 'other' }] });
  assert.deepEqual(readers.readMachineLoad({ cpu: {}, memory: {}, unattributed: {} }),
    { state: 'unavailable', sessions: [], cpu: {}, memory: {}, unattributed: {} });
  for (const read of [readers.readMachineLoad, readers.readSessionLoad, readers.readMachineLoadCpu,
    readers.readMachineLoadMemory, readers.readUnattributedLoad]) {
    for (const raw of [null, [], 42, 'load']) { assert.equal(read(raw), null); }
  }
});

test('record decoding preserves finite percentages but omits nonfinite and unrepresentable counts', () => {
  assert.deepEqual(readers.readMachineLoadCpu({ percent: 101.25, cores: 1.5 }), { percent: 101.25 });
  assert.deepEqual(readers.readMachineLoadCpu({ percent: Infinity, cores: 2147483648 }), {});
  assert.deepEqual(readers.readMachineLoadMemory({ totalBytes: 1.5, availableBytes: NaN, committedPercent: -1 }),
    { committedPercent: -1 });
  assert.deepEqual(readers.readSessionLoad({ session: 's', processes: -1, handles: 2147483648, workingSetBytes: 1.5, cpuPercent: NaN }),
    { session: 's', agent: 'other', state: 'unavailable', processes: -1 });
});

test('unknown fields at every record level are dropped while all known numerical fields survive', () => {
  const raw = full(), expected = full();
  for (const record of [raw, raw.cpu, raw.memory, raw.unattributed, ...raw.sessions]) { record.future = 'ignored'; }
  assert.deepEqual(readers.readMachineLoad(raw), expected);
});
