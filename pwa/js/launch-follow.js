// What a machine is busy with, listed again once it can have ended, and a launch reported
// unconfirmed that turns out to have started (#896): the Windows app's LaunchViewModel.Late.cs
// (#891), for the Launch view (ui/launch-view.js). A launched agent asking whether to trust its
// folder keeps the machine busy until its session registers, or for two minutes at most
// (docs/protocol/v1-launch.md); the view used to keep saying so until it was opened again, and
// to keep saying the launch was not confirmed, with its prompt still in the box, after its
// session had appeared. Both invited a second press and a second session. Nothing new is asked
// of the machine: its listing, its sessions and the outcome (its session's id, or the agent and
// folder it started in) say all of it.
//
// Kept apart from the view, with its clock and timers handed in, so its rules are tested
// without a page.

export const BUSY_FOR_MS = 2 * 60000;
// Listed again a moment after the machine's own two minutes, and never sooner than this: a clock
// here ahead of the machine's would otherwise list again and again.
export const BUSY_SLACK_MS = 1000;
export const BUSY_AT_LEAST_MS = 5000;
// As long as the machine follows a launch whose agent asks about its folder (the host's
// TrustFollowFor): a start seen after that is not shown as this one's.
export const LATE_FOLLOW_FOR_MS = 30 * 60000;
// A launch that named no session is matched only to one that says it started no earlier than
// this before the launch was first sent: the machine's clock may be a little behind this one's,
// but a session started there before the launch, in the same folder, is not this launch's. A
// clock further behind only leaves the launch unconfirmed, as it was.
export const LATE_START_SLACK_MS = 2 * 60000;
// Nor to one that says it started more than this after now here: the machine's clock is then
// ahead by an amount not known, so how old the session is cannot be told, and the launch stays
// unconfirmed (#973).
export const LATE_AHEAD_MS = 30000;
// A session that looks like a launch's is taken as its own only once nothing else has looked
// like it for this long: a reconnect's list comes one session at a time, and two that match
// leave the launch unconfirmed, as a wrong jump is worse than none (#973).
export const LATE_SETTLE_MS = 1000;

// The kind of session each launcher registers: Agency's are Copilot's.
const KIND = Object.freeze({ agency: 'copilot', copilot: 'copilot', claude: 'claude', codex: 'codex' });

