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
// A large one goes through the relay's blob store, uploaded once and fetched by the machine.
// The entry's progress is { sent, total, phase } in bytes while they go, and until the reply
// itself is sent the person can cancel, which stops an upload where it is.
import { AnswerService } from './answer-service.js';
import { uploadAttachment } from './attachments.js';

// Where an answer's entry keeps its question, a reply's keeps this.
const REPLY = '';

export const CANCELLED = 'cancelled';

export class ReplyService extends AnswerService {
  #current;
  #blobs;
  // The attempt sending each session's files now, by entry key: aborting it cancels the reply.
  #sending = new Map();

  // blobs(): the blob store through the current relay connection (blobs.js RelayBlobs), or null.
  constructor(options) {
    super({
      ...options,
      send: async (client, data, entry, progress) => {
        const files = entry?.answer?.attachments ?? [];
        const abort = new AbortController();
        this.#sending.set(entry.key, abort);
        try {
          const total = files.reduce((n, f) => n + f.size, 0);
          let done = 0;
          for (const file of files) {
            const before = done;
            await uploadAttachment(client, file, (n, phase) => progress({ sent: before + n, total, phase }),
              { blobs: this.#blobs?.() ?? null, hostId: entry.hostId, signal: abort.signal });
            done += file.size;
          }
          // Past here the reply is on its way and can no longer be called back.
          abort.signal.throwIfAborted();
          this.#sending.delete(entry.key);
          if (files.length) { progress({ sent: total, total, phase: 'reply' }); }
          return await client.reply(data);
        } catch (e) {
          if (abort.signal.aborted) { throw Object.assign(new Error(CANCELLED), { fromHost: true, reason: CANCELLED }); }
          throw e;
        } finally {
          if (this.#sending.get(entry.key) === abort) { this.#sending.delete(entry.key); }
        }
      },
      payload: (e) => ({
        id: e.sessionId, request: e.request, text: e.answer.text,
        ...(e.answer.attachments?.length ? { attachments: e.answer.attachments.map((f) => ({ id: f.id })) } : {}),
      }),
      final: () => false,
    });
    this.#current = options.current;
    this.#blobs = options.blobs;
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

  // Whether the session's reply is still sending its files, and so can be cancelled.
  cancellable(hostId, sessionId) { return this.#sending.has(AnswerService.key(hostId, sessionId, REPLY)); }

  // Stops the files on their way and the reply with them, if it has not gone yet. The entry
  // fails with reason 'cancelled'; the files stay in the box, and an upload already finished
  // is reused when the person sends again.
  cancel(hostId, sessionId) {
    const abort = this.#sending.get(AnswerService.key(hostId, sessionId, REPLY));
    if (!abort) { return false; }
    abort.abort();
    return true;
  }

  // A file the person took out of the box: if the machine has it, it can let it go now rather
  // than when it expires, and a blob uploaded for it is deleted from the store. Best effort;
  // nothing waits on it.
  discard(hostId, attachment, blob = null) {
    if (blob) { this.#blobs?.()?.delete(blob).catch(() => {}); }
    const cur = this.#current(hostId);
    if (!cur?.client || cur.client.closed || cur.hostId !== hostId || typeof cur.client.attachmentDiscard !== 'function') { return; }
    cur.client.attachmentDiscard(attachment).catch(() => {});
  }
}
