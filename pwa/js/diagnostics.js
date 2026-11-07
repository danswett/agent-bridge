// "Diagnose this machine" (#802, docs/protocol/v1-diagnostics.md): a read-only snapshot of a
// machine's health, asked for over its end-to-end channel by a person's device pinned there.
// Shown here for the person to read, and copied only after they say they have reviewed it; it
// is never sent anywhere from here. Mirrors src/AgentBridge.Protocol/Diagnostics.cs.
//
// The host sends closed codes, validated versions, times and counts only, but this does not take
// its word for it: a misbehaving host must not put free text in front of the person, or on their
// clipboard, through this. Each section and field the snapshot requires must be there with its
// type, or the whole reply is unreadable (never filled in as if healthy). Each code is checked
// against its declared set (else "unknown"), each version against the journal's own grammar,
// each time against .NET's ISO form. An exception's type is shown only if it is one of the
// runtime's the journal lists (JournalIdentifiers.RuntimeExceptions), else <unrecognized>; its
// frames name the product's code, which only the host and the Windows app can check, so here
// they are counted and never named. Anything else is left out and counted.
import { BRAND } from './brand.js';
import { copyText } from './markdown.js';

export const DIAGNOSTICS = Object.freeze({ capability: 'diagnostics', get: 'diagnostics.get', schema: 1, entries: 100, minIntervalMs: 2000 });

// The host's reasons for not answering (DiagnosticsProtocol.Refused, TooSoon, Unfinished), each
// said in the person's words; any other reason is not shown.
const REFUSALS = Object.freeze({
  "diagnostics are for a person's device approved at this machine, through the relay": (name) => `Approve this device at ${name} to diagnose it`,
  'diagnostics were read moments ago; try again shortly': () => 'Diagnosed moments ago \u00B7 try again shortly',
  'diagnostics could not be read in time; try again shortly': (name) => `${name} didn't answer in time \u00B7 try again`,
});
export function refusalText(reason, name = 'this machine') {
  const say = typeof reason === 'string' && Object.hasOwn(REFUSALS, reason) ? REFUSALS[reason] : null;
  return say ? say(name) : `${name} couldn't be reached \u00B7 try again`;
}

const MISSING = ['notRecorded', 'notStarted', 'notConfigured', 'invalid', 'failed'];
const READ_ONLY = ['none', 'configReadOnly', 'configProblem', 'agentHaBridge', 'other'];
const RELAY = ['off', 'signedOut', 'connecting', 'connected', 'refused'];
const CHECK = ['missing', 'needsAction', 'working', 'blocked', 'failed'];
const AGENT = ['other', 'copilot', 'claude', 'codex'];
const KINDS = ['copilot', 'claude', 'codex'];
const CHECKS = ['installation', 'versionCheck', 'hooks', 'authentication', 'activation'];
// The #737 journal's entries as JournalCodec writes them (src/AgentBridge.Protocol/Journal.cs):
// enum names as declared, not camelCase; a name outside the declared set reads as 'unknown'. Its
// omissions are flags.
const FAMILY = ['RelayConnect', 'RelayDrop', 'Delivery', 'HookIntake', 'Launch', 'Update', 'HostLifecycle', 'Crash'];
const KIND = ['RelayConnected', 'RelayConnectFailed', 'RelayClosed', 'DeliveryStage', 'DeliveryRefused', 'HookPassFailed', 'HookSpoolRetry', 'HookSpoolDropped',
  'HookIntakePaused', 'HookIntakeResumed', 'LaunchRequested', 'UpdateStep', 'HostStarted', 'HostStopped', 'UnhandledException'];