// The same folder, as a machine may write it: either slash, any case, no slash at the end.
export function sameFolder(a, b) {
  const norm = (p) => (typeof p === 'string' ? p.replace(/\//g, '\\').replace(/\\+$/, '').toLowerCase() : '');
  return norm(a) !== '' && norm(a) === norm(b);
}

// The same folder-trust question, though the machine may have asked it again under a new id
// since (#901): never the folder alone, as another launch into it asks there too.
export function sameQuestion(mine, q) {
  if (!mine || !q) { return false; }
  if (mine.id && mine.id === q.id) { return true; }
  return mine.askedAt != null && mine.folder === q.folder && mine.agent === q.agent && mine.askedAt === q.askedAt;
}

// The first message in the box is still the one a launch sent: a box of only spaces counts as
// empty both ways, as nothing is sent then.
export function samePrompt(box, sent) {
  const text = (s) => (s?.trim() ? s : null);
  return text(box) === text(sent);
}

export class LaunchFollow {
  #timer = null;
  // The newest listed question's two minutes were already up when the timer was last set: what
  // the machine is busy with may not be that question (a Bypass Permissions busy), so it is not
  // listed again every few seconds.
  #overdue = false;
  // The sessions the machine shown had when it said it was busy: { hostId, ids }. A new one may
  // be the one that kept it busy, now registered.
  #known = null;
  // One listing at a time for sessions that register while the machine is busy, and one more for
  // any that came during it.
  #relisting = false;
  #again = false;
  // By machine, its last launch, reported unconfirmed while its session was not listed there:
  // { request, session, at, question, match, get }. Session is its id, or null when the outcome
  // named none; match is then how its session is told: { kind, cwd, known, sent, found }, the
  // sessions listed and the time when it was first sent, and those seen since that may be its own.
  #late = new Map();
  // By machine, the launch on its way there: { request, at, known }, when it was first sent and
  // the ids the machine listed then. Kept through its resends: a session that registered before
  // a lost outcome came back may be its own (#973).
  #sent = new Map();
  // By machine, recent launches whose outcome named no session, so may yet start one of their
  // agent in their folder: [{ request, kind, cwd, at }]. Another of these like a launch followed
  // makes any match for it a guess, so it stays unconfirmed.
  #unnamed = new Map();
  // By machine, the sessions outcomes there named: none of them is another launch's.
  #named = new Map();
  // By machine, the wait before a match is taken: { request, timer }.
  #settle = new Map();
  // By machine, the last outcome looked at, so the same one is never followed twice.
  #seen = new Map();
  // While the machine shown is busy, when it is next listed: { hostId, at, load }. Kept while the
  // app is in the background, with no timer running (as fleet.js's probe), so coming back lists
  // it once if that time has passed, or waits for what is left.
  #due = null;
  // A listing a session asked for while the app was in the background, made on its return.
  #owed = null;

  // showing(hostId): the Launch view is showing that machine. relist(): lists the machine shown
  // again, a promise. loads(): how many listings were asked for so far. started(hostId, late):
  // a launch followed has started. doc: the page, whose visibility pauses the listing. signal:
  // aborted when its owner goes, taking the page's listener with it.
  constructor({ showing, relist, loads, started, now = () => Date.now(), setTimer = (fn, ms) => setTimeout(fn, ms), clearTimer = (t) => clearTimeout(t), doc = globalThis.document, signal }) {
    Object.assign(this, { showing, relist, loads, started, now, setTimer, clearTimer, doc });
    doc?.addEventListener?.('visibilitychange', () => (this.#hidden() ? this.#cancel() : this.#back()), signal ? { signal } : undefined);
  }

  #hidden() { return this.doc?.hidden === true; }

  #arm() {
    this.#cancel();
    const due = this.#due;
    if (!due || this.#hidden()) { return; }
    this.#timer = this.setTimer(() => {
      this.#timer = null;
      if (this.#hidden()) { return; }
      this.#due = null;
      if (this.showing(due.hostId) && due.load === this.loads()) { this.#relistBusy(due.hostId); }
    }, Math.max(0, due.at - this.now()));
  }

  // In front again: one listing if the busy can have ended (or a session asked for one), else the
  // wait goes on for what is left of it.
  #back() {
    const due = this.#due;
    const owed = this.#owed;
    this.#owed = null;
    if (owed && this.showing(owed.hostId)) {
      this.#due = null;
      this.#relistBusy(owed.hostId);
    } else if (due && due.at <= this.now()) {
      this.#due = null;
      if (this.showing(due.hostId) && due.load === this.loads()) { this.#relistBusy(due.hostId); }
    } else {
      this.#arm();
    }
  }

  // After each listing of the machine shown: what it lists now (null if nothing), and the ids of
  // its sessions. Returns how long until it is listed again, or null when it isn't busy.
  listed(hostId, options, sessionIds) {
    this.#cancel();
    // A listing asked for after a session asked for one is that one; one asked before it is not.
    if (this.#owed && this.loads() > this.#owed.load) { this.#owed = null; }
    if (!options?.busy) {
      this.#due = null;
      this.#known = null;
      this.#overdue = false;
      return null;
    }
    this.#known = { hostId, ids: new Set(sessionIds) };
    const asked = (options.trust ?? []).map((q) => Date.parse(q?.askedAt ?? '')).filter(Number.isFinite);
    // Bypass Permissions' busy names no question: two minutes is the most it lasts.
    const left = asked.length ? Math.max(...asked) + BUSY_FOR_MS - this.now() : BUSY_FOR_MS;
    // A question whose two minutes are already up here (a clock ahead of the machine's) is listed
    // again soon, not after another two; but once only, as a busy still there then is not that
    // question's.
    const overdue = asked.length > 0 && left <= 0;
    const most = BUSY_FOR_MS + BUSY_SLACK_MS;
    const wait = overdue && this.#overdue ? most : Math.min(Math.max(left + BUSY_SLACK_MS, BUSY_AT_LEAST_MS), most);
    this.#overdue = overdue;
    this.#due = { hostId, at: this.now() + wait, load: this.loads() };
    this.#arm();
    return wait;
  }

  // The view closed, or another machine picked: nothing is listed again for it.
  stop() {
    this.#cancel();
    this.#due = null;
    this.#owed = null;
    this.#known = null;
    this.#overdue = false;
  }

  // Everything tied to the signed-in account goes with it.
  clear() {
    this.stop();
    for (const hostId of [...this.#settle.keys()]) { this.#unsettle(hostId); }
    this.#late.clear();
    this.#seen.clear();
    this.#sent.clear();
    this.#unnamed.clear();
    this.#named.clear();
  }

  #cancel() {
    if (this.#timer) { this.clearTimer(this.#timer); }
    this.#timer = null;
  }

  // A launch's entry (launch-service.js), on any machine. listed(id): that machine lists that
  // session now; ids(): the ids of every session it lists now; get(id): one of them, or null.
  // True when it is followed.
  follow(hostId, entry, listed, ids = () => [], get = () => null) {
    if (!hostId || !entry) { return false; }
    // One launch followed per machine: a newer one there, however it goes, replaces it.
    if (this.#late.get(hostId)?.request !== entry.request) { this.#late.delete(hostId); this.#unsettle(hostId); }
    // Its first send: when it went, and what the machine listed then. Its resends keep both.
    if (entry.state === 'sending' && this.#sent.get(hostId)?.request !== entry.request) {
      this.#sent.set(hostId, { request: entry.request, at: this.now(), known: new Set(ids()) });
    }
    if (entry.state !== 'done' || this.#seen.get(hostId) === entry.request) { return false; }
    this.#seen.set(hostId, entry.request);
    const sent = this.#sent.get(hostId)?.request === entry.request ? this.#sent.get(hostId) : { at: this.now(), known: new Set(ids()) };
    const session = entry.detail?.session ?? null;
    if (session) { this.#namedOn(hostId).add(session); }
    // An outcome that named no session (a Codex has none until it registers, as the host follows
    // it by its process): its session is the one new one of its agent, in the folder it was
    // started in. Never a Copilot's or Agency's, whose registering proves nothing (see session()),
    // nor a resumed or transferred one, which is not new. Its question, when it met one, still
    // ties a Trust answered here to it (answered()).
    const launched = entry.detail?.launched;
    const kind = KIND[launched?.agent];
    const guessed = !session && kind && kind !== 'copilot' && launched.cwd && !launched.resume && !launched.transfer;
    if (guessed && (entry.outcome === 'started' || entry.outcome === 'unconfirmed')) {
      this.#unnamed.set(hostId, [...this.#recent(hostId), { request: entry.request, kind, cwd: launched.cwd, at: sent.at, question: entry.detail?.trust ?? null }]);
    }
    if (entry.outcome !== 'unconfirmed') { return false; }
    const question = entry.detail?.trust ?? null;
    const late = { request: entry.request, session, at: this.now(), question, match: null, get };
    if (session && listed(session)) {
      // Listed by the time its outcome came (one lost and sent again): started, by the machine's
      // rule (session()), unless it is a Copilot's or ending.
      const s = get(session);
      if (!s || s.agent === 'copilot' || s.status === 'ending') { return false; }
      this.#late.set(hostId, late);
      this.#settleSoon(hostId, late);
      return true;
    }
    late.match = guessed ? { kind, cwd: launched.cwd, known: sent.known, sent: sent.at, found: new Map() } : null;
    if (!session && !late.match && !question) { return false; }
    this.#late.set(hostId, late);
    // One that registered between its first send and its outcome may be its own.
    if (late.match) {
      for (const id of ids()) { this.#candidate(hostId, late, get(id)); }
      if (late.match.found.size) { this.#settleSoon(hostId, late); }
    }
    return true;
  }

  #namedOn(hostId) {
    if (!this.#named.has(hostId)) { this.#named.set(hostId, new Set()); }
    return this.#named.get(hostId);
  }

  // The machine's launches that named no session and may still start one.
  #recent(hostId) { return (this.#unnamed.get(hostId) ?? []).filter((o) => this.now() - o.at <= LATE_FOLLOW_FOR_MS); }

  // Whether a session may be the session of a launch that named none: new since it was first
  // sent, of its agent, in its folder, not another launch's, not ending, and saying it started no
  // earlier than LATE_START_SLACK_MS before it was sent nor later than LATE_AHEAD_MS after now.
  // One that says nothing of when it started could be any.
  #candidate(hostId, late, s) {
    const m = late.match;
    if (!m || !s?.id) { return; }
    const started = Date.parse(s.startedAt ?? '');
    if (m.known.has(s.id) || this.#named.get(hostId)?.has(s.id) || s.agent !== m.kind || !sameFolder(s.cwd, m.cwd)) { return; }
    if (s.status === 'ending' || !Number.isFinite(started) || started < m.sent - LATE_START_SLACK_MS || started > this.now() + LATE_AHEAD_MS) { m.found.delete(s.id); return; }
    m.found.set(s.id, s);
  }

  #current(late) { return this.now() - late.at <= LATE_FOLLOW_FOR_MS; }

  // Taken only after LATE_SETTLE_MS with nothing more like it: each one that looks like it starts
  // the wait again.
  #settleSoon(hostId, late) {
    this.#unsettle(hostId);
    const timer = this.setTimer(() => { this.#settle.delete(hostId); this.#decide(hostId, late.request); }, LATE_SETTLE_MS);
    this.#settle.set(hostId, { request: late.request, timer });
  }

  #unsettle(hostId) {
    const wait = this.#settle.get(hostId);
    if (wait) { this.clearTimer(wait.timer); }
    this.#settle.delete(hostId);
  }

  // The launch followed has started when there is no doubt which session is its own: the one its
  // outcome named, or the only one that may be its own while no other launch of its agent into its
  // folder may still start one. A doubt leaves it unconfirmed, followed only for its question.
  #decide(hostId, request) {
    const late = this.#late.get(hostId);
    if (late?.request !== request) { return; }
    if (late.session) {
      const s = late.get(late.session);
      if (s && s.agent !== 'copilot' && s.status !== 'ending' && this.#start(hostId, late, s.id)) { this.#relistOrOwe(hostId); }
      return;
    }
    const m = late.match;
    if (!m || m.found.size === 0) { return; }
    const others = this.#recent(hostId).some((o) => o.request !== request && o.kind === m.kind && sameFolder(o.cwd, m.cwd));
    if (m.found.size > 1 || others) {
      late.match = null;
      if (!late.question) { this.#late.delete(hostId); }
      return;
    }
    const [id] = m.found.keys();
    const s = late.get(id) ?? m.found.get(id);
    if (s.status !== 'ending' && this.#start(hostId, late, id)) { this.#relistOrOwe(hostId); }
  }

  // True when the machine shown is to be listed again: its question, and the busy it kept, are gone.
  #start(hostId, late, session) {
    this.#late.delete(hostId);
    this.#unnamed.set(hostId, this.#recent(hostId).filter((o) => o.request !== late.request));
    if (!this.#current(late)) { return false; }
    this.started(hostId, { request: late.request, session, at: late.at, question: late.question });
    return this.showing(hostId);
  }

  #relistOrOwe(hostId) {
    if (this.#hidden()) { this.#owed = { hostId, load: this.loads() }; } else { this.#relistBusy(hostId); }
  }

  // A session registered or changed on a machine (readSession's shape).
  session(hostId, s) {
    if (!s?.id) { return; }
    let relist = false;
    const late = this.#late.get(hostId);
    // Its appearing is its start by the machine's own rule (the host's LaunchService: a registered
    // session has started, unless its agent registers before its window is ready). A Copilot or
    // Agency does, so its appearing proves nothing, and only a Trust answered here that starts it
    // says it started. Nor does one ending, or one seen after LATE_FOLLOW_FOR_MS.
    if (late?.session && late.session === s.id && s.status !== 'ending') {
      this.#unsettle(hostId);
      if (s.agent === 'copilot') { this.#late.delete(hostId); } else { relist = this.#start(hostId, late, s.id); }
    } else if (late?.match) {
      const had = late.match.found.has(s.id);
      this.#candidate(hostId, late, s);
      if (late.match.found.has(s.id) && !had) { this.#settleSoon(hostId, late); }
    }
    const known = this.#known;
    if (known?.hostId === hostId && this.showing(hostId) && !known.ids.has(s.id)) {
      known.ids.add(s.id);
      relist = true;
    }
    if (relist) { this.#relistOrOwe(hostId); }
  }
  // A folder-trust question on that machine answered here, with its outcome: the launch followed
  // that met it, when Trust started it; null otherwise. Either way, once started or declined, it
  // is no longer followed, nor may it still start a session that a later launch's could be
  // mistaken for, though a newer launch there is the one followed now.
  answered(hostId, question, outcome) {
    if (outcome !== 'started' && outcome !== 'declined') { return null; }
    this.#unnamed.set(hostId, this.#recent(hostId).filter((o) => !sameQuestion(o.question, question)));
    const late = this.#late.get(hostId);
    if (!late || !sameQuestion(late.question, question) || !this.#current(late)) { return null; }
    this.#late.delete(hostId);
    return outcome === 'started' ? { request: late.request, session: late.session, at: late.at, question: late.question } : null;
  }

  #relistBusy(hostId) {
    if (this.#relisting) { this.#again = true; return; }
    this.#relisting = true;
    let listing;
    try { listing = Promise.resolve(this.relist()); } catch { listing = Promise.resolve(); }
    listing.catch(() => {}).finally(() => {
      this.#relisting = false;
      if (this.#again) {
        this.#again = false;
        if (this.#hidden()) { this.#owed = { hostId, load: this.loads() }; } else if (this.showing(hostId)) { this.#relistBusy(hostId); }
      }
    });
  }
}
