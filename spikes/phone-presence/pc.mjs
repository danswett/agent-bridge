import { randomBytes } from 'node:crypto';
import { createInterface } from 'node:readline/promises';
import { pathToFileURL } from 'node:url';
import { Sessions, challenge } from './core.mjs';
import { parseStrict } from '../../pwa/js/roster/strict-json.js';
import { authenticationString } from './sas.mjs';
import { phoneOrigin } from './return.mjs';
import { seedRequest, readMailbox, requestFingerprint } from './client.mjs';
import { lifetime } from './lifetime.mjs';
import { poll } from './poller.mjs';
import { confirmOwnerCode } from './owner.mjs';
import { writeResult } from './results.mjs';
export async function verifyEvidence(bundle, raw, clocks = {}) {
  const bytes = Buffer.from(raw); if (bytes.length > 4096) throw new Error('Refused: evidence size.'); parseStrict(bytes, 'evidence');
  const r = JSON.parse(raw), store = new Sessions({ origin: phoneOrigin, rpId: 'proof.slipgates.com', now: clocks.now, monotonic: clocks.monotonic });
  store.open(bundle.create); store.register(bundle.create.sessionId, 'spike', r.registration); store.open(bundle.get);
  const facts = store.verify(bundle.get.sessionId, 'spike', r.assertion), phone = {};
  for (const field of ['OS', 'browser', 'path', 'authenticatorAttachment']) {
    const v = r.phone?.[field], markers = [...Object.values(r.registration), ...Object.values(r.assertion)];
    if (typeof v !== 'string' || v.length > 512 || markers.some(m => typeof m === 'string' && m.length > 0 && v.includes(m))) throw new Error('Refused: phone metadata.');
    phone[field] = v;
  }
  return { facts, phone, code: await authenticationString(store.credentialSpki(r.registration.id), challenge(bundle.get)) };
}
export async function runPC(options = {}) {
  const store = new Sessions({ origin: phoneOrigin, rpId: 'proof.slipgates.com', now: options.now, monotonic: options.monotonic }), generation = randomBytes(16).toString('hex');
  const bundle = { create: store.createEnrollment(), get: store.create(generation, Buffer.from(JSON.stringify({ account: 'spike', generation, fakeGenesis: true, nonce: randomBytes(16).toString('hex') }))) };
  const live = lifetime(Math.min(bundle.create.expiry, bundle.get.expiry), options), writer = options.write ?? (r => writeResult('RESULTS-PHONE.md', r)); let attempted = false;
  const write = async r => { attempted = true; await writer(r); };
  try {
    const raw = await live.wait(signal => seedRequest(bundle, { ...options, signal })), fingerprint = await requestFingerprint(raw);
    const url = new URL(phoneOrigin); url.search = new URLSearchParams({ pc: bundle.get.sessionId, fingerprint });
    (options.print ?? console.log)(`Account: spike. Genesis UNSOLVED.\nRequest fingerprint: ${fingerprint}\nOpen on PC: ${url.href}`);
    return await poll(bundle.get, { ...options, signal: live.signal, write, refuse: options.refuse ?? console.error,
      read: signal => readMailbox(bundle.get.sessionId, { ...options, signal }).then(blobs => { if (blobs.length && blobs[0] !== raw) throw new Error('Refused: request slot changed; retry in a NEW session.'); return blobs.slice(1); }),
      verify: blob => verifyEvidence(bundle, blob, options),
    });
  } catch (error) { if (!attempted) await write({ outcome: 'FAIL', invalidBlobs: Number.isSafeInteger(error?.invalidBlobs) ? error.invalidBlobs : 0 }); throw error; }
  finally { live.close(); }
}
if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  const abort = new AbortController(); process.once('SIGINT', () => abort.abort());
  runPC({ signal: abort.signal, confirm: async (code, expiry, clocks) => {
    if (!process.stdin.isTTY) throw new Error('Refused: owner console required.');
    const rl = createInterface({ input: process.stdin, output: process.stdout });
    try { return await confirmOwnerCode(code, expiry, { ...clocks, ask: (text, opts) => rl.question(text, opts), print: console.log }); } finally { rl.close(); }
  } }).then(r => { process.exitCode = r.outcome === 'PASS' ? 0 : 1; }).catch(() => { console.error('Refused: PC spike failed. Check RESULTS-PHONE.md; retry in a new session.'); process.exitCode = 1; });
}
