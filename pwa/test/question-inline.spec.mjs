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
  await expect(card.locator('.resolved-summary')).toContainText('You answered');
  await page.evaluate(() => window.agentBridgeDemo.setOnline('demo', false));
  await expect(card.locator('.resolved-summary')).toContainText('You answered');
});

test('a question answered elsewhere without a local attempt stays where it was asked through later questions', async ({ page }) => {
  const card = await setup(page);
  await page.evaluate(({ id }) => {
    const d = window.agentBridgeDemo;
    const s = d.host.get(id);
    const t = new Date(Date.now() + 2000).toISOString();
    d.upsert({ ...s, status: 'working', updatedAt: t, question: null });
  }, { id: CODEX });
  const closed = card.locator(`.question.resolved[data-question="${QID}"]`);
  await expect(closed).toHaveCount(1);
  await expect(closed).toContainText('Question closed');
  await expect(card.locator('.reply textarea')).toBeEnabled();
  await page.evaluate(({ id }) => {
    const d = window.agentBridgeDemo;
    const s = d.host.get(id);
    const question = { ...d.host.historyTurns.get(id)[1].parts[0].question, id: 'next-question' };
    d.upsert({ ...s, status: 'waiting', updatedAt: new Date(Date.now() + 3000).toISOString(), question });
  }, { id: CODEX });
  await expect(card.locator('.history .history-turn').nth(1).locator('.question.resolved')).toHaveCount(1);
  await expect(card.locator('.question:not(.resolved)')).toHaveAttribute('data-question', 'next-question');
  await expect(closed.locator('input, .choice')).toHaveCount(0);
});

test('live reply updates keep focus in an unchanged latest-turn question', async ({ page }) => {
  const card = await setup(page);
  await page.evaluate(async ({ id }) => {
    const d = window.agentBridgeDemo;
    const turns = d.host.historyTurns.get(id);
    turns.splice(2);
    const question = { ...turns[1].parts[0].question, fields: [{ name: 'answer', type: 'text', title: 'Answer' }] };
    turns[1].parts[0].question = question;
    d.upsert({ ...d.host.get(id), question, activity: { ...d.host.get(id).activity, turn: `${id}-turn-2` } });
    await d.view.history.open('demo', id, { session: d.host.get(id) });
  }, { id: CODEX });
  const input = card.locator('.question:not(.resolved) textarea');
  await input.fill('My draft');
  await input.focus();
  await page.evaluate(({ id }) => {
    const d = window.agentBridgeDemo;
    const s = d.host.get(id);
    d.upsert({ ...s, updatedAt: new Date(Date.now() + 5000).toISOString(), activity: { ...s.activity, responseTurn: s.activity.turn, response: 'A new live reply' } });
  }, { id: CODEX });
  await expect(input).toBeFocused();
  await expect(input).toHaveValue('My draft');
});

test('a delayed answer receipt stays once in its own turn when another question arrives', async ({ page }) => {
  const card = await setup(page);
  await page.evaluate(() => window.agentBridgeDemo.host.holdBefore('result'));
  await card.locator(`[data-question="${QID}"] .choice[data-value="pg"]`).click();
  await page.waitForFunction((qid) => window.agentBridgeDemo.host.typed.some((t) => t.question === qid), QID);
  await page.evaluate(async ({ id }) => {
    const d = window.agentBridgeDemo;
    const s = d.host.get(id);
    const at = new Date(Date.now() + 3000).toISOString();
    const question = { ...d.host.historyTurns.get(id)[1].parts[0].question, id: 'next-question' };
    d.host.historyTurns.get(id).push({ id: 'next-turn', at, prompt: { text: 'Next prompt' }, parts: [{ id: 'next-part', kind: 'question', at, question }] });
    d.upsert({ ...s, question, status: 'waiting', updatedAt: at, activity: { ...s.activity, turn: 'next-turn', responseTurn: 'next-turn', response: null } });
    await d.view.history.open('demo', id, { session: d.host.get(id) });
    d.host.releaseBefore('result');
  }, { id: CODEX });
  await expect(card.locator(`.history-turn[data-turn="${CODEX}-turn-2"] [data-question="${QID}"] .resolved-summary`)).toContainText('You answered');
  await expect(card.locator(`[data-question="${QID}"]`)).toHaveCount(1);
});

