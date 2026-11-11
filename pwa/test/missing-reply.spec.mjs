import { test, expect } from '@playwright/test';
import { open, view } from './nav.mjs';

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
