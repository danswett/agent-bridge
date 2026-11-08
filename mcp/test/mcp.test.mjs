import test from 'node:test';
import assert from 'node:assert/strict';
import net from 'node:net';
import os from 'node:os';
import path from 'node:path';
import { Client } from '@modelcontextprotocol/sdk/client/index.js';
import { InMemoryTransport } from '@modelcontextprotocol/sdk/inMemory.js';
import { openHost, pipeName, resolveEndpoint, PipeError } from '../src/pipe.mjs';
import { createConnection, createServer } from '../src/server.mjs';
import { turnMarker } from '../src/tools.mjs';

// A fake host: speaks the pipe's framing and answers from `handlers`.
async function fakeHost(handlers) {
  const name = process.platform === 'win32' ? `\\\\.\\pipe\\ap2-${process.pid}-${Math.random().toString(36).slice(2)}` : path.join(os.tmpdir(), `ap2-${process.pid}-${Math.random().toString(36).slice(2)}.sock`);
  const seen = [];
  const server = net.createServer((socket) => {
    let buf = Buffer.alloc(0);
    socket.on('data', (chunk) => {
      buf = Buffer.concat([buf, chunk]);
      while (buf.length >= 4 && buf.length >= 4 + buf.readUInt32LE(0)) {
        const len = buf.readUInt32LE(0);
        const req = JSON.parse(buf.subarray(4, 4 + len).toString('utf8'));
        buf = buf.subarray(4 + len);
        seen.push(req);
        let reply;
        try {
          const result = handlers[req.op]?.(req.data);
          reply = result === undefined ? { re: req.id, ok: false, error: 'unknown op' } : { re: req.id, ok: true, result };
        } catch (e) {
          reply = { re: req.id, ok: false, error: e.message };
        }
        const body = Buffer.from(JSON.stringify(reply));
        const head = Buffer.alloc(4);
        head.writeUInt32LE(body.length);
        setTimeout(() => socket.write(Buffer.concat([head, body])), handlers.delayMs?.[req.op] ?? 0);
      }
    });
  });
  await new Promise((r) => server.listen(name, r));
  return { name, seen, close: () => server.close() };
}

const CAPS = ['sessions.read', 'sessions.reply', 'sessions.launch'];
const session = (over = {}) => ({ id: 's1', agent: 'copilot', title: 'T', status: 'idle', statusSince: 't1', activity: { response: 'hello' }, ...over });

async function clientFor(host, env = {}) {
  const connection = createConnection({ env: { AGENT_BRIDGE_TOKEN: 'abat1_x', ...env }, endpoint: () => host?.name ?? null });
  const server = createServer(connection);
  const [a, b] = InMemoryTransport.createLinkedPair();
  await server.connect(a);
  const client = new Client({ name: 't', version: '0' });
  await client.connect(b);
  return { client, connection, server };
}
const call = async (client, name, args) => (await client.callTool({ name, arguments: args })).structuredContent;

test('pipe name matches the host algorithm (hash of "sid|lowercased folder")', () => {
  assert.match(pipeName('S-1-5-21-1', 'C:\\Users\\Me\\AppData\\Local\\AgentBridge\\default'), /^AgentBridge\.Host\.[0-9a-f]{16}$/);
  assert.equal(pipeName('S-1-5-21-1', 'C:\\X'), pipeName('S-1-5-21-1', 'c:\\x'));
});

test('endpoint: an explicit AGENT_BRIDGE_PIPE wins; no SID means none', () => {
  assert.equal(resolveEndpoint({ AGENT_BRIDGE_PIPE: 'Foo' }, 'win32'), '\\\\.\\pipe\\Foo');
  assert.equal(resolveEndpoint({ LOCALAPPDATA: 'C:\\L' }, 'win32', () => null), null);
  assert.match(resolveEndpoint({ LOCALAPPDATA: 'C:\\L' }, 'win32', () => 'S-1-5-1'), /^\\\\\.\\pipe\\AgentBridge\.Host\./);
});

test('openHost says hello, then agent.hello with the token', async () => {
  const host = await fakeHost({ hello: () => ({ capabilities: CAPS }), 'agent.hello': (d) => ({ principal: `agent:m/${d.token}` }) });
  const { client, principal } = await openHost({ endpoint: host.name, token: 'abat1_x' });
  assert.equal(principal, 'agent:m/abat1_x');
  assert.deepEqual(host.seen.map((r) => r.op), ['hello', 'agent.hello']);
  client.close(); host.close();
});

