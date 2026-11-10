// A session's conversation as the host's history pages and live activity give it (#292,
// docs/protocol/v1-history.md, ui.md §6.4 H0-H8): the turns held per session, keyed by turn id, so a
// turn loaded twice is held once. The data layer only: nothing here draws; session-card.js draws
// the turns before the latest exchange from it (ui/history-view.js).
//
//   open()         the newest page (session.history without a cursor). Asked again later it
//                  merges by id: each turn it holds is replaced in place, each it does not is
//                  put where the page puts it. It is how a client gets a finished turn whole.
//   loadEarlier()  the page before the oldest turn held, by the cursor the host gave with it.
//   live()         the session's activity merged by turn id (activity.turn, responseTurn).
//   watch()        calls fn(snapshot, change) whenever the session's history changes.
//
// What a snapshot says (get()):
//   phase     'idle' (nothing asked), 'loading' (the newest page is on its way), 'ready', 'failed'
//             (the newest page could not be had, with nothing held to show), 'unavailable' (the host
//             has no history for the session, or offers none: `unsupported`), 'missing' (the host
//             no longer lists the session).
//   turns     oldest first, as drawn. A turn is the host's (id, at, prompt, parts, partsMore,
//             truncated, unreadable: history.js), with `live` once activity has said anything of
//             it, and `provisional` while only activity has: its prompt is activity's, its `at` is
//             null and its parts are empty until a page replaces it. Turn objects are never
//             changed once given; a change is a new object, so a view can compare by identity.
//   more      an earlier page can be loaded. `start` says where the history begins, once it does
//             ({ at } the session's first record, { limited } the host serves no more).
//   loading   'newest' or 'earlier' while that page is on its way; error: { on, reason, fromHost }
//             after one that failed, kept until the next one is asked for.
//   atLatest  the newest turns are held. False after the newest end was let go to keep within the
//             limit (H6); open() brings them back.
//   latest    activity.turn, the id of the session's latest turn: null from a host that says none,
//             and then the live exchange is drawn after the history held, as v1-history.md says.
//   partial   a held turn is unreadable or cut short, or the history ends where the host's reading
//             gave out.
import { mainVoice } from './attribution.js';
import { HISTORY } from './history.js';

// Pages one walk back may ask for after a stale cursor, before it leaves the rest to the next
// loadEarlier: a page holds at least one turn, and a host serves 500.
const MAX_WALK = 100;

const same = (a, b) => JSON.stringify(a) === JSON.stringify(b);
const adopt = (page, held) => (held.live ? { ...page, live: held.live } : page);

export class HistoryStore {
  #entries = new Map();
  #listeners = new Map();
  #any = new Set();

  // current(hostId): { client } for the host if it is connected, else null. keep: turns held per
  // session, about (a page is let go whole at the old end, so a little more may be held). limit:
  // turns asked for a page, 1 to 50; none leaves it to the host (20).
  // first: turns the newest page asks for when limit is none.
  constructor({ current, keep = HISTORY.kept, limit = null, first = HISTORY.firstTurns }) {
    Object.assign(this, { current, keep, limit, first });
  }

  static key(hostId, sessionId) { return JSON.stringify([hostId, sessionId]); }

