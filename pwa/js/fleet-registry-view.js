// The PWA as a registry reader (docs/design/fleet-session-registry.md, slice 6): the JS port of the host's
// FleetRegistryReader (slice RC), case for case. Pure state, no sockets: relay frames in, a snapshot of the
// account's machines out. `verify(machineId, record)` is the seam for hostSign (#1776): without it a record
// is never `verified`; with it, a record it refuses is not kept. fleet-registry-reader.js stays the
// signature-only reader for when roster v2 is live; this one shows unsigned records, labelled.
export const LIVE_WINDOW_MS = 60_000;
export const CACHE_AGE_MS = 24 * 3600_000;

const isObject = (v) => v !== null && typeof v === 'object' && !Array.isArray(v);
const text = (f, name) => (isObject(f) && typeof f[name] === 'string' ? f[name] : null);
const flag = (f, name) => isObject(f) && f[name] === true;
const idOf = (f) => { const id = text(f, 'machine'); return id && id.length <= 64 ? id : null; };
const timeOf = (f, name) => { const s = text(f, name); const t = s && s.length <= 40 ? Date.parse(s) : NaN; return Number.isFinite(t) ? t : null; };

export class FleetRegistryView {
  #machines = new Map();

  // now: wall clock in ms (the 60 s window); mono: monotonic ms (the 24 h cache).
  constructor({ now = () => Date.now(), mono = () => performance.now(), verify = null } = {}) {
    Object.assign(this, { now, mono, verify });
  }

  // A registry-get reply: one frame per machine holding a record, then `known`. A machine in neither is gone.
  applyGet(frames) {
    const listed = new Set();
    for (const f of frames.filter((x) => idOf(x) !== null)) {
      const id = idOf(f);
      listed.add(id);
      const e = this.#entry(id);
      e.listed = true;
      e.name = text(f, 'name') ?? e.name;
      e.lastSeen = timeOf(f, 'lastSeen') ?? e.lastSeen;
      e.online = flag(f, 'online');
      if (e.online && 'record' in f) { this.#accept(id, e, f.record); }
    }
    // A machine's own frame takes precedence over its `known` metadata.
    for (const k of frames.filter((x) => isObject(x) && Array.isArray(x.known)).flatMap((x) => x.known)) {
      const id = idOf(k);
      if (id === null || listed.has(id)) { continue; }
      listed.add(id);
      const e = this.#entry(id);
      e.listed = true;
      e.name = text(k, 'name') ?? e.name;
      e.lastSeen = timeOf(k, 'lastSeen') ?? e.lastSeen;
      e.online = flag(k, 'online');
    }
    for (const id of [...this.#machines.keys()]) { if (!listed.has(id)) { this.#machines.delete(id); } }
  }

  // One registry-changed frame: a record, offline, or removed.
  applyChanged(frame) {
    const id = idOf(frame);
    if (id === null) { return; }
    if (flag(frame, 'removed')) { this.#machines.delete(id); return; }
    const old = this.#machines.get(id);
    const e = old ?? { listed: false, online: false, name: null, lastSeen: null, held: null };
    if (!flag(frame, 'online')) { e.online = false; return; }
    if ('record' in frame && this.#accept(id, e, frame.record)) {
      e.online = true;
      if (!old) { this.#machines.set(id, e); }
    }
  }

  snapshot() {
    const wall = this.now();
    const mono = this.mono();
    for (const [id, e] of [...this.#machines]) {
      if (e.held && mono - e.held.seen >= CACHE_AGE_MS) { e.held = null; }
      if (!e.held && !e.listed) { this.#machines.delete(id); }
    }
    return [...this.#machines].map(([id, e]) => ({
      id, name: e.name, online: e.online, lastSeen: e.lastSeen, record: e.held?.record ?? null,
      live: e.online && e.held !== null && Math.abs(e.held.at - wall) <= LIVE_WINDOW_MS,
      verified: e.held?.verified ?? false,
    }));
  }

  #entry(id) {
    let e = this.#machines.get(id);
    if (!e) { e = { listed: false, online: false, name: null, lastSeen: null, held: null }; this.#machines.set(id, e); }
    return e;
  }

  // Schema of what the order rule needs, then the 60 s rule, then order, then the verifier. A refused
  // record leaves the last accepted one in place, stale in time.
  #accept(id, e, rec) {
    const at = timeOf(rec, 'at');
    if (!isObject(rec) || rec.machine !== id || typeof rec.epoch !== 'string' || !/^[0-9a-f]{16}$/.test(rec.epoch)
      || !Number.isSafeInteger(rec.seq) || rec.seq < 1 || at === null || Math.abs(at - this.now()) > LIVE_WINDOW_MS) { return false; }
    // Equal is accepted: the relay serves the same snapshot until the host's next heartbeat.
    const old = e.held;
    if (old && (old.epoch === rec.epoch ? rec.seq < old.seq : at <= old.at)) { return false; }
    if (this.verify && !this.verify(id, rec)) { return false; }
    e.held = { epoch: rec.epoch, seq: rec.seq, at, record: structuredClone(rec), verified: this.verify !== null, seen: this.mono() };
    return true;
  }
}
