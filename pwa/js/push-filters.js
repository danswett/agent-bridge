// This device's notification filters (docs/protocol/v1-sessions.md, "Notification filters"). They are
// kept on this device and sent to each machine with push.subscribe, so the machine filters before it
// sends: the service worker never drops a push, because a browser needs a visible notification for
// every push it receives.
//
//   questions        a session is waiting on an answer (on)
//   pinned           every finished turn in a pinned session (on)
//   all              every finished turn in any session (off); covers pinned
//   deliveryFailures a count of messages a lane could not deliver (off)
export const FILTER_DEFAULTS = Object.freeze({ questions: true, pinned: true, all: false, deliveryFailures: false });

// The most pinned ids, and the longest one, a machine takes in one subscription.
export const MAX_PINNED = 300;
export const MAX_ID_CHARS = 128;

// Anything unreadable, or not a boolean, is the default.
export function readFilters(raw) {
  let v = null;
  try { v = JSON.parse(raw ?? 'null'); } catch { v = null; }
  const out = { ...FILTER_DEFAULTS };
  for (const k of Object.keys(FILTER_DEFAULTS)) { if (typeof v?.[k] === 'boolean') { out[k] = v[k]; } }
  return out;
}

// The session ids this device has pinned on one machine, from the order's keys (hostId, newline, id):
// a set, never the order, within what a machine takes. The latest pins are kept if there are too many.
export function pinnedIdsFor(keys, hostId) {
  const prefix = `${hostId ?? ''}\n`;
  const ids = (Array.isArray(keys) ? keys : [])
    .filter((k) => typeof k === 'string' && k.startsWith(prefix))
    .map((k) => k.slice(prefix.length))
    .filter((id) => id.length >= 1 && id.length <= MAX_ID_CHARS);
  return ids.slice(-MAX_PINNED);
}
