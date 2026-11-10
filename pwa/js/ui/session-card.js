// One session, twice: its row in the sessions list, and its view in the session pane
// (docs/design/ui.md §1 principle 4, §6.1, §6.2, §6.4, §6.5; #179). Nothing expands in place:
// the row is two lines, and choosing it shows the view beside the list on a desktop, or pushes
// it on a phone.
//
// The row: the status glyph (aligned to line 1), the title, and at most two facts: the status
// words (or "Driven by an agent"), then the machine with several shown, or else the repository.
// The age trails it, or the new-activity dot does. The model, SHA, counts and pid are never on
// the row: they are in Details.
//
// The view: a header (title; one meta line of glyph · status · machine · repository; Pin,
// Details, and the inspector's toggle, or on a phone Back and an overflow menu), then the
// conversation (your last message as a bubble, or a prompt you didn't type as a collapsed line in
// agent colour (#489), the main agent's label and reply with its
// reasoning behind "Show reasoning", the question as a Needs you card) over the reply box; and
// the inspector, with Details (everything the row leaves out, and End session) and Tools. The
// inspector is a pane of its own at the three-pane width; below it, Tools is a tab beside
// Conversation and Details is the header's button.
//
// Delegates (ui.md §6.3, #176): at three panes a strip under the header, the delegate chosen
// there shown in the inspector in place of Details and Tools; below that a Delegates tab, whose
// list pushes the delegate chosen in its place; on a phone a chip row as well. The conversation
// says when the main agent's own delegates start and report back, each name opening the delegate.
// Nothing types to a delegate: its one action fills the reply box to the main agent.
//
// Status is a glyph and the words of ui.md §2.2 (js/status.js): no coloured edge or glow (#178).
import { h } from './dom.js';
import { designIcon, drivenBadge, glyph, setGlyph } from './glyph.js';
import { ago, localTime } from './time.js';
import { copyText, renderMarkdown } from '../markdown.js';
import { copyFeedback } from './copy-feedback.js';
import { ATTENTION_RULES, rowAt, sessionStatus, statusLabel } from '../status.js';
import { awayWords, detailRows } from '../details-rows.js';
import { QuestionPanel, describeAnswer, describeClosed, renderQuestion, renderResolved } from './question-view.js';
import { ReceiptClock, ReceiptTiming, LINGER_READ_MS, drawReceipt, drawn, receiptLive } from './receipt.js';
import { answerability } from '../answers.js';
import { escapedApprove } from '../escaped.js';
import { ReplyBox } from './reply-view.js';
import { EndSession } from './stop-view.js';
import { lastSegment, pathView, setFacts } from './meta.js';
import { RowMenu } from './row-menu.js';
import { DelegateDetail, DelegatesBlock, DelegateStrip } from './delegates-view.js';
import { aloud, backgroundCounts, delegateEvents, delegateFold, delegateList, eventParts, liveVoice, mainVoice } from '../attribution.js';
import { delegateOf } from '../needs-you.js';
import { enter, leave, scrollBehavior, slide, stopTravel } from './motion.js';
import { CollapsingHeader, PHONE } from './collapse-header.js';
import { CollapsingFoot } from './collapse-foot.js';
import { UNSUPPORTED_SESSIONS } from '../brand.js';
import { PromptMemory, disclosureName, promptOrigin } from '../prompt-origin.js';
import { holdKey } from './hold.js';
import { CONNECTION_CHANGED } from '../channels.js';
import { HistoryView } from './history-view.js';

const AGENT_NAME = { copilot: 'Copilot', claude: 'Claude', codex: 'Codex', unknown: 'Agent' };
// The Details rows whose values move with the clock: drawn again where they are, not as new nodes.
const CLOCK_ROWS = new Set(['status', 'since', 'started', 'updated']);
// Each session's delegate events as the person left them (ui.md §6.4): its fold (D3) open or
// closed, and the shared lines opened to name everyone. By machine and session, so a card drawn
// again (a machine back online, a list sent again) keeps them, for as long as the page is open.
const EVENT_STATE = new Map();
// Puts nodes in a parent in this order, moving none that are already so: a node moved loses focus.
function place(parent, nodes) {
  const now = [...parent.children];
  if (now.length === nodes.length && now.every((n, i) => n === nodes[i])) { return; }
  parent.replaceChildren(...nodes);
}
// A tool call's glyph: running turns, as working does; a finished one is done or failed.
const TOOL_GLYPH = { running: 'working', done: 'done', failed: 'failed' };
// How long a question's controls ignore a pointer's click once the question appears or is
// scrolled into view (ui.md §5.3, Scroll to a question). A click is often already on its way
// when the content under it changes: people take about a quarter of a second to see a change
// and more to stop a click, so twice that is kept. One click on a choice sends the answer, and
// a click aimed at the agent's last lines must not answer a question that moved under it. No
// motion token measures this, and reduced motion keeps it: a jump moves the content as surely.
// A key still acts at once, since nothing moves the focus to a question.
export const QUESTION_ARRIVAL_GUARD_MS = 500;
// A smooth scroll steps every frame (16 ms at 60 Hz, 33 ms on a phone saving power): a pause of
// several frames is a scroll come to rest. Short, so a scroll the person starts just after it
// isn't taken for part of it.
const SCROLL_SETTLED_MS = 100;
export const TABS = Object.freeze(['conversation', 'delegates', 'tools']);

// A question's or Needs you's answers that take a click (what their guards drop a pointer's on).
export const QUESTION_CONTROLS = 'button, input, select, label, summary, a';

// Whether the browser keeps what's on screen in place as content above it changes (scroll
// anchoring; WebKit doesn't): found once, by growing what is above a small scroller's top.
let anchorsAtAll = null;
function browserAnchors(doc) {
  if (anchorsAtAll !== null) { return anchorsAtAll; }
  try {
    const box = doc.createElement('div');
    box.style.cssText = 'position:fixed;left:-10000px;top:0;width:10px;height:10px;overflow:auto;visibility:hidden;pointer-events:none';
    const above = doc.createElement('div');
    above.style.height = '10px';
    const below = doc.createElement('div');
    below.style.height = '100px';
    box.append(above, below);
    doc.body.append(box);
    box.scrollTop = 20;
    void box.scrollTop;
    above.style.height = '30px';
    anchorsAtAll = box.scrollTop > 30;
    box.remove();
  } catch {
    anchorsAtAll = false;
  }
  return anchorsAtAll;
}

// Answers moved under a still pointer (#491): where each of controls() sits on screen is read
// whenever something that can move it changes size (holder, everything holding it, everything
// beside each of those, and also), and a move of more than a pixel calls moved(). That includes
// the browser pulling a scroll back as what it scrolls gets shorter at its end: the clamp is part
// of the same layout the size change is seen in, before its scroll event comes. A scroll only
// updates where they are: the person's own, and the browser's bringing a focused control into
// view, aren't moves under a pointer, and the app's own scrolls to a question or a row wait in
// their own right. Only answers shown are read: a hidden one has no place to keep. The guards'
// own attribute changes no size, so a guard can't feed itself; a read is one per frame at most.
// paused: nothing read or watched until track() (pause() again stops it).
export function watchMoves({ holder, controls, also = [], skip = () => false, moved, paused = false }) {
  const win = holder.ownerDocument?.defaultView ?? globalThis;
  let live = !paused;
  let was = null;
  // The answers' own question laying itself out after the person's click, key or typing in it
  // (an error under a field, Other's box opening): where they are now, not a move under a
  // pointer. Only for the frame that interaction is drawn in, and only what resized in or around
  // the question itself: anything else resizing then (a title, the response, a banner) is
  // nothing the person did, and still counts.
  let own = false;
  let acts = 0;
  // Something the app drew in or around the question (a redraw, a receipt's words): the next
  // frame is not the person's alone, whichever came first.
  let forced = false;
  let draws = 0;
  // Theirs only if the question itself resized, nothing but it and what holds it did, and nothing
  // outside the page's content changed with it (the window, or what a scroller shows, resized): a
  // window resized as the person types still moves the answers. A scroll pulled back because
  // their own question got shorter (an error going) stays theirs.
  const theirs = (entries, outside) => own && !forced && !outside && !!entries
    && entries.some((e) => holder.contains(e.target))
    && entries.every((e) => holder.contains(e.target) || e.target.contains?.(holder));
  let view = null;
  // Whether the last read saw the window or what a scroller shows change size: a clamp after it
  // isn't the person's, whatever they are doing in the question.
  let wasOutside = false;
  // What each scroller showed at the last read: a scroll event between reads updates the scroll
  // accounting, and mustn't hide from the next read that what a scroller shows changed size.
  let readInner = [];
  // How far everything holding the answers is scrolled and how big each is inside and out; for
  // each the holder's place doesn't pin (nothing fixed between them), how far the browser's
  // anchoring moved its scrolling to keep what's at its top in place as content above changed.
  // A change in scrolling that is all anchoring (read off what is at the top of the scroller,
  // which the browser keeps in place) is left for the read to find in place; any other is a
  // scroll someone made, and is taken off where the answers were rather than read afresh, which
  // would also take in a move laid out in the same frame (a title wrapping as the list
  // scrolls). A browser that doesn't anchor (WebKit) has its whole scroll taken off. A scroller
  // pulled back to a lower end (what it holds got shorter, or it got taller) slides the answers
  // under a still pointer: nothing of it is taken off, and the read finds the move, whichever
  // comes first, the scroll event or the read.
  const doc = holder.ownerDocument;
  const style = (el) => win.getComputedStyle?.(el);
  const pinnedIn = (el, from) => {
    for (let up = from; up && up !== el; up = up.parentElement) {
      const p = style(up)?.position;
      if (p === 'fixed' || p === 'sticky') { return true; }
    }
    return false;
  };
  // What el scrolls that is just inside its top edge, above the holder's middle: where the
  // browser's anchor is, or a box holding it.
  const topOf = (el, box) => {
    const at = holder.getBoundingClientRect();
    const y = (el === doc.documentElement ? 0 : Math.max(box + (el.clientTop || 0), 0)) + 1;
    for (const hit of doc.elementsFromPoint?.(at.left + at.width / 2, y) ?? []) {
      if (hit !== el && el.contains(hit) && !pinnedIn(el, hit)) { return hit; }
    }
    return null;
  };
  const scrolledBy = (before = []) => {
    const anchors = browserAnchors(doc);
    const by = [];
    let pinned = false;
    for (let below = holder; below.parentElement; below = below.parentElement) {
      const p = style(below)?.position;
      pinned ||= p === 'fixed';
      const el = below.parentElement;
      const top = el.scrollTop || 0;
      const height = el.scrollHeight || 0;
      const inner = el.clientHeight || 0;
      const carried = !pinned && height > inner;
      let probe = null;
      let probeAt = 0;
      let anchored = 0;
      if (carried && anchors && style(el)?.overflowAnchor !== 'none') {
        // A place in what el scrolls: the document's box moves with its own scrolling, an
        // element's doesn't.
        const box = el.getBoundingClientRect().top;
        const place = (x) => x.getBoundingClientRect().top - (el === doc.documentElement ? box : box - top);
        const [, , , , wasProbe, wasAt] = before[by.length] ?? [];
        if (wasProbe?.isConnected && el.contains(wasProbe)) { anchored = place(wasProbe) - wasAt; }
        probe = topOf(el, box);
        probeAt = probe ? place(probe) : 0;
      }
      by.push([top, height, inner, carried, probe, probeAt, anchored]);
    }
    return by;
  };  let wasBy = [];
  // A size change that resized nothing watched (the window taller where the page itself scrolls,
  // a margin, something positioned) brings no read of its own: the next frame reads instead,
  // after this frame's own read if there is one.
  let pending = false;
  const frame = (fn) => (win.requestAnimationFrame ? win.requestAnimationFrame(fn) : setTimeout(fn, 16));
  // It runs in a frame's callbacks, before that frame's resize read: while the person's own act
  // in the question holds, it waits, so what they did is read with what resized (and theirs).
  const later = () => {
    if (pending) { return; }
    pending = true;
    frame(() => frame(() => {
      if (!pending) { return; }
      if (own) {
        pending = false;
        later();
        return;
      }
      read(true);
    }));
  };
  // How far a scroll since the last look moved the answers on screen (see scrolledBy), and
  // whether anything scrolling changed size; null if they are held elsewhere now.
  const scrolledSince = (by) => {
    if (by.length !== wasBy.length) { return null; }
    let shift = 0;
    let resized = false;
    let clamped = false;
    by.forEach(([top, height, inner, carried, , , anchored], i) => {
      const [wasTop, wasHeight, wasInner, wasCarried] = wasBy[i];
      const sized = height !== wasHeight || inner !== wasInner;
      resized ||= sized;
      // One that scrolled the answers before, or was last seen past its end with a new size, still
      // can: pulled back to 0 as it stops overflowing (a read for that size may come first).
      if (top === wasTop || !(carried || wasCarried || wasBy[i][7] === true)) { return; }
      // Pulled back: the end came up past where it was scrolled to, seen with the change of size,
      // or already seen with it (see overEnd).
      const over = wasBy[i][7] === true;
      const pulled = top < wasTop && top >= height - inner - 1 && (over || (sized && height - inner < wasHeight - wasInner));
      // Nothing taken off; read in a moment, in case the read for the new size came first.
      if (pulled) {
        resized = true;
        clamped = true;
        return;
      }
      // All of it anchoring (nothing scrolled by anyone): left for the read to find in place,
      // less what rounding leaves over. Anything else (a 1 px step of a slow scroll included) is
      // all taken off as a scroll: when a scroll and anchoring come in one frame, where the
      // browser anchors after the scroll isn't known, so a move above the question then
      // (content growing as the person scrolls) takes its click away for a moment rather than
      // hiding a move between the anchor and the question.
      const all = Math.abs(anchored) > 0.5 && Math.abs(top - wasTop - anchored) <= 1;
      shift += all ? top - wasTop - anchored : top - wasTop;
    });
    return { shift, resized, clamped };
  };
  // Marks a scroller this look finds past its own end with a new size (or still past it since
  // one): a browser that laid out the size before it moved the scroll, so the pull back to the
  // end that follows is a clamp. An elastic bounce past the end (iOS) changes no size.
  const overEnd = (by) => {
    by.forEach((now, i) => {
      const [top, height, inner] = now;
      const was = by.length === wasBy.length ? wasBy[i] : null;
      now[7] = !!was && top > height - inner + 1 && (height !== was[1] || inner !== was[2] || was[7] === true);
    });
    return by;
  };
  const read = (count, entries = null) => {
    pending = false;
    if (!live) { return; }
    // A scroll can be laid out before its scroll event comes (made in a frame's own callbacks,
    // or by the compositor): what it moved is taken off here, so the event finds nothing left.
    const by = scrolledBy(wasBy);
    const since = was && scrolledSince(by);
    if (since?.shift) { was = was.map(([y, x]) => [y - since.shift, x]); }
    // Where each is across as well as down: a window narrowed or widened within one layout moves
    // a centred conversation sideways with nothing changing height.
    const at = controls().filter((b) => b.getClientRects().length > 0).map((b) => {
      const r = b.getBoundingClientRect();
      return [r.top, r.left];
    });
    const changed = was && (at.length !== was.length || at.some(([y, x], i) => Math.abs(y - was[i][0]) > 1 || Math.abs(x - was[i][1]) > 1));
    const size = [win.innerWidth, win.innerHeight];
    const outside = !view || size[0] !== view[0] || size[1] !== view[1]
      || by.some(([, , inner, carried], i) => carried && readInner.length === by.length && inner !== readInner[i]);
    const fired = !!(count && changed && !theirs(entries, outside));
    if (fired) { moved(); }
    view = size;
    wasOutside = outside;
    readInner = by.map(([, , inner]) => inner);
    was = at;
    wasBy = overEnd(by);
    return fired;
  };
  const sized = typeof ResizeObserver === 'function' ? new ResizeObserver((entries) => read(true, entries)) : null;
  const watched = new Set();
  const observe = (el) => {
    if (!live || !el || watched.has(el) || skip(el)) { return; }
    watched.add(el);
    sized?.observe(el);
  };
  // What holds it can be put on the page after this starts watching (a session's view is built
  // before it is shown): track() takes in whatever holds it now, and lets go of whatever has
  // left the page since, which nothing else would.
  const track = () => {
    live = true;
    for (const el of watched) {
      if (!el.isConnected) {
        watched.delete(el);
        sized?.unobserve(el);
      }
    }
    for (let up = holder; up; up = up.parentElement) {
      observe(up);
      for (const el of up.parentElement?.children ?? []) { observe(el); }
    }
    for (const el of also) { observe(el); }
  };
  if (live) { track(); }
  const scrolled = (e) => {
    if (!e.target?.contains?.(holder) || !was) { return; }
    const by = scrolledBy(wasBy);
    const since = scrolledSince(by);
    // Held elsewhere now (moved to another page): left for the next read.
    if (!since) {
      later();
      return;
    }
    if (since.shift) { was = was.map(([y, x]) => [y - since.shift, x]); }
    wasBy = overEnd(by);
    // A clamp is read now, not frames on: the answers it slid are under the pointer already. While
    // the person's own act in the question holds, it is left to the read with what resized, unless
    // that read saw the window or a scroller change size (then the clamp isn't theirs).
    if (since.clamped && (!own || wasOutside)) {
      read(true);
      return;
    }
    if (since.resized) { later(); }
  };
  win.addEventListener?.('scroll', scrolled, { capture: true, passive: true });
  return {
    track,
    observe,
    unobserve(el) {
      if (!el || !watched.delete(el)) { return; }
      sized?.unobserve(el);
    },
    // The person acted in the answers' own question: what it draws next is theirs. Each act holds
    // it for the frame it's drawn in, counted from the latest: an earlier act's frames ending
    // mustn't cut a later one's short.
    own() {
      own = true;
      const mine = ++acts;
      frame(() => frame(() => { if (mine === acts) { own = false; } }));
    },
    // The app drew something in or around the question itself: whatever moves in the frames it's
    // drawn in counts.
    notOwn() {
      forced = true;
      const mine = ++draws;
      frame(() => frame(() => { if (mine === draws) { forced = false; } }));
    },
    // Where they are now, as the place to measure moves from (a new set of answers drawn).
    settle() { was = null; read(false); },
    // Read now, as a click comes (#491): something laid out since the last frame's read (an update
    // in the task just before the click) is found before the click is let through, not a frame
    // later. Nothing the person did is exempt here. Says whether it found a move.
    check() { return read(true) === true; },
    // Nothing of it read or watched until track() (a session's view hidden while another shows).
    pause() {
      live = false;
      pending = false;
      was = null;
      wasBy = [];
      readInner = [];
      sized?.disconnect();
      watched.clear();
    },
    stop() {
      sized?.disconnect();
      watched.clear();
      win.removeEventListener?.('scroll', scrolled, { capture: true });
    },
  };
}

