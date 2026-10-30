// Agent usage (docs/protocol/v1-usage.md): reading reports, merging every machine's into one
// row per allowance, and the words and colours a client shows. Mirrors
// src/AgentBridge.Protocol/Usage.cs, and agent-ha-bridge's AgentBridgeUsageCard exactly in
// what it shows. Pure: nothing here touches the DOM, so it is tested in Node.

export const USAGE = Object.freeze({ capability: 'usage', get: 'usage.get', subscribe: 'usage.subscribe', update: 'usage.update' });

const AGENTS = ['copilot', 'claude', 'codex'];
const SOURCES = ['api', 'cache', 'transcript', 'none'];
const str = (v) => (typeof v === 'string' && v !== '' ? v : null);
const num = (v) => (typeof v === 'number' && Number.isFinite(v) ? v : null);
const obj = (v) => (v !== null && typeof v === 'object' && !Array.isArray(v) ? v : null);

function readWindow(raw) {
  const w = obj(raw);
  const percent = num(w?.percent);
  if (!w || percent === null) { return null; }
  return {
    key: str(w.key) ?? '',
    label: str(w.label) ?? str(w.key) ?? '',
    percent,
    used: num(w.used),
    limit: num(w.limit),
    unit: str(w.unit),
    startsAt: str(w.startsAt),
    resetsAt: str(w.resetsAt),
  };
}

function readAgent(raw) {
  const a = obj(raw);
  if (!a) { return null; }
  const agent = AGENTS.includes(a.agent) ? a.agent : 'unknown';
  return {
    agent,
    label: str(a.label) ?? agent,
    account: str(a.account),
    accountKey: str(a.accountKey),
    plan: str(a.plan),
    source: SOURCES.includes(a.source) ? a.source : 'none',
    windows: Array.isArray(a.windows) ? a.windows.map(readWindow).filter(Boolean) : [],
    error: str(a.error),
    measuredAt: str(a.measuredAt),
    // A further account the host was asked to report (usage.copilotAccounts), by its place. Rows
    // are grouped by account, not by this: one machine's further account is another's own.
    slot: Number.isInteger(a.slot) ? a.slot : null,
  };
}

// usage.get's and usage.subscribe's result, and usage.update's data.
export function readUsage(raw) {
  const r = obj(raw);
  return { agents: Array.isArray(r?.agents) ? r.agents.map(readAgent).filter(Boolean) : [] };
}

export const hasReading = (entry) => entry.windows.length > 0;

// The fullest window: what the folded line shows and what the rows are ordered by. A weekly
// allowance at 90% matters more than a session window at 2%.
export const headline = (entry) => (entry.windows.length ? Math.max(...entry.windows.map((w) => w.percent)) : null);

// One row per allowance, carrying the freshest reading any machine has. An allowance belongs
// to an account, not a computer, so two machines signed in to one report it twice: a laptop
// shut for a week is outvoted, not averaged in. A reading outranks a failure however new,
// because a failure is stamped with when the attempt was made: on the clock alone, a machine
// that cannot read an account would hide the figure another machine has.
export function mergeUsage(reports) {
  const slots = new Map();
  for (const report of reports) {
    for (const a of report?.agents ?? []) {
      const key = `${a.agent}\u0000${a.accountKey ?? (a.account ?? '').toLowerCase()}`;
      const entry = { ...a, key, at: Date.parse(a.measuredAt ?? '') || 0 };
      const kind = hasReading(entry) ? 'reading' : 'failure';
      const slot = slots.get(key) ?? {};
      if (!slot[kind] || slot[kind].at < entry.at) { slot[kind] = entry; }
      slots.set(key, slot);
    }
  }
  const entries = [];
  for (const { reading, failure } of slots.values()) {
    if (!reading) { entries.push(failure); continue; }
    // The reading is drawn; a failure newer than it is still the latest word on why nothing
    // fresher exists, shown only once the reading is stale.
    entries.push(failure?.error && failure.at > reading.at ? { ...reading, error: failure.error } : reading);
  }
  // Two rows of one agent with no account to name either (Claude names none) would look the
  // same: a few characters of the key keep them apart, only when there is something to keep
  // apart.
  const perAgent = new Map();
  for (const e of entries) { perAgent.set(e.agent, (perAgent.get(e.agent) ?? 0) + 1); }
  for (const e of entries) {
    if (!e.account && e.accountKey && perAgent.get(e.agent) > 1) { e.account = `#${e.accountKey.slice(0, 4)}`; }
  }
  return entries.sort((x, y) => (headline(y) ?? 0) - (headline(x) ?? 0));
}

