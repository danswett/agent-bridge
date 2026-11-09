// #1193, after the fix: the same states the inventory (docs/design/stale-lines-1193.md, #1200)
// drew, drawn by the product's demo at a phone's width in dark and light.
//   fixed-a2: a stale result that came after a new question was drawn. It was an amber "Your answer:
//     Already answered or gone" that stayed; it is now the question as it was asked, kept in the
//     conversation, with "No longer waiting" in neutral words, and it is still there 7 s later.
//   fixed-formreset: a stale result for a form the host cancelled part answered (code formReset). It
//     is not "already answered": the line says it was not delivered and keeps what was typed to copy.
import { test, expect } from '../../../../pwa/node_modules/@playwright/test/index.mjs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const CODEX = '019a1b2c-codex-example';

test.use({ viewport: { width: 390, height: 844 }, hasTouch: true });

const view = (page, id) => page.locator(`.session-view[data-id="${id}"]`);
const later = () => new Date(Date.now() + 1000).toISOString();

async function openDemo(page) {
  await page.goto('?demo=1&static=1&answerDelay=300');
  await page.waitForSelector('html[data-demo-ready]');
}

async function openSession(page, id) {
  const v = view(page, id);
  if (!(await v.isVisible())) {
    await page.locator(`.session[data-id="${id}"] .row-main`).click();
    await expect(v).toBeVisible();
  }
  await page.waitForFunction(() => !document.getAnimations().some((a) => a.id === 'ab-motion-push' && a.playState === 'running'));
  await expect(v.locator('.question[data-arriving]')).toHaveCount(0);
  return v;
}

async function ask(page, question, id = CODEX) {
  await page.evaluate(({ id, question, t }) => {
    const d = window.agentBridgeDemo;
    d.upsert({ ...d.host.get(id), status: 'waiting', statusSince: t, updatedAt: t, question: { kind: 'form', askedAt: t, ...question } });
  }, { id, question, t: later() });
  const q = view(page, id).locator('.question:not(.resolved)');
  await expect(q).toHaveAttribute('data-question', question.id);
  await openSession(page, id);
  return q;
}

const choice = (id = 'q-db') => ({
  id, message: 'Which **database** should the migration target?',
  fields: [{ name: 'answer', title: 'Database', type: 'choice', required: true, options: [{ value: 'sqlite', label: 'SQLite' }, { value: 'pg', label: 'PostgreSQL (Recommended)' }, { value: 'none', label: 'Neither' }] }],
});

async function snap(page, id, scroll) {
  // Only motions with an end: a ring that turns while something is on its way never finishes.
  await page.evaluate(() => Promise.all(document.getAnimations()
    .filter((a) => Number.isFinite(a.effect?.getComputedTiming?.().endTime))
    .map((a) => a.finished.catch(() => {}))));
  await scroll.scrollIntoViewIfNeeded();
  for (const scheme of ['dark', 'light']) {
    await page.emulateMedia({ colorScheme: scheme });
    await page.waitForTimeout(150);
    await page.screenshot({ path: path.join(HERE, `${id}-${scheme}.png`), animations: 'disabled', caret: 'hide' });
  }
}

test('fixed-a2: stale while sending, the question replaced meanwhile', async ({ page }) => {
  await openDemo(page);
  await page.evaluate(() => window.agentBridgeDemo.script('stale'));
  const q = await ask(page, choice());
  await page.evaluate(() => window.agentBridgeDemo.host.holdResponses());
  await q.locator('.choice[data-value="pg"]').tap();
  await expect(q).toHaveAttribute('data-answer', /^(sending|waiting)$/);
  await ask(page, { id: 'q-next', message: 'Run the tests now?', fields: [{ name: 'ok', type: 'boolean', default: true }] });
  await page.evaluate(() => window.agentBridgeDemo.host.releaseResponses());
  const line = view(page, CODEX).locator('.answer-receipt');
  await expect(line).toHaveText('No longer waiting', { timeout: 8000 });
  await page.waitForTimeout(7000);
  await expect(line).toHaveText('No longer waiting');
  await snap(page, 'fixed-a2', view(page, CODEX).locator('.question-slot'));
});

test('fixed-formreset: a form the host cancelled part answered', async ({ page }) => {
  await openDemo(page);
  const reason = 'this question is out of date: the answer was left part answered at the machine, so the host cancelled the form there, and the session did not ask it again';
  await page.evaluate((r) => window.agentBridgeDemo.script({ outcome: 'stale', code: 'formReset', reason: r }), reason);
  const q = await ask(page, {
    id: 'q-form', message: 'Which **database**, and why?',
    fields: [
      { name: 'db', title: 'Database', type: 'choice', required: true, options: [{ value: 'sqlite', label: 'SQLite' }, { value: 'pg', label: 'PostgreSQL' }] },
      { name: 'why', title: 'Why', type: 'text', required: true },
    ],
  });
  await q.getByRole('radio', { name: 'PostgreSQL' }).check();
  await q.getByRole('textbox', { name: 'Why' }).fill('it has the JSON I need');
  await q.getByRole('button', { name: 'Send answer' }).tap();
  await expect(q.locator('.answer-status')).toHaveText('Not delivered: the form could not take your answer at the machine.');
  // The session stops asking: the words stay, to read and copy.
  await page.evaluate((id) => {
    const d = window.agentBridgeDemo;
    const t = new Date(Date.now() + 1000).toISOString();
    d.upsert({ ...d.host.get(id), status: 'idle', statusSince: t, updatedAt: t, question: null });
  }, CODEX);
  await expect(view(page, CODEX).locator('.answer-kept textarea')).toHaveValue('Database: PostgreSQL\nWhy: it has the JSON I need');
  await snap(page, 'fixed-formreset', view(page, CODEX).locator('.question-slot'));
});
