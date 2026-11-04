// Moving a session by hand (#592, #593; ui.md §5.3 Lift and Reorder, §6.1 Moving, §9): the card
// itself is lifted and follows the pointer, and the cards it passes ease aside, so the gap they
// leave is where it lands. There is no insertion line, ghost or caption.
//
//   * Touch: a long press anywhere on a card (the platform's own: 500 ms on iOS, 400 on Android,
//     500 elsewhere), or a 150 ms hold or a 4 px move on its grip, lifts it, with one haptic tick.
//     A finger that moves 10 px first is scrolling: the browser has it, and nothing lifts. A long
//     press released without moving opens the row's menu.
//   * Mouse or pen: the grip lifts at once; the card itself once it has moved 4 px with the
//     button down, so a click still opens the session.
//   * Escape, a release more than 32 px outside the list, the system taking the pointer (with no
//     touch stream to follow), a second finger, the window losing focus or the page hiding puts
//     the card back.
//
// The order changes at the release, in one change, and the card then only settles there: a key,
// the menu or a press during the settle acts on the order as it is now. (#605's prototype rebuilt
// the order when the settle ended, which lost a key's move made meanwhile, and left a press on
// the gap to land on nothing.)
//
// It knows the list's markup only as children: `.session` cards (data-pinned on a pinned one),
// `.list-group[data-group]` headers, and anything else, which stays where it is. What a card is,
// and what a drop does, are the host's.
import { leave, slide, timing } from './motion.js';
import { platform } from '../platform.js';

// A long press, as each platform times its own (D2): UIKit's drag lift and Android 12's
// ViewConfiguration. Android before 12 used 500; Chrome there reports the same "Android".
export function longPressMs(p = platform().platform) { return p === 'android' ? 400 : 500; }
// A finger that moves further than this before the long press is scrolling.
export const TOUCH_SLOP = 10;
// A mouse or pen (and a finger on the grip) moving further than this is dragging.
const DRAG_SLOP = 4;
const GRIP_HOLD_MS = 150;
const OUTSIDE = 32;
// Auto-scroll at the edges: 1000 px/s × (depth ÷ zone)², the zone 10% of the list seen, 40–80 px.
const SPEED = 1000;
const SAY_EVERY_MS = 500;
const NAMES = { pinned: 'Pinned', sessions: 'Sessions' };
const CUES = { pinned: ' \u00b7 drop to pin', sessions: ' \u00b7 drop to unpin' };

const same = (a, b) => !!a && !!b && a.group === b.group && a.index === b.index;

// host: {
//   cardOf(el) -> the card an element is in, or null (card.element is its `.session`);
//   movable(card) -> whether it can be lifted now (Your order, no search, placed);
//   fixed(card) -> whether it holds its place (its machine is still sending its list);
//   title(card) -> what to call it;
//   commit(card, group, index, { before, after }) -> whether the order changed (and the list was
//     drawn so): the card goes to place index of group, between the cards before and after (each
//     side's, nearest first, as they were when it was lifted);
//   menu(card): its row menu, for a long press released without moving;
//   hint(): what a tap on the grip says;
//   header(group) -> the group's header, for Sessions while everything is pinned and a card is held;
//   say(text): a polite announcement;
//   drawn(): the list may draw what waited while a card was held.
// }
export class Reorder {
  #g = null;
  #settle = null;
  #reroute = null;
  #swallow = null;
  #touch = null;
  #spoke = 0;
  #speak = 0;

  constructor(list, host, { top = null, bottom = null } = {}) {
    this.list = list;
    this.host = host;
    // The bars the list scrolls under (the app bar, a phone's bottom navigation): what is seen of
    // the list ends at them.
    this.bars = { top, bottom };
    this.doc = list.ownerDocument;
    this.win = this.doc.defaultView;
    this.#listen();
  }

