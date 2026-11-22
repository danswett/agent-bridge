// The conversation before the latest exchange (#1285 S2a): a session with turns shows the earlier
// ones read-only above its latest exchange, and opens at the latest.
import { test, expect } from '@playwright/test';
import { open, view } from './nav.mjs';

test('earlier turns are drawn above the latest exchange, which is drawn once', async ({ page }) => {
  await page.goto('?demo=1&static=1&signin=1&name=DEV-VM1#/sessions');
  await page.waitForSelector('html[data-demo-ready]');
  await open(page, 'signin-1');
  const v = view(page, 'signin-1');
  // signin-1 has three turns: two earlier, and the latest the card draws live.
  await expect(v.locator('.history-turn')).toHaveCount(2);
  await expect(v.locator('.history-turn').first()).toContainText('Demo prompt 1');
  await expect(v.getByText('Demo prompt 3')).toHaveCount(0);
  await expect(v.locator('.history-turn textarea')).toHaveCount(0);
  await expect(v.locator('.history-turn button:not(.msg-time)')).toHaveCount(0);
  const scroll = v.locator('.stream-scroll');
  await expect.poll(() => scroll.evaluate((el) => el.scrollHeight - el.scrollTop - el.clientHeight)).toBeLessThan(4);
});

test('an earlier prompt an agent sent is collapsed with its title and a preview, and opens on Show prompt', async ({ page }) => {
  await page.goto('?demo=1&static=1&signin=1&name=DEV-VM1#/sessions');
  await page.waitForSelector('html[data-demo-ready]');
  await open(page, 'error-quota');
  const turn = view(page, 'error-quota').locator('.history-turn').first();
  const prompt = turn.locator('.prompt-origin');
  await expect(prompt.locator('.prompt-facts')).toHaveText('Sent by an agent');
  await expect(prompt.locator('.prompt-preview')).toHaveText('Demo prompt 1');
  await expect(prompt.locator('.prompt-full')).toBeHidden();
  await expect(turn.locator('.history-origin-label')).toHaveCount(0);
  const toggle = prompt.getByRole('button', { name: 'Prompt sent by an agent, Show prompt' });
  await expect(toggle).toHaveAttribute('aria-expanded', 'false');
  await toggle.click();
  await expect(prompt.getByRole('button', { name: 'Prompt sent by an agent, Hide prompt' })).toHaveAttribute('aria-expanded', 'true');
  await expect(prompt.locator('.prompt-full')).toHaveText('Demo prompt 1');
  await expect(prompt.locator('.prompt-preview')).toBeHidden();
});

test('an earlier relay message from an agent, stored as the person\'s, is the collapsed agent line without its envelope, and the person\'s own prompt stays a bubble', async ({ page }) => {
  await page.goto('/?demo=1&static=1');
  await page.waitForSelector('html[data-demo-ready]');
  const envelope = '[Slipgates \u00b7 from an agent on BUILD-HOST-A \u00b7 session s-9 \u00b7 request r-1 \u00b7 not typed by your user] ';
  const report = Array.from({ length: 40 }, (_, i) => `Line ${i + 1} of the report.`).join('\n');
  const drawn = await page.evaluate(async ({ envelope, report }) => {
    const { turnView } = await import('/js/ui/history-view.js');
    const out = {};
    for (const [name, prompt] of [['absent', { text: envelope + report }], ['person', { text: envelope + report, from: 'person' }], ['own', { text: 'Please rebase.', from: 'person' }]]) {
      const el = turnView({ id: name, at: null, prompt, parts: [] });
      out[name] = { bubble: !!el.querySelector('.history-bubble'), facts: el.querySelector('.prompt-facts')?.textContent ?? null, full: el.querySelector('.prompt-full')?.textContent ?? null, text: el.textContent };
    }
    return out;
  }, { envelope, report });
  for (const name of ['absent', 'person']) {
    expect(drawn[name].bubble).toBe(false);
    expect(drawn[name].facts).toBe('From: s-9 on BUILD-HOST-A');
    expect(drawn[name].full).toBe(report);
    expect(drawn[name].text).not.toContain('not typed by your user');
  }
  expect(drawn.own.bubble).toBe(true);
  expect(drawn.own.facts).toBeNull();
});