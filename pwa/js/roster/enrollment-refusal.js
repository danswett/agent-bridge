// Why an enrollment is refused (design §4.4): the same codes, in the same order, as the C# library's
// (src/AgentBridge.Roster/EnrollmentRefusal.cs), which docs/protocol/devices.md ("Enrollment refusals") lists.
export const ENROLLMENT_REFUSAL = Object.freeze({
  wrongCode: 'wrong-code',
  expired: 'expired',
  alreadyApproved: 'already-approved',
  removed: 'removed',
  unknownKind: 'unknown-kind',
  rosterLimit: 'roster-limit',
  approverProbation: 'approver-probation',
  noPresence: 'no-presence',
});

export const ENROLLMENT_REFUSALS = Object.freeze(Object.values(ENROLLMENT_REFUSAL));

export function isEnrollmentRefusal(code) {
  return ENROLLMENT_REFUSALS.includes(code);
}

// What a person is told; '' for a code this build does not have, so a screen shows its generic text.
const MESSAGE = {
  'wrong-code': 'That code is not the one this device shows.',
  expired: 'This attempt ran out of time. Start again.',
  'already-approved': 'This device is already approved.',
  removed: 'This device was removed; sign out and in again to make a new key.',
  'unknown-kind': 'This kind of device cannot be approved.',
  'roster-limit': 'This account has the most devices it can have; remove one first.',
  'approver-probation': 'This device is too new to approve others yet.',
  'no-presence': 'This device cannot prove that a person is here, so it cannot approve.',
};

export function enrollmentRefusalMessage(code) {
  return Object.hasOwn(MESSAGE, code) ? MESSAGE[code] : '';
}
