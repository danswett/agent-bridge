import test from 'node:test';
import assert from 'node:assert/strict';
import net from 'node:net';
import os from 'node:os';
import path from 'node:path';
import { readFileSync, readdirSync } from 'node:fs';
import { Client } from '@modelcontextprotocol/sdk/client/index.js';
import { InMemoryTransport } from '@modelcontextprotocol/sdk/inMemory.js';
import { createConnection, createServer } from '../src/server.mjs';

// HA retirement row 18 (agent-facing list and read of sessions, MCP), HA off (#1396). The tools talk to the host's pipe only: with every Home
// Assistant variable absent, the list and read tools work, ask the host nothing but its own session ops, and make no network call. This file
// does not test the absence of an ask tool (a separate guard test owns that) and it is not a fleet-mode test: list_agent_sessions lists this
// machine's sessions (sessions.list); a fleet-wide MCP list does not exist yet.

async function fakeHost(handlers) {
  const name = process.platform === 'win32' ? `\\\\.\\pipe\\haoff-${process.pid}-${Math.random().toString(36).slice(2)}` : path.join(os.tmpdir(), `haoff-${process.pid}-${Math.random().toString(36).slice(2)}.sock`);
  const seen = [];
  const server = net.createServer((socket) => {
    let buf = Buffer.alloc(0);
    socket.on('data', (chunk) => {
      buf = Buffer.concat([buf, chunk]);
      while (buf.length >= 4 && buf.length >= 4 + buf.readUInt32LE(0)) {
        const len = buf.readUInt32LE(0);
        const req = JSON.parse(buf.subarray(4, 4 + len).toString('utf8'));
        buf = buf.subarray(4 + len);
        seen.push(req.op);
        const result = handlers[req.op]?.(req.data);
        const reply = result === undefined ? { re: req.id, ok: false, error: 'unknown op' } : { re: req.id, ok: true, result };
        const body = Buffer.from(JSON.stringify(reply));
        const head = Buffer.alloc(4);
        head.writeUInt32LE(body.length);
        socket.write(Buffer.concat([head, body]));
      }
    });
  });
  await new Promise((r) => server.listen(name, r));
  return { name, seen, close: () => server.close() };
}

const session = (over = {}) => ({ id: 's1', agent: 'copilot', title: 'T', cwd: 'C:\\w', repository: 'r', branch: 'b', model: 'm', status: 'idle', statusSince: 't1', startedAt: 't0', updatedAt: 't2', activity: { response: 'hello' }, question: { text: 'q' }, ...over });
const call = async (client, name, args) => (await client.callTool({ name, arguments: args })).structuredContent;

async function connect(host, env) {
  const connection = createConnection({ env, endpoint: () => host.name });
  const server = createServer(connection);
  const [a, b] = InMemoryTransport.createLinkedPair();
  await server.connect(a);
  const client = new Client({ name: 't', version: '0' });
  await client.connect(b);
  return { client, connection };
}

const HA = /(^|_)HA(_|$)|HOME_?ASSISTANT/i;

test('list and read answer with every Home Assistant variable absent, ask the host only for its own session ops, and make no network call', async (t) => {
  const host = await fakeHost({ hello: () => ({ capabilities: ['sessions.read'] }), 'agent.hello': () => ({ principal: 'p' }), 'sessions.list': () => ({ sessions: [session()] }), 'session.get': () => session() });
  const removed = Object.entries(process.env).filter(([k]) => HA.test(k));
  for (const [k] of removed) delete process.env[k];
  let fetched = 0;
  const realFetch = globalThis.fetch;
  globalThis.fetch = async () => { fetched++; throw new Error('no network in this test'); };
  const { client, connection } = await connect(host, { AGENT_BRIDGE_TOKEN: 'abat1_x' });
  t.after(() => { globalThis.fetch = realFetch; for (const [k, v] of removed) process.env[k] = v; connection.close(); host.close(); });

  const listed = await call(client, 'list_agent_sessions', {});
  const read = await call(client, 'read_agent_session', { session_id: 's1' });

  assert.equal(listed.sessions[0].id, 's1');
  assert.equal(read.found, true);
  assert.deepEqual([...new Set(host.seen)].sort(), ['agent.hello', 'hello', 'session.get', 'sessions.list']);
  assert.equal(fetched, 0);
  assert.equal(Object.keys(process.env).some((k) => HA.test(k)), false);
});

test('the default list is typed: no session content; include "content" adds the latest answer and question and nothing else', async (t) => {
  const host = await fakeHost({ hello: () => ({ capabilities: ['sessions.read'] }), 'agent.hello': () => ({ principal: 'p' }), 'sessions.list': () => ({ sessions: [session()] }) });
  const { client, connection } = await connect(host, { AGENT_BRIDGE_TOKEN: 'abat1_x' });
  t.after(() => { connection.close(); host.close(); });

  const plain = (await call(client, 'list_agent_sessions', {})).sessions[0];
  const withContent = (await call(client, 'list_agent_sessions', { include: 'content' })).sessions[0];

  assert.deepEqual(Object.keys(plain).sort(), ['agent', 'branch', 'cwd', 'id', 'model', 'repository', 'startedAt', 'status', 'statusSince', 'title', 'updatedAt']);
  assert.equal('activity' in plain || 'question' in plain, false);
  assert.deepEqual(Object.keys(withContent).filter((k) => !(k in plain)).sort(), ['activity', 'question']);
});

test('no server source reads a Home Assistant variable, names a Home Assistant endpoint, or opens a network connection of its own', () => {
  const dir = new URL('../src/', import.meta.url);
  const sources = readdirSync(dir).filter((f) => f.endsWith('.mjs')).map((f) => [f, readFileSync(new URL(f, dir), 'utf8')]);
  assert.ok(sources.length >= 3);
  for (const [file, text] of sources) {
    assert.doesNotMatch(text, /AGENT_HA|FLEET_HA|HOME_?ASSISTANT|homeassistant|\/api\/states/i, `${file} names Home Assistant`);
    assert.doesNotMatch(text, /\bfetch\s*\(|node:https?['"]|XMLHttpRequest|WebSocket/, `${file} opens a network connection`);
  }
});
