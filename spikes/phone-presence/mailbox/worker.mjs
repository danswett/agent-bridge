import { phoneOrigin } from '../return.mjs';
const headers = { 'Cache-Control': 'no-store', 'Access-Control-Allow-Origin': phoneOrigin, 'Access-Control-Allow-Methods': 'POST', 'Access-Control-Allow-Headers': 'Content-Type', 'Access-Control-Max-Age': '600' };
const reply = (value, status) => status === 204 ? new Response(null, { status, headers }) : Response.json(value, { status, headers });
export async function handle(request, env) {
  const path = new URL(request.url).pathname, match = /^\/s\/([^/]*)$/.exec(path);
  if (!match) return reply({ error: 'route' }, 404);
  if (!/^[0-9a-f]{32}$/.test(match[1])) return reply({ error: 'id' }, 400);
  if (!['POST', 'GET', 'OPTIONS'].includes(request.method)) return reply({ error: 'method' }, 404);
  const origin = request.headers.get('Origin');
  if ((request.method !== 'GET' && origin !== phoneOrigin) || (origin !== null && origin !== phoneOrigin)) return reply({ error: 'origin' }, 403);
  if (request.method === 'OPTIONS') return reply(null, 204);
  let body;
  if (request.method === 'POST') {
    if (request.headers.get('Content-Type')?.split(';')[0].trim().toLowerCase() !== 'application/json') return reply({ error: 'content type' }, 415);
    const declared = request.headers.get('Content-Length');
    if (declared !== null && (!/^\d+$/.test(declared) || Number(declared) > 4096)) return reply({ error: 'body size' }, 413);
    const reader = request.body?.getReader(), chunks = []; let length = 0;
    if (!reader) return reply({ error: 'empty body' }, 400);
    for (;;) {
      const { value, done } = await reader.read(); if (done) break;
      length += value.length;
      if (length > 4096) { await reader.cancel(); return reply({ error: 'body size' }, 413); }
      chunks.push(value);
    }
    const bytes = new Uint8Array(length); let offset = 0;
    for (const chunk of chunks) { bytes.set(chunk, offset); offset += chunk.length; }
    try { body = new TextDecoder('utf-8', { fatal: true }).decode(bytes); }
    catch (error) { if (!(error instanceof TypeError)) throw error; return reply({ error: 'invalid UTF-8' }, 400); }
  }
  const target = env.MAILBOX.get(env.MAILBOX.idFromName(match[1]));
  return target.fetch(new Request(request.url, { method: request.method, ...(body === undefined ? {} : { body }) }));
}
export class SessionMailbox {
  constructor(ctx, env) { this.ctx = ctx; this.storage = ctx.storage; this.now = env.now ?? Date.now; }
  async fetch(request) {
    const body = request.method === 'POST' ? await request.text() : undefined;
    const result = await this.ctx.blockConcurrencyWhile(async () => {
      const state = await this.storage.get('state');
      if (state && this.now() >= state.expiry) return { status: 404, value: { error: 'expired' } };
      if (request.method === 'GET') return state ? { status: 200, value: { blobs: [...state.blobs] } } : { status: 404, value: { error: 'empty' } };
      if (state?.blobs.length >= 3) return { status: 409, value: { error: 'full' } };
      const next = state ?? { expiry: this.now() + 300000, blobs: [] };
      next.blobs.push(body); await this.storage.put('state', next); await this.storage.setAlarm(next.expiry);
      return { status: 201, value: { stored: true } };
    });
    return reply(result.value, result.status);
  }
  async alarm() {
    await this.ctx.blockConcurrencyWhile(async () => {
      const state = await this.storage.get('state'); if (!state) return;
      if (this.now() < state.expiry) { await this.storage.setAlarm(state.expiry); return; }
      // Keep only the expiry tombstone so a retry cannot reopen an expired id.
      await this.storage.put('state', { expiry: state.expiry, blobs: [] });
    });
  }
}
export default { fetch: handle };
