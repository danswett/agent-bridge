import { test } from 'node:test';
import assert from 'node:assert/strict';
import { FleetRegistryView } from '../../js/fleet-registry-view.js';

// Slice 6 (docs/design/fleet-session-registry.md): the PWA as a registry reader, the JS port of the host's
// FleetRegistryReader (FleetRegistryReaderTests.cs); the same cases, the same answers.
const Epoch = '0123456789abcdef';
const Other = 'fedcba9876543210';
const M = 'aaaaaaaaaaaaaaaa';
const T0 = Date.parse('2026-11-22T12:00:00Z');

function setup(options = {}) {
  const clock = { wall: T0, mono: 0 };
  const view = new FleetRegistryView({ now: () => clock.wall, mono: () => clock.mono, ...options });
  const advance = (ms) => { clock.wall += ms; clock.mono += ms; };
  const record = ({ seq = 1, epoch = Epoch, age = 0, machine = M } = {}) =>
    ({ v: 1, machine, epoch, seq, at: new Date(clock.wall - age * 1000).toISOString(), truncated: false, sessions: [] });
  const part = (rec, { online = true, machine = M } = {}) =>
    ({ t: 'registry', machine, name: 'box', online, lastSeen: new Date(clock.wall).toISOString(), record: rec });
  const changed = (rec, machine = M) => ({ t: 'registry-changed', machine, online: true, record: rec });
  const known = (...ids) => ({ t: 'registry', known: ids.map((machine) => ({ machine, name: 'k', online: false })) });
  const only = () => { const s = view.snapshot(); assert.equal(s.length, 1); return s[0]; };
  return { view, advance, record, part, changed, known, only };
}

test('within an epoch a lower seq is refused and an equal one accepted; a new epoch needs a later at', () => {
  const { view, advance, record, part, changed, known, only } = setup();
  view.applyGet([part(record({ seq: 5 })), known()]);
  view.applyChanged(changed(record({ seq: 4 })));
  assert.equal(only().record.seq, 5);
  view.applyChanged(changed(record({ seq: 5 })));
  view.applyChanged(changed(record({ seq: 6 })));
  assert.equal(only().record.seq, 6);

  advance(10_000);
  view.applyChanged(changed(record({ seq: 1, epoch: Other, age: 20 })));
  assert.equal(only().record.seq, 6);
  view.applyChanged(changed(record({ seq: 1, epoch: Other })));
  assert.equal(only().record.epoch, Other);
  view.applyChanged(changed(record({ seq: 9 })));
  assert.equal(only().record.epoch, Other);
});

test('a record more than sixty seconds from the clock is refused and a held one goes stale', () => {
  const { view, advance, record, part, changed, known, only } = setup();
  view.applyGet([part(record({ age: 61 })), known()]);
  assert.equal(only().record, null);
  view.applyChanged(changed(record({ seq: 1, age: -61 })));
  assert.equal(only().record, null);
  view.applyChanged(changed(record({ seq: 2 })));
  assert.equal(only().live, true);
  advance(61_000);
  const stale = only();
  assert.equal(stale.live, false);
  assert.notEqual(stale.record, null);
});

test('an offline change keeps the last copy as stale and the relay online flag alone never makes it live', () => {
  const { view, record, part, changed, known, only } = setup();
  view.applyChanged(changed(record()));
  view.applyChanged({ t: 'registry-changed', machine: M, online: false, record: null });
  const gone = only();
  assert.equal(gone.live, false);
  assert.equal(gone.online, false);
  assert.notEqual(gone.record, null);
  view.applyGet([part(record({ seq: 2 }), { online: false }), known()]);
  assert.equal(only().live, false);
});

test('removed purges the machine and its history so a reconnecting machine is accepted at any epoch', () => {
  const { view, record, changed, only } = setup();
  view.applyChanged(changed(record({ seq: 50, epoch: Other })));
  view.applyChanged({ t: 'registry-changed', machine: M, removed: true });
  assert.deepEqual(view.snapshot(), []);
  view.applyChanged(changed(record({ seq: 1, epoch: Epoch, age: 5 })));
  const back = only();
  assert.equal(back.record.epoch, Epoch);
  assert.equal(back.live, true);
});

test('a get drops machines it no longer lists and keeps a listed but silent machine offline without a record', () => {
  const { view, record, part, changed, known } = setup();
  const gone = 'bbbbbbbbbbbbbbbb';
  const quiet = 'cccccccccccccccc';
  view.applyChanged(changed(record({ machine: gone }), gone));
  view.applyChanged(changed(record()));
  view.applyGet([part(record({ seq: 2 })), known(M)]);
  assert.deepEqual(view.snapshot().map((m) => m.id), [M]);
  view.applyGet([known(M, quiet)]);
  const q = view.snapshot().find((m) => m.id === quiet);
  assert.equal(q.online, false);
  assert.equal(q.record, null);
  assert.notEqual(view.snapshot().find((m) => m.id === M).record, null);
});

test('a cached copy is deleted after twenty four hours on the monotonic clock', () => {
  const { view, advance, record, changed } = setup();
  view.applyChanged(changed(record()));
  advance(24 * 3600_000 - 1000);
  assert.equal(view.snapshot().length, 1);
  advance(1000);
  assert.deepEqual(view.snapshot(), []);
});

test('once an order is held a record with a missing or malformed epoch, seq or at is refused', () => {
  const { view, record, changed, only } = setup();
  view.applyChanged(changed(record({ seq: 3 })));
  const at = new Date(T0).toISOString();
  const base = { v: 1, machine: M, epoch: Epoch, seq: 9, at, truncated: false, sessions: [] };
  const { epoch, ...noEpoch } = base;
  for (const bad of [noEpoch, { ...base, seq: 0 }, { ...base, seq: 1.5 }, { ...base, at: 'yesterday' }, { ...base, machine: 'bbbbbbbbbbbbbbbb' }]) {
    view.applyChanged(changed(bad));
  }
  assert.equal(only().record.seq, 3);
});

test('a record is verified only when the verifier says so and one it refuses is not kept', () => {
  const a = setup();
  a.view.applyChanged(a.changed(a.record()));
  assert.equal(a.only().verified, false);
  const yes = setup({ verify: () => true });
  yes.view.applyChanged(yes.changed(yes.record()));
  assert.equal(yes.only().verified, true);
  const no = setup({ verify: () => false });
  no.view.applyChanged(no.changed(no.record()));
  assert.deepEqual(no.view.snapshot(), []);
});
