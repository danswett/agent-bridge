// What the person is reading in this page (#1190): the session open while the page is in sight.
// Owner report: push notifications arrived after the messages were already read in the app. So
// the machines are told (sessions.focus: a device on screen is not sent `finished` for a turn it
// sees), and the notices already shown for that session are taken down, which also covers a push
// that was on its way when the session opened. The service worker is not involved: it must
// still show every push it gets, or iOS revokes the app's notifications (sw.js).
//
// The owner rule on #1190: while a person is actively using the app, no push at all. So the
// focus also carries the age of the last input (idleMs, read as it is sent), and is said again
// when the person becomes active, and about every keepalive ms while input goes on, so a host's
// two-minute window stays fresh. Nothing is sent while idle or hidden; a host that hears nothing
// treats the device as not active and sends the push.
//
// hosts: HostSession (setFocus, refreshFocus, sessions.onChange). close(hostId, sessionId,
// question): takes down that session's shown notices that are read: all but those of questions
// other than `question`. A change to the open session is followed by a close pass after delayMs,
// and by later passes (laterMs, after the last change): the host sends a question's push 2 s
// after the question and a finished turn's 3 s after the turn, and the push service adds a
// little more, so one look a moment after the change would find nothing to close and leave the
// notice shown while it is read. At most one timer for each pass; the later ones restart with each
// change, the first does not, so a session that goes on changing is still looked at.
export class Reading {
  #hosts;
  #close;
  #doc;
  #delayMs;
  #laterMs;
  #keepaliveMs;
  #now;
  #open = null;
  #passes = [];
  #alive = null;
  #lastInput = null;
  #stopped = false;
  #off = [];

  constructor({ hosts, close, doc = globalThis.document ?? null, win = globalThis, delayMs = 1000, laterMs = [4000, 10000], keepaliveMs = 60000, now = () => Date.now() }) {
    this.#hosts = hosts;
    this.#close = close;
    this.#doc = doc;
    this.#delayMs = delayMs;
    this.#laterMs = laterMs;
    this.#keepaliveMs = keepaliveMs;
    this.#now = now;
    this.#listen(doc, 'visibilitychange', () => this.#apply());
    this.#listen(win, 'pageshow', () => this.#apply());
    // Passive, and cheap: a timestamp write, and only once in INPUT_GAP_MS of a stream of moves.
    for (const type of ['pointerdown', 'pointermove', 'keydown', 'touchstart', 'wheel']) {
      this.#listen(win, type, () => this.#input(), { passive: true, capture: true });
    }
    this.#off.push(hosts.sessions.onChange((c) => {
      if (c?.type === 'upsert' && this.#reading(c.hostId, c.id)) { this.#watch(); }
    }));
    this.#apply();
  }

  // Listeners are kept to be taken off again: a Reading is made for each sign-in, and one left
  // behind would go on telling the machines of an account that is gone.
  #listen(target, type, fn, options) {
    if (typeof target?.addEventListener !== 'function') { return; }
    target.addEventListener(type, fn, options);
    this.#off.push(() => target.removeEventListener?.(type, fn, options));
  }

  get visible() { return this.#doc?.visibilityState === 'visible'; }

  // Ms since the last input in this page, or null when none has been seen: unknown, not 0.
  idleMs() { return this.#lastInput === null ? null : Math.max(0, this.#now() - this.#lastInput); }

  // The session the page shows now ({ hostId, id, question }), or null: the list, another page,
  // or none. question: the id of the question its card shows, if any: the notice of that question
  // is read too, and a notice of another question is not.
  open(target) {
    const next = target?.id ? { hostId: target.hostId ?? null, id: target.id, question: typeof target.question === 'string' && target.question ? target.question : null } : null;
    if (this.#stopped || (next?.hostId === this.#open?.hostId && next?.id === this.#open?.id && next?.question === this.#open?.question)) { return; }
    // What was waiting was for the session or question shown before. The one shown now has its
    // own passes (a question that has just come is exactly what a push is on its way for).
    this.#cancel();
    this.#open = next;
    this.#apply();
    if (next && this.visible) { this.#watch(); }
  }

  // Everything it listened to is let go, and nothing more is said.
  stop() {
    this.#stopped = true;
    this.#cancel();
    clearTimeout(this.#alive);
    this.#alive = null;
    for (const off of this.#off.splice(0)) { off(); }
  }

  #reading(hostId, id) { return this.visible && this.#open?.hostId === hostId && this.#open?.id === id; }

  // The close passes after a change: the first once in delayMs, the later ones each laterMs after
  // the last change. Never more timers than passes.
  #watch() {
    if (this.#stopped) { return; }
    const pass = (i) => () => {
      this.#passes[i] = null;
      this.#closeOpen();
    };
    [this.#delayMs, ...this.#laterMs].forEach((ms, i) => {
      if (i === 0 && this.#passes[0]) { return; }
      clearTimeout(this.#passes[i]);
      this.#passes[i] = setTimeout(pass(i), ms);
    });
  }

  #cancel() {
    for (const t of this.#passes) { clearTimeout(t); }
    this.#passes = [];
  }

  #input() {
    const now = this.#now();
    if (this.#lastInput !== null && now - this.#lastInput < INPUT_GAP_MS) { return; }
    this.#lastInput = now;
    // The first input of a spell of activity says so at once (the host holds at most one a
    // second or two per channel); the rest of the spell is kept alive by the timer.
    if (this.#alive === null && this.visible) {
      this.#refresh();
      this.#keepAlive();
    }
  }

  #refresh() {
    try { this.#hosts.refreshFocus?.(); } catch (e) { console.error(e); }
  }

  // While input goes on and the page is in sight: say so again each keepalive. Once a whole
  // keepalive has passed without input, or the page is hidden, nothing more is sent.
  #keepAlive() {
    clearTimeout(this.#alive);
    this.#alive = this.visible ? setTimeout(() => {
      this.#alive = null;
      if (!this.visible || (this.idleMs() ?? Infinity) >= this.#keepaliveMs) { return; }
      this.#refresh();
      this.#keepAlive();
    }, this.#keepaliveMs) : null;
  }

  #apply() {
    if (this.#stopped) { return; }
    try {
      this.#hosts.setFocus({
        visible: this.visible, hostId: this.#open?.hostId ?? null, id: this.#open?.id ?? null, question: this.#open?.question ?? null, idle: () => this.idleMs(),
      });
    } catch (e) { console.error(e); }
    if (!this.visible) {
      this.#cancel();
      clearTimeout(this.#alive);
      this.#alive = null;
    } else if (this.#alive === null && this.#lastInput !== null && this.idleMs() < this.#keepaliveMs) {
      this.#keepAlive();
    }
    this.#closeOpen();
  }

  #closeOpen() {
    const o = this.#open;
    if (this.#stopped || !o || !this.#reading(o.hostId, o.id)) { return; }
    Promise.resolve().then(() => this.#close(o.hostId, o.id, o.question)).catch(() => {});
  }
}

// A stream of pointer moves is one input for this long.
const INPUT_GAP_MS = 250;

