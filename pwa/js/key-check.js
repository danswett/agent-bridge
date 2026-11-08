// The keyboard check (#1209): what a hardware keyboard's keys reach this app as, key by key (key,
// code, modifiers, what had focus, whether the app used it), in words to read back or paste into an
// issue: Alt+Up/Down on a session row did nothing on an iPad's Magic Keyboard. Pure; drawn by
// ui/key-check-view.js. Kept in memory only, never sent; a key that could be typing in a text box
// (a reply being written) is never shown, only that one was.

// The most entries shown, newest first.
export const KEPT = 50;

const yesNo = (v) => (v ? 'yes' : 'no');

// Whether an element takes typing: a text field, a text area or anything content-editable.
export function isEditable(el) {
  if (!el || typeof el !== 'object') { return false; }
  if (el.isContentEditable) { return true; }
  const tag = String(el.tagName ?? '').toLowerCase();
  if (tag === 'textarea') { return true; }
  if (tag !== 'input') { return false; }
  const type = String(el.type ?? 'text').toLowerCase();
  return !['button', 'checkbox', 'color', 'file', 'hidden', 'image', 'radio', 'range', 'reset', 'submit'].includes(type);
}

// The element with focus, by what it is and never by what it says (a session row's name is the
// session's title): { plain, tech }, where tech is tag.class#id as a developer would look it up.
export function describeFocus(el) {
  if (!el || typeof el !== 'object' || !el.tagName) { return { plain: 'nothing', tech: '(none)' }; }
  const tag = String(el.tagName).toLowerCase();
  const classes = String(el.className?.baseVal ?? el.className ?? '').split(/\s+/).filter(Boolean);
  const id = typeof el.id === 'string' && el.id ? `#${el.id}` : '';
  const tech = `${tag}${classes.slice(0, 3).map((c) => `.${c}`).join('')}${id}`;
  const has = (c) => classes.includes(c);
  let plain;
  if (tag === 'body' || tag === 'html') { plain = 'nothing (the page itself)'; }
  else if (has('row-main')) { plain = 'a session row'; }
  else if (has('key-check-button')) { plain = 'the practice button'; }
  else if (has('key-check-box')) { plain = 'the practice box'; }
  else if (isEditable(el)) { plain = 'a text box'; }
  else if (tag === 'button') { plain = 'a button'; }
  else if (tag === 'a') { plain = 'a link'; }
  else if (tag === 'select') { plain = 'a list to choose from'; }
  else { plain = `${/^[aeiou]/.test(tag) ? 'an' : 'a'} ${tag} element`; }
  return { plain, tech };
}

// Keys that name an action rather than type text: shown even in a text box.
const NAMED = new Set([
  'ArrowUp', 'ArrowDown', 'ArrowLeft', 'ArrowRight', 'Enter', 'Tab', 'Escape', 'Backspace', 'Delete', 'Insert',
  'Home', 'End', 'PageUp', 'PageDown', 'Shift', 'Control', 'Alt', 'AltGraph', 'Meta', 'CapsLock', 'ContextMenu',
]);
const named = (key) => NAMED.has(key) || /^F\d{1,2}$/.test(key);

// Whether a key in a text box could be typing, so neither its key nor its code may be shown: anything
// but a named key or a shortcut. An input method's keys (Process, composing), a dead key and AltGr's
// characters (Ctrl and Alt together on Windows and Linux) are typing too.
function typing(e, key) {
  if (e?.isComposing === true || e?.getModifierState?.('AltGraph') === true) { return !named(key); }
  const shortcut = e?.metaKey === true || (e?.ctrlKey === true && e?.altKey !== true);
  return !named(key) && !shortcut;
}

// One keydown, as kept: { key, code, alt, ctrl, meta, shift, focus, handled }. A key that could be
// typing in a text box keeps only that it was one. handled: whether the app used the key (it
// prevented the browser's own action), null until known.
export function keyEntry(e, { focus = null, handled = null } = {}) {
  const key = typeof e?.key === 'string' ? e.key : '';
  const code = typeof e?.code === 'string' ? e.code : '';
  const hidden = isEditable(focus) && typing(e, key);
  const flag = (k) => e?.[k] === true;
  return {
    key: hidden ? null : key, code: hidden ? null : code,
    alt: flag('altKey'), ctrl: flag('ctrlKey'), meta: flag('metaKey'), shift: flag('shiftKey'),
    focus: describeFocus(focus), handled,
  };
}

const quoted = (v) => (v === null ? 'a typing key (not shown)' : v === '' ? '(empty)' : `"${v === ' ' ? 'Space' : v}"`);

// One entry as a line. n: its number, counting from the first key pressed.
export function entryLine(entry, n) {
  const used = entry.handled === null ? 'not known' : yesNo(entry.handled);
  return [
    `${n}. key ${quoted(entry.key)}, code ${entry.code === null ? '(not shown)' : quoted(entry.code)}`,
    `Option (altKey) ${yesNo(entry.alt)}`, `Control (ctrlKey) ${yesNo(entry.ctrl)}`,
    `Command (metaKey) ${yesNo(entry.meta)}`, `Shift (shiftKey) ${yesNo(entry.shift)}`,
    `focus on ${entry.focus.plain} (${entry.focus.tech})`, `used by the app: ${used}`,
  ].join(' \u00b7 ');
}

const PLATFORM_NAMES = Object.freeze({
  windows: 'Windows', macos: 'macOS', ios: 'iOS', ipados: 'iPadOS', android: 'Android', chromeos: 'ChromeOS', linux: 'Linux', unknown: 'an unknown platform',
});

// How the page is open: the display mode matched (readDisplayMode), iOS's navigator.standalone and
// the platform (platform.js). Installed is standalone alone, as platform.js has it: a browser tab
// in fullscreen matches fullscreen too.
export function modeLine({ displayMode = null, iosStandalone = undefined, platform = 'unknown' } = {}) {
  const installed = displayMode === 'standalone' || iosStandalone === true;
  const where = PLATFORM_NAMES[platform] ?? PLATFORM_NAMES.unknown;
  const facts = [`display-mode: ${displayMode ?? 'not reported'}`];
  if (iosStandalone !== undefined) { facts.push(`navigator.standalone: ${iosStandalone === true}`); }
  return `Open ${installed ? 'as the installed app' : 'in the browser'} on ${where} (${facts.join(', ')})`;
}

// The display mode the window matches, of those a web app manifest names, or null for none.
export function readDisplayMode(matchMedia) {
  if (typeof matchMedia !== 'function') { return null; }
  for (const mode of ['standalone', 'fullscreen', 'minimal-ui', 'window-controls-overlay', 'browser']) {
    try { if (matchMedia(`(display-mode: ${mode})`)?.matches) { return mode; } } catch { /* not supported */ }
  }
  return null;
}

// What Copy puts on the clipboard: how the page is open, then each key, oldest first.
// entries: newest first, as kept; total: how many were pressed in all.
export function report(mode, entries, total = entries.length) {
  const lines = [...entries].reverse().map((e, i) => entryLine(e, total - entries.length + i + 1));
  return [mode, ...(lines.length ? lines : ['No keys pressed yet.'])].join('\n');
}

// The entries kept, a new one first.
export const keep = (entries, entry) => [entry, ...entries].slice(0, KEPT);