test('a refused agent.hello fails the open', async () => {
  const host = await fakeHost({ hello: () => ({ capabilities: CAPS }), 'agent.hello': () => { throw new Error('refused'); } });
  await assert.rejects(openHost({ endpoint: host.name, token: 'bad' }), (e) => e instanceof PipeError && e.kind === 'refused');
  host.close();
});

test('no host: no tools, so an agent falls back', async () => {
  const { client } = await clientFor(null);
  assert.deepEqual((await client.listTools()).tools, []);
  const r = await client.callTool({ name: 'list_agent_sessions', arguments: {} });
  assert.equal(r.isError, true);
});

test('tools follow the host\'s capabilities and keep the old names', async () => {
  const host = await fakeHost({ hello: () => ({ capabilities: ['sessions.read'] }), 'agent.hello': () => ({ principal: 'p' }) });
  const { client, connection } = await clientFor(host);
  assert.deepEqual((await client.listTools()).tools.map((t) => t.name).sort(), ['list_agent_sessions', 'read_agent_session']);
  connection.close(); host.close();
});

test('the server tells agents to prefer Slipgates messaging', async () => {
  const { client } = await clientFor(null);
  assert.match(client.getInstructions(), /use these tools instead of any other messaging route/);
});

test('list is typed by default and carries content only on request', async () => {
  const host = await fakeHost({
    hello: () => ({ capabilities: CAPS }), 'agent.hello': () => ({ principal: 'p' }),
    'sessions.list': (d) => (d.cursor ? { sessions: [session({ id: 's2' })] } : { sessions: [session()], next: 'c' }),
  });
  const { client, connection } = await clientFor(host);
  const typed = await call(client, 'list_agent_sessions', {});
  assert.deepEqual(typed.sessions.map((s) => s.id), ['s1', 's2']);
  assert.equal(typed.sessions[0].activity, undefined);
  assert.equal((await call(client, 'list_agent_sessions', { include: 'content' })).sessions[0].activity.response, 'hello');
  connection.close(); host.close();
});

test('reply sends the whole text under a fresh request id and returns since', async () => {
  const long = 'x'.repeat(5000);
  const host = await fakeHost({
    hello: () => ({ capabilities: CAPS }), 'agent.hello': () => ({ principal: 'p' }),
    'session.get': () => session(), 'session.reply': () => ({ outcome: 'delivered' }),
  });
  const { client, connection } = await clientFor(host);
  const out = await call(client, 'reply_to_agent_session', { session_id: 's1', text: long });
  assert.equal(out.outcome, 'delivered');
  assert.equal(out.since, turnMarker(session()));
  const sent = host.seen.find((r) => r.op === 'session.reply').data;
  assert.equal(sent.text, long);
  assert.match(sent.request, /^[0-9a-f-]{36}$/);
  connection.close(); host.close();
});

test('a refusing host is reported as refused, not thrown', async () => {
  const host = await fakeHost({
    hello: () => ({ capabilities: CAPS }), 'agent.hello': () => ({ principal: 'p' }),
    'session.launch': () => { throw new Error('agents may not launch yet'); },
  });
  const { client, connection } = await clientFor(host);
  const out = await call(client, 'launch_agent_session', { prompt: 'hi' });
  assert.equal(out.outcome, 'refused');
  assert.match(out.reason, /may not launch/);
  connection.close(); host.close();
});

test('read waits for a turn newer than since, never returning the stale answer', async () => {
  let turn = 0;
  const host = await fakeHost({
    hello: () => ({ capabilities: CAPS }), 'agent.hello': () => ({ principal: 'p' }),
    'session.get': () => (++turn < 3 ? session() : session({ statusSince: 't2', activity: { response: 'fresh' } })),
  });
  const { client, connection } = await clientFor(host);
  const stale = await call(client, 'read_agent_session', { session_id: 's1', since: turnMarker(session()) });
  assert.equal(stale.done, false);
  assert.equal(stale.response, '');
  const fresh = await call(client, 'read_agent_session', { session_id: 's1', since: turnMarker(session()), wait_seconds: 5 });
  assert.equal(fresh.done, true);
  assert.equal(fresh.response, 'fresh');
  connection.close(); host.close();
});

test('read is not done when only statusSince changes, and a refused reply returns no since', async () => {
  const host = await fakeHost({
    hello: () => ({ capabilities: CAPS }), 'agent.hello': () => ({ principal: 'p' }),
    'session.get': () => session({ statusSince: 't9', status: 'waiting-for-input' }),
    'session.reply': () => ({ outcome: 'refused', reason: 'no' }),
  });
  const { client, connection } = await clientFor(host);
  const stale = await call(client, 'read_agent_session', { session_id: 's1', since: turnMarker(session()) });
  assert.equal(stale.done, false);
  assert.equal(stale.response, '');
  const refused = await call(client, 'reply_to_agent_session', { session_id: 's1', text: 'x' });
  assert.equal(refused.outcome, 'refused');
  assert.equal(refused.since, undefined);
  connection.close(); host.close();
});

