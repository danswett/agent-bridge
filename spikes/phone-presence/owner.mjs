import { lifetime } from './lifetime.mjs';
export async function confirmOwnerCode(code, expiry, options = {}) {
  if (typeof code !== 'string' || !/^[a-f0-9]{4}( [a-f0-9]{4}){3}$/.test(code) || typeof options.ask !== 'function') return false;
  const live = lifetime(expiry, options);
  try {
    const input = await live.wait(signal => options.ask(`PC verified-key code: ${code}\nTYPE the code shown on your phone (not yes): `, { signal }));
    return live.valid() && input === code;
  } catch (error) { if (error?.name !== 'AbortError') throw error; return false; }
  finally { live.close(); }
}