const STAGE = ['Received', 'Queued', 'Typed', 'Submitted', 'Confirmed', 'Spooled', 'Handled', 'Connect', 'Handshake', 'Other'];
const OUTCOME = ['Ok', 'Refused', 'Failed', 'Cancelled', 'TimedOut', 'Unavailable'];
const OMISSIONS = ['VersionInvalid', 'ExceptionTypeUnrecognized', 'FramesTruncated', 'StagesTruncated', 'CodeOutOfRange', 'DurationClamped', 'FrameUnrecognized'];
const MAX_AGENTS = 8;
const MAX_ENTRIES = 200;
const MAX_STAGES = 8;
const MAX_FRAMES = 16;

// The journal's version grammar (JournalContract.IsVersion): numbers, and a closed set of
// prerelease words, and nothing else.
const VERSION = /^\d{1,9}(\.\d{1,9}){1,3}(-(alpha|beta|rc|preview|dev|ci)(\.\d{1,9}){0,3})?$/;
const MAX_VERSION = 64;
// An ISO time as .NET writes one, and nothing else.
const TIME = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2}):(\d{2})(\.\d{1,7})?(Z|[+-](\d{2}):(\d{2}))$/;
const UNRECOGNIZED = '<unrecognized>';
// The runtime's exception types the journal names (JournalIdentifiers.RuntimeExceptions, by full
// name; test/unit/diagnostics.test.mjs keeps the two lists the same). The product's own types are
// listed there by its assemblies, which this cannot check, so they read as <unrecognized> here.
const EXCEPTION_TYPES = new Set([
  'Exception', 'SystemException', 'ApplicationException', 'InvalidOperationException', 'ArgumentException', 'ArgumentNullException',
  'ArgumentOutOfRangeException', 'NotSupportedException', 'NotImplementedException', 'PlatformNotSupportedException',
  'NullReferenceException', 'IndexOutOfRangeException', 'Collections.Generic.KeyNotFoundException', 'FormatException',
  'OverflowException', 'ArithmeticException', 'DivideByZeroException', 'InvalidCastException', 'ObjectDisposedException',
  'OperationCanceledException', 'Threading.Tasks.TaskCanceledException', 'TimeoutException', 'OutOfMemoryException',
  'InsufficientExecutionStackException', 'TypeInitializationException', 'Reflection.TargetInvocationException', 'AggregateException',
  'IO.IOException', 'IO.FileNotFoundException', 'IO.DirectoryNotFoundException', 'IO.PathTooLongException', 'IO.EndOfStreamException',
  'IO.InvalidDataException', 'UnauthorizedAccessException', 'Text.Json.JsonException', 'Text.DecoderFallbackException',
  'Text.EncoderFallbackException', 'Text.RegularExpressions.RegexMatchTimeoutException', 'UriFormatException',
  'Net.Http.HttpRequestException', 'Net.WebSockets.WebSocketException', 'Net.Sockets.SocketException', 'ComponentModel.Win32Exception',
  'Runtime.InteropServices.ExternalException', 'Runtime.InteropServices.COMException', 'Runtime.InteropServices.SEHException',
  'Security.Cryptography.CryptographicException', 'Security.Authentication.AuthenticationException',
  'Threading.ThreadInterruptedException', 'Threading.SemaphoreFullException', 'Threading.SynchronizationLockException',
  'Threading.LockRecursionException', 'Threading.AbandonedMutexException', 'DllNotFoundException', 'EntryPointNotFoundException',
  'BadImageFormatException', 'MissingMethodException', 'Threading.Channels.ChannelClosedException',
].map((n) => `System.${n}`));

