// Desktop WebKit models an installed iPhone and keyboard-only viewport changes here.
// This is a layout regression, not proof of a real iPhone's keyboard or safe-area reporting.
import { test, expect } from '@playwright/test';
import { open, motionsDone } from './nav.mjs';

const IDLE = '8c4e1f20-5a7b-4d3c-9e61-0b2f7d9a1c35';
const PHONE = { width: 390, height: 844 };

test.use({
  viewport: PHONE, isMobile: true, hasTouch: true, deviceScaleFactor: 3,
  userAgent: 'Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1',
});

async function installed(page, { platform = 'ios', standalone = true } = {}) {
  const size = page.viewportSize();
  await page.addInitScript(({ platform, standalone, size }) => {
    Object.defineProperty(navigator, 'standalone', { value: standalone });
    Object.defineProperty(navigator, 'userAgentData', { value: null });
    Object.defineProperty(navigator, 'userAgent', { value: platform === 'ios' ? 'iPhone' : platform === 'ipados' ? 'iPad' : 'Windows' });
    const vv = new EventTarget();
    Object.assign(vv, { ...size, offsetTop: 0, offsetLeft: 0, scale: 1 });
    Object.defineProperty(window, 'visualViewport', { value: vv });
  }, { platform, standalone, size });
  await page.goto('?demo=1&static=1');
  await page.waitForSelector('html[data-demo-ready]');
  await expect(page.locator('html')).toHaveAttribute('data-platform', platform);
  await page.evaluate((id) => {
    const d = window.agentBridgeDemo, s = d.host.get(id);
    d.upsert({ ...s, question: null, activity: {
      ...s.activity, response: Array.from({ length: 100 }, (_, i) => `Paragraph ${i}. A long transcript.`).join('\n\n'),
      reasoning: null, userMessage: null, currentTool: null,
    } });
    document.documentElement.style.setProperty('--env-safe-top', '47px');
    document.documentElement.style.setProperty('--env-safe-bottom', '34px');
  }, IDLE);
  return open(page, IDLE);
}

async function viewport(page, { height, top = 0, safe = 34 }) {
  await page.evaluate(({ height, top, safe }) => {
    document.documentElement.style.setProperty('--env-safe-bottom', `${safe}px`);
    Object.assign(visualViewport, { height, width: innerWidth, offsetTop: top });
    visualViewport.dispatchEvent(new Event('resize'));
    visualViewport.dispatchEvent(new Event('scroll'));
    window.dispatchEvent(new Event('resize'));
  }, { height, top, safe });
  await motionsDone(page);
}

async function check(page, v, { keyboard, safe = 34 }, info) {
  const box = v.locator('.reply-box'), send = v.getByRole('button', { name: 'Send', exact: true });
  const geometry = await box.evaluate((el) => {
    const r = el.getBoundingClientRect(), vv = visualViewport;
    const pane = document.querySelector('.session-pane').getBoundingClientRect();
    const button = el.querySelector('.send'), b = button.getBoundingClientRect();
    const hit = document.elementFromPoint(b.x + b.width / 2, b.y + b.height / 2);
    return {
      box: { top: r.top, bottom: r.bottom, left: r.left, right: r.right },
      pane: { top: pane.top, bottom: pane.bottom },
      viewport: { top: vv.offsetTop, height: vv.height, bottom: vv.offsetTop + vv.height },
      gap: vv.offsetTop + vv.height - r.bottom,
      sendHit: button === hit || button.contains(hit),
      hit: hit?.className, send: { top: b.top, bottom: b.bottom },
      paddingBottom: getComputedStyle(document.querySelector('.session-pane')).paddingBottom,
      slotPadding: getComputedStyle(el.closest('.reply-slot')).paddingBottom,
      pageScroll: scrollY,
      grid: getComputedStyle(el.closest('.session-view')).gridTemplateRows,
      children: [...el.closest('.session-view').children].map((e) => [e.className, getComputedStyle(e).gridRow, getComputedStyle(e).position]),
      ancestors: ['.shell', '.session-pane', '.session-view', '.view-body', '.conversation', '.reply-slot'].map((selector) => {
        const n = el.closest(selector), r = n.getBoundingClientRect();
        return [selector, r.top, r.height];
      }),
    };
  });
  await info.attach('reply-geometry.json', { body: JSON.stringify(geometry), contentType: 'application/json' });
  expect(geometry.gap, JSON.stringify(geometry)).toBeGreaterThanOrEqual(0);
  expect(geometry.gap, JSON.stringify(geometry)).toBeLessThanOrEqual(keyboard ? 2 : safe);
  expect(geometry.box.top).toBeGreaterThanOrEqual(geometry.viewport.top);
  expect(geometry.pane.bottom).toBeLessThanOrEqual(geometry.viewport.bottom + 2);
  expect(geometry.box.left).toBeGreaterThanOrEqual(0);
  expect(geometry.box.right).toBeLessThanOrEqual(page.viewportSize().width);
  expect(geometry.sendHit, JSON.stringify(geometry)).toBe(true);
  expect(geometry.pageScroll).toBe(0);
  await send.click({ trial: true });
}

