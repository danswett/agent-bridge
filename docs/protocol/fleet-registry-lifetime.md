# Session registry lifetime

Each accepted host snapshot replaces its preceding snapshot, never merges with it.
An omitted session is removed immediately, including when the new snapshot is empty;
it has no per-session TTL. Host disconnect drops the machine's session record at once.
`ending` still means a live process being stopped, not an already-ended session.

The 60-second heartbeat window detects a silent host, not an ended-session grace period.
On an expired read, the relay erases the session payload and returns JSON null.
Only offline machine metadata and the ordering watermark remain; older snapshots
still refuse, and a clock rewind cannot resurrect erased sessions.
A newer valid snapshot restores the live record. State stays in memory, without HA.