// A field the snapshot requires that is absent or of another type makes the whole reply
// unreadable: Bad is thrown, and readDiagnostics answers null.
class Bad extends Error {}
const need = (ok) => { if (!ok) { throw new Bad(); } };
const isObj = (v) => v !== null && typeof v === 'object' && !Array.isArray(v);
const section = (v) => { need(isObj(v)); return v; };
const has = (o, k) => Object.prototype.hasOwnProperty.call(o, k);
// A required time, or (nullable) a required time or null; a malformed one is never shown. Each
// component is checked as .NET checks it: Date.parse would turn 2025-02-30 into March 1.
const isTime = (v) => {
  const m = typeof v === 'string' ? TIME.exec(v) : null;
  if (!m) { return false; }
  const [year, month, day, hour, minute, second] = m.slice(1, 7).map(Number);
  const days = [31, year % 4 === 0 && (year % 100 !== 0 || year % 400 === 0) ? 29 : 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31];
  if (year < 1 || month < 1 || month > 12 || day < 1 || day > days[month - 1] || hour > 23 || minute > 59 || second > 59) { return false; }
  if (m[8] !== 'Z' && (Number(m[10]) > 59 || Number(m[9]) * 60 + Number(m[10]) > 14 * 60)) { return false; }
  return !Number.isNaN(Date.parse(v));
};
const at = (v) => { need(isTime(v)); return v; };
const atOrNull = (o, k) => { need(has(o, k)); need(o[k] === null || isTime(o[k])); return o[k]; };
const int = (v) => { need(Number.isSafeInteger(v)); return v; };
const bool = (v) => { need(typeof v === 'boolean'); return v; };
const str = (v) => { need(typeof v === 'string'); return v; };
const strOrNull = (o, k) => { need(has(o, k)); need(o[k] === null || typeof o[k] === 'string'); return o[k]; };
const array = (v) => { need(Array.isArray(v)); return v; };
// Enum names are camelCase on the wire; a name from the closed set, else 'unknown', as the
// snapshot's own codes read a newer host's.
const one = (set, v) => (set.includes(v) ? v : 'unknown');
const code = (set, v) => one(set, str(v));
// An optional reason: absent or null is none.
const maybe = (set, v) => (v == null ? null : code(set, v));
const version = (v) => (typeof v === 'string' && v.length <= MAX_VERSION && VERSION.test(v) ? v : null);
const count = (v) => (Number.isSafeInteger(v) && v >= 0 ? v : 0);
const whole = (v) => (Number.isSafeInteger(v) ? v : null);
const list = (v) => (Array.isArray(v) ? v : []);

// One journal entry, or null for one this does not read (counted as unreadable).
function readEntry(raw) {
  const e = isObj(raw) ? raw : null;
  // A seq the journal never writes (JournalContract.IsFit: from 1) is unreadable, not shown as #0.
  if (!e || !isTime(e.at) || !Number.isSafeInteger(e.seq) || e.seq < 1) { return null; }
  const ex = isObj(e.exception) ? e.exception : null;
  return {
    seq: e.seq, at: e.at, family: one(FAMILY, e.family), kind: one(KIND, e.kind), outcome: one(OUTCOME, e.outcome),
    code: whole(e.code), version: version(e.version), durationMs: e.ms == null ? null : count(e.ms),
    stages: list(e.stages).slice(0, MAX_STAGES).filter(isObj).map((s) => ({ name: one(STAGE, s.name), ms: count(s.ms) })),
    exception: ex ? {
      type: EXCEPTION_TYPES.has(ex.type) ? ex.type : UNRECOGNIZED,
      frames: Math.min(list(ex.frames).length, MAX_FRAMES),
    } : null,
    omitted: Number.isSafeInteger(e.omitted) && e.omitted > 0 ? [...OMISSIONS.filter((_, i) => e.omitted & (1 << i)), ...(e.omitted >= 1 << OMISSIONS.length ? ['Other'] : [])] : [],
  };
}

function readAgent(a) {
  section(a);
  return {
    id: code(AGENT, a.id), version: version(strOrNull(a, 'version')), versionMissing: maybe(MISSING, a.versionMissing),
    lastHookHandledAt: atOrNull(a, 'lastHookHandledAt'),
    ...Object.fromEntries(CHECKS.map((c) => [c, code(CHECK, a[c])])),
  };
}

