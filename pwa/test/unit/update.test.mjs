import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readUpdateState, readUpdateCommandResult, updateLine, lastLine, canInstall, canCheck, underWay, UpdateControl } from '../../js/update.js';

const NOW = Date.parse('2026-11-02T18:00:00.000Z');
const found = readUpdateState({ current: '0.4.0', installer: true, available: '0.5.0', checkedAt: '2026-11-02T17:55:00.000Z', phase: 'idle' });

test('a state from a newer host reads safely: an unknown phase as idle and an unknown outcome as failed', () => {
  const s = readUpdateState({ installer: true, phase: 'rebooting', progress: 140, last: { outcome: 'rolled-back', at: '2026-11-02T18:00:00.000Z' } });
  assert.equal(s.phase, 'idle');
  assert.equal(s.progress, 100);
  assert.equal(s.last.outcome, 'failed');
  assert.deepEqual(readUpdateCommandResult({ outcome: 'maybe' }), { outcome: 'refused', reason: null, state: null });
});

test('each phase reads as a line, and Install is offered only when a release is found and nothing is under way', () => {
  assert.equal(updateLine(found, NOW), '0.5.0 is available (running 0.4.0).');
  assert.ok(canInstall(found) && canCheck(found) && !underWay(found));
  const downloading = { ...found, phase: 'downloading', progress: 40 };
  assert.equal(updateLine(downloading, NOW), 'Downloading 0.5.0 (40%)\u2026');
  assert.ok(!canInstall(downloading) && underWay(downloading));
  const waiting = { ...found, phase: 'waiting', waiting: 'a session on this machine has a turn running', request: 'r1' };
  assert.equal(updateLine(waiting, NOW), '0.5.0 installs when it can: a session on this machine has a turn running.');
  assert.ok(underWay(waiting));
  // Waiting for idle by itself (automatic updates), asked by nobody: nothing to watch closely.
  assert.ok(!underWay({ ...waiting, request: null }));
  assert.equal(updateLine({ ...found, available: null }, NOW), 'Up to date: 0.4.0, checked 5m ago.');
  // No check finished yet: not called up to date.
  assert.equal(updateLine({ ...found, available: null, checkedAt: null }, NOW), 'Running 0.4.0; not checked for updates yet.');
  const noApp = readUpdateState({ current: '0.4.0', installer: false, unavailable: 'the Agent Bridge app is not running on this machine, and it is what installs updates' });
  assert.equal(updateLine(noApp, NOW), 'The Agent Bridge app is not running on this machine, and it is what installs updates.');
  assert.ok(!canCheck(noApp));
});

test('the last outcome reads as what happened', () => {
  const at = '2026-11-02T17:00:00.000Z';
  assert.equal(lastLine({ last: { outcome: 'updated', from: '0.4.0', to: '0.5.0', at } }, NOW), 'Updated from 0.4.0 to 0.5.0 1h 0m ago.');
  assert.equal(lastLine({ last: { outcome: 'refused', reason: "another profile's host runs from this installation", at } }, NOW), "Not updated: another profile's host runs from this installation.");
  assert.equal(lastLine({ last: { outcome: 'failed', reason: 'the package is damaged', at } }, NOW), 'The update failed: the package is damaged.');
  assert.equal(lastLine(found, NOW), null);
});

function fakeFleet(answer) {
  const states = new Map([['a', found], ['b', { ...found, phase: 'installing' }], ['c', found]]);
  const calls = [];
  const watched = [];
  return {
    calls,
    watched,
    states,
    update: (id) => states.get(id) ?? null,
    noteUpdate: (id, s) => states.set(id, s),
    watch: (id) => watched.push(id),
    async call(id, op, data) { calls.push({ id, op, data }); return answer(id, op); },
  };
}

test('Install sends one press with a request of its own, and a refusal shows on that row until the next press', async () => {
  const fleet = fakeFleet(() => ({ outcome: 'refused', reason: 'only a person\'s phone, browser or Windows app can ask a machine to update' }));
  let n = 0;
  const control = new UpdateControl({ fleet, newRequest: () => `r${++n}` });
  const pressing = control.press('a', 'install');
  assert.equal(control.stateOf('a').busy, true);
  // A second press while the first is on its way does nothing.
  await control.press('a', 'install');
  await pressing;

  assert.deepEqual(fleet.calls, [{ id: 'a', op: 'update.install', data: { request: 'r1' } }]);
  assert.deepEqual(control.stateOf('a'), { busy: false, note: "Only a person's phone, browser or Windows app can ask a machine to update." });
});

test('an accepted press takes the state the machine answered with, and an unreachable machine says so', async () => {
  const fleet = fakeFleet((id) => {
    if (id === 'c') { throw new Error('the machine is offline'); }
    return { outcome: 'accepted', state: { ...found, phase: 'checking' } };
  });
  const control = new UpdateControl({ fleet, newRequest: () => 'r' });
  await control.press('a', 'check');
  assert.equal(fleet.update('a').phase, 'checking');
  assert.equal(control.stateOf('a').note, null);
  // Looked at closely now, whatever the state says yet.
  assert.deepEqual(fleet.watched, ['a']);
  await control.press('c', 'check');
  assert.equal(control.stateOf('c').note, 'It did not take it: the machine is offline.');
});

test('Check all asks every machine that can check now, and none that is busy installing', async () => {
  const fleet = fakeFleet(() => ({ outcome: 'accepted' }));
  const control = new UpdateControl({ fleet, newRequest: () => 'r' });
  await control.checkAll(['a', 'b', 'c', 'gone']);
  assert.deepEqual(fleet.calls.map((c) => [c.id, c.op]), [['a', 'update.check'], ['c', 'update.check']]);
});

test("a press's answer does not replace an update.state that came while it was on its way", async () => {
  const fleet = fakeFleet(() => {
    // The machine's app took it and reported, before the answer (the state from before) arrived.
    fleet.states.set('a', { ...found, phase: 'downloading', progress: 30 });
    return { outcome: 'accepted', state: found };
  });
  const control = new UpdateControl({ fleet, newRequest: () => 'r' });
  await control.press('a', 'install');
  assert.equal(fleet.update('a').phase, 'downloading');
});
