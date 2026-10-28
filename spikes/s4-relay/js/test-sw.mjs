// The service worker's push and notificationclick handlers, run in Node against a stubbed
// worker scope. Headless browsers refuse notification permission, so this is where the
// handlers' behaviour is pinned: every push shows a notification (iOS revokes permission
// otherwise), and a payload can never send a tap outside the app.
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import vm from 'node:vm';

const root = path.dirname(path.dirname(fileURLToPath(import.meta.url)));
const scope = 'https://danswett.github.io/agent-bridge/s4/';

function load() {
  const handlers = {};
  const shown = [];
  const opened = [];
  const clients = [];
  const self = {
    registration: { scope, showNotification: async (title, options) => { shown.push({ title, ...options }); } },
    clients: { matchAll: async () => clients, openWindow: async (u) => { opened.push(u); }, claim() {} },
    addEventListener: (type, fn) => { handlers[type] = fn; },
    skipWaiting() {},
  };
  vm.runInNewContext(readFileSync(path.join(root, 'web', 'sw.js'), 'utf8'), { self, location: new URL(scope), URL, caches: {}, fetch });
  const fire = async (type, event) => {
    let done;
    handlers[type]({ ...event, waitUntil: (p) => { done = p; } });
    await done;
  };
  return { fire, shown, opened, clients };
}

const push = (payload) => ({ data: payload === undefined ? null : { json: () => (typeof payload === 'string' ? JSON.parse(payload) : payload) } });
let failed = 0;
const check = (name, ok, detail) => { console.log(`${ok ? 'PASS' : 'FAIL'}  ${name}${detail ? `  (${detail})` : ''}`); if (!ok) { failed++; } };

{
  const sw = load();
  await sw.fire('push', push({ web_push: 8030, notification: { title: 'DSWETT-HOME asks', body: 'Which database?', navigate: `${scope}?q=ab&host=cd`, tag: 'qab' } }));
  const n = sw.shown[0];
  check('a declarative payload shows its title, body and tag, and the tap goes to the question', n?.title === 'DSWETT-HOME asks' && n.body === 'Which database?' && n.tag === 'qab' && n.data.url === `${scope}?q=ab&host=cd`, JSON.stringify(n));
}
{
  const sw = load();
  await sw.fire('push', push({ web_push: 8030, notification: { title: 'x', navigate: 'https://evil.example/phish' } }));
  await sw.fire('push', push({ web_push: 8030, notification: { title: 'y', navigate: 'https://danswett.github.io/other-app/' } }));
  check('a tap can never leave the app, whatever the payload says', sw.shown.every((n) => n.data.url === scope), sw.shown.map((n) => n.data.url).join(', '));
}
{
  const sw = load();
  await sw.fire('push', push(undefined));
  await sw.fire('push', push('not json'));
  check('an empty or unreadable push still shows a notification', sw.shown.length === 2 && sw.shown.every((n) => n.title === 'Agent Bridge'), `${sw.shown.length} shown`);
}
{
  const sw = load();
  const notification = { close() { this.closed = true; }, data: { url: `${scope}?q=1&host=2` } };
  await sw.fire('notificationclick', { notification });
  const navigated = [];
  sw.clients.push({ url: scope, focus: async () => {}, navigate: async (u) => { navigated.push(u); } });
  await sw.fire('notificationclick', { notification });
  check('a tap opens the app at the question, or moves an open app to it', notification.closed && sw.opened[0] === `${scope}?q=1&host=2` && navigated[0] === `${scope}?q=1&host=2`, `opened ${sw.opened[0]}, navigated ${navigated[0]}`);
}

process.exit(failed ? 1 : 0);
