// The question card sits where it was asked (#1657, S1): later messages come below it, a bar over the
// reply box says one is waiting while it is out of sight, and answering keeps its place.
import { test, expect } from '@playwright/test';
import { open, view } from './nav.mjs';

const CODEX = '019a1b2c-codex-example';
const QID = 'q-inline';
const long = 'Lorem ipsum dolor sit amet. '.repeat(120);

async function setup(page, { holdHistory = false } = {}) {
  await page.setViewportSize({ width: 900, height: 700 });
  await page.goto('?demo=1&static=1&answerDelay=300');
  await page.waitForSelector('html[data-demo-ready]');
  await page.evaluate(({ id, qid, long, holdHistory }) => {
    const d = window.agentBridgeDemo;
    if (holdHistory) { d.host.holdResponses(); }
    const t = new Date(Date.now() + 1000).toISOString();
    const base = Date.now() - 3600000;
    const at = (n) => new Date(base + n * 1000).toISOString();
    const question = {
      id: qid, kind: 'form', askedAt: at(100), message: 'Which database?',
      fields: holdHistory ? [{ name: 'answer', title: 'Answer', type: 'text' }]
        : [{ name: 'answer', title: 'Database', type: 'choice', required: true, options: [{ value: 'sqlite', label: 'SQLite' }, { value: 'pg', label: 'PostgreSQL' }] }],
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
    if (holdHistory) { d.host.historyTurns.get(id).splice(2); }
    const s = d.host.get(id);
    d.upsert({
      ...s, status: 'waiting', statusSince: t, updatedAt: t, question,
      activity: { ...(s.activity ?? {}), turnCount: holdHistory ? 2 : 6, turn: `${id}-turn-${holdHistory ? 2 : 6}` },
    });
  }, { id: CODEX, qid: QID, long, holdHistory });
  await open(page, CODEX);
  const card = view(page, CODEX);
  if (!holdHistory) { await expect(card.locator('.history .history-turn')).not.toHaveCount(0); }
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
  await page.waitForFunction((qid) => window.agentBridgeDemo.host.typed.some((t) => t.question === qid), QID);
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

test('first history attachment keeps the live question draft, focus and caret', async ({ page }) => {
  const card = await setup(page, { holdHistory: true });
  const input = card.locator('.question:not(.resolved) textarea');
  await expect(card.locator('.history .question-slot')).toHaveCount(0);
  await input.focus();
  await page.keyboard.type('Before after');
  await input.evaluate((el) => el.setSelectionRange(7, 7));
  await page.evaluate(() => window.agentBridgeDemo.host.releaseResponses());
  await expect(card.locator('.history .question-slot')).toHaveCount(1);
  await expect(input).toBeFocused();
  await expect(input).toHaveValue('Before after');
  expect(await input.evaluate((el) => [el.selectionStart, el.selectionEnd])).toEqual([7, 7]);
  await page.keyboard.type('still ');
  await expect(input).toHaveValue('Before still after');
});

test('first history attachment does not take focus moved away from the live question', async ({ page }) => {
  const card = await setup(page, { holdHistory: true });
  const input = card.locator('.question:not(.resolved) textarea');
  await input.focus();
  await page.keyboard.type('My draft');
  const elsewhere = page.locator('#nav [data-nav="sessions"]');
  await elsewhere.focus();
  await page.evaluate(() => window.agentBridgeDemo.host.releaseResponses());
  await expect(card.locator('.history .question-slot')).toHaveCount(1);
  await expect(elsewhere).toBeFocused();
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

for (const width of [390, 1024]) {
  for (const recorded of ['stored', 'omitted', 'empty']) {
    test(`answered questions never stay below three later turns after a snapshot jump (${width}px, asking turn ${recorded})`, async ({ page }) => {
      await page.setViewportSize({ width, height: 800 });
      await page.goto('?demo=1&static=1&answerDelay=50');
      await page.waitForSelector('html[data-demo-ready]');
      await page.evaluate(({ id, qid }) => {
        const d = window.agentBridgeDemo;
        const at = new Date(Date.now() - 60000).toISOString();
        const question = {
          id: qid, kind: 'form', askedAt: at, message: 'Which database?',
          fields: [{ name: 'answer', title: 'Database', type: 'choice', required: true, options: [{ value: 'pg', label: 'PostgreSQL' }] }],
        };
        window.pinnedCase = { question };
        d.host.historyTurns.set(id, []);
        d.upsert({
          ...d.host.get(id), question, status: 'waiting',
          activity: { turn: 'asking-turn', turnCount: 1, userMessage: 'Choose a database', response: null, responseTurn: null },
        });
      }, { id: CODEX, qid: QID });
      const card = await open(page, CODEX);
      const closed = card.locator(`.question.resolved[data-question="${QID}"]`);
      await expect(card.locator('.stream > .question-slot .question:not(.resolved)')).toBeVisible();
      await card.locator(`[data-question="${QID}"] .choice[data-value="pg"]`).click();
      await page.waitForFunction((qid) => window.agentBridgeDemo.host.typed.some((t) => t.question === qid), QID);
      await page.evaluate(async ({ id, recorded }) => {
        const d = window.agentBridgeDemo;
        const s = d.host.get(id);
        const { question } = window.pinnedCase;
        const at = (n) => new Date(Date.now() - 60000 + n * 1000).toISOString();
        const asking = {
          id: 'asking-turn', at: at(0), prompt: { text: 'Choose a database' },
          parts: [{ id: 'asking-part', kind: 'question', at: at(0), question, outcome: 'delivered' }],
        };
        const later = [1, 2, 3].map((n) => ({
          id: `later-${n}`, at: at(n), prompt: { text: `Later prompt ${n}` },
          parts: [{ id: `later-reply-${n}`, kind: 'response', at: at(n), text: `Later reply ${n}` }],
        }));
        window.pinnedCase = { asking, later };
        d.host.historyTurns.set(id, recorded === 'omitted' ? later : [{ ...asking, parts: recorded === 'empty' ? [] : asking.parts }, ...later]);
        d.upsert({ ...s, question: null, status: 'idle', activity: {
          turn: 'later-3', turnCount: 4, userMessage: 'Later prompt 3', responseTurn: 'later-3', response: 'Later reply 3',
        } });
        await d.view.history.open('demo', id, { session: d.host.get(id) });
      }, { id: CODEX, recorded });
      await expect(card.locator('.question:not(.resolved)')).toHaveCount(0);
      const unplaced = card.locator('.question.resolved');
      await expect.poll(() => unplaced.evaluateAll((nodes) => nodes.filter((n) => !n.closest('.history-turn')).length)).toBe(0);
      await page.evaluate((id) => {
        const d = window.agentBridgeDemo;
        d.upsert({ ...d.host.get(id), status: 'waiting', question: { ...window.pinnedCase.asking.parts[0].question, id: 'next-unrecorded' } });
      }, CODEX);
      await expect(card.locator('.stream > .question-slot .question:not(.resolved)')).toBeVisible();
      await expect.poll(() => unplaced.evaluateAll((nodes) => nodes.filter((n) => !n.closest('.history-turn')).length)).toBe(0);
      await page.evaluate(async (id) => {
        const d = window.agentBridgeDemo;
        const { asking, later } = window.pinnedCase;
        d.host.historyTurns.set(id, [asking, ...later]);
        await d.view.history.open('demo', id, { session: d.host.get(id) });
      }, CODEX);
      await expect(card.locator('.history-turn[data-turn="asking-turn"]').locator('.resolved-summary')).toContainText('You answered');
      await expect(closed).toHaveCount(1);
      expect(await card.locator('.history-turn').evaluateAll((nodes) => nodes.map((n) => n.dataset.turn))).toEqual(['asking-turn', 'later-1', 'later-2']);
      await expect(card.locator('.question:not(.resolved)')).toHaveAttribute('data-question', 'next-unrecorded');
      await expect(card.locator('.question[data-arriving]')).toHaveCount(0);
      await card.locator('[data-question="next-unrecorded"] .choice[data-value="pg"]').click();
      await page.waitForFunction(() => window.agentBridgeDemo.host.typed.some((t) => t.question === 'next-unrecorded'));
      await page.evaluate(async (id) => {
        const d = window.agentBridgeDemo;
        const { asking, later } = window.pinnedCase;
        later[2].parts.push({ ...asking.parts[0], id: 'next-part', question: { ...asking.parts[0].question, id: 'next-unrecorded' } });
        d.host.historyTurns.set(id, [asking, ...later]);
        await d.view.history.open('demo', id, { session: d.host.get(id) });
      }, CODEX);
      await expect(card.locator('.history-turn[data-turn="later-3"] .resolved-summary')).toContainText('You answered');
      await page.evaluate((id) => {
        const d = window.agentBridgeDemo;
        d.upsert({ ...d.host.get(id), status: 'waiting', question: { ...window.pinnedCase.asking.parts[0].question, id: 'still-waiting' }, activity: {
          turn: 'waiting-turn', turnCount: 5, userMessage: 'Another question', response: null, responseTurn: null,
        } });
      }, CODEX);
      await expect(card.locator('.stream > .question-slot .question:not(.resolved)')).toBeVisible();
      await expect(card.locator('.question.resolved')).toHaveCount(2);
      expect(await card.locator('.question.resolved').evaluateAll((nodes) => nodes.map((n) => n.closest('.history-turn')?.dataset.turn))).toEqual(['asking-turn', 'later-3']);
    });
  }
}

test('delayed history and disjoint local closures share one newest-20 window', async ({ page }) => {
  const id = 'retention-disjoint';
  await page.goto('?demo=1&static=1');
  await page.waitForSelector('html[data-demo-ready]');
  await page.evaluate(({ id, source }) => {
    const d = window.agentBridgeDemo;
    const at = (n) => new Date(Date.now() - 3600000 + n * 1000).toISOString();
    const question = (id, n) => ({ id, kind: 'form', askedAt: at(n), message: `Question ${id}`, fields: [] });
    const turns = Array.from({ length: 20 }, (_, i) => ({
      id: `stored-turn-${i}`, at: at(i), prompt: { text: `Prompt ${i}` },
      parts: [{ id: `stored-part-${i}`, kind: 'question', at: at(i), question: question(`stored-${i}`, i) }],
    }));
    window.retentionCase = { local: [question('local-0', 30), question('local-1', 31)] };
    d.host.historyTurns.set(id, turns);
    d.upsert({ ...d.host.get(source), id, question: null, status: 'idle', activity: { turn: 'stored-turn-19', turnCount: 20 } });
  }, { id, source: CODEX });
  await open(page, id);
  const card = view(page, id);
  const closed = card.locator('.question.resolved');
  await expect(closed).toHaveCount(20);
  await page.evaluate((id) => {
    const d = window.agentBridgeDemo;
    d.host.holdResponses();
    window.retentionCase.refresh = d.view.history.open('demo', id, { session: d.host.get(id) });
  }, id);
  await expect.poll(() => page.evaluate((id) => window.agentBridgeDemo.view.history.get('demo', id).loading, id)).toBe('newest');
  for (let i = 0; i < 2; i++) {
    await page.evaluate(({ id, i }) => {
      const d = window.agentBridgeDemo;
      const s = d.host.get(id);
      d.upsert({ ...s, question: window.retentionCase.local[i], status: 'waiting', activity: { turn: `local-turn-${i}`, turnCount: 21 + i } });
    }, { id, i });
    await expect(card.locator('.question:not(.resolved)')).toHaveAttribute('data-question', `local-${i}`);
    await page.evaluate((id) => {
      const d = window.agentBridgeDemo;
      d.upsert({ ...d.host.get(id), question: null, status: 'idle' });
    }, id);
    await expect(closed).toHaveCount(20);
    expect(await closed.evaluateAll((nodes) => nodes.map((n) => n.dataset.question))).toEqual([
      ...Array.from({ length: 19 - i }, (_, j) => `stored-${j + i + 1}`),
      ...Array.from({ length: i + 1 }, (_, j) => `local-${j}`),
    ]);
  }
  await page.evaluate(async () => {
    window.agentBridgeDemo.host.releaseResponses();
    await window.retentionCase.refresh;
  });
  await expect(closed).toHaveCount(20);
  await page.evaluate(async (id) => {
    const d = window.agentBridgeDemo;
    d.host.historyTurns.get(id).push(...window.retentionCase.local.map((question, i) => ({
      id: `local-turn-${i}`, at: question.askedAt, prompt: { text: `Local prompt ${i}` },
      parts: [{ id: `local-part-${i}`, kind: 'question', at: question.askedAt, question, outcome: 'cancelled' }],
    })));
    await d.view.history.open('demo', id, { session: d.host.get(id) });
  }, id);
  await expect(closed).toHaveCount(20);
  await expect(card.locator('.history .question.resolved[data-question^="local-"]')).toHaveCount(2);
  expect(await closed.evaluateAll((nodes) => nodes.map((n) => n.dataset.question))).toEqual([
    ...Array.from({ length: 18 }, (_, i) => `stored-${i + 2}`), 'local-0', 'local-1',
  ]);
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
      const toggle = closed.getByRole('button', { name: 'Hide question' });
      await toggle.focus();
      await page.evaluate(({ id }) => {
        const d = window.agentBridgeDemo;
        const s = d.host.get(id);
        d.upsert({ ...s, updatedAt: new Date(Date.now() + 5000).toISOString(), activity: { ...s.activity, responseTurn: s.activity.turn, response: 'A new live reply after reload' } });
      }, { id: CODEX });
      await expect(card.getByText('A new live reply after reload', { exact: true }).filter({ visible: true })).toHaveCount(1);
      await expect(toggle).toHaveAttribute('aria-expanded', 'true');
      await expect(closed.locator('.resolved-record')).toBeVisible();
      await expect(toggle).toBeFocused();
    }
  });
}
