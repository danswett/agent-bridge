// Device approval, the PWA's side (#55, docs/design/device-approval.md section 4; D4, mirroring the Windows app's D3a view models).
// View models only: the exchange, its codes and its lockouts belong to the roster library and arrive as an object with its methods, so
// nothing here has crypto of its own, reaches a relay, or signs.
import { enrollmentRefusalMessage } from './roster/enrollment-refusal.js';

// The account's roster mode as the relay's roster snapshot says it. Anything else, including no answer, is off: no join or approve entry
// exists and nothing behind it is built.
export const rosterModeVisible = (mode) => mode === 'observe' || mode === 'enforce';

// The mode source over a connection (connection.js): its roster mode, which is null until the relay has said one, so the gate is shut.
export const rosterModeSource = (connection) => ({ get mode() { return connection.rosterMode; } });

// What the sign-in screen says after the connection signed out (a close code from the relay): why, in fixed words, for a removed device, and
// what it always said for the rest. Nothing the relay sent is shown.
export const signedOutText = (closed) => (closed?.code === 4030 ? EnrollmentTexts.forRefusal('removed') : 'The relay no longer accepts this sign-in. Sign in again.');

export const openDeviceApproval = (source, make) => (rosterModeVisible(source?.mode) ? make() : null);

// What proves a person is present at this device (section 4.6): kind is 'key', 'webauthn' or 'none'. The PWA has none yet (the passkey
// proof is a later, owner-approved slice), so approving from it is not possible until then.
export class NoPresence {
  kind = 'none';
  trySign() { return null; }
}

// The fixed words the screens use. No text from a peer, the relay or an exception is ever shown.
export const EnrollmentTexts = {
  WRONG: 'That code is not right. Try again.',
  ENDED: 'This attempt has ended. Start again.',
  LOCKED: 'Joining is locked for 15 minutes.',
  GENERIC: 'This device could not be added.',
  TOO_MANY_DEVICES: 'This account has too many devices connected or waiting. It will try again.',

  // A closed code to its words, from the one module that lists them (roster/enrollment-refusal.js, the library's codes and words); anything
  // else, or nothing, is the generic line, so a code this build does not have degrades safely.
  forRefusal: (code) => enrollmentRefusalMessage(code) || EnrollmentTexts.GENERIC,

  // A code result of the exchange ('match', 'wrong', 'locked', 'ended') to words.
  forResult: (result) => (result === 'wrong' ? EnrollmentTexts.WRONG : result === 'locked' ? EnrollmentTexts.LOCKED : result === 'ended' ? EnrollmentTexts.ENDED : ''),

  // Hex in groups of four: the first 64 bits of a hash, or an id's first sixteen characters.
  groups: (hex) => hex.match(/.{4}/g)?.join(' ') ?? '',
  fingerprint: (genesisHash) => EnrollmentTexts.groups([...genesisHash.subarray(0, 8)].map((b) => b.toString(16).padStart(2, '0')).join('')),
};
