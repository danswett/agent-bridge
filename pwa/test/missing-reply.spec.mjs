import { test, expect } from '@playwright/test';
import { open, view } from './nav.mjs';
import { readFileSync } from 'node:fs';

test('the 16-event queued-owner fixture retains all four replies under their own origins', async ({ page }) => {
  const events = readFileSync(new URL('../../tests/fixtures/copilot/queued-owner-history.jsonl', import.meta.url), 'utf8').trim().split('\n').map((line) => JSON.parse(line));
  const turns = [[0, 3, 'user', 'person'], [2, 7, 'user', 'person'], [6, 10, 'notification', 'notification'], [12, 14, 'scheduled', 'schedule']]
    .map(([q, a, kind, from]) => ({ id: events[q].id, kind, at: events[q].timestamp,
      prompt: { text: events[q].data.content, from },
      parts: [{ id: events[a].id, at: events[a].timestamp, kind: 'response', text: events[a].data.content }] }));
  const [a, b, notice, scheduled] = turns;
  await page.goto('?demo=1&static=1&signin=1&name=DEV-VM1#/sessions');
  await page.waitForSelector('html[data-demo-ready]');
  await page.evaluate(({ a, b, notice }) => {
    const { host } = window.agentBridgeDemo;
    host.historyTurns.set('signin-1', [a]);
    const s = host.sessions.get('signin-1');
    host.upsert({ ...s, activity: { turn: b.id, kind: 'user', responseTurn: a.id,
      userMessage: b.prompt.text, response: a.parts[0].text, notice } });
  }, { a, b, notice });
  await open(page, 'signin-1');
  const v = view(page, 'signin-1');
  await expect(v.locator(`[data-turn="${notice.id}"]`)).toContainText(notice.parts[0].text);
  await expect(v.locator(`[data-turn="${notice.id}"] .prompt-facts`)).toHaveText('Notification');
  await expect(v.locator(`[data-turn="${notice.id}"] .history-reply`)).toBeVisible();
  await expect(v.locator(`[data-turn="${notice.id}"] .history-prompt`)).toHaveCount(0);
  await expect(v.locator('.response')).toBeHidden();
  await page.evaluate(({ turns, scheduled, notice }) => {
    const { host } = window.agentBridgeDemo;
    host.historyTurns.set('signin-1', turns);
    const s = host.sessions.get('signin-1');
    host.upsert({ ...s, activity: { turn: scheduled.id, kind: 'scheduled', responseTurn: scheduled.id,
      userMessage: scheduled.prompt.text, userMessageFrom: 'schedule', response: scheduled.parts[0].text, notice } });
  }, { turns, scheduled, notice });
  for (const t of turns) {
    const row = t.id === scheduled.id ? v.locator('.stream') : v.locator(`[data-turn="${t.id}"]`);
    await expect(v).toContainText(t.parts[0].text);
    if (t.id !== scheduled.id) { await expect(row).toContainText(t.parts[0].text); }
    await expect(v.locator('.history-reply, .response').filter({ hasText: t.parts[0].text })).toHaveCount(1);
  }
  await expect(v.locator('.stream .prompt-facts')).toHaveText('Scheduled');
});

for (const mode of ['jump', 'in-order', 'stored', 'duplicate']) {
  test(`${mode}: question and reply precede the scheduled prompt and its reply`, async ({ page }) => {
    const warnings = [];
    page.on('console', (m) => { if (m.type() === 'warning') { warnings.push(m.text()); } });
    await page.goto('?demo=1&static=1&signin=1&name=DEV-VM1#/sessions');
    await page.waitForSelector('html[data-demo-ready]');
    await page.evaluate((mode) => {
      const { host } = window.agentBridgeDemo;
      const at = '2026-11-11T06:37:19Z';
      const a = { id: 'question-a', at, prompt: { text: 'Question A', from: 'person' }, parts: [{ id: 'answer-a', kind: 'response', at, text: 'Answer A' }] };
      const b = { id: mode === 'duplicate' ? a.id : 'scheduled-b', at: '2026-11-11T06:37:59Z', prompt: { text: 'Scheduled B', from: 'schedule', label: 'Watch' }, parts: [{ id: 'answer-b', kind: 'response', at, text: 'Answer B' }] };
      window.replyCase = { a, b };
      host.historyTurns.set('signin-1', mode === 'stored' || mode === 'duplicate' ? [a, b] : []);
      const s = host.sessions.get('signin-1');
      host.upsert({ ...s, activity: { ...s.activity, turn: a.id, responseTurn: null, userMessage: a.prompt.text, response: null, own: undefined, source: undefined } });
    }, mode);
    await open(page, 'signin-1');
    if (mode === 'jump' || mode === 'in-order') {
      await expect(view(page, 'signin-1')).toContainText('Question A');
      await page.evaluate((mode) => {
        const { host } = window.agentBridgeDemo;
        const { a, b } = window.replyCase;
        const s = host.sessions.get('signin-1');
        if (mode === 'in-order') { host.upsert({ ...s, activity: { ...s.activity, responseTurn: a.id, response: 'Answer A' } }); }
        host.historyTurns.set('signin-1', [a, b]);
        host.upsert({ ...s, activity: { ...s.activity, turn: b.id, responseTurn: b.id, userMessage: b.prompt.text, userMessageFrom: 'schedule', userMessageLabel: 'Watch', response: 'Answer B' } });
      }, mode);
    }
    // Render both stored turns, not a seeded demo live exchange.
    if (mode === 'stored' || mode === 'duplicate') {
      await page.evaluate(() => {
        const { host } = window.agentBridgeDemo;
        const s = host.sessions.get('signin-1');
        host.upsert({ ...s, activity: { ...s.activity, turn: 'after-b', responseTurn: null, userMessage: 'Next', response: null } });
      });
    }
    const v = view(page, 'signin-1');
    await expect(v).toContainText('Answer A');
    await expect(v).toContainText('Answer B');
    const text = await v.locator('.stream-scroll').innerText();
    const positions = ['Question A', 'Answer A', 'Scheduled B', 'Answer B'].map((s) => text.indexOf(s));
    expect(positions).not.toContain(-1);
    expect(positions).toEqual([...positions].sort((a, b) => a - b));
    for (const word of ['Question A', 'Answer A', 'Scheduled B', 'Answer B']) { expect(text.split(word).length - 1).toBe(1); }
    await expect(v.locator('.prompt-facts').filter({ hasText: 'Scheduled' })).toHaveCount(1);
    if (mode === 'duplicate') { expect(warnings.filter((w) => w.includes('Duplicate history turn ids'))).toEqual(['Duplicate history turn ids: 1']); }
  });
}
