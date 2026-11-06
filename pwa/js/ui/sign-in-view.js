// Sign-in (#800, ui.md §6.13): a banner per machine, agent and account whose agent needs a person
// to sign it in, over the sessions list, and the sheet a person's Sign in opens, with GitHub's code
// and github.com/login/device, Copy code and Open github.com. Then "signed in" or "recovered by
// itself" for a minute, or why it failed.
//
// The banners come from hello.machine.signIn and from each session's attention (a host whose
// hello is older than its sessions still gets one). The code comes only from the signin.code
// event of a flow this device's press started or joined: it is never in a session, a snapshot or
// hello, and it is kept here only while its sheet is open. Everything a host sent is read through
// ../auth-state.js first: only the allowlist's URL is ever a link, only a code of GitHub's shape is
// shown. Nothing here signs anyone in, enters a code, or opens a page unpressed; and the code is
// never logged, announced, stored or put in a notification.
import { h } from './dom.js';
import { glyph } from './glyph.js';
import { AGENT_NAME, RESOLVED_SHOWN_MS, SIGN_IN, clockTime, confirming, flowExpired, needsSignIn, readCode, readDone, readStartResult, spelled } from '../auth-state.js';

const plural = (n, one, many) => `${n} ${n === 1 ? one : many}`;
const uuid = () => globalThis.crypto?.randomUUID?.() ?? `${Date.now()}-${Math.random().toString(16).slice(2)}`;
// How long a signin.code or signin.done is held for a start whose result hasn't come yet.
const EARLY_MS = 10_000;
// A banner's drawing: rebuilt when this changes, so a needed banner's button never lingers.
const viewState = (b) => (b.state === 'needed' ? 'needed' : b.state === 'confirming' ? 'confirming' : 'resolved');
const keyOf = (hostId, agent, account) => `${hostId}\n${agent}\n${account ?? ''}`;

// What the banners show, from every machine shown. Pure, so each case is tested on its own.
// links: [{ hostId, name, rollUp: hello.machine.signIn, sessions: [session], canSignIn }]
// flows: Map key -> this device's flow ({ state, attempt, flow, code, outcome, reason })
// resolved: Map key -> { kind: 'signedIn' | 'recovered', count, at, agent, account, hostId, name }
// A banner is needed while any of its sessions needs sign-in; confirming once every one is signed
// in and waiting for the session to confirm (recovery pending); and resolved only once the host has
// cleared them, never on a signin.done alone (ui.md §6.13, Engineering's ruling on
// #800). A session that fails again while confirming makes it needed again.
export function signInBanners(links, { flows = new Map(), resolved = new Map(), now = Date.now() } = {}) {
  const out = [];
  const live = new Set();
  for (const { hostId, name, rollUp = [], sessions = [], canSignIn = false } of links) {
    const groups = new Map();
    const group = (agent, account) => {
      const k = keyOf(hostId, agent, account);
      if (!groups.has(k)) { groups.set(k, { key: k, agent, account: account ?? null, signIn: null, recovery: null, needed: [], confirming: [] }); }
      return groups.get(k);
    };
    // The sessions are the live word (patched as they change); hello is asked now and then. So the
    // groups, who is in them and how they can be signed in come from each session's own attention,
    // newest first, and the roll-up only fills in a recovery where no session says.
    for (const s of [...sessions].sort((a, b) => (a.attention?.since ?? '') < (b.attention?.since ?? '') ? 1 : -1)) {
      const at = s.attention;
      if (!needsSignIn(at) && !confirming(at)) { continue; }
      const g = group(s.agent, at.account);
      (confirming(at) ? g.confirming : g.needed).push(s);
      if (needsSignIn(at)) {
        // The newest session needing sign-in says how, and its saying nothing is no claim
        // (6648000166): an older session's phone or machine never re-enables an action.
        if (g.needed.length === 1) { g.signIn = at.signIn; }
        g.recovery ??= at.recovery;
      }
    }
    for (const e of rollUp) {
      const g = groups.get(keyOf(hostId, e.agent, e.account));
      if (!g?.needed.length) { continue; }
      // Never how it can be signed in: the sessions have said, and hello is asked now and then.
      g.recovery ??= e.recovery;
    }
    // A flow of this device's still under way keeps its banner while its sessions have gone, until it
    // says how it ended. One that has ended keeps nothing alive.
    for (const [k, f] of flows) {
      if (!k.startsWith(`${hostId}\n`) || groups.has(k) || !['starting', 'pending'].includes(f.state)) { continue; }
      const [, agent, account] = k.split('\n');
      group(agent, account || null);
    }
    for (const g of groups.values()) {
      live.add(g.key);
      const flow = flows.get(g.key) ?? null;
      const code = flow?.code && !flow.code.refused && flowExpired(flow.code, now) ? null : flow?.code ?? null;
      const expired = !!flow?.code && !flow.code.refused && !code;
      const waiting = !g.needed.length && g.confirming.length > 0 && !['starting', 'pending'].includes(flow?.state);
      const shown = g.needed.length ? g.needed : g.confirming;
      out.push({
        key: g.key,
        hostId,
        agent: g.agent,
        agentName: AGENT_NAME[g.agent] ?? AGENT_NAME.unknown,
        machine: name || 'this machine',
        account: g.account,
        state: waiting ? 'confirming' : 'needed',
        sessions: shown.map((s) => ({ id: s.id, title: s.title || 'Untitled session' })),
        // Only a provider the host says has no device flow is signed in at the machine. A value this
        // client doesn't know, or none, is not that (the no-RDP rule, #696).
        atMachine: g.signIn === 'machine',
        recovery: g.recovery,
        // Sign in only where the host says a person's device can (signIn phone): an unknown value or
        // none makes no claim, so no button either (6648000166).
        canSignIn: !waiting && canSignIn && g.signIn === 'phone',
        flow: flow ? { ...flow, code: expired ? null : code, expired: expired || flow.outcome === 'expired' } : null,
      });
    }
  }
  for (const [k, r] of resolved) {
    if (live.has(k) || !(now - r.at < RESOLVED_SHOWN_MS)) { continue; }
    out.push({ key: k, hostId: r.hostId, agent: r.agent, agentName: AGENT_NAME[r.agent] ?? AGENT_NAME.unknown, machine: r.name || 'this machine', account: r.account, state: r.kind, sessions: [], count: r.count, canSignIn: false, flow: null, atMachine: false, recovery: null });
  }
  return out;
}