  get(hostId, sessionId) { return this.#snapshot(this.#entries.get(HistoryStore.key(hostId, sessionId)) ?? this.#blank(hostId, sessionId)); }

  watch(hostId, sessionId, fn) {
    const key = HistoryStore.key(hostId, sessionId);
    if (!this.#listeners.has(key)) { this.#listeners.set(key, new Set()); }
    this.#listeners.get(key).add(fn);
    return () => this.#listeners.get(key)?.delete(fn);
  }

  // fn({ hostId, sessionId, snapshot, change }) for every session.
  onAny(fn) { this.#any.add(fn); return () => this.#any.delete(fn); }

  // Lets a session's turns go, as when it ends or the page leaves it; whatever is on its way is
  // not applied.
  forget(hostId, sessionId) {
    const key = HistoryStore.key(hostId, sessionId);
    const e = this.#entries.get(key);
    if (e) { e.generation++; this.#entries.delete(key); }
  }

  // The newest page, merged into what is held. session (as the sessions store has it) is merged
  // first, so its latest turn is there before the page lands. Resolves to the snapshot.
  open(hostId, sessionId, { session = null } = {}) {
    const e = this.#entry(hostId, sessionId);
    if (session) { this.live(hostId, session); }
    return this.#run(e, 'newest', (gen) => this.#newest(e, gen));
  }

  // The page before the oldest turn held. Nothing is asked when there is none to ask for.
  loadEarlier(hostId, sessionId) {
    const e = this.#entry(hostId, sessionId);
    return this.#run(e, 'earlier', (gen) => this.#earlier(e, gen));
  }

  // The session's activity, merged: by the id of its latest turn, never between turns held (the
  // host never starts a turn before one it has served). The reply goes to the latest turn only when
  // responseTurn says it is that turn's; otherwise it is an earlier turn's, and goes there if held.
  // Does nothing for a session no one has opened.
  live(hostId, session) {
    const e = this.#entries.get(HistoryStore.key(hostId, session?.id));
    if (!e || !session.activity) { return; }
    const before = e.turns;
    const latest = session.activity.turn ?? null;
    let changed = latest !== e.latest;
    const turns = e.turns.slice();
    // The turn that was latest keeps what it was drawn with, but runs no tool now, whether the next
    // turn is already held or activity names none (a record the host could not read).
    if (e.last && e.last !== latest) { changed = this.#stopTool(turns, e.last) || changed; }
    if (latest) {
      changed = this.#merge(e, turns, session, latest) || changed;
      e.last = latest;
    }
    if (!changed) { return; }
    e.latest = latest;
    e.turns = turns;
    if (turns.length > before.length) { this.#trim(e, 'old'); }
    this.#emit(e, 'live', before);
  }

  // ---- the work ----

  #blank(hostId, sessionId) {
    return {
      hostId, sessionId, turns: [], cursors: new Map(), before: null, start: null, endPartial: false, phase: 'idle', why: null,
      unsupported: false, loading: null, error: null, atLatest: true, latest: null, last: null, recovering: null, seen: false,
      generation: 0, tail: Promise.resolve(), queued: {}, running: {}, revision: 0,
    };
  }

  #entry(hostId, sessionId) {
    const key = HistoryStore.key(hostId, sessionId);
    if (!this.#entries.has(key)) { this.#entries.set(key, this.#blank(hostId, sessionId)); }
    return this.#entries.get(key);
  }

  // One page at a time per session: a page is merged into what the one before it left. A call that
  // comes before the one ahead of it has started is that one. An earlier page asked for while one
  // is on its way is that one too: the cursor is the same until it lands. A newest page is asked
  // again, since a turn may have ended after the first was sent. A page asked for before the
  // session was forgotten never goes (a task is given the generation it was asked in).
  #run(e, kind, task) {
    if (e.queued[kind]) { return e.queued[kind]; }
    if (kind === 'earlier' && e.running.earlier) { return e.running.earlier; }
    const gen = e.generation;
    const run = e.tail.then(() => {
      if (e.queued[kind] === run) { delete e.queued[kind]; }
      if (gen !== e.generation) { return undefined; }
      e.running[kind] = run;
      return task(gen);
    }).then(() => this.#snapshot(e));
    e.tail = run.catch(() => {});
    const done = () => { if (e.running[kind] === run) { delete e.running[kind]; } };
    run.then(done, done);
    e.queued[kind] = run;
    return run;
  }

  // The client to ask, or null with the session's state said: no channel, or one whose host does
  // not offer history, which sends no session.history (v1-history.md, Capability).
  #client(e, on) {
    const live = this.current(e.hostId);
    const client = live?.client;
    if (!client || client.closed) {
      this.#fail(e, on, 'not connected', false);
      return null;
    }
    if (!client.capabilities?.includes(HISTORY.capability)) {
      Object.assign(e, { phase: 'unavailable', unsupported: true, why: null, loading: null, error: null });
      this.#emit(e, 'state');
      return null;
    }
    e.unsupported = false;
    return client;
  }

  #ask(client, e, cursor) {
    // The page with no cursor is the newest, and is kept small (HISTORY.firstTurns); `limit`, when
    // given, sets every page's size.
    const limit = this.limit ?? (cursor ? null : this.first);
    return client.history(e.sessionId, { ...(cursor ? { before: cursor } : {}), ...(limit ? { limit } : {}) });
  }

  #fail(e, on, reason, fromHost) {
    e.loading = null;
    e.error = { on, reason, fromHost };
    if (!e.seen && e.phase !== 'unavailable' && e.phase !== 'missing') { e.phase = 'failed'; }
    this.#emit(e, 'state');
  }

  async #newest(e, gen) {
    const client = this.#client(e, 'newest');
    if (!client) { return; }
    e.loading = 'newest';
    e.error = null;
    if (!e.seen && e.phase !== 'unavailable' && e.phase !== 'missing') { e.phase = 'loading'; }
    this.#emit(e, 'state');
    let result;
    try { result = await this.#ask(client, e, null); } catch (err) {
      if (gen === e.generation) { this.#fail(e, 'newest', err.reason ?? err.message, err.fromHost === true); }
      return;
    }
    if (gen !== e.generation) { return; }
    if (result.stale) { this.#fail(e, 'newest', 'the machine called a request with no cursor stale', true); return; }
    this.#land(e, result, 'newest');
  }

  async #earlier(e, gen) {
    if (e.before === null) { return; }
    const client = this.#client(e, 'earlier');
    if (!client) { return; }
    const first = e.turns;
    e.loading = 'earlier';
    e.error = null;
    this.#emit(e, 'state');
    let cursor = e.before;
    // Pages walked back from the newest after a stale cursor, newest first, until one shares a turn
    // with what is held (or the host serves nothing older): held turns are never dropped for a page
    // that is not known to follow them.
    const pending = [];
    let collecting = false;
    for (let walked = 0; walked < MAX_WALK; walked++) {
      let result;
      try { result = await this.#ask(client, e, cursor); } catch (err) {
        if (gen === e.generation) { this.#fail(e, 'earlier', err.reason ?? err.message, err.fromHost === true); }
        return;
      }
      if (gen !== e.generation) { return; }
      if (result.stale) {
        // What is drawn stays. Ask again without the cursor and merge by id: the turn held at the
        // old end is where the walk back from the newest page is done.
        if (cursor === null) { this.#fail(e, 'earlier', 'the machine called a request with no cursor stale', true); return; }
        e.recovering ??= e.turns[0]?.id ?? null;
        e.cursors.clear();
        cursor = null;
        collecting = true;
        pending.length = 0;
        continue;
      }
      if (!result.turns) { this.#land(e, result, 'earlier'); return; }
      let oldest;
      if (collecting) {
        pending.push(result);
        const reached = result.turns.some((t) => e.turns.some((h) => h.id === t.id && !h.provisional));
        if (!reached && result.before && walked + 1 < MAX_WALK) { cursor = result.before; continue; }
        oldest = this.#join(e, pending, reached);
        collecting = false;
        // Nothing held is within reach: what the host serves now takes its place.
        if (!reached) { e.recovering = null; break; }
      } else {
        oldest = this.#page(e, result, 'earlier');
      }
      cursor = result.before;
      if (!e.recovering) { break; }
      if (e.turns[0]?.id !== e.recovering) { e.recovering = null; break; }
      if (oldest) { e.recovering = null; break; }
      if (!cursor) {
        // The host serves nothing older, and the oldest turn held is older than anything it serves.
        Object.assign(e, { recovering: null, before: null, start: { at: null, limited: true }, endPartial: false });
        break;
      }
      // Still walking back to the turn held: the cursor to go on from.
      e.before = cursor;
    }
    e.loading = null;
    this.#emit(e, 'earlier', first);
  }

  // A result that is not a page, or a page.
  #land(e, result, kind) {
    const before = e.turns;
    if (result.missing) {
      Object.assign(e, { loading: null, phase: 'missing' });
      this.#emit(e, 'state');
    } else if (result.unavailable !== undefined) {
      Object.assign(e, { loading: null, phase: 'unavailable', why: result.unavailable, unsupported: false });
      this.#emit(e, 'state');
    } else {
      this.#page(e, result, kind);
      Object.assign(e, { loading: null, seen: true, phase: 'ready', why: null, unsupported: false, error: null });
      this.#emit(e, kind, before);
    }
  }

  // Merges a page into the turns held. Earlier pages go above them. A newest page puts each turn
  // where it belongs by the turns it shares with what is held; one that shares none is not known
  // to follow it, and takes its place (but for turns only activity has said, which are the newest).
  // Returns whether the page's own cursor (or its start) is the held turns' now.
  #page(e, page, kind) {
    const P = page.turns;
    if (P[0] && page.before) { e.cursors.set(P[0].id, page.before); }
    let turns;
    if (kind === 'earlier') {
      turns = e.turns.slice();
      const fresh = [];
      for (const t of P) {
        const at = turns.findIndex((h) => h.id === t.id);
        if (at < 0) { fresh.push(t); } else { turns[at] = adopt(t, turns[at]); }
      }
      turns = [...fresh, ...turns];
    } else {
      turns = this.#newestMerge(e, P);
      e.atLatest = true;
    }
    e.turns = turns;
    let oldest = turns[0]?.id === P[0]?.id;
    if (!oldest && P.length === 0 && (kind === 'earlier' || !turns.some((t) => !t.provisional))) {
      // A page with no turns: what is held is all there is before it (a host that ran out of
      // reading before it found a prompt says start.limited here), or nothing was held but activity.
      oldest = true;
    }
    if (oldest) {
      Object.assign(e, { before: page.before, start: page.before ? null : page.start, endPartial: !page.before && page.partial });
    }
    this.#trim(e, kind === 'earlier' ? 'new' : 'old');
    return oldest;
  }

  // The turn only activity has said, when it is the session's latest and the page does not have
  // it: it is the newest, so it stays after what a page takes the place of the held turns with.
  // Any other such turn is an older one the page has gone past, and goes.
  #tail(e, ids) {
    const last = e.turns.at(-1);
    return last?.provisional && last.id === e.latest && !ids.has(last.id) ? [last] : [];
  }

  // The pages walked back from the newest (oldest first once reversed) joined to the turns held
  // when they share one (reached): the older held turns stay below them, each held turn they hold
  // too is replaced in place, and the newest turn only activity has said stays after. Otherwise they
  // are all there is. Returns whether the oldest page's cursor is the held turns' now.
  #join(e, pending, reached) {
    const pages = pending.slice().reverse();
    const seen = new Set();
    // A turn two of the pages share (the host's cursors shifted between asks) is held once.
    const all = pages.flatMap((p) => p.turns).filter((t) => !seen.has(t.id) && seen.add(t.id));
    for (const p of pages) { if (p.turns[0] && p.before) { e.cursors.set(p.turns[0].id, p.before); } }
    const ids = new Set(all.map((t) => t.id));
    const held = new Map(e.turns.map((t) => [t.id, t]));
    const firstHeld = reached ? e.turns.findIndex((h) => ids.has(h.id) && !h.provisional) : -1;
    const older = firstHeld > 0 ? e.turns.slice(0, firstHeld) : [];
    e.turns = [...older, ...all.map((t) => (held.has(t.id) ? adopt(t, held.get(t.id)) : t)), ...this.#tail(e, ids)];
    e.atLatest = true;
    const oldest = older.length === 0;
    if (oldest) {
      const first = pages[0];
      Object.assign(e, { before: first.before, start: first.before ? null : first.start, endPartial: !first.before && first.partial });
    }
    this.#trim(e, 'old');
    return oldest;
  }

  #newestMerge(e, P) {
    const held = e.turns;
    if (!held.length) { return P.slice(); }
    const out = held.slice();
    const at = (id) => out.findIndex((t) => t.id === id);
    // Turns only activity has said are not turns the page is known to follow: a page that shares
    // only those with what is held takes its place.
    if (!P.some((t) => out.some((h) => h.id === t.id && !h.provisional))) {
      const ids = new Set(P.map((t) => t.id));
      return [...P.map((t) => { const h = held.find((x) => x.id === t.id); return h ? adopt(t, h) : t; }), ...this.#tail(e, ids)];
    }
    let last = -1;
    P.forEach((t, i) => {
      const pos = at(t.id);
      if (pos >= 0) { out[pos] = adopt(t, out[pos]); last = pos; return; }
      if (last < 0) { last = at(P.slice(i + 1).find((x) => at(x.id) >= 0).id); out.splice(last, 0, t); return; }
      out.splice(last + 1, 0, t);
      last += 1;
    });
    return out;
  }

  // Keeps the held turns to about `keep`, letting go of the end farthest from where the turns
  // were added (H6): at the old end when turns came in at the new, in whole pages, to one whose
  // cursor the host gave, so that earlier ones can be loaded again; at the new end when earlier
  // ones came in, which then are no longer all there (atLatest).
  #trim(e, side) {
    const over = e.turns.length - this.keep;
    if (over <= 0) { return; }
    if (side === 'new') {
      e.turns = e.turns.slice(0, this.keep);
      e.atLatest = false;
      return;
    }
    let at = over;
    while (at < e.turns.length - 1 && !e.cursors.has(e.turns[at].id)) { at++; }
    if (e.cursors.has(e.turns[at].id)) {
      e.turns = e.turns.slice(at);
      e.before = e.cursors.get(e.turns[0].id);
      e.start = null;
    } else {
      // No page boundary to cut at (so no cursor to load what goes again): the limit holds all the
      // same, and the history reads as ending where it is cut.
      e.turns = e.turns.slice(over);
      Object.assign(e, { before: null, start: { at: null, limited: true } });
    }
    Object.assign(e, { endPartial: false, recovering: null });
    const held = new Set(e.turns.map((t) => t.id));
    for (const id of e.cursors.keys()) { if (!held.has(id)) { e.cursors.delete(id); } }
  }

  #stopTool(turns, id) {
    const at = turns.findIndex((t) => t.id === id);
    if (at < 0 || !turns[at].live?.currentTool) { return false; }
    turns[at] = { ...turns[at], live: { ...turns[at].live, currentTool: null } };
    return true;
  }

