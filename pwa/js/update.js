// A machine's updates, seen and asked for from this device (#51, docs/protocol/v1-update.md).
// The Windows app on that machine does the work, as it does its automatic updates: it finds the
// release, downloads it, waits until no session there is busy (or six hours), then stops the
// host and restarts both on the new release. Its host carries what the app reports here, and a
// press of Check or Install there. A machine that is read-only offers none of this. Mirrors
// src/AgentBridge.Protocol/Update.cs. The words and state are pure, and tested in Node.
import { ago } from './ui/time.js';

export const UPDATE = Object.freeze({
  capability: 'update', get: 'update.get', subscribe: 'update.subscribe', event: 'update.state',
  check: 'update.check', install: 'update.install',
});

const PHASES = ['idle', 'checking', 'downloading', 'waiting', 'installing'];
const OUTCOMES = ['updated', 'current', 'refused', 'failed'];
const str = (v) => (typeof v === 'string' && v !== '' ? v : null);
const obj = (v) => (v !== null && typeof v === 'object' && !Array.isArray(v) ? v : null);

function readResult(raw) {
  const r = obj(raw);
  if (!r) { return null; }
  return {
    // An unknown outcome reads as failed, never as updated.
    outcome: OUTCOMES.includes(r.outcome) ? r.outcome : 'failed',
    from: str(r.from), to: str(r.to), reason: str(r.reason), request: str(r.request), at: str(r.at),
  };
}

// update.get's and update.subscribe's result, and update.state's data.
export function readUpdateState(raw) {
  const s = obj(raw) ?? {};
  const progress = typeof s.progress === 'number' && Number.isFinite(s.progress) ? Math.max(0, Math.min(100, Math.round(s.progress))) : null;
  return {
    current: str(s.current),
    installer: s.installer === true,
    unavailable: str(s.unavailable),
    available: str(s.available),
    notes: str(s.notes),
    checkedAt: str(s.checkedAt),
    // An unknown phase reads as idle: nothing to wait on.
    phase: PHASES.includes(s.phase) ? s.phase : 'idle',
    progress,
    waiting: str(s.waiting),
    request: str(s.request),
    automatic: s.automatic === true,
    last: readResult(s.last),
  };
}

// update.check's and update.install's result.
export function readUpdateCommandResult(raw) {
  const r = obj(raw) ?? {};
  return { outcome: r.outcome === 'accepted' ? 'accepted' : 'refused', reason: str(r.reason), state: obj(r.state) ? readUpdateState(r.state) : null };
}

// Something is going on that ends on its own: worth looking at again soon.
export const underWay = (s) => !!s && (['checking', 'downloading', 'installing'].includes(s.phase) || (s.phase === 'waiting' && !!s.request));

const settled = (s) => !!s && s.installer && s.phase === 'idle';
export const canCheck = (s) => settled(s);
export const canInstall = (s) => settled(s) && !!s.available;

const sentence = (s) => (!s ? '' : /[.!?]$/.test(s) ? s : `${s}.`);
// A line of its own, which a host's reason (written to follow a colon) starts.
const line = (s) => { const t = sentence(s); return t ? t[0].toUpperCase() + t.slice(1) : t; };

// What the machine's updater is doing, in a line.
export function updateLine(s, now = Date.now()) {
  if (!s) { return ''; }
  if (!s.installer) { return line(s.unavailable ?? 'its app is not running, so it cannot update now'); }
  const next = s.available ?? 'the update';
  switch (s.phase) {
    case 'checking': return 'Checking for updates\u2026';
    case 'downloading': return `Downloading ${next}${s.progress !== null ? ` (${s.progress}%)` : ''}\u2026`;
    case 'installing': return `Installing ${next}: its app and host restart\u2026`;
    case 'waiting': return `${s.available ?? 'The update'} installs when it can: ${sentence(s.waiting ?? 'a session is busy')}`;
    default:
      if (s.available) { return `${s.available} is available (running ${s.current ?? 'an older release'}).`; }
      return `Up to date: ${s.current ?? 'unknown'}${s.checkedAt ? `, checked ${ago(s.checkedAt, now)}` : ''}.`;
  }
}

// How the last update asked for, or installed by itself, ended; null when none has.
export function lastLine(s, now = Date.now()) {
  const l = s?.last;
  if (!l) { return null; }
  switch (l.outcome) {
    case 'updated': return `Updated from ${l.from ?? 'an older release'} to ${l.to ?? 'a newer one'} ${ago(l.at, now)}.`;
    case 'current': return `Already up to date when asked ${ago(l.at, now)}.`;
    case 'refused': return `Not updated: ${sentence(l.reason ?? 'refused')}`;
    default: return `The update failed: ${sentence(l.reason ?? 'no reason given')}`;
  }
}

// Check and Install on each machine, and Check all. A press goes to the machine (fleet.call),
// which answers at once: accepted (its app takes it from there, and the machine's state says how
// it goes) or refused, and why, shown on the row until the next press.
export class UpdateControl {
  #busy = new Set();
  #notes = new Map();
  #listeners = new Set();

  // fleet: a FleetStatus (update(id), call(id, op, data), noteUpdate(id, state), watch(id)).
  constructor({ fleet, newRequest = () => crypto.randomUUID() }) {
    Object.assign(this, { fleet, newRequest });
  }

  onChange(fn) { this.#listeners.add(fn); return () => this.#listeners.delete(fn); }

  #emit() { for (const fn of this.#listeners) { try { fn(); } catch (e) { console.error(e); } } }

  state(id) { return this.fleet.update(id); }

  // { busy, note } for one machine's row.
  stateOf(id) { return { busy: this.#busy.has(id), note: this.#notes.get(id) ?? null }; }

  // kind: 'check' or 'install'.
  async press(id, kind) {
    if (this.#busy.has(id)) { return; }
    this.#busy.add(id);
    this.#notes.delete(id);
    this.#emit();
    try {
      const before = this.fleet.update(id);
      const result = readUpdateCommandResult(await this.fleet.call(id, kind === 'install' ? UPDATE.install : UPDATE.check, { request: this.newRequest() }));
      // The state as the press found it: older than an update.state that came meanwhile, which
      // it must not replace.
      if (result.state && this.fleet.update(id) === before) { this.fleet.noteUpdate(id, result.state); }
      if (result.outcome === 'accepted') { this.fleet.watch?.(id); } else { this.#notes.set(id, line(result.reason ?? 'it refused')); }
    } catch (e) {
      this.#notes.set(id, `It did not take it: ${sentence(e?.message ?? 'the machine could not be reached')}`);
    } finally {
      this.#busy.delete(id);
      this.#emit();
    }
  }

  // Every machine that can check now.
  checkAll(ids) {
    return Promise.all(ids.filter((id) => canCheck(this.state(id))).map((id) => this.press(id, 'check')));
  }

  forget(id) {
    this.#notes.delete(id);
    this.#emit();
  }

  clear() {
    this.#notes.clear();
    this.#emit();
  }
}
