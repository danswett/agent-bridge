import { test } from 'node:test';
import assert from 'node:assert/strict';
import { FleetStatus } from '../../js/fleet.js';
import { UsageBook } from '../../js/usage.js';
import { SessionsStore } from '../../js/sessions-store.js';
import { readUpdateState } from '../../js/update.js';

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
    async updateState() { return readUpdateState(connection.hosts[channel.id].update); },
    async request(op, data) {
      const h = connection.hosts[channel.id];
      (h.requests ??= []).push({ op, data });
      return h.answer?.(op, data) ?? {};
    },
    close() { channel.close(); },
  });
}

// The machines shown, as HostSession has them: each a link with its own channel and store.
function fakeHosts() {
  const l = listeners();
  const s = listeners();
  return {
    shown: [],
    onChange: (fn) => l.on('change', fn),
    change() { l.emit('change'); },
    sessions: { onChange: (fn) => s.on('change', fn) },
    links() { return this.shown; },
    link(id) { return this.shown.find((x) => x.id === id) ?? null; },
    shows(id) { return this.shown.some((x) => x.id === id); },
    // Shows a machine, open, as when its channel is up and subscribed.
    view(link) {
      const store = link.store ?? new SessionsStore();
      store.onChange((c) => s.emit('change', c));
      this.shown.push({ phase: 'open', store, ...link });
      this.change();
      return this.shown.at(-1);
    },
  };
}

