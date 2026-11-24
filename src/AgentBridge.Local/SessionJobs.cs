namespace AgentBridge.Local;

// design/agent-principals.md §3.4, "Session job". A launch's root process is put in a Windows Job object the host makes for it, with
// no limit but permission to break away (never kill-on-close: a host restart must not end sessions). Children inherit the job, so a process started
// detached, or whose parent has exited, is still a member although its parent-pid chain no longer reaches the root.
public interface ISessionJobs
{
    // Puts the launch's root process in a new job for this key, kept by the host (unnamed: nothing else can open it). Best effort: the caller catches a failure,
    // and the ancestor rule alone then applies.
    void Enroll(string key, int processId, DateTimeOffset startedAt);

    // Whether the process (pid and start time, so a recycled id is not mistaken) is in the job for this key. The job is believed only
    // if the recorded root, pid and start time, is itself in it. False for anything unreadable, a process that has exited, or a key this
    // host holds no job for.
    bool Contains(string key, int rootPid, DateTimeOffset rootStartedAt, int processId, DateTimeOffset? startedAt);

    // The credential is gone: the host lets go of its job. The job itself lives on while its processes do, and nothing is ended.
    void Release(string key);
}
