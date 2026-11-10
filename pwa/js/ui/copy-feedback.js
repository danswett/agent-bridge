// What a Copy button says after it is pressed (#1279): "Copied", or "Couldn't copy", for the
// standard linger (receipt.js), then nothing. The words go to one polite live region, so a screen
// reader hears the outcome while the button keeps its name and its size.
import { LINGER_READ_MS } from './receipt.js';

export const COPIED = 'Copied';
export const COPY_FAILED = "Couldn't copy";

// press() copies and says how it went. A second press restarts the linger, so the first one's
// timer can't clear the second's words early. timers is injectable for tests.
export function copyFeedback({ copy, say, linger = LINGER_READ_MS, timers = { set: (fn, ms) => setTimeout(fn, ms), clear: (t) => clearTimeout(t) } }) {
  let timer = null;
  return {
    async press() {
      let ok = false;
      try { ok = (await copy()) === true; } catch { /* a copy that throws failed */ }
      timers.clear(timer);
      say(ok ? COPIED : COPY_FAILED, ok);
      timer = timers.set(() => { timer = null; say('', ok); }, linger);
      return ok;
    },
    // The row was drawn again or removed: nothing left to clear later.
    cancel() { timers.clear(timer); timer = null; },
  };
}
