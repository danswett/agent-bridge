import { expect } from '@playwright/test';

// Explicit manual preloading for tests of a populated history, without moving the reader.
export async function loadEarlierTo(pane, count) {
  const rows = pane.locator('.history-turn');
  await expect.poll(() => rows.count()).toBeGreaterThan(0);
  while (await rows.count() < count) {
    const before = await rows.count();
    const button = pane.getByRole('button', { name: 'Load earlier messages', exact: true });
    await expect(button).not.toHaveAttribute('aria-disabled', 'true');
    await button.evaluate((b) => b.click());
    await expect.poll(() => rows.count()).toBeGreaterThan(before);
    // A page is drawn over several frames; finish it before asking for the next one.
    await pane.evaluate((el) => new Promise((resolve) => {
      let last = -1;
      let still = 0;
      const frame = () => {
        const n = el.querySelectorAll('.history-turn').length;
        still = n === last ? still + 1 : 0;
        last = n;
        if (still >= 2) { resolve(); } else { requestAnimationFrame(frame); }
      };
      requestAnimationFrame(frame);
    }));
  }
  await expect.poll(() => rows.count()).toBeGreaterThanOrEqual(count);
}
