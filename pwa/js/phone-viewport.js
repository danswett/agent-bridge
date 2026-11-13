// iOS's keyboard changes the visual viewport, not necessarily the layout viewport.
// Use its bounds once; subtracting a keyboard height from them would lift the reply twice.
import { markPlatform, readPlatform } from './platform.js';

export const installedPhone = (root = globalThis.document?.documentElement) =>
  root?.dataset?.platform === 'ios' && root?.dataset?.standalone === 'true';

export function phoneMedia(win, query) {
  const width = win.matchMedia(query), mode = win.matchMedia('(display-mode: standalone)');
  const root = win.document?.documentElement;
  const sync = () => { if (root?.dataset.platform === 'ios') { markPlatform({ root, doc: win.document, info: readPlatform(win) }); } };
  mode.addEventListener('change', sync);
  return {
    get matches() { return installedPhone(root) || width.matches; },
    addEventListener(type, fn) { width.addEventListener(type, fn); mode.addEventListener(type, fn); },
    removeEventListener(type, fn) { width.removeEventListener(type, fn); mode.removeEventListener(type, fn); },
    destroy() { mode.removeEventListener('change', sync); },
  };
}

export function watchPhoneViewport(win = window, root = document.documentElement) {
  if (!installedPhone(root) || !win.visualViewport) { return () => {}; }
  const vv = win.visualViewport;
  let pending = 0;
  const draw = () => {
    pending = 0;
    // Pinch zoom is not a keyboard: keep the unzoomed layout available for panning.
    const unzoomed = vv.scale === 1;
    root.style.setProperty('--phone-view-top', `${unzoomed ? vv.offsetTop : 0}px`);
    root.style.setProperty('--phone-view-height', `${unzoomed ? vv.height : win.innerHeight}px`);
    root.toggleAttribute('data-phone-keyboard', unzoomed && vv.height < win.innerHeight - 1);
    win.dispatchEvent(new win.Event('phone-viewport-change'));
  };
  const changed = () => {
    if (!pending) { pending = win.requestAnimationFrame(draw); }
  };
  for (const event of ['resize', 'scroll']) { vv.addEventListener(event, changed, { passive: true }); }
  for (const event of ['resize', 'orientationchange']) { win.addEventListener(event, changed, { passive: true }); }
  win.document.addEventListener('visibilitychange', changed);
  draw();
  return () => {
    win.cancelAnimationFrame(pending);
    for (const event of ['resize', 'scroll']) { vv.removeEventListener(event, changed); }
    for (const event of ['resize', 'orientationchange']) { win.removeEventListener(event, changed); }
    win.document.removeEventListener('visibilitychange', changed);
    for (const name of ['--phone-view-top', '--phone-view-height']) { root.style.removeProperty(name); }
    root.removeAttribute('data-phone-keyboard');
  };
}
