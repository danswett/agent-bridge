#!/usr/bin/env node
// Slipgates MCP server: agent-ha-bridge's tools over the host's local pipe, on this machine.
import { Server } from '@modelcontextprotocol/sdk/server/index.js';
import { StdioServerTransport } from '@modelcontextprotocol/sdk/server/stdio.js';
import { CallToolRequestSchema, ListToolsRequestSchema } from '@modelcontextprotocol/sdk/types.js';
import { pathToFileURL } from 'node:url';
import { openHost, resolveEndpoint, PipeError } from './pipe.mjs';
import { LaunchLedger, getLaunchOptions, launchSession, listSessions, readSession, replyToSession } from './tools.mjs';

export const INSTRUCTIONS =
  'Slipgates messaging is connected on this machine. When you need to message, read or start another ' +
  'agent session, use these tools instead of any other messaging route; they carry your identity and ' +
  'the permissions the owner gave you. If a tool answers "refused", you may not do that here: say so ' +
  'rather than working around it.';

const str = (description) => ({ type: 'string', description });
const SCHEMAS = {
  list_agent_sessions: { type: 'object', properties: { include: { type: 'string', enum: ['content'], description: 'Add each session\'s latest response and question. Default: status only.' } } },
  read_agent_session: {
    type: 'object',
    required: ['session_id'],
    properties: {
      session_id: str('Session id from list_agent_sessions.'),
      since: str('The "since" reply_to_agent_session returned; the answer then waits for a newer turn.'),
      wait_seconds: { type: 'number', description: 'Wait up to this long (max 600) for the turn to finish.' },
    },
  },
  reply_to_agent_session: {
    type: 'object',
    required: ['session_id', 'text'],
    properties: { session_id: str('Session id.'), text: str('The whole message, up to 16 KiB. It arrives as one line.') },
  },
  launch_agent_session: {
    type: 'object',
    properties: {
      machine: str('Ignored: this server reaches this machine only.'),
      prompt: str('The opening prompt, whole, up to 16 KiB.'),
      agent: str('Launcher id from get_launch_options.'),
      workspace: str('Workspace label.'),
      profile: str('Agency profile.'),
      resume: str('Id of a resumable session.'),
      model: str('Model.'),
      effort: str('Effort.'),
      context: str('Context.'),
      permissions: { type: 'string', enum: ['Ask permission', 'Allow all'] },
    },
  },
  read_launch_result: { type: 'object', required: ['request'], properties: { machine: str('Ignored.'), request: str('The "request" launch_agent_session returned.') } },
  get_launch_options: { type: 'object', properties: { machine: str('Ignored.') } },
};

const NEEDS = {
  list_agent_sessions: 'sessions.read',
  read_agent_session: 'sessions.read',
  reply_to_agent_session: 'sessions.reply',
  launch_agent_session: 'sessions.launch',
  read_launch_result: 'sessions.launch',
  get_launch_options: 'sessions.launch',
};

const DESCRIPTIONS = {
  list_agent_sessions: 'List the agent sessions on this machine (status only unless include is "content").',
  read_agent_session: 'Read a session\'s latest answer. Pass the since from reply_to_agent_session to never get a stale answer.',
  reply_to_agent_session: 'Send a message to another agent session on this machine.',
  launch_agent_session: 'Start (or resume) an agent session on this machine.',
  read_launch_result: 'What a launch made through this server came to.',
  get_launch_options: 'What launch_agent_session can name on this machine.',
};

const MAX_TEXT = 16 * 1024;
const REQUEST_KEY = /^[A-Za-z0-9._:-]{1,128}$/;
// Without an accepted agent principal the connection is unattributed: only reads are offered.
const UNATTRIBUTED_OK = new Set(['list_agent_sessions', 'read_agent_session']);

const allowed = (conn, name) => new Set(conn.hello?.capabilities ?? []).has(NEEDS[name]) && (Boolean(conn.principal) || UNATTRIBUTED_OK.has(name));

