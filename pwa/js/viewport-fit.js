// The phone session pane's height on an installed iPhone (#1620, #1057). iOS standalone can leave
// the layout viewport (innerHeight) a status bar short of the screen after launch or once the
// keyboard closes, so the fixed pane ended early and the reply box floated above the screen's edge
// with an empty band under it. This reads how far short the page is and the keyboard's state, and
// sets them on <html> for app.css: --viewport-short (px the pane is extended by) and
// data-keyboard="up" (no home indicator to keep clear of, so the box sits 12 above the keyboard bar).

// A shrunken visual viewport smaller than this is the keyboard, not a pinch or the URL bar.
const KEYBOARD_MIN = 100;
const SHORT_MAX = 60;

export function measureViewport({ innerWidth, innerHeight, screen, visualViewport }) {
  const keyboardUp = Boolean(visualViewport) && innerHeight - visualViewport.height > KEYBOARD_MIN;
  // The screen's own height for this orientation: iOS reports screen in portrait terms.
  const screenHeight = innerWidth > innerHeight ? Math.min(screen.width, screen.height) : Math.max(screen.width, screen.height);
  // A status bar's worth at most: a bigger gap is a windowed iPad (Split View, Stage Manager), not this bug.
  const short = keyboardUp ? 0 : Math.min(SHORT_MAX, Math.max(0, Math.round(screenHeight - innerHeight)));
  return { short, keyboardUp };
}

export function fitViewport({ win = globalThis.window, root = globalThis.document?.documentElement } = {}) {
  if (!win || !root) { return () => {}; }
  const apply = () => {
    const { short, keyboardUp } = measureViewport(win);
    root.style.setProperty('--viewport-short', `${short}px`);
    if (keyboardUp) { root.dataset.keyboard = 'up'; } else { delete root.dataset.keyboard; }
  };
  // visualViewport's resize can fire before layout settles, so measure again on the next frame.
  const soon = () => { apply(); win.requestAnimationFrame?.(apply); };
  win.addEventListener('resize', soon);
  win.visualViewport?.addEventListener('resize', soon);
  apply();
  return () => {
    win.removeEventListener('resize', soon);
    win.visualViewport?.removeEventListener('resize', soon);
  };
}
