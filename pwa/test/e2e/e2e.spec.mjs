// End to end through a real relay: the spike dev stack (a fake GitHub, the relay and its
// S4 demo host) plus test/e2e/fixture-host.mjs, a Node host that speaks the sessions
// protocol. The PWA is served from its own origin, as on GitHub Pages, under its CSP, and
// the relay is told to allow that origin. Covered: sign-in, the key proof, presence,
// a Noise channel, hello, the subscription snapshot, live upserts and removes,
// session.get, reconnecting after the socket drops (with a session that ended meanwhile
// dropped by the new snapshot), returning to the last host, ?host= links, a host that
// predates the protocol, answering a question: delivered, resent with the same request
// after the socket drops mid-answer (typed once), and the host's own checks; replying:
// delivered as one line, resent after the socket drops mid-reply (typed once), refused while
// the session asks a question; and notifications: registering with the host (with a faked
// browser push subscription, as headless browsers have no push service) and opening a
// notification's session.
//
// Needs: dotnet build spikes/s4-relay/src/S4.DevStack -c Release
import { test, expect } from '@playwright/test';
import { spawn } from 'node:child_process';
import { generateKeyPairSync } from 'node:crypto';
import { existsSync, readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { buildSite } from '../../tools/build-site.mjs';
import { startStaticServer, APP_PATH } from '../serve.mjs';
import { startFixtureHost } from './fixture-host.mjs';

const pwa = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');
const dll = path.join(pwa, '..', 'spikes', 's4-relay', 'src', 'S4.DevStack', 'bin', 'Release', 'net10.0', 's4-devstack.dll');
const fixture = JSON.parse(readFileSync(path.join(pwa, 'fixtures', 'sessions-list.json'), 'utf8'));
const CLAUDE = '6f1c2b7e-claude-example';
const CODEX = '019a1b2c-codex-example';
const DELEGATING = '8c4e1f20-5a7b-4d3c-9e61-0b2f7d9a1c35';
const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;

test.skip(!existsSync(dll), 'the dev stack is not built: dotnet build spikes/s4-relay/src/S4.DevStack -c Release');
test.describe.configure({ mode: 'serial' });

let stack, site, host, appUrl, relayUrl;

const vapidKey = () => generateKeyPairSync('ec', { namedCurve: 'P-256' }).privateKey.export({ type: 'pkcs8', format: 'der' }).toString('base64');
const stackOut = [];

test.beforeAll(async ({}, info) => {
  test.setTimeout(120000);
  // PWA_E2E_PORT moves these, for a machine running another checkout's tests at once.
  const relayPort = (Number(process.env.PWA_E2E_PORT) || 8800) + info.parallelIndex * 2;
  const sitePort = relayPort + 1;
  const relay = `http://127.0.0.1:${relayPort}`;
  relayUrl = relay;
  const dir = buildSite({ out: path.join(pwa, '.site', `e2e-${info.project.name}`), relay });
  site = await startStaticServer({ dir, port: sitePort });
  appUrl = `http://127.0.0.1:${sitePort}${APP_PATH}`;
  stack = spawn('dotnet', [dll, 'serve', '--port', String(relayPort)], {
    // The page's origin, which the relay must accept for sign-in (CORS) and the socket.
    // And a VAPID key, so it offers push (GET /v1/push/key) as the deployed relay does.
    env: { ...process.env, Relay__AllowedOrigins__0: `http://127.0.0.1:${sitePort}`, Relay__VapidPrivateKey: vapidKey() },
    stdio: ['ignore', 'pipe', 'pipe'],
  });
  stack.stdout.on('data', (d) => stackOut.push(d.toString()));
  stack.stderr.on('data', (d) => stackOut.push(d.toString()));
  await expect.poll(() => stackOut.join(''), { timeout: 60000 }).toContain('ready ');
  host = await startFixtureHost({ base: relay, name: 'fixture-host', sessions: structuredClone(fixture.sessions) });
});

test.afterAll(async () => {
  host?.close();
  stack?.kill();
  await new Promise((r) => (site ? site.close(r) : r()));
  expect(stackOut.join('')).not.toContain('LEAK');
});

async function signIn(page) {
  await page.goto(appUrl);
  await page.click('#signin-button');
  await expect(page.locator('#account')).toBeVisible({ timeout: 30000 });
  await expect(page.locator('#conn')).toHaveText('Connected', { timeout: 20000 });
}

const card = (page, id) => page.locator(`.session[data-id="${id}"]`);
const subscribes = () => host.log.filter((op) => op === 'sessions.subscribe').length;

test('a signed-in phone sees a machine\'s live sessions over an end-to-end channel', async ({ page, context }) => {
  const violations = [];
  await page.exposeFunction('cspViolation', (v) => violations.push(v));
  await page.addInitScript(() => document.addEventListener('securitypolicyviolation', (e) => window.cspViolation(`${e.violatedDirective} ${e.blockedURI}`)));
  // Every relay socket goes through the test, so it can drop them the way a phone does
  // when it locks.
  const sockets = [];
  await context.routeWebSocket(/\/v1\/ws$/, (ws) => { ws.connectToServer(); sockets.push(ws); });

  await signIn(page);
  await expect(page.locator('[data-device-name="fixture-host"] button')).toBeVisible();
  await expect(page.locator('[data-device-name="demo-host"] button')).toBeVisible();
  await page.click('[data-device-name="fixture-host"] button');
  await expect(page.locator('.session')).toHaveCount(fixture.sessions.length, { timeout: 30000 });
  await expect(page.locator('#sessions-h')).toHaveText('Sessions on fixture-host');
  await expect(page.locator('[data-device-name="fixture-host"] button')).toHaveAttribute('aria-current', 'true');
  await expect(card(page, CLAUDE)).toHaveAttribute('data-status', 'working');
  // Idle, but waiting on work it started: background crosses the relay intact.
  await expect(card(page, DELEGATING)).toHaveAttribute('data-view', 'delegating');
  await expect(card(page, DELEGATING).locator('.status-text')).toHaveText('Waiting for background agents');

  // Live changes, pushed by the host.
  const t = new Date(Date.now() + 1000).toISOString();
  await host.upsert({ id: 'live-1', agent: 'codex', title: 'Pushed live', status: 'working', statusSince: t, updatedAt: t, activity: { response: 'From the **host**.' } });
  await expect(page.locator('.session').first()).toHaveAttribute('data-id', 'live-1');
  await expect(card(page, 'live-1').locator('.response strong')).toHaveText('host');
  await host.remove(CLAUDE);
  await expect(card(page, CLAUDE)).toHaveCount(0);

  // session.get, for the tool history.
  await card(page, CODEX).locator('details.history summary').click();
  await expect(card(page, CODEX).locator('details.history')).toContainText('No tool calls yet.');
  expect(host.log).toContain('session.get');

  // The socket drops. While the phone is away a session ends; the new subscription's
  // snapshot must take it off the screen.
  const before = subscribes();
  for (const ws of sockets.splice(0)) { await ws.close({ code: 4000, reason: 'test drop' }); }
  await host.remove(CODEX);
  await expect(page.locator('#conn')).toHaveText('Connected', { timeout: 20000 });
  await expect.poll(subscribes, { timeout: 30000 }).toBeGreaterThan(before);
  await expect(card(page, CODEX)).toHaveCount(0, { timeout: 15000 });
  await expect(card(page, 'live-1')).toHaveCount(1);

  // Opened again later, it goes straight back to the same machine.
  await page.reload();
  await expect(page.locator('#sessions-h')).toHaveText('Sessions on fixture-host', { timeout: 30000 });
  await expect(card(page, 'live-1')).toHaveCount(1, { timeout: 30000 });
  expect(violations).toEqual([]);
});

test('a machine from before the sessions protocol is reported, ?host= opens a machine directly, and a refused sign-in leaves nothing behind', async ({ page, context }) => {
  const sockets = [];
  await context.routeWebSocket(/\/v1\/ws$/, (ws) => { ws.connectToServer(); sockets.push(ws); });
  await signIn(page);
  await page.click('[data-device-name="demo-host"] button');
  await expect(page.locator('#host-state')).toContainText("doesn't support Agent Bridge sessions yet", { timeout: 30000 });
  await expect(page.locator('.session')).toHaveCount(0);
  await page.close();

  // What a notification tap opens. The remembered machine is the demo host; the link wins.
  const opened = await context.newPage();
  await opened.goto(`${appUrl}?host=${host.deviceId}`);
  await expect(opened.locator('#sessions-h')).toHaveText('Sessions on fixture-host', { timeout: 30000 });
  await expect(opened.locator('.session').first()).toBeVisible();

  // The relay stops accepting this sign-in. Nothing from it may stay on screen or carry
  // over to the next account: not the sessions, nor the remembered machine.
  for (const ws of sockets.splice(0)) { await ws.close({ code: 4001, reason: 'auth' }); }
  await expect(opened.locator('#signin')).toBeVisible({ timeout: 15000 });
  await expect(opened.locator('#sessions')).toBeHidden();
  expect(await opened.evaluate(() => window.agentBridge.store.size)).toBe(0);
  expect(await opened.evaluate(() => localStorage.getItem('agent-bridge.app.lastHost'))).toBeNull();
  await opened.click('#signin-button');
  await expect(opened.locator('#conn')).toHaveText('Connected', { timeout: 30000 });
  await expect(opened.locator('[data-device-name="fixture-host"] button')).toBeVisible();
  await expect(opened.locator('#sessions')).toBeHidden();
  await expect(opened.locator('.session')).toHaveCount(0);
});

test('a phone answers a question through the relay, and an answer in flight when the socket drops is resent and typed once', async ({ page, context }) => {
  const sockets = [];
  await context.routeWebSocket(/\/v1\/ws$/, (ws) => { ws.connectToServer(); sockets.push(ws); });
  const ask = (id, question) => {
    const t = new Date().toISOString();
    return host.upsert({
      id, agent: 'copilot', title: `Asks ${question}`, status: 'waiting', statusSince: t, updatedAt: t, activity: {},
      question: { id: question, kind: 'form', message: 'Which one?', askedAt: t, fields: [{ name: 'answer', title: 'Pick', type: 'choice', required: true, options: [{ value: 'a', label: 'A' }, { value: 'b', label: 'B (Recommended)' }] }] },
    });
  };
  const sentFor = (id) => host.answers.filter((a) => a.id === id);
  const typedFor = (id) => host.typed.filter((a) => a.id === id);

  await ask('ask-1', 'q1');
  await signIn(page);
  await page.click('[data-device-name="fixture-host"] button');
  const q1 = card(page, 'ask-1').locator('.question');
  await expect(q1).toHaveAttribute('data-answerable', 'true', { timeout: 30000 });
  await expect(q1.locator('.choice[data-value]').first()).toHaveText('B (Recommended)');
  await q1.locator('.choice[data-value="a"]').click();
  await expect(card(page, 'ask-1').locator('.answer-receipt')).toHaveText('Your answer: Delivered. The session has your answer.', { timeout: 15000 });
  await expect(q1).toHaveCount(0);
  expect(sentFor('ask-1')).toEqual([{ id: 'ask-1', question: 'q1', request: expect.stringMatching(UUID), fields: { answer: 'a' } }]);
  expect(typedFor('ask-1')).toHaveLength(1);

  // The socket drops while the host is still delivering. The phone resends on the new
  // channel with the same request; the host returns the first outcome and types nothing more.
  host.scriptAnswers({ outcome: 'delivered', holdMs: 4000 });
  await ask('ask-2', 'q2');
  const q2 = card(page, 'ask-2').locator('.question');
  await expect(q2).toHaveAttribute('data-answerable', 'true', { timeout: 15000 });
  await q2.locator('.choice[data-value="b"]').click();
  await expect.poll(() => sentFor('ask-2').length, { timeout: 10000 }).toBe(1);
  for (const ws of sockets.splice(0)) { await ws.close({ code: 4000, reason: 'test drop' }); }
  await expect(card(page, 'ask-2').locator('.answer-receipt')).toHaveText('Your answer: Delivered. The session has your answer.', { timeout: 30000 });
  const sent = sentFor('ask-2');
  expect(sent).toHaveLength(2);
  expect(sent[1].request).toBe(sent[0].request);
  expect(typedFor('ask-2')).toHaveLength(1);

  // The host checks for itself, whatever a client sends: a question no longer asked is
  // stale, an answer that doesn't fit is refused with the reason, and neither is typed.
  await ask('ask-3', 'q3');
  await expect(card(page, 'ask-3').locator('.question')).toHaveAttribute('data-answerable', 'true', { timeout: 15000 });
  const direct = (data) => page.evaluate((d) => window.agentBridge.hosts.client.answer({ ...d, request: crypto.randomUUID() }), data);
  expect(await direct({ id: 'ask-3', question: 'q-old', fields: { answer: 'a' } })).toEqual({ outcome: 'stale', reason: 'the session is no longer asking that question' });
  expect(await direct({ id: 'ask-3', question: 'q3', fields: { answer: 'zzz' } })).toEqual({ outcome: 'refused', reason: '"Pick": not one of the choices' });
  expect(typedFor('ask-3')).toHaveLength(0);
  await expect(card(page, 'ask-3').locator('.question')).toHaveCount(1);
});

test('a phone replies to a session through the relay, and a reply in flight when the socket drops is resent and typed once', async ({ page, context }) => {
  const sockets = [];
  await context.routeWebSocket(/\/v1\/ws$/, (ws) => { ws.connectToServer(); sockets.push(ws); });
  const t = new Date().toISOString();
  await host.upsert({ id: 'reply-1', agent: 'copilot', title: 'Takes replies', status: 'idle', statusSince: t, updatedAt: t, activity: { response: 'Done.' } });
  const sentFor = (id) => host.replies.filter((r) => r.id === id);
  const typedFor = (id) => host.typedReplies.filter((r) => r.id === id);

  await signIn(page);
  await page.click('[data-device-name="fixture-host"] button');
  const reply = card(page, 'reply-1').locator('form.reply');
  const input = reply.getByRole('textbox', { name: 'Reply' });
  await expect(input).toBeEnabled({ timeout: 30000 });
  await input.fill('Ship it.\nThen tag v0.2 – 🚀');
  await reply.getByRole('button', { name: 'Send' }).click();
  await expect(reply.locator('.reply-status')).toHaveText('Delivered. The session has your reply.', { timeout: 15000 });
  await expect(input).toHaveValue('');
  expect(sentFor('reply-1')).toEqual([{ id: 'reply-1', request: expect.stringMatching(UUID), text: 'Ship it.\nThen tag v0.2 – 🚀' }]);
  expect(typedFor('reply-1').map((r) => r.said)).toEqual(['Ship it. Then tag v0.2 – 🚀']);
  await expect(card(page, 'reply-1')).toHaveAttribute('data-status', 'working');

  // The socket drops while the host is still delivering: resent with the same request, and
  // the host returns the first outcome without typing it again.
  host.scriptReplies({ outcome: 'delivered', holdMs: 4000 });
  await input.fill('and the release notes');
  await reply.getByRole('button', { name: 'Send' }).click();
  await expect.poll(() => sentFor('reply-1').length, { timeout: 10000 }).toBe(2);
  for (const ws of sockets.splice(0)) { await ws.close({ code: 4000, reason: 'test drop' }); }
  await expect(reply.locator('.reply-status')).toHaveText('Delivered. The session has your reply.', { timeout: 30000 });
  const sent = sentFor('reply-1');
  expect(sent).toHaveLength(3);
  expect(sent[2].request).toBe(sent[1].request);
  expect(typedFor('reply-1')).toHaveLength(2);

  // A question arrives: the box is held, and the host refuses a reply sent anyway.
  const asked = new Date().toISOString();
  await host.upsert({ id: 'reply-1', agent: 'copilot', title: 'Takes replies', status: 'waiting', statusSince: asked, updatedAt: asked, activity: {}, question: { id: 'rq1', kind: 'form', message: 'Which?', fields: [], askedAt: asked } });
  await expect(input).toBeDisabled({ timeout: 15000 });
  const direct = await page.evaluate(() => window.agentBridge.hosts.client.reply({ id: 'reply-1', request: crypto.randomUUID(), text: 'not now' }));
  expect(direct).toEqual({ outcome: 'refused', reason: 'the session is waiting for an answer to its question; answer that first' });
  expect(typedFor('reply-1')).toHaveLength(2);
});

test.describe('notifications', () => {
  // The app needs its service worker for a push subscription.
  test.use({ serviceWorkers: 'allow' });

  // A browser that grants notifications and makes push subscriptions without a push service:
  // headless browsers have neither. The permission and the subscription outlive a reload, as
  // a real browser's do.
  async function fakePush(context) {
    await context.addInitScript(() => {
      if (typeof PushManager === 'undefined') { return; }
      const saved = () => JSON.parse(localStorage.getItem('test.push') ?? 'null');
      Object.defineProperty(Notification, 'permission', { configurable: true, get: () => localStorage.getItem('test.permission') ?? 'default' });
      Notification.requestPermission = async () => { localStorage.setItem('test.permission', 'granted'); return 'granted'; };
      const make = (s) => s && {
        endpoint: s.endpoint,
        options: { applicationServerKey: new Uint8Array(s.key).buffer },
        toJSON: () => ({ endpoint: s.endpoint, keys: { p256dh: 'BCVxsr7N_eNgVRqvHtD0zTZsEc6-VV-JvLexhqUzORcxaOzi6-AYWXvTBHm4bjyPjs7Vd8pZGH6SRpkNtoIAiw4', auth: 'BTBZMqHH6r4Tts7J_aSIgg' } }),
        unsubscribe: async () => { localStorage.removeItem('test.push'); localStorage.setItem('test.unsubscribed', s.endpoint); return true; },
      };
      PushManager.prototype.getSubscription = async () => make(saved());
      PushManager.prototype.subscribe = async (o) => {
        const key = [...new Uint8Array(o.applicationServerKey.buffer ?? o.applicationServerKey)];
        localStorage.setItem('test.push', JSON.stringify({ endpoint: `https://web.push.apple.com/e2e-${Date.now()}`, key, visible: o.userVisibleOnly }));
        return make(saved());
      };
    });
  }

  test('turning notifications on registers this phone with the machine, again on every channel, and off takes it back', async ({ page, context, browserName }) => {
    await fakePush(context);
    await signIn(page);
    test.skip(await page.evaluate(() => typeof PushManager === 'undefined'), `${browserName} here has no PushManager`);
    await page.click('[data-device-name="fixture-host"] button');
    await expect(page.locator('.session').first()).toBeVisible({ timeout: 30000 });
    const toggle = page.locator('#notify-toggle');
    await expect(toggle).toHaveText('Turn on notifications');
    await expect(toggle).toHaveAttribute('aria-pressed', 'false');
    const before = host.push.length;

    await toggle.click();
    await expect(toggle).toHaveText('Turn off notifications', { timeout: 15000 });
    await expect(toggle).toHaveAttribute('aria-pressed', 'true');
    await expect(page.locator('#notify-note')).toContainText('On for this device');
    await expect.poll(() => host.push.length).toBe(before + 1);
    const sub = host.push[before];
    const saved = await page.evaluate(() => JSON.parse(localStorage.getItem('test.push')));
    expect(sub.op).toBe('push.subscribe');
    expect(sub.data).toEqual({
      endpoint: saved.endpoint,
      keys: { p256dh: 'BCVxsr7N_eNgVRqvHtD0zTZsEc6-VV-JvLexhqUzORcxaOzi6-AYWXvTBHm4bjyPjs7Vd8pZGH6SRpkNtoIAiw4', auth: 'BTBZMqHH6r4Tts7J_aSIgg' },
      app: appUrl,
      device: expect.stringContaining('Agent Bridge'),
    });
    // Subscribed with the relay's own VAPID key, for visible notifications only.
    const key = (await (await fetch(`${relayUrl}/v1/push/key`)).json()).key;
    expect(Buffer.from(saved.key).toString('base64url')).toBe(key);
    expect(saved.visible).toBe(true);

    // Opened again later: registered again on the new channel, with the same subscription.
    await page.reload();
    await expect(page.locator('.session').first()).toBeVisible({ timeout: 30000 });
    await expect.poll(() => host.push.length, { timeout: 15000 }).toBe(before + 2);
    expect(host.push[before + 1].data.endpoint).toBe(saved.endpoint);

    await page.locator('#notify-toggle').click();
    await expect(page.locator('#notify-toggle')).toHaveText('Turn on notifications', { timeout: 15000 });
    await expect.poll(() => host.push.length).toBe(before + 3);
    expect(host.push[before + 2]).toMatchObject({ op: 'push.unsubscribe', data: { endpoint: saved.endpoint } });
    expect(await page.evaluate(() => localStorage.getItem('test.unsubscribed'))).toBe(saved.endpoint);
  });

  test('a notification\'s link opens its machine and session with the question ready, and so does a tap while the app is open', async ({ page }) => {
    const t = new Date().toISOString();
    const question = (id) => ({ id: `q-${id}`, kind: 'form', message: 'Which one?', askedAt: t, fields: [{ name: 'answer', type: 'choice', options: [{ value: 'a' }, { value: 'b' }] }] });
    for (const id of ['linked-1', 'linked-2']) {
      await host.upsert({ id, agent: 'copilot', title: id, status: 'waiting', statusSince: t, updatedAt: t, activity: {}, question: question(id) });
    }
    await signIn(page);
    await page.goto(`${appUrl}?host=${host.deviceId}&session=linked-1&question=q-linked-1`);
    await expect(card(page, 'linked-1')).toHaveClass(/linked/, { timeout: 30000 });
    await expect(card(page, 'linked-1').locator('.question-slot button').first()).toBeFocused();
    expect(new URL(page.url()).search).toBe('');

    // What the worker tells an open app when one of its notifications is tapped.
    await page.evaluate((url) => navigator.serviceWorker.dispatchEvent(new MessageEvent('message', { data: { type: 'agent-bridge.open', url } })),
      `${appUrl}?host=${host.deviceId}&session=linked-2&question=q-linked-2`);
    await expect(card(page, 'linked-2')).toHaveClass(/linked/);
    await expect(card(page, 'linked-2').locator('.question-slot button').first()).toBeFocused();
  });
});

test('every machine shows its sessions and version, and its agents\' usage, merged, whether or not it is the one being viewed', async ({ page }) => {
  const at = (ms) => new Date(Date.now() + ms).toISOString();
  const copilot = (percent) => ({ agents: [{ agent: 'copilot', label: 'GitHub Copilot', account: 'octo', plan: 'enterprise', source: 'api', measuredAt: at(0), windows: [{ key: 'plan', label: 'Plan', percent, startsAt: at(-10 * 86400000), resetsAt: at(20 * 86400000) }] }] });
  const t = at(0);
  const usageHost = await startFixtureHost({
    base: relayUrl, name: 'usage-host', usage: copilot(40),
    sessions: [{ id: 'u-1', agent: 'copilot', title: 'One', status: 'idle', statusSince: t, updatedAt: t, activity: {} }, { id: 'u-2', agent: 'claude', title: 'Two', status: 'idle', statusSince: t, updatedAt: t, activity: {} }],
  });
  try {
    await signIn(page);
    // Not viewed: a short channel tells the list what it is and the panel what it has spent.
    const row = page.locator('[data-device-name="usage-host"]');
    await expect(row.locator('.machine-status')).toHaveText(' · 2 sessions · 0.0.0-fixture (dev)', { timeout: 30000 });
    await expect(page.locator('#usage .usage-summary')).toHaveText('GitHub Copilot 40%');
    await expect(page.locator('#usage .usage-account')).toHaveText('octo · enterprise');
    // A machine that offers no usage says only its version.
    await expect(page.locator('[data-device-name="fixture-host"] .machine-status')).toHaveText(' · 0.0.0-fixture');
    expect(usageHost.log.filter((op) => op === 'usage.get').length).toBe(1);

    // Viewed: the usage comes on its own channel, as it changes.
    await row.locator('button').click();
    await expect(page.locator('.session')).toHaveCount(2, { timeout: 30000 });
    await expect.poll(() => usageHost.log.filter((op) => op === 'usage.subscribe').length, { timeout: 15000 }).toBe(1);
    await usageHost.setUsage(copilot(91));
    await expect(page.locator('#usage .usage-summary')).toHaveText('GitHub Copilot 91%');
    await expect(page.locator('#usage .usage-figure')).toHaveClass(/crit/);
    await expect(page.locator('#usage .usage-verdict')).toHaveText('58% ahead of pace');
  } finally {
    usageHost.close();
  }
});