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
  #app = false;
  #listeners = new AbortController();
  #observer;
  shown = null;
  #latest = null;
  #pending = new Set();
  #tail = null;
  #rendered = null;

  constructor(element, { scroll = (fn) => fn(), visible = () => element.getClientRects().length > 0, canFollow = () => true, canDefer = () => true, onFollow = () => {} } = {}) {
    Object.assign(this, { element, scroll, visible, canFollow, canDefer, onFollow });
    element.style.overflowAnchor = 'none';
    const { signal } = this.#listeners;
    element.addEventListener('scroll', () => this.read(), { signal, passive: true });
    for (const type of ['wheel', 'touchstart', 'touchmove', 'pointerdown', 'keydown']) {
      element.addEventListener(type, () => { this.#app = false; }, { signal, passive: true });
    }
    element.addEventListener('scrollend', () => { this.#app = false; this.remember(); }, { signal });
    const win = element.ownerDocument.defaultView;
    if (typeof win.ResizeObserver === 'function') {
      this.#observer = new win.ResizeObserver(() => this.changed());
      for (const el of [element, ...element.children]) { this.#observer.observe(el); }
    }
    this.remember();
  }

  remember() {
    this.#top = this.element.scrollTop;
    this.#max = this.element.scrollHeight - this.element.clientHeight;
  }

  read() {
    const el = this.element;
    if (!this.visible()) { return el.scrollTop; }
    const max = el.scrollHeight - el.clientHeight;
    // A changed extent is layout, not input. App writes have already recorded their position.
    const wasFollowing = this.following;
    if (!this.#app && el.scrollTop !== this.#top && max === this.#max) {
      this.following = max - el.scrollTop <= BOTTOM_TOLERANCE_PX;
    }
    this.remember();
    if (!wasFollowing && this.following && this.#latest) {
      this.#pending.clear();
      this.shown = this.#latest;
      this.onFollow();
      if (this.#tail) {
        this.#observer?.unobserve(this.#tail.root);
        this.#tail.view.dispose();
        this.#tail.root.remove();
      }
      this.#tail = null;
      this.after(el.scrollTop);
    }
    return el.scrollTop;
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
    if (this.following || this.#rendered?.id !== id) {
      this.#rendered = { id, turn: snapshot.turns.find((t) => t.id === id) };
    }
    const head = this.following ? snapshot.turns : snapshot.turns.flatMap((t) => t.id === id ? (this.#rendered.turn ? [this.#rendered.turn] : []) : [t]);
    if (!this.#pending.size) { return { ...snapshot, turns: head }; }
    if (!this.#tail) {
      const view = new HistoryView();
      this.#tail = { view, root: h('div', { class: 'stream' }, view.element) };
      this.#tail.root.style.marginBlockStart = 'var(--ab-space-6)';
      this.element.append(this.#tail.root);
      this.#observer?.observe(this.#tail.root);
    }
    const turns = snapshot.turns.filter((t) => this.#pending.has(t.id)).map(streamedTurn);
    this.#tail.view.render({ ...snapshot, turns }, null, inline);
    return { ...snapshot, turns: head.filter((t) => !this.#pending.has(t.id)) };
  }

  get questionHost() { return this.#pending.size ? this.#tail?.root : null; }
  get hosted() { return this.#pending.size > 0 && this.#tail?.view.hosted; }

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

  after(top) {
    if (!this.visible() || this.#app) { return; }
    if (!this.following || this.canFollow()) {
      const to = this.following ? this.element.scrollHeight : top;
      if (this.element.scrollTop !== Math.min(to, Math.max(0, this.element.scrollHeight - this.element.clientHeight))) {
        this.scroll(() => { this.element.scrollTop = to; });
      }
    }
    this.remember();
  }

  changed() {
    this.read();
    if (this.following) { this.after(this.#top); }
  }

  // A deliberate reveal (for example a question) is not the person's scroll input.
  appScroll(change) {
    this.#app = true;
    change();
    this.remember();
  }

  resume() { this.#app = false; this.remember(); }

  dispose() {
    this.#listeners.abort();
    this.#observer?.disconnect();
    this.#tail?.view.dispose();
  }
}