// What a banner says, and what is read aloud when it comes or changes (ui.md §6.13): "Copilot on
// DEV-VM1 needs sign-in · @octocat" over "2 sessions affected: Fix the relay, Draft the notes".
export function bannerWords(b) {
  const who = `${b.agentName} on ${b.machine}`;
  const acct = b.account ? ` \u00b7 @${b.account}` : '';
  if (b.state === 'signedIn') {
    const line = `Signed in \u00b7 ${plural(b.count ?? 0, 'session', 'sessions')} recovered`;
    return { head: `${who}${acct}`, line, said: `${who}: ${line}` };
  }
  if (b.state === 'recovered') { return { head: `${who}${acct}`, line: 'Recovered by itself', said: `${who} recovered by itself` }; }
  if (b.state === 'confirming') {
    const line = `Signed in \u00b7 waiting for ${plural(b.sessions.length, 'session', 'sessions')} to confirm`;
    return { head: `${who}${acct}`, line, said: `${who}: ${line}` };
  }
  const line = stateLine(b) ?? `${affected(b)}${b.sessions.map((s) => s.title).join(', ')}`;
  return { head: `${who} needs sign-in${acct}`, line, said: `${who} needs sign-in` };
}

// What a needed banner says instead of its sessions, or null when it has nothing else to say.
function stateLine(b) {
  const f = b.flow;
  if (b.atMachine) { return 'Sign in at the machine'; }
  if (f?.outcome === 'failed') { return f.reason ? `Sign-in failed \u00b7 ${f.reason}` : 'Sign-in failed'; }
  if (f?.outcome === 'refused') { return f.reason ? `Sign-in refused \u00b7 ${f.reason}` : 'Sign-in refused'; }
  if (f?.expired) { return 'Code expired'; }
  if (f?.code && !f.code.refused) { return `Waiting for sign-in \u00b7 code expires ${clockTime(f.code.expiresAt)}`; }
  if (b.recovery === 'checking') { return 'Checking for a sign-in that still works\u2026'; }
  if (b.recovery === 'signingIn' && !f) { return 'Waiting for sign-in'; }
  return null;
}

// "2 sessions affected: " before the sessions' titles, each a link to its session (ui.md §6.13,
// owner decision 6675817525), with no line of titles of its own; without a session, the count alone.
const affected = (b) => `${plural(b.sessions.length, 'session', 'sessions')} affected${b.sessions.length ? ': ' : ''}`;

// Whether the banner's line names its sessions: a needed banner with nothing else to say.
const listsSessions = (b) => b.state === 'needed' && b.sessions.length > 0 && stateLine(b) === null;

// What is said aloud as a banner changes, once each (§9.1 A6): every way an attempt can end, as the
// banner or the sheet shows it, so none passes unsaid. Never the machine's own words, nor the code.
const SAID_KEEP_MS = 10000;

export function bannerSaid(b) {
  const who = `${b.agentName} on ${b.machine}`;
  if (b.state !== 'needed') { return { state: b.state, words: bannerWords(b).said }; }
  const f = b.flow;
  if (b.atMachine) { return { state: 'machine', words: `${who} needs sign-in at the machine` }; }
  if (f?.outcome === 'failed') { return { state: 'failed', words: `Sign-in failed for ${who}` }; }
  if (f?.outcome === 'refused') { return { state: 'refused', words: `Sign-in refused for ${who}` }; }
  if (f?.expired || f?.outcome === 'expired') { return { state: 'expired', words: `Sign-in code expired for ${who}` }; }
  if (f?.code?.refused) { return { state: 'unshown', words: `The sign-in code for ${who} couldn\u2019t be shown` }; }
  return { state: 'needed', words: bannerWords(b).said };
}

// The one verb a banner offers (§9.1 A7): Sign in; Show code while a flow runs that this device
// isn't showing; Try again after one that failed, expired or was refused. None while confirming.
export function bannerVerb(b) {
  if (!b.canSignIn) { return null; }
  const f = b.flow;
  if (f && (f.state === 'starting' || f.state === 'pending') && !f.expired) { return null; }
  if (f?.expired || ['failed', 'refused', 'expired'].includes(f?.outcome)) { return 'Try again'; }
  if (b.recovery === 'signingIn') { return 'Show code'; }
  return 'Sign in';
}

