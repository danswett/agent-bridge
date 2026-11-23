// The code exchange of device approval (design §4.2), the PWA's port of src/AgentBridge.Roster/EnrollmentExchange.cs with no I/O: the
// caller carries the messages over the Noise channel and passes in `h`, that channel's handshake hash. A relay between the two devices
// has two channels with two different `h`, so the two ends compute different codes. The hashes are asynchronous (WebCrypto), so the
// steps that take a peer's message are too; typing a code is not, because the codes are worked out when the transcript is.
// The approver is in enrollment-approver.js.
import { equal } from './roster/codec.js';
import { NONCE_BYTES, commit as commitOf, transcript as transcriptOf, code1 as code1Of, code2 as code2Of, shown, matches } from './roster/enrollment.js';

// Two byte strings compared without stopping at the first difference (a transcript or a commitment); anything but two equal-length arrays is false.
export function macMatches(a, b) {
  if (!(a instanceof Uint8Array) || !(b instanceof Uint8Array) || a.length !== b.length) return false;
  let diff = 0;
  for (let i = 0; i < a.length; i++) diff |= a[i] ^ b[i];
  return diff === 0;
}

// The same device in every field and key (the library's SameDevice): a descriptor as js/roster/entry.js parses it.
const isKey = (k) => k instanceof Uint8Array;
const same = (a, b) => (a == null ? b == null : isKey(a) && isKey(b) && equal(a, b));
const FIELDS = ['id', 'kind', 'platform', 'name', 'presence', 'signAlg', 'hostSignAlg', 'capSignAlg'];
const KEYS = ['dh', 'sign', 'hostSign', 'hostSignProof', 'capSign', 'capSignProof'];
const sameDevice = (a, b) => isKey(a.dh) && FIELDS.every((f) => (a[f] ?? null) === (b[f] ?? null)) && KEYS.every((k) => same(a[k], b[k]))
  && (a.webauthn == null ? b.webauthn == null : b.webauthn != null && a.webauthn.rpId === b.webauthn.rpId && a.webauthn.attested === b.webauthn.attested
    && a.webauthn.backupEligible === b.webauthn.backupEligible && same(a.webauthn.credentialId, b.webauthn.credentialId) && same(a.webauthn.key, b.webauthn.key));

export const bytes = (v, n) => v instanceof Uint8Array && v.length === n; // what a peer sent, checked before it is read
export const randomNonce = () => crypto.getRandomValues(new Uint8Array(NONCE_BYTES));
const WRONG_ENTRIES = 3;
export const ATTEMPT_LIFETIME = 5 * 60_000;
const LOCK_FOR = 15 * 60_000;
const WINDOW = 60 * 60_000;

// The limits of §4.4, counted by the device the person is typing on: a hostile relay must not be able to reset them. `counts` is
// 'wrong-entries' (the approver: three wrong code-1 entries in an hour, over any devices, lock it) or 'ended-attempts' (the joiner:
// three ended attempts in an hour lock its joining). `now` returns milliseconds.
export class EnrollmentLockout {
  #now; #counts; #keys = new Map(); #marks = []; #lockedUntil = null;