export function severity(percent) {
  if (!Number.isFinite(percent)) { return ''; }
  if (percent >= 90) { return 'crit'; }
  if (percent >= 75) { return 'warn'; }
  return '';
}

// An hour old is not a reading, it is a memory; routine for Codex, whose only source is its
// own last transcript.
export const isStale = (at, now = Date.now()) => at > 0 && now - at > 3600000;

// "in 25d" / "in 4h" / "in 12m". Past is "due": the window has rolled over, and the figure
// beside it is the old one until the agent runs again.
export function until(iso, now = Date.now()) {
  const at = Date.parse(iso ?? '');
  if (!Number.isFinite(at)) { return ''; }
  const seconds = Math.round((at - now) / 1000);
  if (seconds <= 0) { return 'due'; }
  if (seconds >= 86400) { return `in ${Math.round(seconds / 86400)}d`; }
  if (seconds >= 3600) { return `in ${Math.round(seconds / 3600)}h`; }
  return `in ${Math.max(1, Math.round(seconds / 60))}m`;
}

export function ago(at, now = Date.now()) {
  if (!at) { return ''; }
  const seconds = Math.round((now - at) / 1000);
  if (seconds < 90) { return 'just now'; }
  if (seconds < 3600) { return `${Math.round(seconds / 60)}m ago`; }
  if (seconds < 86400) { return `${Math.round(seconds / 3600)}h ago`; }
  return `${Math.round(seconds / 86400)}d ago`;
}

export const amount = (value) => (Number.isFinite(value) ? Math.round(value).toLocaleString('en-US') : '');

// How far through its window we are, 0-100, worked out now rather than when the host read
// it; null unless both ends are known.
export function elapsed(window, now = Date.now()) {
  const start = Date.parse(window.startsAt ?? '');
  const end = Date.parse(window.resetsAt ?? '');
  if (!Number.isFinite(start) || !Number.isFinite(end) || end <= start) { return null; }
  return Math.round(1000 * Math.min(1, Math.max(0, (now - start) / (end - start)))) / 10;
}

// The spend against the clock. A tenth of the window either way is noise, not a trend worth
// colouring.
export function pace(percent, gone) {
  const ahead = percent - gone;
  if (ahead > 10) { return { text: `${Math.round(ahead)}% ahead of pace`, ahead: true }; }
  if (ahead < -10) { return { text: `${Math.round(-ahead)}% under pace`, ahead: false }; }
  return { text: 'on pace', ahead: false };
}

// A failed read beside a reading taken minutes ago is noise; with no reading, or an old one,
// the failure is all there is to say.
export const showsError = (entry, now = Date.now()) => !!entry.error && !(hasReading(entry) && entry.at > 0 && !isStale(entry.at, now));

// "3 sessions · 0.1.0 (dev)", from hello's machine (and a live count when there is one), and
// "discovery held: …" while the host's discovery is held, as agent-ha-bridge's status line said.
export function machineStatus({ sessions = null, hostVersion = null, dev = false, held = null, readOnly = null } = {}) {
  const bits = [];
  if (Number.isInteger(sessions) && sessions >= 0) { bits.push(`${sessions} session${sessions === 1 ? '' : 's'}`); }
  if (hostVersion) { bits.push(`${hostVersion}${dev ? ' (dev)' : ''}`); }
  if (typeof readOnly === 'string' && readOnly) { bits.push(`read-only: ${readOnly}`); }
  if (typeof held === 'string' && held) { bits.push(`discovery held: ${held}`); }
  return bits.join(' \u00b7 ');
}

// Every machine's latest report, kept until replaced, so a machine that goes away is outvoted
// rather than forgotten mid-read. Listeners hear of every change.
export class UsageBook {
  #reports = new Map();
  #listeners = new Set();

  onChange(fn) { this.#listeners.add(fn); return () => this.#listeners.delete(fn); }

  #emit() { for (const fn of this.#listeners) { try { fn(); } catch (e) { console.error(e); } } }

  set(machineId, report) {
    if (!machineId) { return; }
    this.#reports.set(machineId, readUsage(report));
    this.#emit();
  }

  get size() { return this.#reports.size; }

  entries() { return mergeUsage([...this.#reports.values()]); }

  clear() {
    if (this.#reports.size === 0) { return; }
    this.#reports.clear();
    this.#emit();
  }
}
