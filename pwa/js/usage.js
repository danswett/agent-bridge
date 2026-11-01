// Agent usage (docs/protocol/v1-usage.md): reading reports, merging every machine's into one
// row per allowance, and the words and colours a client shows. Mirrors
// src/AgentBridge.Protocol/Usage.cs, and agent-ha-bridge's AgentBridgeUsageCard exactly in
// what it shows. Pure: nothing here touches the DOM, so it is tested in Node.

export const USAGE = Object.freeze({ capability: 'usage', shared: 'usage.shared', get: 'usage.get', subscribe: 'usage.subscribe', update: 'usage.update' });

// How long a reading is kept, by a host for the account's other machines and by this app for the
// account (#172): a fortnight-old figure is history, not usage.
export const KEPT_MS = 14 * 86400000;
export const KEPT_MAX = 32;

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
    // The machine that took it, on a reading a host passed on from another of the account's
    // machines (usage.shared). Clients mark their own with the machine that sent it.
    readBy: readReader(a.readBy),
  };
}

function readReader(raw) {
  const r = obj(raw);
  const id = str(r?.id);
  if (!id || id.length > 64) { return null; }
  return { id, name: (str(r.name) ?? id.slice(0, 8)).slice(0, 64) };
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
  const entries = combineUsage(reports);
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

// One entry per allowance, as mergeUsage, without the names it adds for showing them: what this
// app keeps of an account's usage between visits. Of two readings taken at the same moment, the
// one from the report given first.
export function combineUsage(reports) {
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
  return entries;
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

// Which machine took a reading: a kept one, or one a host passed on, may be from a machine that is
// offline now (#172). The Windows app leaves it out for the machine it runs on; here there is none.
export const readOn = (entry) => (entry.readBy?.name ? `read on ${entry.readBy.name}` : '');

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
//
// With a cache (useCache, once signed in), it also keeps the last reading of each of the account's
// allowances between visits (#172), so a reading outlives the machine that took it going offline,
// as agent-ha-bridge's retained sensors did in Home Assistant. Each reading is marked with the
// machine that took it (readBy): a host's own with that host, one it passed on from another
// machine already says. Readings older than KEPT_MS go.
export class UsageBook {
  #reports = new Map();
  #listeners = new Set();
  #kept = [];
  #cache = null;
  #saved = '[]';
  // Machines forgotten (#50) while this page is open: what they read is dropped even where another
  // host still passes it on, until they come back.
  #forgotten = new Set();

  constructor({ now = () => Date.now() } = {}) { this.now = now; }

  onChange(fn) { this.#listeners.add(fn); return () => this.#listeners.delete(fn); }

  #emit() { for (const fn of this.#listeners) { try { fn(); } catch (e) { console.error(e); } } }

  // name: what the machine is called, for "read on <name>".
  set(machineId, report, name = null) {
    if (!machineId) { return; }
    // A machine reporting is the account's again, whether or not it was forgotten.
    this.#forgotten.delete(machineId);
    this.#reports.set(machineId, { report: readUsage(report), name: typeof name === 'string' && name ? name : null });
    this.#keep();
    this.#emit();
  }

  // A machine that has said it reports no usage (hello without the capability: usage.publish
  // off) has nothing to outvote; its last report would otherwise be shown for good. forget: the
  // machine was forgotten (#50), so what it read is not kept or shown either, wherever it comes from.
  delete(machineId, { forget = false } = {}) {
    const had = this.#reports.delete(machineId);
    if (forget) {
      this.#forgotten.add(machineId);
      this.#kept = this.#kept.filter((e) => e.readBy?.id !== machineId);
    } else if (!had) { return; }
    this.#keep();
    this.#emit();
  }

  get size() { return this.#reports.size; }

  // Whether there is anything to show: a report from a machine, or a reading kept.
  get empty() { return this.#reports.size === 0 && this.entries().length === 0; }

  #live() {
    return [...this.#reports].map(([id, { report, name }]) => ({
      agents: report.agents
        .map((a) => (a.readBy ? a : { ...a, readBy: { id, name: name ?? id.slice(0, 8) } }))
        .filter((a) => !this.#forgotten.has(a.readBy.id)),
    }));
  }

  // Where readings are kept, none past their 14 days, whether or not anything has been heard since:
  // the view redraws on a timer, and a machine that went offline sends nothing more.
  entries() {
    const all = mergeUsage([...this.#live(), { agents: this.#kept }]);
    if (!this.#cache) { return all; }
    const oldest = this.now() - KEPT_MS;
    return all.filter((e) => e.at >= oldest);
  }

  // What is kept for the signed-in account (as the store gave it back), and where to keep it:
  // save(entries) is called with every change to what is kept.
  useCache({ entries = [], save = () => {} } = {}) {
    const oldest = this.now() - KEPT_MS;
    this.#kept = readUsage({ agents: Array.isArray(entries) ? entries.slice(0, KEPT_MAX) : [] }).agents.filter((a) => (Date.parse(a.measuredAt ?? '') || 0) >= oldest);
    this.#saved = JSON.stringify(this.#kept);
    this.#cache = { save };
    this.#keep();
    this.#emit();
  }

  #keep() {
    if (!this.#cache) { return; }
    const oldest = this.now() - KEPT_MS;
    this.#kept = combineUsage([...this.#live(), { agents: this.#kept }])
      .filter((e) => e.at >= oldest)
      // As many allowances as a host keeps (PeerUsage), the one read longest ago going first.
      .sort((x, y) => y.at - x.at)
      .slice(0, KEPT_MAX)
      .map(({ key, at, ...a }) => a);
    const text = JSON.stringify(this.#kept);
    // Every report from every machine passes through here; most change nothing kept.
    if (text === this.#saved) { return; }
    this.#saved = text;
    try { this.#cache.save(this.#kept); } catch (e) { console.warn('usage cache:', e?.message); }
  }

  // Everything tied to the signed-in account goes with it, what is kept included.
  clear() {
    const had = this.#reports.size > 0 || this.#kept.length > 0;
    this.#reports.clear();
    this.#kept = [];
    this.#cache = null;
    this.#saved = '[]';
    this.#forgotten.clear();
    if (had) { this.#emit(); }
  }
}
