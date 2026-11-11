namespace AgentBridge.Protocol;

[UnknownAs(Unavailable)]
public enum MachineLoadState { Unavailable, Fresh, Partial, Stale }

[UnknownAs(Unavailable)]
public enum SessionLoadState { Unavailable, Fresh, Partial }

[UnknownAs(Other)]
public enum LoadAgentKind { Other, Agency, Direct, Copilot, Claude, Codex }

[UnknownAs(Other)]
public enum LoadHandleKind { Other, Handles, Fds }

// Relay-only numerical readings (v1-machine-load.md). Unknown figures stay absent, never zero.
public sealed record MachineLoad
{
    MachineLoadState state;
    IReadOnlyList<SessionLoad> sessions = [];
    bool truncated;

    public DateTimeOffset? SampledAt { get; init; }
    public long? AgeMs { get; init; }
    public MachineLoadState State { get => truncated ? MachineLoadState.Partial : state; init => state = value; }
    public long? StaleAfterMs { get; init; }
    public MachineLoadCpu? Cpu { get; init; }
    public MachineLoadMemory? Memory { get; init; }
    public IReadOnlyList<SessionLoad> Sessions
    {
        get => sessions;
        init
        {
            truncated = value.Count > 200;
            sessions = truncated ? [.. value.OrderByDescending(s => s.WorkingSetBytes).Take(200)] : value;
        }
    }
    public UnattributedLoad? Unattributed { get; init; }
}

public sealed record MachineLoadCpu(double? Percent = null, int? Cores = null);
public sealed record MachineLoadMemory(long? TotalBytes = null, long? AvailableBytes = null, double? CommittedPercent = null);
public sealed record UnattributedLoad(int? Processes = null, long? WorkingSetBytes = null);

public sealed record SessionLoad
{
    [Strict(Strictness.Identity)]
    public required string Session { get; init; }
    public LoadAgentKind Agent { get; init; }
    public int? Processes { get; init; }
    public double? CpuPercent { get; init; }
    public long? WorkingSetBytes { get; init; }
    public int? Handles { get; init; }
    public LoadHandleKind? HandleKind { get; init; }
    public SessionLoadState State { get; init; }
}
