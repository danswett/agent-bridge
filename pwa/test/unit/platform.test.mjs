import { test } from 'node:test';
import assert from 'node:assert/strict';
import { detectPlatform, detectStandalone, edgeSwipeBack, installedOnApple, isSearchKey, isSendKey, markPlatform, shortcutHints } from '../../js/platform.js';

const UA = {
  chromeWindows: 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36',
  chromeMac: 'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36',
  chromeAndroid: 'Mozilla/5.0 (Linux; Android 10; K) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Mobile Safari/537.36',
  chromeOs: 'Mozilla/5.0 (X11; CrOS x86_64 14541.0.0) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36',
  chromeLinux: 'Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36',
  safariMac: 'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Safari/605.1.15',
  safariIPhone: 'Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1',
  firefoxLinux: 'Mozilla/5.0 (X11; Linux x86_64; rv:132.0) Gecko/20100101 Firefox/132.0',
};

test('Chromium is placed by its client hints, whatever its user agent string says', () => {
  // Chromium's reduced user agent string says less than the hint; the hint wins.
  for (const [hint, ua, want] of [
    ['Windows', UA.chromeWindows, 'windows'],
    ['macOS', UA.chromeMac, 'macos'],
    ['Android', UA.chromeAndroid, 'android'],
    ['Chrome OS', UA.chromeOs, 'chromeos'],
    ['Linux', UA.chromeLinux, 'linux'],
    ['Windows', 'Mozilla/5.0 (Something else)', 'windows'],
  ]) {
    assert.equal(detectPlatform({ userAgentData: { platform: hint }, userAgent: ua }), want, hint);
  }
});

test('Safari on a Mac is macOS, and a "Mac" with a touch screen is an iPad asking for the desktop page', () => {
  assert.equal(detectPlatform({ userAgent: UA.safariMac, maxTouchPoints: 0 }), 'macos');
  assert.equal(detectPlatform({ userAgent: UA.safariMac, maxTouchPoints: 5 }), 'ipados');
});

test('Safari on an iPhone is iOS, though its user agent says "like Mac OS X"', () => {
  assert.equal(detectPlatform({ userAgent: UA.safariIPhone, maxTouchPoints: 5 }), 'ios');
});

test('Firefox on Linux, which has no client hints, is Linux; and Android is never taken for Linux', () => {
  assert.equal(detectPlatform({ userAgent: UA.firefoxLinux }), 'linux');
  assert.equal(detectPlatform({ userAgent: UA.chromeAndroid }), 'android');
});

test('a user agent nothing recognises, or a client hint that says nothing, is unknown', () => {
  assert.equal(detectPlatform({ userAgent: 'Mozilla/5.0 (Unrecognised OS) SomeBrowser/1.0' }), 'unknown');
  assert.equal(detectPlatform({ userAgentData: { platform: '' }, userAgent: '' }), 'unknown');
  assert.equal(detectPlatform({ userAgentData: { platform: 'Unknown' } }), 'unknown');
  assert.equal(detectPlatform(), 'unknown');
});

test('installed is display-mode standalone, or iOS\'s own flag where that says nothing', () => {
  const mode = (on) => (q) => ({ matches: on && q === '(display-mode: standalone)' });
  assert.equal(detectStandalone({ matchMedia: mode(true) }), true);
  assert.equal(detectStandalone({ matchMedia: mode(false), standalone: true }), true);
  assert.equal(detectStandalone({ matchMedia: mode(false), standalone: false }), false);
  assert.equal(detectStandalone({ matchMedia: () => { throw new Error('no such query'); } }), false);
});

function fakePage(content = 'width=device-width, initial-scale=1, viewport-fit=cover') {
  const viewport = { content };
  return { root: { dataset: {} }, doc: { querySelector: (s) => (s === 'meta[name="viewport"]' ? viewport : null) }, viewport };
}

test('the page names its platform and whether it is installed on the root element', () => {
  const { root, doc } = fakePage();
  markPlatform({ root, doc, info: { platform: 'android', standalone: true } });
  assert.deepEqual(root.dataset, { platform: 'android', standalone: 'true' });
});

test('only an app installed on iOS or iPadOS keeps drawing under the notch; everywhere else the browser keeps the page clear', () => {
  for (const [info, cover] of [
    [{ platform: 'ios', standalone: true }, true],
    [{ platform: 'ipados', standalone: true }, true],
    [{ platform: 'ios', standalone: false }, false],
    [{ platform: 'android', standalone: true }, false],
    [{ platform: 'macos', standalone: true }, false],
    [{ platform: 'windows', standalone: false }, false],
    [{ platform: 'unknown', standalone: true }, false],
  ]) {
    const { root, doc, viewport } = fakePage();
    markPlatform({ root, doc, info });
    assert.equal(installedOnApple(info), cover, JSON.stringify(info));
    assert.equal(viewport.content, cover ? 'width=device-width, initial-scale=1, viewport-fit=cover' : 'width=device-width, initial-scale=1', JSON.stringify(info));
  }
});

test('hints name ⌘ on Apple\'s platforms and Ctrl everywhere else, unknown included', () => {
  for (const p of ['macos', 'ipados', 'ios']) {
    assert.deepEqual(shortcutHints(p), { send: '⌘↩', search: '⌘K', searchKeys: 'Meta+K' }, p);
  }
  for (const p of ['windows', 'android', 'chromeos', 'linux', 'unknown']) {
    assert.deepEqual(shortcutHints(p), { send: 'Ctrl+Enter', search: 'Ctrl+K', searchKeys: 'Control+K' }, p);
  }
});

test('Send and search take either modifier on every platform, so a platform taken for another still works', () => {
  assert.equal(isSearchKey({ key: 'k', ctrlKey: true }), true);
  assert.equal(isSearchKey({ key: 'K', metaKey: true }), true);
  assert.equal(isSearchKey({ key: 'k', ctrlKey: true, shiftKey: true }), false);
  assert.equal(isSearchKey({ key: 'k', metaKey: true, altKey: true }), false);
  assert.equal(isSearchKey({ key: 'k' }), false);
  assert.equal(isSendKey({ key: 'Enter', ctrlKey: true }), true);
  assert.equal(isSendKey({ key: 'Enter', metaKey: true }), true);
  assert.equal(isSendKey({ key: 'Enter' }), false);
});

function fakeTarget() {
  const on = {};
  return { on, addEventListener: (t, fn) => { on[t] = fn; }, removeEventListener: (t) => { delete on[t]; } };
}
const touch = (x, y, identifier = 1) => ({ clientX: x, clientY: y, identifier });
function swipe(target, from, to) {
  target.on.touchstart({ touches: [touch(...from)], target: null });
  target.on.touchend({ touches: [], changedTouches: [touch(...to)], target: null });
}

test('a swipe in from the left edge goes back once; one from further in, too short, or mostly vertical does not', () => {
  const t = fakeTarget();
  let backs = 0;
  edgeSwipeBack(t, () => { backs++; });
  swipe(t, [8, 300], [140, 310]);
  assert.equal(backs, 1);
  swipe(t, [60, 300], [220, 300]);
  swipe(t, [8, 300], [40, 300]);
  swipe(t, [8, 300], [90, 420]);
  assert.equal(backs, 1);
  // Two fingers are a pinch, not a swipe.
  t.on.touchstart({ touches: [touch(8, 300), touch(200, 300, 2)], target: null });
  t.on.touchend({ touches: [], changedTouches: [touch(140, 300)], target: null });
  assert.equal(backs, 1);
});
