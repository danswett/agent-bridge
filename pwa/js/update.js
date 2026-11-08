// A machine's updates, seen and asked for from this device (#51, docs/protocol/v1-update.md).
// The Windows app on that machine does the work, as it does its automatic updates: it finds the
// release, downloads it, waits until no session there is busy (or six hours), then stops the
// host and restarts both on the new release. Its host carries what the app reports here, and a
// press of Check or Install there. A machine that is read-only offers none of this. Mirrors
// src/AgentBridge.Protocol/Update.cs. The words and state are pure, and tested in Node.
//
// Update now (#1159, v1-update.md "Update now"): Install without the app's wait for a quiet
// machine, signed by this device. A quiet machine updates on one press; a busy one, or one whose
// sessions the bridge can't read, asks first, listing what it would go over, and the press carries
// what was shown (accept). Every word about it is this file's, from the machine's codes, never the
// machine's own text (#826).
import { ago } from './ui/time.js';
import { clockTime, plainText } from './auth-state.js';

export const UPDATE = Object.freeze({
  capability: 'update', get: 'update.get', subscribe: 'update.subscribe', event: 'update.state',
  check: 'update.check', install: 'update.install',
});

// UpdateProtocol.BusyListed and BusyMost: the busy sessions a state lists, and the most any count
// says (an accept counting more is refused bad-args).
export const BUSY_LISTED = 32;
export const BUSY_MOST = 1_000_000;
// How long a machine leaving for an Update now reads "Restarting to update…" once its channel has
// closed, then Offline as any other (#815's 90 s; v1-update.md "Announced as planned").
export const RESTART_SHOWN_MS = 90 * 1000;

const PHASES = ['idle', 'checking', 'downloading', 'waiting', 'installing'];
const OUTCOMES = ['updated', 'current', 'refused', 'failed'];
const BUSY_STATES = ['approval', 'question', 'working'];
const str = (v) => (typeof v === 'string' && v !== '' ? v : null);
const obj = (v) => (v !== null && typeof v === 'object' && !Array.isArray(v) ? v : null);
// A code (UpdateProtocol.IsCode): a short lowercase word or words joined by '-'.
const code = (v) => (typeof v === 'string' && /^[a-z-]{1,32}$/.test(v) ? v : null);
const count = (v) => (Number.isSafeInteger(v) && v >= 0 ? Math.min(v, BUSY_MOST) : 0);

function readResult(raw) {
  const r = obj(raw);
  if (!r) { return null; }
  return {
    // An unknown outcome reads as failed, never as updated.
    outcome: OUTCOMES.includes(r.outcome) ? r.outcome : 'failed',
    from: str(r.from), to: str(r.to), reason: str(r.reason), request: str(r.request), at: str(r.at),
    code: code(r.code),
  };
}

// One busy session, by id and state only. An id that isn't text reads as empty, as the host's
// reader has it (a confirmation names that one "A session"); a state this device can't word is
// dropped.
function readBusySession(raw) {
  const s = obj(raw);
  if (!s || !BUSY_STATES.includes(s.state)) { return null; }
  return { id: typeof s.id === 'string' ? s.id : '', state: s.state };
}

// UpdateBusy: what Update now would go over, as the machine counts it. Its sessions are listed
// approvals first, then questions, then turns, at most BUSY_LISTED; every count at most BUSY_MOST.
export function readBusy(raw) {
  const b = obj(raw);
  if (!b) { return null; }
  const rank = (s) => BUSY_STATES.indexOf(s.state);
  const sessions = (Array.isArray(b.sessions) ? b.sessions : []).map(readBusySession).filter(Boolean)
    .map((s, i) => ({ s, i })).sort((x, y) => rank(x.s) - rank(y.s) || x.i - y.i).map((x) => x.s).slice(0, BUSY_LISTED);
  return {
    working: count(b.working), waiting: count(b.waiting), unknown: b.unknown === true, sessions,
    moreApprovals: count(b.moreApprovals), moreOther: count(b.moreOther),
  };
}

// What to show when the machine says nothing of its sessions: it can't tell, so it asks first.
const CANT_TELL = Object.freeze({ working: 0, waiting: 0, unknown: true, sessions: [], moreApprovals: 0, moreOther: 0 });

