// What each machine last said it can launch (launch.options), kept so the Start a session page
// shows a form at once instead of waiting on the machine (#1527). It is a copy for display
// only: the host checks every launch against its own current options, so a stale form can at
// worst be refused, never act on something the machine does not offer.
//
// Nothing here reads a clock to decide to ask: a machine is asked on a data event (the page
// learning of it, its channel opening again, a launch outcome from it, a person's Reload), never
// on a timer (#1511). The age is only computed when a draw asks for it. Memory only: a page
// reload starts empty and fills again from the background asks.

export class LaunchOptionsCache {
  #entries = new Map();
  #queue = Promise.resolve();
  #now;
  #watchers = new Set();

  constructor({ now = () => Date.now() } = {}) { this.#now = now; }

  // { options, at, error, asking } for a machine, or null when it was never asked. options and
  // at are the last good answer and stay through a later failure; error is the last ask's
  // failure (null after an answer), asking whether an ask is on its way.
  get(hostId) {
    const e = this.#entries.get(hostId);
    return e ? { options: e.options, at: e.at, error: e.error, asking: !!e.pending } : null;
  }

  // fn(hostId) after an ask of a machine settles, whichever way.
  watch(fn) { this.#watchers.add(fn); return () => this.#watchers.delete(fn); }

  // Asks the machine now, or joins the ask already on its way. load() is launch.options.
  // fresh: the caller needs an answer to a question put after something happened (a launch
  // outcome, a reconnect), so an ask sent before it is not joined; it is superseded, and its
  // late answer is dropped rather than stored over the newer one.
  ask(hostId, load, { fresh = false } = {}) {
    const e = this.#entry(hostId);
    if (e.pending && !fresh && !e.fenced) { return e.pending; }
    e.fenced = false;
    const gen = ++e.gen;
    const pending = e.pending = Promise.resolve().then(load).then((options) => {
      if (gen === e.gen) {
        e.options = options;
        e.at = this.#now();
        e.error = null;
      }
      return options;
    }, (err) => {
      if (gen === e.gen) { e.error = err?.reason ?? err?.message ?? String(err); }
      throw err;
    }).finally(() => {
      if (e.pending === pending) { e.pending = null; }
      if (gen === e.gen) { for (const fn of this.#watchers) { fn(hostId); } }
    });
    // Nobody has to be waiting on a background ask for its failure to be handled.
    pending.catch(() => {});
    return pending;
  }

  // The machines that launch, as handed in on every draw: [{ hostId, load }] for those open now.
  // One never asked is asked in the background, one machine at a time so a loaded host or
  // relay is not hit by every machine's scan together; one that came back after a gap
  // (away(), then here again) is asked again. Machines not listed are marked away.
  sync(open) {
    const here = new Set(open.map((m) => m.hostId));
    for (const [id, e] of this.#entries) { if (!here.has(id)) { e.away = true; } }
    for (const { hostId, load } of open) {
      const e = this.#entry(hostId);
      if ((e.pending && !e.fenced) || e.queued) { continue; }
      if (e.asked && !e.away) { continue; }
      e.asked = true;
      e.away = false;
      e.queued = true;
      this.#queue = this.#queue.then(() => { e.queued = false; return this.ask(hostId, load).catch(() => {}); });
    }
  }

  // Its channel opened again, or something it said makes its choices suspect: the next sync asks,
  // and an ask still on its way (on the old channel) is not joined by the next one.
  stale(hostId) { const e = this.#entry(hostId); e.away = true; e.fenced = true; }

  #entry(hostId) {
    let e = this.#entries.get(hostId);
    if (!e) { this.#entries.set(hostId, e = { options: null, at: 0, error: null, pending: null, asked: false, away: false, queued: false, fenced: false, gen: 0 }); }
    return e;
  }
}
