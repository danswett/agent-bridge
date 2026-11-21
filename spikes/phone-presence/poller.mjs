import { setTimeout as sleep } from 'node:timers/promises';
import { lifetime } from './lifetime.mjs';
export async function poll(session, options) {
  if (session.used || session.ended) throw new Error('Refused: session used or ended.');
  const live = lifetime(session.expiry, options), seen = new Set(); let invalidBlobs = 0;
  const finish = async (outcome, verified) => { const report = { outcome, invalidBlobs, ...(outcome === 'PASS' ? verified : {}) }; await options.write(report); return report; };
  try {
    while (live.valid()) {
      let blobs;
      try { blobs = await live.wait(signal => options.read(signal)); }
      catch (error) { if (!live.valid()) break; if (!(error instanceof TypeError)) throw error; options.refuse('Refused: mailbox network failure.'); blobs = []; }
      if (!Array.isArray(blobs) || blobs.length > 3) throw new Error('Refused: malformed mailbox response.');
      for (const blob of blobs) {
        if (seen.has(blob)) continue; seen.add(blob);
        let verified;
        try { verified = await live.wait(() => options.verify(blob)); }
        catch { if (!live.valid()) break; invalidBlobs++; options.refuse('Refused: invalid evidence blob.'); continue; }
        if (!live.valid()) break;
        session.used = true;
        const confirmed = await live.wait(signal => (options.confirm ?? (async () => false))(verified.code, session.expiry, { ...options, signal }));
        return await finish(confirmed === true && live.valid() ? 'PASS' : 'FAIL', verified);
      }
      if (session.used || !live.valid()) break;
      await live.wait(signal => options.sleep ? options.sleep(Math.min(2000, live.remaining()), signal) : sleep(Math.min(2000, live.remaining()), undefined, { signal }));
    }
    return await finish('FAIL');
  } catch (error) { if (error?.name !== 'AbortError') throw error; return await finish('FAIL'); }
  finally { session.ended = true; live.close(); }
}
