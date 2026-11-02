// The platform layer (#168, docs/design/ui.md §7, PWA): detection only, for the shortcut hints,
// the safe areas and the system's Back, in demo mode in Chromium and WebKit. Each test says which
// platform the page is on through what platform.js reads (the user agent, Chromium's client
// hints, touch points and whether it is installed), so both engines play every platform.
import { test, expect } from '@playwright/test';
import { open, view } from './nav.mjs';

const IDLE = '8c4e1f20-5a7b-4d3c-9e61-0b2f7d9a1c35';
const DELEGATING = 'demo-delegates';

const UA = {
  windows: 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36',
  mac: 'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Safari/605.1.15',
  iphone: 'Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1',
  android: 'Mozilla/5.0 (Linux; Android 15; Pixel 9) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Mobile Safari/537.36',
  unrecognised: 'Mozilla/5.0 (Unrecognised OS) SomeBrowser/1.0',
};
const AS = {
  windows: { ua: UA.windows, hint: 'Windows' },
  unknown: { ua: UA.unrecognised },
  macos: { ua: UA.mac },
  iosSafari: { ua: UA.iphone, touch: 5 },
  iosInstalled: { ua: UA.iphone, touch: 5, standalone: true },
  ipadosInstalled: { ua: UA.mac, touch: 5, standalone: true },
  androidInstalled: { ua: UA.android, hint: 'Android', touch: 5, displayMode: true },
};

// The page as it is on that platform: what platform.js reads says so, from before the first script.
async function on(page, as, query = '') {
  await page.addInitScript(({ ua, hint = null, touch = 0, standalone = false, displayMode = false }) => {
    window.__cspViolations = [];
    document.addEventListener('securitypolicyviolation', (e) => window.__cspViolations.push(`${e.violatedDirective} ${e.blockedURI}`));
    const nav = Navigator.prototype;
    Object.defineProperty(nav, 'userAgent', { get: () => ua, configurable: true });
    Object.defineProperty(nav, 'userAgentData', { get: () => (hint ? { platform: hint } : undefined), configurable: true });
    Object.defineProperty(nav, 'maxTouchPoints', { get: () => touch, configurable: true });
    Object.defineProperty(nav, 'standalone', { get: () => (ua.includes('iPhone') || ua.includes('Mac') ? standalone : undefined), configurable: true });
    if (displayMode) {
      const real = window.matchMedia.bind(window);
      window.matchMedia = (q) => (q === '(display-mode: standalone)' ? { matches: true, media: q, onchange: null, addEventListener() {}, removeEventListener() {}, addListener() {}, removeListener() {} } : real(q));
    }
  }, as);
  await page.goto(`?demo=1&static=1${query}`);
  await page.waitForSelector('html[data-demo-ready]');
}

test.afterEach(async ({ page }) => {
  expect(await page.evaluate(() => window.__cspViolations ?? [])).toEqual([]);
});

test.describe('shortcut hints', () => {
  test.use({ viewport: { width: 1440, height: 900 } });

  test('on a Mac, Send\'s hint says ⌘↩ and search\'s ⌘K, and ⌘K focuses search', async ({ page }) => {
    await on(page, AS.macos);
    await expect(page.locator('html')).toHaveAttribute('data-platform', 'macos');
    const v = await open(page, IDLE);
    await expect(v.getByRole('button', { name: 'Send' })).toHaveAttribute('title', 'Send (⌘↩)');
    const search = page.locator('#search');
    await expect(search).toHaveAttribute('title', 'Search sessions (⌘K)');
    await expect(search).toHaveAttribute('aria-keyshortcuts', 'Meta+K');
    await page.keyboard.press('Meta+k');
    await expect(search).toBeFocused();
  });

  test('on Windows, Send\'s hint says Ctrl+Enter and search\'s Ctrl+K, and Ctrl+K focuses search', async ({ page }) => {
    await on(page, AS.windows);
    await expect(page.locator('html')).toHaveAttribute('data-platform', 'windows');
    const v = await open(page, IDLE);
    await expect(v.getByRole('button', { name: 'Send' })).toHaveAttribute('title', 'Send (Ctrl+Enter)');
    const search = page.locator('#search');
    await expect(search).toHaveAttribute('title', 'Search sessions (Ctrl+K)');
    await expect(search).toHaveAttribute('aria-keyshortcuts', 'Control+K');
    await page.keyboard.press('Control+k');
    await expect(search).toBeFocused();
  });
});

