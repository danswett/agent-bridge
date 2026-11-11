import { isEditable } from '../key-check.js';
import { shortcutHints } from '../platform.js';

export function mountKeyboardShortcuts({ doc = globalThis.document, enabled = () => true } = {}) {
  const dialog = doc.getElementById('keyboard-shortcuts-dialog');
  const close = doc.getElementById('keyboard-shortcuts-close');
  const settings = doc.getElementById('keyboard-shortcuts-open');
  doc.getElementById('keyboard-shortcuts-search').textContent = shortcutHints().search;
  let opener = null;
  const open = (from) => {
    if (!enabled() || doc.querySelector('dialog[open]')) { return; }
    opener = from;
    dialog.showModal();
    close.focus();
  };
  settings.addEventListener('click', () => open(settings));
  close.addEventListener('click', () => dialog.close());
  dialog.addEventListener('close', () => {
    (opener?.isConnected ? opener : doc.getElementById('main'))?.focus({ preventScroll: true });
    opener = null;
  });
  doc.addEventListener('keydown', (e) => {
    if (e.key !== '?' || e.defaultPrevented || e.repeat || e.isComposing || e.ctrlKey || e.metaKey || e.altKey) { return; }
    if (isEditable(doc.activeElement) || doc.activeElement?.tagName === 'SELECT') { return; }
    if (!enabled() || doc.querySelector('dialog[open]')) { return; }
    e.preventDefault();
    open(doc.activeElement);
  });
}
