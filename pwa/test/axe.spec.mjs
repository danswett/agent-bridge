// Accessibility, scanned (#356, quality/README.md §3.4): axe on every view the demo reaches, in
// light, dark and forced colours, at a phone's and a desktop's width. A serious or critical
// violation fails, unless it is a known one below, listed by rule and selector with the issue that
// tracks it. A rule is never turned off: a new element breaking a known rule still fails.
import { test, expect } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { countMotions, go, motionsDone, motionsStarted, open, row, view } from './nav.mjs';

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
const KNOWN = [];

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

// Records the page's CSP violations, which afterEach expects none of.
async function trackCsp(page) {
  await page.addInitScript(() => {
    window.__cspViolations = [];
    document.addEventListener('securitypolicyviolation', (e) => window.__cspViolations.push(`${e.violatedDirective} ${e.blockedURI}`));
  });
}

async function openDemo(page, query) {
  await trackCsp(page);
  await page.goto(`?demo=1&static=1${query}`);
  await page.waitForSelector('html[data-demo-ready]');
}

// The serious and critical violations on the page as it is, less the known ones, as
// "rule: selector: what to fix". only: { include, rules } scans those elements for those rules
// alone, and then also returns the elements they passed on, so a scan that found nothing to
// check can't pass.
async function scan(page, only = null) {
  let violations;
  let passes;
  for (let attempt = 1; ; attempt += 1) {
    // At rest: a fade still under way draws its text paler than it will be, which isn't the
    // contrast anyone reads (ui.md §5.3's fades last a few frames), and an element still at
    // opacity 0 isn't checked at all.
    const before = await motionsDone(page);
    if (before === null) { throw new Error('scan needs countMotions(page) before the page loads'); }
    const axe = new AxeBuilder({ page });
    ({ violations, passes } = await (only ? axe.include(only.include).withRules(only.rules) : axe.withTags(TAGS)).analyze());
    // A motion that started while axe read the page (#551) makes what it read neither the page
    // before nor the page after, and may have hidden an element mid-fade from it: that reading is
    // thrown away and the page read again once at rest. A page that never comes to rest fails.
    const moved = (await motionsStarted(page)) - before;
    if (!moved) { break; }
    if (attempt === 3) {
      const restless = [`the page never came to rest: motions started during each of ${attempt} scans (${moved} in the last)`];
      return only ? { failed: restless, passed: [] } : restless;
    }
  }
  const nodes = violations.filter((v) => FAILING.has(v.impact)).flatMap((v) => v.nodes.map((node) => ({ v, node })));
  // Whether each is a known one, in one call. A node has one selector, unless it is inside a frame
  // or a shadow root (one selector per level), which the app has none of: such a node is never
  // taken for a known one.
  const known = await page.evaluate((checks) => checks.map(({ target, selectors }) => {
    if (target.length !== 1) { return false; }
    // axe's selectors are unique; one that isn't can't say which element it meant.
    const els = document.querySelectorAll(target[0]);
    return els.length === 1 && selectors.some((s) => els[0].matches(s));
  }), nodes.map(({ v, node }) => ({ target: node.target, selectors: KNOWN.filter((k) => k.rule === v.id).map((k) => k.selector) })));
  const failed = nodes.filter((_, i) => !known[i]).map(({ v, node }) =>
    `${v.id} (${v.impact}): ${node.target.join(' >>> ')}: ${(node.failureSummary ?? v.help).replace(/\s+/g, ' ')}`);
  return only ? { failed, passed: passes.flatMap((p) => p.nodes.map((n) => n.target.join(' >>> '))) } : failed;
}

// The two that were known before ui.md §9.1 settled them, each on its own, in light at both
// widths: a session row's name holds every word the row draws (A1, #365), and a selected
// machine's state and its Check and Install, words that would be Bridge on the selected tint,
// read at 4.5:1 there, over a phone's shell and a desktop's layer (A8, #366).
for (const layout of LAYOUTS) {
  test.describe(`${layout.name} in light`, () => {
    test.use({ viewport: layout.viewport, hasTouch: layout.phone });

    test('every session row\'s name holds the words it draws, and a selected machine\'s state, Check and Install read at 4.5:1 on its tint', async ({ page }) => {
      await page.emulateMedia({ colorScheme: 'light' });
      await countMotions(page);
      // Rows with every machine shown, which draw their machine, and with one, which draw their
      // workspace.
      for (const query of ['&machines=3', '&updates=1']) {
        await openDemo(page, query);
        const rows = await scan(page, { include: '.session .row-main', rules: ['label-content-name-mismatch'] });
        expect(rows.failed, query).toEqual([]);
        expect(rows.passed.length, query).toBe(await page.locator('.session .row-main').count());
      }

      await go(page, 'machines');
      const selected = page.locator('.machine.selected');
      await expect(selected.locator('.machine-update-install')).toBeVisible();
      const words = await scan(page, { include: '.machine.selected', rules: ['color-contrast'] });
      expect(words.failed).toEqual([]);
      const checked = await page.evaluate((targets) => targets.map((t) => document.querySelector(t)?.className ?? ''), words.passed);
      for (const part of ['machine-state', 'machine-update-check', 'machine-update-install']) {
        expect(checked.some((c) => c.split(' ').includes(part)), `${part} is checked`).toBe(true);
      }
    });
  });
}

