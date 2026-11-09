# Script credentials: one credential for fleet scripts that run outside any session (#1402)

Design only. Part of #1396 (HA parity, slice S5), and of epic #1248. The code slice is
#1414, sized in §9. Nothing here is built; §2 says what exists today. This is a credential
design, so it is sensitive: it needs two independent reviews from different model families,
and Engineering and the owner's security review must agree before #1414 is cut (§10).

Companion notes: [agent-principals.md](agent-principals.md) (§3.3 and §3.4 define the
session credential this builds on), [agent-launch-scope.md](agent-launch-scope.md) (§11
adds a launch grant on top of this credential),
[fleet-session-registry.md](fleet-session-registry.md) (the read ops) and
[agent-token-rotation.md](agent-token-rotation.md) (how a token is stored on a host).

## 1. The problem

Fleet scripts (the pump wake, the board collector, the messenger, the review router and the
dispatcher) run on a timer or by hand, not inside an agent session. Today they authenticate
to Home Assistant (HA) with `AGENT_HA_AGENT_TOKEN`, which the old daemon's
`set-agent-ha-token.ps1` places per VM. That token is an HA user's long-lived token: it
reaches everything that HA user can reach, on every machine, and it is also copied into every
session's environment. The HA token can only be removed when these scripts have a Slipgates
credential of their own.

Slipgates mints a credential per session at launch. A script has no session, so it has none.

## 2. What exists today (checked in the code on `origin/main`)

| Piece | State | Where |
|---|---|---|
| Per-session credential: token `abat1_…`, only its SHA-256 stored, scope, expiry, bound to a session and a process | Built | `AgentCredentials.cs` |
| Scope names `none`, `read`, `read+reply`, `read+reply+launch` | Built | `AgentPrincipals.cs:18-23` |
| Credential refuses a caller whose ancestry is partial or lacks the bound process | Built | `AgentCredentials.Check` |
| The credential store fails closed when its file is unreadable | Built | `AgentCredentials.cs` (`problem`) |
| Revoke by session, or when the bound process ends | Built | `RevokeSession`, `RevokeEnded` |
| Pipe ops an agent connection may call: `agent.send`, `fleet.list`, `fleet.find` | Built (Windows for `agent.send`) | `LiteAgentSend.cs`, `FleetList.cs` |
| A credential **not tied to a session or a process** | **Does not exist.** `Mint` requires a session id, and `Check` refuses any credential whose process is not started (`RootPid <= 0`) | `AgentCredentials.cs` |
| A host command to list, mint or revoke credentials by hand (`agents revoke`) | **Not found in the code.** agent-principals.md §3.4 describes it; no command implements it | design only |
| Any credential that lets a script act as a script, with its own name in the audit | **Does not exist** | none |

So Slipgates has no credential today that a script can hold. Everything below that is not in
the table above is a proposal.

## 3. Decision: a limit on the existing credential, with a new binding

**Recommended.** "Send and read only" is **a limit on the existing credential kind**, not a new
kind. A script credential is the same record, the same hashed store, the same `abat1_`-style
token and the same scope words as a session credential, with scope **`read+reply`**. Reasons:

1. The scope words already say it. `read+reply` is "send and read, no launch"; the credential
   needs no second vocabulary, and every op's scope check keeps working unchanged.
2. A second kind means a second store, a second prefix for secret scanners and a second
   revocation path, each a new way to leave a token behind.
3. agent-launch-scope.md §11 already relies on this: the dispatcher's launch right is a
   separate grant on the same credential, off until the owner ticks it, and "not a new
   credential kind".

What does change is the **binding**. A session credential is bound to one session and its
process (`session`, `rootPid`, `rootStartUtc`); that binding is what makes it work only from
inside its session. A script has neither. The honest summary: same kind and same limits, a
different binding (a **name** and a machine instead of a session and a process). #1414 adds
that binding and nothing else to the record. A record carries `kind: "session"` (the existing
ones, unchanged) or `kind: "script"`, so the two are never confused at check time.

## 4. The credential

