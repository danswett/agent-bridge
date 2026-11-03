// Accessibility, scanned (#356, quality/README.md §3.4): axe on every view the demo reaches, in
// light, dark and forced colours, at a phone's and a desktop's width. A serious or critical
// violation fails, unless it is a known one below, listed by rule and selector with the issue that
// tracks it. A rule is never turned off: a new element breaking a known rule still fails.
import { test, expect } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { go, open, row, view } from './nav.mjs';

const COPILOT = '02d9e5d5-6b35-49bd-b083-39572fbff648';
const LAND = '8c4e1f20-5a7b-4d3c-9e61-0b2f7d9a1c35';
const DELEGATES = 'demo-delegates';

// WCAG 2.0 to 2.2 A and AA, and axe's best practices. Chosen by tag, so a rule axe still calls
// experimental runs too where it tests a WCAG criterion (label-content-name-mismatch, 2.5.3).
// axe-core is locked (package-lock.json), so a new rule arrives only with a deliberate upgrade.
const TAGS = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22a', 'wcag22aa', 'best-practice'];
const FAILING = new Set(['serious', 'critical']);

// Known violations, each tracked by an issue. `selector` is matched against the element axe names,
// so an entry covers those elements only and never the rule: the same rule elsewhere still fails.
const KNOWN = [
  // A row's name is its glyph's "title, status, machine" (ui.md §9); the row shows its facts and
  // age as well.
  { rule: 'label-content-name-mismatch', selector: '.session .row-main', issue: 365 },
  // Bridge text on the selected fill, in light: 4.45:1 over the card, 4.09:1 over the shell.
  { rule: 'color-contrast', selector: '.machine.selected .machine-state', issue: 366 },
  { rule: 'color-contrast', selector: '.machine.selected .machine-update-check', issue: 366 },
  { rule: 'color-contrast', selector: '.machine.selected .machine-update-install', issue: 366 },
];

const THEMES = [
  { name: 'light', media: { colorScheme: 'light' } },
  { name: 'dark', media: { colorScheme: 'dark' } },
  { name: 'forced colours', media: { colorScheme: 'light', forcedColors: 'active' }, chromiumOnly: true },
];

// Tall, so a scroll pane holds its whole content: axe takes what an overflow clips for hidden,
// and would then miss its contrast and read a field's visible label as missing.
const LAYOUTS = [
  { name: 'on a phone', viewport: { width: 390, height: 1800 }, phone: true },
  { name: 'on a desktop', viewport: { width: 1440, height: 1800 }, phone: false },
];

const permission = {
  id: 'r1#0123456789abcdef', kind: 'approval', message: 'Allow **shell**?\n\nPush the branch\n\n```\ngit push origin main --force-with-lease\n```',
  fields: [{ name: 'decision', type: 'choice', required: true, options: [{ value: 'approve', label: 'Approve once' }, { value: 'deny', label: 'Deny' }] }],
};

const TRUST = {
  id: '5d0c9f3e-2b1a-4c8d-9e7f-6a5b4c3d2e1f', agent: 'codex', kind: 'codex', label: 'Codex', workspace: 'agent-bridge',
  folder: 'C:\\Users\\demo\\repos\\agent-bridge\\web', root: 'c:\\users\\demo\\repos\\agent-bridge',
  rootNote: 'Note: You’re in a subdirectory of a Git project. Trusting will apply to the repository root:',
  warning: 'Trust this folder? Codex can read, edit, and run files here. Continue only if you trust these files.',
  answerable: true,
};

async function openDemo(page, query) {
  await page.addInitScript(() => {
    window.__cspViolations = [];
    document.addEventListener('securitypolicyviolation', (e) => window.__cspViolations.push(`${e.violatedDirective} ${e.blockedURI}`));
  });
  await page.goto(`?demo=1&static=1${query}`);
  await page.waitForSelector('html[data-demo-ready]');
}

// The serious and critical violations on the page as it is, less the known ones, as
// "rule: selector" lines that say what to fix.
async function scan(page) {
  const { violations } = await new AxeBuilder({ page }).withTags(TAGS).analyze();
  const found = [];
  for (const v of violations.filter((x) => FAILING.has(x.impact))) {
    for (const node of v.nodes) {
      // One selector, unless the element is inside a frame or a shadow root (one selector per
      // level), which the app has none of: such a node is never taken for a known one.
      const [selector, ...within] = node.target;
      const target = node.target.join(' >>> ');
      const known = within.length === 0 && await page.evaluate(({ selector, known }) => {
        const el = document.querySelector(selector);
        return known.some((s) => el?.matches(s));
      }, { selector, known: KNOWN.filter((k) => k.rule === v.id).map((k) => k.selector) });
      if (!known) { found.push(`${v.id} (${v.impact}): ${target}: ${node.failureSummary.replace(/\s+/g, ' ')}`); }
    }
  }
  return found;
}

