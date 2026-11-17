import assert from 'node:assert/strict';
import { test } from 'node:test';
import { readStatusJson } from './contract.mjs';

const now = Date.parse('2026-11-17T16:00:00Z');
const at = (minutesAgo = 0) => new Date(now - minutesAgo * 60_000).toISOString();
const document = () => ({
  schemaVersion: 1,
  generatedAt: at(),
  delivery: {
    observedAt: at(),
    mergesPerHour: { agentBridge: 1, fleet: 2, roadmap: 3 },
    minutesSinceLastMerge: { agentBridge: 2, fleet: 3, roadmap: 4 },
    mainCi: { state: 'green', ageMinutes: 1 },
    actionsQueue: { queued: 0, inProgress: 1 },
    pullRequests: { ready: 1, armed: 1, blocked: 0 },
    flakeCount: 0,
  },
  fleet: {
    observedAt: at(),
    liveSessions: 2,
    builders: { working: 1, idle: 0 },
    reviewers: { working: 0, idle: 1 },
    agentErrors: 0,
    machinesOnline: 3,
    cpuBands: { low: 2, medium: 1, high: 0 },
    memoryBands: { low: 2, medium: 1, high: 0 },
    readyIssues: 1,
  },
  systems: { enabled: false },
  quota: {
    observedAt: at(),
    githubRemainingPercent: [10, 99],
    modelConcurrency: { active: 1, cap: 2 },
    vmLoadBands: { low: 2, medium: 0, high: 0 },
  },
  programs: { enabled: false },
  incidents: { observedAt: at(), items: [] },
});
const read = (value = document()) => readStatusJson(JSON.stringify(value), now);

test('A valid status document is returned with its derived banner and unavailable unproduced rows', () => {
  const result = read();
  assert.equal(result.state, 'available');
  assert.equal(result.banner, 'Operational');
  assert.deepEqual(result.groups.systems, { state: 'unavailable', counted: false });
  assert.deepEqual(result.groups.programs, { state: 'unavailable', counted: false });
});

test('An enabled Programs group is counted only when its timestamp is fresh', () => {
  const value = document();
  value.programs = {
    enabled: true,
    observedAt: at(),
    haRetirement: { done: 2, total: 4, parity: 'pending' },
    fleetView: 'ready',
    f7: 'shadow',
  };
  assert.equal(read(value).groups.programs.counted, true);
  value.programs.fleetView = 'degraded'; assert.equal(read(value).banner, 'Degraded');
  value.programs.observedAt = at(11);
  assert.deepEqual(read(value).groups.programs, { state: 'unavailable', counted: true });
});

test('A missing, unknown, malformed, or oversized value fails closed', () => {
  const bad = [
    { ...document(), extra: 1 },
    { ...document(), schemaVersion: '1' },
    { ...document(), delivery: { ...document().delivery, privateTitle: 'hidden' } },
    { ...document(), fleet: { ...document().fleet, liveSessions: -1 } },
    { ...document(), fleet: { ...document().fleet, cpuBands: { low: 0, medium: 0, high: 0 } } },
    { ...document(), incidents: { observedAt: at(), items: [{ state: 'active', startedAt: at(), title: 'secret' }] } },
    { ...document(), incidents: { observedAt: at(), items: [{ state: 'resolved', startedAt: at() }] } },
    { ...document(), quota: undefined },
  ];
  for (const value of bad) { assert.equal(read(value).state, 'unavailable'); }
  assert.equal(readStatusJson('x'.repeat(65_537), now).state, 'unavailable');
  assert.equal(readStatusJson('{', now).state, 'unavailable');
  assert.equal(readStatusJson(JSON.stringify(document()), Number.NaN).state, 'unavailable');
  const tooMany = document();
  tooMany.incidents.items = Array.from({ length: 21 }, () => ({ state: 'active', startedAt: at() }));
  assert.equal(read(tooMany).state, 'unavailable');
});

test('Whole-document freshness accepts exactly ten minutes and rejects older or future data', () => {
  const exact = document();
  exact.generatedAt = at(10);
  assert.equal(read(exact).state, 'available');
  exact.generatedAt = at(10 + 1 / 60_000);
  assert.deepEqual(read(exact), { state: 'unavailable', reason: 'stale' });
  exact.generatedAt = new Date(now + 1).toISOString();
  assert.deepEqual(read(exact), { state: 'unavailable', reason: 'future' });
});

test('A group accepts the exact freshness limit and hides stale or future values', () => {
  const value = document();
  value.delivery.observedAt = at(10);
  assert.equal(read(value).groups.delivery.state, 'fresh');
  value.delivery.observedAt = at(10 + 1 / 60_000);
  let result = read(value);
  assert.deepEqual(result.groups.delivery, { state: 'unavailable', counted: true });
  assert.equal(result.banner, 'Degraded');
  value.delivery.observedAt = new Date(now + 1).toISOString();
  result = read(value);
  assert.deepEqual(result.groups.delivery, { state: 'unavailable', counted: true });
  assert.equal(result.groups.delivery.data, undefined);
});

