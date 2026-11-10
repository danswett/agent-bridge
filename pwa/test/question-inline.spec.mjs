// The question card sits where it was asked (#1657, S1): later messages come below it, a bar over the
// reply box says one is waiting while it is out of sight, and answering keeps its place.
import { test, expect } from '@playwright/test';
import { open, view } from './nav.mjs';

const CODEX = '019a1b2c-codex-example';
const QID = 'q-inline';
const long = 'Lorem ipsum dolor sit amet. '.repeat(120);

async function setup(page) {
  await page.setViewportSize({ width: 900, height: 700 });
  await page.goto('?demo=1&static=1&answerDelay=300');
  await page.waitForSelector('html[data-demo-ready]');
  await page.evaluate(({ id, qid, long }) => {
    const d = window.agentBridgeDemo;
    const t = new Date(Date.now() + 1000).toISOString();
    const base = Date.now() - 3600000;
    const at = (n) => new Date(base + n * 1000).toISOString();
    const question = {
      id: qid, kind: 'form', askedAt: at(100), message: 'Which database?',
      fields: [{ name: 'answer', title: 'Database', type: 'choice', required: true, options: [{ value: 'sqlite', label: 'SQLite' }, { value: 'pg', label: 'PostgreSQL' }] }],
    };
    const turn = (k, parts) => ({ id: `${id}-turn-${k}`, at: at(k * 100), prompt: { text: `Prompt ${k}` }, parts });
    const say = (k) => ({ id: `${id}-turn-${k}.1`, kind: 'response', at: at(k * 100 + 1), text: `Reply ${k}. ${long}` });
    d.host.historyTurns.set(id, [
      turn(1, [say(1)]),
      turn(2, [{ id: `${id}-turn-2.0`, kind: 'question', at: at(200), question, outcome: null, answer: null }]),
      turn(3, [say(3)]),
      turn(4, [say(4)]),
      turn(5, [say(5)]),
    ]);
    const s = d.host.get(id);
    d.upsert({
      ...s, status: 'waiting', statusSince: t, updatedAt: t, question,
      activity: { ...(s.activity ?? {}), turnCount: 6, turn: `${id}-turn-6` },
    });
  }, { id: CODEX, qid: QID, long });
  await open(page, CODEX);
  const card = view(page, CODEX);
  await expect(card.locator('.history .history-turn')).not.toHaveCount(0);
  return card;
}

test('an open question sits at its place in the history, with later turns below it', async ({ page }) => {
  const card = await setup(page);
  const q = card.locator(`.question[data-question="${QID}"]`);
  await expect(q).toHaveCount(1);
  const qTop = await q.evaluate((el) => el.getBoundingClientRect().top + el.ownerDocument.defaultView.scrollY);
  const later = await card.locator('.history .history-turn').last().evaluate((el) => el.getBoundingClientRect().top + el.ownerDocument.defaultView.scrollY);
  expect(qTop).toBeLessThan(later);
  await expect(card.locator('.history .question-slot')).toHaveCount(1);
});

test('the Question waiting bar shows while the card is out of sight, and one tap brings it back', async ({ page }) => {
  const card = await setup(page);
  const bar = card.locator('.question-waiting');
  await card.locator('.stream-scroll').evaluate((el) => { el.scrollTop = el.scrollHeight; });
  await expect(bar).toBeVisible();
  await bar.click();
  await expect(card.locator(`.question[data-question="${QID}"]`)).toBeInViewport();
  await expect(bar).toBeHidden();
});

test('answering turns the card into a one-line record in the same place', async ({ page }) => {
  const card = await setup(page);
  const slot = card.locator('.history .question-slot');
  await card.locator(`.question[data-question="${QID}"] .choice[data-value="pg"]`).click();
  await page.evaluate(({ id }) => {
    const d = window.agentBridgeDemo;
    const t = new Date(Date.now() + 2000).toISOString();
    d.upsert({ ...d.host.get(id), status: 'working', statusSince: t, updatedAt: t, question: null });
  }, { id: CODEX });
  await expect(card.locator('.question.resolved')).toHaveCount(1);
  await expect(card.locator('.history .question-slot .question.resolved')).toHaveCount(1);
  await expect(slot).toHaveCount(1);
  await expect(card.locator('.question-waiting')).toBeHidden();
});
