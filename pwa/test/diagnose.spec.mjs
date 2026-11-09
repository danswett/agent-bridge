// "Diagnose this machine" (#802) on the Machines page, in demo mode with three machines and
// ?diagnose=1, where the laptop offers diagnostics.get and the other two are hosts from before
// it: the laptop's snapshot shown as plain text with only what the app reads, Copy only once the
// person has said they reviewed it, Escape closing it, a refusal said in the person's words and
// never with the host's text, and an older host showing an update hint in place of Diagnose and
// never asked.
import { test, expect } from '@playwright/test';
import { go } from './nav.mjs';

const LAPTOP = 'demo-laptop';
const SHADOW = 'demo-shadow';
const CANARY = 'ghp_CANARYtoken0123456789abcdef';

const item = (page, id) => page.locator(`#machine-list [data-device-id="${id}"]`);
const demo = (page, id, field) => page.evaluate(([id, field]) => window.agentBridgeDemo.machines[id][field], [id, field]);

async function openDemo(page) {
  await page.addInitScript(() => {
    window.__cspViolations = [];
    document.addEventListener('securitypolicyviolation', (e) => window.__cspViolations.push(`${e.violatedDirective} ${e.blockedURI}`));
  });
  await page.goto('?demo=1&static=1&answerDelay=200&machines=3&diagnose=1');
  await page.waitForSelector('html[data-demo-ready]');
  await page.evaluate(() => {
    window.__copies = [];
    const clipboard = { writeText: (text) => { window.__copies.push(text); return Promise.resolve(); } };
    Object.defineProperty(navigator, 'clipboard', { configurable: true, get: () => clipboard });
    document.execCommand = (command) => {
      const a = document.activeElement;
      if (command !== 'copy' || !(a instanceof HTMLTextAreaElement)) { return false; }
      window.__copies.push(a.value.slice(a.selectionStart, a.selectionEnd));
      return true;
    };
  });
  await go(page, 'machines');
}

test.afterEach(async ({ page }) => {
  expect(await page.evaluate(() => window.__cspViolations ?? [])).toEqual([]);
});

test.use({ viewport: { width: 390, height: 664 }, hasTouch: true });

