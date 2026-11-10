// A closed question is one line in the conversation (#1281): its words, and a button that opens
// the whole record in place. Focus stays on the button, and moves to the line when the card collapses.
import { test, expect } from '@playwright/test';
import { open, view } from './nav.mjs';

const CODEX = '019a1b2c-codex-example';
const card = (page, id) => view(page, id);
const later = () => new Date(Date.now() + 1000).toISOString();
const withdraw = (page) => page.evaluate(({ id, t }) => {
  const d = window.agentBridgeDemo;
  d.upsert({ ...d.host.get(id), status: 'working', statusSince: t, updatedAt: t, question: null });
}, { id: CODEX, t: later() });

async function ask(page, question) {
  await page.goto('?demo=1&static=1&answerDelay=300');
  await page.waitForSelector('html[data-demo-ready]');
  await page.evaluate(({ id, question, t }) => {
    const d = window.agentBridgeDemo;
    d.upsert({ ...d.host.get(id), status: 'waiting', statusSince: t, updatedAt: t, question: { kind: 'form', askedAt: t, ...question } });
  }, { id: CODEX, question, t: later() });
  const q = card(page, CODEX).locator('.question:not(.resolved)');
  await expect(q).toHaveAttribute('data-question', question.id);
  await open(page, CODEX);
  return q;
}

test('a closed question is one line, opens in place on its button, and focus follows', async ({ page }) => {
  const q = await ask(page, {
    id: 'q-db', message: 'Which **database**?',
    fields: [{ name: 'answer', title: 'Database', type: 'choice', required: true, options: [{ value: 'sqlite', label: 'SQLite' }, { value: 'pg', label: 'PostgreSQL' }] }],
  });
  await page.evaluate(() => window.agentBridgeDemo.script('stale'));
  await q.locator('.choice[data-value="pg"]').click();
  await withdraw(page);
  const resolved = card(page, CODEX).locator('.question.resolved');
  await expect(resolved).toHaveCount(1);
  const line = resolved.locator('.answer-receipt');
  await expect(line).toHaveText('Question closed');
  await expect(resolved.locator('input, .choice')).toHaveCount(0);
  const record = resolved.locator('.resolved-record');
  await expect(record).toBeHidden();

  const toggle = resolved.getByRole('button', { name: 'Show question' });
  await expect(toggle).toHaveAttribute('aria-expanded', 'false');
  await toggle.focus();
  await page.keyboard.press('Enter');
  const hide = resolved.getByRole('button', { name: 'Hide question' });
  await expect(hide).toHaveAttribute('aria-expanded', 'true');
  await expect(hide).toBeFocused();
  await expect(record).toBeVisible();
  await expect(record).toContainText('database');
  await expect(record.locator('.opt')).toHaveCount(2);

  await hide.focus();
  await page.keyboard.press('Enter');
  await expect(record).toBeHidden();
  await expect(resolved.getByRole('button', { name: 'Show question' })).toBeFocused();
});

test('a closed approval says only that it closed, never the agent\'s words', async ({ page }) => {
  const q = await ask(page, {
    id: 'q-approval', kind: 'approval', message: 'Allow **Bash**?\n\n```\nrm -rf build\n```',
    fields: [{ name: 'decision', type: 'choice', required: true, options: [{ value: 'yes', label: 'Yes' }, { value: 'no', label: 'No' }] }],
  });
  await page.evaluate(() => window.agentBridgeDemo.script('stale'));
  await q.getByRole('button', { name: 'No' }).click();
  await withdraw(page);
  const resolved = card(page, CODEX).locator('.question.resolved');
  await expect(resolved.locator('.answer-receipt')).toHaveText('Question closed');
  await expect(resolved.locator('.resolved-summary')).not.toContainText('rm -rf');
});
