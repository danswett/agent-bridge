# Script credentials: how a scheduled task gets its token (`scripts run`) (#3305 slice 3b, design only)

Status: DESIGN ONLY, no code. **Needs Quality's sensitive read. Q-run-2, Q-run-3 and Q-run-4 below are OPEN FOR THE OWNER (Q-run-4 is the read's P2-6 point); nothing is built on a guess at any of them.**
Part of #3305 (parent #1421); answers the OPEN item in [script-credentials.md](script-credentials.md) section 7, "How a script gets it".
The credential format, the stored hash, the scope rules (`read+reply`, the allow-list) and the person-pressed mint, rotate and revoke are **unchanged**.
Revised after Quality's read (receipt on #4429): the pinned program (section 4), what is a barrier and what is a rail (section 6), the token's real exposure
(sections 3 and 5), and the per-slice acceptance (section 9).

## 1. The problem

A scheduled task (for example the F7 dispatcher cycle) must authenticate as `script:<machine>/<name>`. Nothing on `main` hands it its token: the CLI has
`scripts add|revoke|grant-fleet|revoke-fleet` only, the host writes the token into an owner-only DPAPI file, and a hand-edited wrapper has to read that file
and set `AGENT_BRIDGE_TOKEN`. The owner's rule is that nobody runs or edits PowerShell for this. Section 7 of the base doc also forbids a per-VM environment
variable, because an inherited variable reached every session. This note must do better than that and says plainly where it only does less badly (section 5).

## 2. Proposal: a small admin-locked launcher, `script-run`, started by the task

The task's one action is a launcher with one job:

```
"C:\Program Files\<product>\script-run.exe" <name> -- "C:\Program Files\PowerShell\7\pwsh.exe" -NoProfile -NonInteractive -File "C:\<locked folder>\<script>.ps1"
```

`script-run` asks the host to admit it (section 7), reads the script's own protected file, starts the command with `AGENT_BRIDGE_TOKEN` in **that child's
environment**, waits, and exits with the child's exit code. The script keeps using `AGENT_BRIDGE_TOKEN` as the fleet scripts already do. The host binary
also exposes the same behaviour as `agent-bridge-host scripts run`, for a person testing by hand with no task binding; it is refused for a bound credential.

## 3. What reaches the child, and what is required of the command

- **The command is pinned.** `script-run` refuses a command that is not an absolute path to an executable in an admin-only location, refuses a relative name
  (no PATH, current-directory or profile lookup), and for PowerShell requires `-NoProfile -NonInteractive -File <absolute script>`. The arguments are part of the
  task definition and so are covered by the definition hash; the script file, and every file it runs, are covered only if named with `--file` at mint (the
  existing rule), so the mint press lists them for the person.
- **The environment is built, not inherited whole.** It is the environment the Scheduler gives the task, minus every `AGENT_BRIDGE_*` variable (so a
  planted variable cannot shadow the real one), plus the token. A user can still edit the user-level variables a task starts with; the pin cannot cover that, and
  the absolute-path rule is what stops a redirected PATH from choosing the program.
- **Stdout and stderr pass straight through**; `script-run` captures and echoes nothing of the child's output. The script must not print the token, and the
  fleet scripts already do not.

## 4. The pinned program (the P1 of the read): the host image cannot be the task's program

