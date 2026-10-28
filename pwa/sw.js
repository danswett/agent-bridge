// Network first, so a new version takes effect on the next load, with the cached shell as
// the offline fallback. Nothing from the relay is ever cached.
const PREFIX = 'agent-bridge-app-';
const CACHE = `${PREFIX}v1`;
const SHELL = [
  './', './index.html', './app.css', './config.js', './manifest.webmanifest', './icon.svg',
  './js/main.js', './js/bytes.js', './js/noise-p256.js', './js/identity.js', './js/signin.js',
  './js/relay.js', './js/channels.js', './js/connection.js', './js/protocol.js', './js/host-client.js',
  './js/host-session.js', './js/sessions-store.js', './js/markdown.js', './js/demo.js',
  './js/ui/dom.js', './js/ui/time.js', './js/ui/fold-store.js', './js/ui/question-view.js',
  './js/ui/session-card.js', './js/ui/views.js', './fixtures/sessions-list.json',
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
