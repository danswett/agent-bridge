// The widths of the list and the inspector from 760 px up (ui.md §5.2, #1030): what a divider
// moves, what is kept on the device, and how the panes give way when the window is short of room.
// Pure arithmetic and a small store; the dividers that use them are in ui/pane-dividers.js.

// layout.pwa in design/tokens.json, in CSS px. design-gen emits no variables for them, so the
// unit test holds these to the tokens.
export const PANES = Object.freeze({
  list: 320, listMin: 264, listMax: 480,
  inspector: 336, inspectorMin: 280, inspectorMax: 560,
  sessionMin: 440,
});

export const STEP = 16;
// Beside Your order (session-order.js), and like it never sent to a host.
export const PANES_KEY = 'agent-bridge.app.panes';

const clamp = (n, lo, hi) => Math.max(lo, Math.min(hi, n));
const minOf = (which) => (which === 'list' ? PANES.listMin : PANES.inspectorMin);
const maxOf = (which) => (which === 'list' ? PANES.listMax : PANES.inspectorMax);

// The widths shown for the widths kept, in a window `total` wide. The inspector gives way first,
// down to its minimum, then the list; the kept widths are not changed by it, so a wider window
// gives the room back. inspector: whether the inspector pane shows at all.
export function settle({ total, list = PANES.list, inspector = PANES.inspector, inspectorShown = false }) {
  let l = clamp(list, PANES.listMin, PANES.listMax);
  let i = inspectorShown ? clamp(inspector, PANES.inspectorMin, PANES.inspectorMax) : 0;
  let over = l + i + PANES.sessionMin - total;
  if (over > 0 && inspectorShown) {
    const give = Math.min(over, i - PANES.inspectorMin);
    i -= give;
    over -= give;
  }
  if (over > 0) { l -= Math.min(over, l - PANES.listMin); }
  return { list: l, inspector: i };
}

// What a divider can take its pane to: its own limits, and the room the window leaves. The list's
// divider may take the inspector down to its minimum; the inspector's may not take the list's
// width, which it has not given way yet.
export function limits(which, { total, list = PANES.list, inspectorShown = false }) {
  const shown = settle({ total, list, inspectorShown, inspector: PANES.inspectorMin });
  const others = which === 'list'
    ? (inspectorShown ? PANES.inspectorMin : 0)
    : shown.list;
  const min = minOf(which);
  return { min, max: Math.max(min, Math.min(maxOf(which), total - PANES.sessionMin - others)) };
}

// A width a pointer or a key asks for, held to limits(which, …).
export function pick(which, width, state) {
  const { min, max } = limits(which, state);
  return clamp(Math.round(width), min, max);
}

// The kept widths: { list?, inspector? }, each only where moved from its default. Storage that
// can't be read or written (blocked, full) keeps them for this page's life only.
export class PaneWidths {
  #kept = {};

  constructor({ storage = storageOrNull() } = {}) {
    this.storage = storage;
    try {
      const saved = JSON.parse(storage?.getItem(PANES_KEY) ?? 'null');
      for (const which of ['list', 'inspector']) {
        const w = saved?.[which];
        if (Number.isFinite(w)) { this.#kept[which] = clamp(w, minOf(which), maxOf(which)); }
      }
    } catch { /* unreadable: the defaults */ }
  }

  get list() { return this.#kept.list ?? PANES.list; }
  get inspector() { return this.#kept.inspector ?? PANES.inspector; }

  // save: false while a drag moves, which writes the device's storage once, where it ends.
  set(which, width, save = true) { this.#kept[which] = width; if (save) { this.#save(); } }
  reset(which) { delete this.#kept[which]; this.#save(); }
  // What a drag started from, put back unsaved when the drag is cancelled (undefined: the default).
  kept(which) { return this.#kept[which]; }
  restore(which, width) { if (width === undefined) { delete this.#kept[which]; } else { this.#kept[which] = width; } }

  #save() {
    try {
      if (Object.keys(this.#kept).length) { this.storage?.setItem(PANES_KEY, JSON.stringify(this.#kept)); }
      else { this.storage?.removeItem(PANES_KEY); }
    } catch { /* kept for this page's life */ }
  }
}

function storageOrNull() {
  try { return globalThis.localStorage ?? null; } catch { return null; }
}