function fleetWith(hostsById, { now = () => 0 } = {}) {
  const connection = fakeConnection(hostsById);
  const hosts = fakeHosts();
  const book = new UsageBook();
  const fleet = new FleetStatus({ connection, hosts, book, me: 'me', now, doc: null, makeClient: fakeClient(connection) });
  return { connection, hosts, book, fleet };
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

test('a machine that stops offering usage has its last report dropped, and one that cannot be reached keeps it', async () => {
  let now = 0;
  const { connection, book, fleet } = fleetWith({
    a: { machine: { hostVersion: '0.1.0' }, capabilities: ['usage'], usage: usage(40) },
    b: { machine: { hostVersion: '0.1.0' }, capabilities: ['usage'], usage: usage(20) },
  }, { now: () => now });
  fleet.probe();
  await fleet.idle();
  assert.equal(book.size, 2);

  // a restarted with usage.publish off; b went away.
  connection.hosts.a.capabilities = ['sessions.read'];
  connection.hosts.b.fail = true;
  now = 60000;
  fleet.probe();
  await fleet.idle();

  assert.equal(book.size, 1);
  assert.equal(book.entries()[0].windows[0].percent, 20);
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
  const { connection, hosts, book, fleet } = fleetWith({ v: { machine: {}, capabilities: [] } });
  let onUsage = null;
  const client = { synced: true, on: (type, fn) => { if (type === 'usage') { onUsage = fn; } }, subscribeUsage: async () => usage(10) };
  const { store } = hosts.view({ id: 'v', client, machine: { hostVersion: '0.1.0', dev: true, sessions: 0 }, capabilities: ['sessions.read', 'usage'] });
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
  const fleet = new FleetStatus({ connection, hosts: fakeHosts(), book: new UsageBook(), me: 'me', now: () => now, doc, makeClient: fakeClient(connection) });
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
test("a look still under way when its machine is chosen does not overwrite what the machine's own channel says", async () => {
  let release;
  const held = new Promise((r) => { release = r; });
  const { connection, hosts, book, fleet } = fleetWith({ a: { machine: { hostVersion: '0.1.0' }, capabilities: ['usage'], usage: usage(40) } });
  const makeClient = fakeClient(connection);
  fleet.makeClient = (ch) => ({ ...makeClient(ch), async usage() { await held; return usage(40); } });
  fleet.probe();
  await flush();

  const client = { synced: true, on: () => {}, subscribeUsage: async () => usage(55) };
  hosts.view({ id: 'a', client, machine: { hostVersion: '0.1.0' }, capabilities: ['sessions.read', 'usage'] });
  await flush();
  release();
  await fleet.idle();

  assert.equal(book.entries()[0].windows[0].percent, 55);
});

test('the machine being viewed is asked again on its own channel every minute, and its status says when it is read-only', async () => {
  let now = 0;
  const { connection, hosts, fleet } = fleetWith({ v: { machine: {}, capabilities: [] } }, { now: () => now });
  let refreshed = 0;
  const client = { synced: false, on() {}, subscribeUsage: async () => usage(10) };
  hosts.refresh = async (id) => {
    refreshed++;
    hosts.link(id).machine = { hostVersion: '0.1.0', readOnly: 'agent-ha-bridge is running for this user' };
    hosts.change();
  };
  hosts.view({ id: 'v', client, machine: { hostVersion: '0.1.0' }, capabilities: ['sessions.read'] });
  await flush();
  assert.equal(refreshed, 0);

  now = 30000;
  fleet.probe();
  assert.equal(refreshed, 0);
  now = 60000;
  fleet.probe();
  await flush();
  assert.equal(refreshed, 1);
  assert.deepEqual(connection.opened, []);
  assert.equal(fleet.status('v'), '0.1.0 \u00b7 read-only: agent-ha-bridge is running for this user');
});
test('a machine forgotten while it is being looked at keeps nothing from that look', async () => {
  let release;
  const held = new Promise((r) => { release = r; });
  const { connection, book, fleet } = fleetWith({
    a: { machine: { hostVersion: '0.1.0', sessions: 3 }, capabilities: ['sessions.read', 'usage'], usage: usage(40) },
  });
  const makeClient = fakeClient(connection);
  fleet.makeClient = (channel) => ({ ...makeClient(channel), async usage() { await held; return connection.hosts.a.usage; } });

  fleet.probe();
  await flush();
  fleet.forget('a');
  release();
  await fleet.idle();

  assert.equal(book.size, 0);
  assert.equal(fleet.status('a'), '');
});

test('a forgotten machine whose look then fails is not remembered as looked at', async () => {
  const { connection, fleet } = fleetWith({ a: { fail: true } });
  connection.openChannel = async (id) => { connection.opened.push(id); fleet.forget(id); throw new Error('gone'); };

  fleet.probe();
  await fleet.idle();
  fleet.probe();
  await fleet.idle();

  // Not held off for a minute as a machine that failed would be: nothing about it is kept.
  assert.deepEqual(connection.opened, ['a', 'a']);
});

test('with every machine shown none is probed: each one\'s usage and live count come on the channel already open to it', async () => {
  const { connection, hosts, book, fleet } = fleetWith({ a: { machine: {}, capabilities: [] }, b: { machine: {}, capabilities: [] } });
  const client = (percent) => ({ synced: true, on() {}, subscribeUsage: async () => usage(percent) });
  hosts.view({ id: 'a', client: client(10), machine: { hostVersion: '0.1.0' }, capabilities: ['sessions.read', 'usage'] });
  const b = hosts.view({ id: 'b', client: client(30), machine: { hostVersion: '0.2.0' }, capabilities: ['sessions.read', 'usage'] });
  fleet.probe();
  await fleet.idle();
  await flush();

  assert.deepEqual(connection.opened, []);
  assert.equal(book.size, 2);
  b.store.upsert({ id: 's1', agent: 'copilot', status: 'idle', updatedAt: '2026-10-28T20:00:00.000Z', activity: {} });
  assert.equal(fleet.status('a'), '0 sessions \u00b7 0.1.0');
  assert.equal(fleet.status('b'), '1 session \u00b7 0.2.0');
});

test('a machine shown that cannot show sessions is still looked at for its status and usage', async () => {
  const { connection, hosts, book, fleet } = fleetWith({ old: { machine: { hostVersion: '0.0.9' }, capabilities: ['usage'], usage: usage(25) } });
  hosts.view({ id: 'old', phase: 'unsupported', client: null, machine: null, capabilities: [] });
  fleet.probe();
  await fleet.idle();
  assert.deepEqual(connection.opened, ['old']);
  assert.equal(fleet.status('old'), '0.0.9');
  assert.equal(book.entries()[0].windows[0].percent, 25);
});

// Updates (#51): read with the look at each machine, followed on a machine shown, and looked at
// every few seconds instead of every minute while one is under way.
const found = { current: '0.4.0', installer: true, available: '0.5.0', phase: 'idle' };

test("each machine's update state is read with its look, and dropped when it stops offering updates", async () => {
  let now = 0;
  const { connection, fleet } = fleetWith({ a: { machine: { hostVersion: '0.4.0' }, capabilities: ['update'], update: found } }, { now: () => now });
  fleet.probe();
  await fleet.idle();
  assert.equal(fleet.update('a').available, '0.5.0');

  // agent-ha-bridge started there: read-only, it offers none.
  connection.hosts.a.capabilities = ['sessions.read'];
  now = 60000;
  fleet.probe();
  await fleet.idle();
  assert.equal(fleet.update('a'), null);
});

test('a machine whose update is under way is looked at every few seconds, and every minute once it is done', async () => {
  let now = 0;
  const { connection, fleet } = fleetWith({ a: { machine: { hostVersion: '0.4.0' }, capabilities: ['update'], update: { ...found, phase: 'downloading', progress: 10 } } }, { now: () => now });
  fleet.probe();
  await fleet.idle();
  now = 10000;
  fleet.probe();
  await fleet.idle();
  assert.deepEqual(connection.opened, ['a', 'a']);

  connection.hosts.a.update = { ...found, phase: 'idle', available: null, current: '0.5.0', last: { outcome: 'updated', from: '0.4.0', to: '0.5.0', at: '2026-11-02T18:00:00.000Z' } };
  now = 20000;
  fleet.probe();
  await fleet.idle();
  now = 30000;
  fleet.probe();
  await fleet.idle();
  assert.deepEqual(connection.opened, ['a', 'a', 'a']);
  assert.equal(fleet.update('a').last.outcome, 'updated');
});

test("a machine shown is followed on its own channel, and a press goes on that channel; another machine's on a short one", async () => {
  const { connection, hosts, fleet } = fleetWith({ b: { machine: {}, capabilities: ['update'], update: found, answer: () => ({ outcome: 'accepted' }) } });
  let onUpdate = null;
  const sent = [];
  const client = {
    synced: true,
    on: (type, fn) => { if (type === 'update') { onUpdate = fn; } },
    subscribeUpdate: async () => readUpdateState(found),
    request: async (op, data) => { sent.push({ op, data }); return { outcome: 'accepted' }; },
  };
  hosts.view({ id: 'v', client, machine: {}, capabilities: ['sessions.read', 'update'] });
  await flush();
  await fleet.idle();
  // Only the machine not shown was looked at.
  assert.deepEqual(connection.opened, ['b']);
  assert.equal(fleet.update('v').available, '0.5.0');
  onUpdate(readUpdateState({ ...found, phase: 'installing' }));
  assert.equal(fleet.update('v').phase, 'installing');

  await fleet.call('v', 'update.install', { request: 'r1' });
  assert.deepEqual(sent, [{ op: 'update.install', data: { request: 'r1' } }]);
  assert.deepEqual(connection.opened, ['b']);

  await fleet.call('b', 'update.check', { request: 'r2' });
  assert.deepEqual(connection.hosts.b.requests, [{ op: 'update.check', data: { request: 'r2' } }]);
  assert.deepEqual(connection.opened, ['b', 'b']);
  fleet.clear();
  assert.equal(fleet.update('v'), null);
});
