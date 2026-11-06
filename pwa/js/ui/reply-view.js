// A session's reply box: a prompt for the session, typed into its input on the machine as
// agent-ha-bridge's reply box does (session.reply). Shown when the machine says it takes
// replies (`sessions.reply`); ../reply-service.js sends it and resends it, with the same
// request, until there is an outcome.
//
// The words stay in the box until the session has them: a reply that was refused, went
// nowhere or can't be confirmed keeps them, and a delivered one takes out only what was
// sent, so anything typed meanwhile stays. While the session is asking a question the box
// is disabled and says to answer first: the words would answer it.
//
// Where the machine takes files too (`sessions.attach`), images and files can be pasted into
// the box, dropped on it or picked with Attach, and go with the reply: shown as chips, with a
// thumbnail for an image, until it is delivered. ../attachments.js prepares them and sends them
// ahead of the reply: up to 256 KiB in the channel, and larger ones, where the machine and the
// relay take them (`sessions.attach.blob`), through the relay's blob store, with progress and a
// Cancel until the reply itself goes. Without that, an image over the limit is made smaller.
import { h, icon } from './dom.js';
import { designIcon } from './glyph.js';
import { utf8Bytes } from '../answers.js';
import { ReplyLimits, checkReply, replyRequest } from '../replies.js';
import { AttachmentLimits, describeSize, prepareAttachment } from '../attachments.js';
import { CANCELLED } from '../reply-service.js';
import { isSendKey, shortcutHints } from '../platform.js';
import { ReceiptClock, describeInFlight, drawReceipt, drawn, dropCheck, holdCheck, receiptLive } from './receipt.js';
import { PHONE } from './collapse-header.js';

let seq = 0;
const kib = (n) => `${(n / 1024).toFixed(1)} KiB`;
const capitalise = (s) => (s ? s[0].toUpperCase() + s.slice(1) : s);
// Images a browser can show as a thumbnail. HEIC only Safari can, and it says so by loading.
const SHOWN = /^image\/(png|jpeg|gif|webp|heic|heif)$/;
// Above this a thumbnail is not worth a second copy of the photo in memory.
const THUMBNAIL_MAX = 16 * 1024 * 1024;
// A phone's box grows a line at a time to six lines (24 each, and 10 above and below), then
// scrolls inside (ui.md §6.5, #935).
const GROW_MAX = 164;

// After busy, Send comes back after this long, as for an answer.
export const REPLY_BUSY_PAUSE_MS = 2000;

const OUTCOME_TEXT = {
  delivered: () => 'Delivered',
  unconfirmed: () => 'Sent, but not confirmed. Check the conversation before sending it again.',
  stale: () => 'Not sent: the session has ended.',
  busy: () => 'Not sent: an answer or another reply to this session is being delivered. Try again in a moment.',
  refused: () => 'The machine refused this reply.',
};

// What a reply's progress or outcome says: the receipt under the box (receipt.js; ui.md §6.5,
// decided on #617). Its files' upload is a line; Sending…, Sent, Passed to the agent or Up next
// and Delivered are only heard (Send's ring and check show them); a stall, a wait and any other
// outcome are a line, in today's words. stalled: whether the state it is in has stalled
// (ReceiptClock). Returns { key, text, more, tone, line, speech }; tone: progress | warn | bad,
// or room for nothing to show.
export function describeReply(entry, { machineName, stalled = false } = {}) {
  const said = (key, text, tone) => ({ key, text, more: null, tone, line: true, speech: `Reply: ${text}` });
  switch (entry.state) {
    case 'sending': {
      const p = entry.progress;
      if (p && p.total > 0 && p.sent < p.total) {
        const files = entry.answer?.attachments?.length ?? 0;
        const what = files === 1 ? 'the attachment' : `${files} attachments`;
        const percent = Math.floor((100 * p.sent) / p.total);
        const doing = p.phase === 'upload' ? `Uploading ${what}…` : p.phase === 'fetch' ? `The machine is receiving ${what}…` : `Sending ${what}…`;
        // Said once a phase, not at every percent.
        return { key: 'upload', text: `${doing} ${percent}%`, more: null, tone: 'progress', line: true, speech: `Reply: ${doing}` };
      }
      return describeInFlight('reply', entry, { machineName, stalled });
    }
    case 'waiting':
      return describeInFlight('reply', entry, { machineName, stalled });
    case 'failed':
      if (entry.reason === CANCELLED) { return said('cancelled', 'Cancelled. Nothing was sent to the session.', 'warn'); }
      return said('failed', `Couldn't send it: ${entry.reason ?? 'the machine said no'}.`, 'bad');
    default: {
      const outcome = OUTCOME_TEXT[entry.outcome] ? entry.outcome : 'unconfirmed';
      // Send's check says it (holdCheck), and the line keeps its room, empty.
      if (outcome === 'delivered') { return { key: 'delivered', text: '', more: null, tone: 'room', line: false, speech: 'Reply delivered' }; }
      let text = OUTCOME_TEXT[outcome]();
      if (outcome === 'refused' && entry.reason) { text = `The machine refused this reply: ${entry.reason}.`; }
      if (outcome === 'unconfirmed' && entry.reason) { text += ` (${entry.reason})`; }
      return said(outcome, text, outcome === 'refused' ? 'bad' : 'warn');
    }
  }
}

