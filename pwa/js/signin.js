// Sign-in: GitHub's device flow, run through the relay (which holds the GitHub App's
// secret). The page shows a code; the user enters it at github.com; the relay then issues
// its own token for this account.
import { sleep } from './bytes.js';

export async function startDeviceFlow(base) {
  const r = await fetch(`${base}/v1/auth/device`, { method: 'POST' });
  if (!r.ok) { throw new Error(`sign-in is unavailable (${r.status})`); }
  const s = await r.json();
  return {
    userCode: String(s.userCode ?? ''),
    verificationUri: String(s.verificationUri ?? ''),
    expiresIn: Number(s.expiresIn) || 900,
    // Polls until the code is entered, refused or expired. `signal` cancels.
    async wait(signal) {
      let interval = Number(s.interval) || 5;
      const deadline = Date.now() + (Number(s.expiresIn) || 900) * 1000;
      while (Date.now() < deadline) {
        await sleep(interval * 1000);
        if (signal?.aborted) { throw new Error('sign-in cancelled'); }
        const t = await fetch(`${base}/v1/auth/token`, {
          method: 'POST',
          headers: { 'content-type': 'application/json' },
          body: JSON.stringify({ deviceCode: s.deviceCode }),
        });
        const j = await t.json().catch(() => ({}));
        if (t.status === 202) { if (j.interval) { interval = Number(j.interval) || interval; } continue; }
        if (!t.ok) { throw new Error(`sign-in failed: ${j.error ?? t.status}`); }
        return j;
      }
      throw new Error('the code expired; sign in again');
    },
  };
}

// Prefixed keys: localStorage is per origin, and /s4/ on the same origin uses 'account'.
const ACCOUNT = 'agent-bridge.app.account';

export const accountStore = {
  load(storage = localStorage) {
    try {
      const a = JSON.parse(storage.getItem(ACCOUNT) ?? 'null');
      return a && typeof a.token === 'string' ? a : null;
    } catch { return null; }
  },
  // The relay's token as issued. The product will keep a short-lived token and refresh it;
  // that needs relay support that does not exist yet.
  save(account, storage = localStorage) { storage.setItem(ACCOUNT, JSON.stringify(account)); },
  clear(storage = localStorage) { storage.removeItem(ACCOUNT); },
};
