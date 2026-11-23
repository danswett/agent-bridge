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

test('usage is hidden and refused without its exact capability or an accepted principal', async (t) => {
  for (const [capabilities, token] of [
    [['sessions.read'], 'abat1_x'],
    [['usage.shared', 'usage.write', 'future.usage'], 'abat1_x'],
    [['usage'], ''],
  ]) {
    const host = await fakeHost({
      hello: () => ({ capabilities }), 'agent.hello': () => ({ principal: 'p' }),
      'usage.get': () => ({ agents: [] }),
    });
    t.after(() => host.close());
    const { client, connection } = await clientFor(host, { AGENT_BRIDGE_TOKEN: token });
    t.after(() => connection.close());
    assert.equal((await client.listTools()).tools.some((tool) => tool.name === 'read_agent_usage'), false);
    const refused = await client.callTool({ name: 'read_agent_usage', arguments: {} });
    assert.equal(refused.isError, true);
    assert.match(refused.content[0].text, /not available on this connection/);
    assert.equal(host.seen.some((r) => r.op.startsWith('usage.')), false);
  }
});

test('usage reads preserve allowance fields and failures without adding zero readings', async (t) => {
  const report = { agents: [
    { agent: 'copilot', label: 'GitHub Copilot', account: 'example', plan: 'enterprise', source: 'api',
      measuredAt: '2026-11-17T08:00:00Z', windows: [
        { key: 'plan', label: 'Plan', percent: 75.5, used: 755, limit: 1000, unit: 'AIC',
          startsAt: '2026-11-01T00:00:00Z', resetsAt: '2026-12-01T00:00:00Z' },
      ] },
    { agent: 'codex', label: 'Codex', source: 'none', windows: [],
      measuredAt: '2026-11-17T08:01:00Z', error: 'No reading available' },
  ] };
  const host = await fakeHost({
    hello: () => ({ capabilities: ['usage'] }), 'agent.hello': () => ({ principal: 'p' }),
    'usage.get': () => report,
  });
  t.after(() => host.close());
  const { client, connection } = await clientFor(host);
  t.after(() => connection.close());
  assert.deepEqual((await client.listTools()).tools.map((tool) => tool.name), ['read_agent_usage']);
  assert.deepEqual(await call(client, 'read_agent_usage', {}), report);
  assert.deepEqual(host.seen.filter((r) => r.op.startsWith('usage.')).map(({ op, data }) => ({ op, data })),
    [{ op: 'usage.get', data: {} }]);
});

test('usage offers no write path and rejects operation, shared and remote arguments', async (t) => {
  const host = await fakeHost({
    hello: () => ({ capabilities: ['usage', 'usage.shared', 'usage.write'] }),
    'agent.hello': () => ({ principal: 'p' }), 'usage.get': () => ({ agents: [] }),
  });
  t.after(() => host.close());
  const { client, connection } = await clientFor(host);
  t.after(() => connection.close());
  const [tool] = (await client.listTools()).tools;
  assert.equal(tool?.name, 'read_agent_usage');
  assert.equal(tool.annotations.readOnlyHint, true);
  assert.equal(tool.inputSchema.additionalProperties, false);
  for (const arguments_ of [{ op: 'usage.reset' }, { shared: true }, { machine: 'other' }]) {
    assert.equal((await client.callTool({ name: 'read_agent_usage', arguments: arguments_ })).isError, true);
  }
  for (const name of ['write_agent_usage', 'reset_agent_usage', 'usage.get', 'usage.subscribe']) {
    assert.equal((await client.callTool({ name, arguments: {} })).isError, true);
  }
  assert.equal(host.seen.some((r) => r.op.startsWith('usage.')), false);
});