// Nothing working, no question or approval waiting, and its sessions known: one press, no dialog.
export const quiet = (b) => !!b && !b.unknown && b.working === 0 && b.waiting === 0 && b.sessions.length === 0 && b.moreApprovals === 0 && b.moreOther === 0;

// The press's accept: exactly what the confirmation showed, so the machine can tell what the
// person agreed to go over (v1-update.md "Covered by the confirmation").
export function acceptOf(b) {
  const a = { working: b.working, waiting: b.waiting };
  if (b.unknown) { a.unknown = true; }
  if (b.sessions.length) { a.sessions = b.sessions.map(({ id, state }) => ({ id, state })); }
  if (b.moreApprovals || b.moreOther) { Object.assign(a, { moreApprovals: b.moreApprovals, moreOther: b.moreOther }); }
  return a;
}

// UpdateNowArgs, signed with the press: now, the release shown, and accept only when one was shown.
export function nowArgs({ release, accept = null }) {
  return accept ? { now: true, release, accept } : { now: true, release };
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
    // #1159: the host's say on Update now, and the app's on the press it carries.
    canUpdateNow: s.canUpdateNow === true,
    updateNowWhy: code(s.updateNowWhy),
    updateNowAt: str(s.updateNowAt),
    busy: readBusy(s.busy),
    requestNow: s.requestNow === true,
    waitingCode: code(s.waitingCode),
  };
}

// update.check's and update.install's result. An unknown outcome reads as refused.
export function readUpdateCommandResult(raw) {
  const r = obj(raw) ?? {};
  const outcome = ['accepted', 'confirm'].includes(r.outcome) ? r.outcome : 'refused';
  return { outcome, reason: str(r.reason), state: obj(r.state) ? readUpdateState(r.state) : null, code: code(r.code) };
}

// Something is going on that ends on its own: worth looking at again soon.
export const underWay = (s) => !!s && (['checking', 'downloading', 'installing'].includes(s.phase) || (s.phase === 'waiting' && !!s.request));

const settled = (s) => !!s && s.installer && s.phase === 'idle';
export const canCheck = (s) => settled(s);
export const canInstall = (s) => settled(s) && !!s.available;
// Update now beside Install: only where the machine says it can be pressed, with a release ready,
// and nothing under way there but an update waiting (one waiting for a quiet moment is pressed
// again to confirm what is busy now).
export const canUpdateNow = (s) => !!s && s.installer && !!s.available && s.canUpdateNow && ['idle', 'waiting'].includes(s.phase);

const sentence = (s) => (!s ? '' : /[.!?]$/.test(s) ? s : `${s}.`);
// A line of its own, which a host's reason (written to follow a colon) starts.
const line = (s) => { const t = sentence(s); return t ? t[0].toUpperCase() + t.slice(1) : t; };

// Why Update now can't be pressed on a machine with an update ready, in place of its button
// (updateNowWhy). Creative's words (#1159, 6711605555). An unknown code says nothing.
export function updateNowReason(s) {
  if (!s?.installer || !s.available || canUpdateNow(s)) { return null; }
  switch (s.updateNowWhy) {
    case 'disabled': return 'Update now is off on this machine';
    case 'read-only': return 'View only';
    case 'old-app': return 'Update now needs a newer app there. Install still works.';
    case 'unavailable': return "The app there can't install updates now";
    case 'sign-in': return 'An agent sign-in is under way there';
    case 'busy': return 'Another update or restart is under way there';
    case 'restart-cap': {
      const at = clockTime(s.updateNowAt);
      return at ? `Update now is available again at ${at}` : 'Update now is available again later';
    }
    default: return null;
  }
}

// An Update now waiting for a quiet moment, by its waitingCode: Creative's fixed lines (#1185,
// 6723869235), never the machine's waiting text.
export const WAITING_WORDS = Object.freeze({
  'turn-running': 'Update ready \u00b7 waiting for the current turn to finish',
  'question-waiting': 'Update ready \u00b7 waiting for a question to be answered',
  'usage-check': 'Update ready \u00b7 waiting for a Codex usage check to finish',
  unknown: 'Update ready \u00b7 waiting until sessions here are quiet',
});

