// The confirmation sheet for an approval the host escaped anything in (#803, ui.md §6.4, "Escaped
// approvals"): the escaped command in full, the warning, and the one button that approves, at the
// end of the command. Cancel, Back and Escape send nothing. The command is signed only when that
// button is pressed, so a slow read never expires a confirmation.
import { h } from './dom.js';
import { segments, pieces, describeToken, warnings, shownDigest } from '../escaped.js';

export const SHEET_TITLE = 'Approve a command with hidden characters?';
export const CONFIRM_LABEL = "I've read it. Approve once";
export const CONFIRM_NAME = "Approve once: I've read the escaped command";
export const SCROLL_HINT = 'Scroll to the end to approve.';
export const AVAILABLE = 'Approve is now available';
// How long the confirm stays disabled after it first comes into view: a double tap that opened the
// sheet cannot land on it.
export const CONFIRM_DELAY_MS = 1000;

// One run of text with its host escapes boxed, each token named for a screen reader. Text the agent
// typed itself, "\u202E" included, is never boxed. Every character is drawn: anything inside a mark
// that is not an escape is drawn as the plain text it is, never dropped, so nothing approved goes
// unseen (#836 review; a question whose marks don't check is never offered the sheet at all).
export function escapedText(message, marks) {
  const out = [];
  for (const { text, mark } of segments(message, marks)) {
    if (!mark) { out.push(text); continue; }
    for (const piece of pieces(text)) {
      out.push(piece.token ? h('span', { class: 'escape-token', role: 'img', 'aria-label': describeToken(piece.text) }, piece.text) : piece.text);
    }
  }
  return out;
}

// A warning sentence with its codes boxed as the command's are.
function sentence(text) {
  const parts = [];
  let at = 0;
  for (const m of text.matchAll(/\\u[0-9A-F]{4}/g)) {
    if (m.index > at) { parts.push(text.slice(at, m.index)); }
    parts.push(h('span', { class: 'escape-token', role: 'img', 'aria-label': describeToken(m[0]) }, m[0]));
    at = m.index + m[0].length;
  }
  if (at < text.length) { parts.push(text.slice(at)); }
  return h('p', { class: 'sheet-warning-line' }, parts);
}

