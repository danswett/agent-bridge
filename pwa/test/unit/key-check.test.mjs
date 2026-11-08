import { test } from 'node:test';
import assert from 'node:assert/strict';
import { KEPT, describeFocus, entryLine, isEditable, keep, keyEntry, modeLine, readDisplayMode, report } from '../../js/key-check.js';

const row = { tagName: 'BUTTON', className: 'row-main', id: 'row-1' };
const reply = { tagName: 'TEXTAREA', className: 'reply-input', id: '' };
const body = { tagName: 'BODY', className: '', id: '' };
const optionUp = { key: 'ArrowUp', code: 'ArrowUp', altKey: true, ctrlKey: false, metaKey: false, shiftKey: false };

test('an Option+Up on a session row reads its key, code, every modifier, the focus and whether the app used it', () => {
  const entry = keyEntry(optionUp, { focus: row, handled: true });
  assert.equal(entryLine(entry, 3),
    '3. key "ArrowUp", code "ArrowUp" \u00b7 Option (altKey) yes \u00b7 Control (ctrlKey) no \u00b7 Command (metaKey) no \u00b7 Shift (shiftKey) no'
    + ' \u00b7 focus on a session row (button.row-main#row-1) \u00b7 used by the app: yes');
});

test('a key that reached the page with nothing focused says so, and a key not yet known to be used says that', () => {
  const line = entryLine(keyEntry({ ...optionUp, key: 'Unidentified', code: '' }, { focus: body }), 1);
  assert.match(line, /key "Unidentified", code \(empty\)/);
  assert.match(line, /focus on nothing \(the page itself\) \(body\)/);
  assert.match(line, /used by the app: not known$/);
  assert.match(entryLine(keyEntry(optionUp, { focus: null }), 1), /focus on nothing \(\(none\)\)/);
});

test('a typing key in a text box is never shown, only that one was; named keys and shortcuts there are', () => {
  const typed = keyEntry({ key: 'h', code: 'KeyH', shiftKey: false }, { focus: reply });
  assert.deepEqual([typed.key, typed.code], [null, null]);
  assert.match(entryLine(typed, 1), /key a typing key \(not shown\), code \(not shown\)/);
  // An input method's keys, a dead key, AltGr's characters and Option's: typing too.
  for (const e of [
    { key: 'Process', code: 'KeyA', isComposing: true },
    { key: 'Unidentified', code: 'KeyS' },
    { key: 'Dead', code: 'KeyE', altKey: true },
    { key: '@', code: 'KeyQ', ctrlKey: true, altKey: true },
    { key: '\u20ac', code: 'KeyE', getModifierState: (m) => m === 'AltGraph' },
    { key: '\u2122', code: 'Digit2', altKey: true },
  ]) {
    assert.deepEqual(Object.values(keyEntry(e, { focus: reply })).slice(0, 2), [null, null], JSON.stringify(e));
  }
  assert.equal(keyEntry({ key: 'ArrowUp', code: 'ArrowUp', isComposing: true }, { focus: reply }).key, 'ArrowUp');
  assert.equal(keyEntry(optionUp, { focus: reply }).key, 'ArrowUp');
  assert.equal(keyEntry({ key: 'F10', code: 'F10', shiftKey: true }, { focus: reply }).key, 'F10');
  assert.equal(keyEntry({ key: 'k', code: 'KeyK', metaKey: true }, { focus: reply }).key, 'k');
  assert.equal(keyEntry({ key: 'k', code: 'KeyK', ctrlKey: true }, { focus: reply }).key, 'k');
  assert.equal(keyEntry({ key: 'x', code: 'KeyX' }, { focus: { tagName: 'INPUT', type: 'password', className: '', id: '' } }).key, null);
  // Outside a text box a letter is a shortcut, shown as it came.
  assert.equal(keyEntry({ key: 'h', code: 'KeyH' }, { focus: row }).key, 'h');
  assert.match(entryLine(keyEntry({ key: ' ', code: 'Space' }, { focus: row }), 1), /key "Space"/);
});

test('focus is named by what the element is, never by its words', () => {
  assert.deepEqual(describeFocus({ tagName: 'BUTTON', className: 'button secondary key-check-button', id: '', textContent: 'secret title' }),
    { plain: 'the practice button', tech: 'button.button.secondary.key-check-button' });
  assert.equal(describeFocus({ tagName: 'INPUT', type: 'search', className: '', id: 'search' }).plain, 'a text box');
  assert.equal(describeFocus({ tagName: 'INPUT', type: 'checkbox', className: '', id: '' }).plain, 'an input element');
  assert.equal(describeFocus({ tagName: 'DIV', className: '', id: '', isContentEditable: true }).plain, 'a text box');
  assert.equal(describeFocus({ tagName: 'A', className: '', id: '' }).plain, 'a link');
  assert.ok(!isEditable({ tagName: 'BUTTON' }));
});

test('how the page is open says installed or browser, the platform and the raw display mode', () => {
  assert.equal(modeLine({ displayMode: 'standalone', iosStandalone: true, platform: 'ipados' }),
    'Open as the installed app on iPadOS (display-mode: standalone, navigator.standalone: true)');
  assert.equal(modeLine({ displayMode: 'browser', platform: 'windows' }), 'Open in the browser on Windows (display-mode: browser)');
  // A browser tab in fullscreen matches fullscreen: not installed by that alone.
  assert.equal(modeLine({ displayMode: 'fullscreen', platform: 'windows' }), 'Open in the browser on Windows (display-mode: fullscreen)');
  assert.equal(modeLine({ displayMode: null, iosStandalone: true, platform: 'ios' }),
    'Open as the installed app on iOS (display-mode: not reported, navigator.standalone: true)');
  const matching = (mode) => (q) => ({ matches: q === `(display-mode: ${mode})` });
  assert.equal(readDisplayMode(matching('standalone')), 'standalone');
  assert.equal(readDisplayMode(matching('minimal-ui')), 'minimal-ui');
  assert.equal(readDisplayMode(() => ({ matches: false })), null);
  assert.equal(readDisplayMode(null), null);
});

test('the report lists how the page is open, then each key oldest first, numbered from the first pressed', () => {
  let entries = [];
  for (let i = 0; i < KEPT + 2; i++) { entries = keep(entries, keyEntry({ ...optionUp, key: `F${i}` }, { focus: row })); }
  assert.equal(entries.length, KEPT);
  const lines = report('Open in the browser on macOS', entries, KEPT + 2).split('\n');
  assert.equal(lines[0], 'Open in the browser on macOS');
  assert.match(lines[1], /^3\. key "F2"/);
  assert.match(lines.at(-1), new RegExp(`^${KEPT + 2}\\. key "F${KEPT + 1}"`));
  assert.equal(report('Open', []), 'Open\nNo keys pressed yet.');
});
