import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readDiagnostics, diagnosticsText, pacific, refusalText, DiagnoseControl, DIAGNOSTICS } from '../../js/diagnostics.js';
import { BRAND } from '../../js/brand.js';

const CANARY = 'ghp_CANARYtoken0123456789abcdef';
const snapshot = (more = {}) => ({
  schema: 1, at: '2026-11-03T12:00:00+00:00',
  host: { version: '0.4.2', dev: false, startedAt: '2026-11-03T10:30:00+00:00', uptimeSeconds: 5400 },
  lastRestart: { at: '2026-11-03T10:30:00+00:00', causeMissing: 'notRecorded' },
  relay: { state: 'connected', since: null },
  holds: { readOnly: 'none', discoveryHeld: [] },
  readiness: { checkedAt: '2026-11-03T11:59:00+00:00', omitted: 0, agents: [] },
  journal: { entries: [], omittedByLimit: 0, omittedBySize: 0, invalid: 0, evictedByCount: 0, evictedByAge: 0, evictedByBytes: 0, rejectedOversized: 0, rejectedMalformed: 0, droppedOnLoad: 0, loadFailed: false, persistFailures: 0 },
  ...more,
});

const agent = (more = {}) => ({
  id: 'copilot', version: '1.0.3', lastHookHandledAt: null,
  installation: 'working', versionCheck: 'working', hooks: 'working', authentication: 'working', activation: 'working', ...more,
});

test('nothing but closed codes, versions, times and counts reaches the text, whatever a host sends', () => {
  const d = readDiagnostics(snapshot({
    relay: { state: 'connected', since: null, url: `wss://relay/?t=${CANARY}`, detail: CANARY },
    holds: { readOnly: CANARY, discoveryHeld: ['claude', CANARY], reason: CANARY },
    readiness: { checkedAt: null, omitted: 0, agents: [agent({ id: CANARY, label: CANARY, home: `C:\\Users\\${CANARY}`, version: `1.0 ${CANARY}`, installation: CANARY, authentication: 'failed' })] },
    journal: { ...snapshot().journal, entries: [CANARY, { seq: 2, at: CANARY, family: 'Crash' }, { seq: 1, at: '2026-11-03T11:00:00Z', family: 'Crash', kind: `${CANARY} x`, outcome: 'Failed', message: CANARY,
      exception: { type: `C:\\${CANARY}`, frames: [{ type: 'AgentBridge.Host.Program', method: `Main ${CANARY}` }], message: CANARY }, stages: [{ name: CANARY, ms: 1 }] }] },
  }));
  const text = diagnosticsText(d, 'Laptop');
  assert.ok(!text.includes(CANARY), text);
  assert.ok(!text.includes('wss:'), text);
  assert.ok(!text.includes('Main'), text);
  assert.equal(d.holds.readOnly, 'unknown');
  assert.deepEqual(d.holds.discoveryHeld, ['claude']);
  assert.equal(d.readiness.agents[0].id, 'unknown');
  assert.equal(d.readiness.agents[0].version, null);
  assert.equal(d.journal.entries.length, 1);
  assert.equal(d.journal.invalid, 2);
  assert.deepEqual(d.journal.entries[0].exception, { type: '<unrecognized>', frames: 1 });
});

test('a version is the journal\'s grammar only: numbers and a closed set of prerelease words', () => {
  const versionOf = (v) => readDiagnostics(snapshot({ host: { ...snapshot().host, version: v } })).host;
  for (const ok of ['0.4.2', '0.4', '1.2.3.4', '0.4.2-beta', '0.4.2-rc.1', '10.0.0-preview.1.2']) { assert.equal(versionOf(ok).version, ok); }
  for (const bad of [`0.4.2-${CANARY}`, CANARY, '0.4.2+abc', '0', '1.2.3.4.5', '0.4.2-beta.x', `0.4.2-beta.${'1'.repeat(70)}`]) {
    assert.deepEqual([versionOf(bad).version, versionOf(bad).versionMissing], [null, 'invalid'], bad);
  }
});

