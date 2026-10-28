// Sending replies, and getting each one to exactly one outcome: AnswerService's machinery
// (a fresh request UUID per reply, resent with the same request after a lost response or a
// reconnect, until there is an outcome), with one reply on its way per session at a time.
//
// Unlike an answer, no outcome is final for a session: once a reply has its outcome, the
// person can send another, which is a new request.
import { AnswerService } from './answer-service.js';

// Where an answer's entry keeps its question, a reply's keeps this.
const REPLY = '';

export class ReplyService extends AnswerService {
  constructor(options) {
    super({
      ...options,
      send: (client, data) => client.reply(data),
      payload: (e) => ({ id: e.sessionId, request: e.request, text: e.answer.text }),
      final: () => false,
    });
  }

  get(hostId, sessionId) { return super.get(hostId, sessionId, REPLY); }

  // Calls fn(entry) whenever the session's reply changes.
  watch(hostId, sessionId, fn) { return super.watch(hostId, sessionId, REPLY, fn); }

  // Sends a reply. While one to this session is on its way it is returned as is, and nothing
  // more is sent.
  submit({ hostId, sessionId, text }) { return super.submit({ hostId, sessionId, questionId: REPLY, answer: { text } }); }

  resend(hostId, sessionId) { return super.resend(hostId, sessionId, REPLY); }
}
