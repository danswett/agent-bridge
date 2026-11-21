import { phoneOrigin, postEvidence } from './return.mjs';
import { readMailbox, requestFingerprint } from './client.mjs';
import { utf8 } from '../../pwa/js/roster/framing.js';
import { isId } from '../../pwa/js/roster/codec.js';
import { parseStrict } from '../../pwa/js/roster/strict-json.js';
import { challenge, plan } from './phone.mjs';
import { renderSession } from './page.mjs';
import { lifetime } from './lifetime.mjs';
export async function boot(doc, location, nav, options = {}) {
  const now = options.now ?? Date.now, params = new URLSearchParams(location.hash ? location.hash.slice(1) : location.search), id = params.get(location.hash ? 'session' : 'pc'), expected = params.get('fingerprint');
  if (location.origin !== phoneOrigin || !isId(id) || [...params.keys()].join(',') !== (location.hash ? 'session,fingerprint' : 'pc,fingerprint')) throw new Error('Refused: request URL.');
  const loading = lifetime(now() + 300000, options); let live;
  try {
    const blobs = await loading.wait(signal => readMailbox(id, { ...options, signal })), raw = blobs[0];
    if (typeof raw !== 'string') throw new Error('Refused: request slot one missing.');
    parseStrict(utf8(raw), 'request'); const bundle = JSON.parse(raw).request;
    if (await requestFingerprint(raw) !== expected) throw new Error('Refused: request fingerprint mismatch.');
    await challenge(bundle.get);
    const fragment = '#' + new URLSearchParams({ session: id, fingerprint: expected });
    plan(bundle, fragment, now(), location.origin, expected);
    live = lifetime(Math.min(bundle.create.expiry, bundle.get.expiry), options);
    if (!live.valid()) throw new Error('Refused: request expired or closed.');
    const closeScreen = renderSession(doc, bundle, location.hash ? fragment : '', location.origin, nav, evidence => live.wait(signal => postEvidence(id, evidence, { ...options, signal })), now, expected);
    const close = () => { closeScreen(); live.close(); options.signal?.removeEventListener('abort', close); };
    options.signal?.addEventListener('abort', close, { once: true }); doc.getElementById('approval').hidden = false; return close;
  } catch (error) { live?.close(); throw error; }
  finally { loading.close(); }
}
if (typeof window !== 'undefined') {
  const abort = new AbortController(); window.addEventListener('pagehide', () => abort.abort(), { once: true });
  boot(document, new URL(window.location.href), navigator, { signal: abort.signal }).catch(error => { document.getElementById('status').textContent = error instanceof Error && error.message.startsWith('Refused:') ? error.message : 'Refused: request could not be loaded.'; });
}