test('an exception type is shown only from the runtime\'s closed list; any other name, however it looks, is not', () => {
  const typeOf = (type) => readDiagnostics(snapshot({
    journal: { ...snapshot().journal, entries: [{ seq: 1, at: '2026-11-03T11:00:00Z', family: 'Crash', kind: 'UnhandledException', outcome: 'Failed', exception: { type, frames: [] } }] },
  })).journal.entries[0].exception.type;
  assert.equal(typeOf('System.IO.IOException'), 'System.IO.IOException');
  assert.equal(typeOf('System.Threading.Channels.ChannelClosedException'), 'System.Threading.Channels.ChannelClosedException');
  for (const bad of [`System.${CANARY}`, `System.${CANARY}Exception`, `AgentBridge.${CANARY}`, 'System.IO', 'system.ioexception']) {
    assert.equal(typeOf(bad), '<unrecognized>', bad);
  }
});

test('the runtime exception list is the journal\'s own', async () => {
  const { readFile } = await import('node:fs/promises');
  const cs = await readFile(new URL('../../../src/AgentBridge.Protocol/Journal.cs', import.meta.url), 'utf8');
  const js = await readFile(new URL('../../js/diagnostics.js', import.meta.url), 'utf8');
  const listed = cs.slice(cs.indexOf('RuntimeExceptions ='), cs.indexOf('];', cs.indexOf('RuntimeExceptions ='))).match(/typeof\((\w+)\)/g).map((t) => t.slice(7, -1));
  const mirrored = js.slice(js.indexOf('EXCEPTION_TYPES'), js.indexOf('].map(', js.indexOf('EXCEPTION_TYPES'))).match(/'[\w.]+'/g).map((n) => n.slice(1, -1).split('.').pop());
  assert.deepEqual(mirrored, listed);
});

test('a snapshot missing a required section or field, or with one of another type, is unreadable, never healthy', () => {
  assert.equal(readDiagnostics({ schema: 1 }), null);
  const s = snapshot({ readiness: { checkedAt: null, omitted: 0, agents: [agent()] } });
  assert.ok(readDiagnostics(s));
  const without = (path) => {
    const copy = structuredClone(s);
    const keys = path.split('.');
    const last = keys.pop();
    delete keys.reduce((o, k) => o[k], copy)[last];
    return copy;
  };
  const set = (path, v) => {
    const copy = structuredClone(s);
    const keys = path.split('.');
    const last = keys.pop();
    keys.reduce((o, k) => o[k], copy)[last] = v;
    return copy;
  };
  for (const path of ['at', 'host', 'host.version', 'host.dev', 'host.startedAt', 'host.uptimeSeconds', 'lastRestart', 'lastRestart.at', 'lastRestart.causeMissing',
    'relay', 'relay.state', 'relay.since', 'holds', 'holds.readOnly', 'holds.discoveryHeld', 'readiness', 'readiness.checkedAt', 'readiness.agents', 'readiness.omitted',
    'readiness.agents.0.id', 'readiness.agents.0.version', 'readiness.agents.0.hooks', 'readiness.agents.0.lastHookHandledAt',
    'journal', 'journal.entries', 'journal.invalid', 'journal.loadFailed', 'journal.persistFailures']) {
    assert.equal(readDiagnostics(without(path)), null, path);
  }
  for (const [path, v] of [['host.dev', 'true'], ['host.uptimeSeconds', '5'], ['host.startedAt', null], ['relay.since', 'yesterday'], ['readiness.agents', [null]],
    ['readiness.agents.0', CANARY], ['holds.discoveryHeld', [null]], ['journal.loadFailed', 0], ['at', CANARY], ['journal.entries', {}]]) {
    assert.equal(readDiagnostics(set(path, v)), null, path);
  }
  // Nullable fields may be null, and the optional reasons absent.
  assert.ok(readDiagnostics(set('holds.discoveryHeld', null)));
  assert.ok(readDiagnostics(set('host.version', null)));
  // readOnly may be null, said unknown, never none, when the host has no reason with it.
  assert.match(diagnosticsText(readDiagnostics(set('holds.readOnly', null))), /read-only: unavailable \(unknown\)/);
  // So may the kinds discovery holds: only an empty list is none, and a reason sent is said.
  const held = (v, missing) => diagnosticsText(readDiagnostics(snapshot({ holds: { readOnly: 'none', discoveryHeld: v, ...(missing ? { discoveryMissing: missing } : {}) } })));
  assert.equal(readDiagnostics(set('holds.discoveryHeld', null)).holds.discoveryHeld, null);
  assert.match(held(null), /discovery held: unavailable \(unknown\)\n/);
  assert.match(held([]), /discovery held: none\n/);
  assert.match(held(null, 'failed'), /discovery held: unavailable \(failed\)\n/);
  assert.match(held(['claude']), /discovery held: claude\n/);
  // A kind this doesn't know is held too: not known alone, never none, and said beside known kinds.
  assert.match(held(['futureAgent']), /discovery held: unavailable \(unknown\)\n/);
  assert.match(held(['claude', 'futureAgent']), /discovery held: claude, unknown\n/);
  assert.doesNotMatch(held(['claude', 'futureAgent']), /futureAgent/);
});

