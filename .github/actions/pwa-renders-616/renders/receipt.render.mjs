import { test, expect } from '@playwright/test';
import { eachLook, shoot } from './shoot.mjs';
import { open, view } from '../nav.mjs';

// The reply and answer receipt (#600) in the states Design signs off (ui.md §6.5 and §6.4 as #617
// decides them: quiet unless something's wrong, and tap to answer), drawn from the demo.

const IDLE = '8c4e1f20-5a7b-4d3c-9e61-0b2f7d9a1c35';
const CODEX = '019a1b2c-codex-example';
const later = () => new Date(Date.now() + 1000).toISOString();
const demo = (page, fn, arg) => page.evaluate(fn, arg);
const hold = (page) => demo(page, () => window.agentBridgeDemo.host.holdResponses());
const script = (page, ...outcomes) => demo(page, (o) => window.agentBridgeDemo.script(...o), outcomes);

async function openDemo(page, id) {
  await page.goto('?demo=1&static=1&answerDelay=200');
  await page.waitForSelector('html[data-demo-ready]');
  await open(page, id);
}

async function ask(page, question) {
  await demo(page, ({ id, question, t }) => {
    const d = window.agentBridgeDemo;
    d.upsert({ ...d.host.get(id), status: 'waiting', statusSince: t, updatedAt: t, question: { kind: 'form', askedAt: t, ...question } });
  }, { id: CODEX, question, t: later() });
  const q = view(page, CODEX).locator('.question');
  await expect(q).toHaveAttribute('data-question', question.id);
  await open(page, CODEX);
  return q;
}

const choice = {
  id: 'q-choice', message: 'Which **database**?',
  fields: [{ name: 'answer', title: 'Database', type: 'choice', required: true, options: [{ value: 'sqlite', label: 'SQLite' }, { value: 'pg', label: 'PostgreSQL (Recommended)' }, { value: 'none', label: 'Neither' }] }],
};
const reply = (page) => view(page, IDLE).locator('form.reply');

// Send's ring, with the line empty in its room: the quiet window, and the normal path after it.
eachLook(test, 'Send\'s ring while a reply is on its way', async ({ page, theme, width }) => {
  await openDemo(page, IDLE);
  await reply(page).getByRole('textbox', { name: 'Reply' }).fill('Ship it once the tests pass.');
  await hold(page);
  await reply(page).getByRole('button', { name: 'Send' }).click();
  await expect(reply(page).getByRole('button', { name: 'Send' })).toHaveAttribute('aria-busy', 'true');
  await shoot(page, 'reply-ring', theme, width);
});

// Delivered: Send's check, held about a second.
eachLook(test, 'Send\'s check once a reply is delivered', async ({ page, theme, width }) => {
  await openDemo(page, IDLE);
  await reply(page).getByRole('textbox', { name: 'Reply' }).fill('Ship it once the tests pass.');
  await reply(page).getByRole('button', { name: 'Send' }).click();
  await expect(reply(page).locator('.button.send.checked')).toHaveCount(1);
  await shoot(page, 'reply-check', theme, width);
});

// A stall: nothing heard for 3 s puts its words on the line.
eachLook(test, 'a reply that stalls says Still sending…', async ({ page, theme, width }) => {
  await openDemo(page, IDLE);
  await reply(page).getByRole('textbox', { name: 'Reply' }).fill('Ship it once the tests pass.');
  await hold(page);
  await reply(page).getByRole('button', { name: 'Send' }).click();
  await expect(reply(page).locator('.reply-status')).toHaveText('Still sending…', { timeout: 8000 });
  await shoot(page, 'reply-stall', theme, width);
});

// Failed: the host failed the request (here, a file it couldn't take); Try again resends it.
eachLook(test, 'a failed reply offers Try again', async ({ page, theme, width }) => {
  await openDemo(page, IDLE);
  await reply(page).locator('input.file-picker').setInputFiles([{ name: 'plan.md', mimeType: 'text/markdown', buffer: Buffer.alloc(40, 'a') }]);
  await demo(page, () => { window.agentBridgeDemo.host.failNextChunk = 'the host could not take it'; });
  await reply(page).getByRole('button', { name: 'Send' }).click();
  await expect(reply(page).getByRole('button', { name: 'Try again' })).toBeVisible();
  await shoot(page, 'reply-failed', theme, width);
});

