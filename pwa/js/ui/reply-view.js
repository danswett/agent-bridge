// A session's reply box: a prompt for the session, typed into its input on the machine as
// agent-ha-bridge's reply box does (session.reply). Shown when the machine says it takes
// replies (`sessions.reply`); ../reply-service.js sends it and resends it, with the same
// request, until there is an outcome.
//
// The words stay in the box until the session has them: a reply that was refused, went
// nowhere or can't be confirmed keeps them, and a delivered one takes out only what was
// sent, so anything typed meanwhile stays. While the session is asking a question the box
// is disabled and says to answer first: the words would answer it.
import { h } from './dom.js';
import { utf8Bytes } from '../answers.js';
import { ReplyLimits, checkReply, replyRequest } from '../replies.js';

let seq = 0;
const where = (machineName) => (machineName ? ` on ${machineName}` : '');
const kib = (n) => `${(n / 1024).toFixed(1)} KiB`;
const capitalise = (s) => (s ? s[0].toUpperCase() + s.slice(1) : s);

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

  // replying: { service, hostId, sessionId }.
  constructor({ machineName, replying }) {
    this.machineName = machineName;
    this.replying = replying;
    this.idBase = `r${++seq}`;
    this.#build();
    const { service, hostId, sessionId } = replying;
    this.#unwatch = service.watch(hostId, sessionId, (e) => this.#show(e));
  }

  destroy() { this.#unwatch?.(); clearTimeout(this.#busyTimer); }

  get text() { return this.els.input.value; }

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
    e.bytes = h('span', { class: 'bytes', id: `${id}-bytes`, 'aria-live': 'polite' });
    e.hint = h('p', { class: 'note reply-hint', id: `${id}-hint`, hidden: true }, 'Line breaks are sent as spaces: the session gets your reply as one line.');
    e.held = h('p', { class: 'note warn reply-held', id: `${id}-held`, hidden: true }, 'The session is waiting for an answer to its question. Answer that first, then reply.');
    e.error = h('p', { class: 'answer-error reply-error', role: 'alert', hidden: true });
    e.send = h('button', { type: 'submit', class: 'button send' }, 'Send');
    e.resend = h('button', { type: 'button', class: 'button secondary resend', hidden: true, onclick: () => this.#resend() }, 'Try again');
    e.status = h('p', { class: 'answer-status reply-status', role: 'status', 'aria-live': 'polite' });
    e.form = h('form', { class: 'reply', novalidate: true, 'aria-labelledby': `${id}-label` },
      h('label', { for: `${id}-text`, class: 'text-label', id: `${id}-label` }, 'Reply'),
      e.held,
      h('div', { class: 'text-wrap' }, e.input, e.bytes),
      e.hint, e.error,
      h('div', { class: 'answer-actions' }, e.send, e.resend),
      e.status);
    e.form.addEventListener('submit', (ev) => { ev.preventDefault(); this.#send(); });
    this.element = e.form;
  }

  // The session as it is now: whether it is asking anything, and whether it is working.
  update(session) {
    this.#session = session;
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
    e.held.hidden = !held;
    e.input.disabled = held;
    e.send.disabled = held || this.#pending() || !!this.#busyTimer || text.trim() === '';
  }

  #clearError() {
    if (this.els.error.hidden) { return; }
    this.els.error.hidden = true;
    this.els.error.textContent = '';
  }

  #send() {
    if (this.#held() || this.#pending() || this.#busyTimer) { return; }
    const text = this.els.input.value;
    const { service, hostId, sessionId } = this.replying;
    // Checked as it will be sent, with a stand-in request the length of a real one.
    const problem = checkReply(replyRequest({ sessionId, request: '00000000-0000-4000-8000-000000000000', text }));
    if (problem) {
      this.els.error.textContent = `${capitalise(problem)}.`;
      this.els.error.hidden = false;
      this.els.input.focus();
      return;
    }
    this.#clearError();
    const entry = service.submit({ hostId, sessionId, text: replyRequest({ sessionId, request: '', text }).text });
    this.#sent = { request: entry.request, text };
  }

  #resend() {
    const { service, hostId, sessionId } = this.replying;
    service.resend(hostId, sessionId);
  }

  #show(entry) {
    const e = this.els;
    // Delivered: what went comes out of the box, and only that. Anything typed while it was
    // on its way stays; a box no longer beginning with it has been rewritten, and is kept.
    if (entry?.state === 'done' && entry.outcome === 'delivered' && this.#sent?.request === entry.request) {
      const value = e.input.value;
      if (value.startsWith(this.#sent.text)) { e.input.value = value.slice(this.#sent.text.length).replace(/^\s+/, ''); }
      this.#sent = null;
    }
    const shown = entry ? describeReply(entry, { machineName: this.machineName, working: this.#session?.status === 'working' }) : { text: '', tone: '' };
    e.form.dataset.reply = entry ? (entry.state === 'done' ? (OUTCOME_TEXT[entry.outcome] ? entry.outcome : 'unconfirmed') : entry.state) : 'none';
    e.status.textContent = shown.text;
    e.status.className = `answer-status reply-status${shown.tone ? ` ${shown.tone}` : ''}`;
    e.form.setAttribute('aria-busy', shown.tone === 'progress' ? 'true' : 'false');
    e.resend.hidden = entry?.state !== 'failed';
    if (entry?.state === 'done' && entry.outcome === 'busy' && !this.#busyTimer && this.#busyFor !== entry.request) {
      this.#busyFor = entry.request;
      this.#busyTimer = setTimeout(() => { this.#busyTimer = null; this.#sync(); }, REPLY_BUSY_PAUSE_MS);
    }
    this.#sync();
  }
}
