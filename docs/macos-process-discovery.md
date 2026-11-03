# macOS process discovery

The host takes one bounded PID snapshot per agent discovery scan and matches the
agent executable, its `.exe` spelling, `node` and `bun` in memory. It reads BSD start
time and parent information only for matching names, with the BSD command name as
a fallback when an executable path cannot be read. Existing script classification,
loader suppression and command-line caching by PID and start time remain in use.
An unreadable generation stays unknown; failed, malformed or persistently full PID
lists are reported as an incomplete scan.

This avoids four `Process.GetProcessesByName` calls per agent on macOS. Each of
those .NET calls enumerates the entire machine and reads task information even for
nonmatching names. An idle-host sample showed process enumeration dominating the
active worker; a warmed local comparison reduced the combined CPU cost of three
agent scans from roughly 336 ms to 52 ms. This is a local measurement, not a timing
guarantee, and the polling interval is unchanged.

The discovery snapshot is not authorization to type or stop a session. G5's fresh
raw owner/process scan and the managed backend's process, pane and file-descriptor
checks remain separate. No process snapshot is cached across discovery passes.

Tests cover all candidate spellings in a single snapshot, unreadable names and
generations, runtime uncertainty, native PID-list growth/failure and exact Darwin
start-time decoding. Native structure offsets follow Apple's `sys/proc_info.h` on
both arm64 and x86_64.
