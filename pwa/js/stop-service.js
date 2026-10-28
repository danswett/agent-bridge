// Ending sessions (session.stop), each press to exactly one outcome: AnswerService's machinery
// (a fresh request UUID per press, resent with the same request after a lost response or a
// reconnect, until there is an outcome), with one press on its way per session at a time.
//
// A session that is not idle takes two presses, and the machine enforces it: the first comes
// back `confirm` with a token, and the second press sends that token. No outcome is final: a
// press after any outcome is a new request.
import { AnswerService } from './answer-service.js';

// Where an answer's entry keeps its question, a stop's keeps this.
const STOP = '';

export class StopService extends AnswerService {
  constructor(options) {
    super({
      ...options,
      send: (client, data) => client.stop(data),
      payload: (e) => ({ id: e.sessionId, request: e.request, ...(e.answer.confirm ? { confirm: e.answer.confirm } : {}) }),
      final: () => false,
    });
  }

  get(hostId, sessionId) { return super.get(hostId, sessionId, STOP); }

  // Calls fn(entry) whenever the session's stop changes.
  watch(hostId, sessionId, fn) { return super.watch(hostId, sessionId, STOP, fn); }

  // Presses End session: confirm is the token from a first press's `confirm` outcome, or
  // null. While a press is on its way it is returned as is, and nothing more is sent.
  submit({ hostId, sessionId, confirm = null }) { return super.submit({ hostId, sessionId, questionId: STOP, answer: { confirm } }); }

  resend(hostId, sessionId) { return super.resend(hostId, sessionId, STOP); }
}
