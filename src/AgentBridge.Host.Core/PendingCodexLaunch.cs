namespace AgentBridge.Host.Core;

// A Codex this host started and is still waiting to see register, as its launch knows it: the
// process it started, when that process started (the OS's own time for it, which tells it from
// another process given the same pid), and the folder it was asked to run in. Discovery binds a
// hook registration that names no process to such a launch (SessionSource.PendingCodexLaunches).
public sealed record PendingCodexLaunch(int ProcessId, DateTimeOffset? StartedAt, string? Folder);