| Property | Design |
|---|---|
| Name | **script credential**; one per script per machine, named by the owner (`pump-wake`, `board-collector`, `messenger`, `review-router`, `dispatcher`) |
| Token | Same format and prefix family as the session token, so redaction and secret scanning already recognize it. 32 random bytes; only the hash is kept by the host |
| Principal in the audit and in the UI | `script:<machine>/<name>`, never `agent:…`. A script is shown as a script (a distinct label), so a reader never mistakes it for a session |
| Account | The host's own account only. A script cannot name another account, and cross-account federation (agent-principals.md §7) is not extended to it |
| Scope | `read+reply` only: `agent.send` and the read ops (`fleet.list`, `fleet.find`). **No launch.** No answering an approval, no folder trust, no stop, no transfer, no attachments |
| Expiry | 30 days by default, shorter if the owner sets it (open question Q1). A script that runs past it fails loudly; it is never extended silently |
| Count | One credential per name; minting the same name again replaces the old one (the old one is dead at once) |

**Launch is not part of this credential.** The dispatcher's launch right is the separate
`launch:fleet` grant in agent-launch-scope.md §11 (slice 2e), and a person at the machine
turns it on per credential. Until then the dispatcher keeps launching through HA, as that note
says. The base script credential never carries it.

## 5. Who mints and who revokes

