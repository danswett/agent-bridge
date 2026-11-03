// Getting about the app as a person does: the navigation (a rail on a desktop, the bottom bar on
// a phone) and the sessions page's New, which is the way to Launch on a phone; and a session,
// opened from its row (docs/design/ui.md §5.2: beside the list on a desktop, pushed on a phone).
import { expect } from '@playwright/test';

// A session's view in the session pane, by its id (and its machine, where two have the same id).
export function view(page, id, hostId = null) {
  return page.locator(`.session-view[data-id="${id}"]${hostId ? `[data-host-id="${hostId}"]` : ''}`);
}

// A session's row in the list.
export function row(page, id, hostId = null) {
  return page.locator(`.session[data-id="${id}"]${hostId ? `[data-host-id="${hostId}"]` : ''}`);
}

// Every motion with an end (a fade, a slide, a transition; not a glyph's turning or the progress
// ring) has reached it: what a test reads next is the page at rest, as a person sees it once
// things have moved (ui.md §5.3). A fade still under way reads as a paler colour, or as not drawn.
export async function motionsDone(page) {
  await page.evaluate(() => Promise.all(document.getAnimations()
    .filter((a) => a.playState === 'running' && Number.isFinite(a.effect?.getComputedTiming?.().endTime))
    .map((a) => a.finished.catch(() => {}))));
}

// On a phone, the view pushed over the list goes back to it first.
async function backToList(page) {
  const back = page.locator('.session-view:not([hidden]) .session-header .back');
  if (await back.isVisible()) {
    await back.click();
    await expect(back).toBeHidden();
  }
}

export async function go(page, route) {
  await backToList(page);
  const item = page.locator(`#nav [data-nav="${route}"]`);
  if (route === 'launch' && !(await item.isVisible())) {
    await go(page, 'sessions');
    await page.locator('.new-session').click();
  } else {
    await item.click();
  }
  await expect(page.locator(`[data-route="${route}"]`)).toBeVisible();
}

// Opens a session from its row, on the sessions page; its view is then the one shown.
export async function open(page, id, hostId = null) {
  const v = view(page, id, hostId);
  if (!(await v.isVisible())) {
    if (!(await page.locator('[data-route="sessions"]').isVisible())) { await go(page, 'sessions'); }
    await backToList(page);
    await row(page, id, hostId).locator('.row-main').click();
    await expect(v).toBeVisible();
  }
  // On a phone its content slides in (ui.md §5.3): what a test measures next is where it lands.
  await page.waitForFunction(() => !document.getAnimations().some((a) => a.id === 'ab-motion-push' && a.playState === 'running'));
  // A question it shows takes no click for a moment after it shows (ui.md §5.3, Scroll to a
  // question), whether it was opened now or was open already: a test that clicks next clicks as
  // a person would, once it has.
  await expect(v.locator('.question[data-arriving]')).toHaveCount(0);
  return v;
}
