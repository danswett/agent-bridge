import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mountKeyboardShortcuts } from '../../js/ui/keyboard-shortcuts.js';

function fixture(enabled = true) {
  const doc = new EventTarget();
  const elements = new Map();
  const element = (id, tagName = 'BUTTON') => {
    const el = new EventTarget();
    Object.assign(el, { tagName, isConnected: true, focus: () => { doc.activeElement = el; } });
    elements.set(id, el);
    return el;
  };
  const opener = element('keyboard-shortcuts-open');
  const close = element('keyboard-shortcuts-close');
  const dialog = element('keyboard-shortcuts-dialog', 'DIALOG');
  element('keyboard-shortcuts-search');
  element('main', 'MAIN');
  dialog.open = false;
  dialog.showModal = () => { dialog.open = true; };
  dialog.close = () => { dialog.open = false; dialog.dispatchEvent(new Event('close')); };
  doc.getElementById = (id) => elements.get(id);
  doc.querySelector = () => dialog.open ? dialog : null;
  opener.focus();
  const key = (props = {}) => {
    const e = new Event('keydown', { cancelable: true });
    Object.assign(e, { key: '?', ...props });
    if (props.handled) { e.preventDefault(); }
    doc.dispatchEvent(e);
    return e;
  };
  mountKeyboardShortcuts({ doc, enabled: () => enabled });
  return { doc, opener, close, dialog, key };
}

test('help stays closed until chosen, and question mark opens it with focus returned on close', () => {
  const { doc, opener, close, dialog, key } = fixture();
  assert.equal(dialog.open, false);
  assert.equal(key().defaultPrevented, true);
  assert.equal(dialog.open, true);
  assert.equal(doc.activeElement, close);
  dialog.close();
  assert.equal(doc.activeElement, opener);
  doc.activeElement = doc.getElementById('main');
  opener.dispatchEvent(new Event('click'));
  assert.equal(dialog.open, true);
  close.dispatchEvent(new Event('click'));
  assert.equal(dialog.open, false);
  assert.equal(doc.activeElement, opener);
});

test('question mark leaves typing, composition and modified keys alone', () => {
  const { doc, dialog, key } = fixture();
  for (const field of [
    { tagName: 'INPUT', type: 'search' }, { tagName: 'TEXTAREA' },
    { tagName: 'SPAN', isContentEditable: true }, { tagName: 'SELECT' },
  ]) {
    doc.activeElement = field;
    assert.equal(key().defaultPrevented, false);
    assert.equal(dialog.open, false);
  }
  doc.activeElement = { tagName: 'BUTTON' };
  for (const props of [{ key: '/' }, { isComposing: true }, { repeat: true }, { ctrlKey: true }, { metaKey: true }, { altKey: true }]) {
    assert.equal(key(props).defaultPrevented, false);
    assert.equal(dialog.open, false);
  }
});

for (const type of ['checkbox', 'radio', 'button']) {
  test(`question mark leaves a focused ${type} input and its focus alone`, () => {
    const { doc, dialog, key } = fixture();
    const field = { tagName: 'INPUT', type };
    doc.activeElement = field;
    const event = key();
    assert.deepEqual({
      prevented: event.defaultPrevented, open: dialog.open, focusedField: doc.activeElement === field,
    }, { prevented: false, open: false, focusedField: true });
  });
}

test('help leaves a handled key and an open dialog alone, and is unavailable while signed out', () => {
  const { doc, close, dialog, key } = fixture();
  key({ handled: true });
  assert.equal(dialog.open, false);
  key();
  assert.equal(doc.activeElement, close);
  assert.equal(key().defaultPrevented, false);
  assert.equal(doc.activeElement, close);
  const signedOut = fixture(false);
  assert.equal(signedOut.key().defaultPrevented, false);
  signedOut.opener.dispatchEvent(new Event('click'));
  assert.equal(signedOut.dialog.open, false);
});
