import { phoneOrigin } from '../return.mjs';
const headers = { 'Cache-Control': 'no-store', 'Access-Control-Allow-Origin': phoneOrigin, 'Access-Control-Allow-Methods': 'POST', 'Access-Control-Allow-Headers': 'Content-Type, Idempotency-Key', 'Access-Control-Max-Age': '600' };
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
    if (!/^[0-9a-f]{32}$/.test(request.headers.get('Idempotency-Key') ?? '')) return reply({ error: 'idempotency key' }, 400);
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
    try { body = new TextDecoder('utf-8', { fatal: true, ignoreBOM: true }).decode(bytes); }
    catch (error) { if (!(error instanceof TypeError)) throw error; return reply({ error: 'invalid UTF-8' }, 400); }
  }
  const target = env.MAILBOX.get(env.MAILBOX.idFromName(match[1]));
  return target.fetch(new Request(request.url, { method: request.method, ...(body === undefined ? {} : { body, headers: { 'Idempotency-Key': request.headers.get('Idempotency-Key') } }) }));
}
export class SessionMailbox {
  constructor(ctx, env) { this.ctx = ctx; this.storage = ctx.storage; this.now = env.now ?? Date.now; }
  async #clear() { await this.storage.deleteAll(); await this.storage.deleteAlarm(); }
  async fetch(request) {
    const body = request.method === 'POST' ? await request.text() : undefined;
    const result = await this.ctx.blockConcurrencyWhile(async () => {
      const state = await this.storage.get('state');
      if (state && (!Number.isSafeInteger(state.discard) || this.now() >= state.discard)) { await this.#clear(); return { status: 404, value: { error: 'retired' } }; }
      if (state && this.now() >= state.expiry) return { status: 404, value: { error: 'expired' } };
      if (request.method === 'GET') return state ? { status: 200, value: { blobs: state.posts.map(p => p.body) } } : { status: 404, value: { error: 'empty' } };
      const key = request.headers.get('Idempotency-Key'), previous = state?.posts.find(p => p.key === key);
      if (previous) return previous.body === body ? { status: 201, value: { stored: true } } : { status: 409, value: { error: 'idempotency conflict' } };
      if (state?.posts.length >= 3) return { status: 409, value: { error: 'full' } };
      const started = this.now(), next = state ?? { expiry: started + 300000, discard: started + 3600000, posts: [] };
      next.posts.push({ key, body }); await this.storage.put('state', next); await this.storage.setAlarm(next.expiry);
      return { status: 201, value: { stored: true } };
    });
    return reply(result.value, result.status);
  }
  async alarm() {
    await this.ctx.blockConcurrencyWhile(async () => {
      const state = await this.storage.get('state');
      if (!state || !Number.isSafeInteger(state.discard) || this.now() >= state.discard) { await this.#clear(); return; }
      if (this.now() < state.expiry) { await this.storage.setAlarm(state.expiry); return; }
      await this.storage.put('state', { expiry: state.expiry, discard: state.discard });
      await this.storage.setAlarm(state.discard);
    });
  }
}
export default { fetch: handle };
