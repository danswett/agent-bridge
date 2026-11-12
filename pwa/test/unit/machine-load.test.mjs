import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import * as readers from '../../js/load-feed.js';

const records = JSON.parse(readFileSync(new URL('../../../docs/protocol/vectors/machine-load.json', import.meta.url))).records;
const full = () => structuredClone(records.MachineLoad.valid.find((v) => v.name === 'full').json);
const overCap = () => records.MachineLoad.valid.find((v) => v.json.sessions?.length === 201);

test('the shared 201-session input retains exactly the largest 200 working sets and becomes partial', () => {
  const vector = overCap();
  assert.ok(vector, 'the shared #1876 cap vector must exist, not a local replacement');
  const read = readers.readMachineLoad(vector.json);
  assert.deepEqual(read, vector.reads);
  assert.equal(read.state, 'partial');
  assert.equal(read.sessions.length, 200);
  assert.deepEqual(read.sessions.map((s) => s.session),
    [...vector.json.sessions].sort((a, b) => b.workingSetBytes - a.workingSetBytes).slice(0, 200).map((s) => s.session));
});

test('capping sessions never improves a worse state, even when state follows sessions on the wire', () => {
  const vector = overCap(), sessions = structuredClone(vector.json.sessions);
  for (const [state, expected] of [['fresh', 'partial'], ['partial', 'partial'], ['stale', 'stale'],
    ['unavailable', 'unavailable'], ['future', 'unavailable'], [undefined, 'unavailable']]) {
    const raw = { sessions, ...(state === undefined ? {} : { state }) };
    const before = structuredClone(raw);
    const read = readers.readMachineLoad(raw);
    assert.equal(read.state, expected);
    assert.equal(read.sessions.length, 200);
    assert.deepEqual(read.sessions, vector.reads.sessions);
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

test('sample times reject calendar rollover and out-of-range dates and offsets as C# does', () => {
  for (const sampledAt of ['2026-02-30T00:00:00.000Z', '2025-02-29T00:00:00.000Z',
    '0000-01-01T00:00:00.000Z', '2026-11-11T24:00:00.000Z', '2026-11-11T12:00:00.000+14:01']) {
    assert.equal(readers.readMachineLoad({ sampledAt }).sampledAt, undefined, sampledAt);
  }
  const sampledAt = '2024-02-29T12:00:00.000Z';
  assert.equal(readers.readMachineLoad({ sampledAt }).sampledAt, sampledAt);
});
