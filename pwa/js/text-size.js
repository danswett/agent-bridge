// Text size, kept on this device (docs/design/ui.md §4.4, owner decision 2026-11-06). The ramps
// (§4.3) are sized for a desktop and a phone, and an iPad's PWA takes the desktop ramp, where at
// arm's length it read too big. Smaller, Default and Larger scale every type role together
// through one multiplier on <html data-text-size>, which app.css turns into --text-scale; Default
// is the ramp as it was, and sets nothing. Text fields keep 16 px whatever the size, so Safari
// doesn't zoom on focus.
//
// It is a person's choice for this device only: never sent to a machine, never synced. Storage
// that can't be read or written leaves Default, and a choice that can't be kept still applies
// until the page goes.
const KEY = 'agent-bridge.app.textSize';
export const TEXT_SIZES = Object.freeze(['smaller', 'default', 'larger']);
export const TEXT_SIZE_LABELS = Object.freeze({ smaller: 'Smaller', default: 'Default', larger: 'Larger' });

const storageOrNull = () => { try { return globalThis.localStorage ?? null; } catch { return null; } };

export function readTextSize(storage = storageOrNull()) {
  let v = null;
  try { v = storage?.getItem(KEY) ?? null; } catch { v = null; }
  return TEXT_SIZES.includes(v) ? v : 'default';
}

// Default removes the attribute, so the page is exactly as it was before this setting.
export function applyTextSize(size, root = globalThis.document?.documentElement) {
  if (!root) { return; }
  if (size === 'default' || !TEXT_SIZES.includes(size)) { root.removeAttribute('data-text-size'); }
  else { root.setAttribute('data-text-size', size); }
}

// Returns whether the choice was kept for the next visit.
export function saveTextSize(size, storage = storageOrNull()) {
  if (!TEXT_SIZES.includes(size)) { return false; }
  try {
    if (!storage) { return false; }
    if (size === 'default') { storage.removeItem(KEY); } else { storage.setItem(KEY, size); }
    return true;
  } catch { return false; }
}