// An answer whose panel stays once its question has gone (SessionCard #leave): one on its way, and
// any outcome §6.5 draws on the panel. Once it has an outcome other than delivered the card resolves
// at once to a closed item (SessionCard #leave): "No longer waiting", neutral (ui.md §7).
const leaves = (entry) => entry.state === 'sending' || entry.state === 'waiting' || entry.state === 'failed'
  || (entry.state === 'done' && entry.outcome !== 'busy' && entry.outcome !== 'refused');

// How many closed items (questions the session stopped asking whose answer did not go through)
// a card keeps in its conversation, as the history keeps its latest (ui.md §6.4).
const CLOSED_KEPT = 20;

// Why nothing can be given for a session now, for under its controls, or null (#269): its
// machine is offline, reconnecting, back and its whole list not yet in, or no longer offering
// sessions. what: 'answered', 'replied to'... While the app is catching up after a return (#950) the
// hold says nothing: { updating, drawn }. An offline machine's reply box and question say so in one
// line in place of their actions (ui.md rule 8): { text, offline }.
export function staleNote(stale, machineName, what) {
  if (!stale) { return null; }
  if (stale.grace) { return { updating: true, drawn: stale.drawn === true }; }
  const name = machineName || 'The machine';
  if (stale.reason === 'offline') {
    return what === 'ended'
      ? `${name} is offline. This can't be ${what} from here until it is back.`
      : { text: `${name} is offline \u00b7 you can answer once it's back`, offline: true };
  }
  if (stale.reason === 'unsupported') { return `${name} ${UNSUPPORTED_SESSIONS} now, so this can't be ${what} from here.`; }
  const where = stale.reason === 'confirming' ? 'is back' : 'is reconnecting';
  return `${name} ${where}. This can be ${what} once it has sent its sessions again.`;
}

let seq = 0;

export class SessionCard {
  #s = null;
  #last = {};
  #history = { loaded: false, loading: false };
  #panel = null;
  #receipt = null;
  // Receipts that closed: the question went and the answer did not go through. They stay, in order,
  // above the receipt and the question, and are not dropped by the next question (#retireReceipt).
  #closed = [];
  // The panel of a question its host took away while its answer was still on its way (#leave).
  #leaving = null;
  #orphan = null;
  #reply = null;
  // The host the reply box is for, and what it held when it last went away (the machine
  // stopped taking replies for a moment, say), so it comes back with the words in it.
  #replyHost = null;
  #replyDraft = { hostId: null, text: '' };
  #stop = null;
  #stopHost = null;
  #selected = false;
  #pinned = false;
  #tab = 'conversation';
  // Why its machine's sessions can't be acted on now ({ reason, since }), or null (#269); and
  // the words that said so when last drawn, which move on with the clock.
  #stale = null;
  #shownStale = null;
  // Details' value cells by row, and the status words the header shows, which its Status row says.
  #factCells = null;
  #idCopy = null;
  #detailsStatus = null;
  #detailsAway = false;
  // The row's name but for its age, as #nameRow puts it together: { drawn, unsaid }.
  #rowName = { drawn: [], unsaid: [] };
  // The question last drawn (undefined before the first drawing), and whether a finger or a
  // mouse button is on the conversation: what decides a scroll to a question that arrives.
  #asked = undefined;
  #touches = false;
  #pressing = false;
  // A mouse or pen over the conversation: someone reading it, perhaps about to click.
  #hovering = false;
  // Whether the conversation is read to its end (ui.md §5.3, Scroll to a question), and where it
  // was scrolled to when that was measured: kept while only its content changes, since a response
  // growing below isn't followed and so doesn't take the reader from its end; measured again once
  // it has scrolled since (read from scrollTop, before its scroll event has come). Forgotten (null)
  // while it isn't on screen, and when it may come back, where the browser can have moved it.
  #readToEnd = null;
  #readAt = 0;
  // Until when the question's controls ignore a pointer's click (QUESTION_ARRIVAL_GUARD_MS).
  #guardUntil = 0;
  // The question's view the guard covers, and the question it was armed for.
  #guarded = null;
  #guardedId = null;
  // How the guarded question was last drawn: held (a #269 note, or null) and answerable here.
  #guardedHeld = null;
  #guardedAnswerable = false;
  #guardTimer = null;
  // Listening for the end of a smooth scroll to the question, until it ends or is replaced.
  #scrollEnd = null;
  // A question that arrived in the drawing under way, to bring into sight once it is all drawn.
  #arrived = null;
  // What moves the guarded question's answers on screen while it stays (#491).
  #moves = null;
  #menu = new RowMenu();
  // The delegate open, in the inspector or pushed in the Delegates tab.
  #delegate = null;
  // Whether the inspector was a pane of its own at the last layout.
  #wasInspector = false;
  // A prompt the person didn't type (#489): what this view saw of the session's prompts, to fold
  // a schedule's repeats and date them; whether Show prompt is open, and when the prompt shown
  // began (null where unknown).
  #prompts = new PromptMemory();
  #promptOpen = false;
  // The keys of the delegate event lines drawn, or null before the first: only a line arriving
  // after that fades in.
  #eventKeys = null;
  #promptAt = null;
  // The Delegates tab's count on a phone (#629): what the last update counted, and the width that shows it.
  #tabCounts = { running: 0, asking: 0 };
  #phone = typeof window !== 'undefined' && window.matchMedia ? window.matchMedia(PHONE) : null;
  #onPhone = () => this.#renderTabCount();
  // hostId: the machine the session is on, which everything the view sends goes to.
  // fetchDetail(id) -> Promise<Session|null>, for the tool history (session.get).
  // answering() -> { service, replies, stops, hostId, capabilities } | null: who answers
  // questions, sends replies and ends sessions, and whether the session's machine takes each at
  // all. labelled: whether the row names its machine, as it does with every machine shown.
  // onSelect(card): its row was chosen. onPin(card, pinned), onBack(card): the header's Pin and
  // (on a phone) Back. layout() -> { inspector, wide }: whether the inspector is a pane of its own
  // now, and whether the window is wide enough for one (the strip's width).
  // stale: why its machine's sessions can't be acted on now ({ reason, since }), or null.
  constructor(session, { hostId = null, history = null, fetchDetail, fetchDelegate = null, machineName, labelled = false, answering = () => null, onSelect = () => {}, onPin = () => {}, onBack = () => {}, layout = () => ({ inspector: false, wide: false }), viewing = () => true, stale = null }) {
    Object.assign(this, { hostId, historyStore: history, fetchDetail, fetchDelegate, machineName, labelled, answering, onSelect, onPin, onBack, layout, viewing });
    this.#stale = stale;
    const n = ++seq;
    this.ids = { row: `s${n}-row`, glyph: `s${n}-glyph`, unread: `s${n}-unread`, title: `s${n}-title`, conversation: `s${n}-conversation`, tools: `s${n}-tools`, details: `s${n}-details`, tabC: `s${n}-tab-c`, tabD: `s${n}-tab-d`, tabT: `s${n}-tab-t`, delegates: `s${n}-delegates`, delegate: `s${n}-delegate`, prompt: `s${n}-prompt`, events: `s${n}-events` };
    this.#buildRow();
    this.#buildView();
    this.update(session);
  }

