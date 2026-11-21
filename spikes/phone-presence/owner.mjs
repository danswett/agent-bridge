import { lifetime } from './lifetime.mjs';
export async function confirmOwnerCode(code, expiry, options = {}) {
  if (typeof code !== 'string' || !/^[a-f0-9]{4}( [a-f0-9]{4}){3}$/.test(code) || typeof options.ask !== 'function') return false;
  const live = lifetime(expiry, options);
  try {
    const input = await live.wait(signal => options.ask(`TYPE the code shown on your phone (not yes): `, { signal }));
    const matched = live.valid() && input === code; options.print?.(matched ? 'Owner console code: match.' : 'Owner console code: no match.'); return matched;
  } catch (error) { if (error?.name !== 'AbortError') throw error; return false; }
  finally { live.close(); }
}
