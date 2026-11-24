// The joining device N (device-approval.md section 4.1 steps 1 and 3, section 4.2): it shows code 1, then what it is joining, and the
// person types code 2. It is driven by the exchange's own events alone, so the screen never waits on presence or a roster answer.
import { EnrollmentTexts } from './device-approval.js';

export class JoinViewModel {
  state = 'not-started'; // not-started | locked | waiting-for-approver | enter-code-2 | joined | ended
  code1 = '';
  fingerprint = '';
  statusText = '';
  #joiner;

  constructor(joiner) { this.#joiner = joiner; }

  // The commitment to send first; null while joining is locked (three ended attempts in an hour).
  commit() {
    if (this.state !== 'not-started' && this.state !== 'locked') return null; // one attempt at a time; a new attempt is a new model
    const commit = this.#joiner.commit();
    if (commit === null) { this.state = 'locked'; this.statusText = EnrollmentTexts.LOCKED; return null; }
    this.state = 'waiting-for-approver';
    return commit;
  }

  // The approver's offer: the reveal to send back, and code 1 to show. Null when the offer cannot be taken.
  onOffer(nonceA) {
    if (this.state !== 'waiting-for-approver' || this.code1 !== '') return null;
    const reveal = this.#joiner.onOffer(nonceA);
    if (reveal === null) return null;
    this.code1 = this.#joiner.code1Shown ?? '';
    return reveal;
  }

  // The roster it would join arrived (its genesis hash). The code 2 box appears only after code 1 is shown.
  showGenesis(genesisHash) {
    // A hash shorter than the 8 bytes the fingerprint shows would be a blank or cut-short fingerprint for the person to confirm.
    if (this.state !== 'waiting-for-approver' || this.code1 === '' || !(genesisHash instanceof Uint8Array) || genesisHash.length < 8) return;
    this.fingerprint = EnrollmentTexts.fingerprint(genesisHash);
    this.state = 'enter-code-2';
  }

  // What the person typed here from the approver's screen.
  submit(input) {
    if (this.state !== 'enter-code-2') return 'ended';
    const result = this.#joiner.enterCode2(input);
    this.statusText = EnrollmentTexts.forResult(result);
    this.state = result === 'match' ? 'joined' : result === 'wrong' ? this.state : result === 'locked' ? 'locked' : 'ended';
    return result;
  }
}
