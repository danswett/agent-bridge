// One cache per release, filled whole when the release's worker installs and served from then
// on without asking the network (#609). A warm start used to revalidate every file with the
// server first (network first, cache: 'no-cache'): 61 requests answered 304 before the first
// session showed, 3.4 s on a phone. Nothing from the relay is ever cached.
//
// A release changes only as a whole. Its worker is new whenever anything served changes (sw.js
// carries a hash of the rest), the browser checks for one on each start and the app on each
// return to the front (main.js), and the new worker fills its own cache before it takes over;
// the app then offers Reload. So an open app runs one release's files throughout, never old
// modules under a new main.js: usage (#26) was missing on an iPhone when they mixed. The app's
// scripts are bundled under names that carry their content's hash (tools/build-site.mjs), so a
// page never asks for one of another release's by mistake either.
const PREFIX = 'agent-bridge-app-';
// A hash of every file the site serves, written by tools/build-site.mjs. The browser only
// installs a new worker when this file's bytes change, and the app only announces an
// update when one takes over, so any change to the app or its relay must change it.
const VERSION = 'dev';
const CACHE = `${PREFIX}${VERSION}`;
// The product's name, for a notification that has no title of its own: written by
// tools/brand.mjs from js/brand.js, which a classic worker can't import.
const NAME = 'Slipgates';
// The app's bundle: its entry and every chunk it imports, written by tools/build-site.mjs. The
// demo's own chunk is left out, as only the demo asks for it.
const BUNDLE = [];
const SHELL = [
  './', './index.html', './app.css', './design/tokens.css', './design/sprite.svg', './design/brand.js',
  './design/fonts/SlipgatesSans.woff2', './design/fonts/SlipgatesSans-Italic.woff2',
  './design/fonts/SlipgatesMono-Regular.woff2', './design/fonts/SlipgatesMono-Italic.woff2', './design/fonts/SlipgatesMono-Medium.woff2',
  './design/fonts/SlipgatesMono-MediumItalic.woff2', './design/fonts/SlipgatesMono-SemiBold.woff2',
  './design/fonts/SlipgatesMono-SemiBoldItalic.woff2', './design/fonts/SlipgatesMono-Bold.woff2', './design/fonts/SlipgatesMono-BoldItalic.woff2',
  './manifest.webmanifest', './icon.svg', './icons/icon-192.png', './icons/icon-512.png',
  './icons/maskable-192.png', './icons/maskable-512.png', './icons/monochrome-192.png', './icons/monochrome-512.png',
  './icons/apple-touch-icon.png', './icons/badge-96.png',
  './fixtures/sessions-list.json', './text-size-boot.js',
  ...BUNDLE,
];

// GitHub Pages lets a browser keep every file for 10 minutes (max-age=600), and a worker's
// fetch goes through that cache like any other: a phone opened just after a deploy installed
// the new worker with the old files. So the shell is filled straight from the network.
self.addEventListener('install', (e) => {
  e.waitUntil((async () => {
    // A worker that changed only itself, or a rollback, finds its release already kept, and
    // a failed install must leave that as it was for the worker still in control. A release
    // this install only partly kept is no release: gone, so it is never kept as the one before.
    const kept = await caches.has(CACHE);
    try {
      await (await caches.open(CACHE)).addAll(SHELL.map((u) => new Request(u, { cache: 'reload' })));
    } catch (err) {
      if (!kept) { await caches.delete(CACHE); }
      throw err;
    }
  })());
  self.skipWaiting();
});

// Which releases were in control, the latest and the one before it, recorded by each as it
// takes over. Cache order can't say: a rollback takes an older release's cache again.
const RELEASES = 'agent-bridge-app';
// Not a file of the site's, so not written as one (./…): the tests take every such name for a shell file.
const RECORD = 'releases.json';

