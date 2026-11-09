// The values the code exchange and the QR paths compute (docs/protocol/devices.md, "Enrollment"),
// as the C# library's Enrollment: both sides of an exchange must compute them alike.
import { hex } from './codec.js';
import { frame, utf8, sha256, hmacSha256, LABEL } from './framing.js';

export const NONCE_BYTES = 32;
export const QR_SECRET_BYTES = 16;

// N's commitment to its nonce: SHA-256(F(commit label, h, nN)).
export const commit = (h, nN) => sha256(frame(utf8(LABEL.enrollCommit), h, nN));

// What a joining person's device signs with its `sign` key on this channel.
export const keyProof = (h) => frame(utf8(LABEL.enrollKey), h);

// T = SHA-256(F(label, account, h, nA, nN)).
export const transcript = (account, h, nA, nN) => sha256(frame(utf8(LABEL.enroll), utf8(account), h, nA, nN));

async function code(label, t) {
  const hash = await sha256(frame(utf8(label), t));
  const value = new DataView(hash.buffer, hash.byteOffset, 4).getUint32(0) % 1_000_000;
  return String(value).padStart(6, '0');
}

export const code1 = (t) => code(LABEL.enrollCode1, t);
export const code2 = (t) => code(LABEL.enrollCode2, t);

// A code as shown: two groups of three.
export const shown = (c) => `${c.slice(0, 3)} ${c.slice(3)}`;

// What the person typed, spaces dropped, if it is exactly six ASCII digits; else null.
export function typed(input) {
  const c = input.replaceAll(' ', '');
  return /^[0-9]{6}$/.test(c) ? c : null;
}

// Whether the person typed the expected code, compared without stopping at the first difference.
export function matches(expected, input) {
  const t = typed(input);
  if (t === null || t.length !== expected.length) return false;
  let diff = 0;
  for (let i = 0; i < t.length; i++) diff |= t.charCodeAt(i) ^ expected.charCodeAt(i);
  return diff === 0;
}

// The hint a QR-joining device shows the relay: the first 8 bytes of HMAC(q, F(label)), as hex.
export const inviteHint = async (q) => hex((await hmacSha256(q, frame(utf8(LABEL.inviteHint)))).subarray(0, 8));

export const qrJoiner = (q, h) => hmacSha256(q, frame(utf8(LABEL.qrJoiner), h));
export const qrApprover = (q, h) => hmacSha256(q, frame(utf8(LABEL.qrApprover), h));
