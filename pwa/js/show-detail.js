// This device's display choice only; never included in a host request.
const KEY = 'agent-bridge.app.showDetail';
const storageOrNull = () => { try { return globalThis.localStorage ?? null; } catch { return null; } };

export function readShowDetail(storage = storageOrNull()) {
  try { return storage?.getItem(KEY) !== 'false'; } catch { return true; }
}

export function saveShowDetail(value, storage = storageOrNull()) {
  if (typeof value !== 'boolean' || !storage) { return false; }
  try { storage.setItem(KEY, String(value)); return true; } catch { return false; }
}

const listeners = new Set();
export const showDetail = {
  value: readShowDetail(),
  apply(value) {
    if (typeof value !== 'boolean' || value === this.value) { return; }
    this.value = value;
    for (const listener of listeners) { listener(); }
  },
  subscribe(listener) { listeners.add(listener); return () => listeners.delete(listener); },
};
