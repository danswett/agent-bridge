// Settings > Keyboard check (#1209): while on, every keydown anywhere in the app is listed here
// (key-check.js words it), on the practice targets or a real session row. It only watches: it never
// prevents a key or moves focus. Nothing outlives the page or is sent; Copy puts the list on this
// device's clipboard for the person to paste.
import { keep, keyEntry, entryLine, modeLine, readDisplayMode, report } from '../key-check.js';
import { copyText } from '../markdown.js';
import { platform as currentPlatform } from '../platform.js';
import { h } from './dom.js';

export function mountKeyCheck({ doc = document, win = window, platform = currentPlatform().platform } = {}) {
  const $ = (id) => doc.getElementById(id);
  const els = {
    mode: $('key-check-mode'), toggle: $('key-check-toggle'), copy: $('key-check-copy'), clear: $('key-check-clear'),
    targets: $('key-check-targets'), list: $('key-check-list'), status: $('key-check-status'),
  };
  if (!els.toggle || !els.list) { return null; }
  let entries = [];
  let total = 0;
  let on = false;

  const mode = () => modeLine({
    displayMode: readDisplayMode(typeof win.matchMedia === 'function' ? (q) => win.matchMedia(q) : null),
    iosStandalone: typeof win.navigator?.standalone === 'boolean' ? win.navigator.standalone : undefined,
    platform,
  });
  const draw = () => {
    els.list.replaceChildren(...entries.map((e, i) => h('li', { class: 'key-check-entry' }, entryLine(e, total - i))));
    for (const el of [els.list, els.copy, els.clear]) { el.hidden = entries.length === 0; }
  };
  const say = (text) => { els.status.textContent = text; };

  // Capture, on the window: seen before any handler of the app's can stop it going further.
  const onKey = (e) => {
    const entry = keyEntry(e, { focus: doc.activeElement });
    entries = keep(entries, entry);
    total += 1;
    draw();
    // Whether the app used it is known once every handler has had it.
    win.setTimeout(() => { entry.handled = e.defaultPrevented === true; draw(); }, 0);
  };
  const set = (next) => {
    on = next;
    // The label says what a press does; no aria-pressed beside it, which would read "Stop, pressed".
    els.toggle.textContent = on ? 'Stop keyboard check' : 'Start keyboard check';
    els.targets.hidden = !on;
    if (on) {
      els.mode.textContent = mode();
      win.addEventListener('keydown', onKey, true);
      say('Keyboard check on. Keys you press anywhere in the app are listed here.');
    } else {
      win.removeEventListener('keydown', onKey, true);
      say('Keyboard check off.');
    }
  };

  els.mode.textContent = mode();
  els.toggle.addEventListener('click', () => set(!on));
  els.clear.addEventListener('click', () => {
    entries = [];
    total = 0;
    draw();
    say('Cleared.');
    els.toggle.focus();
  });
  els.copy.addEventListener('click', async () => {
    const copied = await copyText(report(mode(), entries, total), doc);
    say(copied ? `Copied ${entries.length === 1 ? '1 key' : `${entries.length} keys`}.` : "Couldn't copy. Select the list and copy it instead.");
  });
  draw();
  return { get on() { return on; }, stop: () => { if (on) { set(false); } } };
}
