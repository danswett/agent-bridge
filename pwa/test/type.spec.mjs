// Inline mono beside the system face (docs/design/ui.md §4.2, #215): a branch in a session's
// meta line and a tool's name and arguments sit as if they were the text face, at a phone's
// width, in both engines. Measured on the page, not read back from the stylesheet.
import { test, expect } from '@playwright/test';

const CLAUDE = '6f1c2b7e-claude-example';

test.use({ viewport: { width: 390, height: 844 } });

async function openDemo(page) {
  await page.goto('?demo=1&static=1');
  await page.waitForSelector('html[data-demo-ready]');
}

// The x-height inline mono is drawn at, as a fraction of the size of the text around it: a
// 1ex-wide probe in it (`ex` is the used font's own x-height, after any font-size-adjust),
// over the surrounding font size; and a second probe in the text itself.
async function monoX(page, selector) {
  return page.locator(selector).first().evaluate((el) => {
    const p = document.createElement('span');
    p.style.display = 'inline-block';
    p.style.width = '1ex';
    el.append(p);
    const ex = p.getBoundingClientRect().width;
    p.remove();
    const target = Number(getComputedStyle(document.documentElement).getPropertyValue('--ab-inline-mono-x'));
    // And the text face's own x-height beside it, whatever system-ui is on this machine.
    const q = document.createElement('span');
    q.style.display = 'inline-block';
    q.style.width = '1ex';
    el.parentElement.append(q);
    const text = q.getBoundingClientRect().width;
    q.remove();
    return { ratio: ex / parseFloat(getComputedStyle(el.parentElement).fontSize), target, ex, text };
  });
}

test('a branch in the meta line and a tool\'s name and arguments have the text face\'s x-height, not a smaller or larger one', async ({ page }) => {
  await openDemo(page);
  await expect(page.locator(`.session[data-id="${CLAUDE}"] .tool-line .tool-name`)).toHaveText('Bash');
  for (const selector of ['.meta .branch', `.session[data-id="${CLAUDE}"] .tool-line .tool-name`, `.session[data-id="${CLAUDE}"] .tool-line .tool-summary`]) {
    const { ratio, target, ex, text } = await monoX(page, selector);
    expect(target, 'tokens.css names a ratio').toBeGreaterThan(0.4);
    // The platform face's ratio, closely: what the design commits to on every machine.
    expect(Math.abs(ratio - target), `${selector}: x-height ${ratio.toFixed(4)} of the text size, not ${target}`).toBeLessThan(0.01);
    // And the rendered text beside it, within a pixel (#215), even where the test machine's
    // system-ui isn't the platform face the ratio is for (CI's Linux runner).
    expect(Math.abs(ex - text), `${selector}: mono x-height ${ex} px beside text's ${text} px`).toBeLessThanOrEqual(1);
  }
});

test('inline mono is set in Agent Bridge Mono at the weight of the text around it, with no spacing of its own', async ({ page }) => {
  await openDemo(page);
  const tool = page.locator(`.session[data-id="${CLAUDE}"] .tool-line .tool-name`);
  await expect(tool).toHaveText('Bash');
  for (const selector of ['.meta .branch', `.session[data-id="${CLAUDE}"] .tool-line .tool-name`]) {
    const style = await page.locator(selector).first().evaluate((el) => {
      const own = getComputedStyle(el);
      const around = getComputedStyle(el.parentElement);
      return { family: own.fontFamily, weight: own.fontWeight, around: around.fontWeight, spacing: own.letterSpacing, padding: own.paddingLeft };
    });
    expect(style.family, selector).toMatch(/^"Agent Bridge Mono"/);
    expect(style.weight, selector).toBe(style.around);
    expect(style.spacing, selector).toBe('normal');
    expect(style.padding, selector).toBe('0px');
  }
});

test('the separator before a branch is in the text face, not the branch\'s mono', async ({ page }) => {
  await openDemo(page);
  const branch = page.locator('.meta .branch').first();
  await expect(branch).toBeVisible();
  const [separator, text] = await branch.evaluate((el) => [getComputedStyle(el, '::before'), getComputedStyle(el.parentElement)]
    .map((s) => ({ family: s.fontFamily, content: s.content })));
  expect(separator.content).toBe('"·"');
  expect(separator.family).toBe(text.family);
  expect(separator.family).not.toMatch(/Agent Bridge Mono/);
});