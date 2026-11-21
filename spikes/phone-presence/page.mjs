import { origin, plan } from './phone.mjs';
import { qrSvg } from './qr.mjs';
import { mountPhone } from './screen.mjs';
export function renderSession(doc, bundle, fragment, actualOrigin, nav, returnEvidence, now = Date.now) {
  const u = new URL(origin + '/');
  const fingerprint = bundle.get.genesisHash.slice(0, 16).match(/.{4}/g)?.join(' ');
  u.hash = new URLSearchParams({ session: bundle.get.sessionId, fingerprint }).toString();
  const p = plan(bundle, fragment || u.hash, now(), actualOrigin), find = id => doc.getElementById(id);
  find('account').textContent = 'Account: spike (test only)'; find('fingerprint').textContent = p.fingerprint;
  if (fragment) {
    find('qr').innerHTML = ''; find('approve').hidden = false;
    return mountPhone(doc, p, nav, returnEvidence, now);
  }
  find('qr').innerHTML = qrSvg(u.href);
  find('approve').hidden = true;
  find('status').textContent = 'Scan with your separate phone. Compare fingerprint groups, then compare its enrollment code with the PC verifier. No match means FAIL.';
  return () => { find('qr').innerHTML = ''; find('status').textContent = 'Refused: PC session closed.'; };
}
