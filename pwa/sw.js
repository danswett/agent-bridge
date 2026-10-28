// Network first, so a new version takes effect on the next load, with the cached shell as
// the offline fallback. Nothing from the relay is ever cached.
const PREFIX = 'agent-bridge-app-';
// A hash of every file the site serves, written by tools/build-site.mjs. The browser only
// installs a new worker when this file's bytes change, and the app only announces an
// update when one takes over, so any change to the app or its relay must change it.
const VERSION = 'dev';
const CACHE = `${PREFIX}${VERSION}`;
const SHELL = [
  './', './index.html', './app.css', './config.js', './manifest.webmanifest', './icon.svg',
  './js/main.js', './js/bytes.js', './js/noise-p256.js', './js/identity.js', './js/signin.js',
  './js/relay.js', './js/channels.js', './js/connection.js', './js/protocol.js', './js/host-client.js',
  './js/host-session.js', './js/sessions-store.js', './js/markdown.js', './js/demo.js', './js/push.js',
  './js/answers.js', './js/answer-service.js', './js/replies.js', './js/reply-service.js',
  './js/ui/dom.js', './js/ui/time.js', './js/ui/fold-store.js', './js/ui/question-view.js',
  './js/ui/reply-view.js', './js/ui/session-card.js', './js/ui/views.js', './fixtures/sessions-list.json',
];

self.addEventListener('install', (e) => {
  e.waitUntil(caches.open(CACHE).then((c) => c.addAll(SHELL)));
  self.skipWaiting();
});

self.addEventListener('activate', (e) => {
  // Only this app's old caches: other apps on the same origin (the /s4/ spike) keep theirs.
  e.waitUntil(caches.keys().then((keys) => Promise.all(keys.filter((k) => k.startsWith(PREFIX) && k !== CACHE).map((k) => caches.delete(k)))));
  self.clients.claim();
});

self.addEventListener('fetch', (e) => {
  const url = new URL(e.request.url);
  if (e.request.method !== 'GET' || url.origin !== location.origin || !url.href.startsWith(self.registration.scope) || url.pathname.includes('/v1/')) { return; }
  e.respondWith(fetch(e.request)
    .then((r) => {
      if (r.ok) { const copy = r.clone(); caches.open(CACHE).then((c) => c.put(e.request, copy)); }
      return r;
    })
    .catch(() => caches.match(e.request, { ignoreSearch: true })));
});

// ---- notifications (docs/protocol/v1-sessions.md, Notifications) ----
// A push is a notice a machine encrypted to this browser (RFC 8291); the browser has
// decrypted it. It is a Declarative Web Push message, which Safari 18.4+ shows without
// running this worker; everywhere else this shows the same fields. Every push must show a
// notification, even one that cannot be read: iOS revokes notifications from an app whose
// pushes show nothing.
const KINDS = ['question', 'finished', 'resolved'];

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
  const tag = text(n.tag);
  const options = {
    body: text(n.body) ?? 'A session needs you.',
    icon: 'icon.svg',
    lang: text(n.lang) ?? 'en',
    data: { url: inApp(text(n.navigate)), kind, machine: text(d.machine), session: text(d.session), question: text(d.question) },
  };
  if (tag) {
    // Every notice about a session replaces the one before it. A newer question or a
    // finished turn alerts again; an answered question changes quietly.
    options.tag = tag;
    options.renotify = kind !== 'resolved';
  }
  if (kind === 'resolved' || n.silent === true) { options.silent = true; }
  return { title: text(n.title) ?? 'Agent Bridge', options };
}

self.addEventListener('push', (e) => {
  let message = null;
  try { message = e.data?.json() ?? null; } catch { /* unreadable: the generic notice */ }
  const { title, options } = notice(message);
  e.waitUntil(self.registration.showNotification(title, options));
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