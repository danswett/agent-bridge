import { h } from './dom.js';
import { HistoryView } from './history-view.js';

export const BOTTOM_TOLERANCE_PX = 4;
const streamedTurns = new WeakMap();

// A history page may lag the live words of the current deferred turn.
export function streamedTurn(turn) {
  const cached = streamedTurns.get(turn);
  if (cached) { return cached; }
  const text = turn.live?.response;
  const at = turn.parts.findLastIndex((p) => p.kind === 'response' && p.text);
  const last = turn.parts[at];
  const continuation = last && !turn.parts.slice(at + 1).some((p) => p.kind === 'question');
  if (!text || text === last?.text || (continuation && last.text.startsWith(text))) { return turn; }
  const parts = [...turn.parts];
  const response = { kind: 'response', text, truncated: turn.live.responseTruncated === true };
  if (continuation && text.startsWith(last.text)) {
    parts[at] = { ...last, ...response };
  } else {
    parts.push(response);
  }
  const drawn = { ...turn, parts };
  streamedTurns.set(turn, drawn);
  return drawn;
}

// Content changes and our own scrolls cannot change the person's follow state.
export class ScrollFollow {
  following = true;
  #top = 0;
  #max = 0;
  #app = null;
  #listeners = new AbortController();
  #observer;
  #observed = new Set();
  #sizes = new WeakMap();
  shown = null;
  #latest = null;
  #pending = new Set();
  #tail = null;
  #rendered = null;