test('the installed iPhone reply box stays at the visible bottom through five keyboard cycles, attachments, long scroll and rotation', async ({ page }, info) => {
  const v = await installed(page), input = v.getByRole('textbox', { name: 'Reply', exact: true });
  await input.fill('Reply');
  await viewport(page, { height: 844 });
  await check(page, v, { keyboard: false }, info);
  for (let cycle = 0; cycle < 5; cycle++) {
    await input.focus();
    // The layout viewport stays 844: unlike setViewportSize, only the keyboard viewport changes.
    // iOS may retain its bottom safe area while the keyboard is present.
    await viewport(page, { height: 450, top: cycle % 2 ? 80 : 0, keyboard: true });
    await check(page, v, { keyboard: true }, info);
    if (cycle === 1) {
      await v.locator('input[type=file]').setInputFiles({
        name: 'layout.txt', mimeType: 'text/plain', buffer: Buffer.from('layout attachment'),
      });
      await expect(v.locator('.attachment')).toHaveCount(1);
    }
    if (cycle >= 1) { await input.fill('One\nTwo\nThree\nFour\nFive\nSix\nSeven\nEight'); }
    await motionsDone(page);
    await check(page, v, { keyboard: true }, info);
    if (cycle === 2) {
      await page.setViewportSize({ width: 844, height: 390 });
      await page.evaluate(() => document.documentElement.style.setProperty('--env-safe-top', '0px'));
      await viewport(page, { height: 260, keyboard: true, safe: 21 });
      await check(page, v, { keyboard: true, safe: 21 }, info);
      await page.setViewportSize(PHONE);
      await page.evaluate(() => document.documentElement.style.setProperty('--env-safe-top', '47px'));
      await viewport(page, { height: 450, keyboard: true });
      await check(page, v, { keyboard: true }, info);
    }
    await input.blur();
    await viewport(page, { height: 844 });
    const scroller = v.locator('.stream-scroll');
    await scroller.evaluate((el) => { el.scrollTop = 0; el.dispatchEvent(new Event('scroll')); });
    await scroller.evaluate((el) => { el.scrollTop = el.scrollHeight; el.dispatchEvent(new Event('scroll')); });
    await motionsDone(page);
    await check(page, v, { keyboard: false }, info);
  }
  await page.evaluate(() => {
    visualViewport.dispatchEvent(new Event('resize'));
    dispatchEvent(new PageTransitionEvent('pagehide', { persisted: true }));
  });
  await viewport(page, { height: 450, keyboard: true });
  expect(await page.locator('html').evaluate((el) => el.style.getPropertyValue('--phone-view-height'))).toBe('');
  await page.evaluate(() => dispatchEvent(new PageTransitionEvent('pageshow', { persisted: true })));
  await motionsDone(page);
  await check(page, v, { keyboard: true }, info);
  await viewport(page, { height: 844 });
  await check(page, v, { keyboard: false }, info);
});

test('a keyboard-only resize never puts the reply box below the visible viewport or counts the retained safe area twice', async ({ page }, info) => {
  const v = await installed(page);
  await v.getByRole('textbox', { name: 'Reply', exact: true }).fill('Reply');
  await viewport(page, { height: 450, keyboard: true });
  await check(page, v, { keyboard: true }, info);
});

