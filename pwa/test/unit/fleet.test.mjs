import { test } from 'node:test';
import assert from 'node:assert/strict';
import { FleetStatus } from '../../js/fleet.js';
import { UsageBook } from '../../js/usage.js';
import { SessionsStore } from '../../js/sessions-store.js';

const flush = async () => { for (let i = 0; i < 10; i++) { await new Promise((r) => setImmediate(r)); } };
const usage = (percent) => ({ agents: [{ agent: 'copilot', label: 'GitHub Copilot', account: 'octo', source: 'api', measuredAt: '2026-10-28T20:00:00.000Z', windows: [{ key: 'plan', label: 'Plan', percent }] }] });

function listeners() {
  const map = new Map();
  return {
    on(type, fn) { if (!map.has(type)) { map.set(type, new Set()); } map.get(type).add(fn); return () => map.get(type).delete(fn); },
    emit(type, detail) { for (const fn of map.get(type) ?? []) { fn(detail); } },
  };
}

// A relay connection whose channels go to scripted hosts: each answers hello and usage.get.
function fakeConnection(hosts) {
  const l = listeners();
  const opened = [];
  let open = 0;
  let mostAtOnce = 0;
  return {
    ...l,
    ready: true,
    presence: Object.keys(hosts).map((id) => ({ id, name: id, kind: 'host' })),
    opened,
    get mostAtOnce() { return mostAtOnce; },
    async openChannel(id) {
      opened.push(id);
      open++;
      mostAtOnce = Math.max(mostAtOnce, open);
      await flush();
      return { id, closed: false, close() { if (!this.closed) { this.closed = true; open--; } } };
    },
    hosts,
  };
}

function fakeClient(connection) {
  return (channel) => ({
    channel,
    async hello() { const h = connection.hosts[channel.id]; if (h.fail) { throw new Error('no answer'); } return { machine: h.machine, capabilities: h.capabilities }; },
    async usage() { return connection.hosts[channel.id].usage; },
    close() { channel.close(); },
  });
}

function fakeHosts() {
  const l = listeners();
  return { onChange: (fn) => l.on('change', fn), change() { l.emit('change'); }, targetId: null, phase: 'none', client: null, machine: null, capabilities: [] };
}

function fleetWith(hostsById, { now = () => 0 } = {}) {
  const connection = fakeConnection(hostsById);
  const hosts = fakeHosts();
  const store = new SessionsStore();
  const book = new UsageBook();
  const fleet = new FleetStatus({ connection, hosts, store, book, me: 'me', now, doc: null, makeClient: fakeClient(connection) });
  return { connection, hosts, store, book, fleet };
}

test('every other online machine is asked once, one at a time, for its status and usage', async () => {
  const { connection, book, fleet } = fleetWith({
    a: { machine: { hostVersion: '0.1.0', dev: true, sessions: 3 }, capabilities: ['sessions.read', 'usage'], usage: usage(40) },
    b: { machine: { hostVersion: '0.1.0', sessions: 1 }, capabilities: ['sessions.read'] },
  });

  fleet.probe();
  fleet.probe();
  await fleet.idle();

  assert.deepEqual(connection.opened, ['a', 'b']);
  assert.equal(connection.mostAtOnce, 1);
  assert.equal(fleet.status('a'), '3 sessions \u00b7 0.1.0 (dev)');
  assert.equal(fleet.status('b'), '1 session \u00b7 0.1.0');
  assert.equal(book.size, 1);
  assert.equal(book.entries()[0].windows[0].percent, 40);
});

test('a machine is not asked again within the minute, and is afterwards', async () => {
  let now = 0;
  const { connection, fleet } = fleetWith({ a: { machine: { hostVersion: '0.1.0' }, capabilities: [] } }, { now: () => now });
  fleet.probe();
  await fleet.idle();
  now = 59000;
  fleet.probe();
  await fleet.idle();
  assert.deepEqual(connection.opened, ['a']);
  now = 60000;
  fleet.probe();
  await fleet.idle();
  assert.deepEqual(connection.opened, ['a', 'a']);
});

test('a machine that does not answer is not asked again in a hot loop, and its channel is closed', async () => {
  const { connection, fleet } = fleetWith({ a: { fail: true } });
  fleet.probe();
  await fleet.idle();
  fleet.probe();
  await fleet.idle();
  assert.deepEqual(connection.opened, ['a']);
  assert.equal(fleet.status('a'), '');
});

test('the machine being viewed is never probed; its usage comes on its own channel and its count is live', async () => {
  const { connection, hosts, store, book, fleet } = fleetWith({ v: { machine: {}, capabilities: [] } });
  let onUsage = null;
  const client = { synced: true, on: (type, fn) => { if (type === 'usage') { onUsage = fn; } }, subscribeUsage: async () => usage(10) };
  Object.assign(hosts, { targetId: 'v', phase: 'open', client, machine: { hostVersion: '0.1.0', dev: true, sessions: 0 }, capabilities: ['sessions.read', 'usage'] });
  hosts.change();
  await flush();

  assert.deepEqual(connection.opened, []);
  assert.equal(book.entries()[0].windows[0].percent, 10);
  onUsage(usage(12));
  assert.equal(book.entries()[0].windows[0].percent, 12);
  store.upsert({ id: 's1', agent: 'copilot', status: 'idle', updatedAt: '2026-10-28T20:00:00.000Z', activity: {} });
  assert.equal(fleet.status('v'), '1 session \u00b7 0.1.0 (dev)');

  fleet.clear();
  assert.equal(book.size, 0);
  assert.equal(fleet.status('v'), '');
});

test('a machine left alone is looked at again every minute while the app is in front, and not while it is hidden', async (t) => {
  t.mock.timers.enable({ apis: ['setInterval'] });
  let now = 0;
  const doc = { visibilityState: 'visible', listeners: [], addEventListener(type, fn) { this.listeners.push(fn); } };
  const connection = fakeConnection({ a: { machine: { hostVersion: '0.1.0', sessions: 1 }, capabilities: [] } });
  const fleet = new FleetStatus({ connection, hosts: fakeHosts(), store: new SessionsStore(), book: new UsageBook(), me: 'me', now: () => now, doc, makeClient: fakeClient(connection) });
  fleet.start();
  fleet.probe();
  await fleet.idle();
  assert.deepEqual(connection.opened, ['a']);

  now = 60000;
  t.mock.timers.tick(60000);
  await fleet.idle();
  assert.deepEqual(connection.opened, ['a', 'a']);

  doc.visibilityState = 'hidden';
  for (const fn of doc.listeners) { fn(); }
  now = 180000;
  t.mock.timers.tick(120000);
  await fleet.idle();
  assert.deepEqual(connection.opened, ['a', 'a']);

  doc.visibilityState = 'visible';
  for (const fn of doc.listeners) { fn(); }
  await fleet.idle();
  assert.deepEqual(connection.opened, ['a', 'a', 'a']);
  fleet.clear();
});