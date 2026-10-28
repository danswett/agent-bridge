// Sending replies, and getting each one to exactly one outcome: AnswerService's machinery
// (a fresh request UUID per reply, resent with the same request after a lost response or a
// reconnect, until there is an outcome), with one reply on its way per session at a time.
//
// Unlike an answer, no outcome is final for a session: once a reply has its outcome, the
// person can send another, which is a new request.
//
// A reply's files go to the machine first (attachments.js), on every attempt: the machine
// says how much of each it has, so a resend after a reconnect sends only what is missing,
// and one after the machine restarted sends them again rather than naming files it has lost.
// The entry's progress is { sent, total } in bytes while they go.
import { AnswerService } from './answer-service.js';
import { uploadAttachment } from './attachments.js';

// Where an answer's entry keeps its question, a reply's keeps this.
const REPLY = '';

export class ReplyService extends AnswerService {
  #current;

  constructor(options) {
    super({
      ...options,
      send: async (client, data, entry, progress) => {
        const files = entry?.answer?.attachments ?? [];
        const total = files.reduce((n, f) => n + f.size, 0);
        let done = 0;
        for (const file of files) {
          const before = done;
          await uploadAttachment(client, file, (n) => progress({ sent: before + n, total }));
          done += file.size;
        }
        return client.reply(data);
      },
      payload: (e) => ({
        id: e.sessionId, request: e.request, text: e.answer.text,
        ...(e.answer.attachments?.length ? { attachments: e.answer.attachments.map((f) => ({ id: f.id })) } : {}),
      }),
      final: () => false,
    });
    this.#current = options.current;
  }

  get(hostId, sessionId) { return super.get(hostId, sessionId, REPLY); }

  // Calls fn(entry) whenever the session's reply changes.
  watch(hostId, sessionId, fn) { return super.watch(hostId, sessionId, REPLY, fn); }

  // Sends a reply, with any prepared files. While one to this session is on its way it is
  // returned as is, and nothing more is sent.
  submit({ hostId, sessionId, text, attachments = [] }) {
    return super.submit({ hostId, sessionId, questionId: REPLY, answer: { text, attachments } });
  }

  resend(hostId, sessionId) { return super.resend(hostId, sessionId, REPLY); }

  // A file the person took out of the box: if the machine has it, it can let it go now rather
  // than when it expires. Best effort; nothing waits on it.
  discard(hostId, attachment) {
    const cur = this.#current();
    if (!cur?.client || cur.client.closed || cur.hostId !== hostId || typeof cur.client.attachmentDiscard !== 'function') { return; }
    cur.client.attachmentDiscard(attachment).catch(() => {});
  }
}
