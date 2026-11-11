using AgentBridge.Protocol;
using WireLoad = AgentBridge.Protocol.MachineLoad;
using WireSession = AgentBridge.Protocol.SessionLoad;

namespace AgentBridge.Host.Core.Load;

public static class MachineLoadMapper
{
    const long MaxInteger = 9007199254740991;
    const int MaxBytes = 64 * 1024;

    public static WireLoad Map(ProcessLoadReport? report, DateTimeOffset now, TimeSpan staleAfter)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(staleAfter, TimeSpan.Zero);
        var result = new WireLoad { StaleAfterMs = staleAfter.Ticks / TimeSpan.TicksPerMillisecond };
        if (report is null) { return result; }
        var machine = report.Machine;
        var percent = Percent(machine.CpuPercent);
        var total = Number(machine.TotalMemoryBytes);
        var available = Number(machine.AvailableMemoryBytes);
        var sessions = report.Sessions.Select(s => MapSession(s.Key, s.Value)).ToArray();
        long? age = now >= report.SampledAt ? (now - report.SampledAt).Ticks / TimeSpan.TicksPerMillisecond : null;
        var any = percent is not null || total is not null || available is not null
            || sessions.Any(s => s.State != SessionLoadState.Unavailable);
        var complete = percent is not null && machine.Processors > 0 && total is not null && available is not null
            && sessions.All(s => s.State == SessionLoadState.Fresh);
        result = result with
        {
            SampledAt = report.SampledAt, AgeMs = age,
            State = !any || age is null ? MachineLoadState.Unavailable : age > result.StaleAfterMs ? MachineLoadState.Stale
                : complete ? MachineLoadState.Fresh : MachineLoadState.Partial,
            Cpu = percent is null ? null : new(percent, machine.Processors > 0 ? machine.Processors : null),
            Memory = total is null && available is null ? null : new(total, available),
            Sessions = sessions,
        };
        // Account for escaped UTF-8 bytes, not characters; trim the smallest working sets first.
        var kept = result.Sessions.OrderByDescending(s => s.WorkingSetBytes).ToList();
        while (ProtocolJson.ByteCount(result) > MaxBytes)
        {
            kept.RemoveAt(kept.Count - 1);
            result = result with
            {
                Sessions = kept.ToArray(),
                State = result.State == MachineLoadState.Fresh ? MachineLoadState.Partial : result.State,
            };
        }
        return result;
    }

    static WireSession MapSession(string id, SessionLoad load)
    {
        if (load.State is not (LoadState.Ok or LoadState.Partial)) { return new() { Session = id }; }
        int? processes = load.Processes >= 0 ? load.Processes : null;
        var cpu = Percent(load.CpuPercent);
        var workingSet = Number(load.WorkingSetBytes);
        return new()
        {
            Session = id, Processes = processes, CpuPercent = cpu, WorkingSetBytes = workingSet,
            State = processes is null && cpu is null && workingSet is null ? SessionLoadState.Unavailable
                : load.State == LoadState.Ok && processes is not null && cpu is not null && workingSet is not null
                    ? SessionLoadState.Fresh : SessionLoadState.Partial,
        };
    }

    static double? Percent(double? value) => value is >= 0 and <= 100 ? value : null;
    static long? Number(long? value) => value is >= 0 and <= MaxInteger ? value : null;
}