**Only a person at the machine mints or revokes**, through the host's own command line or the
app. Never an agent, never a script, never a remote device. This follows guard #67 (host
management is refused from inside an agent session) and agent-token-rotation.md §4 (a new
token is accepted only from a person's device).

* **Mint (proposed).** `agent-bridge-host scripts add <name>`. The host writes the token
  straight into an owner-only file for that script (§7) and shows the owner the name, scope,
  expiry date and the **last four characters** only. The token is never printed, logged,
  put in an argument, or sent over any channel.
* **Revoke (proposed).** `agent-bridge-host scripts revoke <name>`, or a "Revoke" button
  beside the script in the app. The record is deleted, every open connection on it is closed
  and the next call is refused.
* **Rotate.** Mint the same name again. The host writes the new file atomically and keeps the
  old credential until the new one passes one real call (agent-token-rotation.md §4), then
  deletes it. A script reads its file on each run, so it needs no restart.
* **Automatic revocation.** Expiry; an unreadable store (fail closed, nothing is accepted until
  the owner deals with it).

## 6. Limits, and what holds them

1. **Scope check at the host**, on every op, from the record, never from what the script says.
2. **Own account**, as for sessions: the host only ever acts for its own account's roster.
3. **Not from inside an agent session.** The caller's ancestry must be read in full and must
   contain no agent session: the reverse of `agent.hello`'s rule, as agent-launch-scope.md §11
   states it. A partial or untraceable ancestry is refused, never treated as a script.
4. **No launch, no answers.** The ops list is an allow-list; an op not on it is refused by
   default.
5. **Rate limit and size limit** per credential, modelled on the session limits (`agent.send` text is
   already capped at 4096 characters, `LiteAgentSend.MaxText`), and the audit log (#1401) records every
   call with the script principal.

**Honest residual.** Rule 3 is a safety rail, not a boundary (agent-principals.md §3.4,
"This check is a safety rail"). A script credential sits in an owner-only file, which any
program running as the same user can read; an agent that copies it and uses a helper with a
spoofed parent gets `read+reply` as that script. That is the same-user limit of
agent-principals.md §1.3. It is **no worse than the HA token it replaces**, which every
session's environment holds and which reaches far more (launch, Allow all, every machine).
It is narrower in three ways: one named script, not the whole HA user; no launch; and
revocable at the machine in one command.

## 7. Placing it on each machine

* **Per machine, minted on that machine.** A credential works only on the host that minted it.
  Nothing is synced, and the relay never holds one (agent-token-rotation.md §3).
* **Where it lives.** One owner-only file per script, outside session folders and the profile
  bundle area, wrapped by the platform's user-bound protection (DPAPI on Windows), the same
  rules as agent-token-rotation.md §1. The host keeps only the hash.
* **How a script gets it.** The script reads **its own file** by a path that the owner sets in
  the script's own configuration. It is not put in any session's environment, and the
  `set-agent-ha-token.ps1` approach of setting a per-VM environment variable is not repeated:
  an environment variable is inherited by every child process, which is how the HA token ended
  up in every session.
* **Rollout, one machine at a time.** The owner runs `scripts add` for each script on each of
  the four machines. A fleet roll-out that wants to avoid running it by hand needs a
  person-approved remote mint, which is out of scope here (Q3).
* **No Apple host yet.** Apple machines are out for now (epic #1396), and the session
  credential has no Mac or Linux path until peer identification exists (agent-principals.md
  §3.4); the same holds here.

## 8. Lost or leaked credentials: what the owner sees

| Event | What happens | What the owner sees |
|---|---|---|
| Token used from inside an agent session | Refused at the host; nothing is sent | A line in the audit log and a notice: "script `<name>` credential was presented from inside an agent session", with no token text |
| Token used after expiry or revoke | Refused as "no live credential" | The script's own error; a notice carries the expiry date, never the token |
| Owner suspects a leak | `scripts revoke <name>`; remint the same name | The script shows as revoked in the app; the audit log shows the last use (time and op) of the old one |
| File lost with the machine | Revoke by name on the machine, or delete the credential store | The remaining machines are untouched, because nothing is shared |
| Store unreadable at host start | Fails closed: nothing accepted or issued | A host notice, as for session credentials |

Never in clear: logs, errors, notices, diagnostics, the relay. A canary test plants a token and
scans the profile, logs and bundles for it, as agent-token-rotation.md §5 requires.

## 9. Every fleet script that needs it

The scripts themselves live in the fleet repository (`danswett/slipgates-fleet`), not in this
one, and I did not read their source. This table is from #1402, the epic's fleet issues
(#103 to #108) and the ops that exist in this repository. **Engineering must confirm each row
from the script before #1414 is cut (Q4).**

| Script | What it does today | Operation it needs | Notes |
|---|---|---|---|
| Pump wake | Wakes a waiting coordinator | `agent.send` (send text to a session) | Send only |
| Board collector | Gathers fleet status for the board | `fleet.list` | Read only |
| Messenger | Sends and reads session messages (#728 cut-over, fleet #104) | `agent.send`, and `fleet.find` to turn a name into a session | The first to cut over |
| Review router | Picks a reviewer and passes work on (fleet #105) | `fleet.list`, `fleet.find`, `agent.send` | No launch |
| Dispatcher | Starts worker sessions (fleet #106) | `session.launch` | **Not covered by this credential.** Needs the `launch:fleet` grant (agent-launch-scope.md §11, slice 2e) |

Four of five need only `read+reply`. The dispatcher is the exception, deliberately.

**Code slice #1414 (needs-design, both receipts), under 400 changed lines:**

| Part | Lines |
|---|---|
| `kind` and `name` on the record; mint without a session; check that skips the process binding | about 90 |
| Caller rule: ancestry in full, no agent session in it, else refuse | about 60 |
| `scripts add` and `scripts revoke` host commands, owner-only file write, last-four display | about 100 |
| Script principal in the audit and the UI label; op allow-list for `kind: "script"` | about 50 |
| Tests (kind confusion, refusal from inside a session, expiry, revoke, canary) | about 90 |
| **Total** | **about 390** |

It lands after #1402 (this note) and after the audit log (#1401). If it runs over 400 lines,
split the host commands from the host check.

## 10. Open questions

Engineering and the owner's security review agree to this note before any code is cut.

1. **Q1: expiry.** 30 days is a proposal. Longer means fewer renewals to forget; shorter limits
   a leak. The session default is 7 days (`AgentCredentials.DefaultDays`).
2. **Q2: one credential per script, or one shared by all five.** Recommended: one per script,
   so a leak of the pump wake's does not open the messenger's ops, and the audit says which
   script acted. Cost: five mints per machine.
3. **Q3: remote mint.** Is running `scripts add` by hand on four machines acceptable, or is a
   person-approved mint from the app needed first?
4. **Q4: the ops per script** in §9 are inferred, not read from the scripts (§9 preamble).
5. **Q5: macOS and Linux.** Out of scope now; confirm the owner is content that the script
   credential waits for peer identification there, as the session credential does.
6. **Q6: the HA token's removal.** Fleet #107 removes it. Recommended gate: a script's HA
   fallback is deleted only after one machine runs it for a full run with zero HA calls
   (the epic's retirement gate counts this), not on a date.

## 11. What is not in this note

No code, no change to the live agent-ha-bridge, no change to how a person's devices
authenticate, and no launch right (that is agent-launch-scope.md §11).