// What iOS reports for an iPhone's notch and home indicator, standing in for env(): no desktop
// engine reports a safe area. Through the CSSOM, which the page's policy allows.
const IPHONE_INSETS = { top: 47, right: 0, bottom: 34, left: 0 };
const IPAD_INSETS = { top: 24, right: 0, bottom: 20, left: 0 };
const LANDSCAPE_INSETS = { top: 0, right: 47, bottom: 21, left: 47 };
async function reported(page, insets) {
  await page.evaluate((i) => {
    for (const side of ['top', 'right', 'bottom', 'left']) { document.documentElement.style.setProperty(`--env-safe-${side}`, `${i[side]}px`); }
  }, insets);
}
const rect = (locator) => locator.evaluate((el) => el.getBoundingClientRect().toJSON());
const viewportFit = (page) => page.locator('meta[name="viewport"]').getAttribute('content');

test.describe('safe areas on a phone', () => {
  test.use({ viewport: { width: 390, height: 844 }, hasTouch: true });

  // Where the app bar's search row, the bottom navigation and a pushed session's reply box end up
  // with an iPhone's insets reported.
  async function placed(page) {
    await reported(page, IPHONE_INSETS);
    await page.locator('.search-open').filter({ visible: true }).first().click();
    const search = await rect(page.locator('#search'));
    await page.locator('#search-close').click();
    const nav = await rect(page.locator('#nav .nav-item').first());
    const v = await open(page, IDLE);
    const reply = await rect(v.locator('form.reply'));
    return { searchTop: search.top, navBottom: nav.bottom, replyBottom: reply.bottom };
  }

  test('installed on an iPhone, the app bar, the bottom navigation and the reply box keep clear of the notch and the home indicator', async ({ page }) => {
    await on(page, AS.iosInstalled);
    await expect(page.locator('html')).toHaveAttribute('data-platform', 'ios');
    await expect(page.locator('html')).toHaveAttribute('data-standalone', 'true');
    expect(await viewportFit(page)).toContain('viewport-fit=cover');
    const p = await placed(page);
    expect(p.searchTop).toBeGreaterThanOrEqual(IPHONE_INSETS.top);
    expect(p.navBottom).toBeLessThanOrEqual(844 - IPHONE_INSETS.bottom + 0.5);
    expect(p.replyBottom).toBeLessThanOrEqual(844 - IPHONE_INSETS.bottom + 0.5);
  });

  for (const [name, as, platform] of [
    ['in Safari on an iPhone', AS.iosSafari, 'ios'],
    ['installed on Android', AS.androidInstalled, 'android'],
    ['on Windows', AS.windows, 'windows'],
    ['on a platform nobody recognises', AS.unknown, 'unknown'],
  ]) {
    test(`${name}, the page leaves the safe areas to the browser and pads for none`, async ({ page }) => {
      await on(page, as);
      await expect(page.locator('html')).toHaveAttribute('data-platform', platform);
      expect(await viewportFit(page)).not.toContain('viewport-fit');
      const p = await placed(page);
      // Exactly where it is with nothing reported: the app bar's row at its own padding, the
      // navigation and the reply box down to the bottom edge.
      expect(p.searchTop).toBeLessThan(IPHONE_INSETS.top);
      expect(p.navBottom).toBeGreaterThan(844 - IPHONE_INSETS.bottom);
      expect(p.replyBottom).toBeGreaterThan(844 - IPHONE_INSETS.bottom);
    });
  }

  test('installed on an iPhone a tap is a tap, with no wait for a double tap; elsewhere the browser decides', async ({ page, browser }) => {
    await on(page, AS.iosInstalled);
    expect(await page.evaluate(() => getComputedStyle(document.documentElement).touchAction)).toBe('manipulation');
    const other = await browser.newPage({ viewport: { width: 390, height: 844 }, hasTouch: true });
    await on(other, AS.iosSafari);
    expect(await other.evaluate(() => getComputedStyle(document.documentElement).touchAction)).toBe('auto');
    await other.close();
  });
});