test('another machine\'s diagnostics are shown as closed plain text, and Copy waits for the review', async ({ page }) => {
  await openDemo(page);
  // What a host should never send, everywhere it could: none of it reaches the page or the copy.
  await page.evaluate(([id, canary]) => {
    const d = window.agentBridgeDemo.machines[id].diagnostics;
    d.relay.url = `wss://relay/?t=${canary}`;
    d.holds.readOnly = canary;
    d.readiness.agents[0].label = canary;
    d.journal.entries[0].message = canary;
    d.journal.entries.push({ seq: 9, at: canary, family: 'Crash', kind: 'UnhandledException', outcome: 'Failed', exception: { type: `C:\\${canary}`, frames: [], message: canary } });
    d.journal.entries.push({ seq: 10, at: '2026-11-03T10:30:06+00:00', family: 'Crash', kind: 'UnhandledException', outcome: 'Failed', exception: { type: `System.${canary}`, frames: [`System.${canary}.Run`], message: canary } });
  }, [LAPTOP, CANARY]);

  const laptop = item(page, LAPTOP);
  await laptop.locator('.machine-diagnose').click();
  const text = laptop.locator('pre.machine-diagnostics');
  await expect(text).toContainText('relay: connected');
  await expect(text).toContainText('read-only: unknown');
  // Times in Pacific, as the person reads everything here; the wire keeps them in UTC.
  await expect(text).toContainText('last restart: 2026-11-03 02:30:00 PST, cause unavailable (notRecorded)');
  expect(await text.textContent()).toMatch(/^[^\n#]+ diagnostics \u00B7 Laptop \u00B7 \d{4}-\d\d-\d\d \d\d:\d\d:\d\d P[SD]T\n/);
  await expect(text).toContainText('RelayConnect.RelayConnected Ok');
  await expect(text).toContainText('exception <unrecognized> (1 frames)');
  await expect(text).toContainText('1 unreadable');
  await expect(page.locator('body')).not.toContainText(CANARY);
  expect(await demo(page, LAPTOP, 'diagnosticsLog')).toEqual([{ maxEntries: 100 }]);

  // Not copied until the box is checked.
  const copy = laptop.locator('.machine-diagnose-copy');
  await expect(copy).toHaveAttribute('aria-disabled', 'true');
  // Pressed anyway (aria-disabled keeps it focusable), it copies nothing.
  await copy.click({ force: true });
  expect(await page.evaluate(() => window.__copies)).toEqual([]);
  await laptop.locator('.machine-diagnose-review').check();
  await expect(copy).not.toHaveAttribute('aria-disabled', 'true');
  await copy.click();
  await expect(copy).toHaveText('Copied');
  const copied = await page.evaluate(() => window.__copies);
  expect(copied).toEqual([await text.textContent()]);
  expect(copied[0]).not.toContain(CANARY);

  // Escape closes the report, and focus goes back to Diagnose.
  await text.focus();
  await page.keyboard.press('Escape');
  await expect(text).toHaveCount(0);
  await expect(laptop.locator('.machine-diagnose')).toBeFocused();
});

// The longest report a host may send, with every line as long as it can be, wraps inside its row
// on the narrowest phone: nothing on the page scrolls sideways.
for (const width of [390, 320]) {
  test(`the longest report wraps inside its row at ${width}px`, async ({ page }) => {
    await page.setViewportSize({ width, height: 664 });
    await openDemo(page);
    await page.evaluate((id) => {
      const d = window.agentBridgeDemo.machines[id].diagnostics;
      const long = '123456789.123456789.123456789.123456789-preview.123456789';
      d.host.version = long;
      d.readiness.omitted = 3;
      d.readiness.agents = ['copilot', 'claude', 'codex', 'other', 'copilot', 'claude', 'codex', 'other'].map((a) => ({
        id: a, version: long, lastHookHandledAt: '2026-11-03T11:58:00+00:00',
        installation: 'needsAction', versionCheck: 'needsAction', hooks: 'needsAction', authentication: 'needsAction', activation: 'needsAction',
      }));
      d.journal.entries = Array.from({ length: 200 }, (_, i) => ({
        seq: 200 - i, at: '2026-11-03T10:30:05+00:00', family: 'RelayConnect', kind: 'RelayConnectFailed', outcome: 'Failed',
        code: -2147483648, version: long, ms: 2147483647, omitted: 127,
        stages: Array.from({ length: 8 }, () => ({ name: 'Handshake', ms: 2147483647 })),
        exception: { type: 'System.Security.Authentication.AuthenticationException', frames: Array.from({ length: 16 }, () => 'Frame') },
      }));
      d.journal.omittedByLimit = 2147483647;
      d.journal.persistFailures = 2147483647;
    }, LAPTOP);

    const laptop = item(page, LAPTOP);
    await laptop.locator('.machine-diagnose').click();
    const text = laptop.locator('pre.machine-diagnostics');
    await expect(text).toContainText('System.Security.Authentication.AuthenticationException (16 frames)');
    const fit = await page.evaluate((id) => {
      const row = document.querySelector(`#machine-list [data-device-id="${id}"]`);
      const pre = row.querySelector('pre.machine-diagnostics');
      const longest = Math.max(...pre.textContent.split('\n').map((l) => l.length));
      return {
        page: document.documentElement.scrollWidth - document.documentElement.clientWidth,
        body: document.body.scrollWidth - document.body.clientWidth,
        row: row.scrollWidth - row.clientWidth,
        pre: pre.scrollWidth - pre.clientWidth,
        right: pre.getBoundingClientRect().right - row.getBoundingClientRect().right,
        width: innerWidth,
        longest,
        lines: pre.textContent.split('\n').length,
        tall: pre.getBoundingClientRect().height / parseFloat(getComputedStyle(pre).lineHeight),
      };
    }, LAPTOP);
    // Long enough that it could only fit by wrapping.
    expect(fit.longest * 6).toBeGreaterThan(fit.width);
    expect(fit.tall).toBeGreaterThan(fit.lines);
    expect(fit).toMatchObject({ page: 0, body: 0, row: 0, pre: 0 });
    expect(fit.right).toBeLessThanOrEqual(0);
  });
}

// What is held from discovery, said only as the kinds this device knows: one it doesn't is said
// as unknown, never named and never read as none.
test('a held kind this device does not know is said as unknown and never named', async ({ page }) => {
  await openDemo(page);
  const laptop = item(page, LAPTOP);
  const text = laptop.locator('pre.machine-diagnostics');
  const show = async (held) => {
    await page.evaluate(([id, held]) => { window.agentBridgeDemo.machines[id].diagnostics.holds.discoveryHeld = held; }, [LAPTOP, held]);
    if (await text.count()) {
      await laptop.locator('.machine-diagnose-close').click();
      await expect(text).toHaveCount(0);
      await page.waitForTimeout(2100);
    }
    await laptop.locator('.machine-diagnose').click();
    await expect(text).toBeVisible();
  };

  await show(['futureAgent']);
  await expect(text).toContainText('discovery held: unavailable (unknown)\n');
  await show(['claude', 'futureAgent', 'claude']);
  await expect(text).toContainText('discovery held: claude, unknown\n');
  await laptop.locator('.machine-diagnose-review').check();
  await laptop.locator('.machine-diagnose-copy').click();
  const copied = await page.evaluate(() => window.__copies);
  expect(copied).toEqual([await text.textContent()]);
  await show([]);
  await expect(text).toContainText('discovery held: none\n');

  await expect(page.locator('body')).not.toContainText('futureAgent');
  expect(copied[0]).not.toContain('futureAgent');
  expect(await demo(page, LAPTOP, 'diagnosticsLog')).toHaveLength(3);
});

test('an older machine offers no Diagnose and is never asked, and a refusal is said in plain words', async ({ page }) => {
  await openDemo(page);
  const shadow = item(page, SHADOW);
  await expect(shadow.locator('.machine-diagnose')).toHaveCount(0);
  await expect(shadow.locator('.machine-diagnose-hint')).toHaveText(/^Can't be diagnosed from this device \u00B7 update .+ on DEV-VM1 \(shadow\)$/);
  expect(await demo(page, SHADOW, 'calls')).not.toContain('diagnostics.get');
  expect(await demo(page, 'demo', 'calls')).not.toContain('diagnostics.get');

  // The host's own reason, in the person's words.
  await page.evaluate((id) => {
    window.agentBridgeDemo.machines[id].diagnosticsRefusal = "diagnostics are for a person's device approved at this machine, through the relay";
  }, LAPTOP);
  const laptop = item(page, LAPTOP);
  await laptop.locator('.machine-diagnose').click();
  await expect(laptop.locator('.machine-error')).toHaveText('Approve this device at Laptop to diagnose it');
  // The line sits under the row's block, still in the row, and Diagnose points at it (#858).
  const errId = await laptop.locator(':scope > .machine-error').getAttribute('id');
  expect((await laptop.locator('.machine-diagnose').getAttribute('aria-describedby')).split(' ')).toContain(errId);
  await expect(laptop.locator('pre.machine-diagnostics')).toHaveCount(0);
  await expect(laptop.locator('.machine-diagnose-copy')).toHaveCount(0);

  // Any other reason is not shown.
  await page.evaluate(([id, canary]) => { window.agentBridgeDemo.machines[id].diagnosticsRefusal = `refused: ${canary}`; }, [LAPTOP, CANARY]);
  await page.waitForTimeout(2100);
  await laptop.locator('.machine-diagnose').click();
  await expect(laptop.locator('.machine-error')).toHaveText("Laptop couldn't be reached \u00B7 try again");
  await expect(page.locator('body')).not.toContainText(CANARY);
  expect(await demo(page, LAPTOP, 'diagnosticsLog')).toHaveLength(2);
});