export const RESTARTING = 'Restarting to update\u2026';

// An Update now's waiting line: its code's, and the unknown one's for a code missing or new, never
// the machine's waiting text. null for anything else waiting.
function nowWaiting(s) {
  if (s?.phase !== 'waiting') { return null; }
  if (Object.hasOwn(WAITING_WORDS, s.waitingCode ?? '')) { return WAITING_WORDS[s.waitingCode]; }
  return s.requestNow ? WAITING_WORDS.unknown : null;
}

// Why an Update now didn't happen, from its code, as a whole line: "Not updated: …" (v1-update.md
// "What the person sees", with Creative's three on #1185). at: when Update now works again, for
// restart-cap. An unknown code is "the machine refused it", never what the machine wrote.
export function notUpdated(why, { at = null, failed = false } = {}) {
  if (failed) { return "Not updated: the update didn't take."; }
  switch (why) {
    case 'expired': return 'Not updated: not done within 15 minutes. Press Update now again.';
    case 'usage-check': return 'Not updated: a Codex usage check was still running';
    case 'release-changed': return 'Not updated: the update changed. Check for updates again.';
    case 'restart-cap': {
      const time = clockTime(at);
      return `Not updated: Update now has used its restarts for the last 6 hours; ${time ? `try after ${time}` : 'try again later'}.`;
    }
    default: break;
  }
  const reasons = {
    'not-quiet': 'a message was being delivered there the whole time',
    changed: 'something changed on the machine; try again',
    'rate-limited': 'too many presses just now; try again in a few minutes',
    busy: 'another update or restart is under way there',
    'sign-in': 'an agent sign-in is under way there',
    'announce-failed': "the bridge couldn't tell your devices it was restarting",
    'other-profile': 'another bridge profile is running there',
    disabled: 'Update now is off on this machine',
    'read-only': 'Update now is off on this machine',
    future: "the request didn't verify; try again",
    'bad-signature': "the request didn't verify; try again",
    'bad-args': "the request wasn't one this machine understands; try again",
    'not-person': "this device can't update that machine",
    'not-peer': "this device can't update that machine",
    'not-connected': "this device can't update that machine",
    'local-device': "this device can't update that machine",
    'needs-roster': 'this needs your approved devices first',
    unavailable: "the app there can't install it now",
    'old-app': "the app there can't install it now",
  };
  return `Not updated: ${Object.hasOwn(reasons, why) ? reasons[why] : 'the machine refused it'}.`;
}

// A press that never got an answer (the machine unreachable, or no key to sign with): this
// device's words, never an error's text.
export const NOT_SENT = "Not updated: the request didn't go through; try again.";

// What the machine's updater is doing, in a line.
export function updateLine(s, now = Date.now()) {
  if (!s) { return ''; }
  if (!s.installer) { return line(s.unavailable ?? 'its app is not running, so it cannot update now'); }
  // An Update now waiting says why from its code alone (#826).
  const nowLine = nowWaiting(s);
  if (nowLine) { return nowLine; }
  // "Needs you" (#800) is the host's own line, in whatever phase: an update waited past six hours
  // is never left to the log alone.
  if (s.waiting?.startsWith('Needs you')) { return sentence(s.waiting); }
  // So is the line of an update held back for a question waiting (Creative on #719, 6674096426):
  // its exact words.
  if (s.waiting?.startsWith('Update ready \u00b7 ')) { return s.waiting; }
  const next = s.available ?? 'the update';
  switch (s.phase) {
    case 'checking': return 'Checking for updates\u2026';
    // An Update now's own words (v1-update.md "While it goes"): never "Updating…" (#950).
    case 'downloading':
      if (s.requestNow) { return `Downloading ${next}${s.progress !== null ? ` \u00b7 ${s.progress}%` : ''}`; }
      return `Downloading ${next}${s.progress !== null ? ` (${s.progress}%)` : ''}\u2026`;
    case 'installing': return s.requestNow ? RESTARTING : `Installing ${next}: its app and host restart\u2026`;
    case 'waiting': return `${s.available ?? 'The update'} installs when it can: ${sentence(s.waiting ?? 'a session is busy')}`;
    default:
      if (s.available) { return `${s.available} is available (running ${s.current ?? 'an older release'}).`; }
      // No check has finished there yet (automatic updates off, or the first one failed): nothing
      // newer found is not the same as nothing newer.
      if (!s.checkedAt) { return `Running ${s.current ?? 'an unknown release'}; not checked for updates yet.`; }
      return `Up to date: ${s.current ?? 'unknown'}, checked ${ago(s.checkedAt, now)}.`;
  }
}

