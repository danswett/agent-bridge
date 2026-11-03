// Sign-in: GitHub's device flow, run through the relay (which holds the GitHub App's
// secret). The page shows a code; the user enters it at github.com; the relay then issues
// its own token for this account.
import { sleep } from './bytes.js';

const seconds = (value, max) => Number.isInteger(value) && value >= 1 && value <= max;
const nonempty = value => typeof value === 'string' && value.trim().length > 0;

const validAccount = account => nonempty(account?.token) && nonempty(account.accountId) && nonempty(account.login);
const invalidAccount = () => new Error('The relay returned incomplete sign-in information. Start sign-in again.');

export async function startDeviceFlow(base) {
  const r = await fetch(`${base}/v1/auth/device`, { method: 'POST' });
  if (!r.ok) { throw new Error(`sign-in is unavailable (${r.status})`); }
  const s = await r.json();
  const devicePage = 'https://github.com/login/device';
  if (s.verificationUri !== devicePage) { throw new Error('The relay returned an untrusted sign-in page.'); }
  if (!nonempty(s.deviceCode) || !nonempty(s.userCode) || !seconds(s.expiresIn, 3600) || !seconds(s.interval, 60)) {
    throw new Error('The relay returned an invalid sign-in code or lifetime.');
  }
  const deadline = Date.now() + s.expiresIn * 1000;
  return {
    userCode: s.userCode,
    verificationUri: devicePage,
    expiresIn: s.expiresIn,
    // Polls until the code is entered, refused or expired. `signal` cancels.
    async wait(signal) {
      let interval = s.interval;
      while (Date.now() < deadline) {
        await sleep(Math.min(interval * 1000, deadline - Date.now()));
        if (Date.now() >= deadline) { break; }
        if (signal?.aborted) { throw new Error('sign-in cancelled'); }
        const t = await fetch(`${base}/v1/auth/token`, {
          method: 'POST',
          headers: { 'content-type': 'application/json' },
          body: JSON.stringify({ deviceCode: s.deviceCode }),
        });
        const j = await t.json().catch(() => ({}));
        if (Date.now() >= deadline) { break; }
        if (t.status === 202) {
          if (j?.pending !== true || ('interval' in j && !seconds(j.interval, 60))) {
            throw new Error('The relay returned an invalid polling response.');
          }
          interval = Math.max(interval, j.interval ?? interval);
          continue;
        }
        if (t.status !== 200) { throw new Error(`sign-in failed (${t.status}); start sign-in again`); }
        if (!validAccount(j)) { throw invalidAccount(); }
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
      return validAccount(a) ? a : null;
    } catch { return null; }
  },
  // The relay's token as issued. The product will keep a short-lived token and refresh it;
  // that needs relay support that does not exist yet.
  save(account, storage = localStorage) {
    if (!validAccount(account)) { throw invalidAccount(); }
    storage.setItem(ACCOUNT, JSON.stringify(account));
  },
  clear(storage = localStorage) { storage.removeItem(ACCOUNT); },
};
