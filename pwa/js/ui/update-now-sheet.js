// Update now's confirmation (#1159, docs/protocol/v1-update.md "What the person sees", ui.md §6.6):
// a modal dialog listing what the update would go over (update.js nowSheet), with Update now and
// Cancel, focus starting on Cancel, as #1142's model confirmation has it. Cancel and Escape send
// nothing. Nothing is signed here: the press is signed once Update now is chosen.
import { h } from './dom.js';

export const CONFIRM_LABEL = 'Update now';
export const CANCEL_LABEL = 'Cancel';

// sheet: what nowSheet says. Resolves true for Update now, false for Cancel or Escape. returnFocus:
// called once it has closed, to put focus back where the press came from (the row is drawn again
// meanwhile, so the button that opened it may be a new one). signal: closes it as Cancel when
// aborted (the account signed out, say), without returning focus.
export function openUpdateNowSheet(sheet, { doc = document, returnFocus = null, signal = null } = {}) {
  return new Promise((resolve) => {
    if (signal?.aborted) { resolve(false); return; }
    let settled = false;
    const id = `update-now-${Math.random().toString(36).slice(2)}`;
    const cancel = h('button', { type: 'button', class: 'button secondary update-now-cancel' }, CANCEL_LABEL);
    const confirm = h('button', { type: 'button', class: 'button update-now-confirm' }, CONFIRM_LABEL);
    const dialog = h('dialog', { class: 'update-now-sheet', 'aria-labelledby': `${id}-t`, 'aria-describedby': `${id}-b` },
      h('h2', { class: 'update-now-title', id: `${id}-t` }, sheet.title),
      h('div', { class: 'update-now-body', id: `${id}-b` },
        sheet.sessions.length ? h('ul', { class: 'update-now-list' }, sheet.sessions.map((s) => h('li', { class: 'update-now-session' },
          // A session's name is the agent's or the person's text: isolated in its own direction.
          h('bdi', { class: 'update-now-name' }, s.name), ' \u00b7 ', h('span', { class: 'update-now-state' }, s.state)))) : null,
        sheet.more ? h('p', { class: 'update-now-more' }, sheet.more) : null,
        sheet.unknown ? h('p', { class: 'update-now-unknown' }, sheet.unknown) : null,
        h('p', { class: 'update-now-pause' }, sheet.pause),
        sheet.warn ? h('p', { class: 'update-now-warn' }, sheet.warn) : null),
      h('div', { class: 'update-now-actions' }, cancel, confirm));

    const close = (go, { refocus = true } = {}) => {
      if (settled) { return; }
      settled = true;
      signal?.removeEventListener('abort', aborted);
      if (dialog.open) { dialog.close(); }
      dialog.remove();
      resolve(go);
      if (refocus) { try { returnFocus?.(); } catch (e) { console.error(e); } }
    };
    const aborted = () => close(false, { refocus: false });
    signal?.addEventListener('abort', aborted);
    cancel.addEventListener('click', () => close(false));
    confirm.addEventListener('click', () => close(true));
    // Escape is the dialog's own cancel: nothing is sent.
    dialog.addEventListener('cancel', (ev) => { ev.preventDefault(); close(false); });
    dialog.addEventListener('close', () => close(false));

    doc.body.append(dialog);
    if (typeof dialog.showModal === 'function') { dialog.showModal(); } else { dialog.setAttribute('open', ''); }
    cancel.focus();
  });
}
