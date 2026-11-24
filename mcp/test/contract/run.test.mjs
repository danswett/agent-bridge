// Runs mcp/test/contract/cases.json against an MCP server process over stdio, with a fake host on the local pipe.
// The server command is SLIPGATES_MCP_COMMAND (a JSON array, e.g. ["agent-bridge-host","mcp"]); the default is the Node server.
// Nothing here imports the server: the same cases are the contract for the .NET port (beta-messaging.md sec 5).
import test from 'node:test';
import assert from 'node:assert/strict';
import net from 'node:net';
import os from 'node:os';
import path from 'node:path';
import { readFileSync, realpathSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { Client } from '@modelcontextprotocol/sdk/client/index.js';
import { StdioClientTransport } from '@modelcontextprotocol/sdk/client/stdio.js';

const here = path.dirname(fileURLToPath(import.meta.url));
const contract = JSON.parse(readFileSync(path.join(here, 'cases.json'), 'utf8'));
const command = process.env.SLIPGATES_MCP_COMMAND ? JSON.parse(process.env.SLIPGATES_MCP_COMMAND) : [process.execPath, realpathSync.native(path.join(here, '..', '..', 'src', 'server.mjs'))]  // real-cased path: the server starts only when import.meta.url equals argv[1];
const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

// Ids named in the file ("$s", "$r") are replaced before use, in arguments and expectations alike.
const ids = (v) => (v === '$long' ? 'x'.repeat(17000) : typeof v === 'string' ? (contract.ids[v.slice(1)] && v.startsWith('$') && v.length === 2 ? contract.ids[v.slice(1)] : v)
  : Array.isArray(v) ? v.map(ids) : v && typeof v === 'object' ? Object.fromEntries(Object.entries(v).map(([k, x]) => [k, ids(x)])) : v);

/** Subset match: objects by the expected keys, arrays by length then element, strings exactly unless a placeholder. */
export function matches(actual, want, where = 'value') {
  if (want === '$any') return;
  if (want === '$absent') return assert.equal(actual, undefined, `${where} must be absent`);
  if (want === '$uuid') return assert.match(String(actual), UUID, `${where} must be a UUID`);
  if (typeof want === 'string' && want.startsWith('re:')) return assert.match(String(actual), new RegExp(want.slice(3), 'i'), where);
  if (Array.isArray(want)) {
    assert.ok(Array.isArray(actual) && actual.length === want.length, `${where}: expected ${want.length} items, got ${JSON.stringify(actual)}`);
    return want.forEach((w, i) => matches(actual[i], w, `${where}[${i}]`));
  }
  if (want && typeof want === 'object') {
    assert.ok(actual && typeof actual === 'object', `${where}: expected an object, got ${JSON.stringify(actual)}`);
    return Object.entries(want).forEach(([k, w]) => matches(actual[k], w, `${where}.${k}`));
  }
  assert.deepEqual(actual, want, where);
}

const frame = (value) => { const body = Buffer.from(JSON.stringify(value)); const head = Buffer.alloc(4); head.writeUInt32LE(body.length); return Buffer.concat([head, body]); };

/** A fake host on the local pipe; `seen` holds every op after the hello pair. */
async function fakeHost(spec) {
  const id = `slipgates-contract-${process.pid}-${Math.random().toString(36).slice(2)}`;
  const listen = process.platform === 'win32' ? `\\\\.\\pipe\\${id}` : path.join(os.tmpdir(), `${id}.sock`);
  const seen = [];
  const answer = {
    hello: () => ({ capabilities: spec.caps ?? [] }),
    'agent.hello': () => { if (spec.helloError) return { $error: 'refused' }; return { principal: spec.principal ?? 'agent:test/1' }; },
    ...spec.replies,
  };
  const server = net.createServer((socket) => {
    socket.on('error', () => {});
    let buf = Buffer.alloc(0);
    socket.on('data', (chunk) => {
      buf = Buffer.concat([buf, chunk]);
      while (buf.length >= 4 && buf.length >= 4 + buf.readUInt32LE(0)) {
        const length = buf.readUInt32LE(0);
        const request = JSON.parse(buf.subarray(4, 4 + length).toString('utf8'));
        buf = buf.subarray(4 + length);
        if (request.op !== 'hello' && request.op !== 'agent.hello') seen.push({ op: request.op, data: request.data });
        if (spec.hang?.includes(request.op)) continue;
        if (spec.close?.includes(request.op)) { socket.destroy(); return; }
        let reply = answer[request.op]; if (typeof reply === 'function') reply = reply(request.data);
        if (reply === undefined) socket.write(frame({ re: request.id, ok: false, error: 'unknown op' }));
        else if (reply?.$error) socket.write(frame({ re: request.id, ok: false, error: reply.$error }));
        else socket.write(frame({ re: request.id, ok: true, result: reply }));
      }
    });
  });
  await new Promise((resolve) => server.listen(listen, resolve));
  return { env: id, seen, close: () => new Promise((resolve) => { server.closeAllConnections?.(); server.close(resolve); }) };
}

async function connect(spec) {
  const host = spec.hostless ? null : await fakeHost(spec.host);
  const env = { AGENT_BRIDGE_PIPE: host?.env ?? `slipgates-contract-absent-${process.pid}`, AGENT_BRIDGE_TOKEN: spec.host?.token === false || spec.hostless ? '' : 'abat1_contract' };
  const client = new Client({ name: 'contract', version: '0' });
  await client.connect(new StdioClientTransport({ command: command[0], args: command.slice(1), env, stderr: process.env.CONTRACT_STDERR ? 'inherit' : 'ignore' }));
  return { host, client, close: async () => { await client.close().catch(() => {}); await host?.close(); } };
}

for (const spec of contract.cases) {
  test(spec.name, { timeout: spec.slow ? 60000 : 20000 }, async () => {
    const run = await connect(spec);
    try {
      if (spec.tools) assert.deepEqual((await run.client.listTools()).tools.map((t) => t.name).sort(), spec.tools);
      for (const [i, step] of (spec.steps ?? []).entries()) {
        const where = `step ${i + 1} (${step.tool})`;
        const before = run.host?.seen.length ?? 0;
        const result = await run.client.callTool({ name: step.tool, arguments: ids(step.args) }).catch((error) => ({ isError: true, content: [{ text: String(error.message) }] }));
        const want = ids(step.expect);
        const ops = run.host?.seen.slice(before) ?? [];
        assert.equal(Boolean(result.isError), Boolean(want.error), `${where}: isError, got ${JSON.stringify(result.content)}`);
        if (want.text) matches(result.content?.[0]?.text, want.text, `${where} text`);
        if (want.out) matches(result.structuredContent, want.out, `${where} output`);
        if (want.ops) matches(ops, want.ops, `${where} ops`);
        for (const prefix of want.noOps ?? []) assert.equal(ops.some((o) => o.op.startsWith(prefix)), false, `${where}: sent ${prefix}*`);
      }
      if (spec.onlyOps) {
        const allowed = Array.isArray(spec.onlyOps) ? spec.onlyOps : contract[spec.onlyOps];
        for (const o of run.host.seen) assert.ok(allowed.includes(o.op), `unexpected pipe op ${o.op}`);
      }
    } finally {
      await run.close();
    }
  });
}
