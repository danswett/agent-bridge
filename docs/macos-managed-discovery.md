# Managed macOS session discovery

Pass `new ManagedSessionProcessInspector(backend)` to `SessionSource`, sharing the
same `ManagedTmuxBackend` used by launch and delivery. Its normal process listing
forwards `SystemProcessInspector` unchanged: external Copilot, Claude and registered
Codex sessions remain visible, with the same uncertainty and process-generation
semantics. This does not grant them delivery ownership. The managed backend still
refuses typing or stopping a process outside its own verified pane registry.

The adapter adds the missing macOS Codex thread-lock lookup. Darwin's
`PROC_PIDLISTFDS` and `PROC_PIDFDVNODEINFO` enumerate settled descriptor lists and
compare device, inode and generation with the regular lock file's `lstat64`
identity. The query includes external Codex processes and unidentified runtimes to
detect shared or unreadable holders; any unowned holder makes the result ambiguous
and refuses the binding. A filtered `lsof` query is insufficient: it can omit a
failed descriptor read while returning another process successfully.

The candidate universe comes from G5's raw PID, owner and stable executable checks,
using a Codex/node/bun name filter. An unreadable same-user identity or path refuses
the lookup, even if .NET omitted that process. A separately proven foreign user is
excluded. The G5 PowerShell filter and the normal process observation are unchanged.

Limits are 256 processes, 4,096 descriptors per process, 16,384 in total, and a
three-second traversal budget checked between kernel calls. Native calls cannot be
cancelled while inside the kernel. A full or malformed descriptor buffer, unknown
descriptor type or any vnode read failure refuses the entire lookup. Descriptor
lists and vnode identities are read again, process generations are checked before
and after, and the lock path must still identify the same regular file. The adapter
then rechecks the holder against the backend's owned ancestry and registered pane.
No paths from descriptors, arguments or environment are collected, no foreign pane
is adopted, and no hook registration is synthesized.

These are bounded point-in-time observations, not an atomic proof of all concurrent
opens and closes. Darwin omits descriptors already closing, and file generation is
normally zero for an unprivileged caller. Device/inode comparisons and repeated
identity/descriptor checks reduce stale observations; exact registered process and
pane ownership remains the authority for delivery.

The native layouts come from Apple's
[proc_info.h](https://github.com/apple-oss-distributions/xnu/blob/main/bsd/sys/proc_info.h)
and [stat.h](https://github.com/apple-oss-distributions/xnu/blob/main/bsd/sys/stat.h).
The used sizes and field offsets were checked by compiling SDK-header assertions
for both arm64 and x86_64.

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
