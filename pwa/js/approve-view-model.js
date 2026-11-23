// The approving device A (device-approval.md section 4.1 steps 2 and 4). The person types code 1 (never compares and taps), may edit the
// name, and only then sees code 2. Approving needs a presence proof; without one this ends in cannot-approve and neither asks the proof
// nor signs. The banner is plain text from the device: a screen must draw it with textContent, never as HTML. The model never signs: with a
// proof it hands the transcript, the name and the proof to `sign`, the caller's one signing path.
import { EnrollmentTexts } from './device-approval.js';

export const ApproveTexts = {
  CANNOT_APPROVE: 'This device cannot approve yet.',

  kindText: (kind, platform) => (kind === 'app' && platform === 'windows' ? 'a Windows app'
    : kind === 'app' ? 'an app' : kind === 'pwa' ? 'a browser app' : kind === 'phone' ? 'a phone' : kind === 'host' ? 'a host'
      : 'a device of an unknown kind'),

  ago: (ms) => (!Number.isFinite(ms) || ms < 60_000 ? 'just now' : ms < 3_600_000 ? `${Math.floor(ms / 60_000)} min ago` : `${Math.floor(ms / 3_600_000)} h ago`),
};

const MAX_NAME_SCALARS = 64;
// C0 and C1 controls (the Windows app's char.IsControl) and the characters that hide or reorder text: zero-width and direction marks,
// line and paragraph separators, direction overrides and isolates, and the byte order mark.
const HIDDEN = '\\u0000-\\u001f\\u007f-\\u009f\\u200b-\\u200f\\u2028-\\u202e\\u2060-\\u2069\\ufeff';
const HAS_HIDDEN = new RegExp(`[${HIDDEN}]`, 'u');
const HIDDEN_ALL = new RegExp(`[${HIDDEN}]`, 'gu');
// What a device calls itself, made safe to show and to sign: nothing hidden, at most 64 characters, never empty.
const cleanName = (value) => [...(typeof value === 'string' ? value : '').replace(HIDDEN_ALL, '').trim()].slice(0, MAX_NAME_SCALARS).join('').trim() || 'unnamed device';

export class ApproveViewModel {
  // waiting-for-code-1 | code-2-shown | ready-to-approve | cannot-approve | approved | ended | locked. Only the model moves these.
  #state = 'waiting-for-code-1';
  #statusText = '';
  #name; #pending; #signing = false;
  #approver; #proof; #sign; #now;

  // pending is what the device said about itself: { name, kind, platform, id, signedInAt (ms) }; shown as given, never as a trusted name.
  constructor(approver, pending, proof, sign, now = Date.now) {
    this.#approver = approver;
    this.#pending = Object.freeze({ ...pending }); // a copy: the caller cannot change who this is about once it is shown
    this.#proof = proof;
    this.#sign = sign;
    this.#now = now;
    this.#name = cleanName(pending.name);
  }

  get state() { return this.#state; }
  get statusText() { return this.#statusText; }
  get name() { return this.#name; }
  get pending() { return this.#pending; }
  get canApprove() { return this.#proof.kind === 'key' || this.#proof.kind === 'webauthn'; }
  // The id is what the device said: only hex is shown as an id, anything else as a fixed placeholder.
  get idShown() { return typeof this.#pending.id === 'string' && /^[0-9a-f]{16,}$/i.test(this.#pending.id) ? EnrollmentTexts.groups(this.#pending.id.slice(0, 16)) : 'unknown'; }
  get banner() {
    const p = this.#pending;
    return `A new device wants to join: ${cleanName(p.name)}, says it is ${ApproveTexts.kindText(p.kind, p.platform)}, id ${this.idShown}, `
      + `signed in ${ApproveTexts.ago(this.#now() - p.signedInAt)}`;
  }

  // Code 2, for the person to type at the joining device: null until code 1 has matched.
  get code2() { return this.#approver.code2Shown ?? null; }

  // One to sixty-four characters, nothing hidden. False keeps the old name.
  setName(value) {
    const trimmed = value.trim();
    if (trimmed === '' || [...trimmed].length > MAX_NAME_SCALARS || HAS_HIDDEN.test(trimmed)) return false;
    this.#name = trimmed;
    return true;
  }

  enterCode1(input) {
    if (this.#state !== 'waiting-for-code-1') return 'ended';
    const result = this.#approver.enterCode1(input);
    this.#statusText = EnrollmentTexts.forResult(result);
    this.#state = result === 'match' ? 'code-2-shown' : result === 'wrong' ? this.#state : result === 'locked' ? 'locked' : 'ended';
    return result;
  }

  // The joining device typed code 2 and said so (section 4.2 step 6).
  onJoinerOk() {
    if (this.#state === 'code-2-shown') this.#state = 'ready-to-approve';
  }

  // `sign` may answer at once or later; only a true counts, and a refusal or a throw is a failure that can be tried again.
  async approve() {
    if (!this.canApprove) { this.#state = 'cannot-approve'; this.#statusText = ApproveTexts.CANNOT_APPROVE; return 'cannot-approve'; }
    if (this.#state !== 'ready-to-approve') return 'not-ready';
    const transcript = this.#approver.transcriptToSign;
    if (!transcript) { this.#state = 'ended'; this.#statusText = EnrollmentTexts.ENDED; return 'failed'; }
    if (this.#signing) return 'not-ready';
    this.#signing = true;
    let signed = false;
    try { signed = (await this.#sign({ transcript, name: this.#name, proof: this.#proof })) === true; } catch { signed = false; } finally { this.#signing = false; }
    if (!signed) { this.#statusText = EnrollmentTexts.GENERIC; return 'failed'; }
    this.#state = 'approved';
    this.#statusText = '';
    return 'approved';
  }
}