  // Puts activity's latest turn, and its reply, into `turns`. Returns whether anything changed.
  #merge(e, turns, session, latest) {
    const a = session.activity;
    const voice = mainVoice(session);
    const at = (id) => turns.findIndex((t) => t.id === id);
    let changed = false;
    let i = at(latest);
    if (i < 0) {
      // The new turn goes after every turn held, when they are the newest ones.
      if (!e.atLatest) { return changed; }
      turns.push({
        id: latest, at: null, provisional: true, parts: [], partsMore: 0, truncated: false, unreadable: false,
        prompt: a.userMessage ? { text: a.userMessage, truncated: false, from: a.userMessageFrom ?? 'person', label: a.userMessageLabel ?? null } : null,
      });
      i = turns.length - 1;
      changed = true;
    }
    const replied = a.responseTurn === latest;
    const next = { ...turns[i].live, response: replied ? voice.response : (turns[i].live?.response ?? null), responseTruncated: replied ? voice.responseTruncated : (turns[i].live?.responseTruncated ?? false), reasoning: voice.reasoning, currentTool: a.currentTool ?? null };
    if (!same(next, turns[i].live)) { turns[i] = { ...turns[i], live: next }; changed = true; }
    // A reply that is an earlier turn's is drawn there, never under the new prompt.
    const of = a.responseTurn && a.responseTurn !== latest ? at(a.responseTurn) : -1;
    if (of >= 0) {
      const reply = { reasoning: null, currentTool: null, ...turns[of].live, response: voice.response, responseTruncated: voice.responseTruncated };
      if (!same(reply, turns[of].live)) { turns[of] = { ...turns[of], live: reply }; changed = true; }
    }
    return changed;
  }

  // ---- what it tells ----

  #snapshot(e) {
    return {
      hostId: e.hostId,
      sessionId: e.sessionId,
      phase: e.phase,
      loading: e.loading,
      error: e.error,
      turns: e.turns.slice(),
      more: e.before !== null,
      start: e.start,
      partial: e.endPartial || e.turns.some((t) => t.unreadable || t.truncated),
      why: e.why,
      unsupported: e.unsupported,
      atLatest: e.atLatest,
      latest: e.latest,
      revision: e.revision,
    };
  }

  // change: { kind, above, below }: what moved, and how many turns came in above the first turn
  // held before and below the last, which a view keeping its place needs to know. above and below
  // are 0 when what was held before is not all there now.
  #emit(e, kind, before = e.turns) {
    e.revision++;
    const ids = e.turns.map((t) => t.id);
    const [first, last] = [before[0]?.id, before.at(-1)?.id];
    const [from, to] = [ids.indexOf(first), ids.indexOf(last)];
    const change = { kind, above: from > 0 ? from : 0, below: to >= 0 && to < ids.length - 1 ? ids.length - 1 - to : 0 };
    const snapshot = this.#snapshot(e);
    const detail = { hostId: e.hostId, sessionId: e.sessionId, snapshot, change };
    for (const fn of this.#listeners.get(HistoryStore.key(e.hostId, e.sessionId)) ?? []) {
      try { fn(snapshot, change); } catch (err) { console.error(err); }
    }
    for (const fn of this.#any) {
      try { fn(detail); } catch (err) { console.error(err); }
    }
  }
}