// The release before this one is kept until the next takes over: a page still open on it when
// this worker took over (before the person pressed Reload) can still be given its own files.
// Older ones go. Only this app's: other apps on the same origin (the /s4/ spike) keep theirs.
self.addEventListener('activate', (e) => {
  e.waitUntil((async () => {
    const record = await caches.open(RELEASES);
    const was = await (await record.match(RECORD))?.json().catch(() => null) ?? null;
    const others = (await caches.keys()).filter((k) => k.startsWith(PREFIX) && k !== CACHE);
    // A worker that changed only itself keeps its release, and so the one before it too. With
    // no record (the first release to keep one), the last kept is taken for the one before.
    const before = was?.active === CACHE ? was.before : (was?.active ?? others.at(-1));
    await Promise.all(others.filter((k) => k !== before).map((k) => caches.delete(k)));
    await record.put(RECORD, new Response(JSON.stringify({ active: CACHE, before: before ?? null })));
  })());
  self.clients.claim();
});
const inShell = (url) => SHELL.some((u) => new URL(u, self.registration.scope).href === url);

// A file kept by the release before this one: a page still open on it asks for its own files,
// which may be gone from the server. Never refreshed, as a release's files never change.
async function earlier(request) {
  for (const k of (await caches.keys()).filter((n) => n.startsWith(PREFIX) && n !== CACHE).reverse()) {
    const hit = await (await caches.open(k)).match(request);
    if (hit) { return hit; }
  }
  return undefined;
}

// By URL rather than the request itself: a navigation request can't be given a cache mode.
const network = (request) => fetch(request.url, { cache: 'no-cache', credentials: 'same-origin' });

self.addEventListener('fetch', (e) => {
  const url = new URL(e.request.url);
  if (e.request.method !== 'GET' || url.origin !== location.origin || !url.href.startsWith(self.registration.scope) || url.pathname.includes('/v1/')) { return; }
  // The app is served at its origin's root (#393), where the spikes (/s2/, /s4/) and the old
  // /app/ redirect page are also in scope: a navigation to any of those is theirs, not the app's.
  if (e.request.mode === 'navigate' && url.pathname !== new URL(self.registration.scope).pathname) { return; }
  e.respondWith((async () => {
    const own = await caches.open(CACHE);
    // A navigation is the app's page, whatever its query says (?demo=1, ?host=…).
    if (e.request.mode === 'navigate') {
      return (await own.match(self.registration.scope)) ?? network(e.request).catch(() => Response.error());
    }
    const hit = await own.match(e.request);
    // The release's own files never change: served as they are, with nothing asked of the network.
    if (hit && inShell(url.href.split('#')[0])) { return hit; }
    if (!hit) {
      const old = await earlier(e.request);
      if (old) { return old; }
    }
    // Anything else (the demo's chunk, a file of the app's source layout) is
    // stale-while-revalidate: what is kept now, and the network's copy kept for next time.
    const fresh = network(e.request).then((r) => {
      if (r.ok) { const copy = r.clone(); e.waitUntil(own.put(e.request, copy)); }
      return r;
    });
    if (hit) {
      e.waitUntil(fresh.catch(() => {}));
      return hit;
    }
    return fresh.catch(async () => (await caches.match(e.request, { ignoreSearch: true })) ?? Response.error());
  })());
});
// ---- notifications (docs/protocol/v1-sessions.md, Notifications) ----
// A push is a notice a machine encrypted to this browser (RFC 8291); the browser has
// decrypted it. It is a Declarative Web Push message, which Safari 18.4+ shows without
// running this worker; everywhere else this shows the same fields. Every push must show a
// notification, even one that cannot be read: iOS revokes notifications from an app whose
// pushes show nothing.
// signIn (#800): a machine's sessions need sign-in; it alerts, as a question does.
const KINDS = ['question', 'finished', 'resolved', 'signIn'];
const CLASSES = ['needsYou', 'done', 'fyi'];
const WITHDRAWN_TAG = 'agent-bridge-withdrawn';

// Only ever within this app, whatever a payload says.
function inApp(url) {
  const app = self.registration.scope;
  try {
    const u = new URL(url ?? app, app);
    return u.href.startsWith(app) ? u.href : app;
  } catch {
    return app;
  }
}

