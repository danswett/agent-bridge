# Managed macOS terminal delivery

`ManagedTmuxBackend` supplies the macOS implementations of `IAgentStarter`,
`IConsoleInjector`, `ITargetGuard` and `IProcessControl`. The host must share one
instance between launch, answer, reply and stop, and keep its G5/read-only probe on
all of those services. This adapter alone does not enable capabilities or hook intake.

The constructor takes an absolute tmux executable and a private runtime directory.
It creates a fresh owner-only subdirectory and Unix socket; the complete socket path
must fit in 100 UTF-8 bytes. It never connects to the default tmux server or loads the
user's tmux configuration. A launch uses a detached 220-by-50 pane. No Terminal
Automation or Accessibility permission, foreground-window switching, or global
keystroke injection is involved. A person can attach to the private socket and the
session name reported by `Snapshot()` using tmux at the machine:

```sh
tmux -S '<SocketPath>' attach-session -t '<SessionName>'
```

Replace those two placeholders with the backend's socket path and managed session
name. Detaching from that terminal leaves the session running. The backend has no
shutdown operation which kills the server or its panes; host exit must not end an
agent's work. Old private socket folders are not automatically swept while a pane
may still be using them.

Only panes started by this backend instance are eligible. Each registry entry binds
the tmux pane and session to the server and agent process IDs, kernel start times,
executable paths, and effective, real and saved user IDs. A descendant is eligible
only through a freshly verified process ancestry chain. Existing Terminal or tmux
sessions are never adopted. After a host restart previous panes remain read-only;
the user may finish those sessions in their terminal. The snapshot exposes only
launch identity, paths and the known session ID hint, never arbitrary environment
values or the prompt arguments.

The native identity reader uses Darwin `proc_pidinfo(PROC_PIDTBSDINFO)` and
`proc_pidpath`; its ABI comes from Apple's
[proc_info.h](https://github.com/apple-oss-distributions/xnu/blob/main/bsd/sys/proc_info.h).
An executable or shebang interpreter must match the observed process image before
registration. Ambiguous shell wrappers and later image changes fail closed. An
explicit native CLI path is preferable to a wrapper which replaces itself with a
different executable. Root or set-user-ID processes are refused.

tmux receives separate arguments, with no generated shell command. Its argv parser
also treats trailing semicolons as command separators, so those are escaped using
the behavior of
[cmd_parse_from_arguments](https://github.com/tmux/tmux/blob/3.6a/cmd-parse.y#L1000).
Working-directory format markers are escaped. Reply text is literal input; Enter,
Up, Down and Escape are deliberate separate keys. Unknown control sequences are
refused before any key. `KeyPlan.Proceed` and visible-row expectations remain in
force. A lost command response, cancellation after dispatch, or failure after any
possibly delivered key returns `Interrupted`, never a safe-to-retry result.

Graceful stop uses the shared service's `/exit` behavior and confirmation rules.
When a forced stop is necessary, the adapter revalidates the binding and closes
only that owned pane. This closes its pseudo-terminal; it does not use `kill(pid)`
or claim Windows' retained-process-handle semantics. Unknown or changed processes
and panes are refused.

A launch whose tmux response or identity cannot be verified is **unconfirmed**.
The host remembers the request UUID, consumes potentially handed attachment IDs
while retaining their staged files, and retains any worktree reservation. The user
must check the terminal before starting another session or manually unlocking the
worktree. No uncertain launch is automatically repeated.

Tests use fake identity/command probes for refusal and race cases, plus disposable
private tmux servers running only test-authored echo programs on macOS. They never
launch a real agent, change agent profiles/hooks, or touch the installed legacy bridge.
