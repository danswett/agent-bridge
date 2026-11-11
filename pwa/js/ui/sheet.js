import { h } from './dom.js';

let nextSheet = 0;

// Content scrolls; footer stays pinned. An alert can omit Cancel and focus its safe footer action.
// The caller owns its content and actions, and closes the sheet after a successful choice.
export function openSheet({ title, content = null, footer = null, kind = 'sheet', anchor = null,
  cancelLabel = 'Cancel', initialFocus = null, onClose = null } = {}) {
  if (!['sheet', 'menu', 'form', 'alert'].includes(kind)) { throw new TypeError(`Unknown sheet kind: ${kind}`); }
  const id = `sg-sheet-${++nextSheet}`;
  const back = document.activeElement;
  const cancel = cancelLabel ? h('button', { type: 'button', class: 'sg-sheet-cancel' }, cancelLabel) : null;
  const dialog = h('dialog', {
    class: 'sg-sheet', tabindex: '-1', role: kind === 'alert' ? 'alertdialog' : 'dialog',
    'aria-modal': 'true', 'aria-labelledby': id, dataset: { kind, anchored: String(!!anchor) },
  },
  h('header', { class: 'sg-sheet-head' }, h('h2', { id }, title), cancel),
  h('div', { class: 'sg-sheet-body' }, content),
  footer ? h('footer', { class: 'sg-sheet-foot' }, footer) : null);
  const listeners = new AbortController();
  const { signal } = listeners;
  const win = window;
  const viewport = win.visualViewport;
  let observer = null;
  let closed = false;
  const close = () => {
    if (closed) { return; }
    closed = true;
    listeners.abort();
    observer?.disconnect();
    dialog.close();
    dialog.remove();
    if (back?.isConnected) { back.focus({ preventScroll: true }); }
    onClose?.();
  };
  cancel?.addEventListener('click', close, { signal });
  dialog.addEventListener('cancel', (e) => { e.preventDefault(); close(); }, { signal });
  dialog.addEventListener('close', close, { signal });
  dialog.addEventListener('keydown', (e) => {
    if (e.key !== 'Tab') { return; }
    const items = [...dialog.querySelectorAll('button, [href], input, select, textarea, [tabindex]')]
      .filter((el) => el.tabIndex >= 0 && !el.matches(':disabled') && !el.closest('[inert]')
        && el.getClientRects().length && getComputedStyle(el).visibility !== 'hidden');
    const first = items[0] ?? dialog;
    const last = items.at(-1) ?? dialog;
    if (e.shiftKey && (document.activeElement === first || document.activeElement === dialog)) {
      e.preventDefault(); last.focus();
    } else if (!e.shiftKey && (document.activeElement === last || document.activeElement === dialog)) {
      e.preventDefault(); first.focus();
    }
  }, { signal });
  const place = () => {
    // At pinch zoom the visual viewport isn't a keyboard: leave the person's zoom alone.
    const visible = viewport?.scale === 1 ? viewport : null;
    const top = visible?.offsetTop ?? 0;
    const height = visible?.height ?? win.innerHeight;
    dialog.style.setProperty('--sheet-view-top', `${top}px`);
    dialog.style.setProperty('--sheet-view-height', `${height}px`);
    dialog.style.setProperty('--sheet-bottom', `${Math.max(0, win.innerHeight - height - top)}px`);
    if (!anchor || !win.matchMedia('(min-width: 760px)').matches || !['sheet', 'menu'].includes(kind)) { return; }
    const r = anchor.getBoundingClientRect();
    const box = dialog.getBoundingClientRect();
    const safeTop = parseFloat(getComputedStyle(dialog).getPropertyValue('--safe-top')) || 0;
    const left = r.right + 12 + box.width <= win.innerWidth - 16 ? r.right + 12 : r.left - box.width - 12;
    dialog.style.setProperty('--sheet-x', `${Math.max(16, Math.min(left, win.innerWidth - box.width - 16))}px`);
    dialog.style.setProperty('--sheet-y', `${Math.max(top + safeTop + 16, Math.min(r.top, top + height - box.height - 16))}px`);
  };
  win.addEventListener('resize', place, { signal });
  win.addEventListener('scroll', place, { signal, capture: true });
  viewport?.addEventListener('resize', place, { signal });
  viewport?.addEventListener('scroll', place, { signal });
  document.body.append(dialog);
  dialog.showModal();
  place();
  observer = new ResizeObserver(place);
  observer.observe(dialog);
  (initialFocus && dialog.contains(initialFocus) ? initialFocus : cancel ?? dialog).focus({ preventScroll: true });
  return { dialog, close };
}