export class ReplyBox {
  #unwatch = null;
  #receipt = null;
  // Whether the line says files are being read, which the receipt doesn't know of.
  #reading = false;
  #busyTimer = null;
  #sent = null;
  #session = null;
  // The busy outcome whose pause has been started, so a re-render does not start another.
  #busyFor = null;
  // Prepared files waiting to go, in order, and how many are still being read.
  #files = [];
  #preparing = 0;
  #attach = false;
  // The largest file the machine takes through the blob store now; 0 for none.
  #largeMax = 0;
  // A failed reply whose files have changed since: Try again would send the old set, so it
  // goes, and Send sends what the box shows.
  #edited = null;
  // Why nothing can be sent now (its machine offline, #269), or null.
  #offline = null;
  // Below 760 (a phone): the box is one line at rest and grows with its words.
  #phone = null;
  #resize = null;
  #width = 0;
  #fitFrame = 0;
  // Takes the person to the question the reply waits on, and focuses its first choice.
  #onGoToQuestion = null;

  // replying: { service, hostId, sessionId }. attach: whether the machine takes files.
  // largeMax: the largest it takes through the blob store, 0 for none. onGoToQuestion: the
  // phone notice's link, to the question the reply waits on.
  constructor({ machineName, replying, attach = false, largeMax = 0, onGoToQuestion = null, uuid = () => globalThis.crypto.randomUUID() }) {
    this.machineName = machineName;
    this.#onGoToQuestion = onGoToQuestion;
    this.replying = replying;
    this.uuid = uuid;
    this.idBase = `r${++seq}`;
    this.#attach = attach;
    this.#largeMax = largeMax;
    this.#phone = globalThis.matchMedia?.(PHONE) ?? null;
    this.#build();
    this.#phone?.addEventListener('change', this.#onPhone);
    // The box's width decides where its words wrap, and it has none until it is drawn.
    if (typeof globalThis.ResizeObserver === 'function') {
      this.#resize = new ResizeObserver(() => {
        const w = this.els.form.offsetWidth;
        if (w === this.#width) { return; }
        this.#width = w;
        cancelAnimationFrame(this.#fitFrame);
        this.#fitFrame = requestAnimationFrame(() => this.fit());
      });
      this.#resize.observe(this.els.form);
    }
    this.#receipt = new ReceiptClock({
      describe: (entry, { stalled }) => describeReply(entry, { machineName: this.machineName, stalled }),
      draw: (d) => drawReceipt(this.els.status, 'answer-status reply-status', d),
      say: (text) => { this.els.live.textContent = text; },
      heard: () => drawn(this.els.live),
      delivered: () => holdCheck(this.els.send),
      follow: (fn) => replying.service.watch(replying.hostId, replying.sessionId, fn),
    });
    const { service, hostId, sessionId } = replying;
    this.#unwatch = service.watch(hostId, sessionId, (e) => this.#show(e));
  }

  destroy() {
    this.#unwatch?.();
    this.#receipt.destroy();
    clearTimeout(this.#busyTimer);
    this.#phone?.removeEventListener('change', this.#onPhone);
    this.#resize?.disconnect();
    if (this.#fitFrame) { cancelAnimationFrame(this.#fitFrame); }
    for (const f of this.#files) { if (f.url) { URL.revokeObjectURL(f.url); } }
  }

  #onPhone = () => { this.#sync(); };

  // A phone's box as tall as its words, from one line to six, then it scrolls inside. From 760 the
  // box is as the stylesheet draws it.
  fit() {
    const t = this.els.input;
    if (!this.#phone?.matches) {
      t.style.removeProperty('height');
      t.style.removeProperty('overflow-y');
      return;
    }
    if (!t.getClientRects().length) { return; }
    const top = t.scrollTop;
    // Its least height (the stylesheet's one line) first, so the words alone decide.
    t.style.height = '0px';
    const full = t.scrollHeight;
    t.style.height = `${Math.min(full, GROW_MAX)}px`;
    t.style.overflowY = full > GROW_MAX ? 'auto' : 'hidden';
    t.scrollTop = top;
  }

  get text() { return this.els.input.value; }

  // Words put in for the person to read and send, after anything already typed ("Ask main agent
  // to stop it", ui.md §6.3): nothing is sent until Send. What is there is kept as it is, its
  // trailing space or line break too: a reply on its way comes out of the box only while the box
  // still begins with it, so trimmed, a delivered reply would stay to be sent again.
  prefill(text) {
    const input = this.els.input;
    const v = input.value;
    input.value = !v.trim() ? text : /\s$/.test(v) ? `${v}${text}` : `${v} ${text}`;
    this.#clearError();
    this.#sync();
    input.focus();
    input.setSelectionRange(input.value.length, input.value.length);
  }

  // The files waiting to go: { id, name, size, type, resized, large }.
  get files() { return this.#files.map(({ id, name, size, type, resized, large }) => ({ id, name, size, type, resized, large: !!large })); }

  get entry() { const { service, hostId, sessionId } = this.replying; return service.get(hostId, sessionId); }

  #build() {
    const id = this.idBase;
    const e = this.els = {};
    e.input = h('textarea', { id: `${id}-text`, rows: 2, class: 'text-input reply-input', placeholder: 'Message the main agent', 'aria-describedby': `${id}-hint ${id}-bytes ${id}-held` });
    e.input.addEventListener('input', () => {
      this.#clearError();
      // The receipt's room is kept from Send until the box is typed in again (ui.md §6.5).
      if (!e.status.textContent) { e.status.classList.remove('room'); }
      this.#sync();
    });
    // Ctrl+Enter or Cmd+Enter sends, as in most chat boxes; Enter alone is a new line.
    e.input.addEventListener('keydown', (ev) => {
      if (isSendKey(ev)) { ev.preventDefault(); this.#send(); }
    });
    // A pasted screenshot or copied file is attached; pasted text is left to the box.
    e.input.addEventListener('paste', (ev) => {
      const files = [...(ev.clipboardData?.items ?? [])].filter((i) => i.kind === 'file').map((i) => i.getAsFile()).filter(Boolean);
      if (!files.length || !this.#attach) { return; }
      ev.preventDefault();
      this.#add(files);
    });
    e.bytes = h('span', { class: 'bytes', id: `${id}-bytes`, 'aria-live': 'polite' });
    e.hint = h('p', { class: 'note reply-hint', id: `${id}-hint`, hidden: true }, 'Line breaks are sent as spaces: the session gets your reply as one line.');
    e.held = h('p', { class: 'note warn reply-held', id: `${id}-held`, hidden: true }, 'The session is waiting for an answer to its question. Answer that first, then reply.');
    e.toQuestion = h('button', { type: 'button', class: 'reply-question-link', onclick: () => this.#onGoToQuestion?.() }, 'Go to the question');
    e.error = h('p', { class: 'answer-error reply-error', role: 'alert', hidden: true });
    e.files = h('ul', { class: 'attachments', 'aria-label': 'Attachments', hidden: true });
    e.picker = h('input', { type: 'file', multiple: true, class: 'file-picker', hidden: true, tabindex: '-1', 'aria-hidden': 'true' });
    e.picker.addEventListener('change', () => {
      const files = [...(e.picker.files ?? [])];
      e.picker.value = '';
      this.#add(files);
    });
    // Attach, then Send (ui.md §6.5): icons, named, with the shortcut in Send's tooltip: ⌘↩ on
    // Apple's platforms, Ctrl+Enter elsewhere (the platform layer, #168).
    e.attach = h('button', { type: 'button', class: 'icon-button attach', 'aria-label': 'Attach a file or image', title: 'Attach a file or image', hidden: !this.#attach, onclick: () => e.picker.click() }, icon('paperclip'), h('span', { class: 'attach-label' }, 'Attach'));
    e.send = h('button', { type: 'submit', class: 'button send', 'aria-label': 'Send', title: `Send (${shortcutHints().send})` }, designIcon('send', { cls: 'ab-icon-20' }));
    e.resend = h('button', { type: 'button', class: 'button secondary resend', hidden: true, onclick: () => this.#resend() }, 'Try again');
    e.cancel = h('button', { type: 'button', class: 'button secondary cancel', hidden: true, onclick: () => this.#cancel() }, 'Cancel');
    // The receipt's words, and apart from them the line a screen reader hears it on (receipt.js).
    e.status = h('p', { class: 'answer-status reply-status' });
    e.live = receiptLive();
    e.form = h('form', { class: 'reply', novalidate: true, 'aria-labelledby': `${id}-label` },
      h('label', { for: `${id}-text`, class: 'text-label sr-only', id: `${id}-label` }, 'Reply'),
      e.held,
      h('div', { class: 'reply-box' },
        h('div', { class: 'text-wrap' }, e.input, e.bytes),
        e.files,
        h('div', { class: 'reply-tools' }, e.attach, e.send)),
      e.hint, e.error,
      h('div', { class: 'answer-actions' }, e.resend, e.cancel),
      e.picker,
      e.status, e.live);
    e.form.addEventListener('submit', (ev) => { ev.preventDefault(); this.#send(); });
    // Files dropped on the box, from a desktop.
    e.form.addEventListener('dragover', (ev) => { if (this.#attach && ev.dataTransfer?.types?.includes('Files')) { ev.preventDefault(); } });
    e.form.addEventListener('drop', (ev) => {
      const files = [...(ev.dataTransfer?.files ?? [])];
      if (!files.length || !this.#attach) { return; }
      ev.preventDefault();
      this.#add(files);
    });
    this.element = e.form;
  }

  // The session as it is now: whether it is asking anything, and whether it is working; and
  // whether the machine takes files, and how large through the blob store. offline: why nothing
  // can be sent now (its machine offline, #269), or null; what is in the box stays.
  update(session, { attach = this.#attach, largeMax = this.#largeMax, offline = null } = {}) {
    this.#session = session;
    this.#attach = attach;
    this.#largeMax = largeMax;
    this.#offline = offline || null;
    this.#show(this.entry);
  }

  #held() { return !!this.#session?.question || !!this.#offline; }

  #pending(entry = this.entry) { return !!entry && (entry.state === 'sending' || entry.state === 'waiting'); }

  #sync() {
    const e = this.els;
    const text = e.input.value;
    const n = utf8Bytes(text);
    const over = n > ReplyLimits.text;
    e.bytes.textContent = n > ReplyLimits.text * 0.75 ? `${over ? 'Too long: ' : ''}${kib(n)} of ${ReplyLimits.text / 1024} KiB` : '';
    e.bytes.classList.toggle('over', over);
    if (over) { e.input.setAttribute('aria-invalid', 'true'); } else { e.input.removeAttribute('aria-invalid'); }
    e.hint.hidden = !/[\r\n\u2028\u2029]/.test(text);
    const held = this.#held();
    const pending = this.#pending();
    const linked = e.toQuestion === e.toQuestion.ownerDocument.activeElement;
    e.held.hidden = !held;
    if (this.#offline) {
      e.held.textContent = this.#offline;
    } else if (held && this.#session.question.answerAtMachine) {
      e.held.textContent = 'The session is waiting at the machine for a prompt only it can answer. Answer it there first, then reply.';
    } else if (held && this.#phone?.matches && this.#onGoToQuestion) {
      // A phone's notice is one line, and its link takes the person to the question (ui.md §6.5).
      e.held.replaceChildren(h('span', { class: 'reply-notice-text' }, 'Answer its question first.'), ' ', e.toQuestion);
    } else if (held) {
      e.held.textContent = 'The session is waiting for an answer to its question. Answer that first, then reply.';
    }
    e.held.classList.toggle('reply-notice', e.held.contains(e.toQuestion));
    // Offline, the words can still be written, to go once it is back; a question would take them.
    e.input.disabled = !!this.#session?.question;
    // The link gone with the question it led to: focus goes to the words, not the top of the page.
    if (linked && (e.held.hidden || !e.held.contains(e.toQuestion)) && !e.input.disabled) { e.input.focus(); }
    e.attach.hidden = !this.#attach;
    e.attach.disabled = held || pending || this.#files.length + this.#preparing >= AttachmentLimits.perReply;
    for (const b of e.files.querySelectorAll('button.remove')) { b.disabled = pending; }
    // Nothing to send, or held: disabled, and focus, if it was here, goes back to the words (a
    // delivered reply empties the box). On its way, or back after busy: not disabled, which
    // would drop focus to the top of the page (#369); #send does nothing meanwhile, and the
    // service sends one request per reply whatever is pressed.
    const off = held || this.#preparing > 0 || (text.trim() === '' && this.#files.length === 0);
    if (off && e.send === e.send.ownerDocument.activeElement && !e.input.disabled) { e.input.focus(); }
    e.send.disabled = off;
    if (!off && (pending || this.#busyTimer)) { e.send.setAttribute('aria-disabled', 'true'); } else { e.send.removeAttribute('aria-disabled'); }
    e.resend.disabled = !!this.#offline;
    this.fit();
  }

  #clearError() {
    if (this.els.error.hidden) { return; }
    this.els.error.hidden = true;
    this.els.error.textContent = '';
  }

  #error(text) {
    this.els.error.textContent = text;
    this.els.error.hidden = false;
  }

  // Reads picked, pasted or dropped files, and shows each as a chip once it is ready. None while
  // its machine is offline: nothing is attached to a session that may have gone.
  async #add(files) {
    if (this.#offline) { return; }
    this.#clearError();
    const room = AttachmentLimits.perReply - this.#files.length - this.#preparing;
    if (files.length > room) {
      this.#error(`A reply carries at most ${AttachmentLimits.perReply} attachments.`);
      files = files.slice(0, Math.max(0, room));
    }
    const problems = [];
    this.#preparing += files.length;
    this.#sync();
    this.#show(this.entry);
    for (const file of files) {
      try {
        const prepared = await prepareAttachment(file, { uuid: this.uuid, largeMax: this.#largeMax });
        if (prepared.image && SHOWN.test(prepared.type) && prepared.size <= THUMBNAIL_MAX) { prepared.url = URL.createObjectURL(new Blob([prepared.bytes], { type: prepared.type })); }
        this.#files.push(prepared);
        this.#filesChanged();
      } catch (err) {
        problems.push(`Couldn't attach ${file.name || 'that'}: ${err?.message ?? err}.`);
      } finally {
        this.#preparing--;
      }
    }
    if (problems.length) { this.#error(problems.join(' ')); }
    this.#renderFiles();
    this.#show(this.entry);
  }

  #remove(file) {
    const i = this.#files.indexOf(file);
    if (i < 0 || this.#pending()) { return; }
    this.#files.splice(i, 1);
    this.#filesChanged();
    if (file.url) { URL.revokeObjectURL(file.url); }
    // Sent to the machine before (with a reply that was refused, say): it can let it go now,
    // and a blob uploaded for it can go from the store.
    if (file.offered || file.blob) { this.replying.service.discard?.(this.replying.hostId, file.id, file.blob?.id ?? null); }
    this.#renderFiles();
    this.#show(this.entry);
    this.els.input.focus();
  }

  #filesChanged() {
    const entry = this.entry;
    if (entry?.state === 'failed') { this.#edited = entry.request; }
  }

  #renderFiles() {
    const e = this.els;
    e.files.replaceChildren(...this.#files.map((f) => {
      const preview = f.url
        ? h('img', { class: 'attachment-thumb', src: f.url, alt: '' })
        : h('span', { class: 'attachment-thumb file' }, icon('file'));
      // A thumbnail this browser can't draw (HEIC outside Safari) shows as a file instead.
      if (f.url) { preview.addEventListener('error', () => preview.replaceWith(h('span', { class: 'attachment-thumb file' }, icon('file'))), { once: true }); }
      return h('li', { class: 'attachment', dataset: { id: f.id }, title: f.name },
        preview,
        h('span', { class: 'attachment-name' }, f.name),
        h('span', { class: 'attachment-size' }, `${describeSize(f.size)}${f.resized ? ' · made smaller' : ''}`),
        h('button', { type: 'button', class: 'remove', 'aria-label': `Remove ${f.name}`, onclick: () => this.#remove(f) }, '×'));
    }));
    e.files.hidden = this.#files.length === 0;
  }

  #send() {
    if (this.#held() || this.#pending() || this.#busyTimer || this.#preparing > 0) { return; }
    const text = this.els.input.value;
    const files = [...this.#files];
    const { service, hostId, sessionId } = this.replying;
    // Checked as it will be sent, with a stand-in request the length of a real one.
    const problem = checkReply(replyRequest({ sessionId, request: '00000000-0000-4000-8000-000000000000', text, attachments: files }));
    if (problem) {
      this.#error(`${capitalise(problem)}.`);
      this.els.input.focus();
      return;
    }
    this.#clearError();
    for (const f of files) { f.offered = true; }
    const entry = service.submit({ hostId, sessionId, text: replyRequest({ sessionId, request: '', text }).text, attachments: files });
    // Refused unsent: its machine takes nothing now.
    if (!entry) { for (const f of files) { f.offered = false; } return; }
    this.#sent = { request: entry.request, text, files };
  }

  #cancel() {
    const { service, hostId, sessionId } = this.replying;
    service.cancel?.(hostId, sessionId);
  }

  #resend() {
    if (this.entry?.request === this.#edited || this.#offline) { return; }
    const { service, hostId, sessionId } = this.replying;
    service.resend(hostId, sessionId);
  }

  #show(entry) {
    const e = this.els;
    if (entry?.state === 'done' && this.#sent?.request === entry.request) {
      if (entry.outcome === 'delivered') {
        // Delivered: what went comes out of the box, and only that. Anything typed while it was
        // on its way stays; a box no longer beginning with it has been rewritten, and is kept.
        const value = e.input.value;
        if (value.startsWith(this.#sent.text)) { e.input.value = value.slice(this.#sent.text.length).replace(/^\s+/, ''); }
        for (const f of this.#sent.files) { if (f.url) { URL.revokeObjectURL(f.url); } }
        this.#files = this.#files.filter((f) => !this.#sent.files.includes(f));
        this.#renderFiles();
      } else if (entry.outcome === 'unconfirmed') {
        // The files went with it, and the machine has let them go (and deleted any blob): sent
        // again, they are new.
        for (const f of this.#sent.files) { f.id = this.uuid(); f.offered = false; f.blob = null; }
        this.#renderFiles();
      }
      this.#sent = null;
    }
    e.form.dataset.reply = entry ? (entry.state === 'done' ? (OUTCOME_TEXT[entry.outcome] ? entry.outcome : 'unconfirmed') : entry.state) : 'none';
    // Send acknowledged (ui.md §5.3): each new state of the reply rises into place (receipt.js).
    // Files still being read, with nothing on its way, say so instead.
    const reading = this.#preparing > 0 && !this.#pending(entry);
    this.#receipt.show(reading ? null : entry);
    if (reading) {
      drawReceipt(e.status, 'answer-status reply-status', { key: 'reading', text: 'Reading the attachment…', more: null, tone: 'progress', line: true });
      // Its words are drawn, not said (the line isn't live): the live line says it once, as it starts.
      if (!this.#reading) { e.live.textContent = 'Reading the attachment'; }
    } else if (this.#reading && !entry) {
      drawReceipt(e.status, 'answer-status reply-status', null);
    }
    this.#reading = reading;
    // Send is busy (ui.md §9), not the form: the receipt's live region is in it, and a reader may
    // hold back what a busy region says until it is done.
    e.send.setAttribute('aria-busy', reading || this.#pending(entry) ? 'true' : 'false');
    // A reply sent during the last one's check: the check goes at once, so the new one never looks delivered.
    if (reading || this.#pending(entry)) { dropCheck(e.send); }
    // Held on this device for a machine that isn't connected: Send's ring stands still.
    e.send.classList.toggle('still', entry?.state === 'waiting' && entry.reason === 'not connected');
    e.resend.hidden = entry?.state !== 'failed' || entry.request === this.#edited || entry.reason === CANCELLED;
    const { service, hostId, sessionId } = this.replying;
    e.cancel.hidden = !(entry?.state === 'sending' && service.cancellable?.(hostId, sessionId));
    if (entry?.state === 'done' && entry.outcome === 'busy' && !this.#busyTimer && this.#busyFor !== entry.request) {
      this.#busyFor = entry.request;
      this.#busyTimer = setTimeout(() => { this.#busyTimer = null; this.#sync(); }, REPLY_BUSY_PAUSE_MS);
    }
    this.#sync();
  }
}