export class SignInBanners {
  #views = new Map();
  #said = new Map();
  #flows = new Map();
  #resolved = new Map();
  // The last word of each banner still needing sign-in, until it is resolved: what "recovered by
  // itself" or "signed in" names once it has gone.
  #last = new Map();
  // signin.code and signin.done that came before signin.start's result named their flow: held
  // as read (never the raw event, whose other fields could be anything), only while a start is
  // under way, and for EARLY_MS (or until that start settles) at most.
  #early = [];
  #scheduled = false;
  #sheet = null;
  #focusBack = null;
  // What this render has to say: two machines' changes in one update are both said, one after the
  // other, never the second written over the first (#speak).
  #toSay = [];
  // Cancels sent for a banner's flows and not yet finished, by its key: its next start waits for
  // them, so it can't join the flow being stopped (busy) and then end with it.
  #cancelling = new Map();
  // Where a person here signed in (signin.done signedIn), by banner: once the host clears its
  // sessions, that reads "Signed in · N sessions recovered" rather than "Recovered by itself".
  #signedHere = new Map();

  // container: where the banners go. hosts: a HostSession. live: a polite live region on the page
  // (role=status), empty until something is said. open(hostId, id): shows a session. fallback():
  // where focus goes when the sheet closes with its opener and banner both gone (§6.13: the
  // Sessions heading).
  constructor(container, hosts, { live = null, open = () => {}, fallback = () => null, clipboard = globalThis.navigator?.clipboard ?? null, now = () => Date.now() } = {}) {
    Object.assign(this, { container, hosts, live, open, fallback, clipboard, now });
    this.unwatch = [
      hosts.sessions.onChange((c) => { if (c?.type === 'signin') { this.#event(c.hostId, c.ev, c.data); } this.schedule(); }),
      hosts.onChange(() => this.schedule()),
    ];
    // A code runs out, and "signed in" goes, with nothing from the machine.
    this.ticker = setInterval(() => this.render(), 5000);
    this.render();
  }

  // How many codes this device holds, in flows and held early: none survives a cancel, an expiry or
  // its machine going away. For the tests.
  get codesHeld() {
    return [...this.#flows.values()].filter((f) => f.code && !f.code.refused).length
      + this.#early.filter((e) => e.ev === SIGN_IN.code && !e.read.refused && !e.read.expired).length
      + [...this.#last.values()].filter((b) => b.flow?.code).length;
  }

  schedule() {
    if (this.#scheduled) { return; }
    this.#scheduled = true;
    queueMicrotask(() => { this.#scheduled = false; this.render(); });
  }

  #links() {
    const out = [];
    for (const id of this.hosts.shownIds?.() ?? []) {
      const link = this.hosts.link(id);
      // A machine away (#269): what it last said may no longer be so, and nothing can be sent.
      if (!link?.store || this.hosts.staleOf?.(id)) { continue; }
      out.push({
        hostId: id,
        name: this.hosts.nameOf(id),
        rollUp: link.machine?.signIn ?? [],
        sessions: link.store.list(),
        canSignIn: (link.capabilities ?? []).includes(SIGN_IN.capability) && !link.machine?.readOnly,
      });
    }
    return out;
  }

  render() {
    if (this.stopped) { return; }
    const now = this.now();
    const links = this.#links();
    const current = new Set(links.map((l) => l.hostId));
    // A machine no longer here (another picked, expired, forgotten): nothing of it stays on screen,
    // not even how its last sign-in ended. One merely away keeps its link, and what it last said.
    if (typeof this.hosts.link === 'function') {
      const owned = (id) => { const l = this.hosts.link(id); return !!l && !l.expired; };
      for (const [k, r] of [...this.#resolved]) { if (!owned(r.hostId)) { this.#resolved.delete(k); } }
      for (const [k, b] of [...this.#last]) { if (!owned(b.hostId)) { this.#last.delete(k); this.#signedHere.delete(k); } }
    }
    // No code outlives its use: one that has expired is dropped (the sheet says so), and a flow under
    // way on a machine gone away is let go with its code, so that once it is back a press joins it
    // again (busy) rather than finding a banner with nothing to press.
    for (const [k, f] of [...this.#flows]) {
      if (['starting', 'pending'].includes(f.state) && !current.has(k.split('\n')[0])) { this.#flows.delete(k); }
      else if (f.code && !f.code.refused && flowExpired(f.code, now)) { this.#flows.set(k, { ...f, code: null, outcome: 'expired' }); }
    }
    this.#early = this.#early.map((e) => this.#lapsed(e, now)).filter((e) => current.has(e.hostId) && this.#stillHeld(e, now));
    const banners = signInBanners(links, { flows: this.#flows, resolved: this.#resolved, now });
    // A banner that went without a press here: its sessions no longer need sign-in, so it
    // recovered by itself (or another device signed it in). One whose sessions went too says nothing.
    const live = new Set(banners.map((b) => b.key));
    // Every session a banner has held up since it came: they confirm one by one, so the last word
    // alone would count only the last of them as recovered.
    for (const b of banners) {
      if (b.state !== 'needed' && b.state !== 'confirming') { continue; }
      // A new episode: the last one's "recovered" is over, and this one gets its own when it ends.
      this.#resolved.delete(b.key);
      const all = new Map((this.#last.get(b.key)?.all ?? []).map((s) => [s.id, s]));
      for (const s of b.sessions) { all.set(s.id, s); }
      // Its words and sessions only: never its flow, nor so its code.
      this.#last.set(b.key, { ...b, flow: null, all: [...all.values()] });
    }
    for (const [k, b] of [...this.#last]) {
      if (live.has(k)) { continue; }
      // A machine away (#269), or not shown: what it last said stands until it is back.
      if (!current.has(b.hostId)) { continue; }
      // A flow of this device's under way: its signin.done says how it went.
      if (['starting', 'pending'].includes(this.#flows.get(k)?.state)) { continue; }
      this.#last.delete(k);
      if (this.#resolved.has(k)) { continue; }
      // Recovered only by the sessions' own word: still there, and cleared by the host (or resumed).
      const cleared = b.all.map((s) => this.hosts.sessions.get?.(b.hostId, s.id)).filter((s) => s && !needsSignIn(s.attention) && !confirming(s.attention));
      const here = this.#signedHere.has(k);
      this.#signedHere.delete(k);
      if (cleared.length) { this.#resolve(b, here ? 'signedIn' : 'recovered', cleared.length); }
    }
    // An ended flow whose banner has gone keeps nothing.
    for (const [k, f] of [...this.#flows]) { if (f.state === 'done' && !live.has(k)) { this.#flows.delete(k); } }
    const all = signInBanners(links, { flows: this.#flows, resolved: this.#resolved, now });
    let prev = null;
    const keep = new Set();
    for (const b of all) {
      keep.add(b.key);
      let view = this.#views.get(b.key);
      // Drawn anew for its new state (needed to confirming): focus inside the old one moves to the
      // new one's line, never left on a removed element (nowhere).
      let refocus = false;
      if (!view || view.state !== viewState(b)) {
        refocus = !!view?.el.contains(this.container.ownerDocument?.activeElement);
        view?.el.remove();
        view = this.#build(b);
        this.#views.set(b.key, view);
      }
      this.#fill(view, b);
      const at = prev ? prev.nextSibling : this.container.firstChild;
      if (at !== view.el) { this.container.insertBefore(view.el, at); }
      if (refocus) { const key = b.key; this.#focusOn(() => this.#lineOf(key)); }
      prev = view.el;
      this.#announce(b);
    }
    for (const [key, view] of this.#views) {
      if (!keep.has(key)) { view.el.remove(); this.#views.delete(key); this.#said.delete(key); }
    }
    for (const [k, r] of this.#resolved) { if (!(now - r.at < RESOLVED_SHOWN_MS)) { this.#resolved.delete(k); } }
    this.container.hidden = all.length === 0;
    this.#speak();
    this.#drawSheet(all.find((b) => b.key === this.#sheet?.key) ?? null);
    // Back to the control that opened the sheet, or, when that has gone, its banner's status line
    // ("Signed in · waiting for…" once a person signed in, as in the app), else the banner (§9.1 A4):
    // after drawing, as the button is not there while its flow runs. With all gone (the machine
    // away), the Sessions heading (§6.13), never nowhere.
    const back = this.#focusBack;
    this.#focusBack = null;
    if (back) {
      this.#focusOn(() => (back.opener?.isConnected ? back.opener : this.#lineOf(back.key)), back.restored);
    }
  }

  // Focus on target, else the Sessions heading. WebKit can refuse focus to an element drawn in the
  // same task the modal sheet closed in (the banner drawn anew for confirming), so a refused target is
  // tried once more on the next frame, as the app tries again after the next layout (FocusOnce), if
  // focus is still nowhere by then: the page, or the closed sheet (Chromium leaves it on Cancel a while).
  // pick() is asked again for the retry: the banner may have been drawn anew in between (the host's
  // pending after signin.done), so its line then is a new element. restored: where the closing
  // dialog put focus back by itself (the opener), which is no person's move, so the retry goes on.
  #focusOn(pick, restored = null) {
    const doc = this.container.ownerDocument;
    const take = (el) => {
      if (!el?.isConnected) { return false; }
      el.focus({ preventScroll: true });
      return doc?.activeElement === el;
    };
    const heading = () => {
      const h = this.fallback();
      if (h && !h.hasAttribute('tabindex')) { h.setAttribute('tabindex', '-1'); }
      take(h);
    };
    const target = pick();
    if (take(target)) { return; }
    if (!target?.isConnected) { heading(); return; }
    const later = globalThis.requestAnimationFrame ?? ((f) => setTimeout(f, 16));
    later(() => {
      const at = doc?.activeElement;
      if (at && at !== doc.body && at !== restored && at.isConnected && !at.closest?.('dialog:not([open])')) { return; }
      if (!take(pick())) { heading(); }
    });
  }

  // A banner's status line where it is shown, else the banner, by key: whichever is drawn now.
  #lineOf(key) {
    const view = this.#views.get(key);
    return view && !view.els.line.hidden ? view.els.line : view?.el;
  }

  #resolve(b, kind, count) {
    this.#resolved.set(b.key, { kind, count, at: this.now(), agent: b.agent, account: b.account, hostId: b.hostId, name: b.machine });
    this.#flows.delete(b.key);
    this.#last.delete(b.key);
  }

  // Said once as it comes, and once when it is resolved (§9.1 A6): never the code.
  #announce(b) {
    // Politely, once a state: needed, each way an attempt can end, confirming, signed in or recovered.
    const { state, words } = bannerSaid(b);
    if (this.#said.get(b.key) === state) { return; }
    this.#said.set(b.key, state);
    this.#toSay.push(words);
  }

  // Everything this render had to say, each a fresh line added to the live region, which reads
  // additions only (aria-atomic false, aria-relevant additions in index.html): a screen reader reads
  // every new line once, and never the lines already there again. Lines go once SAID_KEEP_MS old,
  // which bounds the region; there is no count limit, so no line of a render is ever dropped.
  #speak() {
    const words = this.#toSay;
    this.#toSay = [];
    if (!this.live) { return; }
    const now = this.now();
    for (const old of [...this.live.children]) { if (!(now - Number(old.dataset.at) < SAID_KEEP_MS)) { old.remove(); } }
    if (!words.length) { return; }
    this.live.append(...words.map((w) => h('p', { class: 'sign-in-said-line', dataset: { at: String(now) } }, w)));
  }

  #build(b) {
    const view = { key: b.key, state: viewState(b), entry: b, els: {} };
    const els = view.els;
    els.head = h('p', { class: 'sign-in-head' });
    // Focusable from script only (never a Tab stop): where focus goes once the button has gone.
    // While the banner names its sessions, each title in it is a button that opens that session.
    els.line = h('p', { class: 'sign-in-line', tabindex: '-1' });
    // Not a live region: it is in the list pane, which a phone hides, and drawn on every render. A
    // failed start is said politely through the sign-in live region instead (#start).
    els.problem = h('p', { class: 'sign-in-problem' });
    els.actions = h('div', { class: 'sign-in-actions' });
    els.verb = h('button', { type: 'button', class: 'button sign-in-start', onclick: (e) => this.#start(view, e.detail > 0) });
    view.el = h('section', { class: 'banner sign-in-banner', tabindex: '-1', dataset: { key: b.key, hostId: b.hostId, agent: b.agent } },
      h('span', { class: 'sign-in-mark' }, glyph(view.state === 'needed' ? 'needs' : 'idle', { size: 16 })),
      h('div', { class: 'sign-in-body' }, els.head, els.line, els.problem, els.actions));
    return view;
  }

  #fill(view, b) {
    view.entry = b;
    const { els } = view;
    const words = bannerWords(b);
    const subject = `${b.agentName} on ${b.machine}`;
    view.el.dataset.state = b.state === 'needed' ? (b.flow?.code ? 'pending' : b.flow?.outcome ?? (b.flow?.expired ? 'expired' : b.recovery ?? 'needed')) : b.state;
    view.el.setAttribute('aria-label', words.head);
    els.head.textContent = words.head;
    // Drawn again only when its words change, so a session's button with focus keeps it.
    const lists = listsSessions(b);
    const drawn = `${lists}\n${words.line}\n${b.sessions.map((s) => s.id).join('\n')}`;
    if (view.drawn !== drawn) {
      view.drawn = drawn;
      if (lists) {
        // Each button keeps only where it goes, never the banner, whose flow may hold a code.
        const { hostId } = b;
        const parts = [affected(b)];
        b.sessions.forEach(({ id, title }, i) => {
          if (i > 0) { parts.push(', '); }
          parts.push(h('button', { type: 'button', class: 'sign-in-session', onclick: () => this.open(hostId, id) }, title));
        });
        els.line.replaceChildren(...parts);
      } else {
        els.line.textContent = words.line;
      }
    }
    els.line.hidden = !words.line;
    const problem = this.#flows.get(b.key)?.problem ?? '';
    if (els.problem.textContent !== problem) { els.problem.textContent = problem; }
    els.problem.hidden = !problem;
    const verb = bannerVerb(b);
    const busy = this.#flows.get(b.key)?.state === 'starting';
    if (verb) {
      els.verb.textContent = verb;
      els.verb.setAttribute('aria-label', verb === 'Show code' ? `Show code for ${subject}` : verb === 'Try again' ? `Try again to sign in to ${subject}` : `Sign in to ${subject}`);
    }
    // In flight, it keeps focus and says it is busy (§9.1 A4), and a second press sends nothing.
    els.verb.setAttribute('aria-disabled', busy ? 'true' : 'false');
    const want = verb ? [els.verb] : [];
    if (want.length !== els.actions.childElementCount) { els.actions.replaceChildren(...want); }
    els.actions.hidden = want.length === 0;
  }

  // This device's own flow: started on a press, its code from signin.code, how it ended from
  // signin.done. A flow id this device didn't start or join is not its to show.
  #event(hostId, ev, data) {
    const read = ev === SIGN_IN.code ? readCode(data) : ev === SIGN_IN.done ? readDone(data) : null;
    if (read) { this.#apply(hostId, ev, read); }
  }

  // A signin.code or signin.done as read (auth-state.js): nothing else of the event is kept.
  #apply(hostId, ev, read) {
    const entry = [...this.#flows.entries()].find(([k, f]) => k.startsWith(`${hostId}\n`) && f.flow && f.flow === read.flow);
    if (!entry) {
      // Held a moment for a start whose result hasn't come yet; never shown unless it names its flow.
      if (this.#startingOn(hostId)) { this.#early.push({ hostId, ev, read, at: this.now() }); }
      return;
    }
    const [key, f] = entry;
    if (ev === SIGN_IN.code && read.expired) {
      // A code that ran out while its start's result was on the way: the sheet says so, Try again.
      this.#flows.set(key, { ...f, state: 'pending', code: null, outcome: 'expired' });
    } else if (ev === SIGN_IN.code) {
      const code = read;
      this.#flows.set(key, { ...f, state: 'pending', outcome: undefined, code: code.refused ? { refused: true } : code });
    } else if (ev === SIGN_IN.done) {
      const done = read;
      if (done.outcome === 'signedIn') {
        // Signing in isn't recovery (ui.md §6.13): the banner and rows follow the host, which says
        // pending (waiting for the session to confirm) and clears the sessions only once the model
        // answers. All this device keeps is that a person here signed in.
        this.#flows.delete(key);
        this.#signedHere.set(key, this.now());
        this.#closeSheet(key);
        // Focus to the banner's line, never back to Sign in: the host's pending may come after this,
        // and with it Sign in goes, which would leave focus nowhere. The closing dialog itself puts
        // focus back on Sign in, so a retry finding it there still moves it on (r4485757956).
        if (this.#focusBack?.key === key) { this.#focusBack.restored = this.#focusBack.opener; this.#focusBack.opener = null; }
      } else if (done.outcome === 'cancelled') {
        this.#flows.delete(key);
        this.#closeSheet(key);
      } else {
        this.#flows.set(key, { state: 'done', flow: null, outcome: done.outcome, reason: done.reason, code: null });
        this.#closeSheet(key);
      }
    }
    this.schedule();
  }

  // Only a person's press sends it, saying only which agent and account: the machine runs the
  // agent's own sign-in, and the person enters the code at github.com themselves.
  // pointer: pressed with a pointer (a click or a tap, not a key), so the sheet's focus is drawn
  // without a ring (§9.1 A12).
  async #start(view, pointer = false) {
    const b = view.entry;
    const f = this.#flows.get(b.key);
    if (f?.state === 'starting') { return; }
    const client = this.hosts.route?.(b.hostId)?.client;
    if (!client) { this.#flows.set(b.key, { state: 'done', problem: `${b.machine} can\u2019t be reached now` }); this.render(); return; }
    // This press's own: a result that comes back after a Cancel, or after a later press on the
    // same banner, is not this attempt's to apply.
    const attempt = uuid();
    const mine = () => this.#flows.get(b.key)?.attempt === attempt;
    this.#flows.set(b.key, { state: 'starting', attempt, flow: null, code: null });
    this.#openSheet(b.key, view.els.verb, pointer);
    this.render();
    if (this.#cancelling.has(b.key)) {
      await this.#awaitCancels(b.key);
      // Cancelled, or pressed again, while it waited: not this attempt's to start.
      if (!mine()) { return; }
      // The host stopped offering this device sign-in while it waited: nothing is sent, and the
      // sheet goes, rather than saying "Starting…" for a start that never comes.
      if (!this.#views.get(b.key)?.entry?.canSignIn) {
        this.#flows.delete(b.key);
        this.#closeSheet(b.key);
        this.render();
        return;
      }
    }
    let r;
    try {
      r = readStartResult(await client.request(SIGN_IN.start, { agent: b.agent, ...(b.account ? { account: b.account } : {}), request: attempt }));
    } catch {
      if (!mine()) { return; }
      this.#dropOrphans(b.key, this.#flows.get(b.key)?.orphans, null);
      // The machine's own words are not shown: they could be anything.
      this.#flows.set(b.key, { state: 'done', problem: 'Couldn\u2019t start sign-in. Try again.' });
      this.#dropHostHeld(b.hostId);
      this.#closeSheet(b.key);
      // Said, politely: the sheet went, and the line that says why isn't read on its own.
      this.#toSay.push(`Couldn\u2019t start sign-in for ${b.agentName} on ${b.machine}. Try again.`);
      this.render();
      return;
    }
    if (!mine()) {
      // Cancelled while it started: the flow it started is cancelled too, unless a later press here
      // joined that same flow. While that press's own result hasn't come, it can't be known: the
      // flow and what came for it wait for that result (#dropOrphans).
      const now = this.#flows.get(b.key);
      // Only a flow it started: one it joined (busy) is another device's sign-in.
      if (!r.flow || r.outcome !== 'started') { return; }
      if (now?.state === 'starting') { this.#flows.set(b.key, { ...now, orphans: [...(now.orphans ?? []), r.flow] }); return; }
      if (now?.flow !== r.flow) {
        this.#cancelInBackground(b.key, r.flow);
        this.#dropFlowHeld(b.hostId, r.flow);
      }
      return;
    }
    this.#dropOrphans(b.key, this.#flows.get(b.key)?.orphans, r.flow);
    if (r.outcome === 'started' || r.outcome === 'busy') {
      // Joined: another device's flow. Cancel here only closes the sheet (#cancel).
      this.#flows.set(b.key, { state: 'pending', attempt, flow: r.flow, code: null, joined: r.outcome === 'busy' });
      // What came for it before this result did.
      const early = this.#early.map((e) => this.#lapsed(e, this.now())).filter((e) => e.hostId === b.hostId && e.read.flow === r.flow);
      this.#early = this.#early.map((e) => this.#lapsed(e, this.now())).filter((e) => !early.includes(e) && this.#stillHeld(e, this.now()));
      for (const e of early) { this.#apply(e.hostId, e.ev, e.read); }
      // What is left came for a flow no start here can claim now (a cancelled one's late code):
      // dropped, unless another start on this machine is still under way.
      this.#dropHostHeld(b.hostId);
    } else {
      if (r.flow) { this.#dropFlowHeld(b.hostId, r.flow); }
      // Refused (any outcome the protocol doesn't define reads so). "Sign in at the machine" comes
      // only from the host's signIn: machine, never from a start's result.
      this.#flows.set(b.key, { state: 'done', outcome: 'refused', reason: r.reason });
      // No flow of its own to claim it: what came early on this machine goes now, not after
      // EARLY_MS, unless another start there is still under way.
      this.#dropHostHeld(b.hostId);
      this.#closeSheet(b.key);
    }
    this.render();
  }

  // Flows abandoned starts here began, once the start that followed them says which it is in: each
  // other one is cancelled at the machine, with whatever came for it.
  #dropOrphans(key, orphans, joined) {
    const hostId = key.split('\n')[0];
    for (const o of orphans ?? []) {
      if (o === joined) { continue; }
      this.#cancelInBackground(key, o);
      this.#dropFlowHeld(hostId, o);
    }
  }

  // What came early for one machine's flow: never another machine's, whose flow ids are its own.
  #dropFlowHeld(hostId, flow) { this.#early = this.#early.filter((e) => e.hostId !== hostId || e.read.flow !== flow); }

  #startingOn(hostId) { return [...this.#flows].some(([k, x]) => x.state === 'starting' && k.startsWith(`${hostId}\n`)); }

  // Held for EARLY_MS, and past it while a start on its machine is still under way (its result can
  // take longer). Cancel, failure and the machine going away drop it at once.
  #stillHeld(e, now) { return now - e.at < EARLY_MS || this.#startingOn(e.hostId); }

  // A held code that has run out keeps only that it did, never the code: replayed when its start
  // settles, the sheet says "Code expired" and offers Try again rather than waiting on "Starting…".
  #lapsed(e, now) {
    if (e.ev !== SIGN_IN.code || e.read.refused || e.read.expired || !flowExpired(e.read, now)) { return e; }
    return { ...e, read: { flow: e.read.flow, expired: true } };
  }

  // What came early for a machine, once no start there is under way to claim it.
  #dropHostHeld(hostId) {
    if (this.#startingOn(hostId)) { return; }
    this.#early = this.#early.filter((e) => e.hostId !== hostId);
  }

  #cancel(key) {
    const f = this.#flows.get(key);
    this.#flows.delete(key);
    if (key) { this.#dropOrphans(key, f?.orphans, null); }
    // Nothing held for it: its flow's, and its machine's unless another start there is under way.
    const hostId = key?.split('\n')[0];
    if (f?.flow) { this.#dropFlowHeld(hostId, f.flow); }
    this.#dropHostHeld(hostId);
    this.#closeSheet();
    this.render();
    if (f?.flow && !f.joined) { this.#cancelInBackground(key, f.flow); }
  }

  // Until no cancel for the banner is still under way. Each stays registered until it has finished,
  // so a press after this attempt is cancelled mid-wait waits for it too.
  async #awaitCancels(key) {
    for (let pending = this.#cancelling.get(key); pending; pending = this.#cancelling.get(key)) {
      await pending;
      if (this.#cancelling.get(key) === pending) { this.#cancelling.delete(key); }
    }
  }

  // A cancel sent for a banner's flow, which its next start waits for (#cancelling).
  #cancelInBackground(key, flow) {
    const before = this.#cancelling.get(key);
    this.#cancelling.set(key, Promise.all([before, this.#cancelFlow(key.split('\n')[0], flow)]));
  }

  async #cancelFlow(hostId, flow) {
    try { await this.hosts.route?.(hostId)?.client?.request(SIGN_IN.cancel, { flow }); } catch { /* the code expires with the CLI's own flow */ }
  }

  // The sheet (ui.md §6.13): a modal dialog, so focus stays in it; Cancel is where it starts and
  // what Escape does. It shows the URL as plain text with Open, the code in mono with Copy, and
  // when it expires.
  #openSheet(key, opener, pointer = false) {
    if (!this.#sheet) {
      const els = {};
      els.title = h('h2', { class: 'sign-in-sheet-title', id: 'sign-in-sheet-title' });
      els.body = h('div', { class: 'sign-in-sheet-body' });
      els.cancel = h('button', { type: 'button', class: 'button secondary sign-in-cancel', autofocus: true, onclick: () => this.#cancel(this.#sheet?.key) }, 'Cancel');
      els.dialog = h('dialog', { class: 'sign-in-sheet', 'aria-labelledby': 'sign-in-sheet-title' }, els.title, els.body, h('div', { class: 'sign-in-sheet-actions' }, els.cancel));
      // Escape is Cancel.
      els.dialog.addEventListener('cancel', (e) => { e.preventDefault(); this.#cancel(this.#sheet?.key); });
      globalThis.document.body.append(els.dialog);
      this.#sheet = { els, key: null, opener: null, shape: null };
    }
    this.#sheet.key = key;
    this.#sheet.opener = opener;
    this.#sheet.shape = null;
    if (!this.#sheet.els.dialog.open) { this.#sheet.els.dialog.showModal(); }
    this.#sheet.els.cancel.focus(pointer ? { focusVisible: false } : undefined);
  }

  // key: close it only if it is that banner's sheet (a late result never closes another's).
  #closeSheet(key) {
    const s = this.#sheet;
    if (!s?.els.dialog.open || (key !== undefined && s.key !== key)) { return; }
    s.els.dialog.close();
    this.#focusBack = { opener: s.opener, key: s.key };
    // Handed on: the sheet keeps no hold on the banner that opened it, nor so on that banner's code.
    s.opener = null;
    s.key = null;
    // The code goes with the sheet: nothing of it stays on the page.
    s.els.body.replaceChildren();
    s.shape = null;
    this.schedule();
  }

  #drawSheet(b) {
    const s = this.#sheet;
    if (!s?.els.dialog.open) { return; }
    if (!b) {
      // Its machine went away (#269) or stopped being shown: the flow is let go here, so that once it
      // is back a press joins it again (busy) rather than finding a banner with nothing to press.
      const f = this.#flows.get(s.key);
      if (['starting', 'pending'].includes(f?.state)) { this.#flows.delete(s.key); }
      this.#dropHostHeld(s.key?.split('\n')[0]);
      this.#closeSheet();
      return;
    }
    const subject = `${b.agentName} on ${b.machine}`;
    const f = b.flow;
    const kind = f?.code?.refused ? 'refused' : f?.code ? `${f.code.code}\n${f.code.uri}\n${f.code.expiresAt}` : f?.expired ? 'expired' : 'starting';
    // Try again only while the host still offers this device sign-in (sessions.signin, not
    // read-only, the newest session's signIn phone): drawn again when that changes.
    const shape = `${kind}\n${b.canSignIn}`;
    s.els.title.textContent = `Sign in to ${subject}`;
    s.els.cancel.setAttribute('aria-label', `Cancel sign-in for ${subject}`);
    if (s.shape === shape) { return; }
    s.shape = shape;
    const retry = b.canSignIn ? [this.#retryButton(subject)] : [];
    let parts;
    if (kind === 'refused') {
      // Not GitHub's link, or not GitHub's kind of code: possibly phishing, so none of it is shown.
      // Not "Sign in at the machine": only a host saying the provider has no device flow may claim
      // that (the no-RDP rule, #696).
      parts = [h('p', { class: 'sign-in-refused' }, 'This sign-in code couldn\u2019t be shown.'), ...retry];
    } else if (kind === 'expired') {
      parts = [h('p', { class: 'sign-in-expired' }, 'Code expired'), ...retry];
    } else if (f?.code) {
      const { code, uri, expiresAt } = f.code;
      parts = [
        h('p', { class: 'sign-in-where' }, 'Open this page and enter the code.'),
        h('p', { class: 'sign-in-uri mono', dir: 'ltr' }, uri.replace(/^https:\/\//, '')),
        // Read a character at a time, as it is typed: "Code: W D J B, M J H T".
        // aria-label isn't allowed on <code> (ARIA 1.2): the spelled code is its own text for screen
        // readers, and the visible one, still selectable, is hidden from them.
        h('p', { class: 'sign-in-code-line' }, h('span', { class: 'sr-only sign-in-code-spelled' }, spelled(code)), h('code', { class: 'sign-in-code', dir: 'ltr', 'aria-hidden': 'true' }, code)),
        h('p', { class: 'sign-in-expiry' }, h('time', { dateTime: expiresAt }, `Expires ${clockTime(expiresAt)}`)),
        h('div', { class: 'sign-in-flow-actions' },
          h('button', { type: 'button', class: 'button sign-in-copy', 'aria-label': `Copy code for ${subject}`, onclick: () => this.#copy(code) }, 'Copy'),
          // The allowlist's own string (auth-state.js), opened only by this press, in a new tab
          // that can't reach back to this one.
          h('a', { class: 'button secondary sign-in-open', href: uri, target: '_blank', rel: 'noopener noreferrer', 'aria-label': `Open github.com to sign in to ${subject}` }, 'Open')),
        h('p', { class: 'sign-in-copied', role: 'status', 'aria-live': 'polite' }),
      ];
    } else {
      parts = [h('p', { class: 'sign-in-starting' }, `Starting ${b.agentName}\u2019s sign-in on ${b.machine}\u2026`)];
    }
    s.els.body.replaceChildren(...parts);
  }

  // Try again, in the sheet: a new request, the one before cancelled at the machine.
  #retryButton(subject) {
    return h('div', { class: 'sign-in-flow-actions' },
      h('button', { type: 'button', class: 'button sign-in-retry', 'aria-label': `Try again to sign in to ${subject}`, onclick: (e) => this.#retry(e.detail > 0) }, 'Try again'));
  }

  // The flow before is cancelled at the machine first, and only then a new request sent, so the new
  // one can't join the flow being stopped (busy) and end with it. Guarded by an attempt: a Cancel, or
  // the host no longer offering sign-in, while it waits starts nothing.
  async #retry(pointer) {
    const key = this.#sheet?.key;
    const view = key ? this.#views.get(key) : null;
    if (!view?.entry?.canSignIn) { return; }
    const f = this.#flows.get(key);
    if (f?.state === 'starting') { return; }
    const attempt = uuid();
    this.#flows.set(key, { state: 'starting', attempt, flow: null, code: null });
    this.render();
    if (f?.flow && !f.joined) { this.#cancelInBackground(key, f.flow); }
    await this.#awaitCancels(key);
    if (this.#flows.get(key)?.attempt !== attempt) { return; }
    this.#flows.delete(key);
    const now = this.#views.get(key);
    if (this.#sheet?.key !== key || !now?.entry?.canSignIn) {
      // Nothing will start: the sheet goes rather than saying "Starting…", focus to the banner.
      if (this.#sheet?.key === key) { this.#closeSheet(key); }
      this.render();
      return;
    }
    this.#start(now, pointer);
  }

  async #copy(code) {
    const said = this.#sheet?.els.body.querySelector('.sign-in-copied');
    try {
      await this.clipboard.writeText(code);
      if (said) { said.textContent = 'Code copied'; }
    } catch {
      if (said) { said.textContent = 'Couldn\u2019t copy. Select the code to copy it.'; }
    }
  }

  // With the account: nothing of its machines stays on the page.
  stop() {
    clearInterval(this.ticker);
    this.#toSay = [];
    this.live?.replaceChildren();
    for (const u of this.unwatch) { u?.(); }
    this.unwatch = [];
    this.#views.clear();
    this.#flows.clear();
    this.#last.clear();
    this.#resolved.clear();
    this.#signedHere.clear();
    this.#early = [];
    this.#closeSheet();
    this.#sheet?.els.dialog.remove();
    this.#sheet = null;
    this.container.replaceChildren();
    this.container.hidden = true;
    this.stopped = true;
  }
}
