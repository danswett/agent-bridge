using AgentBridge.Host.Core;
using AgentBridge.Host.Delivery.Launch;

namespace AgentBridge.Host;

internal sealed record FleetLaunchStatus(bool Enabled, int FleetPerHour, int LiveFleetWorkers,
    int Hourly, int Live, string[] Reserved);

internal sealed class FleetLaunchState(AgentAuditLog audit, ISessionSource? source, ISessionLauncher? launcher, TimeProvider time)
{
    List<AuditCommands.Row> Read()
    {
        var (rows, complete) = GateAuditProjection.Load(audit.Profile);
        if (!complete || !audit.Verify().Intact || !audit.Verify(launch: true).Intact)
        { throw new InvalidOperationException("fleet admission history cannot be checked safely"); }
        return rows;
    }

    static IEnumerable<AuditCommands.Row> Intents(IEnumerable<AuditCommands.Row> rows) =>
        rows.Where(row => row.Grant == "fleet" && row.Action == "session.launch"
            && row.Gate is { Phase: "intent" } && row.Outcome == "reserved");
    static bool Retired(AuditCommands.Row row, string request) =>
        row.Action == "fleet.retire" && row.Principal == "person:local" && row.Grant == "fleet"
            && row.Outcome == "retired" && row.Request == request && row.Target == request;

    internal FleetLaunchStatus Status()
    {
        var (settings, problem) = HostSettings.Load(audit.Profile);
        if (problem is not null || settings.SectionProblems.Any())
        { throw new InvalidOperationException("fleet admission settings cannot be read safely"); }
        var rows = Read();
        var intents = Intents(rows).ToArray();
        var completed = rows.Where(row => row.Action == "session.launch" && row.Gate is { Phase: "terminal" })
            .Select(row => row.Gate!.Operation).ToHashSet();
        var retired = rows.Where(row => row.Request is { } id && Retired(row, id)).Select(row => row.Request).ToHashSet();
        var now = time.GetUtcNow();
        var reserved = intents.Where(row => row.At > now - TimeSpan.FromMinutes(10)
            && !completed.Contains(row.Gate!.Operation) && !retired.Contains(row.Request))
            .Select(row => row.Request!).Distinct().ToArray();
        if (source is null || source.Held is not null || launcher is null)
        { throw new InvalidOperationException("fleet admission cannot check live workers while session discovery is incomplete"); }
        var registered = 0;
        var processes = new HashSet<SessionProcess>();
        try
        {
            foreach (var session in source.Snapshot())
            {
                var process = source.ProcessOf(session.Id);
                if (launcher.IsFleetWorker(session.Id, process) && process is not null && processes.Add(process)) { registered++; }
            }
        }
        catch (InvalidOperationException)
        { throw new InvalidOperationException("fleet admission cannot check live workers safely"); }
        var limits = settings.Agents.Limits.Launch;
        return new(settings.Agents.Enabled, limits.FleetPerHour, limits.LiveFleetWorkers,
            intents.Where(row => row.At > now - TimeSpan.FromHours(1)).DistinctBy(row => row.Request).Count(),
            reserved.Length + registered, reserved);
    }

    internal object Retire(string request)
    {
        var rows = Read();
        var intent = Intents(rows).SingleOrDefault(row => row.Request == request)
            ?? throw new ArgumentException("no fleet launch reservation exists for that request");
        if (!rows.Any(row => Retired(row, request)))
        {
            if (rows.Any(row => row.Action == "session.launch" && row.Gate is { Phase: "terminal" } gate
                && gate.Operation == intent.Gate!.Operation))
            { throw new InvalidOperationException("only an unresolved fleet launch reservation may be retired"); }
            // Separate evidence releases the reservation without rewriting launch outcomes or replay.
            if (!audit.Append(new("local", request, "person:local", [], "fleet.retire", request, "retired") { Grant = "fleet" }))
            { throw new InvalidOperationException("refused: fleet retirement could not be recorded; the reservation remains held"); }
        }
        return new { Request = request, Retired = true };
    }
}