test('malformed question records are tolerated and local records share the latest-20 limit', async ({ page }) => {
  await setup(page);
  const ids = await page.evaluate(async () => {
    const { HistoryView } = await import('/js/ui/history-view.js');
    const { renderResolved } = await import('/js/ui/question-view.js');
    const { readHistoryResult } = await import('/js/history.js');
    const v = new HistoryView();
    document.body.append(v.element);
    try {
      v.render({ phase: 'ready', ...readHistoryResult({ turns: [{ id: 'bad', parts: [{ id: 'bad-part', kind: 'question', question: { kind: 'form', message: 'No id' } }] }] }) });
      const turns = Array.from({ length: 25 }, (_, i) => ({
        id: `t${i}`, parts: [{ id: `p${i}`, kind: 'question', question: { id: `q${i}`, kind: 'form', askedAt: new Date().toISOString(), message: `Question ${i}`, fields: [] } }],
      }));
      const records = new Map(turns.slice(0, 20).map((t) => [t.parts[0].question.id, renderResolved(t.parts[0].question)]));
      v.render({ phase: 'ready', turns }, null, { records });
      while (v.pending) { await new Promise((resolve) => requestAnimationFrame(resolve)); }
      return [...v.element.querySelectorAll('.question.resolved')].map((n) => n.dataset.question);
    } finally { v.dispose(); v.element.remove(); }
  });
  expect(ids).toEqual(Array.from({ length: 20 }, (_, i) => `q${i + 5}`));
});

for (const [cause, outcome, answer, latest] of [
  ['terminal answer', 'answered', 'PostgreSQL'],
  ['another device answer', 'answered', 'SQLite'],
  ['agent cancellation', 'cancelled', null],
  ['session ending', 'cancelled', null],
  ['terminal answer in the latest turn', 'answered', 'PostgreSQL', true],
]) {
  test(`a stored question after ${cause} reloads as one closed line in time order`, async ({ page }) => {
    await setup(page);
    const records = await page.evaluate(({ id, outcome, answer, latest }) => {
      const d = window.agentBridgeDemo;
      const turns = d.host.historyTurns.get(id);
      Object.assign(turns[1].parts[0], { outcome, answer });
      if (latest) {
        turns.splice(2);
        turns[1].parts.push({ id: 'later-reply', kind: 'response', at: new Date().toISOString(), text: 'Reply after the closed question' });
      }
      return turns;
    }, { id: CODEX, outcome, answer, latest });
    await page.reload();
    await page.waitForSelector('html[data-demo-ready]');
    await page.evaluate(async ({ id, records, latest }) => {
      const d = window.agentBridgeDemo;
      d.host.historyTurns.set(id, records);
      const s = d.host.get(id);
      const activity = latest ? { turn: `${id}-turn-2`, userMessage: 'Prompt 2', responseTurn: `${id}-turn-2`, response: 'Reply after the closed question' } : { turn: `${id}-turn-6` };
      d.upsert({ ...s, question: null, status: 'idle', updatedAt: new Date(Date.now() + 4000).toISOString(), activity: { ...s.activity, ...activity } });
      await d.view.history.open('demo', id, { session: d.host.get(id) });
    }, { id: CODEX, records, latest });
    await open(page, CODEX);
    const card = view(page, CODEX);
    const closed = card.locator('.history .history-turn').nth(1).locator('.question.resolved');
    await expect(closed).toHaveCount(1);
    await expect(closed.locator('.resolved-summary')).toContainText('Question closed');
    await expect(closed.locator('.resolved-record')).toBeHidden();
    await closed.getByRole('button', { name: 'Show question' }).click();
    await expect(closed.locator('.resolved-record')).toContainText('Which database?');
    await expect(closed.locator('input, .choice')).toHaveCount(0);
    await expect(card.locator('.reply textarea')).toBeEnabled();
    if (latest) {
      await expect(card.getByText('Reply after the closed question', { exact: true }).filter({ visible: true })).toHaveCount(1);
      expect(await closed.evaluate((el) => !!el.nextElementSibling?.textContent.includes('Reply after the closed question'))).toBe(true);
    }
  });
}
