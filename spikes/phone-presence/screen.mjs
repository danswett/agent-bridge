import { Approval } from './phone.mjs';
export function mountPhone(doc, p, nav, returnEvidence, now = Date.now) {
  const find = id => doc.getElementById(id), approval = new Approval(p, now);
  let closed = false;
  find('fingerprint').textContent = p.fingerprint;
  find('account').textContent = 'Account: spike (test only)';
  find('approve').disabled = typeof returnEvidence !== 'function';
  find('status').textContent = returnEvidence ? 'Compare all four groups, then approve on your separate phone.' : 'Refused: automatic return is not configured.';
  find('approve').onclick = async () => {
    find('approve').disabled = true;
    try {
      if (typeof returnEvidence !== 'function') throw new Error('Refused: automatic return is not configured.');
      const evidence = await approval.run(nav, true, code => { find('enrollment').textContent = code; });
      if (closed) throw new Error('Refused: session closed.');
      if (now() >= p.get.expiry) throw new Error('Refused: session expired.');
      await returnEvidence(evidence);
      find('status').textContent = 'Evidence returned automatically. Compare the enrollment code with the PC. No match means FAIL; this is not root acceptance.';
    } catch (error) {
      find('status').textContent = error?.name === 'NotAllowedError' || error?.name === 'AbortError'
        ? 'Refused: phone approval was cancelled. No result accepted.' : `Refused: ${error instanceof Error ? error.message : 'unexpected phone approval failure.'}`;
    }
  };
  return () => { closed = true; approval.close(); find('approve').disabled = true; };
}
