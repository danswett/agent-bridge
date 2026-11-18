import { test, expect } from '@playwright/test';

test('display title wire patches set and clear independently of the agent title', async ({ page }) => {
  await page.goto('/?demo=1');
  const titles = await page.evaluate(async () => {
    const { readSession } = await import('/js/protocol.js');
    const { applyPatch } = await import('/js/session-patch.js');
    const original = { id: 'session', title: 'Agent title' };
    const named = applyPatch(original, 1, { id: 'session', base: 1, set: { displayTitle: 'Shared title' } });
    const cleared = applyPatch(named, 2, { id: 'session', base: 2, set: { displayTitle: null } });
    return [readSession(named).displayTitle, readSession(cleared).displayTitle, readSession(cleared).title];
  });
  expect(titles).toEqual(['Shared title', null, 'Agent title']);
});