  constructor(element, { input = element, content = () => element.children, scroll = (fn) => fn(), visible = () => element.getClientRects().length > 0, canFollow = () => true, canDefer = () => true, onFollow = () => {} } = {}) {
    Object.assign(this, { element, content, scroll, visible, canFollow, canDefer, onFollow });
    const { signal } = this.#listeners;
    element.addEventListener('scroll', () => this.read(), { signal, passive: true });
    for (const type of ['wheel', 'touchstart', 'touchmove', 'pointerdown', 'keydown']) {
      input.addEventListener(type, () => { if (this.#app) { this.#app.active = false; } }, { signal, passive: true, capture: true });
    }
    element.addEventListener('scrollend', () => { if (this.#app) { this.#app.active = false; } this.remember(); }, { signal });
    const win = element.ownerDocument.defaultView;
    if (typeof win.ResizeObserver === 'function') {
      this.#observer = new win.ResizeObserver((entries) => this.changed(entries));
      this.#observe();
    }
    this.remember();
  }

  remember() {
    this.#top = this.element.scrollTop;
    this.#max = this.element.scrollHeight - this.element.clientHeight;
  }

  get reading() { return this.following && this.#max - this.#top <= BOTTOM_TOLERANCE_PX; }
  get canPin() { return this.following && !this.#app?.active && this.canFollow(this.#app !== null); }

  #observe() {
    if (!this.#observer) { return; }
    const next = new Set(this.content());
    const removed = [...this.#observed].filter((el) => !next.has(el));
    let replaced = removed.reduce((sum, el) => sum + (this.#sizes.get(el) ?? 0), 0);
    for (const el of removed) { this.#observer.unobserve(el); }
    const visible = this.visible();
    for (const el of next) {
      // Initial layout is not growth; replacements retain the removed content's measurement.
      if (visible && !this.#sizes.has(el)) {
        const height = el.getBoundingClientRect().height;
        this.#sizes.set(el, removed.length ? Math.min(height, replaced) : height);
        replaced = Math.max(0, replaced - height);
      }
      if (!this.#observed.has(el)) { this.#observer.observe(el, { box: 'border-box' }); }
    }
    this.#observed = next;
  }

  read() {
    const el = this.element;
    if (!this.visible()) { return el.scrollTop; }
    const max = el.scrollHeight - el.clientHeight;
    // A changed extent is layout, not input. App writes have already recorded their position.
    const wasFollowing = this.following;
    if (!this.#app?.active && el.scrollTop !== this.#top && max === this.#max) {
      this.following = max - el.scrollTop <= BOTTOM_TOLERANCE_PX;
      this.#app = null;
    }
    this.remember();
    if (!wasFollowing && this.following && this.#latest) {
      this.#rejoin();
    }
    return el.scrollTop;
  }

  // Choosing a cached session starts at its latest, not at the previous reading position.
  open() {
    this.following = true;
    this.resume();
    if (this.#pending.size) { this.#rejoin(); } else { this.after(); }
  }

  #rejoin() {
    this.#pending.clear();
    this.shown = this.#latest;
    this.onFollow();
    if (this.#tail) {
      this.#observer?.unobserve(this.#tail.root);
      this.#tail.view.dispose();
      this.#tail.root.remove();
    }
    this.#tail = null;
    this.after();
  }

  display(session) {
    this.#latest = session;
    if (this.following || !this.shown?.activity.turn || !session.activity.turn || (!this.#pending.size && !this.canDefer())) {
      this.shown = session;
    } else if (session.activity.turn !== this.shown.activity.turn) {
      this.#pending.add(session.activity.turn);
    } else {
      this.shown = session;
    }
    return this.shown;
  }

  // Keep the already visible exchange in its live DOM; append subsequent turns below it.
  snapshot(snapshot, inline) {
    const id = this.shown?.activity.turn;
    if (!this.#pending.size || this.#rendered?.id !== id) {
      this.#rendered = { id, turn: snapshot.turns.find((t) => t.id === id) };
    }
    const head = !this.#pending.size ? snapshot.turns : snapshot.turns.flatMap((t) => t.id === id ? (this.#rendered.turn ? [this.#rendered.turn] : []) : [t]);
    if (!this.#pending.size) { return { ...snapshot, turns: head }; }
    if (!this.#tail) {
      const view = new HistoryView();
      this.#tail = { view, root: h('div', { class: 'stream' }, view.element) };
      this.#tail.root.style.marginBlockStart = 'var(--ab-space-6)';
      this.element.append(this.#tail.root);
    }
    const turns = snapshot.turns.filter((t) => this.#pending.has(t.id)).map(streamedTurn);
    this.#tail.view.render({ ...snapshot, turns }, null, inline);
    return { ...snapshot, turns: head.filter((t) => !this.#pending.has(t.id)) };
  }

  get questionHost() { return this.#pending.size ? this.#tail?.root : null; }
  get hosted() { return this.#pending.size > 0 && this.#tail?.view.hosted; }
  get deferred() { return this.#pending.size > 0; }

  place(slot, host) {
    if (slot.parentNode === host) { return; }
    const focused = slot.ownerDocument.activeElement;
    const held = slot.contains(focused) ? focused : null;
    const caret = held && typeof held.selectionStart === 'number' ? [held.selectionStart, held.selectionEnd, held.selectionDirection ?? 'none'] : null;
    host.append(slot);
    if (held?.isConnected && held.getClientRects().length) {
      held.focus({ preventScroll: true });
      if (caret) { held.setSelectionRange(...caret); }
    }
  }

  after(arrived = true) {
    if (!this.visible()) { return; }
    this.#observe();
    if (this.#app?.active) { return; }
    if (arrived && this.canPin) {
      const to = this.element.scrollHeight;
      if (this.element.scrollTop !== Math.max(0, to - this.element.clientHeight)) {
        this.scroll(() => { this.element.scrollTop = to; });
      }
    }
    this.remember();
  }

  changed(entries) {
    if (!this.visible()) { return; }
    let grew = false;
    for (const { target, contentRect, borderBoxSize } of entries) {
      if (!this.#observed.has(target)) { continue; }
      const height = borderBoxSize?.[0]?.blockSize ?? contentRect.height;
      grew ||= height > (this.#sizes.get(target) ?? 0);
      this.#sizes.set(target, height);
    }
    this.read();
    if (this.following && grew) { this.after(); }
  }

  // A deliberate reveal (for example a question) is not the person's scroll input.
  appScroll(change, protect = true) {
    const hold = this.#app = { active: true };
    change();
    this.remember();
    return () => {
      if (this.#app !== hold) { return; }
      hold.active = false;
      if (!protect) { this.#app = null; }
      this.remember();
    };
  }

  resume() { this.#app = null; this.remember(); }

  dispose() {
    this.#listeners.abort();
    this.#observer?.disconnect();
    this.#tail?.view.dispose();
  }
}