// diagnostics.get's result, or null when it is not a snapshot this reads: another schema, or a
// section or required field missing, null where it may not be, or of another type, as the
// Windows app's strict reader refuses it (MachineDiagnosis.cs).
export function readDiagnostics(raw) {
  try {
    const s = section(raw);
    if (s.schema !== DIAGNOSTICS.schema) { return null; }
    const host = section(s.host);
    const restart = section(s.lastRestart);
    const relay = section(s.relay);
    const holds = section(s.holds);
    const readiness = section(s.readiness);
    const journal = section(s.journal);
    const hostVersion = strOrNull(host, 'version');
    const held = holds.discoveryHeld == null ? (need(has(holds, 'discoveryHeld')), null) : array(holds.discoveryHeld).map(str);
    const allAgents = array(readiness.agents);
    const agents = allAgents.slice(0, MAX_AGENTS).map(readAgent);
    const allEntries = array(journal.entries);
    const raws = allEntries.slice(0, MAX_ENTRIES);
    const entries = raws.map(readEntry).filter(Boolean);
    const counter = (k) => count(int(journal[k]));
    return {
      at: at(s.at),
      host: {
        version: version(hostVersion), dev: bool(host.dev), startedAt: at(host.startedAt), uptimeSeconds: count(int(host.uptimeSeconds)),
        versionMissing: hostVersion !== null && version(hostVersion) === null ? 'invalid' : maybe(MISSING, host.versionMissing),
      },
      lastRestart: { at: atOrNull(restart, 'at'), causeMissing: (need(has(restart, 'causeMissing')), maybe(MISSING, restart.causeMissing)) },
      relay: { state: code(RELAY, relay.state), since: atOrNull(relay, 'since'), missing: maybe(MISSING, relay.missing) },
      holds: {
        // null, as discoveryHeld, when it is not known: never read as none, which only [] says.
        readOnly: holds.readOnly === null ? null : code(READ_ONLY, holds.readOnly),
        readOnlyMissing: maybe(MISSING, holds.readOnlyMissing),
        discoveryHeld: held === null ? null : [...new Set(held.filter((k) => KINDS.includes(k)))],
        // A kind this does not know is held too: said as unknown, never dropped into none.
        discoveryUnknown: held !== null && held.some((k) => !KINDS.includes(k)),
        discoveryMissing: maybe(MISSING, holds.discoveryMissing),
      },
      readiness: {
        checkedAt: atOrNull(readiness, 'checkedAt'), omitted: count(int(readiness.omitted)) + allAgents.length - agents.length,
        missing: maybe(MISSING, readiness.missing),
        agents,
      },
      journal: {
        entries,
        // What the host left out, and what this did: never shown as if there were nothing.
        omittedByLimit: counter('omittedByLimit'), omittedBySize: counter('omittedBySize'),
        invalid: counter('invalid') + raws.length - entries.length + allEntries.length - raws.length,
        evictedByCount: counter('evictedByCount'), evictedByAge: counter('evictedByAge'), evictedByBytes: counter('evictedByBytes'),
        rejectedOversized: counter('rejectedOversized'), rejectedMalformed: counter('rejectedMalformed'),
        droppedOnLoad: counter('droppedOnLoad'), loadFailed: bool(journal.loadFailed), persistFailures: counter('persistFailures'),
        missing: maybe(MISSING, journal.missing),
      },
    };
  } catch (e) {
    if (e instanceof Bad) { return null; }
    throw e;
  }
}

// Times are sent in UTC and shown in Pacific (AGENTS.md), with PST or PDT as the date has it,
// exactly as the Windows app shows them (MachineDiagnosis.cs).
const PACIFIC = new Intl.DateTimeFormat('en-US', {
  timeZone: 'America/Los_Angeles', hourCycle: 'h23', year: 'numeric', month: '2-digit', day: '2-digit',
  hour: '2-digit', minute: '2-digit', second: '2-digit', timeZoneName: 'short',
});
export function pacific(iso) {
  const p = Object.fromEntries(PACIFIC.formatToParts(new Date(iso)).map((x) => [x.type, x.value]));
  return `${p.year}-${p.month}-${p.day} ${p.hour}:${p.minute}:${p.second} ${p.timeZoneName}`;
}

