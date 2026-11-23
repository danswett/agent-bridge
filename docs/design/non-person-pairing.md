# Pairing without a person at the machine (non-person principals)

**Status: PROPOSED CHANGE. Owner approval required; not in force.** Part A below records the rules that are in
force today. Part B is a proposal that would change one of them. Nothing here authorizes code, a credential, a
policy change or a rollout; the build waits for the owner's answer (open questions, section 8). Written
November 22, 2026 (Pacific Time) for the Home Assistant retirement (epic #1396), parity row 20 (agent credential
distribution) and row 11 (pairing).

Sources, all read in this repository: [device-approval.md](device-approval.md) (#55),
[roster-v2-transition.md](roster-v2-transition.md) (#1825), [agent-principals.md](agent-principals.md) (#519),
[script-credentials.md](script-credentials.md) (#1402, #1414), [fleet-grants.md](fleet-grants.md) (#3237),
[client-protocol.md](client-protocol.md) (row 20), and the release-gate harness #736 and enforcement #1359 as the
issues that gate the build. Where this note says something exists, it names where; it did not re-run the code.

## 1. The question

A new machine joins the fleet, or a script or agent on it needs to call the host, with no Home Assistant token
pasted on the machine and no HA present. Who is a person here, what proves a thing that is not a person, and what
is refused? The old way (a per-VM `AGENT_HA_AGENT_TOKEN`, set by the owner pasting it at a prompt) is the thing
being replaced (parity row 20 and row 34).

## 2. The principals that are not people

| Principal | What it is | Has a key? | What proves it today |
|---|---|---|---|
| **Host** | The Slipgates host on a machine; a device of kind `host` in the account's roster | Yes: the device key, and a `hostSign` key | The host key, and (once roster v2 is on) a roster entry a person approved. `hostSign` proves only that the host holds its key |
| **Agent session** | One live agent session on one machine | No. "It is not a device: it has no key, never joins a roster and never opens a channel" (agent-principals.md 3.1) | An `abat1_` token bound to the session's process tree, presented on the local pipe, plus the host's claim |
| **Script** | A named, long-lived caller (a scheduled task) on one machine, `script:<machine>/<name>` | No | A per-machine credential file, owner-only, minted on that machine (script-credentials.md 2, 7) |
| **Connector (HA component, S8)** | A service principal that pairs as a client over the relay | Yes, its own | A roster `add` of kind `connector`, approved by the mutual code exchange (agent-principals.md 9.2) |

## 3. What does not change, in either part

* **Only a person authorizes.** Only an active, non-probationary, non-held person's device in the pinned roster
  may authorize a roster change, using its recorded presence mechanism. Its kind and `sign` key come from the
  verified roster, "never the relay's hello kind, a display name, a bearer token, or an agent's credential"
  (roster-v2-transition.md, Who authorizes). Presence is enforced outside the app (device-approval.md 4.6).
* **A host cannot consent.** "A host's `hostSign` key proves only host-key possession. It cannot authorize the
  transition, sign an entry as `by` or `cosigner`, sign a person command, or veto" (roster-v2-transition.md).
* **An agent or a script never mints, revokes or rotates a credential**, and never creates consent
  (script-credentials.md 5; agent-principals.md 3.5).
* **No secret crosses the relay and none is synced.** A credential works only on the host that minted it; the
  relay never holds one (script-credentials.md 7).
* **No fallback to HA, a bearer token, or the relay's own label** when a check cannot be made: it refuses.

## 4. Part A: the rules in force today

1. **A new machine's host joins by a person's approval.** A pending device shows a six-digit code; an approved
   person's device types it, shows a second code, and the person types that on the new device; the approver signs
   the roster `add` with a presence proof (device-approval.md 4.1 to 4.2). A machine with an app and a host
   enrols both with two signed entries (4.7). Approval, enforcement and the host-enrolment slice are not built
   yet (roster-v2-transition.md status; #55, #1825, #1359).
2. **A script credential is minted by a person's press in the Slipgates app, on that machine's own host.**
   "Never an agent, never a script, never a remote device" (script-credentials.md 5; Q3 closed by the owner,
   "there is no remote mint at all"). Rollout is one press per script per machine (script-credentials.md 7).
3. **An agent session's credential is minted by the host at launch** (`read`, or `read+reply` for a fleet child),
   bound to the process, or adopted by a person at the machine (agent-principals.md 3.3 to 3.5;
   client-protocol.md row 20).

What this gives row 20 today, once built: a fresh machine gets its agent and script credentials with **no HA token
step and no pasted token**, but a person must be at that machine for each script credential press (and for the
host's first approval, someone with the approving device reads the new machine's code).

## 5. Part B: Proposed CHANGE (owner approval, not in force)

**What it would change:** the rule in 4.2, "only a person's press at the machine, never a remote device". It would
allow one more way to authorize a script credential on an unattended machine: a **provisioning grant**, issued by a
person from an already-trusted device, that the machine's host consumes to mint the credential **locally**.

**What it does not change:** everything in section 3. In particular the host still mints and keeps the secret
itself, no token is carried anywhere, and an agent, a script and the relay still cannot authorize anything.

**The grant** is modelled on the person-issued fleet grants (fleet-grants.md): authorized by the receiver, from a
signed envelope the receiver checks. Its fields (proposed):

| Field | Meaning |
|---|---|
| grant id | Unique; recorded when consumed, so it is single use |
| machine | The target host's device id: a grant for one machine is refused on another |
| principal | `script` and its name (`script:<machine>/<name>`), or `agent-fleet-child` where fleet launch is already granted |
| scope | At most `read+reply`. Never `launch`: fleet launch stays its own person-set flag per credential (script-credentials.md 2) |
| expiry | Short (proposed: at most 24 hours) |
| signer | A presence-proven person device, resolved from the verified roster |

**Flow (proposed):** the person signs the grant on their device; it reaches the host over the existing
account-scoped channel; the host checks the signer against the **verified roster**, the machine, the principal, the
scope, the expiry and that the id is unused; it mints the credential on its own disk (owner-only file, the platform's
user-bound protection, script-credentials.md 7), records the consumption and an audit line (the id, never the token);
the script reads its own file. The relay sees the signed grant at most, never a credential.

**Why propose it at all:** Part A needs a person at each machine. For machines nobody sits at, that makes
"provision a fresh VM" a manual step per script. Part B removes that step; it does not remove the person.

**Cost and risk:** it adds a remote authorization path for a credential, which the owner has ruled out once
("never a remote device"); the grant must therefore be verifiably the person's, which depends on the roster work
that is not built (#1825 enrolment and #1359 enforcement). Until the verified roster exists, the host cannot check
the signer, and so refuses (section 6). Rolling it out is a separate decision from approving it.

## 6. Refusals

Both parts refuse, with a stated reason, and never fall back to HA, a bearer token or the relay's kind:

| Condition | Refusal |
|---|---|
| No person signature, or the signer is not an active, non-probationary, non-held person in the verified roster | not authorized |
| The signer has no recorded presence proof for this action | not authorized |
| The roster cannot be verified (verifier off, enforcement not on, generation not pinned) | cannot verify: refused, never assumed |
| A host's `hostSign`, an agent credential, a script credential or a relay-listed kind offered as the person | refused (a non-person never authorizes) |
| Grant expired | expired |
| Grant id already consumed (a replay) | already used |
| Grant names another machine | wrong machine |
| Scope above `read+reply`, or a launch scope requested in a grant | refused (launch is a separate person-set flag) |
| Principal kind or name not allowed, or a name already holding 2 live credentials | refused (existing rotation limit) |
| The credential store is at its limit or unreadable | refused, fails closed (existing behaviour) |
| A credential presented on a machine other than the one that minted it | refused |
| An agent or script asks to mint, revoke or rotate | refused (never an agent, never a script) |

## 7. Acceptance for rows 20 and 11

Evidence is recorded in the epic (#1396) per row, every client the parity rule requires, 3 times per machine:

* **Part A.** A fresh machine, with no HA present and no token pasted: its host is enrolled by a person's approval
  with the matching code; one app press per script mints its credential; the script then calls `fleet.list` and
  `agent.send` with it; a credential from another machine is refused; a revoked one is refused; the refusals in
  section 6 that apply to Part A each return their reason.
* **Part B (only if approved).** All of Part A, and: a provisioning grant consumed once mints the credential with no
  person at the machine and no HA present; replaying it, an expired one and one for another machine are each
  refused; a grant asking for `launch` is refused; with the verifier off the grant is refused, not accepted.
* **Row 11, the pairing itself** (a new machine joins only after a matching code is approved) is the device-approval
  build (#55), checked in the clean-VM release-gate harness (#736); this note adds no step to it.

## 8. Open questions for the owner

1. **Keep Part A as it is, or approve Part B?** Part A already meets row 20's "no HA token step" with a person's
   press per script per machine. Part B is only needed if provisioning must work with nobody at the machine. This
   reverses "never a remote device" for script credentials, so it is the owner's call.
2. If B: the grant's longest lifetime (24 hours proposed), and whether it may name an `agent-fleet-child` or only a
   `script`.
3. Confirm the HA component (S8) pairs as a **connector** principal with its own key, approved by the mutual code
   exchange (agent-principals.md 9.2), rather than carrying any shared secret.

## 9. What the C-build waits on

The pairing build starts only when all of these are recorded as landed: #1359 (enforcement), #1825 (roster v2 and
enrolment), the #736 harness, and the owner's answer to section 8. No host code is written before it.
