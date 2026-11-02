// The app's shell (docs/design/ui.md §7, PWA): the web app bar, the navigation (a rail on a
// desktop, a bottom bar on a phone) and the pages it goes between. Each page is a route in the
// address's hash (#/sessions, #/machines, #/launch, #/usage, #/settings), so the browser's own
// back and forward go between them, a reload stays where it was, and nothing the GitHub Pages
// server sees changes. Which page shows is decided here and nowhere else: a page's own panels
// still hide themselves when they have nothing to show, inside it.

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
    // Ctrl+K or Cmd+K focuses search (ui.md §9), from anywhere but a box being typed in.
    doc.addEventListener('keydown', (e) => {
      if (!this.#signedIn || e.key.toLowerCase() !== 'k' || !(e.ctrlKey || e.metaKey) || e.altKey || e.shiftKey) { return; }
      e.preventDefault();
      this.openSearch();
    });
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
  go(route, { rest = [], replace = false } = {}) {
    const hash = routeHash(route, ...rest);
    if (this.win.location.hash !== hash) {
      const url = `${this.win.location.pathname}${this.win.location.search}${hash}`;
      if (replace) { this.win.history.replaceState(null, '', url); } else { this.win.history.pushState(null, '', url); }
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
  setAttention(route, on) {
    for (const a of this.links) { if (a.dataset.nav === route) { a.classList.toggle('attention', on); } }
    for (const a of this.doc.querySelectorAll(`[data-attention-for="${route}"]`)) { a.classList.toggle('attention', on); }
  }

  openSearch() {
    if (!this.#signedIn) { return; }
    this.doc.body.classList.add('searching');
    this.els.search?.focus();
    this.els.search?.select();
  }

  closeSearch() {
    this.doc.body.classList.remove('searching');
    if (this.els.search && this.els.search.value) {
      this.els.search.value = '';
      this.#emitSearch();
    }
  }

  #emitSearch() { this.searchListener?.(this.query); }

  #apply() {
    const from = this.#current;
    this.#current = parseRoute(this.win.location.hash);
    const { route } = this.#current;
    for (const r of this.routes) { r.hidden = !this.#signedIn || r.dataset.route !== route; }
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
