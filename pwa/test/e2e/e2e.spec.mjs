// End to end through a real relay: the spike dev stack (a fake GitHub, the relay and its
// S4 demo host) plus test/e2e/fixture-host.mjs, a Node host that speaks the sessions
// protocol. The PWA is served from its own origin, as on GitHub Pages, under its CSP, and
// the relay is told to allow that origin. Covered: sign-in, the key proof, presence,
// a Noise channel, hello, the subscription snapshot, live upserts and removes,
// session.get, reconnecting after the socket drops (with a session that ended meanwhile
// dropped by the new snapshot), returning to the last host, ?host= links, and a host
// that predates the protocol.
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

test.skip(!existsSync(dll), 'the dev stack is not built: dotnet build spikes/s4-relay/src/S4.DevStack -c Release');
test.describe.configure({ mode: 'serial' });

let stack, site, host, appUrl;
const stackOut = [];

test.beforeAll(async ({}, info) => {
  test.setTimeout(120000);
  const relayPort = 8800 + info.parallelIndex * 2;
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
