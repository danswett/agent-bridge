// The order of the sessions list, kept on this device: the person's, not the host's (ui.md §1
// principle 2, §6.1; #164). Sorting by the last update moved a card on every tool call, every
// reasoning line and every background-count change; with several sessions running the list
// reshuffled every few seconds, you lost your place, and a tap could land on the wrong card.
//
//   * Your order (the default): a session never placed before goes to the top of the unpinned
//     rows once, newest first when several arrive together, and then stays where it is. Nothing
//     the host sends moves a row. Only a move (Alt+Up/Down, a drag, the row menu) does.
//   * Pinned rows sit above the rest, in the order they were pinned or moved to.
//   * Most recent: the old order, last updated first. Machine: by machine name, then your order.
//   * A session the host removed is kept in memory as ended, for the Ended group, until the page
//     goes; nothing else about it is kept.
//   * Activity: a row whose session updated since it was last open on screen is unread. A
//     session seen for the first time is not news: what it did before then was never shown.
//
// It knows nothing of the DOM: the list's markup is rewritten by #179, and this stays.
// Storage that can't be read or written (a blocked or full localStorage) falls back to Most
// recent, as the issue asks: an order that can't be kept would be lost at the next reload.
const KEY = 'agent-bridge.app.order';
export const SORTS = Object.freeze(['yours', 'recent', 'machine']);
export const SORT_LABELS = Object.freeze({ yours: 'Your order', recent: 'Most recent', machine: 'Machine' });
// Rows remembered whose sessions aren't shown now (another machine's, one offline): enough for
// every session of a few busy machines, and bounded so the record never grows without end.
const MAX_KEYS = 300;
const MAX_ENDED = 20;

export const orderKey = (hostId, id) => `${hostId ?? ''}\n${id}`;
const keyOf = (e) => orderKey(e.hostId, e.session.id);

// Last updated first; ties by id, then machine, so the order is stable. Times are in one fixed
// format, so they compare as strings.
function newer(a, b) {
  const x = a.session, y = b.session;
  if (x.updatedAt !== y.updatedAt) { return x.updatedAt < y.updatedAt ? 1 : -1; }
  if (x.id !== y.id) { return x.id < y.id ? -1 : 1; }
  return a.hostId < b.hostId ? -1 : a.hostId > b.hostId ? 1 : 0;
}

const strings = (v) => (Array.isArray(v) ? v.filter((k) => typeof k === 'string') : []);

function read(raw) {
  let v = null;
  try { v = JSON.parse(raw ?? 'null'); } catch { v = null; }
  const seen = {};
  if (v?.seen && typeof v.seen === 'object') {
    for (const [k, t] of Object.entries(v.seen)) { if (typeof t === 'string') { seen[k] = t; } }
  }
  const pinned = [...new Set(strings(v?.pinned))];
  const pins = new Set(pinned);
  return {
    sort: SORTS.includes(v?.sort) ? v.sort : 'yours',
    pinned,
    order: [...new Set(strings(v?.order))].filter((k) => !pins.has(k)),
    seen,
  };
}

export class SessionOrder {
  #state;
  #usable = true;
  #ended = [];
  // What the last arrange() showed, by group: moves are among the rows on screen, not among
  // rows remembered for sessions that aren't shown.
  #shown = { pinned: [], sessions: [] };

