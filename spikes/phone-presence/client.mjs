import { mailboxBase, phoneOrigin } from './return.mjs';
import { sha256, utf8 } from '../../pwa/js/roster/framing.js';
import { hex, isId } from '../../pwa/js/roster/codec.js';
import { parseStrict } from '../../pwa/js/roster/strict-json.js';
export const requestFingerprint = async raw => hex(await sha256(utf8(raw))).slice(0, 16).match(/.{4}/g).join(' ');
export async function readMailbox(id, { fetch: send = globalThis.fetch, signal } = {}) {
  if (!isId(id)) throw new Error('Refused: invalid mailbox id.');
  const response = await send(`${mailboxBase}/s/${id}`, { redirect: 'error', cache: 'no-store', signal });
  if (response.status === 404) return [];
  if (response.status !== 200) throw new Error(`Refused: mailbox HTTP ${response.status}.`);
  if (!response.body) throw new Error('Refused: empty mailbox response.');
  const reader = response.body.getReader(), chunks = []; let size = 0;
  for (;;) {
    const { value, done } = await reader.read(); if (done) break; size += value.length;
    if (size > 80000) { await reader.cancel(); throw new Error('Refused: mailbox response size.'); } chunks.push(value);
  }
  const bytes = new Uint8Array(size); let at = 0; for (const chunk of chunks) { bytes.set(chunk, at); at += chunk.length; }
  parseStrict(bytes, 'mailbox'); const data = JSON.parse(new TextDecoder().decode(bytes));
  if (!Array.isArray(data.blobs) || data.blobs.length > 3 || !data.blobs.every(b => typeof b === 'string' && utf8(b).length <= 4096)) throw new Error('Refused: mailbox shape.');
  return data.blobs;
}
export async function seedRequest(bundle, { fetch: send = globalThis.fetch, signal } = {}) {
  const raw = JSON.stringify({ request: bundle }); if (!isId(bundle.get.sessionId) || utf8(raw).length > 4096) throw new Error('Refused: request size or id.');
  const response = await send(`${mailboxBase}/s/${bundle.get.sessionId}`, { method: 'POST', headers: { 'Content-Type': 'application/json', Origin: phoneOrigin }, body: raw, redirect: 'error', cache: 'no-store', signal });
  if (response.status !== 201) throw new Error('Refused: request slot unavailable; retry in a NEW session.');
  return raw;
}