// How the last update asked for, or installed by itself, ended; null when none has. updateNow:
// it answered an Update now this device pressed; one with a code is an Update now's too. Those are
// worded from the outcome and code alone.
export function lastLine(s, now = Date.now(), { updateNow = false } = {}) {
  const l = s?.last;
  if (!l) { return null; }
  if (updateNow || l.code) {
    switch (l.outcome) {
      case 'updated': return `Updated to ${l.to ?? 'the new release'}.`;
      case 'current': return `Already up to date when asked ${ago(l.at, now)}.`;
      case 'refused': return notUpdated(l.code, { at: s.updateNowAt });
      default: return notUpdated(l.code, { failed: true });
    }
  }
  switch (l.outcome) {
    case 'updated': return `Updated from ${l.from ?? 'an older release'} to ${l.to ?? 'a newer one'} ${ago(l.at, now)}.`;
    case 'current': return `Already up to date when asked ${ago(l.at, now)}.`;
    case 'refused': return `Not updated: ${sentence(l.reason ?? 'refused')}`;
    default: return `The update failed: ${sentence(l.reason ?? 'no reason given')}`;
  }
}

// What Update now's confirmation says (v1-update.md "What the person sees", Creative's rules):
// each busy session by the name this device shows for it ("A session" for one it hasn't), its
// state in words, how many it left out, whether the bridge can't tell, the pause, and, while a
// question or approval waits or it can't tell, that one may have to be answered at the machine.
// nameOf(sessionId): the name, or null for a session this device doesn't have.
export const STATE_WORDS = Object.freeze({ working: 'Mid-turn', question: 'Question waiting', approval: 'Approval waiting' });
export const PAUSE_LINE = 'They pause while the bridge restarts, usually under a minute.';
export const UNKNOWN_LINE = "Some sessions there haven't reported yet.";
export const WARN_LINE = 'A question or approval waiting may have to be answered at the machine afterwards.';

export function nowSheet({ machine, busy, nameOf = () => null }) {
  const b = busy ?? CANT_TELL;
  const left = b.moreApprovals + b.moreOther;
  const asking = b.waiting > 0 || b.moreApprovals > 0 || b.sessions.some((s) => s.state !== 'working');
  return {
    title: `Update ${machine} now?`,
    sessions: b.sessions.map((s) => ({ id: s.id, name: (s.id && nameOf(s.id)) || 'A session', state: STATE_WORDS[s.state] })),
    more: left > 0 ? (b.moreApprovals > 0 ? `and ${left} more, ${b.moreApprovals} of them with an approval waiting` : `and ${left} more`) : null,
    unknown: b.unknown ? UNKNOWN_LINE : null,
    pause: PAUSE_LINE,
    // Until #954 lands (settled decision 8): err toward warning.
    warn: asking || b.unknown ? WARN_LINE : null,
    busy: b,
  };
}

// The name a session's row shows (a title, cleaned), or null when this device doesn't have it.
export const sessionName = (s) => (s ? plainText(s.title, 120) ?? 'Untitled session' : null);

// The polite announcement of an Update now's progress (Creative): once as each step starts, never
// at each percent. key: the step it said last; returns { key, text } with text null when nothing
// new is to be said.
export function nowAnnouncement(s, said = null) {
  const waitingLine = s?.installer ? nowWaiting(s) : null;
  const step = !s || !s.installer ? null
    : s.phase === 'installing' && s.requestNow ? 'restarting'
      : s.phase === 'downloading' && s.requestNow ? 'downloading'
        : waitingLine ? `waiting:${waitingLine}` : null;
  if (step === said) { return { key: said, text: null }; }
  const text = step === 'restarting' ? RESTARTING
    : step === 'downloading' ? `Downloading ${s.available ?? 'the update'}`
      : step ? waitingLine
        // The download finished into something said elsewhere (the outcome's line): said once.
        : said === 'downloading' ? 'Download finished' : null;
  return { key: step, text };
}

