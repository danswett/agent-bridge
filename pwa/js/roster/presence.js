// A signer's presence proof (§4.6, decision 3(a)), for entries, vetoes and person commands alike:
// the PWA's port of PresenceProof in src/AgentBridge.Roster/Commands.cs.
import { RosterFormatError } from './refusal.js';
import { presenceChallenge } from './entry.js';
import { checkAssertion } from './webauthn.js';
import { INVALID } from './invalid.js';

// `key` signs with no assertion, `webauthn` needs the signer's own assertion over the role's
// challenge, and `none` signs nothing.
export async function presenceProof(body, device, role, uv) {
  switch (device.presence) {
    case 'key':
      return uv === null ? null : INVALID.presence;
    case 'webauthn':
      if (uv === null) return INVALID.presence;
      try {
        await checkAssertion(uv, device.webauthn, await presenceChallenge(role, body));
      } catch (e) {
        if (e instanceof RosterFormatError) return INVALID.presence;
        throw e;
      }
      return null;
    default:
      return INVALID.presence;
  }
}