  // A card is held: what the host changes in the list waits to be drawn until it's let go.
  get holding() { return this.#g?.phase === 'lifted'; }
  // The session held, if one is.
  get held() { return this.holding ? this.#g.card : null; }
  // A press or a drag is going on (the long press's own contextmenu is the drag's, not a menu).
  get busy() { return !!this.#g; }

  // A press on a card (pointerdown), which may become a lift. target: what was pressed.
  press(e, card, target = e.target) {
    if (this.#g || !e.isPrimary || !card?.element?.isConnected) { return false; }
    const type = e.pointerType || 'mouse';
    if (type !== 'touch' && e.button !== 0) { return false; }
    const onGrip = !!target.closest?.('.drag-handle');
    const g = { id: e.pointerId, type, card, el: card.element, onGrip, sx: e.clientX, sy: e.clientY, x: e.clientX, y: e.clientY, phase: 'pending', timer: 0 };
    if (type === 'touch' && this.#touch && Math.abs(e.timeStamp - this.#touch.at) < 100) { g.touchId = this.#touch.id; }
    this.#g = g;
    this.#unswallow();
    this.#attach(g);
    if (type === 'touch') {
      g.el.classList.add('holding');
      if (onGrip) {
        g.timer = setTimeout(() => this.#lift(), GRIP_HOLD_MS);
      } else {
        const ms = longPressMs();
        // The card sinks over the hold, so the hold shows its progress (ui.md §5.3, Lift); with
        // reduced motion it doesn't.
        if (timing('lift', { doc: this.doc }).duration > 0) {
          g.el.style.setProperty('--ab-hold', `${ms}ms`);
          g.el.classList.add('sinking');
        }
        g.timer = setTimeout(() => this.#lift(), ms);
      }
    } else if (onGrip) {
      // No text selection, and no focus moving off what has it, from a press on the grip.
      e.preventDefault();
      this.#lift();
    }
    return true;
  }

  // Ends any press or drag at once, with the card back where it was: the sort changed or search
  // started (said, as Escape is). removed: the held session went, and its card fades out where it
  // is held (Arriving and leaving), with nothing said.
  cancel({ removed = false } = {}) {
    const g = this.#g;
    if (!g) { this.finish(); return; }
    if (g.phase === 'pending') { this.#abort(); return; }
    this.#drop(true, { instant: true, removed });
  }

  // A drop's settle ends at once: a key or the menu acts on the card where it now is.
  finish() {
    for (const a of this.list.getAnimations?.({ subtree: true }) ?? []) { if (a.id === 'ab-motion-reorder') { a.finish(); } }
    this.#settle?.done();
  }

  // What a press (pointerdown) acts on: during a drop's settle, the card in its new place under the
  // pointer rather than whatever was drawn there a moment before.
  target(e) { return this.#reroute?.id === e.pointerId ? this.#reroute.target : e.target; }

  #listen() {
    const list = this.list;
    list.addEventListener('pointerdown', (e) => {
      this.#reroute = null;
      if (!this.#settle) { return; }
      this.finish();
      // The grip is the row's sibling, not in it: a press there lifts the card now under it.
      const hit = this.doc.elementFromPoint?.(e.clientX, e.clientY)?.closest?.('.drag-handle, .row-main');
      if (hit && list.contains(hit) && !hit.contains(e.target)) {
        this.#reroute = { id: e.pointerId, target: hit, row: hit.matches('.row-main') ? hit : null };
      }
    }, true);
    // The click of a press that was rerouted lands where the press went (on the grip, nowhere);
    // its target is wherever the press and the release had in common.
    list.addEventListener('click', (e) => {
      const r = this.#reroute;
      if (!r || e.detail === 0 || r.row?.contains(e.target)) { return; }
      this.#reroute = null;
      e.preventDefault();
      e.stopImmediatePropagation();
      r.row?.click();
    }, true);
    list.addEventListener('dragstart', (e) => { if (e.target.closest?.('.session')) { e.preventDefault(); } });
    // The finger a touch press is: some engines send its touchstart before its pointerdown.
    list.addEventListener('touchstart', (e) => {
      const t = e.changedTouches?.[0];
      this.#touch = t ? { id: t.identifier, at: e.timeStamp } : null;
    }, { passive: true });
  }

  #attach(g) {
    const doc = this.doc;
    const win = this.win;
    const on = [];
    const add = (target, type, fn, opts) => { target.addEventListener(type, fn, opts); on.push(() => target.removeEventListener(type, fn, opts)); };
    add(doc, 'pointermove', (e) => this.#move(e));
    add(doc, 'pointerup', (e) => this.#up(e));
    add(doc, 'pointercancel', (e) => this.#pointerCancel(e));
    add(win, 'keydown', (e) => {
      if (e.key !== 'Escape') { return; }
      if (this.#g?.phase === 'lifted') { e.preventDefault(); e.stopImmediatePropagation(); this.#drop(true); } else { this.#abort(); }
    }, true);
    add(win, 'blur', () => this.cancel());
    add(doc, 'visibilitychange', () => { if (doc.hidden) { this.cancel(); } });
    // Touch: the drag is one finger's. Once lifted the page mustn't scroll under it (the list
    // auto-scrolls instead), and a browser that takes the pointer for itself (pointercancel)
    // still sends that finger's touches, which carry the drag on.
    add(doc, 'touchstart', (e) => {
      if (e.touches.length > 1) { this.cancel(); return; }
      // The press's own finger: where its pointerdown came first, the one remembered may be the
      // finger before's.
      const t = e.changedTouches[0];
      if (t && g.phase === 'pending') { g.touchId = t.identifier; }
    }, { passive: true, capture: true });
    add(doc, 'touchmove', (e) => {
      if (this.#g !== g) { return; }
      if (g.phase === 'lifted' || (g.phase === 'pending' && g.onGrip)) { if (e.cancelable) { e.preventDefault(); } }
      const t = g.viaTouch && g.phase === 'lifted' ? this.#own(g, e.touches) : null;
      if (t) { g.x = t.clientX; g.y = t.clientY; this.#track(); }
    }, { passive: false, capture: true });
    add(doc, 'touchend', (e) => { if (this.#g === g && g.viaTouch && g.phase === 'lifted' && this.#own(g, e.changedTouches)) { this.#drop(false, { released: true }); } }, true);
    add(doc, 'touchcancel', (e) => { if (this.#g === g && g.phase === 'lifted' && this.#own(g, e.changedTouches)) { this.#drop(true, { released: true }); } }, true);
    // The list (or the page) scrolling moves the list under a card held still.
    add(doc, 'scroll', () => { if (this.#g === g && g.phase === 'lifted') { this.#track(); } }, { passive: true, capture: true });
    add(win, 'resize', () => { if (this.#g === g && g.phase === 'lifted' && this.list.getBoundingClientRect().width !== g.width) { this.#drop(true, { instant: true }); } });
    g.off = () => { for (const off of on.splice(0)) { off(); } };
  }

  #own(g, touches) {
    const list = [...(touches ?? [])];
    return g.touchId === undefined ? list[0] ?? null : list.find((t) => t.identifier === g.touchId) ?? null;
  }

  #move(e) {
    const g = this.#g;
    if (!g || e.pointerId !== g.id) { return; }
    g.x = e.clientX;
    g.y = e.clientY;
    if (g.phase === 'pending') {
      const d = Math.hypot(g.x - g.sx, g.y - g.sy);
      if (g.type === 'touch') {
        if (g.onGrip && d > DRAG_SLOP) { this.#lift(); } else if (!g.onGrip && d > TOUCH_SLOP) { this.#abort(); }
      } else if (d > DRAG_SLOP) {
        this.#lift();
      }
    } else if (g.phase === 'lifted') {
      this.#track();
    }
  }

  #up(e) {
    const g = this.#g;
    if (!g || e.pointerId !== g.id) { return; }
    if (g.phase === 'lifted') { this.#drop(false, { released: true }); return; }
    const tapGrip = g.onGrip && g.type === 'touch';
    this.#abort();
    if (tapGrip) { this.#swallowClick(); this.host.hint?.(); }
  }

  #pointerCancel(e) {
    const g = this.#g;
    if (!g || e.pointerId !== g.id) { return; }
    if (g.phase !== 'lifted') { this.#abort(); return; }
    // A finger whose touches still come: they carry the drag; a touchcancel ends it.
    if (g.type === 'touch' && g.touchId !== undefined) { g.viaTouch = true; return; }
    this.#drop(true, { released: true });
  }

  #abort() {
    const g = this.#g;
    if (!g) { return; }
    clearTimeout(g.timer);
    g.off?.();
    this.#unhold(g);
    this.#g = null;
  }

  #unhold(g) {
    g.el.classList.remove('holding', 'sinking');
    g.el.style.removeProperty('--ab-hold');
  }

  // The list as it is laid out now (no card moved yet): each child's box, top and bottom with its
  // margins, relative to the list's top; the groups and the places a card can land in each.
  #measure(el) {
    const list = this.list;
    const lr = list.getBoundingClientRect();
    const items = [...list.children].filter((n) => !n.matches('.drag-gap, .drag-keep') && n.getClientRects().length > 0);
    const box = (n) => {
      const cs = getComputedStyle(n);
      const top = this.#offset(n);
      return { el: n, top: top - (parseFloat(cs.marginTop) || 0), bottom: top + this.#height(n) + (parseFloat(cs.marginBottom) || 0) };
    };
    const at = items.indexOf(el);
    const me = box(el);
    const gap = parseFloat(getComputedStyle(list).rowGap) || 0;
    const L = items.filter((n) => n !== el).map((n, j) => ({ ...box(n), after: j >= at }));
    const heads = {};
    L.forEach((b, j) => { if (b.el.matches('.list-group[data-group]')) { heads[b.el.dataset.group] = j; } });
    const grouped = Object.keys(heads).length > 0;
    const groupOf = (n) => (grouped && n.dataset.pinned === 'true' ? 'pinned' : 'sessions');
    const groups = {};
    for (const id of grouped ? Object.keys(heads) : ['sessions']) { groups[id] = { id, head: heads[id] ?? null, cards: [] }; }
    L.forEach((b, j) => {
      if (!b.el.classList.contains('session')) { return; }
      const c = this.host.cardOf(b.el);
      groups[groupOf(b.el)]?.cards.push({ j, el: b.el, fixed: !!(c && this.host.fixed?.(c)) });
    });
    const origin = { group: groupOf(el), index: items.slice(0, at).filter((n) => n.classList.contains('session') && groupOf(n) === groupOf(el)).length };
    return { L, me, at, h: me.bottom - me.top, S: me.bottom - me.top + gap, gap, groups, grouped, origin, width: lr.width };
  }

  // Every place the card can land: where it would sit (relative to the list's top), and how far
  // each other child moves to make room for it there.
  #slots(m) {
    const slots = [];
    const others = m.groups[m.origin.group]?.cards.length ?? 1;
    for (const grp of Object.values(m.groups)) {
      const n = grp.cards.length;
      let first = 0;
      while (first < n && grp.cards[first].fixed) { first += 1; }
      for (let i = first; i <= n; i++) {
        const p = i < n ? grp.cards[i].j : n > 0 ? grp.cards[n - 1].j + 1 : grp.head !== null ? grp.head + 1 : 0;
        // The last card of its group held in the other keeps its group open, its place standing
        // empty ("Drop here to keep it pinned", or "Drop here to unpin").
        const keep = grp.id !== m.origin.group && others === 0;
        const removed = (j) => (!keep && m.L[j]?.after ? -m.S : 0);
        // A group with no card (Sessions, everything pinned): its place is under its header.
        const top = n === 0 && grp.head !== null ? m.L[grp.head].bottom + m.gap + removed(grp.head)
          : p < m.L.length ? m.L[p].top + removed(p) : m.L.length ? m.L[m.L.length - 1].bottom + m.gap + removed(m.L.length - 1) : m.me.top;
        // The cards either side of the place, nearest first, by which the drop says where it goes.
        const before = grp.cards.slice(i).map((c) => c.el);
        const after = grp.cards.slice(0, i).reverse().map((c) => c.el);
        slots.push({ group: grp.id, index: i, n: n + 1, p, keep, top, before, after, shift: (j) => removed(j) + (j >= p ? m.S : 0) });
      }
    }
    return slots;
  }

  #lift() {
    const g = this.#g;
    if (!g || g.phase !== 'pending') { return; }
    clearTimeout(g.timer);
    this.#unhold(g);
    if (!g.el.isConnected || !this.host.movable(g.card)) { this.#abort(); return; }
    this.finish();
    // Everything pinned: Sessions has no header for as long as it is empty, so one shows under the
    // list while a pinned card is held, with "Drop here to unpin" in its empty place (ui.md §6.1,
    // Groups). It is below every card, so nothing above it moves as it comes and goes.
    if (g.el.dataset.pinned === 'true' && !this.list.querySelector(':scope > .list-group[data-group="sessions"]')) {
      const head = this.host.header?.('sessions');
      if (head && !head.isConnected) {
        g.head = head;
        this.list.insertBefore(head, this.list.querySelector(':scope > .ended-group'));
      }
    }
    const m = this.#measure(g.el);
    const slots = this.#slots(m);
    const origin = slots.find((s) => same(s, m.origin));
    if (!origin) { g.head?.remove(); this.#abort(); return; }
    const top = this.list.getBoundingClientRect().top + m.me.top;
    Object.assign(g, { phase: 'lifted', m, slots, origin, slot: origin, offY: g.y - top, lx: g.x, ly: g.y, armed: false, outside: false, acc: 0, t: 0, width: m.width });
    const doc = this.doc;
    g.gap = doc.createElement('div');
    g.gap.className = 'drag-gap';
    g.gap.setAttribute('aria-hidden', 'true');
    g.gap.style.height = `${m.h}px`;
    g.gap.style.transform = `translateY(${m.me.top}px)`;
    g.chip = doc.createElement('span');
    g.chip.className = 'drag-chip';
    g.chip.setAttribute('aria-hidden', 'true');
    g.el.querySelector('.row-main')?.append(g.chip);
    this.list.append(g.gap);
    this.list.classList.add('reordering');
    doc.documentElement.dataset.reordering = g.type;
    g.el.classList.add('lifted');
    if (g.type !== 'touch') { try { this.list.setPointerCapture(g.id); } catch { /* the pointer is already gone */ } }
    // One light tick as it lifts (D4), where the web has one (Android's Chrome; not iOS's Safari).
    if (g.type === 'touch') { try { this.win.navigator.vibrate?.(10); } catch { /* not allowed */ } }
    const { group, index, n } = origin;
    this.#say(`Picked up ${this.host.title(g.card)}, position ${index + 1} of ${n} in ${NAMES[group]}.`);
    this.#track();
    const tick = (t) => {
      if (this.#g !== g || g.phase !== 'lifted') { return; }
      this.#scroll(g, t);
      g.raf = this.win.requestAnimationFrame(tick);
    };
    g.raf = this.win.requestAnimationFrame(tick);
  }

  // What is seen of the list: the list's box inside the viewport, its scroller and the bars.
  #seen() {
    const lr = this.list.getBoundingClientRect();
    let top = Math.max(lr.top, 0);
    // The places standing empty below the rows ("Drop here to …") are the list's too, though they
    // only make room under its box: while one can show, the list reaches that far for the whole
    // drag, so the card aimed at one doesn't turn to Cancel as the rows close up over it.
    const g = this.#g;
    const empty = g?.m ? Object.values(g.m.groups).filter((grp) => !grp.cards.length && grp.head !== null).length : 0;
    const reach = Math.max(parseFloat(this.list.style.marginBottom) || 0, empty * (g?.m?.S ?? 0));
    let bottom = Math.min(lr.bottom + reach, this.win.innerHeight);
    const scroller = this.#scroller();
    if (scroller !== this.doc.scrollingElement) {
      const r = scroller.getBoundingClientRect();
      top = Math.max(top, r.top + scroller.clientTop);
      bottom = Math.min(bottom, r.top + scroller.clientTop + scroller.clientHeight);
    }
    const bar = (sel) => {
      const el = sel ? this.doc.querySelector(sel) : null;
      if (!el || el.hidden || !el.getClientRects().length) { return null; }
      const pos = getComputedStyle(el).position;
      return pos === 'fixed' || pos === 'sticky' ? el.getBoundingClientRect() : null;
    };
    const above = bar(this.bars.top);
    if (above && above.top <= 0.5) { top = Math.max(top, above.bottom); }
    const below = bar(this.bars.bottom);
    if (below && below.bottom >= this.win.innerHeight - 0.5 && below.width >= this.win.innerWidth / 2) { bottom = Math.min(bottom, below.top); }
    return { left: lr.left, right: lr.right, top, bottom: Math.max(top, bottom) };
  }

  #scroller() {
    for (let el = this.list.parentElement; el && el !== this.doc.body; el = el.parentElement) {
      const oy = getComputedStyle(el).overflowY;
      if ((oy === 'auto' || oy === 'scroll') && el.scrollHeight > el.clientHeight + 1) { return el; }
    }
    return this.doc.scrollingElement ?? this.doc.documentElement;
  }

  // The card under the pointer, and the place it would land.
  #track() {
    const g = this.#g;
    if (!g || g.phase !== 'lifted') { return; }
    const lr = this.list.getBoundingClientRect();
    g.ty = g.y - g.offY - lr.top - g.m.me.top;
    // The `translate` property, applied before the card's `scale`: a `transform` would be scaled
    // with it, and the card would drift from the pointer by 2% of how far it had gone.
    g.el.style.translate = `0 ${g.ty}px`;
    if (!g.armed && Math.hypot(g.x - g.lx, g.y - g.ly) > DRAG_SLOP) { g.armed = true; }
    const v = this.#seen();
    const outside = g.x < v.left - OUTSIDE || g.x > v.right + OUTSIDE || g.y < v.top - OUTSIDE || g.y > v.bottom + OUTSIDE;
    let slot = g.origin;
    if (!outside) {
      const at = g.m.me.top + g.ty;
      const d = (s) => Math.abs(s.top - at);
      let best = g.slot;
      for (const s of g.slots) { if (d(s) < d(best) - 1) { best = s; } }
      slot = best;
    }
    this.#place(g, slot, outside);
  }

  #place(g, slot, outside) {
    const crossing = !outside && slot.group !== g.origin.group;
    g.el.classList.toggle('crossing', crossing);
    g.el.classList.toggle('outside', outside);
    g.chip.textContent = outside ? 'Cancel' : crossing ? (slot.group === 'pinned' ? 'Pin' : 'Unpin') : '';
    for (const grp of Object.values(g.m.groups)) {
      const head = grp.head !== null ? g.m.L[grp.head].el : null;
      if (!head) { continue; }
      const target = crossing && grp.id === slot.group;
      head.classList.toggle('drop-target', target);
      let cue = head.querySelector(':scope > .group-cue');
      if (target && !cue) {
        cue = this.doc.createElement('span');
        cue.className = 'group-cue';
        head.append(cue);
      }
      if (cue) { if (target) { cue.textContent = CUES[grp.id]; } else { cue.remove(); } }
    }
    if (outside !== g.outside) { g.outside = outside; }
    if (same(slot, g.slot) && g.placed) { return; }
    const moved = g.placed && !same(slot, g.slot);
    g.slot = slot;
    g.placed = true;
    const holders = this.#holders(g, slot);
    // A group's empty place shown under its header moves what follows it down a place too.
    const after = holders.filter((p) => p.head !== undefined);
    g.m.L.forEach((b, j) => {
      const dy = slot.shift(j) + after.filter((p) => j > p.head).length * g.m.S;
      b.el.style.transform = dy ? `translateY(${dy}px)` : '';
    });
    g.gap.style.transform = `translateY(${slot.top}px)`;
    this.#hold(g, holders);
    if (moved && !outside) {
      const crossed = slot.group !== g.origin.group;
      this.#say(crossed
        ? `${NAMES[slot.group]}, position ${slot.index + 1} of ${slot.n}. Drops as ${slot.group === 'pinned' ? 'pinned' : 'unpinned'}.`
        : `Position ${slot.index + 1} of ${slot.n}.`, { held: true });
    }
  }

  // The places of groups left with no card while the card is held outside them (ui.md §6.1,
  // Groups): "Drop here to keep it pinned" or "Drop here to unpin". The card's own group's stands
  // where it was; a group with no card at all (Sessions, everything pinned) has its place under
  // its header.
  #holders(g, slot) {
    const out = [];
    for (const grp of Object.values(g.m.groups)) {
      if (grp.cards.length || grp.id === slot.group || grp.head === null) { continue; }
      const text = grp.id === 'pinned' ? 'Drop here to keep it pinned' : 'Drop here to unpin';
      if (grp.id === g.m.origin.group) {
        // Where it was, moved down a place with what follows it when the card is held above it.
        out.push({ id: grp.id, text, top: g.m.me.top + (slot.p <= g.m.at ? g.m.S : 0) });
      } else {
        out.push({ id: grp.id, text, head: grp.head, top: g.m.L[grp.head].bottom + g.m.gap + slot.shift(grp.head) });
      }
    }
    return out;
  }

  #hold(g, holders) {
    g.holders ??= new Map();
    for (const [id, el] of g.holders) {
      if (!holders.some((p) => p.id === id)) { el.remove(); g.holders.delete(id); }
    }
    for (const p of holders) {
      let el = g.holders.get(p.id);
      if (!el) {
        el = this.doc.createElement('div');
        el.className = 'drag-keep';
        el.dataset.group = p.id;
        el.textContent = p.text;
        el.style.height = `${g.m.h}px`;
        el.style.transform = `translateY(${p.top}px)`;
        this.list.append(el);
        g.holders.set(p.id, el);
      }
      el.style.transform = `translateY(${p.top}px)`;
    }
    // Each place standing empty moved the rows below it down one: the list's foot does too, so
    // nothing after it is covered.
    this.list.style.marginBottom = g.holders.size ? `${g.holders.size * g.m.S}px` : '';
  }

  #scroll(g, t) {
    const dt = g.t ? Math.min(0.05, (t - g.t) / 1000) : 0;
    g.t = t;
    if (!g.armed || g.outside) { g.acc = 0; return; }
    const v = this.#seen();
    const zone = Math.max(40, Math.min(80, (v.bottom - v.top) * 0.1));
    const up = v.top + zone - g.y;
    const down = g.y - (v.bottom - zone);
    const speed = up > 0 ? -SPEED * Math.min(1, up / zone) ** 2 : down > 0 ? SPEED * Math.min(1, down / zone) ** 2 : 0;
    if (!speed) { g.acc = 0; return; }
    g.acc += speed * dt;
    const step = Math.trunc(g.acc);
    if (!step) { return; }
    g.acc -= step;
    const scroller = this.#scroller();
    const before = scroller.scrollTop;
    scroller.scrollTop = before + step;
    if (scroller.scrollTop !== before) { this.#track(); }
  }

  // Let go: back where it came from, or into the gap. instant: no settle (the list is about to
  // change under it). released: the pointer or finger that held it is up (or gone), so the click
  // its release brings has come or won't.
  #drop(cancel, { instant = false, removed = false, released = false } = {}) {
    const g = this.#g;
    if (!g || g.phase !== 'lifted') { return; }
    g.off?.();
    this.win.cancelAnimationFrame(g.raf);
    try { if (this.list.hasPointerCapture?.(g.id)) { this.list.releasePointerCapture(g.id); } } catch { /* already released */ }
    const hold = g.type === 'touch' && !g.armed && !cancel;
    const back = cancel || g.outside || hold;
    const to = back ? g.origin : g.slot;
    const lr = this.list.getBoundingClientRect();
    // Where each card is seen now, part way through making room perhaps; and the held one.
    const seen = new Map(g.m.L.map((b) => [b.el, b.el.getBoundingClientRect().top]));
    const from = lr.top + g.m.me.top + (g.ty ?? 0);
    // A held session that went fades where it is held, as it is seen and clipped there (the host's
    // own fade would be at its place, which auto-scroll may have taken out of sight).
    if (removed) { leave(g.el, 'listItem'); }
    this.#g = null;
    // At rest in the layout at once: nothing eases there, everything is slid from where it was seen.
    this.list.classList.remove('reordering');
    for (const b of g.m.L) {
      b.el.style.transform = '';
      b.el.classList.remove('drop-target');
      b.el.querySelector(':scope > .group-cue')?.remove();
    }
    g.gap.remove();
    this.#hold(g, []);
    // Sessions' header shown for the drag goes; the list draws it again if the drop needs it.
    g.head?.remove();
    g.chip.remove();
    delete this.doc.documentElement.dataset.reordering;
    g.el.classList.remove('lifted', 'crossing', 'outside');
    g.el.style.translate = '';
    const swallow = () => this.#swallowClick(released ? null : g);
    if (removed) {
      g.el.dataset.left = 'true';
      // The finger or button is still down over whatever takes the card's place: its release
      // presses nothing. The move is cancelled, said as a cancel is but with no place to be back
      // at (ui.md §9).
      this.#say(`${this.host.title(g.card)} ended. Move cancelled.`);
      swallow();
      // The host draws the list without it once this returns; the others then close the gap from
      // where they were seen, on `reorder` (at once under reduced motion).
      queueMicrotask(() => {
        for (const [el, top] of seen) {
          if (!el.isConnected || el === g.el) { continue; }
          const dy = top - el.getBoundingClientRect().top;
          if (Math.abs(dy) > 0.5) { slide(el, 'reorder', 0, dy); }
        }
      });
      return;
    }
    if (!instant) { g.el.classList.add('settling'); }
    const cards = (els) => els.map((el) => this.host.cardOf(el)).filter(Boolean);
    const moved = !back && !same(to, g.origin) && g.el.isConnected && this.host.commit(g.card, to.group, to.index, { before: cards(to.before), after: cards(to.after) });
    if (!moved) { this.host.drawn?.(); }
    if (!instant && g.el.isConnected) {
      for (const [el, top] of seen) {
        if (!el.isConnected) { continue; }
        const dy = top - el.getBoundingClientRect().top;
        if (Math.abs(dy) > 0.5) { slide(el, 'reorder', 0, dy); }
      }
      // The held card's own box, without the scale it is still easing down from; the slide is
      // scaled with it, so it starts that much shorter.
      const now = this.list.getBoundingClientRect().top + this.#offset(g.el);
      slide(g.el, 'reorder', 0, (from - now) / (parseFloat(getComputedStyle(g.el).scale) || 1));
      const ms = timing('reorder', { doc: this.doc }).duration;
      const settle = { done: () => { clearTimeout(settle.timer); g.el.classList.remove('settling'); if (this.#settle === settle) { this.#settle = null; } } };
      this.#settle = settle;
      if (ms > 0) { settle.timer = setTimeout(settle.done, ms + 34); } else { settle.done(); }
    } else {
      g.el.classList.remove('settling');
    }
    swallow();
    if (hold && g.el.isConnected) { this.host.menu(g.card); return; }
    const where = g.el.isConnected ? this.where(g.el) : null;
    if (!where) { return; }
    const { group, index, n } = where;
    if (moved && to.group !== g.origin.group) {
      this.#say(`${group === 'pinned' ? 'Pinned' : 'Unpinned'}. Position ${index + 1} of ${n} in ${NAMES[group]}.`);
    } else if (moved) {
      this.#say(`Moved to position ${index + 1} of ${n} in ${NAMES[group]}.`);
    } else {
      this.#say(`${cancel || g.outside ? 'Cancelled. ' : ''}Back at position ${index + 1} of ${n}.`);
    }
  }

  // A child's layout top within the list (from its border edge): where its box is, less what a
  // transform's scale about its centre (a sink, a lift easing down) moves it. Read from its
  // rectangle rather than offsetTop, which rounds to whole pixels.
  #offset(el) {
    const r = el.getBoundingClientRect();
    const s = parseFloat(getComputedStyle(el).scale) || 1;
    return r.top - this.list.getBoundingClientRect().top - ((r.height / s) * (1 - s)) / 2;
  }

  // Its layout height, likewise.
  #height(el) {
    return el.getBoundingClientRect().height / (parseFloat(getComputedStyle(el).scale) || 1);
  }

  // A card's group (and what it is called) and place in it, as the list is now.
  where(el) {
    const kids = [...this.list.children];
    const grouped = kids.some((n) => n.matches('.list-group[data-group]'));
    const groupOf = (n) => (grouped && n.dataset.pinned === 'true' ? 'pinned' : 'sessions');
    const peers = kids.filter((n) => n.classList.contains('session') && groupOf(n) === groupOf(el));
    const index = peers.indexOf(el);
    return index < 0 ? null : { group: groupOf(el), name: NAMES[groupOf(el)], index, n: peers.length };
  }

  // Polite, and while a card is held at most every 500 ms: the latest place, not each one passed.
  #say(text, { held = false } = {}) {
    clearTimeout(this.#speak);
    const now = this.win.performance?.now() ?? Date.now();
    const wait = held ? this.#spoke + SAY_EVERY_MS - now : 0;
    const speak = () => { this.#spoke = this.win.performance?.now() ?? Date.now(); this.host.say(text); };
    if (wait > 0) { this.#speak = setTimeout(speak, wait); } else { speak(); }
  }

  // The click a drag's release brings is the drag's: it never opens a session, chooses a menu
  // item or presses an answer (ui.md §5.3: nothing lands on what was moved under the pointer). The
  // next press is the person's own, so it stops waiting for one then. g: the drag, when its
  // pointer or finger is still down (Escape, the window losing focus, its session ending): the
  // wait for the click starts only once that is released, however long it is held.
  #swallowClick(g = null) {
    this.#unswallow();
    const win = this.win;
    let timer = 0;
    // A key's or assistive technology's click (detail 0) is no release's: it goes through, and
    // the release's is still waited for.
    const eat = (e) => { if (e.detail === 0) { return; } e.preventDefault(); e.stopImmediatePropagation(); off(); };
    const ends = ['pointerup', 'pointercancel', 'touchend', 'touchcancel'];
    const up = (e) => {
      if (e.type.startsWith('pointer') ? e.pointerId !== g.id : !this.#own(g, e.changedTouches)) { return; }
      for (const type of ends) { win.removeEventListener(type, up, true); }
      timer = setTimeout(off, 600);
    };
    const off = () => {
      win.removeEventListener('click', eat, true);
      win.removeEventListener('pointerdown', off, true);
      for (const type of ends) { win.removeEventListener(type, up, true); }
      clearTimeout(timer);
      if (this.#swallow === off) { this.#swallow = null; }
    };
    win.addEventListener('click', eat, true);
    win.addEventListener('pointerdown', off, true);
    if (g) { for (const type of ends) { win.addEventListener(type, up, true); } } else { timer = setTimeout(off, 600); }
    this.#swallow = off;
  }

  #unswallow() { this.#swallow?.(); }
}