test('launch rejects a request id that is not a short string', async () => {
  const host = await fakeHost({ hello: () => ({ capabilities: CAPS }), 'agent.hello': () => ({ principal: 'p' }), 'session.launch': () => ({ outcome: 'started' }) });
  const { client, connection } = await clientFor(host);
  const res = await client.callTool({ name: 'launch_agent_session', arguments: { request: 'r'.repeat(500) } });
  assert.equal(res.isError, true);
  assert.equal(host.seen.some((r) => r.op === 'session.launch'), false);
  connection.close(); host.close();
});

test('launch maps permissions, remembers the outcome, and read_launch_result returns it', async () => {
  const host = await fakeHost({
    hello: () => ({ capabilities: CAPS }), 'agent.hello': () => ({ principal: 'p' }),
    'session.launch': () => ({ outcome: 'started', session: 's9' }),
  });
  const { client, connection } = await clientFor(host);
  const out = await call(client, 'launch_agent_session', { prompt: 'go', permissions: 'Allow all', workspace: 'Bridge' });
  assert.equal(out.outcome, 'started');
  assert.equal(host.seen.find((r) => r.op === 'session.launch').data.permissions, 'allowAll');
  assert.equal((await call(client, 'read_launch_result', { request: out.request })).outcome, 'started');
  assert.equal((await call(client, 'read_launch_result', { request: 'nope' })).status, 'unknown');
  connection.close(); host.close();
});

test('without a credential only read tools are listed and dispatched', async () => {
  const host = await fakeHost({ hello: () => ({ capabilities: CAPS }), 'session.reply': () => ({ outcome: 'delivered' }), 'sessions.list': () => ({ sessions: [] }) });
  const { client, connection } = await clientFor(host, { AGENT_BRIDGE_TOKEN: '' });
  assert.deepEqual((await client.listTools()).tools.map((t) => t.name).sort(), ['list_agent_sessions', 'read_agent_session']);
  const r = await client.callTool({ name: 'reply_to_agent_session', arguments: { session_id: 's1', text: 'hi' } });
  assert.equal(r.isError, true);
  assert.equal(host.seen.some((q) => q.op === 'session.reply' || q.op === 'session.get'), false);
  connection.close(); host.close();
});

test('unknown or inherited tool names and bad arguments are rejected before any pipe call', async () => {
  const host = await fakeHost({ hello: () => ({ capabilities: CAPS }), 'agent.hello': () => ({ principal: 'p' }), 'session.get': () => session(), 'session.reply': () => ({ outcome: 'delivered' }) });
  const { client, connection } = await clientFor(host);
  for (const [name, args] of [['constructor', {}], ['__proto__', {}], ['reply_to_agent_session', { session_id: 's1' }], ['reply_to_agent_session', { session_id: 's1', text: 'x'.repeat(16 * 1024 + 1) }]]) {
    assert.equal((await client.callTool({ name, arguments: args })).isError, true, name);
  }
  assert.equal(host.seen.some((q) => q.op === 'session.reply' || q.op === 'session.get'), false);
  connection.close(); host.close();
});

test('a host that stops answering fails the request instead of hanging', async () => {
  const name = process.platform === 'win32' ? `\\\\.\\pipe\\ap2-mute-${process.pid}` : path.join(os.tmpdir(), `ap2-mute-${process.pid}.sock`);
  const sockets = [];
  const server = net.createServer((s) => sockets.push(s));
  await new Promise((r) => server.listen(name, r));
  await assert.rejects(openHost({ endpoint: name, token: 'x', timeoutMs: 200 }), (e) => e instanceof PipeError && e.kind === 'unavailable');
  sockets.forEach((s) => s.destroy());
  server.close();
});

test('a read answered after more than a second still succeeds with no wait', async () => {
  const host = await fakeHost({
    hello: () => ({ capabilities: CAPS }), 'agent.hello': () => ({ principal: 'p' }),
    'session.get': () => session(), delayMs: { 'session.get': 1300 },
  });
  const { client, connection } = await clientFor(host);
  const out = await call(client, 'read_agent_session', { session_id: 's1' });
  assert.equal(out.done, true);
  assert.equal(out.response, 'hello');
  connection.close(); host.close();
});