test('a time on a day the calendar does not have is refused in a snapshot and unreadable in the journal, as .NET reads it', () => {
  const entry = (at) => ({ seq: 1, at, family: 'Crash', kind: 'UnhandledException', outcome: 'Failed' });
  const impossible = ['2025-02-29T00:00:00Z', '2025-02-30T00:00:00Z', '2024-02-30T00:00:00Z', '1900-02-29T00:00:00Z', '2026-04-31T00:00:00Z', '2026-13-01T00:00:00Z',
    '2026-00-10T00:00:00Z', '2026-11-00T00:00:00Z', '0000-01-01T00:00:00Z', '2026-11-03T24:00:00Z', '2026-11-03T12:60:00Z', '2026-11-03T12:00:60Z',
    '2026-11-03T12:00:00+14:01', '2026-11-03T12:00:00+05:60'];
  for (const at of impossible) {
    assert.equal(readDiagnostics(snapshot({ at })), null, at);
    assert.equal(readDiagnostics(snapshot({ host: { ...snapshot().host, startedAt: at } })), null, at);
    const d = readDiagnostics(snapshot({ journal: { ...snapshot().journal, entries: [entry(at)] } }));
    assert.deepEqual([d.journal.entries.length, d.journal.invalid], [0, 1], at);
  }
  for (const at of ['2024-02-29T23:59:59Z', '2000-02-29T00:00:00Z', '2026-12-31T23:59:59.9999999Z', '2026-11-03T12:00:00-14:00']) {
    assert.ok(readDiagnostics(snapshot({ at })), at);
    assert.equal(readDiagnostics(snapshot({ journal: { ...snapshot().journal, entries: [entry(at)] } })).journal.entries.length, 1, at);
  }
});