for (const [platform, standalone, width] of [['ipados', true, 1024], ['windows', false, 1440], ['ios', false, 390], ['ipados', true, 844], ['windows', false, 844], ['ios', false, 844]]) {
  test(`${platform} standalone=${standalone} at ${width} keeps its geometry and reading order when the visual viewport changes`, async ({ page }) => {
    await page.setViewportSize({ width, height: 900 });
    const v = await installed(page, { platform, standalone });
    if (width === 844) { await expect(page.locator('.list-pane')).toBeVisible(); await expect(page.locator('.session-pane')).toBeVisible(); }
    await v.getByRole('textbox', { name: 'Reply', exact: true }).fill('One\nTwo');
    const before = await v.locator('.reply-box').boundingBox();
    await viewport(page, { height: 500, keyboard: true });
    expect(await v.locator('.reply-box').boundingBox()).toEqual(before);
    const tree = await v.locator('form.reply').ariaSnapshot();
    expect(tree).toContain('Line breaks are sent');
    expect(tree).toContain('textbox "Reply"');
    expect(tree.indexOf('textbox "Reply"')).toBeLessThan(tree.indexOf('Line breaks are sent'));
    expect(await v.locator('.receipt-live').evaluate((el) => !!(el.compareDocumentPosition(el.closest('form').querySelector('.reply-box')) & Node.DOCUMENT_POSITION_PRECEDING))).toBe(true);
  });
}

test('installed iPhone notices precede the composer in DOM and accessibility order and its existing live region announces an arriving receipt once', async ({ page }, info) => {
  const v = await installed(page), form = v.locator('form.reply');
  await v.getByRole('textbox', { name: 'Reply', exact: true }).fill('One\nTwo');
  const tree = await form.ariaSnapshot();
  expect(tree).toContain('Line breaks are sent');
  expect(tree).toContain('textbox "Reply"');
  expect(tree.indexOf('Line breaks are sent')).toBeLessThan(tree.indexOf('textbox "Reply"'));
  await form.locator('.receipt-live').evaluate((live) => {
    window.__originalLive = live;
    window.__announcements = [];
    window.__liveMoved = false;
    new MutationObserver(() => window.__announcements.push(live.textContent)).observe(live, { childList: true, subtree: true, characterData: true });
    new MutationObserver((records) => {
      if (records.some((r) => [...r.removedNodes].includes(live))) { window.__liveMoved = true; }
    }).observe(live.parentNode, { childList: true });
    if (!(live.compareDocumentPosition(live.closest('form').querySelector('.reply-box')) & Node.DOCUMENT_POSITION_FOLLOWING)) { throw new Error('Live region must precede box'); }
    window.agentBridgeDemo.host.holdResponses();
  });
  await expect(form.locator('.receipt-live')).toHaveAttribute('role', 'status');
  await expect(form.locator('.receipt-live')).toHaveAttribute('aria-live', 'polite');
  await check(page, v, { keyboard: false }, info);
  await form.getByRole('button', { name: 'Send', exact: true }).click();
  await expect(form.locator('.receipt-live')).toHaveText('Sending reply');
  expect(await form.locator('.receipt-live').evaluate((el) => ({
    same: el === window.__originalLive, moved: window.__liveMoved,
    announcements: window.__announcements.filter((text) => text === 'Sending reply'),
  }))).toEqual({ same: true, moved: false, announcements: ['Sending reply'] });
});

test('the installed iPhone landscape More menu keeps its actions tappable', async ({ page }) => {
  const v = await installed(page);
  await page.setViewportSize({ width: 844, height: 390 });
  await viewport(page, { height: 390, safe: 21 });
  await v.getByRole('button', { name: 'More', exact: true }).click();
  const details = v.getByRole('menuitem', { name: 'Details', exact: true });
  expect(await details.evaluate((el) => getComputedStyle(el).pointerEvents)).toBe('auto');
  await details.click();
  await expect(v.locator('.details')).toBeVisible();
});

test('the installed iPhone landscape Tools tab clears the floating header', async ({ page }) => {
  const v = await installed(page);
  await page.setViewportSize({ width: 844, height: 390 });
  await viewport(page, { height: 390, safe: 21 });
  await v.getByRole('tab', { name: 'Tools', exact: true }).click();
  await motionsDone(page);
  const head = await v.locator('.session-top').boundingBox();
  const tools = await v.locator('.tools-panel').boundingBox();
  expect(tools.y).toBeGreaterThanOrEqual(head.y + head.height);
});

test('Back leaves an installed iPhone landscape session and returns to the list', async ({ page }) => {
  const v = await installed(page);
  await page.setViewportSize({ width: 844, height: 390 });
  await viewport(page, { height: 390, safe: 21 });
  await v.getByRole('button', { name: 'Sessions', exact: true }).click();
  await expect(page.locator('.session-pane')).toBeHidden();
  await expect(page.locator('.list-pane')).toBeVisible();
  expect(await page.locator('#list-divider').isVisible()).toBe(false);
});