test.describe('safe areas on an iPad or an iPhone on its side', () => {
  test.use({ viewport: { width: 1024, height: 768 }, hasTouch: true });

  async function placed(page, insets) {
    await reported(page, insets);
    const v = await open(page, IDLE);
    const bar = await rect(page.locator('#appbar'));
    const rail = await rect(page.locator('#nav .nav-item').first());
    const reply = await rect(v.locator('.reply-slot'));
    return { bar, rail, reply };
  }

  test('installed on an iPad, which says it is a Mac, the app bar, the rail and the reply box keep clear of the status bar and the home indicator', async ({ page }) => {
    await on(page, AS.ipadosInstalled);
    await expect(page.locator('html')).toHaveAttribute('data-platform', 'ipados');
    const { bar, reply } = await placed(page, IPAD_INSETS);
    expect(bar.top).toBeGreaterThanOrEqual(IPAD_INSETS.top);
    expect(reply.bottom).toBeLessThanOrEqual(768 - IPAD_INSETS.bottom + 0.5);
  });

  test('installed on an iPhone on its side, the rail and the reply box keep clear of the notch', async ({ page }) => {
    await on(page, AS.iosInstalled);
    const { rail, reply } = await placed(page, LANDSCAPE_INSETS);
    expect(rail.left).toBeGreaterThanOrEqual(LANDSCAPE_INSETS.left);
    expect(reply.right).toBeLessThanOrEqual(1024 - LANDSCAPE_INSETS.right + 0.5);
    expect(reply.bottom).toBeLessThanOrEqual(768 - LANDSCAPE_INSETS.bottom + 0.5);
  });

  test('on a Mac, the page leaves the safe areas to the browser, and the same reported insets move nothing', async ({ page }) => {
    await on(page, AS.macos);
    expect(await viewportFit(page)).not.toContain('viewport-fit');
    const { bar, rail, reply } = await placed(page, LANDSCAPE_INSETS);
    expect(bar.top).toBe(0);
    expect(rail.left).toBeLessThan(LANDSCAPE_INSETS.left);
    expect(reply.bottom).toBeGreaterThan(768 - LANDSCAPE_INSETS.bottom);
  });
});

