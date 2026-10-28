// A session's End session control: agent-ha-bridge's footer button, ending the session on the
// machine (session.stop). Shown when the machine says it can end sessions (`sessions.stop`);
// ../stop-service.js sends each press, and resends it with the same request until there is
// an outcome.
//
// A session that is not idle (working, waiting on you, or waiting for background work it
// started) takes two presses, as on the old dashboard. The machine enforces it, not this:
// the first press comes back asking for a second, with a token the second press sends, and
// the machine ends the session only if that second press comes within its window and the
// session is still the same process. Here the first press's answer is shown with the time
// left, and a press after the window has lapsed is another first press.
import { h, icon } from './dom.js';

export const CONFIRM_NOTE = 'Press End session again to end it';
export const LAPSED_NOTE = 'End session NOT confirmed';

// After busy, End session comes back after this long, as Send does after a busy reply.
export const STOP_BUSY_PAUSE_MS = 2000;

const where = (machineName) => (machineName ? ` on ${machineName}` : '');
const sentence = (s) => (s && !/[.!?]$/.test(s) ? `${s}.` : s);

// What a press's progress or outcome says. armed: { left } (seconds) while a first press is
// waiting for its second, or lapsed: true once the window has passed. tone: progress | ok |
// warn | bad.
export function describeStop(entry, { machineName, armed = null, lapsed = false } = {}) {
  switch (entry.state) {
    case 'sending':
      return { text: entry.answer?.confirm ? 'Ending the session…' : 'Sending…', tone: 'progress' };
    case 'waiting':
      return {
        text: entry.reason === 'still being delivered'
          ? 'The machine is still ending it…'
          : `Waiting for the connection to the machine${where(machineName)}. Your press goes as soon as it is back.`,
        tone: 'progress',
      };
    case 'failed':
      return { text: `Couldn't send it: ${entry.reason ?? 'the machine said no'}.`, tone: 'bad' };
    default:
      break;
  }
  const d = entry.detail ?? {};
  switch (entry.outcome) {
    case 'confirm':
      if (lapsed || !armed) {
        return { text: `${LAPSED_NOTE}: no second press within ${d.confirmSeconds ?? 10} s. The session is still running.`, tone: 'warn' };
      }
      return { text: `${CONFIRM_NOTE}: ${sentence(entry.reason ?? 'this session is not idle')} ${armed.left} s left.`, tone: 'warn' };
    case 'ended':
      return { text: d.forced ? 'Session ended. It did not exit on its own, so the machine terminated it.' : 'Session ended.', tone: 'ok' };
    case 'stale':
      return { text: 'This session had already ended.', tone: 'warn' };
    case 'busy':
      return { text: 'Not ended: an answer or reply to this session is being delivered, or it is already being ended. Try again in a moment.', tone: 'warn' };
    case 'refused':
      return { text: `The machine refused to end this session${entry.reason ? `: ${sentence(entry.reason)}` : '.'}`, tone: 'bad' };
    default:
      return { text: `Couldn't end the session${entry.reason ? `: ${sentence(entry.reason)}` : '. Check at the machine.'}`, tone: 'bad' };
  }
}

export class EndSession {
  #unwatch = null;
  #session = null;
  // The first press waiting for its second: its request, and when its answer arrived here.
  #armed = null;
  #tick = null;
  #busyTimer = null;
  #busyFor = null;

  // stopping: { service, hostId, sessionId }.
  constructor({ machineName, stopping, now = () => Date.now() }) {
    this.machineName = machineName;
    this.stopping = stopping;
    this.now = now;
    this.#build();
    const { service, hostId, sessionId } = stopping;
    this.#unwatch = service.watch(hostId, sessionId, (e) => this.#show(e));
  }

  destroy() {
    this.#unwatch?.();
    clearInterval(this.#tick);
    clearTimeout(this.#busyTimer);
  }

  get entry() { const { service, hostId, sessionId } = this.stopping; return service.get(hostId, sessionId); }

  #build() {
    const e = this.els = {};
    e.button = h('button', { type: 'button', class: 'end-button', onclick: () => this.#press() }, icon('stop'), h('span', null, 'End session'));
    e.resend = h('button', { type: 'button', class: 'button secondary resend', hidden: true, onclick: () => this.#resend() }, 'Try again');
    e.status = h('p', { class: 'answer-status stop-status', role: 'status', 'aria-live': 'polite' });
    this.element = h('div', { class: 'end-session', dataset: { stop: 'none' } }, e.button, e.status, e.resend);
  }

  update(session) {
    this.#session = session;
    this.#show(this.entry);
  }

  #pending(entry = this.entry) { return !!entry && (entry.state === 'sending' || entry.state === 'waiting'); }

  // Seconds left for the second press, or null when no first press is waiting for one.
  #left(entry = this.entry) {
    if (!this.#armed || entry?.state !== 'done' || entry.outcome !== 'confirm' || entry.request !== this.#armed.request) { return null; }
    const ms = this.#armed.at + this.#armed.seconds * 1000 - this.now();
    return ms > 0 ? Math.ceil(ms / 1000) : null;
  }

  #press() {
    const entry = this.entry;
    if (this.#pending(entry) || this.#busyTimer || this.#session?.status === 'ending') { return; }
    const { service, hostId, sessionId } = this.stopping;
    const confirm = this.#left(entry) !== null ? entry.detail.confirm : null;
    service.submit({ hostId, sessionId, confirm });
  }

  #resend() {
    const { service, hostId, sessionId } = this.stopping;
    service.resend(hostId, sessionId);
  }

  #show(entry) {
    const e = this.els;
    if (entry?.state === 'done' && entry.outcome === 'confirm' && this.#armed?.request !== entry.request) {
      this.#armed = { request: entry.request, at: this.now(), seconds: entry.detail?.confirmSeconds ?? 10 };
      clearInterval(this.#tick);
      this.#tick = setInterval(() => {
        if (this.#left() === null) { clearInterval(this.#tick); this.#tick = null; }
        this.#show(this.entry);
      }, 1000);
    }
    const left = this.#left(entry);
    const shown = entry
      ? describeStop(entry, { machineName: this.machineName, armed: left === null ? null : { left }, lapsed: left === null })
      : { text: '', tone: '' };
    const state = !entry ? 'none' : entry.state !== 'done' ? entry.state : entry.outcome === 'confirm' ? (left === null ? 'lapsed' : 'confirm') : entry.outcome;
    this.element.dataset.stop = state;
    e.status.textContent = shown.text;
    e.status.className = `answer-status stop-status${shown.tone ? ` ${shown.tone}` : ''}`;
    this.element.setAttribute('aria-busy', shown.tone === 'progress' ? 'true' : 'false');
    e.resend.hidden = entry?.state !== 'failed';
    if (entry?.state === 'done' && entry.outcome === 'busy' && !this.#busyTimer && this.#busyFor !== entry.request) {
      this.#busyFor = entry.request;
      this.#busyTimer = setTimeout(() => { this.#busyTimer = null; this.#show(this.entry); }, STOP_BUSY_PAUSE_MS);
    }
    const ending = this.#session?.status === 'ending';
    e.button.disabled = this.#pending(entry) || !!this.#busyTimer || ending;
    e.button.setAttribute('aria-label', left === null ? 'End session' : `End session: press again to confirm, ${left} seconds left`);
  }
}
