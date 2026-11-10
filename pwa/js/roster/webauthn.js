// A WebAuthn assertion as a presence proof (docs/protocol/devices.md, "Presence"), checked against
// the credential recorded in the signer's add, in the order the C# library's WebAuthnAssertion
// runs the checks, so one defect gives one code.
import { RosterFormatError, REFUSAL } from './refusal.js';
import { base64Url, equal } from './codec.js';
import { sha256, utf8 } from './framing.js';
import { verify, checkRosterSignature } from './p256.js';
import { parseStrict } from './strict-json.js';

const USER_PRESENT = 0x01;
const USER_VERIFIED = 0x04;
const BACKUP_ELIGIBLE = 0x08;
const BACKED_UP = 0x10;
const ATTESTED_CREDENTIAL_DATA = 0x40;
const EXTENSIONS = 0x80;

export const AUTHENTICATOR_DATA_BYTES = 37;

export const origin = (rpId) => `https://${rpId}`;

const member = (root, name) => {
  const node = root.get(name);
  return node?.t === 'string' ? node.v : null;
};

// Resolves when the assertion proves presence for this credential and challenge; rejects with a
// RosterFormatError naming the first failed check.
export async function checkAssertion(assertion, credential, challenge) {
  const data = assertion.authenticatorData;
  if (data.length !== AUTHENTICATOR_DATA_BYTES || (data[32] & (ATTESTED_CREDENTIAL_DATA | EXTENSIONS)) !== 0) {
    throw new RosterFormatError(REFUSAL.uvAuthenticatorData, 'authenticatorData');
  }
  if (!equal(data.subarray(0, 32), await sha256(utf8(credential.rpId)))) throw new RosterFormatError(REFUSAL.uvRpId, 'rpIdHash');
  const flags = data[32];
  if (!(flags & USER_PRESENT) || !(flags & USER_VERIFIED) || ((flags & BACKED_UP) && !(flags & BACKUP_ELIGIBLE))) {
    throw new RosterFormatError(REFUSAL.uvFlags, 'flags');
  }
  // A synced credential never proves presence in v1, and every assertion repeats the
  // registration's BE flag.
  if (credential.backupEligible || Boolean(flags & BACKUP_ELIGIBLE) !== credential.backupEligible) {
    throw new RosterFormatError(REFUSAL.uvBackup, 'BE flag');
  }

  let root;
  try { root = parseStrict(assertion.clientDataJSON, 'clientDataJSON'); } catch (e) {
    if (e instanceof RosterFormatError) throw new RosterFormatError(REFUSAL.uvClientData, 'clientDataJSON');
    throw e;
  }
  if (root.t !== 'object') throw new RosterFormatError(REFUSAL.uvClientData, 'clientDataJSON');
  if (member(root, 'type') !== 'webauthn.get') throw new RosterFormatError(REFUSAL.uvType, 'type');
  if (member(root, 'challenge') !== base64Url(challenge)) throw new RosterFormatError(REFUSAL.uvChallenge, 'challenge');
  if (member(root, 'origin') !== origin(credential.rpId)
    || (root.has('crossOrigin') && root.get('crossOrigin').t !== 'false')
    || root.has('topOrigin')) {
    throw new RosterFormatError(REFUSAL.uvOrigin, 'origin');
  }

  checkRosterSignature(assertion.signature, 'signature');
  const signed = new Uint8Array(data.length + 32);
  signed.set(data, 0);
  signed.set(await sha256(assertion.clientDataJSON), data.length);
  if (!(await verify(credential.key, signed, assertion.signature))) throw new RosterFormatError(REFUSAL.uvSignature, 'signature');
}