function validate(name, args) {
  if (args === null || typeof args !== 'object' || Array.isArray(args)) throw new PipeError('Arguments must be an object.');
  const requireString = (key, { max = 512, optional = false } = {}) => {
    const value = args[key];
    if (value === undefined && optional) return;
    if (typeof value !== 'string' || (!optional && value.length === 0)) throw new PipeError(`${key} must be a${optional ? '' : ' non-empty'} string.`);
    if (Buffer.byteLength(value, 'utf8') > max) throw new PipeError(`${key} is too large (max ${max} bytes).`);
  };
  const requireRequestKey = ({ optional = false } = {}) => {
    const value = args.request;
    if (value === undefined && optional) return;
    if (typeof value !== 'string' || !REQUEST_KEY.test(value)) throw new PipeError('request must be 1 to 128 characters: letters, digits, "-", "_", "." or ":".');
  };
  switch (name) {
    case 'list_agent_sessions':
      if (args.include !== undefined && args.include !== 'content') throw new PipeError('include must be "content".');
      break;
    case 'read_agent_session':
      requireString('session_id');
      requireString('since', { optional: true });
      if (args.wait_seconds !== undefined && !Number.isFinite(args.wait_seconds)) throw new PipeError('wait_seconds must be a number.');
      break;
    case 'reply_to_agent_session':
      requireString('session_id');
      requireString('text', { max: MAX_TEXT });
      break;
    case 'launch_agent_session':
      requireString('prompt', { max: MAX_TEXT, optional: true });
      requireRequestKey({ optional: true });
      for (const key of ['agent', 'workspace', 'profile', 'resume', 'model', 'effort', 'context', 'permissions']) requireString(key, { optional: true });
      break;
    case 'read_launch_result':
      requireRequestKey();
      break;
    default:
  }
}

/** A lazily (re)connected host. Returns null from `ensure` when there is none. */
export function createConnection({ env = process.env, open = openHost, endpoint = () => resolveEndpoint(env) } = {}) {
  let current = null;
  const ensure = async () => {
    if (current && !current.client.closed) return current;
    const where = endpoint();
    if (!where) return (current = null);
    try {
      current = await open({ endpoint: where, token: env.AGENT_BRIDGE_TOKEN?.trim() || undefined });
    } catch {
      current = null;
    }
    return current;
  };
  return { ensure, close: () => current?.client.close() };
}

export function createServer(connection) {
  const ledger = new LaunchLedger();
  const server = new Server({ name: 'slipgates', version: '0.1.0' }, { capabilities: { tools: {} }, instructions: INSTRUCTIONS });

  server.setRequestHandler(ListToolsRequestSchema, async () => {
    const conn = await connection.ensure();
    if (!conn) return { tools: [] }; // no host: stay quiet so the agent falls back
    const tools = Object.keys(SCHEMAS)
      .filter((name) => allowed(conn, name))
      .map((name) => ({ name, description: DESCRIPTIONS[name], inputSchema: SCHEMAS[name] }));
    return { tools };
  });

  server.setRequestHandler(CallToolRequestSchema, async (request) => {
    const { name, arguments: args = {} } = request.params;
    const json = (value) => ({ content: [{ type: 'text', text: JSON.stringify(value, null, 2) }], structuredContent: value });
    try {
      if (!Object.hasOwn(SCHEMAS, name)) throw new PipeError(`Unknown tool ${name}.`);
      const conn = await connection.ensure();
      if (!conn) throw new PipeError('Slipgates is not running on this machine.', 'unavailable');
      if (!allowed(conn, name)) throw new PipeError(`${name} is not available on this connection.`, 'refused');
      validate(name, args);
      const host = conn.client;
      switch (name) {
        case 'list_agent_sessions': return json(await listSessions(host, args));
        case 'read_agent_session':
          return json(await readSession(host, args.session_id, { since: args.since, waitSeconds: Math.min(600, Number(args.wait_seconds) || 0) }));
        case 'reply_to_agent_session': return json(await replyToSession(host, args.session_id, args.text));
        case 'launch_agent_session': return json(await launchSession(host, ledger, args));
        case 'read_launch_result': return json(ledger.read(args.request));
        default: return json(await getLaunchOptions(host));
      }
    } catch (error) {
      return { isError: true, content: [{ type: 'text', text: error instanceof Error ? error.message : String(error) }] };
    }
  });
  return server;
}

async function main() {
  const connection = createConnection();
  const server = createServer(connection);
  await server.connect(new StdioServerTransport());
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  main().catch((error) => {
    process.stderr.write(`[slipgates-mcp] ${error?.message ?? error}\n`);
    process.exit(1);
  });
}
