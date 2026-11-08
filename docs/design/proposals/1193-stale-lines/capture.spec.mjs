// #1193: the lines the PWA keeps after their cause is gone, drawn by the product's demo at a phone's
// width in dark and light. Inventory only: nothing here changes behaviour. Each test brings one state
// about the way a person would, waits for the line to settle, writes what the page says to
// observed.json (the words, and whether the cause is still true) and draws it as
// stale-<id>-dark.png and stale-<id>-light.png beside this file.
import { test, expect } from '../../../../pwa/node_modules/@playwright/test/index.mjs';
import { mkdirSync, readFileSync, writeFileSync, existsSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const OBSERVED = path.join(HERE, 'observed.json');
const CODEX = '019a1b2c-codex-example';
const CLAUDE = '6f1c2b7e-claude-example';
const IDLE = '8c4e1f20-5a7b-4d3c-9e61-0b2f7d9a1c35';

test.use({ viewport: { width: 390, height: 844 }, hasTouch: true });

const view = (page, id) => page.locator(`.session-view[data-id="${id}"]`);
const later = () => new Date(Date.now() + 1000).toISOString();

async function openDemo(page, query = '') {
  await page.goto(`?demo=1&static=1&answerDelay=300${query}`);
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
  const q = view(page, id).locator('.question');
  await expect(q).toHaveAttribute('data-question', question.id);
  await openSession(page, id);
  return q;
}

// The session moves on with the question gone: cancelled at the machine, answered elsewhere, or
// withdrawn by the agent. The host cannot tell which (#1193, Connection).
const withdraw = (page, id = CODEX) => page.evaluate((id) => {
  const d = window.agentBridgeDemo;
  const t = new Date(Date.now() + 1000).toISOString();
  d.upsert({ ...d.host.get(id), status: 'idle', statusSince: t, updatedAt: t, question: null });
}, id);

const answers = (page, ...o) => page.evaluate((o) => window.agentBridgeDemo.script(...o), o);
const choice = (id = 'q-db') => ({
  id, message: 'Which **database** should the migration target?',
  fields: [{ name: 'answer', title: 'Database', type: 'choice', required: true, options: [{ value: 'sqlite', label: 'SQLite' }, { value: 'pg', label: 'PostgreSQL (Recommended)' }, { value: 'none', label: 'Neither' }] }],
});

function note(id, facts) {
  const all = existsSync(OBSERVED) ? JSON.parse(readFileSync(OBSERVED, 'utf8')) : {};
  all[id] = facts;
  writeFileSync(OBSERVED, `${JSON.stringify(Object.fromEntries(Object.entries(all).sort(([a], [b]) => a.localeCompare(b))), null, 2)}\n`);
}

// What a line says, and how long after the cause went it is still there.
async function snap(page, id, facts, { scroll = null } = {}) {
  // Only motions with an end: a ring that turns while something is on its way never finishes.
  await page.evaluate(() => Promise.all(document.getAnimations()
    .filter((a) => Number.isFinite(a.effect?.getComputedTiming?.().endTime))
    .map((a) => a.finished.catch(() => {}))));
  if (scroll) { await scroll.scrollIntoViewIfNeeded(); }
  for (const scheme of ['dark', 'light']) {
    await page.emulateMedia({ colorScheme: scheme });
    await page.waitForTimeout(150);
    mkdirSync(HERE, { recursive: true });
    await page.screenshot({ path: path.join(HERE, `stale-${id}-${scheme}.png`), animations: 'disabled', caret: 'hide' });
  }
  note(id, facts);
}

const text = (loc) => loc.evaluate((el) => (el.textContent ?? '').replace(/\s+/g, ' ').trim());

test.describe('answer, in the conversation', () => {
  test('a1: stale, the question then withdrawn', async ({ page }) => {
    await openDemo(page);
    await answers(page, 'stale');
    const q = await ask(page, choice());
    await q.locator('.choice[data-value="pg"]').tap();
    await expect(q.locator('.answer-status')).toContainText('Already answered or gone');
    await withdraw(page);
    await expect(view(page, CODEX)).toHaveAttribute('data-status', 'idle');
    await page.waitForTimeout(7000);
    const slot = view(page, CODEX).locator('.question-slot');
    await snap(page, 'a1-answer-stale', {
      after: 'question withdrawn, 7 s, no new turn',
      line: await text(slot),
      questionStillAsked: await view(page, CODEX).locator('.question[data-question]').count() > 0,
    }, { scroll: slot });
  });

  test('a2: stale while sending, the question replaced meanwhile', async ({ page }) => {
    await openDemo(page);
    await answers(page, 'stale');
    const q = await ask(page, choice());
    await page.evaluate(() => window.agentBridgeDemo.host.holdResponses());
    await q.locator('.choice[data-value="pg"]').tap();
    await expect(q).toHaveAttribute('data-answer', /^(sending|waiting)$/);
    await ask(page, { id: 'q-next', message: 'Run the tests now?', fields: [{ name: 'ok', type: 'boolean', default: true }] });
    await page.evaluate(() => window.agentBridgeDemo.host.releaseResponses());
    await expect(view(page, CODEX).locator('.answer-receipt')).toContainText('Already answered or gone', { timeout: 8000 });
    await page.waitForTimeout(7000);
    const slot = view(page, CODEX).locator('.question-slot');
    await snap(page, 'a2-answer-stale-receipt', {
      after: 'result came after a new question was drawn, 7 s',
      line: await text(slot),
      receiptCount: await view(page, CODEX).locator('.answer-receipt').count(),
    }, { scroll: slot });
  });

  test('a3: refused, the question then withdrawn', async ({ page }) => {
    await openDemo(page);
    await answers(page, { outcome: 'refused', reason: 'the session is running elevated' });
    const q = await ask(page, choice());
    await q.locator('.choice[data-value="pg"]').tap();
    await expect(q.locator('.answer-status')).toContainText('The machine refused this answer');
    await withdraw(page);
    await expect(view(page, CODEX).locator('.answer-receipt')).toContainText('refused', { timeout: 5000 });
    await page.waitForTimeout(7000);
    const slot = view(page, CODEX).locator('.question-slot');
    await snap(page, 'a3-answer-refused-gone', { after: 'question withdrawn, 7 s', line: await text(slot) }, { scroll: slot });
  });

  test('a4: busy, the question then withdrawn', async ({ page }) => {
    await openDemo(page);
    await answers(page, 'busy');
    const q = await ask(page, choice());
    await q.locator('.choice[data-value="pg"]').tap();
    await expect(q.locator('.answer-status')).toContainText('Try again in a moment');
    await withdraw(page);
    await expect(view(page, CODEX).locator('.answer-receipt')).toContainText('Try again in a moment', { timeout: 5000 });
    await page.waitForTimeout(7000);
    const slot = view(page, CODEX).locator('.question-slot');
    await snap(page, 'a4-answer-busy-gone', { after: 'question withdrawn, 7 s', line: await text(slot) }, { scroll: slot });
  });

  test('a5: unconfirmed', async ({ page }) => {
    await openDemo(page);
    await answers(page, 'unconfirmed');
    const q = await ask(page, choice());
    await q.locator('.choice[data-value="pg"]').tap();
    await expect(q.locator('.answer-status')).toContainText('Sent, but not confirmed');
    await page.waitForTimeout(7000);
    const slot = view(page, CODEX).locator('.question-slot');
    await snap(page, 'a5-answer-unconfirmed', { after: 'host took the question away, 7 s', line: await text(slot) }, { scroll: slot });
  });

  test('a6: the machine failed the op, the question then withdrawn', async ({ page }) => {
    await openDemo(page);
    await answers(page, { error: 'the host could not reach the session' });
    const q = await ask(page, choice());
    await q.locator('.choice[data-value="pg"]').tap();
    await expect(q.locator('.answer-status')).toContainText("Couldn't send it");
    await withdraw(page);
    await page.waitForTimeout(7000);
    const slot = view(page, CODEX).locator('.question-slot');
    await snap(page, 'a6-answer-failed-gone', {
      after: 'question withdrawn, 7 s',
      line: await text(slot),
      tryAgainOffered: await slot.getByRole('button', { name: 'Try again' }).count() > 0,
    }, { scroll: slot });
  });

  test('a7: the question changes under half an answer', async ({ page }) => {
    await openDemo(page);
    const q = await ask(page, { id: 'q-notes', message: 'Notes for the release?', fields: [{ name: 'n', title: 'Notes', type: 'text' }] });
    await q.getByRole('textbox', { name: 'Notes' }).fill('half a thought');
    await withdraw(page);
    await expect(view(page, CODEX).locator('.question-changed')).toBeVisible();
    await ask(page, { id: 'q-two', message: 'A different question?', fields: [{ name: 'ok', type: 'boolean', default: true }] });
    await page.waitForTimeout(7000);
    const slot = view(page, CODEX).locator('.question-slot');
    await snap(page, 'a7-question-changed', { after: 'a new question has been drawn, 7 s', line: await text(slot) }, { scroll: slot });
  });

  test('a8: delivered', async ({ page }) => {
    await openDemo(page);
    const q = await ask(page, choice());
    await q.locator('.choice[data-value="pg"]').tap();
    await expect(view(page, CODEX).locator('.answer-receipt')).toContainText('You answered');
    await page.waitForTimeout(7000);
    const slot = view(page, CODEX).locator('.question-slot');
    await snap(page, 'a8-answer-delivered', { after: 'delivered, 7 s', line: await text(slot) }, { scroll: slot });
  });
});

test.describe('reply', () => {
  test('r1: busy, after its pause', async ({ page }) => {
    await openDemo(page);
    await openSession(page, IDLE);
    await page.evaluate(() => window.agentBridgeDemo.scriptReplies('busy'));
    const reply = view(page, IDLE).locator('form.reply');
    await reply.getByRole('textbox', { name: 'Reply' }).fill('next step');
    await reply.getByRole('button', { name: 'Send' }).tap();
    await expect(reply.locator('.reply-status')).toContainText('Try again in a moment');
    await expect(reply.getByRole('button', { name: 'Send' })).toBeEnabled({ timeout: 4000 });
    await page.waitForTimeout(6000);
    await snap(page, 'r1-reply-busy', {
      after: 'the pause is over and Send is back, 6 s later',
      line: await text(reply.locator('.reply-status')),
      sendEnabled: await reply.getByRole('button', { name: 'Send' }).isEnabled(),
    });
  });

  test('r2: not sent, the session has ended', async ({ page }) => {
    await openDemo(page);
    await openSession(page, IDLE);
    await page.evaluate(() => window.agentBridgeDemo.scriptReplies('stale'));
    const reply = view(page, IDLE).locator('form.reply');
    await reply.getByRole('textbox', { name: 'Reply' }).fill('next step');
    await reply.getByRole('button', { name: 'Send' }).tap();
    await expect(reply.locator('.reply-status')).toContainText('the session has ended');
    await page.waitForTimeout(6000);
    await snap(page, 'r2-reply-stale', { after: '6 s', line: await text(reply.locator('.reply-status')) });
  });

  test('r3: unconfirmed', async ({ page }) => {
    await openDemo(page);
    await openSession(page, IDLE);
    await page.evaluate(() => window.agentBridgeDemo.scriptReplies('unconfirmed'));
    const reply = view(page, IDLE).locator('form.reply');
    await reply.getByRole('textbox', { name: 'Reply' }).fill('next step');
    await reply.getByRole('button', { name: 'Send' }).tap();
    await expect(reply.locator('.reply-status')).toContainText('not confirmed');
    await page.waitForTimeout(6000);
    await snap(page, 'r3-reply-unconfirmed', { after: '6 s', line: await text(reply.locator('.reply-status')) });
  });
});

test.describe('end session', () => {
  async function showEnd(page, id) {
    const v = await openSession(page, id);
    const end = v.locator('.end-session').getByRole('button', { name: 'End session' });
    if (!(await end.isVisible())) {
      await v.locator('.session-bar .more').click();
      await v.getByRole('menuitem', { name: 'Details' }).click();
    }
    await expect(end).toBeVisible();
    return { v, end, status: v.locator('.end-session .stop-status') };
  }

  test('s1: busy, after its pause', async ({ page }) => {
    await openDemo(page);
    await page.evaluate(() => window.agentBridgeDemo.scriptStops('busy'));
    const { end, status } = await showEnd(page, CLAUDE);
    await end.tap();
    await expect(status).toContainText('Try again in a moment');
    await page.waitForTimeout(6000);
    await snap(page, 's1-stop-busy', { after: 'the pause is over, 6 s', line: await text(status), buttonPressable: (await end.getAttribute('aria-disabled')) !== 'true' });
  });

  test('s2: the second press never came', async ({ page }) => {
    await openDemo(page, '&stopWindow=1500');
    const { end, status } = await showEnd(page, CLAUDE);
    await end.tap();
    await expect(status).toContainText('End session NOT confirmed', { timeout: 6000 });
    await page.waitForTimeout(6000);
    await snap(page, 's2-stop-lapsed', { after: 'window lapsed, 6 s', line: await text(status) });
  });

  test('s3: the session had already ended', async ({ page }) => {
    await openDemo(page);
    await page.evaluate(() => window.agentBridgeDemo.scriptStops('stale'));
    const { end, status } = await showEnd(page, CLAUDE);
    await end.tap();
    await expect(status).toContainText('had already ended');
    await page.waitForTimeout(6000);
    await snap(page, 's3-stop-stale', { after: '6 s', line: await text(status), cardStillShown: await view(page, CLAUDE).count() > 0 });
  });
});

test.describe('launch', () => {
  async function openLaunch(page) {
    await page.locator('.new-session').click();
    await expect(page.locator('#launch .launch-title')).toBeVisible();
    await expect(page.locator('#launch [name="agent"]')).toBeVisible();
  }
  const launchStatus = (page) => page.locator('#launch .launch-status');

  async function launchWith(page, outcome, expected) {
    await openDemo(page);
    await page.evaluate((o) => window.agentBridgeDemo.scriptLaunches(o), outcome);
    await openLaunch(page);
    await page.locator('#launch').getByRole('button', { name: 'Launch' }).tap();
    await expect(launchStatus(page)).toContainText(expected);
    await page.waitForTimeout(6000);
  }

  test('l1: busy', async ({ page }) => {
    await launchWith(page, 'busy', 'Try again in a moment');
    await snap(page, 'l1-launch-busy', { after: '6 s, settings untouched', line: await text(launchStatus(page)) }, { scroll: launchStatus(page) });
  });

  test('l2: refused', async ({ page }) => {
    await launchWith(page, 'refused', 'Not launched');
    await snap(page, 'l2-launch-refused', { after: '6 s', line: await text(launchStatus(page)) }, { scroll: launchStatus(page) });
  });

  test('l3: unconfirmed', async ({ page }) => {
    await launchWith(page, 'unconfirmed', 'not confirmed');
    await snap(page, 'l3-launch-unconfirmed', { after: '6 s', line: await text(launchStatus(page)) }, { scroll: launchStatus(page) });
  });

  test('l4: a folder-trust answer to a question the machine no longer lists', async ({ page }) => {
    const QUESTION = {
      id: '4d1c7e2a-9b3f-4a60-8e15-2c7d6b0f9a31', label: 'Codex', folder: 'C:\\Users\\demo\\repos\\agent-bridge', warning: 'Trust this folder? Codex can read, edit, and run files here.', answerable: true,
    };
    await openDemo(page);
    await page.evaluate((q) => window.agentBridgeDemo.askTrust(q), QUESTION);
    await page.evaluate(() => window.agentBridgeDemo.scriptTrust({ outcome: 'refused', reason: 'that question is no longer waiting: it was answered, or the agent closed' }));
    await openLaunch(page);
    await page.evaluate(() => { const h = window.agentBridgeDemo.host; h.launchOptions = { ...h.launchOptions, trust: [] }; });
    await page.locator('#launch .trust-question').getByRole('button', { name: 'Trust' }).tap();
    await expect(page.locator('#launch .trust-status')).toContainText('Not answered');
    await page.waitForTimeout(6000);
    const card = page.locator('#launch .trust-question');
    await snap(page, 'l4-trust-gone', { after: '6 s', line: await text(card) }, { scroll: card });
  });
});
