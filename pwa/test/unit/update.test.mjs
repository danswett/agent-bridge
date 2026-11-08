import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  readUpdateState, readUpdateCommandResult, updateLine, lastLine, canInstall, canCheck, underWay, UpdateControl,
  canUpdateNow, updateNowReason, notUpdated, nowSheet, acceptOf, quiet, readBusy, nowAnnouncement, RESTARTING, BUSY_MOST,
} from '../../js/update.js';
import { signUpdateNow } from '../../js/transfer.js';
import { clockTime } from '../../js/auth-state.js';
import { P256KeyPair } from '../../js/noise-p256.js';
import { deviceIdOf } from '../../js/identity.js';
import { fromUtf8 } from '../../js/bytes.js';
import { fromBase64url } from '../../js/blobs.js';

const NOW = Date.parse('2026-11-02T18:00:00.000Z');
const found = readUpdateState({ current: '0.4.0', installer: true, available: '0.5.0', checkedAt: '2026-11-02T17:55:00.000Z', phase: 'idle' });

test('a state from a newer host reads safely: an unknown phase as idle and an unknown outcome as failed', () => {
  const s = readUpdateState({ installer: true, phase: 'rebooting', progress: 140, last: { outcome: 'rolled-back', at: '2026-11-02T18:00:00.000Z' } });
  assert.equal(s.phase, 'idle');
  assert.equal(s.progress, 100);
  assert.equal(s.last.outcome, 'failed');
  assert.deepEqual(readUpdateCommandResult({ outcome: 'maybe' }), { outcome: 'refused', reason: null, state: null, code: null });
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

test("a machine whose update needs you says so in any phase, in the host's own words", () => {
  const needsYou = 'Needs you: the update to 0.5.0 has waited since 11/2/2026 9:00 AM because it is downloaded and waits for Restart to update on this machine';
  const idle = { ...found, waiting: needsYou };
  assert.equal(updateLine(idle, NOW), `${needsYou}.`);
  assert.ok(canInstall(idle));
  assert.equal(updateLine({ ...idle, phase: 'checking' }, NOW), `${needsYou}.`);
});

// Creative on #719 (6674096426): an update the backstop holds back for a question waiting reads
// exactly as the host words it.
test('a machine whose update waits for a question to be answered says so in the host\'s words', () => {
  const held = { ...found, phase: 'waiting', automatic: true, waiting: 'Update ready \u00b7 waiting for a question to be answered' };
  assert.equal(updateLine(held, NOW), 'Update ready \u00b7 waiting for a question to be answered');
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
  assert.deepEqual(control.stateOf('a'), { busy: false, asking: false, note: "Only a person's phone, browser or Windows app can ask a machine to update." });
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

// ---- Update now (#1159) ----

const ready = readUpdateState({
  current: '0.4.0', installer: true, available: '0.5.0', checkedAt: '2026-11-02T17:55:00.000Z', phase: 'idle', canUpdateNow: true,
  busy: { working: 0, waiting: 0 },
});
const busyTwo = readBusy({ working: 1, waiting: 1, sessions: [{ id: 's-turn', state: 'working' }, { id: 's-ask', state: 'approval' }] });

test('a busy state reads approvals first, and no count past 1,000,000', () => {
  assert.deepEqual(busyTwo.sessions.map((s) => s.state), ['approval', 'working']);
  const huge = readBusy({ working: 5_000_000, waiting: -3, moreApprovals: 2.5, moreOther: BUSY_MOST + 1 });
  assert.deepEqual([huge.working, huge.waiting, huge.moreApprovals, huge.moreOther], [BUSY_MOST, 0, 0, BUSY_MOST]);
  assert.ok(quiet(ready.busy) && !quiet(busyTwo) && !quiet(readBusy({ working: 0, waiting: 0, unknown: true })));
});

test('no Update now is offered on a machine that cannot press it, and the row says why in its own words', () => {
  assert.ok(canUpdateNow(ready));
  assert.equal(updateNowReason(ready), null);
  const why = (code, extra = {}) => updateNowReason({ ...ready, canUpdateNow: false, updateNowWhy: code, ...extra });
  assert.ok(!canUpdateNow({ ...ready, canUpdateNow: false, updateNowWhy: 'old-app' }));
  assert.equal(why('disabled'), 'Update now is off on this machine');
  assert.equal(why('old-app'), 'Update now needs a newer app there. Install still works.');
  assert.equal(why('busy'), 'Another update or restart is under way there');
  assert.equal(why('restart-cap', { updateNowAt: '2026-11-02T20:30:00.000Z' }), `Update now is available again at ${clockTime('2026-11-02T20:30:00.000Z')}`);
  assert.equal(why('restart-cap'), 'Update now is available again later');
  // A code this device doesn't know, or nothing ready: nothing said, and no button.
  assert.equal(why('something-new'), null);
  assert.equal(updateNowReason({ ...ready, available: null, canUpdateNow: false, updateNowWhy: 'disabled' }), null);
  // Nothing newer, or the app downloading already: no button either.
  assert.ok(!canUpdateNow({ ...ready, available: null }));
  assert.ok(!canUpdateNow({ ...ready, phase: 'downloading' }));
});

test("an Update now waiting, downloading or restarting is worded from its state, never the machine's waiting text", () => {
  const waiting = (code) => updateLine({ ...ready, phase: 'waiting', requestNow: true, waitingCode: code, waiting: 'host words nobody sees' }, NOW);
  assert.equal(waiting('turn-running'), 'Update ready \u00b7 waiting for the current turn to finish');
  assert.equal(waiting('question-waiting'), 'Update ready \u00b7 waiting for a question to be answered');
  assert.equal(waiting('usage-check'), 'Update ready \u00b7 waiting for a Codex usage check to finish');
  assert.equal(waiting('unknown'), 'Update ready \u00b7 waiting until sessions here are quiet');
  // A code missing or new to this device: the unknown line, still never the machine's text.
  assert.equal(waiting('something-new'), 'Update ready \u00b7 waiting until sessions here are quiet');
  assert.equal(waiting(null), 'Update ready \u00b7 waiting until sessions here are quiet');
  assert.equal(waiting('constructor'), 'Update ready \u00b7 waiting until sessions here are quiet');
  assert.equal(updateLine({ ...ready, phase: 'downloading', progress: 40, requestNow: true }, NOW), 'Downloading 0.5.0 \u00b7 40%');
  assert.equal(updateLine({ ...ready, phase: 'installing', requestNow: true }, NOW), RESTARTING);
});

test("an Update now's outcome is worded from its code, never the machine's reason", () => {
  const last = (l) => lastLine({ ...ready, updateNowAt: null, last: { at: '2026-11-02T17:00:00.000Z', reason: 'host words nobody sees', ...l } }, NOW);
  assert.equal(last({ outcome: 'refused', code: 'expired' }), 'Not updated: not done within 15 minutes. Press Update now again.');
  assert.equal(last({ outcome: 'refused', code: 'usage-check' }), 'Not updated: a Codex usage check was still running');
  assert.equal(last({ outcome: 'refused', code: 'release-changed' }), 'Not updated: the update changed. Check for updates again.');
  assert.equal(last({ outcome: 'refused', code: 'not-quiet' }), 'Not updated: a message was being delivered there the whole time.');
  assert.equal(last({ outcome: 'refused', code: 'restart-cap' }), 'Not updated: Update now has used its restarts for the last 6 hours; try again later.');
  assert.equal(last({ outcome: 'refused', code: 'someday-code' }), 'Not updated: the machine refused it.');
  assert.equal(last({ outcome: 'failed', code: 'other-profile' }), "Not updated: the update didn't take.");
  // One this device pressed reads as Update now's even without a code.
  assert.equal(lastLine({ last: { outcome: 'updated', from: '0.4.0', to: '0.5.0', request: 'r1' } }, NOW, { updateNow: true }), 'Updated to 0.5.0.');
  assert.equal(notUpdated('not-person'), "Not updated: this device can't update that machine.");
});

test('the confirmation lists each busy session by name and state, says how many it left out and warns of a waiting approval', () => {
  const names = { 's-ask': 'Fix the relay' };
  const sheet = nowSheet({ machine: 'Laptop', busy: busyTwo, nameOf: (id) => names[id] ?? null });
  assert.equal(sheet.title, 'Update Laptop now?');
  assert.deepEqual(sheet.sessions.map((s) => [s.name, s.state]), [['Fix the relay', 'Approval waiting'], ['A session', 'Mid-turn']]);
  assert.equal(sheet.more, null);
  assert.equal(sheet.pause, 'They pause while the bridge restarts, usually under a minute.');
  assert.equal(sheet.warn, 'A question or approval waiting may have to be answered at the machine afterwards.');
  // Turns only: no warning. Past 32: how many more, and how many of those wait on an approval.
  assert.equal(nowSheet({ machine: 'L', busy: readBusy({ working: 1, waiting: 0, sessions: [{ id: 'a', state: 'working' }] }) }).warn, null);
  const many = readBusy({ working: 3, waiting: 32, sessions: Array.from({ length: 32 }, (_, i) => ({ id: `s${i}`, state: 'approval' })), moreApprovals: 1, moreOther: 2 });
  assert.equal(nowSheet({ machine: 'L', busy: many }).more, 'and 3 more, 1 of them with an approval waiting');
  // Can't tell: it says so, and warns.
  const unsure = nowSheet({ machine: 'L', busy: readBusy({ working: 0, waiting: 0, unknown: true }) });
  assert.equal(unsure.unknown, "Some sessions there haven't reported yet.");
  assert.ok(unsure.warn);
  // What is sent back is what was shown.
  assert.deepEqual(acceptOf(many), { working: 3, waiting: 32, sessions: many.sessions, moreApprovals: 1, moreOther: 2 });
});

function nowFleet(state, answers) {
  const states = new Map([['a', state]]);
  const calls = [];
  return {
    calls,
    states,
    update: (id) => states.get(id) ?? null,
    noteUpdate: (id, s) => states.set(id, s),
    watch: () => {},
    async call(id, op, data) {
      const made = typeof data === 'function' ? await data(new Uint8Array([4])) : data;
      calls.push({ id, op, data: made });
      return answers.shift() ?? { outcome: 'accepted' };
    },
  };
}
const signs = [];
const fakeSign = async (a) => { signs.push(a); return { body: 'b', by: 'y', mac: 'm' }; };

test('Update now on a quiet machine goes on one press, asking nothing and accepting nothing', async () => {
  signs.length = 0;
  const fleet = nowFleet(ready, []);
  let asked = 0;
  const control = new UpdateControl({ fleet, newRequest: () => 'r1', sign: fakeSign });
  await control.pressNow('a', { machine: 'Laptop', confirm: async () => { asked += 1; return true; } });
  assert.equal(asked, 0);
  assert.deepEqual(fleet.calls, [{ id: 'a', op: 'update.install', data: { now: { body: 'b', by: 'y', mac: 'm' } } }]);
  assert.deepEqual(signs.map(({ release, accept, request }) => ({ release, accept, request })), [{ release: '0.5.0', accept: null, request: 'r1' }]);
  assert.ok(control.isUpdateNow('r1'));
});

test('Cancel on a busy machine sends nothing, and Update now sends what the confirmation showed', async () => {
  signs.length = 0;
  const fleet = nowFleet({ ...ready, busy: busyTwo }, []);
  const control = new UpdateControl({ fleet, newRequest: () => 'r2', sign: fakeSign });
  await control.pressNow('a', { machine: 'Laptop', confirm: async () => false });
  assert.deepEqual(fleet.calls, []);
  let shown = null;
  await control.pressNow('a', { machine: 'Laptop', confirm: async (sheet) => { shown = sheet; return true; } });
  assert.deepEqual(shown.sessions.map((s) => s.state), ['Approval waiting', 'Mid-turn']);
  assert.deepEqual(signs[0].accept, { working: 1, waiting: 1, sessions: [{ id: 's-ask', state: 'approval' }, { id: 's-turn', state: 'working' }] });
});

test('a confirm outcome shows the confirmation again with what is busy now, and a refusal is worded from its code', async () => {
  signs.length = 0;
  const now = { working: 1, waiting: 2, sessions: [{ id: 'q', state: 'question' }, { id: 'p', state: 'approval' }, { id: 't', state: 'working' }] };
  const fleet = nowFleet(ready, [
    { outcome: 'confirm', reason: 'busier', state: { ...ready, busy: now } },
    { outcome: 'refused', reason: 'host words nobody sees', code: 'rate-limited' },
  ]);
  const sheets = [];
  let n = 0;
  const control = new UpdateControl({ fleet, newRequest: () => `r${++n}`, sign: fakeSign });
  await control.pressNow('a', { machine: 'Laptop', confirm: async (sheet) => { sheets.push(sheet); return true; } });
  // Quiet when pressed: no confirmation then; the machine's confirm brought it up, with what it named.
  assert.equal(sheets.length, 1);
  assert.deepEqual(sheets[0].sessions.map((s) => [s.id, s.state]), [['p', 'Approval waiting'], ['q', 'Question waiting'], ['t', 'Mid-turn']]);
  assert.deepEqual(signs.map((s) => [s.request, s.accept?.waiting ?? null]), [['r1', null], ['r2', 2]]);
  assert.equal(control.stateOf('a').note, 'Not updated: too many presses just now; try again in a few minutes.');
});

test('a confirmation open when the account is cleared closes as Cancel, and nothing is signed or sent', async () => {
  signs.length = 0;
  const fleet = nowFleet({ ...ready, busy: busyTwo }, []);
  const control = new UpdateControl({ fleet, newRequest: () => 'r9', sign: fakeSign });
  let signal = null;
  let answer = null;
  const pressed = control.pressNow('a', { machine: 'Laptop', confirm: (sheet, o) => { signal = o.signal; return new Promise((r) => { answer = r; }); } });
  await new Promise((r) => setImmediate(r));
  assert.equal(control.stateOf('a').asking, true);
  control.clear();
  assert.equal(signal.aborted, true);
  // Even a confirmation that answered Update now as it went is not sent.
  answer(true);
  await pressed;
  assert.deepEqual([fleet.calls, signs], [[], []]);
  assert.equal(control.stateOf('a').asking, false);
});

test('a machine that left for an Update now reads Restarting to update for 90 seconds, then Offline', async () => {
  let clock = 0;
  const fleet = nowFleet({ ...ready, phase: 'installing', requestNow: true }, []);
  const control = new UpdateControl({ fleet, sign: fakeSign, now: () => clock, restartMs: 90000 });
  assert.equal(control.restarting('a', true), true);
  assert.deepEqual(control.leavingIds(), ['a']);
  clock = 1000;
  assert.equal(control.restarting('a', false), true);
  clock = 90000;
  assert.equal(control.restarting('a', false), true);
  clock = 91001;
  assert.equal(control.restarting('a', false), false);
  // A plain Install restarting is not Update now's: Offline as ever.
  const plain = new UpdateControl({ fleet: nowFleet({ ...ready, phase: 'installing' }, []), sign: fakeSign });
  assert.equal(plain.restarting('a', false), false);
  control.clear();
});

test('an Update now is announced once as each step starts, never at each percent', () => {
  const down = { ...ready, phase: 'downloading', progress: 10, requestNow: true };
  const first = nowAnnouncement(down);
  assert.equal(first.text, 'Downloading 0.5.0');
  assert.equal(nowAnnouncement({ ...down, progress: 60 }, first.key).text, null);
  assert.equal(nowAnnouncement({ ...down, phase: 'installing' }, first.key).text, RESTARTING);
});

test('an Update now press is signed for that machine alone, lives two minutes, and binds now, the release and accept', async () => {
  const [me, machine] = await Promise.all([P256KeyPair.generate(), P256KeyPair.generate()]);
  const deviceId = await deviceIdOf(me.public);
  const accept = acceptOf(busyTwo);
  const command = await signUpdateNow({ key: me, deviceId, machinePublic: machine.public, release: '0.5.0', accept, request: '7d0f3a2e-1c4b-4d5e-8f60-112233445566', now: 1_793_300_000_000 });
  const body = JSON.parse(fromUtf8.decode(fromBase64url(command.body)));
  const id = await deviceIdOf(machine.public);
  assert.deepEqual(body, {
    v: 1, action: 'update.install.now', request: '7d0f3a2e-1c4b-4d5e-8f60-112233445566', principal: { kind: 'user', device: deviceId },
    target: { machine: id }, to: id, issuedAt: 1_793_300_000, expiresAt: 1_793_300_120,
    args: { now: true, release: '0.5.0', accept },
  });
});
