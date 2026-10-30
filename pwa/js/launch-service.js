// Launching sessions (session.launch), each press to exactly one outcome: AnswerService's
// machinery (a fresh request UUID per press, resent with the same request after a lost response
// or a reconnect, until there is an outcome), with one launch on its way per machine at a time.
// No outcome is final: a press after any outcome is a new launch.
import { AnswerService } from './answer-service.js';

// Where an answer's entry keeps its session and question, a launch's keeps these.
const LAUNCH = 'launch';
// A launch that brings a session from another machine waits up to twelve minutes for its
// outcome (host-client.js), so it is resent for longer than other ops: past one lost response
// and its resend. A host taking transfers remembers a launch's outcome for 25 minutes after it
// settles (docs/protocol/v1-transfer.md), which is after the first send, so no resend in these
// 20 minutes finds it forgotten and runs the launch again.
export const TRANSFER_GIVE_UP_MS = 20 * 60000;

export class LaunchService extends AnswerService {
  constructor(options) {
    super({
      ...options,
      send: (client, data) => client.launch(data),
      payload: (e) => ({ ...e.answer, request: e.request }),
      final: () => false,
      resendOnBusy: false,
      giveUpFor: (e) => (e.answer?.transfer ? Math.max(TRANSFER_GIVE_UP_MS, options.giveUpMs ?? 0) : options.giveUpMs ?? 9 * 60000),
    });
  }

  get(hostId) { return super.get(hostId, LAUNCH, ''); }

  // Calls fn(entry) whenever this machine's launch changes.
  watch(hostId, fn) { return super.watch(hostId, LAUNCH, '', fn); }

  // Presses Launch with session.launch's settings (everything but request). While one is on
  // its way it is returned as is, and nothing more is sent.
  submit({ hostId, launch }) { return super.submit({ hostId, sessionId: LAUNCH, questionId: '', answer: launch }); }

  resend(hostId) { return super.resend(hostId, LAUNCH, ''); }
}