// Opens the sheet for question q. sign({ shown, pressed, cancelled }) signs and sends Approve once
// and resolves when it has gone; it is called only from the confirm button, signs as of pressed (the
// press, in ms), and sends nothing once cancelled() is true. Resolves to true once sent, false when cancelled. delayMs: tests shorten
// the confirm's hold.
export function openConfirmSheet(q, { title = null, machineName = null, sign, delayMs = CONFIRM_DELAY_MS, doc = document }) {
  return new Promise((resolve) => {
    let settled = false;
    const finish = (sent) => { if (!settled) { settled = true; resolve(sent); } };
    const id = `escaped-${Math.random().toString(36).slice(2)}`;
    const cancel = h('button', { type: 'button', class: 'button secondary sheet-cancel' }, 'Cancel');
    const confirm = h('button', { type: 'button', class: 'button sheet-confirm', disabled: true, 'aria-label': CONFIRM_NAME }, CONFIRM_LABEL);
    const hint = h('p', { class: 'sheet-hint', id: `${id}-hint` }, SCROLL_HINT);
    const announce = h('p', { class: 'sr-only sheet-announce', 'aria-live': 'polite' });
    const error = h('p', { class: 'answer-error sheet-error', role: 'alert', hidden: true });
    const command = h('div', { class: 'sheet-command', tabindex: '0', role: 'region', 'aria-label': 'The command, escaped', 'aria-describedby': `${id}-hint` },
      h('pre', { class: 'escaped-text', dir: 'ltr' }, escapedText(q.message, q.escaped?.marks ?? [])),
      confirm);
    const where = [title, machineName].filter(Boolean).join(' on ');
    const dialog = h('dialog', { class: 'escaped-sheet', 'aria-labelledby': `${id}-t`, 'aria-describedby': `${id}-w`, dataset: { question: q.id } },
      h('header', { class: 'sheet-head' }, h('h2', { class: 'sheet-title', id: `${id}-t` }, SHEET_TITLE), cancel),
      where ? h('p', { class: 'sheet-where' }, where) : null,
      h('div', { class: 'note warn sheet-warning', id: `${id}-w`, role: 'note' }, warnings({ message: q.message, marks: q.escaped?.marks ?? [] }).map(sentence)),
      hint, command, error, announce);

    let timer = null;
    let observer = null;
    // An entry of its own on the Back stack, so Back (the iOS edge swipe calls history.back())
    // closes the sheet as cancelled, sending nothing, and leaves the session where it was, rather
    // than moving the page beneath an open approval (#836 review).
    const win = doc.defaultView;
    const entry = `escaped-${id}`;
    let stacked = false;
    const onPop = () => { if (win?.history.state?.escapedSheet !== entry) { stacked = false; close(false); } };
    if (win?.history) {
      win.history.pushState({ ...(win.history.state ?? {}), escapedSheet: entry }, '', win.location.href);
      stacked = true;
      win.addEventListener('popstate', onPop);
    }
    const close = (sent) => {
      if (settled) { return; }
      clearTimeout(timer);
      observer?.disconnect();
      win?.removeEventListener('popstate', onPop);
      // Closed here, not by Back: its entry goes too, so Back is not one step that does nothing.
      if (stacked && win?.history.state?.escapedSheet === entry) { stacked = false; win.history.back(); }
      if (dialog.open) { dialog.close(); }
      dialog.remove();
      finish(sent);
    };
    cancel.addEventListener('click', () => close(false));
    // Escape is the dialog's own cancel: nothing is sent.
    dialog.addEventListener('cancel', (ev) => { ev.preventDefault(); close(false); });
    dialog.addEventListener('close', () => close(false));

    // The confirm waits until it has been in view (the end of the command shown), then a moment more.
    const seen = () => {
      if (timer !== null || settled) { return; }
      hint.hidden = true;
      timer = setTimeout(() => {
        confirm.disabled = false;
        announce.textContent = AVAILABLE;
      }, delayMs);
    };
    const View = doc.defaultView?.IntersectionObserver;
    if (View) {
      observer = new View((entries) => { if (entries.some((e) => e.isIntersecting)) { seen(); } }, { root: command, threshold: 1 });
      observer.observe(confirm);
    } else {
      command.addEventListener('scroll', () => { if (command.scrollTop + command.clientHeight >= command.scrollHeight - 1) { seen(); } });
    }

    confirm.addEventListener('click', async () => {
      if (confirm.disabled) { return; }
      // The confirmation is the person's at this moment, taken before anything is awaited: a tab
      // suspended mid-way resumes minutes later, and signed then it had a fresh lifetime (#836
      // review 5780636755).
      const pressed = Date.now();
      confirm.disabled = true;
      error.hidden = true;
      try {
        // What this device has is what is approved: a message that does not hash to what the host
        // said it sent is not confirmed, whichever of them changed.
        const shown = await shownDigest(q.message);
        // Cancelled (Cancel, Escape or Back) while it hashed: nothing goes. sign checks again
        // after signing, immediately before it sends (#836 review).
        if (settled) { return; }
        if (!q.escaped?.shown || shown !== q.escaped.shown) { throw new Error("This approval didn't arrive as the machine sent it. Close this and read it again"); }
        const sent = await sign({ shown, pressed, cancelled: () => settled });
        if (sent !== false) { close(true); }
      } catch (e) {
        if (settled) { return; }
        error.textContent = `${e?.message ?? 'It could not be signed'}.`;
        error.hidden = false;
        confirm.disabled = false;
      }
    });

    doc.body.append(dialog);
    if (typeof dialog.showModal === 'function') { dialog.showModal(); } else { dialog.setAttribute('open', ''); }
    cancel.focus();
  });
}
