// End to end through a real relay: the spike dev stack (a fake GitHub, the relay and its
// S4 demo host) plus test/e2e/fixture-host.mjs, a Node host that speaks the sessions
// protocol. The PWA is served from its own origin, as on GitHub Pages, under its CSP, and
// the relay is told to allow that origin. Covered: sign-in, the key proof, presence,
// a Noise channel, hello, the subscription snapshot, live upserts and removes,
// session.get, reconnecting after the socket drops (with a session that ended meanwhile
// dropped by the new snapshot), returning to the last host, ?host= links, a host that
// predates the protocol, and answering a question: delivered, resent with the same request
// after the socket drops mid-answer (typed once), and the host's own checks; and replying:
// delivered as one line, resent after the socket drops mid-reply (typed once), refused while
// the session asks a question.
//
// Needs: dotnet build spikes/s4-relay/src/S4.DevStack -c Release
import { test, expect } from '@playwright/test';
import { spawn } from 'node:child_process';
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

let stack, site, host, appUrl;
const stackOut = [];

test.beforeAll(async ({}, info) => {
  test.setTimeout(120000);
  // PWA_E2E_PORT moves these, for a machine running another checkout's tests at once.
  const relayPort = (Number(process.env.PWA_E2E_PORT) || 8800) + info.parallelIndex * 2;
  const sitePort = relayPort + 1;
  const relay = `http://127.0.0.1:${relayPort}`;
  const dir = buildSite({ out: path.join(pwa, '.site', `e2e-${info.project.name}`), relay });
  site = await startStaticServer({ dir, port: sitePort });
  appUrl = `http://127.0.0.1:${sitePort}${APP_PATH}`;
  stack = spawn('dotnet', [dll, 'serve', '--port', String(relayPort)], {
    // The page's origin, which the relay must accept for sign-in (CORS) and the socket.
    env: { ...process.env, Relay__AllowedOrigins__0: `http://127.0.0.1:${sitePort}` },
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
