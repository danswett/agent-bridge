import { test } from 'node:test';
import assert from 'node:assert/strict';
import { ALL, HostSession } from '../../js/host-session.js';
import { SessionOrder } from '../../js/session-order.js';

function fixture(t) {
  const listeners = new Map();
  const connection = {
    ready: false,
    presence: [{ id: 'first', kind: 'host' }, { id: 'second', kind: 'host' }],
    on(type, fn) { (listeners.get(type) ?? listeners.set(type, []).get(type)).push(fn); },
    openChannel() { throw new Error('No HA or network source belongs in this projection test'); },
    emit(type) { for (const fn of listeners.get(type) ?? []) fn(); },
  };
  const hosts = new HostSession({ connection, storage: null });
  t.after(() => hosts.forget());
  hosts.select(ALL);
  const order = new SessionOrder({ storage: { getItem: () => null, setItem() {} } });
  hosts.sessions.onChange((change) => {
    order.observe(change);
    if (change.type === 'synced') {
      order.synced(change.hostId, new Set(hosts.link(change.hostId).store.list().map((s) => s.id)));
    }
  });
  const snapshot = (hostId, ids) => {
    const store = hosts.link(hostId).store;
    for (const id of ids) {
      store.upsert({ id, title: id, agent: 'copilot', status: 'idle', updatedAt: '2026-11-17T20:00:00.000Z', activity: {} }, { snapshot: true });
    }
    store.retain(new Set(ids));
  };
  const arranged = () => order.arrange(hosts.sessions.list());
  const ids = () => hosts.sessions.list().map((e) => `${e.hostId}/${e.session.id}`).sort();
  return { connection, hosts, order, snapshot, arranged, ids };
}

test('the list uses each host snapshot without a Home Assistant client', (t) => {
  const f = fixture(t);
  f.snapshot('first', ['here']);
  f.snapshot('second', ['there']);
  assert.deepEqual(f.ids(), ['first/here', 'second/there']);
  assert.deepEqual(Object.values(f.arranged()).flat().map((e) => `${e.hostId}/${e.session.id}`).sort(), f.ids());
});

for (const replacementSnapshot of [false, true]) {
  test(`an ended session leaves every list group immediately after ${replacementSnapshot ? 'a replacement snapshot' : 'a removal event'}`, (t) => {
    const f = fixture(t);
    f.snapshot('first', ['ended', 'live']);
    f.order.setPinned('first\nended', true);
    f.arranged();
    if (replacementSnapshot) f.snapshot('first', ['live']);
    else f.hosts.link('first').store.remove('ended');
    const list = f.arranged();
    assert.deepEqual(f.ids(), ['first/live']);
    assert.deepEqual(list.pinned, []);
    assert.deepEqual(list.sessions.map((e) => e.session.id), ['live']);
    assert.deepEqual(Object.values(list).flat().map((e) => e.session.id), ['live']);
  });
}

test('one host replacement snapshot preserves a silent hosts stale rows', (t) => {
  const f = fixture(t);
  f.snapshot('first', ['old']);
  f.snapshot('second', ['there']);
  f.connection.presence = [{ id: 'first', kind: 'host' }];
  f.connection.emit('presence');
  f.snapshot('first', ['new']);
  assert.deepEqual(f.ids(), ['first/new', 'second/there']);
  assert.equal(f.hosts.staleOf('second').reason, 'offline');
  assert.equal(f.hosts.route('second'), null);
});