for (const layout of LAYOUTS) {
  for (const theme of THEMES) {
    test.describe(`${layout.name} in ${theme.name}`, () => {
      test.use({ viewport: layout.viewport, hasTouch: layout.phone });

      test.beforeEach(async ({ page, browserName }) => {
        test.skip(theme.chromiumOnly && browserName !== 'chromium', 'forced colours are emulated in Chromium only');
        await page.emulateMedia(theme.media);
      });

      test.afterEach(async ({ page }) => {
        expect(await page.evaluate(() => window.__cspViolations ?? [])).toEqual([]);
      });

      // Each view a step, each step's violations reported apart, so one run lists them all.
      const check = async (page, name) => {
        await test.step(name, async () => { expect.soft(await scan(page), name).toEqual([]); });
      };

      test('the sessions, Needs you, a question, an approval, delegates, details, tools, menus and End session have no serious or critical violations', async ({ page }) => {
        await openDemo(page, '&machines=3&delegates=1');
        // An approval in Needs you beside the worked example's question.
        await page.evaluate(({ id, q, t }) => {
          const h = window.agentBridgeDemo.host;
          h.upsert({ ...h.get(id), status: 'waiting', statusSince: t, updatedAt: t, question: { kind: 'form', askedAt: t, ...q } });
        }, { id: LAND, q: permission, t: new Date(Date.now() + 1000).toISOString() });
        await expect(page.locator('#needs-you .needs-you-list > li')).not.toHaveCount(0);
        await check(page, 'the session list with Needs you');

        await row(page, COPILOT).locator('.row-main').focus();
        await page.keyboard.press('Shift+F10');
        await expect(page.locator('.row-menu')).toBeVisible();
        await check(page, 'a row\'s menu');
        await page.keyboard.press('Escape');
        await expect(page.locator('.row-menu')).toHaveCount(0);

        if (layout.phone) {
          await page.locator('.search-open').click();
          await expect(page.locator('#search')).toBeVisible();
        }
        await page.locator('#search').fill('demo');
        await check(page, 'search');
        await page.locator('#search').fill('');
        if (layout.phone) { await page.locator('#search-close').click(); }

        const copilot = await open(page, COPILOT);
        await expect(copilot.locator('.question')).toBeVisible();
        await check(page, 'a session asking a question');

        const land = await open(page, LAND);
        await check(page, 'a session asking for approval');
        await land.getByRole('button', { name: /^Approve once/ }).click();
        await expect(land.locator('.answer-receipt, .answer-status').filter({ hasText: /\S/ }).first()).toBeVisible();
        await check(page, 'an approval answered');

        const delegating = await open(page, DELEGATES);
        if (layout.phone) { await delegating.getByRole('tab', { name: 'Delegates' }).click(); }
        const suite = delegating.locator('.delegate[data-id="demo-agent-suite"]');
        await suite.locator('.delegate-summary').click();
        await expect(suite.locator('.delegate-task')).toBeVisible();
        await delegating.locator('.delegates-finished > summary').click();
        await check(page, 'a session\'s delegates, one open and the finished unfolded');

        if (layout.phone) {
          await delegating.getByRole('tab', { name: 'Tools' }).click();
          await check(page, 'a session\'s tools');
          await delegating.locator('.more').click();
          await expect(page.locator('.row-menu')).toBeVisible();
          await check(page, 'a session\'s overflow menu');
          await page.locator('.row-menu').getByRole('menuitem', { name: 'Details' }).click();
          await expect(delegating.locator('.details')).toBeVisible();
          await check(page, 'a session\'s details');
        } else {
          await expect(delegating.locator('.details')).toBeVisible();
          await expect(delegating.locator('.tools-panel')).toBeVisible();
        }
        // A delegating session ends only on a second press: the first arms it.
        await delegating.locator('.end-session').getByRole('button', { name: 'End session' }).click();
        await expect(delegating.locator('.end-session')).toHaveAttribute('data-stop', 'confirm');
        await check(page, 'End session pressed once');
      });

      test('Launch, a folder-trust question, Machines, usage and settings have no serious or critical violations', async ({ page }) => {
        // Forget stays armed for the scan however slow the machine is.
        await openDemo(page, '&updates=1&offline=1&forgetMs=60000');
        await go(page, 'launch');
        await expect(page.locator('#launch [name="agent"]')).toBeVisible();
        await check(page, 'Launch');

        await page.evaluate((q) => window.agentBridgeDemo.scriptLaunches({ outcome: 'unconfirmed', reason: 'Codex started, and is asking whether to trust agent-bridge', trust: q }), TRUST);
        await page.locator('#launch [name="agent"]').selectOption('codex');
        await page.locator('#launch [name="prompt"]').fill('Add pagination.');
        await page.locator('#launch').getByRole('button', { name: 'Launch' }).click();
        await expect(page.locator('#launch .trust-question')).toBeVisible();
        await check(page, 'a folder-trust question');

        await go(page, 'machines');
        const offline = page.locator('#machine-list').getByRole('button', { name: /^Forget / });
        await offline.first().click();
        await expect(page.locator('#machine-list').getByRole('button', { name: /^Press again to forget / })).toBeVisible();
        await check(page, 'Machines, updating and offline, with Forget armed');

        await go(page, 'usage');
        await check(page, 'usage');

        await go(page, 'settings');
        await check(page, 'settings');
      });

      test('the sign-in page has no serious or critical violations', async ({ page }) => {
        await page.addInitScript(() => {
          window.__cspViolations = [];
          document.addEventListener('securitypolicyviolation', (e) => window.__cspViolations.push(`${e.violatedDirective} ${e.blockedURI}`));
        });
        await page.goto('./');
        await expect(page.locator('#signin')).toBeVisible();
        await check(page, 'sign-in');
      });
    });
  }
}