// Check, Install and Update now on each machine, and Check all. A press goes to the machine
// (fleet.call), which answers at once: accepted (its app takes it from there, and the machine's
// state says how it goes) or refused, and why, shown on the row until the next press; an Update now
// also confirm, when the machine is busier than the person was shown, which shows the confirmation
// again with what is busy now.
export class UpdateControl {
  #busy = new Set();
  #notes = new Map();
  #listeners = new Set();
  // Machines whose Update now confirmation is open.
  #asking = new Set();
  // The Update now presses this device sent and the machine took, so their outcome is worded as an
  // Update now's (lastLine). The last few only.
  #nowRequests = [];
  // Machines seen leaving for an Update now (installing, requestNow), with when their row was first
  // drawn offline and the timer that redraws it once RESTART_SHOWN_MS has passed.
  #leaving = new Map();
  // What each machine's announcement said last (nowAnnouncement).
  #said = new Map();
  // Each open confirmation's abort, by machine: account cleanup closes them, sending nothing.
  #confirming = new Map();
  // Counted up by clear(): a press whose confirmation was open across it is never sent.
  #generation = 0;

  // fleet: a FleetStatus (update(id), call(id, op, data), noteUpdate(id, state), watch(id)).
  // sign({ id, machineKey, release, accept, request }): an Update now's signed command for that
  // machine (transfer.js signUpdateNow), or null where this device can't sign one (no Update now).
  // session(hostId, sessionId): the session this device has, or null, for its name in the
  // confirmation. nameOf(id): a machine's name, for one restarting after it left.
  constructor({ fleet, newRequest = () => crypto.randomUUID(), sign = null, session = () => null, nameOf = () => null, now = () => Date.now(), restartMs = RESTART_SHOWN_MS }) {
    Object.assign(this, { fleet, newRequest, sign, session, nameOf, now, restartMs });
  }