test('Any stale required group is hidden without reading its values', () => {
  for (const name of ['delivery', 'fleet', 'quota', 'incidents']) {
    const value = document();
    value[name].observedAt = at(11);
    const result = read(value);
    assert.deepEqual(result.groups[name], { state: 'unavailable', counted: true });
    assert.equal(result.banner, 'Degraded');
  }
});

test('Enabled F-system rows become unavailable after ten minutes but accept the exact boundary', () => {
  const value = document();
  const row = { state: 'live', heartbeatAgeMinutes: 10 };
  value.systems = {
    enabled: true,
    observedAt: at(),
    f2: row, f3: row, f4: row, f5: row, f6: row, f7: { state: 'shadow', heartbeatAgeMinutes: 10 }, f8: row,
    countsProducer: row, peopleProducer: row, telemetry: row,
  };
  assert.equal(read(value).groups.systems.rows.f2.state, 'live');
  value.systems.f2 = { state: 'live', heartbeatAgeMinutes: 10.01 };
  assert.deepEqual(read(value).groups.systems.rows.f2, { state: 'unavailable' });
});

test('Every banner threshold has a tested boundary', () => {
  const cases = [
    [d => { d.delivery.mainCi.state = 'red'; }, 'Degraded'],
    [d => { d.delivery.mainCi.state = 'running'; d.delivery.mainCi.ageMinutes = 60; }, 'Operational'],
    [d => { d.delivery.mainCi.state = 'running'; d.delivery.mainCi.ageMinutes = 61; }, 'Degraded'],
    [d => { d.delivery.actionsQueue.queued = 5; }, 'Operational'],
    [d => { d.delivery.actionsQueue.queued = 6; }, 'Degraded'],
    [d => { d.delivery.pullRequests.blocked = 1; }, 'Degraded'],
    [d => { d.delivery.flakeCount = 1; }, 'Degraded'],
    [d => { d.fleet.agentErrors = 1; }, 'Degraded'],
    [d => { d.fleet.readyIssues = 2; }, 'Degraded'],
    [d => { d.fleet.cpuBands.low = 1; d.fleet.cpuBands.high = 1; }, 'Degraded'],
    [d => { d.fleet.memoryBands.low = 1; d.fleet.memoryBands.high = 1; }, 'Degraded'],
    [d => { d.quota.githubRemainingPercent = [10]; }, 'Operational'],
    [d => { d.quota.githubRemainingPercent = [9]; }, 'Degraded'],
    [d => { d.quota.modelConcurrency = { active: 1, cap: 2 }; }, 'Operational'],
    [d => { d.quota.modelConcurrency = { active: 2, cap: 2 }; }, 'Degraded'],
    [d => { d.quota.vmLoadBands.high = 1; }, 'Degraded'],
    [d => { d.incidents.items = [{ state: 'active', startedAt: at() }]; }, 'Degraded'],
  ];
  for (const [change, expected] of cases) {
    const value = document();
    change(value);
    assert.equal(read(value).banner, expected);
  }
});

test('An active incident with blocked merges or lost live sessions is an outage', () => {
  const incident = document();
  incident.incidents.items = [{ state: 'active', startedAt: at(), blockedMerges: true, label: 'Merges delayed' }];
  assert.equal(read(incident).banner, 'Outage');
  const fleet = document();
  fleet.fleet.machinesOnline = 0;
  fleet.fleet.cpuBands = { low: 0, medium: 0, high: 0 };
  fleet.fleet.memoryBands = { low: 0, medium: 0, high: 0 };
  assert.equal(read(fleet).banner, 'Outage');
  fleet.fleet.liveSessions = 0;
  assert.equal(read(fleet).banner, 'Operational');
});

test('Only closed incident labels and fields are accepted', () => {
  const value = document();
  const labels = ['Merges delayed', 'Builds or tests failing', 'Fleet capacity reduced', 'Messaging delayed', 'Updates delayed', 'Usage limit reached', 'Status data delayed', 'Other'];
  for (const label of labels) {
    value.incidents.items = [{ state: 'active', startedAt: at(), label }];
    assert.equal(read(value).state, 'available');
  }
  value.incidents.items[0].label = 'Private free text';
  assert.equal(read(value).state, 'unavailable');
});

test('An incident with a future timestamp is unavailable', () => {
  const value = document();
  value.incidents.items = [{ state: 'active', startedAt: new Date(now + 1).toISOString() }];
  assert.equal(read(value).state, 'unavailable');
});
