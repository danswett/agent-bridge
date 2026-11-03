# Managed macOS session discovery

Pass `new ManagedSessionProcessInspector(backend)` to `SessionSource`, sharing the
same `ManagedTmuxBackend` used by launch and delivery. Its normal process listing
forwards `SystemProcessInspector` unchanged: external Copilot, Claude and registered
Codex sessions remain visible, with the same uncertainty and process-generation
semantics. This does not grant them delivery ownership. The managed backend still
refuses typing or stopping a process outside its own verified pane registry.

The adapter adds the missing macOS Codex thread-lock lookup. `/usr/sbin/lsof` receives
an exact lock path and at most 256 observed Codex PIDs as separate arguments. It
returns only PIDs and numeric file descriptors, with a three-second deadline and
16 KiB output limits. The query
includes external Codex processes to detect locks shared with a daemon or another
terminal; any unowned holder makes the result ambiguous and refuses the binding.
The adapter accepts a holder only when its kernel start time agrees with discovery,
its exact identity survives the query, and the backend verifies its ancestry and
registered pane before and after. A changed, absent or unreadable identity does
not acquire a session. No arguments or environment are published and no hook
registration is synthesized.

Existing native discovery continues to choose the session: Copilot's own lock and
session ID, Claude's own session file, and Codex's own writer lock plus top-level
rollout attribution. The adapter does not turn the generic `launched` marker into a
guessed Codex session ID. Unregistered external Codex sessions supported only by
writer locks remain undiscovered, as before; they are never adopted for delivery.

Keep the existing global process inspector for worktree reclamation and machine
usage. No host configuration, capability, installed hook, `HOME` or `CODEX_HOME`
changes are made by this adapter. Tests use invented agent homes and injected
ownership, a real macOS file query against a disposable file opened by the test
process, and an authored lock holder in a real private tmux pane. The latter tests
the kernel identity, backend ownership, file lookup and `SessionSource` together;
only the fixture's agent classification is injected. They do not launch a real
agent or read installed session history.
