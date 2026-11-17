import { test, expect } from '@playwright/test';
import { open } from './nav.mjs';
import { loadEarlierTo } from './history-fixture.mjs';
import { installTypingMeter, summarizeTyping } from './typing-perf.mjs';

const ID = 'typing-perf-0';
const TEXT = 'ab'.repeat(40);
const scenarios = [
  { name: 'short', turns: 5, sessions: 1, streaming: false },
  { name: 'long-transcript', turns: 500, sessions: 1, streaming: false },
  { name: 'streaming', turns: 500, sessions: 1, streaming: true },
  { name: 'many-sessions-streaming', turns: 500, sessions: 200, streaming: true },
];

async function prepare(page, scenario) {
  await page.goto('?demo=1&static=1&signin=1&name=PERF#/sessions');
  await page.waitForSelector('html[data-demo-ready]');
  await page.evaluate(({ turns, sessions }) => {
    const at = new Date().toISOString();
    const startedAt = new Date(Date.now() - 30 * 86400000).toISOString();
    for (const id of [...window.agentBridgeDemo.host.sessions.keys()]) { window.agentBridgeDemo.remove(id); }
    for (let i = 0; i < sessions; i += 1) {
      window.agentBridgeDemo.upsert({
        id: `typing-perf-${i}`, agent: 'copilot', title: `Typing performance ${i}`,
        cwd: 'C:\\demo', status: 'working', statusSince: at, startedAt, updatedAt: at,
        processId: 4300 + i,
        activity: { userMessage: 'Measure typing.', turnCount: turns, longReplies: true },
      });
    }
  }, scenario);
  await expect(page.locator('.session[data-id^="typing-perf-"]')).toHaveCount(scenario.sessions);
  const session = await open(page, ID);
  const pane = session.locator('.stream-scroll');
  await expect(pane.locator('.history-turn')).not.toHaveCount(0);
  if (scenario.turns > 5) { await loadEarlierTo(pane, 104); }
  const input = session.getByRole('textbox', { name: 'Reply', exact: true });
  await expect(input).toBeEditable();
  await input.focus();
  const fixture = await pane.evaluate((el) => ({
    renderedHistoryTurns: el.querySelectorAll('.history-turn').length,
    transcriptCharacters: el.textContent.length,
  }));
  return { input, fixture };
}

async function typeAndMeasure(page, input, text) {
  await input.evaluate(installTypingMeter);
  for (let i = 0; i < text.length; i += 1) {
    await page.keyboard.press(text[i]);
    await page.waitForFunction((count) => window.__typingPerf.samples.length === count, i + 1);
  }
  const measured = await page.evaluate(() => window.__typingPerf.finish());
  expect(measured.trustedInputs).toBe(text.length);
  expect(measured.samples).toHaveLength(text.length);
  await expect(input).toHaveValue(text);
  return { ...measured, ...summarizeTyping(measured.samples, measured.worstFrameMs) };
}

for (const profile of [
  { name: 'windows', viewport: { width: 1280, height: 800 }, hasTouch: false },
  { name: 'ipad', viewport: { width: 1024, height: 1366 }, hasTouch: true },
]) {
  test.describe(`typing performance ${profile.name}`, () => {
    test.use({ viewport: profile.viewport, hasTouch: profile.hasTouch, deviceScaleFactor: 1 });
    test.beforeEach(async ({ page, context }) => {
      await context.route('**/*', (route) => {
        const url = new URL(route.request().url());
        return ['127.0.0.1', '[::1]'].includes(url.hostname) ? route.continue() : route.abort();
      });
      await context.routeWebSocket('**/*', (socket) => socket.close());
      await page.addInitScript((touch) => {
        window.__cspViolations = [];
        document.addEventListener('securitypolicyviolation', (event) => {
          window.__cspViolations.push(`${event.violatedDirective} ${event.blockedURI}`);
        });
        Object.defineProperty(navigator, 'platform', { value: touch ? 'MacIntel' : 'Win32' });
        Object.defineProperty(navigator, 'maxTouchPoints', { value: touch ? 5 : 0 });
        Object.defineProperty(navigator, 'userAgentData', { value: undefined });
      }, profile.hasTouch);
    });
    test.afterEach(async ({ page }) => {
      expect(await page.evaluate(() => window.__cspViolations)).toEqual([]);
    });

    for (const scenario of scenarios) {
      test(`reports compose latency with ${scenario.name}`, async ({ page, browserName }, info) => {
        test.setTimeout(120000);
        const errors = [];
        page.on('pageerror', (error) => errors.push(error.message));
        const { input, fixture } = await prepare(page, scenario);
        await page.evaluate((streaming) => {
          window.__typingStream = { ticks: 0 };
          if (!streaming) { return; }
          let response = '**Live response**\n\n' + '- A streamed transcript line with `code`.\n'.repeat(200);
          window.__typingStream.timer = setInterval(() => {
            const s = window.agentBridgeDemo.host.get('typing-perf-0');
            response += `\nChunk ${++window.__typingStream.ticks}`;
            window.agentBridgeDemo.upsert({
              ...s, updatedAt: new Date().toISOString(), activity: { ...s.activity, response },
            });
          }, 50);
        }, scenario.streaming);
        let measured;
        try {
          measured = await typeAndMeasure(page, input, TEXT);
        } finally {
          await page.evaluate(() => clearInterval(window.__typingStream.timer));
        }
        const streamUpdates = await page.evaluate(() => window.__typingStream.ticks);
        if (scenario.streaming) { expect(streamUpdates).toBeGreaterThan(0); }
        if (scenario.turns > 5) { expect(fixture.renderedHistoryTurns).toBeGreaterThanOrEqual(104); }
        expect(errors).toEqual([]);
        const report = {
          profile: profile.name, engine: browserName, runnerPlatform: process.platform,
          scenario, fixture, streamUpdates, ...measured,
          thresholdsMs: { p95: 100, max: 100, frameGap: 100 },
          mode: process.env.PWA_TYPING_PERF_GATE === '1' ? 'gate' : 'warning-first',
          serviceWorker: 'blocked by existing test configuration',
        };
        console.log(`TYPING_PERF ${JSON.stringify(report)}`);
        await info.attach('typing-perf.json', { body: JSON.stringify(report, null, 2), contentType: 'application/json' });
        if (report.breaches.length) {
          const description = `Under-100-ms typing budget exceeded: ${report.breaches.join(', ')}; p95=${report.p95}, max=${report.max}, frameGap=${report.worstFrameMs} ms`;
          info.annotations.push({ type: 'warning', description });
          console.warn(description);
        }
        if (process.env.PWA_TYPING_PERF_GATE === '1') { expect(report.breaches).toEqual([]); }
      });
    }

    test('the meter detects a deliberately blocked native keystroke', async ({ page }) => {
      const { input } = await prepare(page, scenarios[0]);
      await input.evaluate((el) => {
        el.addEventListener('keydown', () => {
          const until = performance.now() + 150;
          while (performance.now() < until) { /* Known main-thread stall: meter sensitivity control. */ }
        }, { once: true });
      });
      const measured = await typeAndMeasure(page, input, 'a');
      expect(measured.max).toBeGreaterThanOrEqual(150);
      expect(measured.breaches).toContain('max');
      console.log(`TYPING_PERF_CONTROL ${JSON.stringify({ profile: profile.name, ...measured })}`);
    });
  });
}