  constructor({ storage = globalThis.localStorage, now = () => Date.now() } = {}) {
    this.storage = storage;
    this.now = now;
    let raw = null;
    try {
      if (!storage) { throw new Error('no storage'); }
      raw = storage.getItem(KEY);
    } catch { this.#usable = false; }
    this.#state = read(raw);
  }

  // Whether the order can be kept on this device. When it can't, the list is Most recent.
  get usable() { return this.#usable; }

  get sort() { return this.#usable ? this.#state.sort : 'recent'; }

  setSort(sort) {
    if (!SORTS.includes(sort) || this.#state.sort === sort) { return; }
    this.#state.sort = sort;
    this.#save();
  }

  isPinned(key) { return this.#state.pinned.includes(key); }

  // entries: [{ hostId, session }], every live session shown. nameOf(hostId): a machine's name,
  // for the Machine sort. shows(hostId): whether a machine is shown, for the Ended group.
  // settled(hostId): whether a machine has sent its whole list (sessions.synced). Its sessions
  // are placed only then, all together, newest first: a snapshot arrives one session at a time,
  // and placing each as it came would put them in reverse, depending on how the frames batched.
  // Until then they show at the top, newest first.
  // Returns { pinned, sessions, ended }: the first two are entries, in order; ended is
  // [{ hostId, session, endedAt }], the most recently ended first.
  arrange(entries, { nameOf = () => '', shows = () => true, settled = () => true } = {}) {
    this.#place(entries.filter((e) => settled(e.hostId)), entries);
    const s = this.#state;
    const placed = new Set([...s.pinned, ...s.order]);
    const pending = entries.filter((e) => !placed.has(keyOf(e))).sort(newer).map(keyOf);
    const rank = new Map([...pending, ...s.pinned, ...s.order].map((k, i) => [k, i]));
    const yours = (a, b) => rank.get(keyOf(a)) - rank.get(keyOf(b));
    const by = {
      yours,
      recent: newer,
      machine: (a, b) => (nameOf(a.hostId) ?? '').localeCompare(nameOf(b.hostId) ?? '') || (a.hostId < b.hostId ? -1 : a.hostId > b.hostId ? 1 : 0) || yours(a, b),
    }[this.sort];
    const pins = new Set(s.pinned);
    const pinned = entries.filter((e) => pins.has(keyOf(e))).sort(by);
    const sessions = entries.filter((e) => !pins.has(keyOf(e))).sort(by);
    this.#shown = { pinned: pinned.map(keyOf), sessions: sessions.map(keyOf) };
    const live = new Set(entries.map(keyOf));
    const ended = this.#ended.filter((e) => shows(e.hostId) && !live.has(keyOf(e)));
    return { pinned, sessions, ended };
  }

  // Sessions never placed go to the top of the unpinned rows, once; a session seen for the
  // first time starts read. all: every session shown, which the trim never forgets.
  #place(entries, all) {
    const s = this.#state;
    const placed = new Set([...s.pinned, ...s.order]);
    const fresh = entries.filter((e) => !placed.has(keyOf(e))).sort(newer).map(keyOf);
    let changed = fresh.length > 0;
    if (changed) { s.order.unshift(...fresh); }
    for (const e of all) {
      const k = keyOf(e);
      if (s.seen[k] === undefined) { s.seen[k] = e.session.updatedAt; changed = true; }
    }
    if (s.order.length + s.pinned.length > MAX_KEYS) {
      const live = new Set(all.map(keyOf));
      for (let i = s.order.length - 1; i >= 0 && s.order.length + s.pinned.length > MAX_KEYS; i--) {
        if (!live.has(s.order[i])) { delete s.seen[s.order[i]]; s.order.splice(i, 1); }
      }
    }
    if (changed) { this.#save(); }
  }

  #group(key) {
    return this.#shown.pinned.includes(key) ? 'pinned' : this.#shown.sessions.includes(key) ? 'sessions' : null;
  }

  // Whether a row on screen can move by one (delta -1 up, 1 down) within its group.
  canMove(key, delta) {
    const g = this.#group(key);
    if (this.sort !== 'yours' || !g) { return false; }
    const i = this.#shown[g].indexOf(key) + delta;
    return i >= 0 && i < this.#shown[g].length;
  }

  // Moves a row past its neighbour on screen. Rows remembered for sessions not shown keep
  // their places relative to each other.
  move(key, delta) {
    if (!this.canMove(key, delta)) { return false; }
    const g = this.#group(key);
    const shown = this.#shown[g];
    const neighbour = shown[shown.indexOf(key) + delta];
    this.#insert(key, neighbour, delta > 0);
    return true;
  }

  // A drag: the row goes before (or after) the target row, and into the target's group, so a
  // row dragged among the pinned ones is pinned.
  place(key, target, after = false) {
    if (this.sort !== 'yours' || key === target || !this.#group(key) || !this.#group(target)) { return false; }
    this.#insert(key, target, after);
    return true;
  }

  #insert(key, target, after) {
    const s = this.#state;
    this.#drop(key);
    const list = s.pinned.includes(target) ? s.pinned : s.order;
    const at = list.indexOf(target);
    list.splice(at < 0 ? list.length : after ? at + 1 : at, 0, key);
    this.#save();
  }

  #drop(key) {
    const s = this.#state;
    s.pinned = s.pinned.filter((k) => k !== key);
    s.order = s.order.filter((k) => k !== key);
  }

  // A pinned row goes to the foot of the pinned ones, nearest where it was; an unpinned one to
  // the top of the rest.
  setPinned(key, pinned) {
    if (this.isPinned(key) === pinned) { return; }
    this.#drop(key);
    if (pinned) { this.#state.pinned.push(key); } else { this.#state.order.unshift(key); }
    this.#save();
  }

  unread(key, updatedAt) {
    const seen = this.#state.seen[key];
    return seen !== undefined && typeof updatedAt === 'string' && updatedAt > seen;
  }

  // The row is open on screen: what it shows now has been seen.
  markSeen(key, updatedAt) {
    const seen = this.#state.seen[key];
    if (typeof updatedAt !== 'string' || (seen !== undefined && seen >= updatedAt)) { return; }
    this.#state.seen[key] = updatedAt;
    this.#save();
  }

  // What the store says changed ({ type, hostId, session?, removed? }, sessions-store.js): a
  // session the host removed has ended. One that only went from view (its machine went offline,
  // or another is shown) has not, and keeps its place.
  observe(change) {
    if (change?.type === 'remove' && change.session) { this.#ended1(change.hostId, change.session); }
    if (change?.type === 'synced') { for (const s of change.removed ?? []) { this.#ended1(change.hostId, s); } }
  }

  // A machine's complete list: rows remembered for its sessions that aren't in it ended while
  // nobody here was looking, and are forgotten.
  synced(hostId, liveIds) {
    const s = this.#state;
    const prefix = orderKey(hostId, '');
    const gone = (k) => k.startsWith(prefix) && !liveIds.has(k.slice(prefix.length));
    const before = s.order.length + s.pinned.length;
    s.order = s.order.filter((k) => !gone(k));
    s.pinned = s.pinned.filter((k) => !gone(k));
    let changed = s.order.length + s.pinned.length !== before;
    for (const k of Object.keys(s.seen)) { if (gone(k)) { delete s.seen[k]; changed = true; } }
    if (changed) { this.#save(); }
  }

  #ended1(hostId, session) {
    const key = orderKey(hostId, session.id);
    this.#drop(key);
    delete this.#state.seen[key];
    this.#save();
    this.#ended = [{ hostId, session, endedAt: new Date(this.now()).toISOString() }, ...this.#ended.filter((e) => keyOf(e) !== key)].slice(0, MAX_ENDED);
  }

  // The account went: its ended sessions go with it.
  clearEnded() { this.#ended = []; }

  #save() {
    if (!this.#usable) { return; }
    try {
      this.storage.setItem(KEY, JSON.stringify(this.#state));
    } catch {
      // Full or blocked: an order that can't be kept would be lost at the next reload, so the
      // list says Most recent rather than pretend.
      this.#usable = false;
    }
  }
}
