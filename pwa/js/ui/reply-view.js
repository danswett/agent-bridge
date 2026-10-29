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
import { utf8Bytes } from '../answers.js';
import { ReplyLimits, checkReply, replyRequest } from '../replies.js';
import { AttachmentLimits, describeSize, prepareAttachment } from '../attachments.js';
import { CANCELLED } from '../reply-service.js';

let seq = 0;
const where = (machineName) => (machineName ? ` on ${machineName}` : '');
const kib = (n) => `${(n / 1024).toFixed(1)} KiB`;
const capitalise = (s) => (s ? s[0].toUpperCase() + s.slice(1) : s);
// Images a browser can show as a thumbnail. HEIC only Safari can, and it says so by loading.
const SHOWN = /^image\/(png|jpeg|gif|webp|heic|heif)$/;
// Above this a thumbnail is not worth a second copy of the photo in memory.
const THUMBNAIL_MAX = 16 * 1024 * 1024;

// After busy, Send comes back after this long, as for an answer.
export const REPLY_BUSY_PAUSE_MS = 2000;

const OUTCOME_TEXT = {
  delivered: () => 'Delivered. The session has your reply.',
  unconfirmed: (at) => `Sent, but not confirmed. Check at the machine${at} before sending it again.`,
  stale: () => 'Not sent: the session has ended.',
  busy: () => 'Not sent: an answer or another reply to this session is being delivered. Try again in a moment.',
  refused: () => 'The machine refused this reply.',
};

// What a reply's progress or outcome says. tone: progress | ok | warn | bad.
export function describeReply(entry, { machineName, working = false } = {}) {
  const at = where(machineName);
  switch (entry.state) {
    case 'sending': {
      const p = entry.progress;
      if (p && p.total > 0 && p.sent < p.total) {
        const files = entry.answer?.attachments?.length ?? 0;
        const what = files === 1 ? 'the attachment' : `${files} attachments`;
        const percent = Math.floor((100 * p.sent) / p.total);
        if (p.phase === 'upload') { return { text: `Uploading ${what}… ${percent}%`, tone: 'progress' }; }
        if (p.phase === 'fetch') { return { text: `The machine is receiving ${what}… ${percent}%`, tone: 'progress' }; }
        return { text: `Sending ${what}… ${percent}%`, tone: 'progress' };
      }
      const text = entry.attempts > 1 ? 'Sending again…' : 'Sending…';
      return { text: working ? `${text} The session is working, and takes it at its next step.` : text, tone: 'progress' };
    }
    case 'waiting':
      return {
        text: entry.reason === 'still being delivered'
          ? 'The machine is still delivering it…'
          : `Waiting for the connection to the machine${at}. Your reply goes as soon as it is back.`,
        tone: 'progress',
      };
    case 'failed':
      if (entry.reason === CANCELLED) { return { text: 'Cancelled. Nothing was sent to the session.', tone: 'warn' }; }
      return { text: `Couldn't send it: ${entry.reason ?? 'the machine said no'}.`, tone: 'bad' };
    default: {
      const outcome = OUTCOME_TEXT[entry.outcome] ? entry.outcome : 'unconfirmed';
      let text = OUTCOME_TEXT[outcome](at);
      if (outcome === 'refused' && entry.reason) { text = `The machine refused this reply: ${entry.reason}.`; }
      if (outcome === 'unconfirmed' && entry.reason) { text += ` (${entry.reason})`; }
      return { text, tone: outcome === 'delivered' ? 'ok' : outcome === 'refused' ? 'bad' : 'warn' };
    }
  }
}

export class ReplyBox {
  #unwatch = null;
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

  // replying: { service, hostId, sessionId }. attach: whether the machine takes files.
  // largeMax: the largest it takes through the blob store, 0 for none.
  constructor({ machineName, replying, attach = false, largeMax = 0, uuid = () => globalThis.crypto.randomUUID() }) {
    this.machineName = machineName;
    this.replying = replying;
    this.uuid = uuid;
    this.idBase = `r${++seq}`;
    this.#attach = attach;
    this.#largeMax = largeMax;
    this.#build();
    const { service, hostId, sessionId } = replying;
    this.#unwatch = service.watch(hostId, sessionId, (e) => this.#show(e));
  }