The pin refuses a task unless its program and every folder above it are writable only by SYSTEM and Administrators (`ScriptTaskBinding.Pin`, re-checked at
every accept). The host ships in `%LOCALAPPDATA%\AgentBridgeApp\current\`, a folder the user can write, and an update replaces it with a new hash. So:

- **The host image as the pinned program is rejected.** It could not pass the pin's own lock rule, so every bound credential would stay refused. Relaxing the
  rule for the host image is also rejected: a same-user process could rename the user-writable exe, put a trojan at the task's path, let the scheduler start it
  (it decrypts the token itself), and restore the original file before the hash check.
- **The pinned program is `script-run.exe`, a separate tiny launcher installed in an admin-only folder** (Program Files), not in the update path of the host.
  It holds only what section 2 describes. It changes rarely; when it does, a person re-pins by a press. A host (Velopack) update does **not** change it, so a host
  update never invalidates a bound credential.
- Installing the launcher needs elevation once per machine. That is the same elevation the base doc leaves open for the task binding ("how that service is
  elevated is #1414's design to settle"): **Q-run-3 (owner):** is a Windows consent prompt on a person's press in the app the acceptable way to install
  and re-pin it (no terminal, no PowerShell)? Until it is answered, S1 to S3 below are not started.

## 5. The token's exposure, stated honestly

- **Inheritance.** Every descendant of the script (git, gh, an agent CLI it starts) inherits the child's environment. This is narrower than a per-VM variable (one
  task's process tree for the length of one run, not every session on the machine) but it is **not zero**, which is the same inheritance the base doc objects
  to. A script that starts something it does not trust must not pass the environment on; the fleet scripts that run the dispatcher cycle are reviewed for that in S4.
- **Same-user readers.** A process of the same user can read another process's environment (not only a dump). That is the base doc's honest residual
  (section 6, "Rule 3 is a safety rail, not a boundary") and does not change.
- **How the secret is held.** The DPAPI result is a byte array. It is decoded straight into a pinned UTF-16 buffer and written into an explicit environment
  block for a native `CreateProcessW`; it is never a .NET string and never goes through `ProcessStartInfo.Environment` (its values are immutable strings that
  cannot be zeroed). The buffers are zeroed on every exit path, including a failed start (nothing is left behind). The window is the time to start the child.
- **Never** in an argument (so not in the process list or the task history), the audit row, the host log, a file, shell history or a notice. The audit row
  holds the script name, the command's file name, a hash of the command line, the admission result and the child's exit code.
- **Dumps.** A dump of the child can hold its environment, as it can when the script reads the file itself (unchanged). A dump of `script-run` during the
  start window could hold the token; the launcher turns off its own crash-dump collection and keeps the window short, and a canary test checks it (S4).

## 6. Barrier versus rail (this must agree with the base doc and local-pipe.md)

- **What is a boundary:** the **admin-only ACLs** on the task, `script-run.exe`, the script and its `--file`s, and the **pin** (task path, definition hash, file
  hashes). A same-user process cannot edit those, so it cannot make the scheduler run something else with the token.
- **What is a rail, not a boundary:** every **ancestry check**, at `script-run`'s admission and again at `agent.hello`. A same-user process that spoofs a parent,
  or reaches into a running instance, can get past them; the base doc says so and this note does not claim otherwise. The residual is the base doc's section 6
  "Honest residual" and agent-principals.md section 1.3 (the same-user limit), not only Q7.
- **The worst case of a same-user thief** holding the token: scope `read+reply`; the credential's life (30 days, or less if the owner set it); revoked by one
  Revoke press; and, **if the script's credential has the `launch:fleet` tick** (the dispatcher's does), fleet launches within the ticked workspaces and the
  fleet caps. So the base doc's "no launch" comparison with the HA token does not hold for the dispatcher's credential, and this note does not repeat it.
- Another local process calling `script-run`/`scripts run` for a credential that is not its own is refused at admission (section 7): an unbound credential is
  never admitted, and a bound one is admitted only to a descendant of that task's running engine.

## 7. How `script-run` learns state: the host admits it

`script-run` does not read the credential store itself. It asks the host over the local pipe for one new operation, `scripts.admit` (name to be fixed in S1),
and the **host** decides, using the same `ScriptTaskBinding.Refusal` rule it applies at `agent.hello`: the credential is live and unexpired, it is task-bound, a
running instance of the bound task (as the Scheduler reports it, same pid and start time) is an ancestor of the caller, and the task, its ACLs and its file
hashes still match the pin. The host writes the audit row **before** it answers; an unwritable audit refuses ("Audit before use"). Only then does `script-run`
read its file. During a rotation the file holds the newest token and the host says which credential is being admitted.

- **Host not running:** refused, fail closed, exit code reserved for "host unavailable"; nothing is read.
- **Reserved exit code:** a refusal exits `125` with one line holding only a closed code (below). A child's own exit code passes through; a child that itself
  exits `125` is mapped to `1`, so the two never overlap the CLI's `2` and `4`.
- **Closed set of refusal codes shown to the caller:** `refused` (any credential, binding, ancestry or file reason), `host-unavailable`, `bad-command`. The
  precise cause goes only into the audit row, so a same-user process cannot probe which names exist or what state they are in.
- **If the engine is not reported as the `script-run` process** (for example an intermediate `taskeng` or `taskhostw`), the credential **refuses**; the vector
  for that is part of S3.
- **Existing command rules.** host-config.md and local-pipe.md say the `scripts` commands are person-only and refuse when the ancestry cannot be read in full.
  `script-run` is deliberately the opposite (its parent is the Scheduler's SYSTEM host). S1 adds that one exemption, for `scripts.admit` from a bound task's
  descendant and nothing else, and updates both documents.

## 8. DPAPI: who can decrypt

The file is wrapped with Windows **CurrentUser** DPAPI. Only the user who minted it, on that machine, can unprotect it, so the task must run **as the same
Windows user that runs the host**; `SYSTEM` or another account fails closed (`refused`).
- **Q-run-1 (design):** keep CurrentUser DPAPI (no format change; assumed here), or machine-scope so a `SYSTEM` task can read it? Machine scope lets every local
  account decrypt it and is a **format change**, so out of scope unless the owner asks.
- **Q-run-2 (owner):** how the task's user is registered. "Run only when the user is logged on" needs no stored password; "whether logged on or not" needs the user's
  password stored by the scheduler, which is the owner's decision and which no agent sets.
- **Q-run-4 (owner; the P2-6 point, open with Q-run-2):** who registers and edits the admin-locked task with no terminal step. Default proposed: the app's task-bound mint press creates it
  through the elevated step of Q-run-3, from what the person names in the press (task name, script, user), and shows them exactly what will run.

## 9. Slices, order and acceptance (each its own sensitive PR, each under the 400-line cap)

3a (the expiry warning, #4428) is UI only and independent of everything below.

| Order | Slice | Delivers | About | Acceptance and tests (RED first) |
|---|---|---|---|---|
| 1 | S3 | The Windows `IScriptTaskReader` (engine reads, task definition, ACL and file hashes) | 300 | Fakes plus real-Windows vectors: the engine is the `script-run` process; an intermediate engine is refused; an unlocked file is refused |
| 2 | S1 | `script-run` launcher and `scripts.admit` | 300 | An unbound credential is never admitted; a non-descendant is refused; a refused or unwritable audit stops it; a relative command, PowerShell without `-NoProfile`, and PATH lookup refused; `125` mapping; host-down fails closed; the token is in no argument, log or audit row |
| 3 | S2 | The app's task-bound mint press, the launcher install and re-pin (blocked until the owner answers Q-run-2, Q-run-3 and Q-run-4) | 350 | The press shows the person the task, user, program and files; a host update does not invalidate it; an unlocked program is refused at mint |
| 4 | S4 | The canary test (profile, logs, audit, task output, a forced-crash dump folder); doc updates (section 7 of script-credentials.md, host-config.md, local-pipe.md, the fleet README); a review of the dispatcher scripts for what they hand on | 200 | The planted token is found nowhere; a script that passes its environment to an untrusted child fails the review |

S2 uses the real reader of S3, so S3 comes first; S1 can be built with S3's fakes. Nothing here changes the credential format, the stored hash, the scope rules or who
may press mint, rotate and revoke.

## 10. Not in this note

Machine-scope DPAPI (Q-run-1), storing a task password (Q-run-2), automatic renewal (a person approves every rotation), a remote mint, any change to the
`launch:fleet` tick, and any non-Windows host.