  get id() { return this.#s.id; }
  get session() { return this.#s; }
  get selected() { return this.#selected; }
  get tab() { return this.#tab; }

  #buildRow() {
    const e = this.els = {};
    e.rowGlyph = glyph('idle', { size: 16 });
    e.rowGlyph.id = this.ids.glyph;
    e.rowTitle = h('span', { class: 'row-title' });
    e.rowLine1 = h('span', { class: 'row-line1' }, e.rowTitle);
    e.rowFacts = h('span', { class: 'row-facts' });
    e.rowAge = h('time', { class: 'row-age' });
    // Named by #nameRow (ui.md §9.1 A1). The spaces between its lines draw nothing in the grid,
    // but without them the title, the facts and the age read as run-together words
    // ("…AppWaiting on you"), which no name can contain (axe label-content-name-mismatch, #365).
    e.open = h('button', { type: 'button', class: 'row-main', id: this.ids.row, onclick: () => this.onSelect(this) },
      h('span', { class: 'row-glyph' }, e.rowGlyph), ' ',
      h('span', { class: 'row-text' }, e.rowLine1, ' ', e.rowFacts), ' ',
      e.rowAge);
    this.element = h('div', { class: 'session' }, e.open);
  }

  #buildView() {
    const e = this.els;
    // The header (ui.md §6.2).
    e.back = h('button', { type: 'button', class: 'icon-button back', 'aria-label': 'Sessions', title: 'Sessions', onclick: () => this.onBack(this) }, designIcon('back', { cls: 'ab-icon-20' }));
    e.titleText = h('h2', { class: 'session-title', id: this.ids.title });
    e.glyph = glyph('idle', { size: 16 });
    e.statusText = h('span', { class: 'status-text' });
    e.meta = h('span', { class: 'meta-facts' });
    e.statusLine = h('p', { class: 'status-line' }, e.glyph, e.meta);
    e.pin = h('button', { type: 'button', class: 'icon-button pin', 'aria-pressed': 'false', 'aria-label': 'Pin', title: 'Pin', onclick: () => this.onPin(this, !this.#pinned) }, designIcon('pin', { cls: 'ab-icon-20' }));
    e.detailsButton = h('button', { type: 'button', class: 'icon-button details-button', 'aria-pressed': 'false', 'aria-controls': this.ids.details, 'aria-label': 'Details', title: 'Details', onclick: () => this.showDetails() }, designIcon('inspector', { cls: 'ab-icon-20' }));
    e.inspectorButton = h('button', { type: 'button', class: 'icon-button inspector-button', 'aria-label': 'Hide inspector', title: 'Hide inspector', onclick: () => this.onInspector?.() }, designIcon('sidebar', { cls: 'ab-icon-20' }));
    e.more = h('button', { type: 'button', class: 'icon-button more', 'aria-haspopup': 'menu', 'aria-label': 'More', title: 'More', onclick: () => this.#openMore() }, designIcon('more', { cls: 'ab-icon-20' }));
    e.actions = h('div', { class: 'header-actions' }, e.pin, e.detailsButton, e.inspectorButton);
    e.header = h('header', { class: 'session-header' }, h('div', { class: 'header-main' }, e.titleText, e.statusLine), e.actions);
    // The pinned bar of a phone's collapsing header (ui.md §6.2): Back and More are the bar's, always
    // in reach and first in the tab order. Its glyph and title are drawn, not read: the status is
    // read from the meta line, and the h2 is the one heading. From 760 the bar is not shown.
    e.barGlyph = glyph('idle', { size: 16 });
    e.barTitle = h('span', { class: 'bar-title-text' });
    e.bar = h('div', { class: 'session-bar' }, e.back, h('div', { class: 'bar-title', 'aria-hidden': 'true' }, e.barGlyph, e.barTitle), e.more);
    // The delegate strip (§6.3): at three panes under the header; on a phone a chip row.
    // Its item pressed again lets the delegate go, if it is on screen. Out of sight, it shows it:
    // the inspector hidden, or (with no inspector pane) another tab chosen over it, which a tab
    // chosen before the window narrowed leaves too.
    this.strip = new DelegateStrip({
      onSelect: (id) => {
        if (id !== null) { this.openDelegate(id); return; }
        const { inspector, wide } = this.layout();
        if ((wide && !inspector) || (!inspector && this.#tab !== 'delegates')) { this.openDelegate(this.#delegate); } else { this.closeDelegate(); }
      },
    });

    // Conversation / Delegates / Tools, where the inspector isn't a pane of its own (ui.md §6.3).
    // Delegates is there only while the host lists any.
    e.tabC = h('button', { type: 'button', role: 'tab', class: 'view-tab', id: this.ids.tabC, 'aria-controls': this.ids.conversation, onclick: () => this.setTab('conversation') }, 'Conversation');
    // On a phone, with no strip, the tab carries what the strip did (#629): "Delegates 2" while two
    // run, led by the needs glyph while one asks, and named for both. In place: never a new height.
    e.tabNeeds = glyph('needs', { size: 16 });
    e.tabNeeds.setAttribute('hidden', '');
    e.tabCount = h('span', { class: 'tab-count', 'aria-hidden': 'true' });
    e.tabD = h('button', { type: 'button', role: 'tab', class: 'view-tab', id: this.ids.tabD, 'aria-controls': this.ids.delegates, hidden: true, onclick: () => this.setTab('delegates') }, e.tabNeeds, 'Delegates', e.tabCount);
    e.tabT = h('button', { type: 'button', role: 'tab', class: 'view-tab', id: this.ids.tabT, 'aria-controls': this.ids.tools, onclick: () => this.setTab('tools') }, 'Tools');
    e.tabs = h('div', { class: 'view-tabs', role: 'tablist', 'aria-label': 'Session' }, e.tabC, e.tabD, e.tabT);
    e.tabs.addEventListener('keydown', (ev) => {
      if (!['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(ev.key)) { return; }
      ev.preventDefault();
      const tabs = [['conversation', e.tabC], ['delegates', e.tabD], ['tools', e.tabT]].filter(([, el]) => !el.hidden);
      const i = Math.max(0, tabs.findIndex(([t]) => t === this.#tab));
      // Left and Right wrap; Home and End go to the first and last (ui.md §6.2, #629).
      const to = ev.key === 'Home' ? 0 : ev.key === 'End' ? tabs.length - 1 : (i + (ev.key === 'ArrowRight' ? 1 : -1) + tabs.length) % tabs.length;
      const [next, el] = tabs[to];
      this.setTab(next);
      el.focus();
    });

    // The conversation (ui.md §6.4): one voice per line, the main agent's.
    e.userText = h('p', { class: 'plain' });
    e.user = h('div', { class: 'user-message', hidden: true }, h('span', { class: 'sr-only' }, 'Your last message: '), h('div', { class: 'bubble' }, e.userText));
    // A prompt the person didn't type (#489, §6.4): never a bubble. At the column's start, a label
    // line in agent colour (a clock for a schedule, the driven diamond for an agent), read before
    // the prompt; then two lines of it, and Show prompt for the rest, inside an agent rule.
    e.promptIcon = h('span', { class: 'prompt-icon' });
    e.promptFacts = h('span', { class: 'prompt-facts' });
    e.promptBy = h('span', { class: 'prompt-by', hidden: true });
    e.promptAge = h('time', { class: 'prompt-age' });
    e.promptLabel = h('p', { class: 'prompt-label' }, e.promptIcon, e.promptFacts, e.promptBy, e.promptAge);
    e.promptPreview = h('p', { class: 'prompt-preview' });
    e.promptToggleText = h('span', null, 'Show prompt');
    e.promptToggle = h('button', { type: 'button', class: 'prompt-toggle', 'aria-expanded': 'false', 'aria-controls': this.ids.prompt, onclick: () => this.#setPromptOpen(!this.#promptOpen) },
      designIcon('chevron', { size: 16, cls: 'prompt-chevron' }), e.promptToggleText);
    e.promptText = h('p', { class: 'plain' });
    e.promptFull = h('div', { class: 'prompt-full', id: this.ids.prompt, hidden: true }, e.promptText);
    e.prompt = h('div', { class: 'prompt-origin', hidden: true }, e.promptLabel, e.promptPreview, e.promptToggle, e.promptFull);
    e.agentAge = h('time', { class: 'speaker-age' });
    // "Main agent" where the host says whose words these are (attribution.js); a host that doesn't
    // may have a delegate's here, so they are only the agent's, by its name.
    e.voice = h('span', { class: 'speaker-name voice-label', hidden: true }, 'Main agent');
    e.agentLabel = h('span', { class: 'speaker-name' });
    e.speaker = h('p', { class: 'speaker' }, e.voice, e.agentLabel, e.agentAge);
    e.reasoningText = h('p', { class: 'plain' });
    e.reasoning = h('details', { class: 'reasoning', hidden: true }, h('summary', null, 'Show reasoning'), e.reasoningText);
    e.reasoning.addEventListener('toggle', () => { e.reasoning.querySelector('summary').textContent = e.reasoning.open ? 'Hide reasoning' : 'Show reasoning'; });
    e.truncated = h('p', { class: 'note truncated', hidden: true }, 'Earlier text not shown: the response was longer than the machine sends.');
    e.dropped = h('p', { class: 'note dropped', hidden: true }, 'The main agent\'s latest response was left out: too long to send with its delegates.');
    e.response = h('div', { class: 'md response' });
    e.toolName = h('span', { class: 'tool-name' });
    e.toolSummary = h('span', { class: 'tool-summary' });
    e.toolGlyph = glyph('working', { size: 12 });
    e.tool = h('p', { class: 'tool-line', hidden: true }, e.toolGlyph, e.toolName, e.toolSummary);
    // Its own delegates starting and reporting back (§6.4, #661): one meta line per delegate, the
    // name opening it; after its words and before what it is running now. Two or more lines
    // fold into one (D3): its button, the chevron ending it, then a link of its own to the
    // earliest delegate still working.
    e.events = h('ol', { class: 'delegate-events', 'aria-label': 'Delegates started and finished', hidden: true });
    e.foldText = h('span', { class: 'delegate-fold-label' });
    // The button's name is the whole summary (aria-label); the chevron only shows which way it is.
    e.foldToggle = h('button', { type: 'button', class: 'delegate-fold-toggle', 'aria-expanded': 'false', 'aria-controls': this.ids.events, onclick: () => this.#toggleEventFold() },
      e.foldText, h('span', { class: 'delegate-fold-chevron', 'aria-hidden': 'true' }, designIcon('chevron', { size: 16 })));
    e.foldWorking = h('button', { type: 'button', class: 'delegate-link delegate-fold-working', hidden: true, onclick: () => this.openDelegate(e.foldWorking.dataset.id) });
    e.foldLines = h('ol', { class: 'delegate-fold-lines', id: this.ids.events, hidden: true });
    e.fold = h('li', { class: 'delegate-fold' },
      h('div', { class: 'delegate-fold-line' },
        h('span', { class: 'delegate-event-mark' }, designIcon('delegate', { size: 16 })),
        h('span', { class: 'delegate-fold-text' }, e.foldToggle, e.foldWorking)),
      e.foldLines);
    e.agentBlock = h('div', { class: 'agent-block' }, e.speaker, e.reasoning, e.truncated, e.dropped, e.response, e.events, e.tool);
    e.question = h('div', { class: 'question-slot' });
    this.#guardQuestion(e.question);
    e.stream = h('div', { class: 'stream' }, e.user, e.prompt, e.agentBlock, e.question);
    e.reply = h('div', { class: 'reply-slot' });
    // A phone's compact reply area (CollapsingFoot, ui.md §6.5): a pinned bar holding a copy of the
    // box, drawn, not read, and no tab stop; a tap on it brings the box back, focused.
    e.replyCopy = h('div', { class: 'reply-copy', 'aria-hidden': 'true' }, h('span'));
    e.replyBar = h('div', { class: 'reply-bar' }, e.replyCopy);
    this.turns = new HistoryView();
    e.scroll = h('div', { class: 'stream-scroll' }, this.turns.element, e.stream);
    this.#watchHold(e.scroll);
    // An open question out of sight (#1657): one tap brings its card into view.
    e.waiting = h('button', { type: 'button', class: 'question-waiting', hidden: true, onclick: () => this.#showWaiting() }, 'Question waiting');
    // Watched, not measured: a read of the layout here would spend the one the move watch owes.
    if (typeof IntersectionObserver === 'function') {
      new IntersectionObserver((entries) => { this.#questionSeen = entries.at(-1).isIntersecting; this.#syncWaiting(); }, { root: e.scroll }).observe(e.question);
    }
    e.conversation = h('div', { class: 'conversation', id: this.ids.conversation, role: 'tabpanel', 'aria-labelledby': this.ids.tabC },
      e.scroll, e.waiting, e.reply, e.replyBar);

    // The inspector: Details, then Tools.
    e.facts = h('dl', { class: 'facts' });
    // End session lives in Details (ui.md §6.2), last and furthest from Send.
    e.stop = h('div', { class: 'stop-slot' });
    e.details = h('section', { class: 'details', id: this.ids.details, 'aria-labelledby': `${this.ids.details}-h` },
      e.detailsHead = h('h3', { class: 'section-head', id: `${this.ids.details}-h`, tabindex: '-1' }, 'Details'), e.facts, e.stop);
    e.historyBody = h('div', { class: 'history-body' });
    e.tools = h('section', { class: 'tools-panel', id: this.ids.tools, role: 'tabpanel', 'aria-labelledby': this.ids.tabT },
      h('h3', { class: 'section-head' }, 'Tools'), e.historyBody);
    // Delegates (#176, ui.md §6.3): each under its own name, never in the main agent's place. The
    // Delegates tab lists them (or, from a host that lists none, says how many there are); the one
    // opened shows in the inspector, or pushed in the list's place, its own words fetched from the
    // machine (session.get with agent). Nothing here replies to a delegate.
    this.delegates = new DelegatesBlock({ onOpen: (id) => this.openDelegate(id) });
    e.delegateCounts = h('p', { class: 'note delegate-counts', hidden: true });
    e.delegates = h('section', { class: 'delegates-panel', id: this.ids.delegates, role: 'tabpanel', 'aria-labelledby': this.ids.tabD, hidden: true }, this.delegates.element, e.delegateCounts);
    this.detail = new DelegateDetail({
      fetchDelegate: this.fetchDelegate ? (agent) => this.fetchDelegate(this.#s.id, agent) : null,
      onOpen: (id) => this.openDelegate(id),
      onAsk: (text) => this.#askToStop(text),
      onBack: () => this.closeDelegate({ focus: true }),
      canAsk: () => !!this.#reply,
      idBase: this.ids.delegate,
    });
    // The delegate's pane is in the document only while one is open.
    e.inspector = h('aside', { class: 'inspector', 'aria-label': 'Inspector' }, e.details, e.delegates, e.tools);

    // The header floats over the conversation on a phone (CollapsingHeader); the bar first, so Back and
    // More come before the tabs.
    e.top = h('div', { class: 'session-top' }, e.header, this.strip.element, e.tabs);
    e.head = h('div', { class: 'session-head' }, e.bar, e.top);
    this.view = h('section', { class: 'session-view', 'aria-labelledby': this.ids.title, hidden: true, dataset: { tab: 'conversation' } },
      e.head, h('div', { class: 'view-body' }, e.conversation, e.inspector));
    const foot = new CollapsingFoot({ view: this.view, slot: e.reply, bar: e.replyBar, copy: e.replyCopy, scroller: e.scroll });
    this.collapse = new CollapsingHeader({ view: this.view, head: e.head, top: e.top, bar: e.bar, scroller: e.scroll, foot });
    this.foot = foot;
    this.#phone?.addEventListener('change', this.#onPhone);
    this.#applyTab();
    // A question that stays where it is can still move (#491): a title wrapping, a tool line
    // coming or going, the response or reasoning or your message changing above it, a note,
    // banner or app bar above the view, a list shrinking at its end. Each move is the same wait as
    // a question arriving.
    this.#moves = watchMoves({
      holder: e.question,
      controls: () => (this.#selected && this.#guarded?.isConnected ? [...this.#guarded.querySelectorAll(QUESTION_CONTROLS)] : []),
      // Other sessions' views: hidden whenever this one shows, so they never move its question,
      // and holding on to them would keep a removed session's view alive.
      skip: (el) => el !== this.view && el.classList?.contains('session-view'),
      moved: () => this.#movedUnder(),
      // Read only while its view is the one shown.
      paused: !this.#selected,
    });
  }

  #movedUnder() {
    if (this.#guarded?.isConnected && this.#selected && this.viewing() && this.els.scroll.getClientRects().length) { this.#guard(); }
  }

  #changed(key, value) {
    const v = typeof value === 'string' ? value : JSON.stringify(value);
    if (this.#last[key] === v) { return false; }
    this.#last[key] = v;
    return true;
  }

  update(s) {
    const first = this.#s === null;
    // Whether the conversation is read to its end, before this update draws anything into it.
    const atEnd = this.#reading();
    this.#s = s;
    this.historyStore?.live(this.hostId, s);
    if (this.#unwatch) { this.#drawTurns(); }
    const e = this.els;
    const state = sessionStatus(s);
    for (const el of [this.element, this.view]) {
      el.dataset.id = s.id;
      if (this.hostId) { el.dataset.hostId = this.hostId; }
      el.dataset.status = s.status;
      el.dataset.agent = s.agent;
      el.dataset.view = state.view;
      el.dataset.rule = String(state.rule);
      // An agent launched it, or answered or replied to it, and nobody has taken it back since.
      el.dataset.driver = state.driven ? 'agent' : 'person';
    }
    const title = s.title || 'Untitled session';
    const where = s.repository ?? lastSegment(s.cwd);
    const whereView = () => (s.repository ? h('span', { class: 'repo' }, s.repository) : where ? h('span', { class: 'repo mono' }, where) : null);
    // Its machine away (#269, ui.md §2.2): none of the rules apply, since what the row last said
    // may no longer be so. The failed glyph (a host unreachable), and in secondary text that it
    // is offline and since when, or reconnecting; no driven overlay.
    const stale = this.#staleText();
    this.#shownStale = stale?.join(' ') ?? null;
    for (const el of [this.element, this.view]) {
      if (this.#stale && !this.#stale.grace) { el.dataset.stale = this.#stale.reason; } else { delete el.dataset.stale; }
    }
    const away = stale?.join(' \u00b7 ');
    const words = stale ? { ...state, glyph: 'failed', driven: false, facts: stale, text: away, tone: 'muted', spoken: away.charAt(0).toLowerCase() + away.slice(1) } : state;
    this.#detailsStatus = stale ? words.text : null;
    this.#detailsAway = !!stale;

    // The row.
    e.rowTitle.textContent = title;
    setGlyph(e.rowGlyph, words.glyph, { size: 16, driven: words.driven, label: statusLabel(words, { title: s.title, machine: this.machineName }) });
    // Rule 7 on a row: "Driven by an agent" in agent colour, in place of the status words; not over
    // rules 10-12 (#800), which say what is wrong. The glyph keeps the driven badge either way.
    const drivenDrawn = words.driven && !ATTENTION_RULES.has(words.rule);
    if (this.#changed('row', [words.text, words.tone, words.driven, words.rule, s.driverPrincipal, where, s.repository, this.labelled ? this.machineName : null])) {
      const status = drivenDrawn
        ? h('span', { class: 'row-status driver', dataset: { tone: 'agent' }, title: s.driverPrincipal ?? null }, 'Driven by an agent')
        : h('span', { class: 'row-status', dataset: { tone: words.tone } }, words.facts[0]);
      setFacts(e.rowFacts, [status, this.labelled && this.machineName ? h('span', { class: 'machine-label' }, this.machineName) : whereView()]);
    }
    // The row's name (ui.md §9.1 A1, #365) starts with the words it draws, in the order drawn:
    // its title, its second line's facts, then its age (#nameRow). What else §9.2's name holds
    // follows, so nothing it said before goes unsaid: the status's later facts, or all of it
    // under "Driven by an agent", the driver under a rule 10-12 status drawn in its place, and
    // its machine while the row doesn't show it. Both halves key off drivenDrawn, as the row
    // does, or a driven row needing sign-in reads "Driven by an agent" over "Needs sign-in".
    // A status fact can carry a delegate's name, read without its commas there, as in the
    // glyph's name (§9.2).
    this.#rowName = {
      drawn: [title, drivenDrawn ? 'Driven by an agent' : aloud(words.facts[0]), this.labelled && this.machineName ? this.machineName : where],
      unsaid: [
        drivenDrawn ? words.spoken : words.facts.slice(1).map(aloud).join(' \u00b7 '),
        words.driven && !drivenDrawn ? 'driven by an agent' : null,
        this.labelled ? null : this.machineName,
      ],
    };

    // The header: one meta line, glyph · status · machine · repository.
    e.titleText.textContent = title;
    e.barTitle.textContent = title;
    setGlyph(e.glyph, words.glyph, { size: 16, driven: words.driven });
    setGlyph(e.barGlyph, words.glyph, { size: 16, driven: words.driven });
    if (this.#changed('meta', [words.text, words.tone, words.driven, s.driverPrincipal, this.machineName, where, s.repository])) {
      e.statusText.dataset.tone = words.tone;
      setFacts(e.statusText, words.facts);
      const driver = words.driven ? h('span', { class: 'driver', title: s.driverPrincipal ?? null }, 'Driven by an agent') : null;
      setFacts(e.meta, [e.statusText, driver, this.machineName ? h('span', { class: 'session-machine' }, this.machineName) : null, whereView()]);
    }
    // The main agent's words dated by when it wrote them, not by a delegate's later update.
    const said = mainVoice(s).at ?? s.updatedAt;
    e.agentAge.dateTime = said;
    e.agentAge.title = localTime(said);
    e.rowAge.dateTime = rowAt(s);
    e.rowAge.title = localTime(rowAt(s));
    this.refreshTimes();

    const t = s.activity.currentTool;
    e.tool.hidden = !t;
    if (t) {
      e.tool.dataset.status = t.status;
      // Named for its state: the words beside it are the tool's name and summary (ui.md §9).
      setGlyph(e.toolGlyph, TOOL_GLYPH[t.status], { size: 12, label: t.status });
      e.toolName.textContent = t.name;
      e.toolSummary.textContent = t.summary ?? '';
      e.toolSummary.title = t.summary ?? '';
    }

    this.#renderQuestion(s);
    this.#syncWaiting();
    this.#renderReply(s);
    this.#renderStop(s);
    // The main agent's own words only (ui.md §1 principle 3); a delegate's are on its own row.
    const voice = liveVoice(s);
    if (this.#changed('response', voice.response ?? '')) {
      e.response.replaceChildren(renderMarkdown(voice.response ?? ''));
    }
    e.response.hidden = !voice.response;
    e.truncated.hidden = !voice.responseTruncated;
    e.dropped.hidden = !voice.responseDropped;
    e.reasoning.hidden = !voice.reasoning;
    e.reasoningText.textContent = voice.reasoning ?? '';
    e.voice.hidden = !voice.attributed;
    e.agentLabel.hidden = voice.attributed;
    e.agentLabel.textContent = AGENT_NAME[s.agent] ?? AGENT_NAME.unknown;
    this.#renderDelegates(s);
    e.agentBlock.hidden = !voice.response && !voice.responseDropped && !voice.reasoning && !t && e.events.hidden;
    const echoed = (s.activity.userMessage ?? '') !== e.userText.textContent;
    e.user.hidden = !s.activity.userMessage;
    e.userText.textContent = s.activity.userMessage ?? '';
    this.#renderPrompt(s);
    // Send acknowledged (ui.md §5.3): your message, back from the machine, rises into place; a
    // prompt you didn't type is no bubble of yours and never rises.
    if (!first && echoed && !e.user.hidden) { enter(e.user, 'acknowledge', { dy: 1 }); }

    // The rows, as the shared table sets them. What moves with the clock is drawn again by the
    // tick, in place, so only the rest, and which rows there are, decide a redraw. Started's own time
    // is in the key: another start is another <time>.
    const rows = detailRows(s, { status: stale ? words.text : null, machine: this.machineName || null, away: !!stale });
    if (this.#changed('facts', rows.map((r) => (CLOCK_ROWS.has(r.kind) ? [r.kind, r.at] : [r.kind, r.text])))) {
      this.#renderFacts(rows);
    }
    this.#tickFacts(s, Date.now());
    this.#bringQuestion(atEnd);

    // The tools shown follow the session: refetched (at most every 2 s) as it changes.
    if (!first && this.#history.loaded && this.#toolsShown() && this.#changed('historyAt', s.updatedAt)) { this.#scheduleHistory(); }
  }

  // The strip, the Delegates tab and the delegate open (§6.3), and the conversation's events.
  #renderDelegates(s, now = Date.now()) {
    const e = this.els;
    // Every update, open or not: the detail remembers what a trimmed list leaves out.
    if (!this.detail.update(s, now) && this.#delegate) { this.closeDelegate(); }
    this.strip.update(s, now, this.#delegate);
    this.delegates.update(s, now, this.#delegate);
    const listed = !this.delegates.element.hidden;
    // With none listed, a host says only how many (rule 8): Claude, Codex, one before sub-agents,
    // or one whose list is empty, as status rule 8 and the app take it.
    const counts = listed ? null : backgroundCounts(s);
    e.delegateCounts.textContent = counts ?? '';
    e.delegateCounts.hidden = !counts;
    e.delegates.hidden = !listed && !counts;
    e.tabD.hidden = e.delegates.hidden;
    // Running as the strip counts them, the main agent's own; asking at any depth, since a
    // question waits on the person whoever asks it.
    const d = listed ? delegateList(s) : null;
    this.#tabCounts = d
      ? { running: d.active.filter((r) => r.running && (!r.parent || r.parent === r.id)).length, asking: d.active.filter((r) => r.status === 'waiting').length }
      : { running: 0, asking: 0 };
    this.#renderTabCount();
    if (e.tabD.hidden && this.#tab === 'delegates') { this.setTab('conversation'); }
    this.#linkDelegatesTab();
    this.#renderEvents(s, now);
  }

  // From 760 the strip says all this, and the tab is plain "Delegates".
  #renderTabCount() {
    const e = this.els;
    const { running, asking } = this.#phone?.matches ? this.#tabCounts : { running: 0, asking: 0 };
    // The leading space keeps the visible text "Delegates 2", within the name (WCAG 2.5.3); the
    // flex row draws only the gap.
    e.tabCount.textContent = running ? ` ${running}` : '';
    // An <svg> has no hidden property; the attribute is what app.css hides.
    e.tabNeeds.toggleAttribute('hidden', !asking);
    const said = [running && `${running} running`, asking && `${asking} asking`].filter(Boolean);
    if (said.length) { e.tabD.setAttribute('aria-label', ['Delegates', ...said].join(', ')); } else { e.tabD.removeAttribute('aria-label'); }
  }

  // Pushed in the list's place, the delegate open is what the Delegates tab shows: the tab
  // controls it, and it is the tab's panel, named by its own heading. In the inspector it is a
  // pane of its own, and the tab (where there is one) keeps the list.
  #linkDelegatesTab() {
    const pushed = !!this.#delegate && !this.layout().inspector;
    const pane = this.detail.element;
    pane.id = this.ids.delegate;
    this.els.tabD.setAttribute('aria-controls', pushed ? this.ids.delegate : this.ids.delegates);
    if (pushed) { pane.setAttribute('role', 'tabpanel'); } else { pane.removeAttribute('role'); }
  }

  #eventState() {
    const key = `${this.hostId ?? ''}|${this.#s?.id ?? ''}`;
    if (!EVENT_STATE.has(key)) { EVENT_STATE.set(key, { open: false, more: new Set() }); }
    return EVENT_STATE.get(key);
  }

  // The fold opened or closed (Disclosure, ui.md §5.3): its space opens at once and app.css
  // settles its lines in; focus stays on its button.
  #toggleEventFold() {
    const st = this.#eventState();
    st.open = !st.open;
    this.#renderEvents(this.#s);
  }

  // Drawn again as they change and as their ages move on, line by line by key (D2): a line that
  // changes is the same line, its words changed at once and its glyph cross-faded (setGlyph), and
  // only a line arriving fades in, so nothing beside it slides. A control with keyboard focus
  // keeps it, or gives it to where its line went.
  #renderEvents(s, now = Date.now()) {
    const e = this.els;
    const lines = delegateEvents(s, now);
    const fold = delegateFold(lines);
    const st = this.#eventState();
    if (!this.#changed('events', [lines, fold, st.open, [...st.more]])) { return; }
    const had = e.events.contains(document.activeElement) ? document.activeElement : null;
    const where = had && { key: had.closest('.delegate-event')?.dataset.key ?? null, id: had.dataset.id ?? null, more: had.classList.contains('delegate-more') };
    const old = new Map([...e.events.querySelectorAll('li.delegate-event')].map((li) => [li.dataset.key, li]));
    const drawn = this.#eventKeys;
    const keys = new Set(lines.map((l) => l.key));
    // A line keyed anew (a start another joined) is the same line, its old key no longer drawn.
    const was = (line) => (line.was && !keys.has(line.was) ? line.was : null);
    const items = lines.map((line) => this.#eventLine(old.get(line.key) ?? old.get(was(line)), line, st));
    this.#eventKeys = keys;
    if (fold) {
      e.foldText.textContent = fold.button;
      e.foldToggle.setAttribute('aria-label', fold.label);
      e.foldToggle.setAttribute('aria-expanded', String(st.open));
      e.foldWorking.hidden = !fold.link;
      e.foldWorking.textContent = fold.link ?? '';
      e.foldWorking.dataset.id = fold.workingId ?? '';
      e.fold.toggleAttribute('data-open', st.open);
      e.foldLines.hidden = !st.open;
      place(e.foldLines, items);
      place(e.events, [e.fold]);
    } else {
      place(e.events, items);
      e.foldLines.replaceChildren();
    }
    e.events.hidden = lines.length === 0;
    if (drawn) { items.forEach((li, i) => { if (!drawn.has(lines[i].key) && !drawn.has(was(lines[i]))) { enter(li, 'listItem'); } }); }
    // A control no longer drawn has lost focus, whatever activeElement says (WebKit keeps a
    // hidden one there).
    if (!had || (document.activeElement === had && had.isConnected && !had.hidden && had.getClientRects().length)) { return; }
    // Its line went into a closed fold: to the fold's button. Its name still drawn, in its line or
    // one of its own it moved to: to its name there. Else to what its line has left. The fold
    // gone from under its button or link: to the working delegate's name the link named, else
    // to the first line's.
    const key = (where.key && lines.find((l) => was(l) === where.key)?.key) ?? where.key;
    const li = key ? [...e.events.querySelectorAll('li.delegate-event')].find((x) => x.dataset.key === key) : null;
    const byId = (root) => (where.id ? [...root.querySelectorAll('li.delegate-event .delegate-link')].find((b) => b.dataset.id === where.id) : null);
    const target = had === e.foldToggle || had === e.foldWorking ? (fold ? e.foldToggle : byId(e.events) ?? e.events.querySelector('li.delegate-event .delegate-link'))
      : (fold && !st.open) ? e.foldToggle
        : (li && byId(li)) ?? byId(e.events) ?? (li && ((where.more ? li.querySelector('.delegate-more') : null) ?? li.querySelector('.delegate-link')));
    target?.focus({ preventScroll: true });
  }

  // One line: the delegate icon for a start, the outcome's glyph once it reported back; its words
  // with each name a link, and its absolute time (the viewer's own) as its tooltip and in its name.
  #eventLine(li, line, st) {
    if (!li) { li = h('li', { class: 'delegate-event' }, h('span', { class: 'delegate-event-mark' }), h('span', { class: 'delegate-event-text' })); }
    li.dataset.kind = line.kind;
    li.dataset.key = line.key;
    li.title = line.tip;
    const mark = li.firstChild;
    const was = mark.firstChild;
    if (line.glyph && was?.classList.contains('glyph')) {
      setGlyph(was, line.glyph, { size: 12 });
    } else if (line.glyph) {
      const g = glyph(line.glyph, { size: 12 });
      if (was) { leave(was, 'glyph'); }
      mark.replaceChildren(g);
      if (was) { enter(g, 'glyph'); }
    } else if (was?.dataset.icon !== 'delegate') {
      mark.replaceChildren(designIcon('delegate', { size: 16 }));
    }
    const expanded = st.more.has(line.key);
    const parts = eventParts(line, { expanded });
    const said = JSON.stringify([parts, line.tip]);
    if (li.dataset.said === said) { return li; }
    li.dataset.said = said;
    li.lastChild.replaceChildren(...parts.map((p) => {
      if (typeof p === 'string') { return p; }
      if (p.more) {
        return h('button', { type: 'button', class: 'delegate-link delegate-more', onclick: () => {
          st.more.add(line.key);
          this.#renderEvents(this.#s);
          // To the first name it brought.
          li.querySelectorAll('.delegate-link')[2]?.focus();
        } }, `${p.more} more`);
      }
      return h('button', { type: 'button', class: 'delegate-link', dataset: { id: p.id }, onclick: () => this.openDelegate(p.id) }, p.name);
    }), h('span', { class: 'sr-only' }, ` (${line.tip})`));
    return li;
  }

  // A delegate opened from the strip, the list, a nested list or the conversation: in the
  // inspector at three panes (brought back, if it was hidden), else pushed in the Delegates tab.
  openDelegate(id) {
    if (!id) { this.closeDelegate(); return; }
    if (!(this.#s.subagents ?? []).some((a) => a.id === id)) { return; }
    // Opened from inside the detail (its nested list): the button pressed goes with the detail it
    // was in, so focus follows to the delegate opened, at any width.
    const fromDetail = this.detail.element.contains(document.activeElement);
    this.#delegate = id;
    if (!this.detail.element.isConnected) { this.els.inspector.insertBefore(this.detail.element, this.els.tools); }
    this.detail.select(id, this.#s);
    this.view.dataset.delegate = 'open';
    // At three panes a hidden inspector is shown for it: the delegate is there, whenever the
    // layout catches up, never the tab.
    const { wide } = this.layout();
    if (wide && !this.layout().inspector && this.onInspector) { this.onInspector(); }
    const inspector = this.layout().inspector || (wide && !!this.onInspector);
    if (!inspector) { this.setTab('delegates'); }
    this.#renderDelegates(this.#s);
    this.detail.shown();
    // Pushed in the list's place, focus goes to it; in the inspector it stays where it was.
    if (!inspector || fromDetail) { this.detail.focus(); }
    this.onDelegate?.();
  }

  // Back to the session's own Details and Tools, or to the Delegates tab's list.
  closeDelegate({ focus = false } = {}) {
    const id = this.#delegate;
    if (!id) { return; }
    this.#delegate = null;
    this.detail.select(null);
    this.detail.element.remove();
    delete this.view.dataset.delegate;
    this.#renderDelegates(this.#s);
    this.#revealTools();
    if (focus) { (this.delegates.row(id) ?? this.els.tabD).focus(); }
    this.onDelegate?.();
  }

  get openDelegateId() { return this.#delegate; }

  // "Ask main agent to stop it": the words go in the reply box, after anything already there, for
  // the person to read and send. Nothing is sent; Send, as every reply, goes to the main agent.
  #askToStop(text) {
    if (!this.#reply) { return; }
    if (!this.layout().inspector) { this.setTab('conversation'); }
    this.#reply.prefill(text);
  }

  // The session's latest prompt, when the person didn't type it (#489, ui.md §6.4): in place of
  // the bubble, its label line, two lines of it and Show prompt. Every word of an agent's is
  // text (prompt-origin.js has cleaned the label and escaped what hides).
  #renderPrompt(s) {
    const e = this.els;
    const seen = this.#prompts.observe(s);
    // A new prompt starts collapsed; an update to the same one keeps it as the person left it.
    if (seen.fresh) { this.#promptOpen = false; }
    this.#promptAt = seen.at;
    const origin = promptOrigin(s, { repeat: seen.repeat });
    e.prompt.hidden = !origin;
    if (!origin) { return; }
    e.user.hidden = true;
    e.userText.textContent = '';
    e.prompt.dataset.from = origin.from;
    e.prompt.toggleAttribute('data-repeat', origin.repeat);
    if (this.#changed('promptLabel', [origin.icon, origin.facts, origin.by])) {
      e.promptIcon.replaceChildren(origin.icon === 'clock' ? designIcon('clock', { size: 16 }) : drivenBadge());
      setFacts(e.promptFacts, origin.facts);
      e.promptBy.textContent = origin.by ?? '';
      e.promptBy.hidden = !origin.by;
    }
    // A repeat says so instead: its words are the same as the last, which the person has seen.
    e.promptPreview.textContent = origin.repeat ? '' : origin.preview;
    e.promptPreview.hidden = origin.repeat;
    e.promptText.textContent = origin.text;
    e.promptAge.hidden = !this.#promptAt;
    if (this.#promptAt) {
      e.promptAge.dateTime = this.#promptAt;
      e.promptAge.title = localTime(this.#promptAt);
      e.promptAge.textContent = ago(this.#promptAt);
    }
    this.#setPromptOpen(this.#promptOpen);
  }

  // Show prompt (ui.md §6.4, §9): a real button, named for the prompt and its state. Its space
  // opens and closes at once; app.css turns the chevron and settles the text in (Disclosure).
  #setPromptOpen(open) {
    const e = this.els;
    this.#promptOpen = open;
    const origin = promptOrigin(this.#s);
    e.promptFull.hidden = !open;
    e.prompt.toggleAttribute('data-open', open);
    e.promptToggle.setAttribute('aria-expanded', String(open));
    e.promptToggleText.textContent = open ? 'Hide prompt' : 'Show prompt';
    if (origin) { e.promptToggle.setAttribute('aria-label', disclosureName(origin, open)); }
  }

  // Details (ui.md §6.2): everything the row leaves out, as a property list, its rows and their
  // order the shared table's (details-rows.js). Not responding since is the time it went silent,
  // which the row's age counts from (§6.13); Message is the bridge's sentence for what is wrong
  // (#800), plain text, never a link and never the agent's own words, and only here.
  #renderFacts(list) {
    const e = this.els;
    const cells = new Map();
    const value = (row) => {
      switch (row.kind) {
        case 'agent': return h('span', { class: 'agent-name' }, row.text);
        case 'branch': return h('span', { class: 'branch' }, row.text);
        case 'folder': return pathView(row.text);
        case 'attention': return h('span', { class: 'attention-message', dir: 'ltr' }, row.text);
        case 'pid': return h('span', { class: 'mono' }, row.text);
        case 'id': return this.#idValue(row.text);
        case 'started': return h('time', { datetime: row.at, title: localTime(row.at) }, row.text);
        default: return row.text;
      }
    };
    const rows = list.map((row) => {
      const dd = h('dd', null, value(row));
      cells.set(row.kind, dd);
      return h('div', { class: `fact fact-${row.kind}` }, h('dt', null, row.term), dd);
    });
    this.#factCells = cells;
    e.facts.replaceChildren(...rows);
    this.refreshTimes();
  }

  // The ID and its Copy button (#1279): the one Details value with a button, for pasting the id
  // into a bug report from a phone. The button keeps its name and size; "Copied" and "Couldn't
  // copy" go to a polite status beside it (copy-feedback.js).
  #idValue(id) {
    this.#idCopy?.cancel();
    const note = h('span', { class: 'fact-copy-note', role: 'status' });
    this.#idCopy = copyFeedback({
      copy: () => copyText(id),
      say: (text, ok) => { note.textContent = text; note.dataset.copy = text ? (ok ? 'done' : 'failed') : ''; },
    });
    const button = h('button', {
      type: 'button', class: 'fact-copy', 'aria-label': 'Copy session ID', title: 'Copy session ID',
      onclick: () => this.#idCopy.press(),
    }, 'Copy');
    return h('span', { class: 'fact-id-value' }, h('span', { class: 'mono' }, id), button, note);
  }

  // The values that move with the clock (and the status words, which "last seen 2m ago" is one of
  // while the machine is away), changed where they are, so a selection in another row stays.
  #tickFacts(s, now) {
    if (!this.#factCells?.size) { return; }
    for (const row of detailRows(s, { status: this.#detailsStatus, machine: this.machineName || null, away: this.#detailsAway, now })) {
      const dd = CLOCK_ROWS.has(row.kind) ? this.#factCells.get(row.kind) : null;
      // Started's words are in its <time>, which keeps its datetime and its full timestamp's tooltip.
      const at = row.kind === 'started' ? dd?.firstElementChild ?? dd : dd;
      if (at && at.textContent !== row.text) { at.textContent = row.text; }
    }
  }

  // What the session's machine is called, and whether its row says so. What is sent from the
  // view from now on says it too.
  setMachine(name, labelled) {
    if (name === this.machineName && labelled === this.labelled) { return; }
    this.machineName = name;
    this.labelled = labelled;
    this.#last = {};
    if (this.#s) { this.update(this.#s); }
  }

  // Its machine went offline, came back, or sent a whole new list since (#269): what the row and
  // header say. What it offers to send follows answering(), drawn again by refreshQuestion().
  setStale(stale) {
    const same = (a, b) => (a?.reason ?? null) === (b?.reason ?? null) && (a?.since ?? null) === (b?.since ?? null)
      && (a?.grace ?? false) === (b?.grace ?? false) && (a?.drawn ?? false) === (b?.drawn ?? false);
    if (same(stale, this.#stale)) { return; }
    this.#stale = stale ?? null;
    if (this.#s) { this.update(this.#s); }
  }

  // ui.md §2.2 (#269), as facts (details-rows.js's awayWords).
  #staleText(now = Date.now()) {
    return awayWords(this.#stale, now);
  }

  // Activity since the session was last on screen (session-order.js): semibold, and the 6 px
  // Bridge dot at the row's trailing edge in place of its age (ui.md §6.1), named for a screen
  // reader.
  setUnread(unread) {
    const e = this.els;
    if (unread && !e.unread) {
      e.unread = h('span', { class: 'unread-dot' }, h('span', { class: 'sr-only', id: this.ids.unread }, 'New activity'));
      e.rowLine1.append(e.unread);
      this.element.classList.add('unread');
      // Activity dot (ui.md §5.3): fades in, and later out; it never pulses or repeats.
      enter(e.unread, 'activityDot');
    } else if (!unread && e.unread) {
      leave(e.unread, 'activityDot');
      e.unread.remove();
      e.unread = null;
    }
    this.element.classList.toggle('unread', unread);
    this.#nameRow();
  }

  // The row's name with its age, or "New activity" where the dot is drawn in the age's place.
  #nameRow() {
    const e = this.els;
    const { drawn, unsaid } = this.#rowName;
    e.open.setAttribute('aria-label', [...drawn, e.unread ? 'New activity' : e.rowAge.textContent, ...unsaid].filter(Boolean).join(', '));
  }

  setPinned(pinned) {
    this.#pinned = pinned;
    if (pinned) { this.element.dataset.pinned = 'true'; } else { delete this.element.dataset.pinned; }
    const e = this.els;
    e.pin.setAttribute('aria-pressed', pinned ? 'true' : 'false');
    e.pin.setAttribute('aria-label', pinned ? 'Unpin' : 'Pin');
    e.pin.title = pinned ? 'Unpin' : 'Pin';
  }

  // The row chosen, and its view the one shown.
  setSelected(selected) {
    if (selected === this.#selected) { return; }
    this.#selected = selected;
    this.#readToEnd = null;
    this.element.classList.toggle('selected', selected);
    if (selected) { this.els.open.setAttribute('aria-current', 'true'); } else { this.els.open.removeAttribute('aria-current'); }
    this.view.hidden = !selected;
    if (selected) { this.#openTurns(); }
    // Shown again (a row chosen, the browser's Back or Forward): its header whole, the scroll kept.
    if (selected) { this.collapse?.shown(); }
    // Shown now, in the page it was put into: what holds it there can move its question too.
    // Hidden, nothing of it is read or watched.
    if (selected) {
      this.#moves?.track();
      this.#moves?.observe(this.#guarded);
    } else {
      this.#moves?.pause();
    }
    // Its question shows now, where a tap or a click may already be headed (a phone's push puts
    // the view under the finger that opened it): the same wait as a question arriving.
    if (selected && this.#guarded?.isConnected) { this.#guard(); }
    if (!selected) { this.#menu.close(false); }
    this.layoutChanged();
  }

  // Its view is on screen again with nothing of its own changed (Sessions back from another
  // page, the page back from the background): a question it shows waits as one arriving does,
  // where it is. Whether the conversation shows is read from the page, not from the tab picked:
  // with the inspector beside it, Tools picked earlier leaves the conversation on screen.
  reshow() {
    if (this.#selected && this.#guarded?.isConnected && this.viewing() && this.els.scroll.getClientRects().length) { this.#guard(); }
  }

  // The inspector became a pane of its own, or stopped being one.
  layoutChanged() {
    this.#readToEnd = null;
    const { inspector } = this.layout();
    // A delegate shown in the inspector stays shown as the window narrows: pushed in the Delegates
    // tab. Only then: a tab chosen at a narrower width is left as it was.
    if (this.#delegate && this.#wasInspector && !inspector && !this.layout().wide && this.#tab !== 'delegates') { this.setTab('delegates'); }
    this.#wasInspector = inspector;
    this.#linkDelegatesTab();
    this.els.inspectorButton.setAttribute('aria-label', inspector ? 'Hide inspector' : 'Show inspector');
    this.els.inspectorButton.title = this.els.inspectorButton.getAttribute('aria-label');
    this.#revealTools();
    this.detail.shown();
  }

  // Conversation or Tools, where they are tabs; Details, from the header's button.
  setTab(tab) {
    const was = this.#tab;
    const before = this.#tabEl(was);
    const from = before?.getClientRects().length ? before.getBoundingClientRect() : null;
    this.#tab = tab === 'tools' || tab === 'details' || (tab === 'delegates' && !this.els.tabD.hidden) ? tab : 'conversation';
    this.#applyTab();
    this.#revealTools();
    this.detail.shown();
    if (this.#tab === was) { return; }
    this.#readToEnd = null;
    // Back on the conversation, its question shows again where a click may be headed: the same
    // wait as a question arriving, one that came while another tab was shown included.
    if (this.#tab === 'conversation' && this.#guarded?.isConnected && this.#selected) { this.#guard(); }
    // Tab switch (ui.md §5.3): the bar under the tab slides from the one left, and what the tab
    // shows fades in without sliding.
    const now = this.#tabEl(this.#tab);
    if (from && now?.getClientRects().length) {
      const r = now.getBoundingClientRect();
      slide(now, 'selection', from.left - r.left, 0, { pseudo: '::after', scaleX: from.width / r.width });
    }
    // A delegate pushed in the list's place is what the Delegates tab shows, the list hidden.
    const delegates = this.#delegate && !this.layout().inspector ? this.detail.element : this.els.delegates;
    const shown = { conversation: this.els.conversation, delegates, tools: this.els.tools, details: this.els.details }[this.#tab];
    if (from && shown?.getClientRects().length) { enter(shown, 'tab'); }
  }

  #tabEl(tab) { return { conversation: this.els.tabC, delegates: this.els.tabD, tools: this.els.tabT }[tab] ?? null; }

  #applyTab() {
    const e = this.els;
    this.view.dataset.tab = this.#tab;
    for (const [tab, el] of [['conversation', e.tabC], ['delegates', e.tabD], ['tools', e.tabT]]) {
      const on = this.#tab === tab;
      el.setAttribute('aria-selected', on ? 'true' : 'false');
      el.tabIndex = on || (this.#tab === 'details' && tab === 'conversation') ? 0 : -1;
    }
    e.detailsButton.setAttribute('aria-pressed', this.#tab === 'details' ? 'true' : 'false');
    // The header is whole on every tab but the conversation.
    this.collapse?.setPaused(this.#tab !== 'conversation');
  }

  // Details: in the inspector, where it is a pane (shown, if it was hidden); otherwise in place
  // of the conversation, and the button again goes back.
  showDetails() {
    if (this.layout().inspector) {
      // A delegate open holds the inspector in Details' place: it gives way.
      if (this.#delegate) { this.closeDelegate(); }
      this.els.details.scrollIntoView({ block: 'nearest' });
      this.focusDetails();
      return;
    }
    const opening = this.#tab !== 'details';
    this.setTab(opening ? 'details' : 'conversation');
    if (opening) { this.focusDetails(); }
  }

  // Focus to Details' heading, which a screen reader reads as "Details, heading": the same place
  // however Details was opened, and never a button inside it or the question card (#249).
  focusDetails() { this.els.detailsHead.focus(); }

  // Tools shown: fetched the first time, and again when the session changed while they were
  // hidden (updates refetch only what is on screen).
  #revealTools() {
    if (!this.#toolsShown()) { return; }
    if (!this.#history.loaded || (this.#history.at !== this.#s.updatedAt && !this.#history.timer)) { this.#loadHistory(); }
  }

  #toolsShown() {
    // On screen only while Sessions shows (and, on a phone, the session is pushed). At three
    // panes a delegate open takes the inspector in their place, so they are not fetched then.
    return this.#selected && this.viewing() && (this.layout().inspector ? !this.#delegate : this.#tab === 'tools');
  }

  // A phone's overflow menu: Pin, Details and End session (ui.md §6.2).
  #openMore() {
    const end = this.#stop?.pressable ? this.#stop : null;
    this.#menu.show(this.els.bar, [
      { label: this.#pinned ? 'Unpin' : 'Pin', run: () => this.onPin(this, !this.#pinned) },
      { label: 'Details', run: () => { this.setTab('details'); this.focusDetails(); } },
      ...(end ? [{ label: 'End session', run: () => { this.setTab('details'); end.element.querySelector('.end-button').click(); } }] : []),
    ], { back: this.els.more, label: `${this.#s.title || 'Session'}: more` });
  }

  // The session is going: it ended, or its machine is no longer shown.
  dispose() {
    this.#panel?.destroy();
    this.#panel = null;
    this.#reply?.destroy();
    this.#reply = null;
    this.#stop?.destroy();
    this.#stop = null;
    this.#menu.close(false);
    this.#dropLeaving();
    this.#dropReceipt();
    for (const r of this.#closed.splice(0)) { this.#destroyReceipt(r); }
    this.delegates.dispose();
    this.detail.dispose();
    this.#moves?.stop();
    this.collapse?.destroy();
    this.#phone?.removeEventListener('change', this.#onPhone);
    clearTimeout(this.#history.timer);
    this.#unwatch?.();
    this.#unwatch = null;
  }

  // The conversation before the latest exchange, from the history store, opened the first time the
  // session is shown. It opens at the latest message: one scroll to the end, once, when the turns
  // first draw, and none after it.
  #unwatch = null;
  #atLatest = false;
  #openTop = 0;

  #openTurns() {
    if (!this.historyStore) { return; }
    if (this.#unwatch) { this.#drawTurns(); return; }
    this.#openTop = this.els.scroll.scrollTop;
    this.#unwatch = this.historyStore.watch(this.hostId, this.#s.id, () => this.#drawTurns());
    this.historyStore.open(this.hostId, this.#s.id, { session: this.#s }).catch(() => {});
    this.#drawTurns();
  }

  // The question the slot holds a place for: the open one, or the one whose record it still shows.
  #slotQuestion = null;

  #drawTurns() {
    const snapshot = this.historyStore.get(this.hostId, this.#s.id);
    const slot = this.els.question;
    if (this.#s.question) { this.#slotQuestion = this.#s.question.id; } else if (!slot.hasChildNodes()) { this.#slotQuestion = null; }
    const drawn = this.turns.render(snapshot, this.#s.activity?.turn ?? null, { slot, questionId: this.#slotQuestion });
    // Not in a turn (none holds it, or the history is not drawn): back where the live exchange ends.
    if (!this.turns.hosted && slot.parentNode !== this.els.stream) { this.els.stream.append(slot); }
    this.#syncWaiting();
    const sc = this.els.scroll;
    if (!this.#atLatest && snapshot.phase !== 'idle' && snapshot.phase !== 'loading' && sc.getClientRects().length) {
      this.#atLatest = true;
      // Not if the person has already scrolled: the jump would take them from where they are.
      if (drawn && sc.scrollTop === this.#openTop) {
        const toEnd = () => { sc.scrollTop = sc.scrollHeight; };
        if (this.collapse) { this.collapse.appScroll(toEnd); } else { toEnd(); }
      }
    }
  }

  // The machine's capabilities changed (a reconnect to another version of its host).
  refreshQuestion() {
    if (!this.#s) { return; }
    const atEnd = this.#reading();
    this.#renderQuestion(this.#s);
    this.#renderReply(this.#s);
    this.#renderStop(this.#s);
    // An open delegate's "Ask main agent to stop it" goes in the reply box: shown only while
    // there is one, which a change of what the machine takes adds or takes away.
    this.#renderDelegates(this.#s);
    this.#bringQuestion(atEnd);
  }

  // End session, while the connected machine can end sessions.
  #renderStop(s) {
    const ctx = this.answering();
    const hostId = ctx?.stops && ctx.capabilities?.includes('sessions.stop') ? ctx.hostId : null;
    if (this.#stopHost !== hostId || (hostId && !this.#stop)) {
      this.#stop?.destroy();
      this.#stop = hostId ? new EndSession({ machineName: this.machineName, stopping: { service: ctx.stops, hostId, sessionId: s.id } }) : null;
      this.#stopHost = hostId;
      this.els.stop.replaceChildren(...(this.#stop ? [this.#stop.element] : []));
    }
    this.#stop?.update(s, { held: staleNote(ctx?.stale, this.machineName, 'ended') });
  }

  // The reply box, while the connected machine takes replies.
  #renderReply(s) {
    const ctx = this.answering();
    const hostId = ctx?.replies && ctx.capabilities?.includes('sessions.reply') ? ctx.hostId : null;
    if (this.#replyHost !== hostId || (hostId && !this.#reply)) {
      if (this.#reply) {
        this.#replyDraft = { hostId: this.#replyHost, text: this.#reply.text };
        this.#reply.destroy();
        this.#reply = null;
      }
      if (hostId) {
        this.#reply = new ReplyBox({ machineName: this.machineName, replying: { service: ctx.replies, hostId, sessionId: s.id }, attach: ctx.capabilities.includes('sessions.attach'), largeMax: ctx.largeAttachments?.() ?? 0, onGoToQuestion: () => this.#goToQuestion(), onChange: () => this.foot?.changed() });
        if (this.#replyDraft.hostId === hostId) { this.#reply.els.input.value = this.#replyDraft.text; }
      }
      this.#replyHost = hostId;
      this.els.reply.replaceChildren(...(this.#reply ? [this.#reply.element] : []));
    }
    this.#reply?.update(s, { attach: ctx?.capabilities?.includes('sessions.attach') === true, largeMax: ctx?.largeAttachments?.() ?? 0, offline: staleNote(ctx?.stale, this.machineName, 'replied to') });
  }

  #renderQuestion(s) {
    const q = s.question;
    const ctx = this.answering();
    const able = q ? answerability(q, ctx?.capabilities ?? [], s.agent) : null;
    const answerable = !!(able?.ok && ctx?.service);
    // Who asks (ui.md §6.4): the main agent, or the delegate the question or waitingOn names,
    // by the rule Needs you uses, so opening its entry never changes who asks (#249).
    const by = q ? delegateOf(s) : null;
    const asker = q ? (by ? `${by} asks` : 'Main agent asks') : null;
    // Its machine offline, or back without its whole list yet (#269): the form stays, with what
    // was entered, but takes nothing until the machine says the question is still asked.
    const held = staleNote(ctx?.stale, this.machineName, 'answered');
    // An approval the host escaped anything in (#803): how Approve is given for it from here.
    const escapedMode = q && answerable ? escapedApprove(q, ctx.capabilities) : null;
    // A quiet hold starting or turning drawn changes the controls only: the panel stays, with what it says,
    // so a sheet open on it keeps the panel whose hold its signing checks (#950).
    if (!this.#changed('question', [q, answerable, answerable ? ctx.hostId : null, able?.why, ctx?.readOnly ?? null, asker, held?.updating ? '' : holdKey(held), escapedMode])) { this.#panel?.setHeld(held); return; }
    // Drawn by the app, not by the person typing in it: whatever this moves counts.
    this.#moves?.notOwn();
    // A new question while the last one's answer is still on its way: that answer goes on as a receipt.
    if (q && this.#leaving) {
      const { panel } = this.#leaving;
      this.#dropLeaving();
      this.#watchReceipt(panel.answering, panel.q, panel.asker);
    }
    const old = this.#panel;
    // Focus in the form that is about to collapse into its line moves to the line (#focusLine).
    const hadFocus = !!old?.element.contains(document.activeElement);
    const sameQuestion = !!(old && q && old.questionId === q.id && answerable && old.answering.hostId === ctx.hostId);
    let leaving = false;
    if (old && !sameQuestion) {
      // The question changed or went while it was being answered. Text the person typed
      // and hasn't sent is kept in the view, not dropped with the form; an answer that went
      // keeps saying what became of it.
      const draft = old.unsentText();
      const entry = old.entry;
      if (draft) {
        this.#orphan = { text: draft, why: !q || q.id !== old.questionId ? 'changed' : 'unanswerable' };
      } else if (entry && !q && leaves(entry)) {
        // Whichever came first, the question going or the answer's result: the same panel, so a
        // result that arrived first keeps its check, or its pressed option and its words, as the
        // other order does.
        this.#leave(old);
        leaving = true;
      } else if (entry) {
        this.#watchReceipt(old.answering, old.q, old.asker);
      }
    }
    if (!leaving) { old?.destroy(); }
    this.#panel = null;
    let view = '';
    if (q && answerable) {
      this.#panel = new QuestionPanel(q, {
        machineName: this.machineName,
        answering: { service: ctx.service, hostId: ctx.hostId, sessionId: s.id },
        state: sameQuestion ? old.state : null,
        sentDraft: sameQuestion ? old.sentDraft : null,
        asker,
        onSend: (typed) => this.#sent(typed),
        listed: () => this.#s?.question?.id === q.id,
        escaped: escapedMode ? { mode: escapedMode === 'confirm' && !ctx.confirm ? 'device' : escapedMode, sign: ctx.confirm ?? null, title: s.title ?? null } : null,
      });
      this.#panel.setHeld(held);
      view = this.#panel.element;
    } else if (q) {
      view = renderQuestion(q, { machineName: this.machineName, why: able.why, readOnly: ctx?.readOnly ?? null, asker });
    }
    // A new question replaces a receipt that has said its last word, unless that word was that the
    // question it was about is no longer waiting: that stays in the conversation (#retireReceipt). A
    // question that stays moves up into its place.
    let dropped = false;
    if (q && this.#receipt?.final) { dropped = this.#retireReceipt(); }
    // Scroll to a question (ui.md §5.3): one that arrives is brought into sight once the whole
    // update is drawn (#bringQuestion), as long as the conversation was read to its end.
    const arrived = this.#asked !== undefined && !!q && q.id !== this.#asked;
    this.#asked = q?.id ?? null;
    this.#receipt?.redraw();
    this.els.question.replaceChildren(...[this.#orphanView(), ...this.#closed.map((r) => r.element), this.#receipt?.element, this.#leaving?.panel.element, view].filter(Boolean));
    if (hadFocus && !sameQuestion) { this.#focusLine(); }
    // An approval's command never slides: a push still under way stops travelling.
    if (view) { stopTravel(view); }
    if (this.#receipt?.fresh) {
      this.#receipt.fresh = false;
      enter(this.#receipt.element, 'acknowledge', { dy: 1 });
    }
    // A question newly drawn where the person can see it: its controls wait before a click counts.
    // Not the same question redrawn (reworded, or its machine's capabilities changed), unless it
    // has just become answerable: its hold lifted (#269) or its machine now taking answers. The
    // note that said why goes, and Send or Approve once moves up under the pointer as it does.
    const unheld = !!this.#guardedHeld && !held;
    const opened = !this.#guardedAnswerable && answerable;
    if (this.#guarded !== (view || null)) {
      this.#moves?.unobserve(this.#guarded);
      if (view) { this.#moves?.observe(view); }
    }
    this.#guarded = view || null;
    const same = view && q.id === this.#guardedId;
    let armed = false;
    if (view && (!same || unheld || opened || dropped)) {
      this.#guardedId = q.id;
      if (this.#selected && this.viewing() && this.els.scroll.getClientRects().length) {
        this.#guard();
        armed = true;
      }
    }
    // The same question redrawn while its wait still runs: the new view says so for what is left
    // of it, no longer.
    if (view && !armed) { this.#mark(); }
    this.#guardedHeld = view ? held : null;
    this.#guardedAnswerable = !!(view && answerable);
    this.#arrived = arrived && view ? view : null;
  }

  #guard() {
    this.#guardUntil = (globalThis.performance?.now() ?? Date.now()) + QUESTION_ARRIVAL_GUARD_MS;
    this.#mark();
  }

  // Said on the question while the wait lasts (data-arriving), so what waits on it can tell.
  #mark() {
    const view = this.#guarded;
    const left = this.#guardUntil - (globalThis.performance?.now() ?? Date.now());
    clearTimeout(this.#guardTimer);
    if (left <= 0) { return; }
    view?.setAttribute?.('data-arriving', '');
    this.#guardTimer = setTimeout(() => view?.removeAttribute?.('data-arriving'), left);
  }

  // A click from a pointer (detail > 0) on the question's own controls during the guard is
  // dropped before any handler, form or default action sees it: a choice, Approve once, Deny,
  // Send, a radio or its label. A key's activation (detail 0) goes through, and so does anything
  // else in the slot (a kept draft's Dismiss, say), which the question didn't bring.
  #guardQuestion(slot) {
    slot.addEventListener('click', (e) => {
      if (e.detail === 0) { return; }
      if (!this.#guarded?.contains(e.target) || !e.target.closest?.(QUESTION_CONTROLS)) { return; }
      // Moved since the last frame was read (an update in the task just before this click, before
      // any frame): found now, and the wait starts before the click is let through. A read that
      // fails can't say the answers stayed put, so that click is dropped too (#642).
      let read = true;
      try { this.#moves?.check(); } catch { read = false; }
      if (read && (globalThis.performance?.now() ?? Date.now()) >= this.#guardUntil) { return; }
      e.preventDefault();
      e.stopImmediatePropagation();
    }, true);
    // What the person does in the question that went through: the question's answer to it isn't
    // a move under their pointer (watchMoves). A dropped click stops above and changes nothing.
    for (const type of ['click', 'keydown', 'input', 'change']) {
      slot.addEventListener(type, (e) => { if (this.#guarded?.contains(e.target)) { this.#moves?.own(); } }, true);
    }
  }

  #questionSeen = true;

  #syncWaiting() {
    this.els.waiting.hidden = !this.#s?.question || this.#questionSeen;
  }

  #showWaiting() {
    this.revealQuestion();
    this.#syncWaiting();
  }

  #atEnd() {
    const sc = this.els.scroll;
    return sc.scrollHeight - sc.scrollTop - sc.clientHeight < 24;
  }

  #reading() {
    const sc = this.els.scroll;
    if (!sc.getClientRects().length) {
      this.#readToEnd = null;
      return this.#atEnd();
    }
    if (this.#readToEnd === null || sc.scrollTop !== this.#readAt) {
      this.#readToEnd = this.#atEnd();
      this.#readAt = sc.scrollTop;
    }
    return this.#readToEnd;
  }

  // Smoothly, and never into a conversation scrolled back through or held: atEnd is where the
  // reader was before the update, since a longer response drawn with the question moves it.
  // Held is a finger on it, a mouse button down, or a mouse or pen over it: someone reading,
  // whose next click must land on what they were reading (ui.md §5.3).
  #bringQuestion(atEnd) {
    const view = this.#arrived;
    this.#arrived = null;
    const sc = this.els.scroll;
    if (view?.isConnected && atEnd && !this.#touches && !this.#pressing && !this.#hovering && this.#selected && this.viewing() && sc.getClientRects().length) {
      this.#scrollTo(view, scrollBehavior());
    }
  }

  // The question brought into sight on purpose (a notification, a Needs you entry): its controls
  // wait as they do for one that arrives. behavior: the page's smooth scroll, or a jump. el: the
  // part of it that must be in sight (a notification's focused heading). The whole question when
  // it fits in the pane, so its answers come with it; el when it is taller: brought to the
  // nearest edge, the whole question moved nowhere from midway through it, and a heading that
  // had focus stayed out of sight (#893 review, Copilot 4481392664).
  revealQuestion(behavior = scrollBehavior(), el = null) {
    const view = this.#guarded?.isConnected ? this.#guarded : null;
    if (!view) { return; }
    const fits = view.getBoundingClientRect().height <= this.els.scroll.getBoundingClientRect().height;
    this.#scrollTo(el && view.contains(el) && !fits ? el : view, behavior);
  }

  // The phone notice's "Go to the question" (ui.md §6.5): an app scroll to the question, which
  // leaves the header as it was, and focus on its first choice. A question taking nothing yet (its
  // machine offline) has no choice to focus: its heading takes focus, as a notification's does.
  #goToQuestion() {
    const view = this.#guarded?.isConnected ? this.#guarded : null;
    if (!view) { return; }
    const usable = (c) => !c.disabled && !c.closest('fieldset:disabled') && c.getClientRects().length > 0;
    let target = [...view.querySelectorAll('.answer-controls button, .answer-controls input, .answer-controls select, .answer-controls textarea')].find(usable)
      ?? [...view.querySelectorAll(QUESTION_CONTROLS)].find(usable);
    if (!target) {
      target = view.querySelector('.question-head');
      if (target) { target.tabIndex = -1; }
    }
    target?.focus({ preventScroll: true });
    this.revealQuestion(scrollBehavior(), target);
  }

  // A scroll to the question: the guard runs from now and, for a smooth scroll that has somewhere
  // to go, again from each step of it until it lands, since that takes the browser's own time. Only
  // that scroll, heard for a little while: a question already in sight makes no scroll, and a
  // scroll the person makes later must not hold the question's buttons back. A newer scroll
  // replaces it.
  #scrollTo(view, behavior) {
    const sc = this.els.scroll;
    this.#scrollEnd?.abort();
    this.#scrollEnd = null;
    const v = view.getBoundingClientRect();
    const box = sc.getBoundingClientRect();
    // What the floating header covers, and the room the scroll leaves below it (scroll-padding-top).
    const covered = this.collapse?.covered() ?? 0;
    const room = covered > 0 ? parseFloat(sc.ownerDocument.defaultView?.getComputedStyle?.(sc).getPropertyValue('--ab-space-2')) || 0 : 0;
    const top = box.top + (covered > 0 ? covered + room : 0);
    // A pixel's rounding is nowhere to go.
    // And what the reply area covers at the foot (scroll-padding-bottom).
    const bottom = box.bottom - (this.collapse?.footCovered() ?? 0);
    const moves = sc.getClientRects().length > 0 && (v.top < top - 1 || v.bottom > bottom + 1);
    const scroll = () => view.scrollIntoView({ block: 'nearest', behavior });
    // A scroll the app makes leaves the header as it was.
    if (this.collapse) { this.collapse.appScroll(scroll); } else { scroll(); }
    this.#guard();
    if (!moves || behavior !== 'smooth') { return; }
    // Every step of the scroll re-arms it, so it runs out only after the question has stopped
    // moving: `scroll` fires in every engine, where `scrollend` isn't in Safari yet. Listening
    // stops at `scrollend`, once the steps stop, or as soon as the person takes the scroller
    // over, so a scroll of their own after it can't hold a click they mean.
    const ended = this.#scrollEnd = new AbortController();
    const { signal } = ended;
    let settle = 0;
    const done = () => { clearTimeout(settle); ended.abort(); };
    sc.addEventListener('scroll', () => {
      this.#guard();
      clearTimeout(settle);
      settle = setTimeout(done, SCROLL_SETTLED_MS);
    }, { signal, passive: true });
    sc.addEventListener('scrollend', () => { this.#guard(); done(); }, { signal });
    for (const type of ['wheel', 'touchstart', 'pointerdown', 'keydown']) { sc.addEventListener(type, done, { signal, passive: true }); }
    setTimeout(done, 3 * QUESTION_ARRIVAL_GUARD_MS);
  }

  // A finger on the conversation holds it until the last finger lifts. Touch events, not pointer
  // events: once the browser takes a touch over to scroll it sends pointercancel while the finger
  // is still down. A mouse button holds it until released, wherever that is, and a mouse or pen
  // holds it while it is over it.
  #watchHold(scroller) {
    // Any scroll: measured afresh at the next update (a scroll away and back to the same place
    // included). Not measured here: an update can come before this event does.
    scroller.addEventListener('scroll', () => { this.#readToEnd = null; }, { passive: true });
    // What the person opens or presses in it (Show reasoning, a disclosure in a response) can
    // grow it with no scroll: where they are is measured afresh at the next update.
    for (const type of ['toggle', 'click']) { scroller.addEventListener(type, () => { this.#readToEnd = null; }, true); }
    const fingers = (e) => { this.#touches = (e.touches?.length ?? 0) > 0; };
    scroller.addEventListener('touchstart', () => { this.#touches = true; }, { passive: true });
    for (const end of ['touchend', 'touchcancel']) { scroller.addEventListener(end, fingers, { passive: true }); }
    scroller.addEventListener('pointerenter', (e) => { if (e.pointerType !== 'touch') { this.#hovering = true; } });
    scroller.addEventListener('pointerleave', (e) => { if (e.pointerType !== 'touch') { this.#hovering = false; } });
    scroller.addEventListener('pointerdown', (e) => {
      if (e.pointerType === 'touch') { return; }
      this.#pressing = true;
      const win = scroller.ownerDocument?.defaultView;
      const up = () => {
        this.#pressing = false;
        win?.removeEventListener('pointerup', up, true);
        win?.removeEventListener('pointercancel', up, true);
      };
      win?.addEventListener('pointerup', up, true);
      win?.addEventListener('pointercancel', up, true);
    });
  }

  // question: the one answered, which says what a confirmed answer's value was (answeredValue).
  // asker: who asked it, for the question kept once it is no longer waiting (renderResolved).
  #watchReceipt({ service, hostId, sessionId }, question, asker = null) {
    this.#retireReceipt();
    const questionId = question.id;
    // The line, and apart from it the one a screen reader hears it on (receipt.js).
    const line = h('p', { class: 'answer-receipt', tabindex: '-1' });
    const live = receiptLive();
    const element = h('div', { class: 'answer-receipt-slot' }, line, live);
    let resolved = null;
    // What the person answered, for the record the collapsed question opens to (renderResolved).
    let answer = null;
    let resolvedAnswer = null;
    // What the person typed, kept in view with Copy when the form could not take it (notDelivered).
    let kept = null;
    // The same request's clock as the form's was: an answer the form left at Passed to the agent
    // still stalls here, and a handoff inside the quiet window or a dwell still waits it out.
    const clock = new ReceiptClock({
      // Closed only once the session no longer lists the question; one still listed (not answerable
      // from here now) keeps the answer's own words, amber, beside it.
      describe: (entry, { stalled }) => (this.#s?.question?.id === questionId ? describeAnswer : describeClosed)(entry, this.machineName, { question, stalled }),
      draw: (d) => {
        // Its words change as the answer goes, above a question that may stay: whatever they move counts.
        this.#moves?.notOwn();
        // A question that is no longer asked is kept as it was asked, collapsed to this line: either
        // it closed ("Question closed", neutral), or its answer went through and it has left the list.
        const gone = this.#s?.question?.id !== questionId;
        const closed = d?.tone === 'closed';
        const collapsed = closed || (d?.key === 'delivered' && gone);
        // Only a closed one is kept past the next question (#retireReceipt); an answer that went is
        // dropped by it, as before.
        receipt.closed = closed;
        receipt.collapsed = collapsed;
        if (collapsed && d.key === 'delivered') {
          // The collapsed line says the question is answered. Its words never include the approval's
          // own message (#803): the line carries the decision's label only.
          d = { ...d, speech: 'Question answered' };
        }
        if (resolved && (!collapsed || resolvedAnswer !== answer)) {
          element.prepend(line);
          if (kept) { line.after(kept.element); }
          resolved.remove();
          resolved = null;
        }
        if (collapsed && !resolved) {
          // Its line is beside the button that opens the record, and what was kept with it under.
          resolvedAnswer = answer;
          resolved = renderResolved(question, { asker, answer });
          element.prepend(resolved);
          resolved.summary.prepend(line);
          if (kept) { resolved.append(kept.element); }
        }
        const words = d?.key === 'notDelivered' ? d.words ?? null : null;
        if (words && (!kept || kept.words !== words)) {
          kept?.element.remove();
          kept = { words, element: this.#keptWords(words) };
          if (resolved) { resolved.append(kept.element); } else { line.after(kept.element); }
        } else if (!words && kept) {
          kept.element.remove();
          kept = null;
        }
        // "You answered · value" says whose answer it is, and "No longer waiting" is about the question;
        // anything else says it is about the answer.
        drawReceipt(line, 'answer-receipt', !d?.text || ['delivered', 'closed', 'notDelivered'].includes(d.key) ? d : { ...d, text: `Your answer: ${d.text}` }, { enters: false });
      },
      say: (text) => { live.textContent = text; },
      heard: () => drawn(live),
      follow: (fn) => service.watch(hostId, sessionId, questionId, fn),
    });
    const receipt = { element, line, final: false, closed: false, collapsed: false, unwatch: null, fresh: true, clock, redraw: null };
    const show = (entry) => {
      if (!entry) { return; }
      line.dataset.answer = entry.state === 'done' ? entry.outcome : entry.state;
      answer = entry.state === 'done' && entry.outcome === 'delivered' ? entry.answer ?? null : null;
      clock.show(entry);
      receipt.final = entry.state === 'done';
    };
    // Drawn again when the session's list has changed what it says (the question has left since).
    receipt.redraw = () => show(service.get(hostId, sessionId, questionId));
    receipt.unwatch = service.watch(hostId, sessionId, questionId, show);
    show(service.get(hostId, sessionId, questionId));
    this.#receipt = receipt;
  }

  // What the person typed for an answer that did not go, kept in view to read and copy.
  #keptWords(words) {
    const ta = h('textarea', { class: 'text-input', readonly: true, rows: 3, 'aria-label': 'What you typed' });
    ta.value = words;
    const copy = h('button', {
      type: 'button',
      class: 'button secondary answer-copy',
      onclick: async () => {
        const ok = await copyText(words);
        copy.textContent = ok ? 'Copied' : "Couldn't copy";
        setTimeout(() => { copy.textContent = 'Copy'; }, LINGER_READ_MS);
      },
    }, 'Copy');
    return h('div', { class: 'answer-kept' }, ta, copy);
  }

  // A host takes a question away as it types the answer, before its result comes back. The panel
  // stays, with its ring on the control that sent the answer (ui.md §6.5, tap to answer, #617):
  // delivered, its check holds for sendCheckHold, then the card resolves to "You answered". Any
  // other result resolves the card at once to the question as asked with "No longer waiting" (or,
  // not confirmed, its words, neutral), kept in the conversation (ui.md §7): the cause of an amber
  // or red line is gone with the question, and nothing can be tried again for it.
  #leave(panel) {
    this.#dropLeaving();
    const { service, hostId, sessionId } = panel.answering;
    const leaving = { panel, unwatch: null, timer: null };
    const resolve = () => {
      if (this.#leaving !== leaving) { return; }
      this.#leaving = null;
      leaving.unwatch?.();
      this.#watchReceipt(panel.answering, panel.q, panel.asker);
      if (panel.element.isConnected) {
        const hadFocus = panel.element.contains(document.activeElement);
        this.#moves?.notOwn();
        panel.element.replaceWith(this.#receipt.element);
        if (hadFocus) { this.#focusLine(); }
        this.#receipt.fresh = false;
        enter(this.#receipt.element, 'acknowledge', { dy: 1 });
      }
      panel.element.remove();
      panel.destroy();
    };
    const settle = (entry) => {
      if (this.#leaving !== leaving || leaving.timer || !entry || entry.state === 'sending' || entry.state === 'waiting') { return; }
      if (entry.state === 'done' && entry.outcome === 'delivered') {
        // For what is left of its check: all of it, unless the result came before the question went.
        const until = panel.checkUntil;
        leaving.timer = setTimeout(resolve, until === null ? ReceiptTiming.sendCheckHoldMs : Math.max(0, until - Date.now()));
      } else {
        // After the watchers being told now, the panel's own among them: none is told of a view it has left.
        queueMicrotask(resolve);
      }
    };
    this.#leaving = leaving;
    leaving.unwatch = service.watch(hostId, sessionId, panel.questionId, settle);
    settle(service.get(hostId, sessionId, panel.questionId));
  }

  // The form collapsed into its line while focus was in it: focus goes to the line, not to the page.
  #focusLine() {
    if (this.#receipt?.collapsed) { this.#receipt.line.focus({ preventScroll: true }); }
  }

  #dropLeaving() {
    const l = this.#leaving;
    if (!l) { return; }
    this.#leaving = null;
    clearTimeout(l.timer);
    l.unwatch?.();
    l.panel.element.remove();
    l.panel.destroy();
  }

  #dropReceipt() {
    this.#destroyReceipt(this.#receipt);
    this.#receipt = null;
  }

  #destroyReceipt(r) {
    r?.unwatch?.();
    r?.clock.destroy();
    r?.element.remove();
  }

  // The receipt gives way to the next one, or to the next question. One that closed (its question
  // is no longer waiting) is kept, newest last, up to CLOSED_KEPT; any other is dropped. Whether it
  // was dropped: the question that follows moves up into its place.
  #retireReceipt() {
    const r = this.#receipt;
    if (!r) { return false; }
    if (!r.closed) {
      this.#dropReceipt();
      return true;
    }
    this.#receipt = null;
    this.#closed.push(r);
    while (this.#closed.length > CLOSED_KEPT) { this.#destroyReceipt(this.#closed.shift()); }
    return false;
  }

  // An answer went from the form: text kept for the person (#orphanView) that is in it has been sent.
  #sent(typed) {
    const o = this.#orphan;
    const text = o?.text.trim();
    if (text && typed.includes(text)) { this.#dropOrphan(); }
  }

  #dropOrphan() {
    const o = this.#orphan;
    if (!o) { return; }
    this.#orphan = null;
    o.element?.remove();
    this.#movedUnder();
  }

  // Text the person typed for a question that went, kept until they dismiss it or send it (ui.md §7).
  // A neutral note: what it says is over, so it isn't amber, and it stays through the next turn.
  #orphanView() {
    const o = this.#orphan;
    if (!o) { return null; }
    if (!o.element) {
      const ta = h('textarea', { class: 'text-input', readonly: true, rows: 3, 'aria-label': 'What you typed' });
      ta.value = o.text;
      o.element = h('div', { class: 'question-changed', role: 'status' },
        h('p', { class: 'note' }, o.why === 'changed'
          ? 'The question changed before you sent your answer. It was answered at the machine, or the agent moved on. What you typed is kept here.'
          : 'This question can no longer be answered from here. What you typed is kept here.'),
        ta,
        h('button', { type: 'button', class: 'button secondary', onclick: () => this.#dropOrphan() }, 'Dismiss'));
    }
    return o.element;
  }

  refreshTimes(now = Date.now()) {
    const s = this.#s;
    const e = this.els;
    this.#tickFacts(s, now);
    e.rowAge.textContent = ago(rowAt(s), now);
    this.#nameRow();
    e.agentAge.textContent = ago(mainVoice(s).at ?? s.updatedAt, now);
    if (this.#promptAt) { e.promptAge.textContent = ago(this.#promptAt, now); }
    this.delegates.refreshTimes(now);
    this.strip.refreshTimes(now);
    this.detail.refreshTimes(now);
    this.#renderEvents(s, now);
    // "last seen 2m ago" moves on with nothing from the machine, which is away.
    if (this.#stale && (this.#staleText(now)?.join(' ') ?? null) !== this.#shownStale) { this.update(s); }
  }

  #scheduleHistory() {
    if (this.#history.timer) { return; }
    this.#history.timer = setTimeout(() => { this.#history.timer = null; this.#loadHistory(); }, 2000);
  }

  async #loadHistory() {
    // A refresh queued while Tools showed is dropped if they no longer do (another page, tab or
    // session): revealing them again fetches what is stale.
    if (this.#history.loading || !this.fetchDetail || !this.#toolsShown()) { return; }
    this.#history.loading = true;
    const body = this.els.historyBody;
    if (!this.#history.loaded) { body.replaceChildren(h('p', { class: 'note', role: 'status' }, 'Loading…')); }
    const at = this.#s.updatedAt;
    try {
      const full = await this.fetchDetail(this.#s.id);
      if (!full) {
        body.replaceChildren(h('p', { class: 'note' }, 'This session has ended.'));
      } else {
        const list = full.activity.history ?? [];
        body.replaceChildren(list.length
          ? h('ol', { class: 'tools' }, list.slice(-50).reverse().map(toolRow))
          : h('p', { class: 'note' }, 'No tool calls yet.'));
      }
      this.#history.loaded = true;
      this.#history.at = at;
    } catch (e) {
      // The connection went under the request, and the next one asks again: not an error to show
      // as its raw text (#1128), which also named the relay.
      if (e?.reason === CONNECTION_CHANGED) {
        body.replaceChildren(h('p', { class: 'note', role: 'status' }, 'Connection changed. Trying again.'));
        this.#scheduleHistory();
      } else {
        body.replaceChildren(h('p', { class: 'note warn' }, `Couldn't load the history: ${e.message}`));
      }
    } finally {
      this.#history.loading = false;
    }
    // The session changed while this was on its way (an update then had nothing to refresh, or
    // found this still loading): what came may be older than it, so it is asked again.
    if (this.#history.loaded && this.#s.updatedAt !== at && this.#toolsShown()) { this.#scheduleHistory(); }
  }
}

function toolRow(t) {
  return h('li', { class: `tool ${t.status}`, dataset: { status: t.status } },
    h('span', { class: 'tool-mark' }, glyph(TOOL_GLYPH[t.status], { size: 12 })),
    h('span', { class: 'sr-only' }, `${t.status}: `),
    h('span', { class: 'tool-name' }, t.name),
    t.summary ? h('span', { class: 'tool-summary', title: t.summary }, t.summary) : null,
    t.startedAt ? h('time', { class: 'tool-time', datetime: t.startedAt, title: localTime(t.startedAt) }, ago(t.startedAt)) : null);
}