  onChange(fn) { this.#listeners.add(fn); return () => this.#listeners.delete(fn); }

  #emit() { for (const fn of this.#listeners) { try { fn(); } catch (e) { console.error(e); } } }

  state(id) { return this.fleet.update(id); }

  // { busy, asking, note } for one machine's row.
  stateOf(id) { return { busy: this.#busy.has(id), asking: this.#asking.has(id), note: this.#notes.get(id) ?? null }; }

  // Whether this device can offer Update now at all (it has a key to sign with).
  get signs() { return typeof this.sign === 'function'; }

  // Whether the last outcome answers an Update now this device pressed.
  isUpdateNow(request) { return !!request && this.#nowRequests.includes(request); }

  // kind: 'check' or 'install'.
  async press(id, kind) {
    if (this.#busy.has(id) || this.#asking.has(id)) { return; }
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

  // Update now (#1159). On a quiet machine, one press; otherwise confirm(sheet, { signal }) shows
  // nowSheet's confirmation and resolves true for Update now, false for Cancel (which sends
  // nothing); signal aborts when the account is cleared or the machine forgotten, and the
  // confirmation then closes as Cancel. The press signs, for the release shown, what the
  // confirmation showed. A machine that answers confirm shows the confirmation again, with what it
  // says is busy now, whatever that is: never pressed again without the person. machine: the
  // machine's name, for the confirmation's title.
  async pressNow(id, { machine = 'this machine', confirm = null } = {}) {
    if (!this.signs || this.#busy.has(id) || this.#asking.has(id)) { return; }
    const s = this.fleet.update(id);
    if (!canUpdateNow(s)) { return; }
    const release = s.available;
    const generation = this.#generation;
    this.#notes.delete(id);
    let busy = s.busy ?? CANT_TELL;
    let ask = !quiet(busy);
    for (;;) {
      let accept = null;
      if (ask) {
        if (!confirm) { return; }
        const abort = new AbortController();
        this.#asking.add(id);
        this.#confirming.set(id, abort);
        this.#emit();
        let go = false;
        try {
          go = await confirm(nowSheet({ machine, busy, nameOf: (sessionId) => sessionName(this.session(id, sessionId)) }), { signal: abort.signal }) === true;
        } catch (e) {
          console.error(e);
        } finally {
          this.#asking.delete(id);
          if (this.#confirming.get(id) === abort) { this.#confirming.delete(id); }
          this.#emit();
        }
        if (!go || abort.signal.aborted || generation !== this.#generation) { return; }
        accept = acceptOf(busy);
      }
      const result = await this.#sendNow(id, release, accept, generation);
      if (result?.outcome !== 'confirm' || generation !== this.#generation) { return; }
      busy = result.state?.busy ?? this.fleet.update(id)?.busy ?? CANT_TELL;
      ask = true;
    }
  }

  async #sendNow(id, release, accept, generation) {
    this.#busy.add(id);
    this.#emit();
    // An answer that comes once the account has been cleared is nobody's to show.
    const current = () => generation === this.#generation;
    try {
      const before = this.fleet.update(id);
      const request = this.newRequest();
      // Signed once the channel to the machine is open, with the key its handshake proved.
      const make = async (machineKey) => ({ now: await this.sign({ id, machineKey, release, accept, request }) });
      const result = readUpdateCommandResult(await this.fleet.call(id, UPDATE.install, make));
      if (!current()) { return result; }
      if (result.state && this.fleet.update(id) === before) { this.fleet.noteUpdate(id, result.state); }
      if (result.outcome === 'accepted') {
        this.#nowRequests = [...this.#nowRequests.slice(-31), request];
        this.fleet.watch?.(id);
      } else if (result.outcome === 'refused') {
        this.#notes.set(id, notUpdated(result.code, { at: result.state?.updateNowAt ?? this.fleet.update(id)?.updateNowAt ?? null }));
      }
      return result;
    } catch (e) {
      console.error(e);
      if (current()) { this.#notes.set(id, NOT_SENT); }
      return null;
    } finally {
      this.#busy.delete(id);
      this.#emit();
    }
  }

  // Whether a machine reads "Restarting to update…" (#815's planned restart, until it is
  // negotiated): while it is online and its app is installing an Update now, and for
  // RESTART_SHOWN_MS after its row is first drawn offline that way; then Offline as any other.
  restarting(id, online) {
    const s = this.fleet.update(id);
    const was = this.#leaving.get(id);
    if (!(s?.phase === 'installing' && s.requestNow)) {
      if (was) { clearTimeout(was.timer); this.#leaving.delete(id); }
      return false;
    }
    if (online) {
      if (was?.timer) { clearTimeout(was.timer); }
      this.#leaving.set(id, { gone: null, timer: null });
      return true;
    }
    if (!was?.gone) {
      const timer = setTimeout(() => this.#emit(), this.restartMs + 50);
      timer.unref?.();
      this.#leaving.set(id, { gone: this.now(), timer });
      return true;
    }
    return this.now() - was.gone < this.restartMs;
  }

  // The machines seen leaving for an Update now, for a row of their own once offline.
  leavingIds() { return [...this.#leaving.keys()]; }

  // What to announce about a machine's Update now, once per step (nowAnnouncement), or null.
  announce(id) {
    const { key, text } = nowAnnouncement(this.fleet.update(id), this.#said.get(id) ?? null);
    if (key === null) { this.#said.delete(id); } else { this.#said.set(id, key); }
    return text;
  }

  // Every machine that can check now.
  checkAll(ids) {
    return Promise.all(ids.filter((id) => canCheck(this.state(id))).map((id) => this.press(id, 'check')));
  }

  forget(id) {
    this.#confirming.get(id)?.abort();
    this.#notes.delete(id);
    this.#said.delete(id);
    clearTimeout(this.#leaving.get(id)?.timer);
    this.#leaving.delete(id);
    this.#emit();
  }

  clear() {
    this.#generation += 1;
    for (const abort of this.#confirming.values()) { abort.abort(); }
    this.#confirming.clear();
    this.#notes.clear();
    this.#said.clear();
    for (const l of this.#leaving.values()) { clearTimeout(l.timer); }
    this.#leaving.clear();
    this.#nowRequests = [];
    this.#emit();
  }
}