test.describe('the system\'s Back on a phone', () => {
  test.use({ viewport: { width: 390, height: 844 }, hasTouch: true });

  const delegateRow = (page) => view(page, DELEGATING).locator('.delegate[data-id="demo-agent-suite"]');

  // From a page before the app, so leaving it has somewhere to go; then the delegate opened in its
  // session, pushed over the list, from the session's Delegates tab.
  async function openDelegate(page, as) {
    await page.goto('icon.svg');
    await on(page, as, '&delegates=1');
    const v = await open(page, DELEGATING);
    await v.getByRole('tab', { name: 'Delegates' }).click();
    await delegateRow(page).locator('.delegate-summary').click();
    await expect(delegateRow(page).locator('.delegate-detail')).toHaveAttribute('open', '');
    return v;
  }

  test('Back from a delegate goes to its session, then to the list, then out of the app', async ({ page }) => {
    const v = await openDelegate(page, AS.androidInstalled);
    await page.goBack();
    await expect(delegateRow(page).locator('.delegate-detail')).not.toHaveAttribute('open', '');
    await expect(v).toBeVisible();
    await page.goBack();
    await expect(v).toBeHidden();
    await expect(page.locator('#list-pane')).toBeVisible();
    await page.goBack();
    await expect(page).toHaveURL(/icon\.svg$/);
  });

  test('a delegate closed by hand takes its step with it, opened again it is a step again, and the header\'s Back over one is one press to the list', async ({ page }) => {
    const v = await openDelegate(page, AS.androidInstalled);
    const detail = delegateRow(page).locator('.delegate-detail');
    const summary = delegateRow(page).locator('.delegate-summary');
    // Closed by hand, then opened again: one step, not two.
    await summary.click();
    await expect(detail).not.toHaveAttribute('open', '');
    await expect.poll(() => page.evaluate(() => history.state?.delegate ?? null)).toBeNull();
    await summary.click();
    await expect(detail).toHaveAttribute('open', '');
    await page.goBack();
    await expect(detail).not.toHaveAttribute('open', '');
    await expect(v).toBeVisible();
    // The header's Back is to the list, delegate or not, and Back from the list leaves.
    await summary.click();
    await expect(detail).toHaveAttribute('open', '');
    await v.locator('.session-header .back').click();
    await expect(v).toBeHidden();
    await expect(page.locator('#list-pane')).toBeVisible();
    await page.goBack();
    await expect(page).toHaveURL(/icon\.svg$/);
  });

  test('the header\'s Back over a delegate closes it, so Forward to its session shows it closed and Back from there is the list', async ({ page }) => {
    const v = await openDelegate(page, AS.androidInstalled);
    const detail = delegateRow(page).locator('.delegate-detail');
    await v.locator('.session-header .back').click();
    await expect(page.locator('#list-pane')).toBeVisible();
    await page.goForward();
    await expect(v).toBeVisible();
    await expect(detail).not.toHaveAttribute('open', '');
    await page.goBack();
    await expect(v).toBeHidden();
    await expect(page.locator('#list-pane')).toBeVisible();
  });

  test('widened from a phone to three panes over a delegate, the delegate\'s step goes and the session stays', async ({ page }) => {
    const v = await openDelegate(page, AS.androidInstalled);
    const hash = new URL(page.url()).hash;
    // Both width queries change at once: one step back, not two.
    await page.setViewportSize({ width: 1440, height: 900 });
    await expect.poll(() => page.evaluate(() => history.state?.delegate ?? null)).toBeNull();
    await page.waitForTimeout(300);
    expect(new URL(page.url()).hash).toBe(hash);
    await expect(v).toBeVisible();
  });

  test('installed on an iPhone, a swipe in from the left edge goes from a delegate to its session, then to the list', async ({ page }) => {
    const v = await openDelegate(page, AS.iosInstalled);
    // A swipe as iOS delivers it: a touch from the edge, moved most of the way across.
    const swipe = () => page.evaluate(() => {
      const fire = (type, x) => {
        const e = new Event(type, { bubbles: true });
        const t = { identifier: 7, clientX: x, clientY: 400 };
        Object.defineProperty(e, 'touches', { value: type === 'touchend' ? [] : [t] });
        Object.defineProperty(e, 'changedTouches', { value: [t] });
        document.body.dispatchEvent(e);
      };
      fire('touchstart', 6);
      fire('touchend', 220);
    });
    await swipe();
    await expect(delegateRow(page).locator('.delegate-detail')).not.toHaveAttribute('open', '');
    await expect(v).toBeVisible();
    await swipe();
    await expect(v).toBeHidden();
    await expect(page.locator('#list-pane')).toBeVisible();
  });
});

