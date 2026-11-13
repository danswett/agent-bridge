import { expect } from '@playwright/test';
import { HISTORY } from '../js/history.js';

// Explicit manual preloading for tests of a populated history, without moving the reader.
export async function loadEarlierTo(pane, count) {
  const rows = pane.locator('.history-turn');
  await expect.poll(() => rows.count()).toBeGreaterThan(0);
  while (await rows.count() < count) {
    const before = await rows.count();
    const button = pane.getByRole('button', { name: 'Load earlier messages', exact: true });
    await expect(button).not.toHaveAttribute('aria-disabled', 'true');
    await button.evaluate((b) => b.click());
    await expect(rows).toHaveCount(Math.min(count, before + HISTORY.defaultTurns));
  }
  await expect(rows).toHaveCount(count);
}