  destroy() {
    this.#unwatch?.();
    clearTimeout(this.#busyTimer);
    for (const f of this.#files) { if (f.url) { URL.revokeObjectURL(f.url); } }
  }

  get text() { return this.els.input.value; }

  // The files waiting to go: { id, name, size, type, resized, large }.
  get files() { return this.#files.map(({ id, name, size, type, resized, large }) => ({ id, name, size, type, resized, large: !!large })); }

  get entry() { const { service, hostId, sessionId } = this.replying; return service.get(hostId, sessionId); }

  #build() {
    const id = this.idBase;
    const e = this.els = {};
    e.input = h('textarea', { id: `${id}-text`, rows: 2, class: 'text-input reply-input', placeholder: 'Reply to the session…', 'aria-describedby': `${id}-hint ${id}-bytes ${id}-held` });
    e.input.addEventListener('input', () => { this.#clearError(); this.#sync(); });
    // Ctrl+Enter or Cmd+Enter sends, as in most chat boxes; Enter alone is a new line.
    e.input.addEventListener('keydown', (ev) => {
      if (ev.key === 'Enter' && (ev.ctrlKey || ev.metaKey)) { ev.preventDefault(); this.#send(); }
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
    e.error = h('p', { class: 'answer-error reply-error', role: 'alert', hidden: true });
    e.files = h('ul', { class: 'attachments', 'aria-label': 'Attachments', hidden: true });
    e.picker = h('input', { type: 'file', multiple: true, class: 'file-picker', hidden: true, tabindex: '-1', 'aria-hidden': 'true' });
    e.picker.addEventListener('change', () => {
      const files = [...(e.picker.files ?? [])];
      e.picker.value = '';
      this.#add(files);
    });
    e.attach = h('button', { type: 'button', class: 'button secondary attach', 'aria-label': 'Attach a file or image', hidden: !this.#attach, onclick: () => e.picker.click() }, icon('paperclip'), h('span', { class: 'attach-label' }, 'Attach'));
    e.send = h('button', { type: 'submit', class: 'button send' }, 'Send');
    e.resend = h('button', { type: 'button', class: 'button secondary resend', hidden: true, onclick: () => this.#resend() }, 'Try again');
    e.cancel = h('button', { type: 'button', class: 'button secondary cancel', hidden: true, onclick: () => this.#cancel() }, 'Cancel');
    e.status = h('p', { class: 'answer-status reply-status', role: 'status', 'aria-live': 'polite' });
    e.form = h('form', { class: 'reply', novalidate: true, 'aria-labelledby': `${id}-label` },
      h('label', { for: `${id}-text`, class: 'text-label', id: `${id}-label` }, 'Reply'),
      e.held,
      h('div', { class: 'text-wrap' }, e.input, e.bytes),
      e.files,
      e.hint, e.error,
      h('div', { class: 'answer-actions' }, e.send, e.attach, e.resend, e.cancel),
      e.picker,
      e.status);
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
  // whether the machine takes files, and how large through the blob store.
  update(session, { attach = this.#attach, largeMax = this.#largeMax } = {}) {
    this.#session = session;
    this.#attach = attach;
    this.#largeMax = largeMax;
    this.#show(this.entry);
  }

  #held() { return !!this.#session?.question; }

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
    e.held.hidden = !held;
    if (held) {
      e.held.textContent = this.#session.question.answerAtMachine
        ? 'The session is waiting at the machine for a prompt only it can answer. Answer it there first, then reply.'
        : 'The session is waiting for an answer to its question. Answer that first, then reply.';
    }
    e.input.disabled = held;
    e.attach.hidden = !this.#attach;
    e.attach.disabled = held || pending || this.#files.length + this.#preparing >= AttachmentLimits.perReply;
    for (const b of e.files.querySelectorAll('button.remove')) { b.disabled = pending; }
    e.send.disabled = held || pending || !!this.#busyTimer || this.#preparing > 0 || (text.trim() === '' && this.#files.length === 0);
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

  // Reads picked, pasted or dropped files, and shows each as a chip once it is ready.
  async #add(files) {
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
    this.#sent = { request: entry.request, text, files };
  }

  #cancel() {
    const { service, hostId, sessionId } = this.replying;
    service.cancel?.(hostId, sessionId);
  }

  #resend() {
    if (this.entry?.request === this.#edited) { return; }
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
    const shown = entry ? describeReply(entry, { machineName: this.machineName, working: this.#session?.status === 'working' }) : { text: '', tone: '' };
    if (this.#preparing > 0 && !this.#pending(entry)) { shown.text = 'Reading the attachment…'; shown.tone = 'progress'; }
    e.form.dataset.reply = entry ? (entry.state === 'done' ? (OUTCOME_TEXT[entry.outcome] ? entry.outcome : 'unconfirmed') : entry.state) : 'none';
    e.status.textContent = shown.text;
    e.status.className = `answer-status reply-status${shown.tone ? ` ${shown.tone}` : ''}`;
    e.form.setAttribute('aria-busy', shown.tone === 'progress' ? 'true' : 'false');
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
