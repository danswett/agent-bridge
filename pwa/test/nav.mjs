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
//
// At rest means nothing moving for two frames running, not only the motions already started
// having ended: some start a task or a frame after what the test did, so a single look found
// none and axe then read them mid-fade (#551). A <details> opened by a click fades its content
// in on its toggle event, which comes as a task after the click; an update the test waited for
// part of (an Approve's outcome) draws the rest of itself (the receipt, the echoed message) a
// moment later. Returns motionsStarted's count as of the last still frame (null without
// countMotions), so a motion starting just after is counted as during whatever comes next.
export async function motionsDone(page) {
  return page.evaluate(async () => {
    const frame = () => new Promise((resolve) => { requestAnimationFrame(() => resolve()); });
    const moving = () => document.getAnimations()
      .filter((a) => a.playState === 'running' && Number.isFinite(a.effect?.getComputedTiming?.().endTime));
    for (let still = 0; still < 2;) {
      await frame();
      const running = moving();
      if (running.length) {
        still = 0;
        await Promise.all(running.map((a) => a.finished.catch(() => {})));
      } else {
        still += 1;
      }
    }
    return typeof window.__abMotionsStarted === 'number' ? window.__abMotionsStarted : null;
  });
}

// Counts every motion with an end that starts on the page from its next load on (an element's
// animate(), a CSS transition, a CSS animation that ends), for motionsStarted. Call it before
// the page loads.
export async function countMotions(page) {
  await page.addInitScript(() => {
    window.__abMotionsStarted = 0;
    const ends = (a) => !a || Number.isFinite(a.effect?.getComputedTiming?.().endTime);
    const animate = Element.prototype.animate;
    Element.prototype.animate = function (...args) {
      const a = animate.apply(this, args);
      if (ends(a)) { window.__abMotionsStarted += 1; }
      return a;
    };
    document.addEventListener('transitionrun', () => { window.__abMotionsStarted += 1; }, true);
    document.addEventListener('animationstart', (e) => {
      const a = e.target.getAnimations?.({ subtree: true }).find((x) => x.animationName === e.animationName);
      if (ends(a)) { window.__abMotionsStarted += 1; }
    }, true);
  });
}

// How many motions with an end have started on the page since it loaded (countMotions).
export async function motionsStarted(page) {
  const n = await page.evaluate(() => window.__abMotionsStarted);
  if (typeof n !== 'number') { throw new Error('motionsStarted needs countMotions(page) before the page loads'); }
  return n;
}

// On a phone, the view pushed over the list goes back to it first.
async function backToList(page) {
  const back = page.locator('.session-view:not([hidden]) .session-bar .back');
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