const or = (v, missing) => v ?? `unavailable (${missing ?? 'unknown'})`;
// Only a list with nothing in it is none; one with only kinds this doesn't know is not known.
const heldText = (h) => (h.discoveryHeld.length
  ? [...h.discoveryHeld, ...(h.discoveryUnknown ? ['unknown'] : [])].join(', ')
  : h.discoveryUnknown ? or(null, null) : 'none');
const when = (v, missing) => (v ? pacific(v) : or(v, missing));

// The plain text shown for review and copied after it: only what readDiagnostics kept. name: the
// machine's name as this device lists it, which the person already sees.
export function diagnosticsText(d, name = 'this machine') {
  const lines = [
    `${BRAND.name} diagnostics \u00B7 ${name} \u00B7 ${pacific(d.at)}`,
    `host: ${or(d.host.version, d.host.versionMissing)}${d.host.dev ? ' (dev)' : ''}, started ${pacific(d.host.startedAt)}, up ${d.host.uptimeSeconds}s`,
    `last restart: ${when(d.lastRestart.at, 'notRecorded')}, cause ${d.lastRestart.causeMissing ? `unavailable (${d.lastRestart.causeMissing})` : 'unknown'}`,
    `relay: ${d.relay.missing ? `unavailable (${d.relay.missing})` : d.relay.state}${d.relay.since ? ` since ${pacific(d.relay.since)}` : ''}`,
    `read-only: ${d.holds.readOnlyMissing ? `unavailable (${d.holds.readOnlyMissing})` : or(d.holds.readOnly, null)}`,
    `discovery held: ${d.holds.discoveryMissing ? `unavailable (${d.holds.discoveryMissing})` : d.holds.discoveryHeld === null ? or(null, null) : heldText(d.holds)}`,
    `readiness: ${d.readiness.missing ? `unavailable (${d.readiness.missing})` : `checked ${when(d.readiness.checkedAt)}`}${d.readiness.omitted ? `, ${d.readiness.omitted} more not shown` : ''}`,
    ...d.readiness.agents.map((a) => `  ${a.id} ${or(a.version, a.versionMissing)}: ${CHECKS.map((c) => `${c} ${a[c]}`).join(', ')}${a.lastHookHandledAt ? `, last hook ${pacific(a.lastHookHandledAt)}` : ''}`),
  ];
  const j = d.journal;
  if (j.missing) {
    lines.push(`journal: unavailable (${j.missing})`);
  } else {
    lines.push(`journal: ${j.entries.length} entries, newest first` + [
      ['not asked for', j.omittedByLimit], ['left out for size', j.omittedBySize], ['unreadable', j.invalid],
      ['evicted by count', j.evictedByCount], ['evicted by age', j.evictedByAge], ['evicted by size', j.evictedByBytes],
      ['rejected oversized', j.rejectedOversized], ['rejected malformed', j.rejectedMalformed], ['dropped on load', j.droppedOnLoad],
      ['persist failures', j.persistFailures],
    ].filter(([, n]) => n > 0).map(([what, n]) => `; ${n} ${what}`).join('') + (j.loadFailed ? '; load failed' : ''));
    for (const e of j.entries) {
      const parts = [`#${e.seq}`, pacific(e.at), `${e.family}.${e.kind}`, e.outcome];
      if (e.code !== null) { parts.push(`code ${e.code}`); }
      if (e.version) { parts.push(e.version); }
      if (e.durationMs !== null) { parts.push(`${e.durationMs}ms`); }
      if (e.stages.length) { parts.push(`stages ${e.stages.map((s) => `${s.name} ${s.ms}ms`).join(' > ')}`); }
      if (e.exception) { parts.push(`exception ${e.exception.type}${e.exception.frames ? ` (${e.exception.frames} frames)` : ''}`); }
      if (e.omitted.length) { parts.push(`omitted ${e.omitted.join(',')}`); }
      lines.push(`  ${parts.join(' ')}`);
    }
  }
  return lines.join('\n');
}

