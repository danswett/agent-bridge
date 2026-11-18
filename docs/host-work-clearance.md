# Work-state clearance for fleet stops

The host reads the discovered session's exact process, repository, branch and working
directory, never a caller's clearance claim. A clean worktree and a proved pushed head,
no open branch PR, and no held/private markers must each be positively established.
The per-worktree git directory recognizes `agent-bridge-host-held`,
`agent-bridge-host-private` and git's `locked`; unreadable evidence is Unknown.

GitHub reads are unauthenticated, bounded and on demand, never a poller. Missing,
private or fork repository metadata, failed reads and unproved branch ancestry fail
closed. Git status disables fsmonitor execution; evidence is dated before reading.
This adopts the predecessor clearance implementation for
[agent stopping](https://github.com/danswett/agent-bridge/issues/2741), without granting
stop authority. The separate authorization gate must bind and recheck this evidence.
