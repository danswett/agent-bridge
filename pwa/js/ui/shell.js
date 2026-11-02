// The app's shell (docs/design/ui.md §7, PWA): the web app bar, the navigation (a rail on a
// desktop, a bottom bar on a phone) and the pages it goes between. Each page is a route in the
// address's hash (#/sessions, #/machines, #/launch, #/usage, #/settings), so the browser's own
// back and forward go between them, a reload stays where it was, and nothing the GitHub Pages
// server sees changes. Which page shows is decided here and nowhere else: a page's own panels
// still hide themselves when they have nothing to show, inside it.
import { isSearchKey, shortcutHints } from '../platform.js';

export const ROUTES = Object.freeze(['sessions', 'machines', 'launch', 'usage', 'settings']);
export const HOME = 'sessions';

const plural = (n, one, many) => `${n} ${n === 1 ? one : many}`;

// '#/usage' -> { route: 'usage', rest: [] }; anything else is the sessions page. rest is what
// follows the route, decoded, for a page that has more to say (a session, later).
export function parseRoute(hash) {
  const parts = String(hash ?? '').replace(/^#\/?/, '').split('/').filter(Boolean);
  let rest = [];
  try { rest = parts.slice(1).map(decodeURIComponent); } catch { rest = []; }
  return ROUTES.includes(parts[0]) ? { route: parts[0], rest } : { route: HOME, rest: [] };
}

export function routeHash(route, ...rest) {
  return `#/${ROUTES.includes(route) ? route : HOME}${rest.map((r) => `/${encodeURIComponent(r)}`).join('')}`;
}

export class Shell {
  #listeners = new Set();
  #signedIn = false;
  #current = parseRoute('');

  // doc and win stand in for the page's, so this can be driven from anywhere.
  constructor({ doc = globalThis.document, win = globalThis.window } = {}) {
    Object.assign(this, { doc, win });
    const $ = (id) => doc.getElementById(id);
    this.els = {
      nav: $('nav'), appbar: $('appbar'), search: $('search'), searchBox: doc.querySelector('.search'),
      online: $('online'), avatar: $('avatar'), main: $('main'),
    };
    this.routes = [...doc.querySelectorAll('[data-route]')];
    this.links = [...doc.querySelectorAll('[data-nav]')];
    // Back and forward between entries go() pushed fire popstate; a link's own fires hashchange.
    win.addEventListener('hashchange', () => this.#apply());
    win.addEventListener('popstate', () => this.#apply());
    // Ctrl+K or ⌘K focuses search (ui.md §9), from anywhere, on every platform (platform.js).
    doc.addEventListener('keydown', (e) => {
      if (!this.#signedIn || !isSearchKey(e)) { return; }
      e.preventDefault();
      this.openSearch();
    });
    // The search box names its shortcut as the platform writes it: ⌘K on Apple's, Ctrl+K elsewhere.
    if (this.els.search) {
      const hints = shortcutHints();
      this.els.search.setAttribute('aria-keyshortcuts', hints.searchKeys);
      this.els.search.title = `Search sessions (${hints.search})`;
    }
    // Skip to content stays on the page it skips on: its #main is not a route (it would read as
    // Sessions), so it focuses the content itself and leaves the address alone.
    for (const a of doc.querySelectorAll('a.skip[href="#main"]')) {
      a.addEventListener('click', (e) => { e.preventDefault(); this.els.main?.focus(); });
    }
    for (const b of doc.querySelectorAll('.search-open')) { b.addEventListener('click', () => this.openSearch()); }
    $('search-close')?.addEventListener('click', () => this.closeSearch());
    this.els.search?.addEventListener('keydown', (e) => {
      if (e.key === 'Escape') { e.preventDefault(); this.closeSearch(); }
    });
    this.els.search?.addEventListener('input', () => {
      // Search is the sessions list's: typing elsewhere takes you to it.
      if (this.route !== HOME && this.els.search.value) { this.go(HOME); }
      this.#emitSearch();
    });
    this.#apply();
  }

  get route() { return this.#current.route; }
  get rest() { return this.#current.rest; }
  get signedIn() { return this.#signedIn; }
  get query() { return (this.els.search?.value ?? '').trim(); }

  // fn({ route, rest, from }) on every change of page, or of what follows it.
  onRoute(fn) { this.#listeners.add(fn); return () => this.#listeners.delete(fn); }
  onSearch(fn) { this.searchListener = fn; }

  // replace: no new history entry (a page chosen for you, not by you). Applied at once, not on
  // the hashchange event that a location.hash write fires later, so whoever went can scroll to
  // what it went for straight away.
  // state: what the entry carries in history.state (the sessions page marks a session's entry
  // that has the list beneath it).
  go(route, { rest = [], replace = false, state = null } = {}) {
    const hash = routeHash(route, ...rest);
    if (this.win.location.hash !== hash) {
      const url = `${this.win.location.pathname}${this.win.location.search}${hash}`;
      if (replace) { this.win.history.replaceState(state, '', url); } else { this.win.history.pushState(state, '', url); }
    }
    this.#apply();
  }

  // Signed out, there is only the sign-in page: no navigation, nothing in the app bar but the name.
  setSignedIn(signedIn) {
    this.#signedIn = signedIn;
    this.doc.body.dataset.signedIn = signedIn ? 'true' : 'false';
    this.els.nav.hidden = !signedIn;
    if (this.els.searchBox) { this.els.searchBox.hidden = !signedIn; }
    if (!signedIn) { this.els.online.hidden = true; this.els.avatar.hidden = true; this.closeSearch(); }
    this.#apply();
  }

  // The account in the app bar: its first letter, named in full for a screen reader.
  setAccount(login) {
    const a = this.els.avatar;
    a.hidden = !login;
    a.textContent = login ? login.slice(0, 1).toUpperCase() : '';
    a.setAttribute('aria-label', login ? `Account: ${login}` : 'Account');
    a.title = login ? `Signed in as ${login}` : '';
  }

  // Machines online, in the app bar: how many, and the way to them.
  setOnline(count) {
    const o = this.els.online;
    o.hidden = !this.#signedIn;
    o.querySelector('.online-text').textContent = plural(count, 'machine', 'machines');
    o.setAttribute('aria-label', count === 0 ? 'No machines online' : `${plural(count, 'machine', 'machines')} online`);
    o.dataset.online = String(count);
  }

  // The connection's state, as the app bar's pill says it; on a phone, the bar shows only for
  // news (reconnecting, paused, an error), since the page's own title row takes its place.
  setConnection(text, tone = '') {
    const pill = this.doc.getElementById('conn');
    pill.textContent = text;
    pill.className = `pill ${tone}`.trim();
    this.doc.body.dataset.conn = tone || 'none';
  }

  // A needs dot on a page's way in: Launch, while a folder-trust question waits there.
  // Said to a screen reader too, in the way's name: the dot is only drawn.
  setAttention(route, on) {
    const ways = [...this.links.filter((a) => a.dataset.nav === route), ...this.doc.querySelectorAll(`[data-attention-for="${route}"]`)];
    for (const a of ways) {
      a.classList.toggle('attention', on);
      if (a.dataset.label === undefined) { a.dataset.label = a.getAttribute('aria-label') ?? ''; }
      const name = a.dataset.label || a.textContent.trim();
      if (on) { a.setAttribute('aria-label', `${name}, a question is waiting`); } else if (a.dataset.label) { a.setAttribute('aria-label', a.dataset.label); } else { a.removeAttribute('aria-label'); }
    }
  }

  // beforeSearch: what a page does first (the sessions page, on a phone, goes back to the list
  // from a session pushed over it, where search lives).
  openSearch() {
    if (!this.#signedIn) { return; }
    const back = this.beforeSearch?.() === true;
    this.doc.body.classList.add('searching');
    const focus = () => { this.els.search?.focus(); this.els.search?.select(); };
    // Going back is the browser's own, so it lands a moment later: search takes focus once it
    // shows, looked for a frame at a time (WebKit settles the page a frame after popstate).
    const whenShown = (frames) => {
      if (!this.els.search || this.els.search.getClientRects().length > 0) { focus(); return; }
      if (frames > 0) { this.win.requestAnimationFrame(() => whenShown(frames - 1)); }
    };
    if (back) { this.win.addEventListener('popstate', () => whenShown(30), { once: true }); }
    focus();
  }

  closeSearch() {
    const box = this.els.searchBox;
    const had = !!box && box.contains(this.doc.activeElement);
    this.doc.body.classList.remove('searching');
    if (this.els.search && this.els.search.value) {
      this.els.search.value = '';
      this.#emitSearch();
    }
    // On a phone the row goes with the search, and its focus would go to the document: back to
    // the Search that opened it, or else to the page.
    if (had && box.getClientRects().length === 0) {
      const open = [...this.doc.querySelectorAll('.search-open')].find((b) => b.getClientRects().length > 0);
      (open ?? this.els.main)?.focus();
    }
  }

  #emitSearch() { this.searchListener?.(this.query); }

  #apply() {
    const from = this.#current;
    this.#current = parseRoute(this.win.location.hash);
    const { route } = this.#current;
    const focused = this.doc.activeElement;
    const leaving = from.route !== route && this.routes.some((r) => r.dataset.route !== route && r.contains(focused));
    for (const r of this.routes) { r.hidden = !this.#signedIn || r.dataset.route !== route; }
    if (from.route !== route) {
      // Every page shares one scroller (the content on a desktop, the window on a phone): a new
      // page starts at its top, not at the last one's offset.
      if (this.els.main) { this.els.main.scrollTop = 0; }
      this.win.scrollTo?.(0, 0);
      // Focus that was in the page left goes to the new page's content, not to the document.
      if (leaving) { this.els.main?.focus({ preventScroll: true }); }
    }
    for (const a of this.links) {
      if (a.dataset.nav === route) { a.setAttribute('aria-current', 'page'); } else { a.removeAttribute('aria-current'); }
    }
    this.doc.body.dataset.page = route;
    const changed = from.route !== route || from.rest.join('/') !== this.#current.rest.join('/');
    if (changed || !this.#seen) {
      this.#seen = true;
      for (const fn of this.#listeners) { fn({ ...this.#current, from: from.route }); }
    }
  }

  #seen = false;
}
