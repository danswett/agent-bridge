# Script credentials: one credential for fleet scripts that run outside any session (#1402)

**Owner decision (option 1, as relayed by the Chief on #1414 at about 23:55 PT, Nov 22; no direct owner comment link): script credentials are issued from the Slipgates app.**
Only a person's press in the app mints, revokes or rotates one, and no terminal or PowerShell step
exists anywhere (the owner's Nov 19 rule, carried by #3305). The terminal CLI `agent-bridge-host scripts
add|revoke|grant-fleet|revoke-fleet` is built today (`ScriptCredentialCommands.cs`; #2286, PR 2582,
#2820). It is a terminal step, so it is retired or app-gated when the app path lands. Build order: script send (#2763)
and the `launch:fleet` grant first, then the app-issued credential, then the fleet scripts switch
over and `AGENT_HA_AGENT_TOKEN` retires.

Part of #1396 (HA parity, slice S5), and of epic #1248. #2346 implements the Windows caller
slice: registered scripts authenticate as `script:<machine>/<name>` for **`fleet.list` and
`fleet.find` only**. Stored scope remains `read+reply`, but the hello advertises `read` and a
connection allowlist refuses every other op, even after expiry or revocation. The wider send
proposal below is not enabled. Sensitive changes need two different-family whole-file reviews.

The #2763 read-only scope slice also pins that restriction at credential acceptance:
an authenticated script connection cannot rebind to another credential, even after expiry
or revocation. Request fields cannot widen its scope. `agent.send` and `session.reply`
remain refused; script reply wire identity and delivery are a separate predecessor slice.

Companion notes: [agent-principals.md](agent-principals.md) (§3.3 and §3.4 define the
session credential this builds on), [agent-launch-scope.md](agent-launch-scope.md) (§11
adds a launch grant on top of this credential),
[fleet-session-registry.md](fleet-session-registry.md) (the read ops) and
[agent-token-rotation.md](agent-token-rotation.md) (how a token is stored on a host).

### Script identity on the wire (#2765)

The lite envelope carries a script principal as `kind`, `machine`, `add`, `identity`,
`credential`, in that canonical order. `kind` is `script`; `identity` is
`script:<machine>/<name>` and its machine must equal the envelope's sending device ID.
Each identity part is 1-64 ASCII letters, digits, `.`, `_` or `-`, excluding `.` and `..`;
the full identity is at most 128 characters. Script principals carry no agent session,
instance, agent or launcher fields. The chain's sender slot carries the script identity.
The host derives it from the script credential, never request data, and delivery labels
and logs it as a script. Existing agent encodings are unchanged. Shared accept/refuse
cases live in `docs/protocol/vectors/script-principals.json`. This wire support does not
enable script sends: the operation allowlist remains separate work in #2763.

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
| A credential **not tied to a session or a process** | Built: named script record, hashed store and protected file; default and maximum life 30 days | `AgentCredentials.cs` |
| A host CLI to mint, revoke and grant or revoke the fleet launch grant by hand: `agent-bridge-host scripts add\|revoke\|grant-fleet\|revoke-fleet <name>` | **Built** (#2286, PR 2582, #2820). A terminal step: retired or app-gated when the app path lands (owner option 1) | `ScriptCredentialCommands.cs` |
| A script principal in the audit | Built for fleet reads; complete, traceable, agent-free ancestry required | `AgentCredentials.Check`, `FleetList.cs` |

The remaining sections retain the broader proposal. #2346 did not itself implement script send,
launch, the mint/revoke commands (they landed later as #2286, PR 2582 and #2820), signed registry
records or a fleet-script rollout.
Rotation retires the older hash only after the newer token completes an audited fleet read;
a handshake, failed audit, invalid lookup or failed roster read leaves both hashes intact.
An expired, revoked or faulted-store connection is refused and logged, never treated as a person.

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
| Count | One live credential per name, except during a rotation: minting the same name again adds the new one, and the old one dies when the new one passes its first real call (§5, Rotate), or at its own expiry if that never happens |

**Launch is not part of this credential.** The dispatcher's launch right is the separate
`launch:fleet` grant in agent-launch-scope.md §11 (slice 2e), and a person at the machine
turns it on per credential. Until then the dispatcher keeps launching through HA, as that note
says. The base script credential never carries it.

## 5. Who mints and who revokes

**Only a person's press in the Slipgates app mints, revokes or rotates**, on the machine's own
host and from a presence-proven device. Never an agent, never a script, never a remote device
without that presence, and no terminal or PowerShell step. This follows guard #67 (host management
is refused from inside an agent session) and agent-token-rotation.md §4 (a new token is accepted
only from a person's device). The owner chose this over a host command line (as relayed by the Chief on #1414).

**How the host tells a person's press from same-user code imitating the app.** The press must reach
the host as a **person-device presence-signed command**, the same kind the owner's other person-only
actions use (#3305; agent-principals.md §4.2 and the signed-command memory in §4.4): a designated-verifier
MAC from the person's device key (`CommandMac`, a TPM or Secure Enclave key), made with presence, that
names the operation (mint, revoke or rotate), the script name, the machine, a nonce and an expiry.
The host checks the MAC, the machine and the expiry, and refuses a nonce it has seen in the **durable**
replay store (#391 R6, agent-principals.md §4.4); the in-memory store forgets across a restart, so it
is not enough. A remote device mints or revokes only with presence, as the Nov 19 decision says (role
assignments need a person signature made with presence, never an unattended capSign). A bare local
pipe call from the app process is **not** a person press: the app's process identity is a safety rail,
not a boundary. **OPEN (#1414 design):** whether the local app on the same machine can obtain a
presence-signed command from the person's device without a phone or second device, and what the
fallback is on a machine with no presence-capable key. Until that is settled, nothing here removes the
terminal CLI (the Option 1 build order stands).

* **Mint.** In app Settings the person presses "Add script credential" and picks a script name.
  The host writes the token straight into an owner-only file for that script (§7) and the app shows
  the name, scope, expiry date and the **last four characters** only. The token is never printed,
  logged, put in an argument, or sent over any channel, and no person ever handles it.
* **Revoke.** The "Revoke" press beside the script in the app. The record is deleted, every open
  connection on it is closed and the next call is refused.
* **Rotate.** The "Rotate" press, or minting the same name again. The host writes the new file
  atomically and keeps the old credential until the new one passes one real call
  (agent-token-rotation.md §4), then deletes it. At most two credentials per name are live, and
  only during a rotation; a failed first call changes nothing, and a Revoke press kills both. To
  end a leak at once, revoke first and then mint, rather than rotating. A script reads its file on
  each run, so it needs no restart.
* **Automatic revocation.** Expiry; an unreadable store (fail closed, nothing is accepted until
  the owner deals with it).

## 6. Limits, and what holds them

1. **Scope check at the host**, on every op, from the record, never from what the script says.
2. **Own account**, as for sessions: the host only ever acts for its own account's roster.
3. **Not from inside an agent session.** The caller's ancestry must be read in full and must
   contain no agent session: the reverse of `agent.hello`'s rule, as agent-launch-scope.md §11
   states it. A partial or untraceable ancestry is refused, never treated as a script.
   A task the scheduler runs never passes that (its SYSTEM service host can't be opened), and
   ancestry couldn't vouch for it anyway: any user process can register a task that runs anything.
   A scheduled script needs a credential bound to its task, minted by an app press that names the
   task (`<\Folder\Task>`) and any extra `--file` paths, with the host service doing the elevated
   binding (how that service is elevated is #1414's design to settle, not a terminal step). The mint is refused unless the
   task and every file it runs (its program, the script it names, each `--file` that script runs)
   are writable only by SYSTEM and Administrators; it records hashes of the task's definition
   (actions, principal, triggers) and of each file. Accepting it needs a running instance of that
   task, as the Task Scheduler reports it, whose engine (the program it started) is an ancestor
   read in full up to the caller, with the task, ACLs and hashes unchanged. The engine must be the
   same process, not just the same pid: `IScriptTaskReader.RunningEngines(task)` returns each running
   instance's engine as `ScriptTaskEngine(int Pid, DateTimeOffset StartedAt)` (the engine process's
   creation time, UTC; an engine whose start can't be read is left out), and the ancestor must match
   both, its start within a second. At `agent.hello` the host reads the caller's chain again rather
   than trusting the one from connect, and refuses unless it is still the same caller process (pid
   and start): a parent the caller ended after connecting may since be the engine under its pid. Any
   other task path, edit, loosened ACL or wrong engine is refused. A mint that pins more than 16
   files, no action program or anything that isn't a SHA-256 hash is refused, so it can't fault the
   store. **The admin-only ACLs hold this, not ancestry:**
   an agent can't change the task or its files, and running it runs only the pinned files. As the
   same user it can still reach into a running instance (its process handles, a spoofed parent, or
   the user's environment and profile scripts its processes load): the residual below. #3312 is
   the host-side check with fakes; the Windows reads and `--task` follow, and until then every bound
   credential is refused.
   **A script a coordinator starts does not pass this rule, on purpose.** A script started
   from inside a coordinator's session (a child of its shell) has an agent session in its
   ancestry and is refused. There are two ways to run such work:
   * **Recommended: the coordinator sends and reads with its own session credential**
     (`agent.send`, `fleet.list`), which it already holds; no script credential is involved.
   * **Scheduled runs:** the owner binds the credential to an admin-locked task (above), or runs
     the script by hand in a terminal, so its ancestry holds no agent session. A coordinator that
     wants one run now asks the scheduler to run the bound task; it runs only the pinned files.
     An unbound credential from a detached process with an agent-free ancestry is still the rail's
     known gap, not a feature: it is why the credential file, not rule 3, is the real barrier.
   Running a script credential's file from a session by copying or exporting it is a
   leak (§8), not a supported path.
4. **No launch, no answers.** The ops list is an allow-list; an op not on it is refused by
   default.
5. **Rate limit and size limit** per credential, modelled on the session limits (`agent.send` text is
   already capped at 4096 characters, `LiteAgentSend.MaxText`), and the audit log (#1401) records every
   call with the script principal.

**Honest residual.** Same-user code that imitates the app is the agent-principals.md §1.3 residual
(§3.4): it can drive the same pipe and UI as the person. The presence-signed press above is what stops
it from minting, revoking or rotating a credential on its own, but it cannot stop code that already
holds the same-user key material. Replay is closed by the nonce and expiry, not by the app's identity.

Rule 3 is a safety rail, not a boundary (agent-principals.md §3.4,
"This check is a safety rail"). A script credential sits in an owner-only file, which any
program running as the same user can read; an agent that copies it and uses a helper with a
spoofed parent gets `read+reply` as that script. That is the same-user limit of
agent-principals.md §1.3. It is **no worse than the HA token it replaces**, which every
session's environment holds and which reaches far more (launch, Allow all, every machine).
It is narrower in three ways: one named script, not the whole HA user; no launch; and
revocable at the machine in one app press.

## 7. Placing it on each machine

* **Per machine, minted on that machine.** A credential works only on the host that minted it.
  Nothing is synced, and the relay never holds one (agent-token-rotation.md §3).
* **Where it lives.** One owner-only file per script, outside session folders and the profile
  bundle area, wrapped by the platform's user-bound protection (DPAPI on Windows), the same
  rules as agent-token-rotation.md §1. The host keeps only the hash.
* **How a script gets it.** The script reads **its own file**. **OPEN (#1414 design):** how that
  path reaches the script now that no terminal step may set it (a per-script path the host reports
  to the app, or a fixed per-profile location the script derives). It is not put in any session's environment, and the
  `set-agent-ha-token.ps1` approach of setting a per-VM environment variable is not repeated:
  an environment variable is inherited by every child process, which is how the HA token ended
  up in every session.
* **Rollout, one machine at a time.** The owner presses "Add script credential" in the app for
  each script on each of the four machines; no command is run. A mint for another machine from one
  device needs a person-approved remote press, which stays out of scope (Q3).
* **No Apple host yet.** Apple machines are out for now (epic #1396), and the session
  credential has no Mac or Linux path until peer identification exists (agent-principals.md
  §3.4); the same holds here.

## 8. Lost or leaked credentials: what the owner sees

| Event | What happens | What the owner sees |
|---|---|---|
| Token used from inside an agent session | Refused at the host; nothing is sent | A line in the audit log and a notice: "script `<name>` credential was presented from inside an agent session", with no token text |
| Token used after expiry or revoke | Refused as "no live credential" | The script's own error; a notice carries the expiry date, never the token |
| Owner suspects a leak | The "Revoke" press for `<name>`; then mint the same name | The script shows as revoked in the app; the audit log shows the last use (time and op) of the old one |
| File lost with the machine | Revoke by name on the machine, or delete the credential store | The remaining machines are untouched, because nothing is shared |
| Store unreadable at host start | Fails closed: nothing accepted or issued | A host notice, as for session credentials |

Never in clear: logs, errors, notices, diagnostics, the relay. A canary test plants a token and
scans the profile, logs and bundles for it, as agent-token-rotation.md §5 requires.

## 9. Every fleet script that needs it

The scripts themselves live in the fleet repository (`danswett/slipgates-fleet`), not in this
one. The table below is from #1402, the epic's fleet issues (#103 to #108) and the ops that exist
in this repository. **The current fleet-side inventory is `docs/ha-script-credentials.md` in
slipgates-fleet (fleet PR #1128)**, read from the fleet tree at `f9e6ca31`, and it wins where they
differ: the stall alarm, status board and roster snapshot no longer call HA (the snapshot reads the
relay roster); the messenger keeps only an HA rollback transport; the inventory lists no
review-router script; the dispatcher's `FleetLaunch.psm1` is the live HA launch client; and `end-sessions`
and `launch-remote` are person tools on their own transport changes (G10 and HA-Grants-Launch).
Engineering still confirms each row's operations from the script before #1414 is cut (Q4).

| Script | What it does today | Operation it needs | Notes |
|---|---|---|---|
| Pump wake | Wakes a waiting coordinator | `agent.send` (send text to a session) | Send only |
| Board collector | Gathers fleet status for the board | `fleet.list` | Read only |
| Messenger | Sends and reads session messages (#728 cut-over, fleet #104) | `agent.send` and `fleet.find` validate an explicit live session ID, never resolve a name | The first to cut over |
| Review router | Picks a reviewer and passes work on (fleet #105) | `fleet.list`, `fleet.find`, `agent.send` | No launch |
| Dispatcher | Starts worker sessions (fleet #106) | `session.launch` | **Not covered by this credential.** Needs the `launch:fleet` grant (agent-launch-scope.md §11, slice 2e) |

Four of five need only `read+reply`. The dispatcher is the exception, deliberately.
Session lookup follows [the canonical ID-only contract](../protocol/agent-operations.md#fleetfind):
names are display only, machine pins are exact device IDs, and unknown, stale or
offline IDs refuse without name, fragment or HA fallback. Trust/completeness
precedence and the messaging-only own-account exception remain unchanged;
these script credentials grant no roster verification or additional actions.

**Code slice #1414 (needs-design, both receipts), under 400 changed lines:**

| Part | Lines |
|---|---|
| `kind` and `name` on the record; mint without a session; check that skips the process binding | about 90 |
| Caller rule: ancestry in full, no agent session in it, else refuse | about 60 |
| Host mint, revoke and rotate ops behind the app presses (presence-proven), owner-only file write, last-four display | about 100 |
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
3. **Q3: remote mint.** Answered for the local case: the app press replaces the terminal command
   (owner decision, option 1, #1414). Still open: a person-approved press for another machine.
4. **Q4: the ops per script** in §9 are inferred, not read from the scripts (§9 preamble).
5. **Q5: macOS and Linux.** Out of scope now; confirm the owner is content that the script
   credential waits for peer identification there, as the session credential does.
6. **Q6: the HA token's removal.** Fleet #107 removes it. Recommended gate: a script's HA
   fallback is deleted only after one machine runs it for a full run with zero HA calls
   (the epic's retirement gate counts this), not on a date.
7. **Q7: the local press.** Which presence-signed path the app uses for a local press (see §5),
   and the fallback on a machine without a presence-capable device key.

## 11. What is not in this note

No code, no change to the live agent-ha-bridge, no change to how a person's devices
authenticate, and no launch right (that is agent-launch-scope.md §11).