const IDLE = Object.freeze({ phase: 'idle', text: null, reviewed: false, copied: false, note: null });

// One machine's Diagnose, Review, Copy and Close (ui/views.js). A machine whose hello does not
// offer diagnostics shows no Diagnose (offered), and is never asked.
export class DiagnoseControl {
  #states = new Map();
  #askedAt = new Map();
  #epochs = new Map();
  #listeners = new Set();

  // fleet: a FleetStatus (diagnosable(id), call(id, op, data)).
  constructor({ fleet, copy = (text) => copyText(text), now = () => Date.now() }) {
    Object.assign(this, { fleet, copy, now });
  }

  onChange(fn) { this.#listeners.add(fn); return () => this.#listeners.delete(fn); }

  #emit() { for (const fn of this.#listeners) { try { fn(); } catch (e) { console.error(e); } } }

  #set(id, change) { this.#states.set(id, { ...this.stateOf(id), ...change }); this.#emit(); }

  // { phase: idle|asking|review, text, reviewed, copied, note } for one machine's row.
  stateOf(id) { return this.#states.get(id) ?? IDLE; }

  // true: Diagnose is shown. false: the machine's host does not offer it (its row says to update
  // it instead). null: not known yet, so neither.
  offered(id) { return this.fleet.diagnosable(id); }

  // name: the machine's name as this device lists it.
  async press(id, name = 'this machine') {
    const s = this.stateOf(id);
    if (s.phase === 'asking' || this.fleet.diagnosable(id) !== true) { return; }
    if (this.now() - (this.#askedAt.get(id) ?? -Infinity) < DIAGNOSTICS.minIntervalMs) { return; }
    this.#askedAt.set(id, this.now());
    const epoch = this.#epochs.get(id) ?? 0;
    this.#set(id, { ...IDLE, phase: 'asking' });
    let next;
    try {
      const d = readDiagnostics(await this.fleet.call(id, DIAGNOSTICS.get, { maxEntries: DIAGNOSTICS.entries }));
      next = d ? { ...IDLE, phase: 'review', text: diagnosticsText(d, name) } : { ...IDLE, note: `${name} sent diagnostics this app can't read` };
    } catch (e) {
      // Only the host's own three reasons are worded; its text is never shown.
      next = { ...IDLE, note: refusalText(e?.fromHost ? e.reason : null, name) };
    }
    if ((this.#epochs.get(id) ?? 0) === epoch) { this.#set(id, next); }
  }

  review(id, reviewed) {
    if (this.stateOf(id).phase === 'review') { this.#set(id, { reviewed: reviewed === true, copied: false }); }
  }

  // Copied only once the person has said they reviewed it.
  async copyReviewed(id) {
    const s = this.stateOf(id);
    if (s.phase !== 'review' || !s.reviewed || !s.text) { return false; }
    const ok = await this.copy(s.text);
    if (this.stateOf(id) === s || this.stateOf(id).text === s.text) {
      this.#set(id, ok ? { copied: true, note: null } : { note: 'It could not be copied here. Select the text to copy it.' });
    }
    return ok;
  }

  close(id) { this.forget(id); }

  forget(id) {
    this.#epochs.set(id, (this.#epochs.get(id) ?? 0) + 1);
    if (this.#states.delete(id)) { this.#emit(); }
  }

  clear() {
    for (const id of this.#states.keys()) { this.#epochs.set(id, (this.#epochs.get(id) ?? 0) + 1); }
    this.#states.clear();
    this.#askedAt.clear();
    this.#emit();
  }
}
