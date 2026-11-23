# Script credentials: one credential for fleet scripts that run outside any session (#1402)

**Owner decision (option 1, as relayed by the Chief on #1414 at about 23:55 PT, Nov 22; no direct owner comment link): script credentials are issued from the Slipgates app.**
Only a person's press in the app mints, revokes or rotates one, and no terminal or PowerShell step
exists anywhere (the owner's Nov 19 rule, carried by #3305). The terminal CLI `agent-bridge-host scripts
add|revoke|grant-fleet|revoke-fleet` is built today (`ScriptCredentialCommands.cs`; #2286, PR 2582,
#2820). It is a terminal step, so it is retired or app-gated when the app path lands. Build order: script send (#2763)
and the `launch:fleet` grant first, then the app-issued credential, then the fleet scripts switch
over and `AGENT_HA_AGENT_TOKEN` retires.

Part of #1396 (HA parity, slice S5), and of epic #1248. #2346 implements the Windows caller
slice: registered scripts authenticate as `script:<machine>/<name>`. #2763 enables
**`fleet.list`, `fleet.find` and `agent.send` only**, advertising the stored `read+reply`
scope. The allowlist refuses answering questions, settings changes, credential management,
scope raises and unknown operations with a readable reason. Launch requires the separate,
person-set `launch:fleet` grant; this slice neither creates nor widens it.
Sensitive changes need two different-family whole-file reviews.

The #2763 scope slice also pins that restriction at credential acceptance:
an authenticated script connection cannot rebind to another credential, even after expiry
or revocation. Request fields cannot widen its scope. Expiry, revocation or a faulted store
refuses reads and replies. `session.reply` remains refused: scripts send through `agent.send`,
with the script wire identity and delivery checks from #2765, never as the person.
Reply attempts require the existing audit before dispatch and preserve the sender's live-grant checks.
The receiving host resolves the fleet node to exactly one live native session before checking
delivery gates, and checks that binding again before replying. Missing or colliding nodes refuse
with `noSuchSession`; gates, rate limits and replies use the native session ID.

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
cases live in `docs/protocol/vectors/script-principals.json`. #2763 enables this path through
the script connection's read-and-reply allowlist.

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
host. Never an agent, never a script, **never a remote device** (no remote mint, revoke or rotate at
all, per the owner), and no terminal or PowerShell step. This follows guard #67 (host management
is refused from inside an agent session) and agent-token-rotation.md §4 (a new token is accepted
only from a person's device). The owner chose this over a host command line (as relayed by the Chief on #1414).

**What makes the press a person's (presence-proven).** The local WinUI Settings press on the machine
follows the approved grants pattern (fleet-grants.md G4, G7b and G8): the host adapter accepts it
only from the exact installed app image, after the caller guard (guard 67) has read the whole
ancestry and found no agent session, and only with a confirmation bound to the account, profile,
script name, action and credential generation (G8). A bare pipe call without that confirmation is
refused. That local press is what "presence-proven" means here; no further owner step is needed
(ruling relayed by the Chief). A remote device cannot mint, revoke or rotate: there is no remote
press, no phone issuance and no signed remote envelope for these actions.

* **Mint.** In app Settings the person presses "Add script credential" and picks a script name.
  The host writes the token straight into an owner-only file for that script (§7) and the app shows
  the name, scope, expiry date and last use, and no part of the token (not even its last four characters: R9 ruling, no
  credential-store format change). The token is never printed,
  logged, put in an argument, or sent over any channel, and no person ever handles it.
* **Revoke.** The "Revoke" press beside the script in the app. The record is deleted, every open
  connection on it is closed and the next call is refused.
* **Rotate.** The "Rotate" press, or minting the same name again. The host writes the new file
  atomically and keeps the old credential until the new one passes one real call
  (agent-token-rotation.md §4), then deletes it. At most two credentials per name are live, and
  only during a rotation; a failed first call changes nothing, and a Revoke press kills both. To
  end a leak at once, revoke first and then mint, rather than rotating. A script reads its file on
  each run, so it needs no restart.

**Host operations behind the presses (P1).** The app calls five operations on the local pipe, and no
others: `scripts.list`, `scripts.confirm`, `scripts.add` (mint, or rotate when the name already has a
live credential), `scripts.revoke`, and `scripts.grant-fleet` / `scripts.revoke-fleet` (the `launch:fleet`
tick). They are for the person's app on this machine, or still the host command line until the app path
ships and the CLI is app-gated (P3). An agent, a script, an unclassified caller and any connection that has
passed `agent.hello` are refused.

* **Bound confirmation.** An app press must carry a confirmation the host issued for exactly that action. The app
  asks `scripts.confirm` for it, shows the person what it will do, and sends it back with the press. Per script
  name the host keeps a **generation counter**, moved on by every press attempt (right or wrong) and by every mint,
  revoke, rotation, expiry, tick and untick, and holds at most **one outstanding confirmation** for the name, bound to that
  generation, the operation and the connection it was issued to. The value is derived from a random per-process
  salt and a fresh nonce, lives **two minutes on the monotonic clock** (a wall-clock step cannot stretch it), and is checked and used up in one atomic step under the
  credential store's lock; the audit attempt and the change follow, with no store lock held while the audit log is written. So a used, expired, replayed or guessed value, one presented on another
  connection, one made for another action or script, and one whose script has since changed (even if it has
  since come back, as after a mint and a revoke) is refused and changes nothing; two presses at once with one
  confirmation make one credential, and two confirmations asked at once never make two. A wrong try also uses up
  what the script had outstanding, so the person is asked again. At most 64 confirmations are held (the oldest
  goes first), and a restart forgets them all. This is the grants G8 pattern; the host does not trust the app's
  say-so alone (the same-user imitation residual stays OPEN, Q7).
* **The list.** `scripts.list` returns, per script name, the scope, the latest expiry among its live credentials, how many
  credentials are live (two during a rotation) and whether the `launch:fleet` tick is on. It never returns
  a token, a hash, a file path or a credential id, and it shows no last four characters (the host keeps
  only the hash, so a later list cannot recover them).
* **The Settings page (P2).** Settings > Script credentials lists each script (name, scope, expiry, live count, when it last
  authenticated, and the launch tick) and offers Add, Rotate, Revoke and Allow/Stop launching. Each press shows a confirmation dialog
  with plain words, then asks `scripts.confirm` and sends the press; a host refusal is shown as the page's notice. The page never holds,
  shows or copies a secret. It appears only for the app, on Windows, when the host offers the `host.scripts` capability. "Last used" is
  kept in memory since the host started, so it says so; the last four characters of a credential are not shown, because the host
  keeps only the hash. Showing them would need a stored field, which is a store-format change left to a decision.
* **The tick keeps the host's own policy.** `scripts.grant-fleet` is still refused unless the script's
  credential is `read+reply` and the host's agents setting is on; the press cannot widen what the grant
  allows (fleet caps, ticked workspaces and isolation are unchanged and owner-run).
* **Audit before change.** Each accepted press is recorded as an attempt before anything is written, as for the CLI,
  and an unwritable audit log refuses the press (its confirmation is already used up, so the person asks again). Refusals
  before that point (an unclassified caller, an agent, a credentialed connection, a missing, wrong or expired confirmation)
  write no audit row: they change nothing, and a wrong try only costs the person a fresh confirmation.

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
(§3.4): it can drive the same pipe and UI as the person. The caller guard, the exact-image check and
the bound confirmation are safety rails against an agent that has not spoofed its way past them, not a
boundary against same-user code, so this residual stays **OPEN** (Q7).

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
  Copying its protected file, token or hash record never rebinds it: the protected file uses
  Windows CurrentUser DPAPI, and a store with another machine's script identity fails closed.
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
  each script on each of the four machines; no command is run. Nothing is minted for another machine
  from one device: there is no remote mint (Q3).
  The credential is a protected file the host writes for that script; the app never shows it and the
  result never contains the token. How the script learns the file's path is OPEN (#1414 design). The
  scheduled process must use the minting Windows user and read/unprotect the file on each run. Do not
  copy the store, file or token between hosts, export it into session environments, or include it in bundles.
* **Audit before use.** Presentations and scope refusals append one row by script principal;
  fleet reads append one attempt even for invalid input or an unavailable registry.
  Invalid sends append one refusal; valid sends retain their existing attempt and terminal
  outcome rows. Separately granted fleet launches audit credential admission/refusal without
  replacing launch-history evidence. None includes token or message text.
  Missing/unwritable audit refuses use.
  Audit-only hash/name attribution for recently revoked credentials is bounded and in-memory;
  after restart, an unknown/deleted token cannot be attributed to a credential name.
  Rotate with the app's Rotate press, verify an audited fleet read, then retire the
  old consumer. Revoke with the app's Revoke press if compromised. HA-token retirement
  remains separate work after fleet cutover validation.
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
| Host mint, revoke and rotate ops behind the local app presses (grants G4/G7b/G8 pattern), owner-only file write | about 100 |
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
3. **Q3: remote mint.** Closed: the local app press replaces the terminal command (owner decision,
   option 1, #1414), and there is no remote mint at all (the owner said never a remote device).
4. **Q4: the ops per script** in §9 are inferred, not read from the scripts (§9 preamble).
5. **Q5: macOS and Linux.** Out of scope now; confirm the owner is content that the script
   credential waits for peer identification there, as the session credential does.
6. **Q6: the HA token's removal.** Fleet #107 removes it. Recommended gate: a script's HA
   fallback is deleted only after one machine runs it for a full run with zero HA calls
   (the epic's retirement gate counts this), not on a date.
7. **Q7: same-user imitation of the app (agent-principals.md §1.3).** OPEN. The local press rests on
   the caller guard, the exact app image and the bound confirmation; same-user code that imitates the
   app is the accepted §1.3 residual until the owner says otherwise.

## 11. What is not in this note

No code, no change to the live agent-ha-bridge, no change to how a person's devices
authenticate, and no launch right (that is agent-launch-scope.md §11).