test.describe('a platform nobody recognises', () => {
  // Every element's box and the styles that draw it, in document order.
  const drawn = (page) => page.evaluate(() => {
    const props = ['display', 'visibility', 'color', 'background-color', 'font-family', 'font-size', 'font-weight', 'font-size-adjust',
      'padding', 'margin', 'border', 'touch-action', '--ab-inline-mono-x', '--ab-inline-mono-scale', '--safe-top', '--safe-bottom'];
    return [...document.querySelectorAll('*')].map((el) => {
      const s = getComputedStyle(el);
      const r = el.getBoundingClientRect();
      return `${el.tagName}.${el.getAttribute('class') ?? ''} ${[r.x, r.y, r.width, r.height].join(',')} ${props.map((p) => s.getPropertyValue(p)).join('|')}`;
    });
  });
  // Pixels that differ by more than anti-aliasing does between two draws of the same page.
  const differing = (page, a, b) => page.evaluate(async ([a, b]) => {
    const load = async (b64) => createImageBitmap(await (await fetch(`data:image/png;base64,${b64}`)).blob());
    const [ia, ib] = await Promise.all([load(a), load(b)]);
    if (ia.width !== ib.width || ia.height !== ib.height) { return Infinity; }
    const pixels = (img) => {
      const c = Object.assign(document.createElement('canvas'), { width: img.width, height: img.height });
      const g = c.getContext('2d');
      g.drawImage(img, 0, 0);
      return g.getImageData(0, 0, img.width, img.height).data;
    };
    const [pa, pb] = [pixels(ia), pixels(ib)];
    let n = 0;
    for (let i = 0; i < pa.length; i += 4) {
      if (Math.max(Math.abs(pa[i] - pb[i]), Math.abs(pa[i + 1] - pb[i + 1]), Math.abs(pa[i + 2] - pb[i + 2])) > 48) { n++; }
    }
    return n;
  }, [a.toString('base64'), b.toString('base64')]);

  for (const viewport of [{ width: 1440, height: 900 }, { width: 390, height: 844 }]) {
    test(`at ${viewport.width} px it looks as it does on Windows: every box and style the same, and the same picture`, async ({ browser }) => {
      const look = async (as) => {
        // Reduced motion stops every glyph's animation (ui.md §9) without the style element a
        // screenshot's own animation stop would add, which the page's policy refuses.
        const page = await browser.newPage({ viewport, reducedMotion: 'reduce' });
        await page.clock.setFixedTime(new Date('2026-11-02T18:00:00Z'));
        await on(page, as);
        await expect(page.locator('html')).toHaveAttribute('data-platform', as === AS.unknown ? 'unknown' : 'windows');
        const v = await open(page, IDLE);
        if (viewport.width >= 1280) { await expect(v.locator('.tools-panel li.tool').first()).toBeVisible(); }
        // The page's own, before the screenshot's machinery adds a style element the policy refuses.
        expect(await page.evaluate(() => window.__cspViolations)).toEqual([]);
        // Once it has settled: the tools and the rows drawn as they arrive, two readings alike.
        let boxes = await drawn(page);
        for (let i = 0; i < 20; i++) {
          await page.waitForTimeout(150);
          const again = await drawn(page);
          if (JSON.stringify(again) === JSON.stringify(boxes)) { break; }
          boxes = again;
        }
        const png = await page.screenshot({ caret: 'initial' });
        await page.close();
        return { boxes, png };
      };
      const windows = await look(AS.windows);
      const unknown = await look(AS.unknown);
      expect(unknown.boxes).toEqual(windows.boxes);
      const page = await browser.newPage();
      // A glyph's edge drawn a shade apart, at most, as between two draws of one page.
      expect(await differing(page, windows.png, unknown.png)).toBeLessThanOrEqual(20);
      await page.close();
    });
  }

  test.describe('and behaves as it does on Windows', () => {
    test.use({ viewport: { width: 1440, height: 900 } });

    test('the hints say Ctrl, Ctrl+K focuses search, Ctrl+Enter sends, and the page pads for no safe area', async ({ page }) => {
      await on(page, AS.unknown, '&answerDelay=50');
      await expect(page.locator('html')).toHaveAttribute('data-platform', 'unknown');
      await expect(page.locator('html')).toHaveAttribute('data-standalone', 'false');
      expect(await viewportFit(page)).not.toContain('viewport-fit');
      const v = await open(page, IDLE);
      await expect(v.getByRole('button', { name: 'Send' })).toHaveAttribute('title', 'Send (Ctrl+Enter)');
      await expect(page.locator('#search')).toHaveAttribute('title', 'Search sessions (Ctrl+K)');
      const input = v.getByRole('textbox', { name: 'Reply' });
      await input.fill('From a platform nobody knows');
      await input.press('Control+Enter');
      await expect(input).toHaveValue('');
      await expect.poll(() => page.evaluate(() => window.agentBridgeDemo.host.typedReplies.map((r) => r.said))).toContain('From a platform nobody knows');
      await page.keyboard.press('Control+k');
      await expect(page.locator('#search')).toBeFocused();
    });
  });
});
