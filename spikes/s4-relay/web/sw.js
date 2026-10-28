// Network first, so a new relay deployment's PWA takes effect on the next load, with the
// cached shell as the offline fallback. The API and socket are never cached.
const CACHE = 's4-shell-v2';
const SHELL = ['./', './index.html', './app.js', './config.js', './noise-p256.js', './manifest.webmanifest', './icon.svg'];

self.addEventListener('install', (e) => {
  e.waitUntil(caches.open(CACHE).then((c) => c.addAll(SHELL)));
  self.skipWaiting();
});

self.addEventListener('activate', (e) => {
  e.waitUntil(caches.keys().then((keys) => Promise.all(keys.filter((k) => k !== CACHE).map((k) => caches.delete(k)))));
  self.clients.claim();
});

self.addEventListener('fetch', (e) => {
  const url = new URL(e.request.url);
  if (e.request.method !== 'GET' || url.origin !== location.origin || url.pathname.startsWith('/v1/')) { return; }
  e.respondWith(fetch(e.request)
    .then((r) => { const copy = r.clone(); caches.open(CACHE).then((c) => c.put(e.request, copy)); return r; })
    .catch(() => caches.match(e.request)));
});

// A push is a short notice that a host has a question; the browser has already decrypted
// it (RFC 8291). The payload is Declarative Web Push's shape, which Safari 18.4+ can show
// without this worker. Every push must show a notification: iOS revokes the permission of
// sites that receive pushes silently.
self.addEventListener('push', (e) => {
  let n = { title: 'Agent Bridge', body: 'A session needs you' };
  try { n = { ...n, ...(e.data?.json().notification ?? {}) }; } catch { /* keep the generic notice */ }
  // Only ever navigate within this app, whatever a payload says.
  const url = new URL(n.navigate ?? './', self.registration.scope);
  const target = url.href.startsWith(self.registration.scope) ? url.href : self.registration.scope;
  e.waitUntil(self.registration.showNotification(n.title, { body: n.body, tag: n.tag, data: { url: target }, icon: 'icon.svg' }));
});

self.addEventListener('notificationclick', (e) => {
  e.notification.close();
  const url = e.notification.data?.url ?? self.registration.scope;
  e.waitUntil((async () => {
    const open = await self.clients.matchAll({ type: 'window', includeUncontrolled: true });
    const client = open.find((c) => c.url.startsWith(self.registration.scope));
    if (client) {
      await client.focus();
      if ('navigate' in client) { return client.navigate(url); }
    }
    return self.clients.openWindow(url);
  })());
});