function notice(message) {
  const n = message !== null && typeof message === 'object' && message.notification !== null && typeof message.notification === 'object' ? message.notification : {};
  const d = n.data !== null && typeof n.data === 'object' ? n.data : {};
  const text = (v) => (typeof v === 'string' && v ? v : null);
  // A kind from a newer machine reads as a question: shown, and alerting.
  const kind = KINDS.includes(d.kind) ? d.kind : 'question';
  // The host's notification class (#913); absent, unknown or wrong-typed reads as none, and
  // the notice is as it was before classes.
  const cls = CLASSES.includes(d.class) ? d.class : null;
  const tag = text(n.tag);
  // Shown, then closed (withdrawn): an answered question and an FYI leave nothing behind, but a
  // push that shows nothing costs the subscription on iOS and a generic notice elsewhere.
  const withdraw = kind === 'resolved' || cls === 'fyi';
  // Done is always silent, whatever the push says, and nothing to renotify: the host's word
  // is the first safeguard, this the second. A silent notice that renotifies is a TypeError.
  // A push whose kind and class disagree (a finished that needs you; the host never sends one)
  // takes the quieter reading, so it never alerts as "finished"; a question called done is
  // already silent above.
  const disagrees = kind === 'finished' && cls === 'needsYou';
  const silent = withdraw || cls === 'done' || disagrees || n.silent === true;
  const options = {
    // A withdrawn notice may flash, so what it says must be true: never the question's placeholder.
    body: text(n.body) ?? (withdraw ? text(n.title) ?? NAME : 'A session needs you.'),
    // A PNG: not every platform's notifications draw an SVG.
    icon: 'icons/icon-192.png',
    // Android draws the badge in the status bar by its alpha alone: the mark, as ic_stat_slipgates.
    badge: 'icons/badge-96.png',
    lang: text(n.lang) ?? 'en',
    data: { url: inApp(text(n.navigate)), kind, machine: text(d.machine), session: text(d.session), question: text(d.question) },
  };
  if (tag) {
    // Every notice about a session replaces the one before it, never beside it. A newer question
    // alerts again; a finished turn (without a class, as before) too; a silent one changes quietly.
    options.tag = tag;
    options.renotify = !silent;
  }
  if (silent) { options.silent = true; }
  return { title: text(n.title) ?? NAME, options, withdraw };
}

// Pushes about one tag run one after another, in the order they came: a withdrawal still between
// showing and closing would otherwise close a newer question that replaced it under the same tag.
const queues = new Map();
function inTurn(tag, job) {
  if (!tag) { return job(); }
  const run = (queues.get(tag) ?? Promise.resolve()).then(job);
  const tail = run.then(() => {}, () => {});
  queues.set(tag, tail);
  tail.then(() => { if (queues.get(tag) === tail) { queues.delete(tag); } });
  return run;
}

self.addEventListener('push', (e) => {
  let message = null;
  try { message = e.data?.json() ?? null; } catch { /* unreadable: the generic notice */ }
  const { title, options, withdraw } = notice(message);
  // Without a tag of its own, a withdrawn notice is closed by one made for the purpose.
  if (withdraw && !options.tag) { options.tag = WITHDRAWN_TAG; }
  e.waitUntil(inTurn(options.tag, async () => {
    await self.registration.showNotification(title, options);
    if (!withdraw) { return; }
    for (const shown of await self.registration.getNotifications({ tag: options.tag })) { shown.close(); }
  }));
});

// A tap opens the app at the machine, session and question. An app already open is
// brought forward and told where to go, keeping its connection; otherwise it is opened
// at the notice's link.
self.addEventListener('notificationclick', (e) => {
  e.notification.close();
  const url = inApp(e.notification.data?.url);
  e.waitUntil((async () => {
    const open = await self.clients.matchAll({ type: 'window', includeUncontrolled: true });
    const client = open.find((c) => c.url.startsWith(self.registration.scope));
    if (client) {
      client.postMessage({ type: 'agent-bridge.open', url });
      return client.focus();
    }
    return self.clients.openWindow(url);
  })());
});