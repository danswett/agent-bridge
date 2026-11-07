// A client for the host's local pipe (docs/protocol/local-pipe.md): 4-byte little-endian length,
// then UTF-8 JSON. Responses are matched to requests by `re`.
import net from 'node:net';
import { createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import path from 'node:path';

export const MAX_MESSAGE = 65519;

/** What went wrong, in words an agent can act on. */
export class PipeError extends Error {
  constructor(message, kind = 'error') {
    super(message);
    this.kind = kind; // 'unavailable' (no host), 'refused' (the host said no), 'error'
  }
}

export function pipeName(sid, profileFolder) {
  const hash = createHash('sha256').update(`${sid}|${profileFolder.toLowerCase()}`, 'utf8').digest('hex');
  return `AgentBridge.Host.${hash.slice(0, 16)}`;
}

/**
 * Where the host listens. AGENT_BRIDGE_PIPE (a pipe name or socket path) wins; otherwise on
 * Windows it is derived as LocalEndpoint.For derives it, from the user's SID and the profile
 * folder (AGENT_BRIDGE_ROOT, default %LOCALAPPDATA%\AgentBridge, then AGENT_BRIDGE_PROFILE,
 * default "default"). Returns null when it cannot be told.
 */
export function resolveEndpoint(env = process.env, platform = process.platform, sidReader = readSid) {
  const explicit = env.AGENT_BRIDGE_PIPE?.trim();
  if (explicit) {
    if (platform !== 'win32' || explicit.startsWith('\\\\.\\pipe\\')) return explicit;
    return `\\\\.\\pipe\\${explicit}`;
  }
  if (platform !== 'win32') return null;
  const root = env.AGENT_BRIDGE_ROOT?.trim() || (env.LOCALAPPDATA ? path.win32.join(env.LOCALAPPDATA, 'AgentBridge') : '');
  if (!root) return null;
  const folder = path.win32.resolve(root, env.AGENT_BRIDGE_PROFILE?.trim() || 'default');
  const sid = sidReader();
  return sid ? `\\\\.\\pipe\\${pipeName(sid, folder)}` : null;
}

function readSid() {
  try {
    const out = execFileSync('whoami', ['/user', '/fo', 'csv', '/nh'], { encoding: 'utf8', windowsHide: true });
    return /"(S-1-[\d-]+)"\s*$/.exec(out.trim())?.[1] ?? null;
  } catch {
    return null;
  }
}

export function encodeFrame(value) {
  const body = Buffer.from(JSON.stringify(value), 'utf8');
  if (body.length > MAX_MESSAGE) throw new PipeError('The message is too large for the host pipe.');
  const head = Buffer.alloc(4);
  head.writeUInt32LE(body.length);
  return Buffer.concat([head, body]);
}

export class PipeClient {
  constructor(endpoint, { connectTimeoutMs = 5000 } = {}) {
    this.endpoint = endpoint;
    this.connectTimeoutMs = connectTimeoutMs;
    this.nextId = 1;
    this.pending = new Map();
    this.buffer = Buffer.alloc(0);
    this.closed = false;
  }

  connect() {
    return new Promise((resolve, reject) => {
      const socket = net.connect(this.endpoint);
      const timer = setTimeout(() => socket.destroy(new Error('timed out')), this.connectTimeoutMs);
      socket.once('connect', () => {
        clearTimeout(timer);
        this.socket = socket;
        socket.on('data', (chunk) => this.#onData(chunk));
        socket.on('close', () => this.#onClose());
        socket.on('error', () => {});
        resolve(this);
      });
      socket.once('error', (error) => {
        clearTimeout(timer);
        reject(new PipeError(`The Slipgates host is not reachable (${error.code ?? error.message}).`, 'unavailable'));
      });
    });
  }

  #onData(chunk) {
    this.buffer = Buffer.concat([this.buffer, chunk]);
    while (this.buffer.length >= 4) {
      const length = this.buffer.readUInt32LE(0);
      if (length > MAX_MESSAGE) {
        this.socket.destroy();
        return;
      }
      if (this.buffer.length < 4 + length) return;
      const body = this.buffer.subarray(4, 4 + length);
      this.buffer = this.buffer.subarray(4 + length);
      try {
        this.#dispatch(JSON.parse(body.toString('utf8')));
      } catch {
        // A frame that is not JSON is skipped; the host never sends one.
      }
    }
  }

  #dispatch(message) {
    if (message.ev !== undefined || message.re === undefined || message.ok === undefined) return;
    const entry = this.pending.get(message.re);
    if (!entry) return;
    this.pending.delete(message.re);
    if (message.ok) entry.resolve(message.result);
    else entry.reject(new PipeError(String(message.error ?? 'The host refused the request.'), 'refused'));
  }

  #onClose() {
    this.closed = true;
    for (const entry of this.pending.values()) {
      entry.reject(new PipeError('The connection to the Slipgates host closed.', 'unavailable'));
    }
    this.pending.clear();
  }

  request(op, data = {}) {
    if (this.closed || !this.socket) {
      return Promise.reject(new PipeError('The connection to the Slipgates host is closed.', 'unavailable'));
    }
    const id = this.nextId++;
    return new Promise((resolve, reject) => {
      this.pending.set(id, { resolve, reject });
      try {
        this.socket.write(encodeFrame({ id, op, data }));
      } catch (error) {
        this.pending.delete(id);
        reject(error);
      }
    });
  }

  close() {
    this.socket?.destroy();
  }
}

/**
 * Connects, says hello, and, with a credential, proves which session this is. Without a token
 * the connection stays unattributed: reads work, and the host decides what else.
 */
export async function openHost({ endpoint, token }) {
  const client = await new PipeClient(endpoint).connect();
  try {
    const hello = await client.request('hello', {});
    let principal = null;
    if (token) {
      principal = (await client.request('agent.hello', { token }))?.principal ?? null;
    }
    return { client, hello, principal };
  } catch (error) {
    client.close();
    throw error;
  }
}
