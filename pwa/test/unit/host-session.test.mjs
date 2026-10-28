import { test } from 'node:test';
import assert from 'node:assert/strict';
import { HostSession } from '../../js/host-session.js';
import { HostError } from '../../js/host-client.js';
import { SessionsStore } from '../../js/sessions-store.js';

const flush = () => new Promise((r) => setImmediate(r));

// A relay connection whose channel opens the test settles by hand.
function fakeConnection(presence) {
  const listeners = new Map();
  const opens = [];
  return {
    presence,
    ready: true,
    opens,
    on(type, fn) { (listeners.get(type) ?? listeners.set(type, []).get(type)).push(fn); },
    openChannel(peerId) {
      let resolve, reject;
      const p = new Promise((res, rej) => { resolve = res; reject = rej; });
      const channel = { peerId, peerName: peerId, closed: false, close() { this.closed = true; } };
      opens.push({ peerId, channel, resolve: () => resolve(channel), reject });
      return p;
    },
  };
}

function fakeClient(channel) {
  return {
    channel,
    closed: false,
    on() {},
    close() { this.closed = true; },
    hello: async () => ({ protocol: 1, machine: { name: channel.peerId }, capabilities: ['sessions.read'] }),
    subscribe: async () => ({ revision: 1, count: 0 }),
  };
}

function session(presence) {
  const connection = fakeConnection(presence);
  const hosts = new HostSession({ connection, store: new SessionsStore(), storage: null, makeClient: (ch) => fakeClient(ch) });
  return { connection, hosts };
}

test('a hello that times out is retried, not taken for an old host', async () => {
  const connection = fakeConnection([{ id: 'a', kind: 'host' }]);
  const clients = [];
  const hosts = new HostSession({
    connection, store: new SessionsStore(), storage: null,
    makeClient: (ch) => {
      const c = fakeClient(ch);
      c.hello = async () => { throw Object.assign(new Error('hello: timed out'), { reason: 'timed out' }); };
      clients.push(c);
      return c;
    },
  });
  hosts.select('a');
  await flush();
  connection.opens[0].resolve();
  await flush();
  assert.equal(hosts.phase, 'failed');
  assert.equal(hosts.client, null);
  assert.equal(clients[0].closed, true);
});

test('a host that refuses hello is reported as not supporting sessions', async () => {
  const connection = fakeConnection([{ id: 'a', kind: 'host' }]);
  const hosts = new HostSession({
    connection, store: new SessionsStore(), storage: null,
    makeClient: (ch) => {
      const c = fakeClient(ch);
      c.hello = async () => { throw new HostError('hello', 'unknown op hello', { fromHost: true }); };
      return c;
    },
  });
  hosts.select('a');
  await flush();
  connection.opens[0].resolve();
  await flush();
  assert.equal(hosts.phase, 'unsupported');
  assert.match(hosts.error, /unknown op hello/);
});

test('switching machines while a handshake is pending opens the new machine', async () => {
  const { connection, hosts } = session([{ id: 'a', kind: 'host' }, { id: 'b', kind: 'host' }]);
  hosts.select('a');
  hosts.select('b');
  await flush();
  // Whether B starts at once or after A settles, A's late channel must not win.
  connection.opens[0].resolve();
  await flush();
  const forB = connection.opens.find((o) => o.peerId === 'b');
  assert.ok(forB, 'a channel to b was opened');
  forB.resolve();
  await flush();
  assert.equal(hosts.phase, 'open');
  assert.equal(hosts.client.channel.peerId, 'b');
  assert.equal(connection.opens[0].channel.closed, true, "a's channel is closed, not leaked");
});

test('going back to a machine while its first attempt is pending keeps only one channel', async () => {
  const { connection, hosts } = session([{ id: 'a', kind: 'host' }, { id: 'b', kind: 'host' }]);
  hosts.select('a');
  hosts.select('b');
  hosts.select('a');
  await flush();
  for (const o of [...connection.opens]) { o.resolve(); }
  await flush();
  for (const o of [...connection.opens]) { o.resolve(); }
  await flush();
  assert.equal(hosts.phase, 'open');
  assert.equal(hosts.client.channel.peerId, 'a');
  const open = connection.opens.filter((o) => !o.channel.closed);
  assert.equal(open.length, 1, `open channels: ${open.map((o) => o.peerId)}`);
  assert.equal(open[0].channel, hosts.client.channel);
});