// Tap to answer: the ring on the tapped option, the others disabled.
eachLook(test, 'a tapped option on its way', async ({ page, theme, width }) => {
  await openDemo(page, CODEX);
  const q = await ask(page, choice);
  await hold(page);
  await q.locator('.choice[data-value="pg"]').click();
  await expect(q.locator('.choice[data-value="sqlite"]')).toBeDisabled();
  await shoot(page, 'tap-ring', theme, width);
});

// One attempt per prompt: the tapped option stays pressed, the rest disabled, and the line says why.
eachLook(test, 'a tap answer that comes back stale', async ({ page, theme, width }) => {
  await openDemo(page, CODEX);
  const q = await ask(page, choice);
  await script(page, { outcome: 'stale' });
  await q.locator('.choice[data-value="pg"]').click();
  await expect(q).toHaveAttribute('data-answer', 'stale');
  await expect(q.locator('.choice[data-value="pg"]')).toHaveAttribute('aria-pressed', 'true');
  await expect(q.locator('.choice[data-value="sqlite"]')).toBeDisabled();
  await shoot(page, 'tap-stale', theme, width);
});

// Unconfirmed: the host took the question away as it typed, but the question stays as it went,
// the option pressed and the rest disabled, saying to check at the machine.
eachLook(test, 'a tap answer that comes back unconfirmed', async ({ page, theme, width }) => {
  await openDemo(page, CODEX);
  const q = await ask(page, choice);
  await script(page, { outcome: 'unconfirmed' });
  await q.locator('.choice[data-value="pg"]').click();
  await expect(q).toHaveAttribute('data-answer', 'unconfirmed');
  await expect(q.locator('.choice[data-value="pg"]')).toHaveAttribute('aria-pressed', 'true');
  await shoot(page, 'tap-unconfirmed', theme, width);
});

// Delivered: the check on the option that went, for about a second, before You answered.
eachLook(test, 'a tap answer\'s check', async ({ page, theme, width }) => {
  await openDemo(page, CODEX);
  const q = await ask(page, choice);
  await q.locator('.choice[data-value="pg"]').click();
  await expect(q.locator('.choice[data-value="pg"].checked')).toHaveCount(1);
  await shoot(page, 'tap-check', theme, width);
});

for (const [state, outcome] of [['tap-busy', 'busy'], ['tap-refused', 'refused']]) {
  // Nothing was typed: the options are enabled again, none pressed (busy after its pause).
  eachLook(test, `a tap answer that comes back ${outcome}`, async ({ page, theme, width }) => {
    await openDemo(page, CODEX);
    const q = await ask(page, choice);
    await script(page, { outcome, reason: outcome === 'refused' ? 'the session is running elevated' : undefined });
    await q.locator('.choice[data-value="pg"]').click();
    await expect(q).toHaveAttribute('data-answer', outcome);
    await expect(q.locator('.choice[data-value="pg"]')).toBeEnabled({ timeout: 5000 });
    await shoot(page, state, theme, width);
  });
}

// Confirmed: the card resolves to You answered · value, in text2 with no badge.
eachLook(test, 'a tap answer confirmed', async ({ page, theme, width }) => {
  await openDemo(page, CODEX);
  const q = await ask(page, choice);
  await q.locator('.choice[data-value="pg"]').click();
  await expect(view(page, CODEX).locator('.answer-receipt')).toHaveText('You answered · PostgreSQL (Recommended)');
  await shoot(page, 'answered-tap', theme, width);
});

eachLook(test, 'a free-text answer confirmed', async ({ page, theme, width }) => {
  await openDemo(page, CODEX);
  const q = await ask(page, { id: 'q-plain', message: 'What should the release notes lead with?', fields: [] });
  await q.getByRole('textbox', { name: 'Your answer' }).fill('Lead with the faster reconnects, then the new receipt on every reply\nand the rest after.');
  await q.getByRole('button', { name: 'Send answer' }).click();
  await expect(view(page, CODEX).locator('.answer-receipt')).toHaveText(/^You answered · /);
  await shoot(page, 'answered-text', theme, width);
});

eachLook(test, 'a form answer confirmed', async ({ page, theme, width }) => {
  await openDemo(page, CODEX);
  const q = await ask(page, { id: 'q-form', message: 'Release details?', fields: [{ name: 'n', title: 'Notes', type: 'text' }, { name: 'b', title: 'Ship now', type: 'boolean', default: true }] });
  await q.getByRole('textbox').first().fill('Faster reconnects');
  await q.getByRole('button', { name: 'Send answer' }).click();
  await expect(view(page, CODEX).locator('.answer-receipt')).toHaveText('You answered');
  await shoot(page, 'answered-form', theme, width);
});