test('usage host refusals remain errors rather than empty reports', async (t) => {
  const host = await fakeHost({
    hello: () => ({ capabilities: ['usage'] }), 'agent.hello': () => ({ principal: 'p' }),
    'usage.get': () => { throw new Error('usage permission revoked'); },
  });
  t.after(() => host.close());
  const { client, connection } = await clientFor(host);
  t.after(() => connection.close());
  const refused = await client.callTool({ name: 'read_agent_usage', arguments: {} });
  assert.equal(refused.isError, true);
  assert.match(refused.content[0].text, /usage permission revoked/);
  assert.equal(refused.structuredContent, undefined);
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

test('the default list hides the working folder (cwd); title, repository and branch stay; cwd comes only with include "content"', async () => {
  const host = await fakeHost({
    hello: () => ({ capabilities: CAPS }), 'agent.hello': () => ({ principal: 'p' }),
    'sessions.list': () => ({ sessions: [session({ cwd: 'C:/work/repo', repository: 'repo', branch: 'main', title: 'T' })] }),
  });
  const { client, connection } = await clientFor(host);
  const plain = (await call(client, 'list_agent_sessions', {})).sessions[0];
  assert.equal('cwd' in plain, false);
  assert.deepEqual([plain.title, plain.repository, plain.branch], ['T', 'repo', 'main']);
  const withContent = (await call(client, 'list_agent_sessions', { include: 'content' })).sessions[0];
  assert.equal(withContent.cwd, 'C:/work/repo');
  assert.equal(withContent.activity.response, 'hello');
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
  for (const request of ['', 'has space', 'new\nline', 'a/b']) {
    const bad = await client.callTool({ name: 'launch_agent_session', arguments: { request } });
    assert.equal(bad.isError, true, JSON.stringify(request));
    assert.match(bad.content[0].text, /request must be/);
  }
  assert.equal((await client.callTool({ name: 'read_launch_result', arguments: { request: 'x y' } })).isError, true);
  assert.equal(host.seen.some((r) => r.op === 'session.launch'), false);
  connection.close(); host.close();
});

test('launch maps permissions, remembers the outcome, and read_launch_result returns it', async () => {
  const host = await fakeHost({
    hello: () => ({ capabilities: CAPS }), 'agent.hello': () => ({ principal: 'p' }),
    'session.launch': () => ({ outcome: 'started', session: 's9' }),
  });
  const { client, connection } = await clientFor(host);
  const out = await call(client, 'launch_agent_session', { prompt: 'go', permissions: 'Allow all', workspace: 'Bridge', role: 'review' });
  assert.equal(out.outcome, 'started');
  assert.equal(host.seen.find((r) => r.op === 'session.launch').data.permissions, 'allowAll');
  assert.equal(host.seen.find((r) => r.op === 'session.launch').data.role, 'review');
  assert.equal((await call(client, 'read_launch_result', { request: out.request })).outcome, 'started');
  assert.equal((await call(client, 'read_launch_result', { request: 'nope' })).status, 'unknown');
  connection.close(); host.close();
});

test('a coordinator MCP launch defaults omitted roles and preserves an explicit unlisted role', async (t) => {
  const host = await fakeHost({
    hello: () => ({ capabilities: CAPS }), 'agent.hello': () => ({ principal: 'p' }),
    'session.launch': () => ({ outcome: 'started', session: 's9' }),
  });
  t.after(() => host.close());
  const { client, connection } = await clientFor(host);
  t.after(() => connection.close());

  for (const options of [{}, { role: undefined }, { role: 'unlisted' }]) {
    const out = await call(client, 'launch_agent_session', { agent: 'copilot', prompt: 'go', ...options });
    assert.equal(out.outcome, 'started');
  }
  assert.deepEqual(host.seen.filter((r) => r.op === 'session.launch').map((r) => r.data.role), ['default', 'default', 'unlisted']);
});

test('caller-supplied origin fields cannot select or bypass the MCP default role', async (t) => {
  const host = await fakeHost({
    hello: () => ({ capabilities: CAPS }), 'agent.hello': () => ({ principal: 'p' }),
    'session.launch': () => ({ outcome: 'started' }),
  });
  t.after(() => host.close());
  const { client, connection } = await clientFor(host);
  t.after(() => connection.close());

  for (const origin of ['person', 'coordinator']) {
    await call(client, 'launch_agent_session', { agent: 'copilot', origin, coordinator: origin === 'coordinator' });
  }

  const launches = host.seen.filter((r) => r.op === 'session.launch');
  assert.equal(launches.length, 2);
  for (const { data } of launches) {
    assert.equal(data.role, 'default');
    assert.equal(Object.hasOwn(data, 'origin'), false);
    assert.equal(Object.hasOwn(data, 'coordinator'), false);
  }
});

test('null, empty and whitespace-only roles are rejected without forwarding a launch', async (t) => {
  const host = await fakeHost({
    hello: () => ({ capabilities: CAPS }), 'agent.hello': () => ({ principal: 'p' }),
    'session.launch': () => ({ outcome: 'started' }),
  });
  t.after(() => host.close());
  const { client, connection } = await clientFor(host);
  t.after(() => connection.close());

  for (const role of [null, '', ' \t ']) {
    const out = await client.callTool({ name: 'launch_agent_session', arguments: { agent: 'copilot', role } });
    assert.equal(out.isError, true, JSON.stringify(role));
    assert.match(out.content[0].text, /role/);
  }
  assert.equal(host.seen.some((r) => r.op === 'session.launch'), false);
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

// HA retirement rows 5 and 18: an agent behind this server can list, read, reply, launch and read usage, and
// never answer, approve, trust or stop. The host refuses agent answers too (ScriptCredentialScopeTests); these
// pins keep the server from ever offering or sending one.
const ALL_CAPS = [...CAPS, 'usage'];
const SURFACE = ['get_launch_options', 'launch_agent_session', 'list_agent_sessions', 'read_agent_session', 'read_agent_usage', 'read_launch_result', 'reply_to_agent_session'];
const FORBIDDEN_OPS = ['session.answer', 'sessions.approve', 'trust.answer', 'session.stop', 'sessions.focus'];

test('with every capability the tool surface is exactly the known seven, and none can answer, approve, trust or stop', async (t) => {
  const host = await fakeHost({ hello: () => ({ capabilities: ALL_CAPS }), 'agent.hello': () => ({ principal: 'p' }) });
  const { client, connection } = await clientFor(host);
  t.after(() => { connection.close(); host.close(); });
  const names = (await client.listTools()).tools.map((t) => t.name).sort();
  assert.deepEqual(names, SURFACE);
  assert.equal(names.some((n) => /answer|approve|trust|stop|decision|focus/i.test(n)), false);
});

test('using every tool sends the host only list, read, reply, launch and usage operations, never an answer, approval, trust answer or stop', async (t) => {
  const trap = (op) => () => { throw new Error(`${op} must never be sent`); };
  const host = await fakeHost({
    hello: () => ({ capabilities: ALL_CAPS }), 'agent.hello': () => ({ principal: 'p' }),
    'sessions.list': () => ({ sessions: [session()] }), 'session.get': () => session(), 'session.reply': () => ({ outcome: 'delivered' }),
    'session.launch': () => ({ outcome: 'started', session: 's9' }), 'launch.options': () => ({}), 'usage.get': () => ({ agents: [] }),
    ...Object.fromEntries(FORBIDDEN_OPS.map((op) => [op, trap(op)])),
  });
  const { client, connection } = await clientFor(host);
  t.after(() => { connection.close(); host.close(); });
  const args = { list_agent_sessions: {}, read_agent_session: { session_id: 's1' }, reply_to_agent_session: { session_id: 's1', text: 'hi' },
    launch_agent_session: { prompt: 'go' }, read_launch_result: { request: 'r1' }, get_launch_options: {}, read_agent_usage: {} };
  for (const name of SURFACE) { await client.callTool({ name, arguments: args[name] }); }
  const sent = new Set(host.seen.map((r) => r.op));
  for (const op of FORBIDDEN_OPS) { assert.equal(sent.has(op), false, op); }
  assert.deepEqual([...sent].sort(), ['agent.hello', 'hello', 'launch.options', 'session.get', 'session.launch', 'session.reply', 'sessions.list', 'usage.get']);
});
