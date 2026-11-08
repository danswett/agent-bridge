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
// hosts: HostSession (setFocus, refreshFocus, sessions.onChange). close(hostId, sessionId): takes
// down that session's shown notices. delayMs: how long after a change to the open session its
// notices are looked at again; a push that lands during reading is shown a moment after the
// change that caused it, so closing at once would miss it.
export class Reading {
  #hosts;
  #close;
  #doc;
  #delayMs;
  #keepaliveMs;
  #now;
  #open = null;
  #timer = null;
  #alive = null;
  #lastInput = null;
  #off = [];

  constructor({ hosts, close, doc = globalThis.document ?? null, win = globalThis, delayMs = 1000, keepaliveMs = 60000, now = () => Date.now() }) {
    this.#hosts = hosts;
    this.#close = close;
    this.#doc = doc;
    this.#delayMs = delayMs;
    this.#keepaliveMs = keepaliveMs;
    this.#now = now;
    doc?.addEventListener?.('visibilitychange', () => this.#apply());
    win?.addEventListener?.('pageshow', () => this.#apply());
    // Passive, and cheap: a timestamp write, and only once in INPUT_GAP_MS of a stream of moves.
    for (const type of ['pointerdown', 'pointermove', 'keydown', 'touchstart', 'wheel']) {
      win?.addEventListener?.(type, () => this.#input(), { passive: true, capture: true });
    }
    this.#off.push(hosts.sessions.onChange((c) => {
      if (c?.type !== 'upsert' || !this.#reading(c.hostId, c.id) || this.#timer !== null) { return; }
      this.#timer = setTimeout(() => {
        this.#timer = null;
        this.#closeOpen();
      }, this.#delayMs);
    }));
    this.#apply();
  }

  get visible() { return this.#doc?.visibilityState === 'visible'; }

  // Ms since the last input in this page, or null when none has been seen: unknown, not 0.
  idleMs() { return this.#lastInput === null ? null : Math.max(0, this.#now() - this.#lastInput); }

  // The session the page shows now ({ hostId, id }), or null: the list, another page, or none.
  open(target) {
    const next = target?.id ? { hostId: target.hostId ?? null, id: target.id } : null;
    if (next?.hostId === this.#open?.hostId && next?.id === this.#open?.id) { return; }
    this.#open = next;
    this.#apply();
  }

  stop() {
    clearTimeout(this.#timer);
    clearTimeout(this.#alive);
    this.#timer = this.#alive = null;
    for (const off of this.#off) { off(); }
    this.#off = [];
  }

  #reading(hostId, id) { return this.visible && this.#open?.hostId === hostId && this.#open?.id === id; }

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
    try {
      this.#hosts.setFocus({ visible: this.visible, hostId: this.#open?.hostId ?? null, id: this.#open?.id ?? null, idle: () => this.idleMs() });
    } catch (e) { console.error(e); }
    if (!this.visible) {
      clearTimeout(this.#alive);
      this.#alive = null;
    } else if (this.#alive === null && this.#lastInput !== null && this.idleMs() < this.#keepaliveMs) {
      this.#keepAlive();
    }
    this.#closeOpen();
  }

  #closeOpen() {
    const o = this.#open;
    if (!o || !this.#reading(o.hostId, o.id)) { return; }
    Promise.resolve().then(() => this.#close(o.hostId, o.id)).catch(() => {});
  }
}

// A stream of pointer moves is one input for this long.
const INPUT_GAP_MS = 250;

