// The approver's side of the code exchange (design §4.2), the PWA's port of EnrollmentApprover; see enrollment-exchange.js.
import { NONCE_BYTES, commit as commitOf, transcript as transcriptOf, code1 as code1Of, code2 as code2Of, shown, matches } from './roster/enrollment.js';
import { bytes, macMatches, randomNonce, ATTEMPT_LIFETIME } from './enrollment-exchange.js';

export class EnrollmentApprover {
  #account; #h; #pendingId; #lockout; #now; #expires;
  #commit = null; #nA = null; #transcript = null; #code1 = null; #code2 = null; #revealing = false; #matched = false; #ended = false;

  constructor(account, h, pendingId, lockout, now) {
    this.#account = account; this.#h = h; this.#pendingId = pendingId; this.#lockout = lockout; this.#now = now;
    this.#expires = now() + ATTEMPT_LIFETIME;
  }

  // N → A: the commitment. Returns A's offer, its nonce; null if the attempt cannot go on.
  async onCommit(commit, nonce = randomNonce()) {
    if (this.#commit !== null || this.#over() || !bytes(commit, 32)) return null;
    this.#commit = commit;
    this.#nA = nonce;
    return nonce;
  }

  // N → A: the reveal. False abandons the attempt: N's nonce is not the one it committed to.
  async onReveal(nN) {
    if (this.#commit === null || this.#transcript !== null || this.#revealing || this.#over()) return false;
    this.#revealing = true; // taken before the first await: a second reveal while this one is checked is not an abandonment
    try {
      if (!bytes(nN, NONCE_BYTES) || !macMatches(this.#commit, await commitOf(this.#h, nN))) { this.#ended = true; return false; }
      const t = await transcriptOf(this.#account, this.#h, this.#nA, nN);
      const [c1, c2] = [await code1Of(t), await code2Of(t)];
      if (this.#over()) return false;
      [this.#code1, this.#code2, this.#transcript] = [c1, c2, t];
      return true;
    } finally { this.#revealing = false; }
  }

  // What the person typed at A, from N's screen: 'match', 'wrong', 'ended' or 'locked'. Code 2 is shown only after a match.
  enterCode1(input) {
    if (this.#matched) return 'match';
    if (this.#transcript === null || this.#over()) return this.#ended ? 'ended' : 'wrong';
    if (this.#lockout.isBlocked(this.#pendingId)) return 'locked';
    if (matches(this.#code1, typeof input === 'string' ? input : '')) { this.#matched = true; return 'match'; }
    if (this.#lockout.recordWrong(this.#pendingId)) { this.#ended = true; return 'ended'; }
    return 'wrong';
  }

  // Code 2, for the person to type at N: only after code 1 matched.
  get code2Shown() { return this.#matched && !this.#over() && this.#transcript !== null ? shown(this.#code2) : null; }

  // The value the signed add's `approval.transcript` carries: only after code 1 matched, and the approver still has to see N's `ok`.
  get transcriptToSign() { return this.#matched && !this.#over() ? this.#transcript : null; }

  #over() {
    if (this.#ended) return true;
    if (this.#now() < this.#expires) return false;
    this.#ended = true;
    this.#lockout.recordEnded(this.#pendingId);
    return true;
  }
}

