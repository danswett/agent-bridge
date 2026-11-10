// A session that is moving to another machine shows it (#1715, ui.md 6.6.2, #1146 S8a): the five
// steps of the move contract (docs/protocol/v1-machine-load.md, MoveTelemetry) as done, current
// and not-reached states in words and icons, 'From X to Y', a failed step with its plain reason
// and the original still answerable, and 'Moved to Y' with Go to it. Fed a fake step stream until
// the host's step events (#1712) land; the machine names come from the data, never from the code.
import { test, expect } from '@playwright/test';
import { open, row, view } from './nav.mjs';

const ID = '8c4e1f20-5a7b-4d3c-9e61-0b2f7d9a1c35';
const NAMES = ['handoffRequested', 'handoffConfirmed', 'artifactsPushed', 'newLaunched', 'newOnline'];
const LABELS = ['Hand-off requested', 'Hand-off confirmed', 'Files sent', 'New session launched', 'New session online'];

const strip = (page) => view(page, ID).locator('.move-strip');
const stepsOf = (page) => strip(page).locator('.move-step-item');
// The move as the host will send it: the steps reached so far, and an outcome once it ended.
const feed = (page, reached, extra = {}) => page.evaluate(([id, names, extra]) => {
  window.agentBridgeDemo.moves.set(id, {
    moveId: 'm1', from: 'Laptop', to: 'Studio PC', session: id,
    steps: names.map((name, i) => ({ name, at: `2026-11-10T17:00:0${i}Z`, ms: i === 0 ? 0 : 1000 })), ...extra,
  });
}, [ID, NAMES.slice(0, reached), extra]);
// The strip, and the header it is in, never change height from one step to the next (no-pop).
const heights = (page) => page.evaluate((id) => {
  const v = document.querySelector(`.session-view[data-id="${id}"]`);
  return [v.querySelector('.move-strip').getBoundingClientRect().height, v.querySelector('.session-top').getBoundingClientRect().height];
}, ID);

async function openDemo(page) {
  await page.goto('?demo=1&static=1');
  await page.waitForSelector('html[data-demo-ready]');
  await open(page, ID);
}
const expandIfPhone = async (page) => {
  const summary = strip(page).locator('summary');
  if (await stepsOf(page).first().isHidden()) { await summary.click(); }
};

for (const size of [{ name: 'phone', width: 390, height: 700 }, { name: '900 px', width: 900, height: 800 }]) {
  test.describe(size.name, () => {
    test.use({ viewport: { width: size.width, height: size.height } });

    test('a move shows each of the five steps as done, current or not yet, in words, in order', async ({ page }) => {
      await openDemo(page);
      await expect(strip(page)).toBeHidden();
      await feed(page, 0);
      await expect(strip(page)).toBeVisible();
      await expect(strip(page)).toContainText('Moving from Laptop to Studio PC');
      await expect(row(page, ID)).toContainText('Moving to Studio PC');
      await expandIfPhone(page);
      await expect(stepsOf(page)).toHaveCount(5);
      for (let reached = 0; reached < 5; reached++) {
        await feed(page, reached);
        await expect(strip(page)).toContainText(`Step ${reached + 1} of 5: ${LABELS[reached]}`);
        for (let i = 0; i < 5; i++) {
          const state = i < reached ? 'Done' : i === reached ? 'In progress' : 'Not yet';
          await expect(stepsOf(page).nth(i)).toHaveText(new RegExp(`${LABELS[i]}.*${state}`));
          // An icon beside the words: the state is never colour alone.
          await expect(stepsOf(page).nth(i).locator('.glyph')).toHaveCount(1);
        }
      }
    });

    test('the strip and the header do not change height from one step to the next', async ({ page }) => {
      await openDemo(page);
      await feed(page, 0);
      await expandIfPhone(page);
      const first = await heights(page);
      for (const reached of [1, 2, 3, 4, 5]) {
        await feed(page, reached, reached === 5 ? { outcome: 'online' } : {});
        await expect(strip(page)).toContainText(reached === 5 ? 'Moved to Studio PC' : `Step ${reached + 1} of 5`);
        expect(await heights(page)).toEqual(first);
      }
    });

    test('a failed third step says so, names the reason, and the original can still be answered', async ({ page }) => {
      await openDemo(page);
      await feed(page, 2, { outcome: 'failed', reason: 'The files were too large to send.' });
      await expect(strip(page)).toContainText('Failed at step 3 of 5: Files sent');
      await expandIfPhone(page);
      await expect(stepsOf(page).nth(1)).toContainText('Done');
      await expect(stepsOf(page).nth(2)).toContainText('Failed');
      await expect(stepsOf(page).nth(3)).toContainText('Not reached');
      await expect(stepsOf(page).nth(4)).toContainText('Not reached');
      await expect(strip(page).locator('.move-note')).toContainText('The files were too large to send.');
      await expect(strip(page).locator('.move-note')).toContainText('The original session is untouched and you can still answer it.');
      await expect(view(page, ID).locator('form.reply').getByRole('textbox', { name: 'Reply' })).toBeEnabled();
    });

    test('a finished move says where the session went, with Go to it', async ({ page }) => {
      await openDemo(page);
      await feed(page, 5, { outcome: 'online' });
      await expect(strip(page)).toContainText('Moved to Studio PC');
      await expect(strip(page).getByRole('button', { name: 'Go to it' })).toBeVisible();
      await expect(row(page, ID)).toContainText('Moved to Studio PC');
    });

    test('a session that asks a question keeps that on its row while it moves', async ({ page }) => {
      await page.goto('?demo=1&static=1&machines=2');
      await page.waitForSelector('html[data-demo-ready]');
      const ask = 'laptop-ask-1';
      await expect(row(page, ask)).toContainText('Waiting on you');
      await page.evaluate((id) => window.agentBridgeDemo.moves.set(id, { moveId: 'm2', from: 'Laptop', to: 'Studio PC', session: id, steps: [] }), ask);
      await expect(row(page, ask)).toContainText('Waiting on you');
      await expect(row(page, ask)).not.toContainText('Moving to');
      await expect(row(page, ask).locator('.row-status')).toHaveAttribute('data-tone', 'needs');
    });
  });
}
