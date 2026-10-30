namespace AgentBridge.Host.Migration;

// A scheduled task as Task Scheduler reports it.
public sealed record ScheduledTaskState(bool Enabled, bool Running);

// What migration does to agent-ha-bridge's running pieces, behind an interface so tests drive a
// fake and never this machine's scheduler or processes. WindowsAgentHaBridgeRuntime is the real
// one.
public interface IAgentHaBridgeRuntime
{
    // The task, or null when there is none by that name.
    ScheduledTaskState? Task(string name);

    // Enables or disables it. Never deletes it: rollback turns it back on.
    void SetEnabled(string name, bool enabled);

    void Start(string name);

    // Stops this installation's supervisor, daemon and Dev Box keep-awake, supervisor first so it
    // cannot restart the daemon. Throws when one is still running afterwards.
    void Stop(AgentHaBridgeInstall install);

    // Design G5's answer: why agent-ha-bridge counts as running for this user, or null. Not being
    // able to tell counts as running.
    string? Active();

    // Whether that installation's own daemon was seen running: not being able to tell is no, and
    // another installation's daemon is not this one back.
    bool Seen(string installationId);

    // How many of the agent's processes run now, or null when they could not all be identified.
    // With startedBefore, only those started before then, or whose start cannot be told.
    int? Running(AgentBridge.Protocol.AgentKind agent, DateTimeOffset? startedBefore = null);
}

// The host serving this profile, as the migration needs it: to read intake.json (or its absence)
// afresh, it restarts.
public interface IMigrationHost
{
    Task<bool> RunningAsync(CancellationToken cancellationToken);

    // Null once the host has been asked to restart; otherwise why it could not be.
    Task<string?> RestartAsync(CancellationToken cancellationToken);
}

// Runs the installed hook as an agent would; Pid is the process it ran as, which names the spool
// file it writes.
public interface IHookRunner
{
    (int Exit, string Stdout, int Pid) Run(string executable, IReadOnlyList<string> arguments, string stdin);
}