test('a journal entry numbered below one, which the journal never writes, is unreadable rather than shown as #0', () => {
  const entry = (seq) => ({ seq, at: '2026-11-03T11:00:00Z', family: 'Crash', kind: 'UnhandledException', outcome: 'Failed' });
  for (const seq of [0, -1, -9007199254740991]) {
    const d = readDiagnostics(snapshot({ journal: { ...snapshot().journal, entries: [entry(seq)] } }));
    assert.deepEqual([d.journal.entries.length, d.journal.invalid], [0, 1], String(seq));
    assert.doesNotMatch(diagnosticsText(d), /#0|#-/);
  }
  assert.equal(readDiagnostics(snapshot({ journal: { ...snapshot().journal, entries: [entry(1)] } })).journal.entries[0].seq, 1);
});

test('what a host does not know is said as unavailable, never as healthy', () => {
  const text = diagnosticsText(readDiagnostics(snapshot({
    host: { version: null, versionMissing: 'invalid', dev: false, startedAt: '2026-11-03T10:30:00+00:00', uptimeSeconds: 0 },
    relay: { state: 'unknown', since: null, missing: 'notStarted' },
    readiness: { checkedAt: null, agents: [], omitted: 0, missing: 'notStarted' },
    holds: { readOnly: null, readOnlyMissing: 'notStarted', discoveryHeld: [] },
    journal: { ...snapshot().journal, missing: 'notConfigured' },
  })));
  assert.match(text, /host: unavailable \(invalid\)/);
  assert.match(text, /relay: unavailable \(notStarted\)/);
  assert.match(text, /read-only: unavailable \(notStarted\)/);
  assert.match(text, /readiness: unavailable \(notStarted\)/);
  assert.match(text, /journal: unavailable \(notConfigured\)/);
  assert.equal(readDiagnostics({ ...snapshot(), schema: 2 }), null);
});

test('the report names the product, the machine and the time, and shows times in Pacific', () => {
  const text = diagnosticsText(readDiagnostics(snapshot({ lastRestart: { at: '2025-01-01T00:00:00Z', causeMissing: 'notRecorded' } })), 'Laptop');
  assert.equal(text.split('\n')[0], `${BRAND.name} diagnostics \u00B7 Laptop \u00B7 2026-11-03 04:00:00 PST`);
  assert.ok(!text.includes('#802'), text);
  assert.match(text, /last restart: 2024-12-31 16:00:00 PST/);
  assert.equal(pacific('2026-07-01T19:30:00Z'), '2026-07-01 12:30:00 PDT');
  assert.equal(pacific('2026-03-08T10:00:00Z'), '2026-03-08 03:00:00 PDT');
  assert.equal(pacific('2026-03-08T09:59:59Z'), '2026-03-08 01:59:59 PST');
});
test('an older host is not offered and never asked; a newer one is copied only after review', async () => {
  const calls = [];
  const copied = [];
  let offered = false;
  const fleet = { diagnosable: () => offered, call: async (id, op, data) => { calls.push([id, op, data]); return snapshot(); } };
  let now = 0;
  const control = new DiagnoseControl({ fleet, copy: async (text) => { copied.push(text); return true; }, now: () => now });

  assert.equal(control.offered('m'), false);
  await control.press('m', 'Laptop');
  assert.equal(control.stateOf('m').phase, 'idle');
  assert.deepEqual(calls, []);

  offered = true;
  await control.press('m', 'Laptop');
  assert.deepEqual(calls, [['m', DIAGNOSTICS.get, { maxEntries: DIAGNOSTICS.entries }]]);
  assert.equal(control.stateOf('m').phase, 'review');
  assert.match(control.stateOf('m').text, /^.* diagnostics \u00B7 Laptop \u00B7 /);
  assert.equal(await control.copyReviewed('m'), false);
  assert.deepEqual(copied, []);
  control.review('m', true);
  assert.equal(await control.copyReviewed('m'), true);
  assert.deepEqual(copied, [control.stateOf('m').text]);

  // Pressed again at once: not asked again before the host would answer.
  await control.press('m');
  assert.equal(calls.length, 1);
  now += DIAGNOSTICS.minIntervalMs;
  await control.press('m');
  assert.equal(calls.length, 2);
  assert.equal(control.stateOf('m').reviewed, false);
});

test('the host\'s three reasons are said in the person\'s words; any other is not shown', async () => {
  const hostError = (reason) => Object.assign(new Error(`diagnostics.get: ${reason}`), { reason, fromHost: true });
  const noteFor = async (error) => {
    const control = new DiagnoseControl({ fleet: { diagnosable: () => true, call: async () => { throw error; } }, copy: async () => true });
    await control.press('m', 'Laptop');
    assert.equal(control.stateOf('m').phase, 'idle');
    return control.stateOf('m').note;
  };
  assert.equal(await noteFor(hostError("diagnostics are for a person's device approved at this machine, through the relay")), 'Approve this device at Laptop to diagnose it');
  assert.equal(await noteFor(hostError('diagnostics were read moments ago; try again shortly')), 'Diagnosed moments ago \u00B7 try again shortly');
  assert.equal(await noteFor(hostError('diagnostics could not be read in time; try again shortly')), "Laptop didn't answer in time \u00B7 try again");
  for (const e of [hostError(`refused ${CANARY}`), new Error(`refused ${CANARY}`), { reason: "diagnostics are for a person's device approved at this machine, through the relay" }, null]) {
    assert.equal(await noteFor(e), "Laptop couldn't be reached \u00B7 try again");
  }
  assert.equal(refusalText('constructor', 'Laptop'), "Laptop couldn't be reached \u00B7 try again");
});