  constructor(counts, now) { this.#counts = counts; this.#now = now; }

  // Whether key (the pending device's id, at an approver) may try now: its own three wrong entries, or the device's lockout.
  isBlocked(key) {
    const now = this.#now();
    if (this.#lockedUntil !== null && now < this.#lockedUntil) return true;
    const until = this.#keys.get(key)?.blockedUntil ?? null;
    return until !== null && now < until;
  }

  // Counts a wrong entry in the attempt for `key`; true when it ends the attempt.
  recordWrong(key) {
    const now = this.#now();
    const wrong = (this.#keys.get(key)?.wrong ?? 0) + 1;
    const ended = wrong >= WRONG_ENTRIES;
    this.#keys.set(key, ended ? { wrong: 0, blockedUntil: this.#counts === 'wrong-entries' ? now + LOCK_FOR : null } : { wrong, blockedUntil: this.#keys.get(key)?.blockedUntil ?? null });
    if (this.#counts === 'wrong-entries' || ended) this.#mark(now);
    return ended;
  }

  // An attempt that ran out of time: it counts as an ended attempt at a joiner, and blocks no one at an approver.
  recordEnded() { if (this.#counts === 'ended-attempts') this.#mark(this.#now()); }

  #mark(now) {
    this.#marks = this.#marks.filter((m) => now - m <= WINDOW);
    this.#marks.push(now);
    if (this.#marks.length >= WRONG_ENTRIES) this.#lockedUntil = now + LOCK_FOR;
  }
}

// The joining device's side: it commits to its nonce before it sees the approver's, shows code 1, and checks code 2.
export class EnrollmentJoiner {
  #account; #h; #lockout; #now; #expires; #nN; #commit;
  #transcript = null; #code1 = null; #code2 = null; #taking = false; #committed = false; #matched = false; #ended = false;

  // `expires` (ms) is an earlier deadline than the five minutes, such as the sign-in's own.
  static async create(account, h, lockout, now, { nonce = randomNonce(), expires = null } = {}) {
    return new EnrollmentJoiner(account, h, lockout, now, nonce, expires, await commitOf(h, nonce));
  }

  constructor(account, h, lockout, now, nonce, expires, commit) {
    this.#account = account; this.#h = h; this.#lockout = lockout; this.#now = now; this.#nN = nonce; this.#commit = commit;
    const window = now() + ATTEMPT_LIFETIME;
    this.#expires = expires !== null && expires < window ? expires : window;
  }

  // The commitment to send first; null while joining is locked.
  commit() {
    if (this.#lockout.isBlocked('attempt')) return null;
    this.#committed = true;
    return this.#commit;
  }

  // A → N: the offer. Returns the reveal (N's nonce); N then shows code 1.
  async onOffer(nA) {
    if (!this.#committed || this.#transcript !== null || this.#taking || this.#lockout.isBlocked('attempt') || this.#over() || !bytes(nA, NONCE_BYTES)) return null;
    this.#taking = true; // taken before the first await: two offers at once must not both be answered
    try {
      const t = await transcriptOf(this.#account, this.#h, nA, this.#nN);
      const [c1, c2] = [await code1Of(t), await code2Of(t)];
      if (this.#over()) return null;
      [this.#code1, this.#code2, this.#transcript] = [c1, c2, t];
      return this.#nN;
    } finally { this.#taking = false; }
  }

  // Step 8: the newest entry of a roster the joiner has already verified must add exactly this device, signed by the approver the person
  // confirmed, carrying this exchange's transcript, and only after code 2 matched. A relay that played approver on another channel cannot
  // produce this transcript. The entry is a parsed one; its signature is the roster verifier's to check.
  accepts(newest, own, approverId) {
    const b = newest?.body;
    return typeof approverId === 'string' && approverId !== '' && own != null && typeof own === 'object'
      && this.#matched && !this.#over() && this.#transcript !== null && b?.op === 'add' && b.by === approverId
      && b.device != null && sameDevice(b.device, own) && b.approval?.method === 'code' && macMatches(this.#transcript, b.approval.transcript);
  }

  get code1Shown() { return this.#transcript === null ? null : shown(this.#code1); }

  // What the person typed at N, from the approver's screen.
  enterCode2(input) {
    if (this.#over()) return 'ended';
    if (this.#matched) return 'match';
    if (this.#transcript === null) return 'ended';
    if (this.#lockout.isBlocked('attempt')) return 'locked';
    if (matches(this.#code2, typeof input === 'string' ? input : '')) { this.#matched = true; return 'match'; }
    if (this.#lockout.recordWrong('attempt')) { this.#ended = true; return 'ended'; }
    return 'wrong';
  }

  #over() {
    if (this.#ended) return true;
    if (this.#now() < this.#expires) return false;
    this.#ended = true;
    this.#lockout.recordEnded('attempt');
    return true;
  }
}
