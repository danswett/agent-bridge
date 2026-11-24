# Script credentials: how a scheduled task gets its token (`scripts run`) (#3305 slice 3b, design only)

Status: DESIGN ONLY, no code. **Needs Quality's sensitive read, and possibly the owner** (Q-run-2 below decides which Windows user a task runs as).
Part of #3305 (parent #1421); closes the OPEN item in [script-credentials.md](script-credentials.md) section 7, "How a script gets it".
The credential format, the stored hash, the scope rules (`read+reply`, the allow-list) and the person-pressed mint, rotate and revoke are **unchanged**.

## 1. The problem

A scheduled task (for example the F7 dispatcher cycle) must authenticate as `script:<machine>/<name>`. Today nothing on `main` hands it its token:
the CLI has `scripts add|revoke|grant-fleet|revoke-fleet` only, the host writes the token into an owner-only DPAPI file, and a hand-edited wrapper
has to read that file and set `AGENT_BRIDGE_TOKEN`. The owner's rule is that nobody runs or edits PowerShell for this. The design doc
(section 7) also says the token must not be put in a per-VM environment variable: an inherited variable reaches every child, which is how the HA token
ended up in every session.

## 2. Proposal: `agent-bridge-host scripts run <name> -- <command> [args...]`

The task's one action is the host binary:

```
agent-bridge-host.exe scripts run <name> -- pwsh.exe -File <script> ...
```

`scripts run` reads the script's own protected file, starts `<command>` with `AGENT_BRIDGE_TOKEN` in **that child's environment only**, waits for it,
and exits with the child's exit code. Nothing else in the machine's environment, the task definition or any file contains the token. The script keeps
using `AGENT_BRIDGE_TOKEN` exactly as the fleet scripts already do, so no script changes.

Refusals (non-zero exit, a readable one-line reason, no token in it, one audit row): no file for `<name>`; the credential expired or revoked; the file
unreadable; the credential is not task-bound (section 5); the caller is not that task's running engine (section 5); a `<command>` that is missing.

## 3. The token never leaves the child's environment block

- Read from the DPAPI file into one pinned byte array, built into the child's environment block, handed to `CreateProcess`, then zeroed in the
  host process. The window is the time to start the child.
- Never printed, never in an argument (so not in the process list or the task's history), never in the audit log or the host log, never written to disk
  again. The audit row holds the script name, the command's file name, a hash of the full command line and the outcome.
- Crash dumps: the child's own dump can contain its environment, as it can today when the script reads the file itself; that is the script's residual
  and is unchanged. The host side keeps its own window short and zeroes the buffer; `scripts run` does not enable dumps and we state, rather than hide,
  that a dump taken during that window could hold the token. A canary test (as agent-token-rotation.md section 5 requires) plants a token and scans the
  profile, the host and audit logs, the task's output and a forced-crash dump folder of the `scripts run` process for it.
- The child's environment is built from the task's own environment plus the one variable; nothing is inherited from a session.

## 4. DPAPI: who can decrypt

The file is wrapped with Windows **CurrentUser** DPAPI (section 7 of script-credentials.md): only the user who minted it, on that machine, can unprotect it.
So the scheduled task must run **as the same Windows user that runs the host** (the "minting Windows user" the doc already says). A task that runs as
`SYSTEM`, or as another account, cannot decrypt the file, and `scripts run` fails closed with that reason.

- **Q-run-1 (design):** keep CurrentUser DPAPI (no format change; what this note assumes), or move the file to machine-scope DPAPI so a `SYSTEM` task can read it?
  Machine scope lets every local account's code decrypt it: weaker, and a **format change**, so out of scope here unless the owner asks for it.
- **Q-run-2 (owner, possibly):** is it acceptable that the task is registered "run as the host's user" (a task for that user, with or without a stored
  password)? A "run only when the user is logged on" task works with no stored password; "whether logged on or not" needs the user's password stored by the
  scheduler, which is a host-credential matter the owner decides, and no agent sets it.

## 5. What stops another local process from using `scripts run` for a credential that is not its own

Three layers, honest about which is a barrier and which is a rail.

1. **Only a task-bound credential can be run.** `scripts run` refuses an unbound credential outright. A bound credential records the pinned task path and
   hashes of the task definition and its files (`ScriptTaskBinding.Pin`, #3312). So there is no `scripts run` path for the unbound credentials that exist today.
2. **`scripts run` checks the binding itself before it decrypts anything.** It reads the caller's whole ancestry; it proceeds only when a running instance of
   the bound task, as the Task Scheduler reports it (`IScriptTaskReader.RunningEngines(task)`, same pid **and** start time), is its ancestor, and the task
   definition, ACLs and file hashes still match the pin. A person's terminal, an agent session, an ordinary process, or a process that copies the arguments
   fails this and never reads the file. This is the **rail** (a same-user process can still read the file directly: the existing residual, Q7).
3. **The host is the barrier, again at `agent.hello`.** Whoever holds the token still has to be the bound task's descendant when the host reads the
   caller's chain at `agent.hello` (section 6.3 of script-credentials.md), so a token read by another process, or from a dump, is refused as "presented from
   outside its task". `scripts run` adds no new way to get a token past that check; it only replaces the hand-edited wrapper that read the same file.

The admin-only ACLs on the task and its files remain what hold the binding, not ancestry (section 6.3): an agent cannot edit the task or the host binary it runs.

## 6. Do the ancestry and caller checks still hold with the host as parent?

The chain the host sees at `agent.hello` becomes: Task Scheduler engine -> `agent-bridge-host.exe scripts run` -> `<command>` (the script) -> its pipe connection.
- The bound task's **action is the host image** (the pinned program) and the command after `--` is part of the pinned task definition and hashes, so changing
  what the task runs changes the pin and refuses the credential.
- The "engine" the host matches is the process the scheduler started, i.e. the `scripts run` process. It is an ancestor of the caller, read in full, same pid
  and start time, as section 6.3 requires. The reverse rule is untouched: **no agent session may appear in the ancestry**; a partial or untraceable ancestry is
  refused.
- The `scripts run` process itself must be the exact installed host image (checked as the app's own guard checks the installed image), so a copied or renamed binary
  that sets the variable for another command cannot satisfy the pin.
- Open check for the build slice: confirm the scheduler's engine for a task whose action is the host image is reported as that process and not an intermediate
  (`taskeng`/`taskhostw`) the existing reader already handles, and add the vector.

## 7. Slices (each its own PR, each sensitive, each under the 400-line cap)

| Slice | Delivers | About |
|---|---|---|
| S1 | `scripts run`: argument parsing, task-binding check, file read, child environment, audit row, refusals; fakes for the task reader and process start | 300 |
| S2 | The app press that mints a **task-bound** credential: names the task (`\Folder\Task`) and any `--file` paths, shows the person the task, its user and its files, and the host service does the elevated binding (the `--task` mint press; separate because it is the elevated part, #3312's "Windows reads and `--task` follow") | 350 |
| S3 | The Windows `IScriptTaskReader` (Task Scheduler engine reads, ACL and hash reads) and the real-host vectors | 300 |
| S4 | The canary test across profile, logs, audit, task output and a forced-crash dump folder; docs: update section 7 of script-credentials.md ("How a script gets it") and the fleet README | 200 |

Nothing here changes the credential format, the stored hash, the scope rules or who may press mint, rotate and revoke.

## 8. Not in this note

Machine-scope DPAPI (Q-run-1), storing a task password (Q-run-2), automatic renewal (a person approves every rotation), a remote mint, any change to the
`launch:fleet` tick, and any non-Windows host.