for (const layout of LAYOUTS) {
  for (const theme of THEMES) {
    test.describe(`${layout.name} in ${theme.name}`, () => {
      test.use({ viewport: layout.viewport, hasTouch: layout.phone });

      test.beforeEach(async ({ page, browserName }) => {
        test.skip(theme.chromiumOnly && browserName !== 'chromium', 'forced colours are emulated in Chromium only');
        await page.emulateMedia(theme.media);
        await countMotions(page);
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

        // The machine tabs that don't fit, and the More menu open (#1324).
        const more = page.locator('#machine-tabs .machine-more');
        await expect(more).toHaveText(/^\d+ more$/);
        await more.click();
        await expect(page.locator('#machine-tabs .row-menu')).toBeVisible();
        // Scoped to the bar: the open menu covers part of the list below it, as any menu does.
        const bar = await scan(page, { include: '#machine-tabs', rules: ['aria-required-children', 'aria-required-parent', 'aria-allowed-role', 'aria-valid-attr-value', 'aria-roles', 'button-name', 'color-contrast', 'nested-interactive'] });
        expect(bar.failed, 'the machine tabs with More open').toEqual([]);
        expect(bar.passed.length).toBeGreaterThan(0);
        await page.keyboard.press('Escape');
        await expect(page.locator('.row-menu')).toHaveCount(0);

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

        // Details' ID row with its Copy pressed: the button and its status are in the page.
        if (layout.phone) {
          await copilot.locator('.more').click();
          await page.locator('.row-menu').getByRole('menuitem', { name: 'Details' }).click();
        }
        await page.evaluate(() => { document.execCommand = () => true; });
        await copilot.locator('.fact-id').getByRole('button', { name: 'Copy session ID' }).click();
        await expect(copilot.locator('.fact-id [role="status"]')).toHaveText('Copied');
        await check(page, 'Details\' ID row after Copy');

        const land = await open(page, LAND);
        await check(page, 'a session asking for approval');
        await land.getByRole('button', { name: /^Approve once/ }).click();
        // Answered: its outcome in, not still on its way, so nothing new rises in mid-scan.
        await expect(land.locator('.answer-status.progress, .answer-receipt.progress')).toHaveCount(0);
        await expect(land.locator('.answer-receipt, .answer-status').filter({ hasText: /\S/ }).first()).toBeVisible();
        await check(page, 'an approval answered');

        // A delegate opens in the inspector from the strip, or on a phone in the Delegates tab's
        // list's place (ui.md §6.3).
        const delegating = await open(page, DELEGATES);
        if (layout.phone) {
          await delegating.getByRole('tab', { name: 'Delegates' }).click();
          await delegating.locator('.delegates-finished > summary').click();
          await check(page, 'a session\'s delegates, the finished unfolded');
          await delegating.locator('.delegate[data-id="demo-agent-suite"] .delegate-summary').click();
        } else {
          await delegating.locator('.strip-fold').click();
          await delegating.locator('.strip-item[data-id="demo-agent-suite"]').click();
        }
        await expect(delegating.locator('.delegate-pane .delegate-task')).toBeVisible();
        await check(page, layout.phone ? 'a delegate open' : 'a session\'s delegates, one open and the finished unfolded');

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
          // The delegate let go, by its strip item pressed again, gives the inspector back to them.
          await delegating.locator('.strip-item[data-id="demo-agent-suite"]').click();
          await expect(delegating.locator('.delegate-pane')).toBeHidden();
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
        await openDemo(page, '&updates=1&updateNow=1&offline=1&forgetMs=60000');
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

        // Update now's confirmation (#1159), listing the sessions it would go over.
        await page.locator('#machine-list .machine-update-now').click();
        await expect(page.locator('dialog.update-now-sheet')).toBeVisible();
        await check(page, "Update now's confirmation");
        await page.keyboard.press('Escape');
        await expect(page.locator('dialog.update-now-sheet')).toHaveCount(0);

        await go(page, 'usage');
        await check(page, 'usage');

        await go(page, 'settings');
        await check(page, 'settings');

        // The keyboard check on, with its practice targets and a key listed (#1209).
        await page.locator('#key-check-toggle').click();
        await page.locator('.key-check-button').focus();
        await page.keyboard.press('Alt+ArrowUp');
        await expect(page.locator('#key-check-list li').filter({ hasText: 'key "ArrowUp"' })).toHaveCount(1);
        await check(page, 'settings with the keyboard check on');
      });

      test('the sign-in page has no serious or critical violations', async ({ page }) => {
        await trackCsp(page);
        await page.goto('./');
        await expect(page.locator('#signin')).toBeVisible();
        await check(page, 'sign-in');
      });
    });
  }
}
