namespace AgentBridge.Host;

public sealed record GateAuditMetadata(int V, Guid Operation, Guid Run, long Sequence, string Phase, string Path,
    string ActorKind, Guid Incarnation, string ReasonCode, Guid? Question = null, string? LaunchBinding = null,
    DateTimeOffset? PauseDeadline = null)
{
    internal bool Valid(string action, string? target, string outcome) =>
        V == 1 && Operation != Guid.Empty && (Run != Guid.Empty || action is ("session.launch" or "session.answer" or "session.stop") && Phase != "control") && Sequence > 0
        && Phase is "intent" or "terminal" or "control" && Path is "relay" or "local" or "legacy-ha"
        && ActorKind is "person-client" or "agent" or "ha-component"
        && (PauseDeadline is null || Phase == "control" && action == "gate.pause" && outcome == "paused")
        && (LaunchBinding is null || action == "session.launch" && LaunchBinding.Length == 64 && LaunchBinding.All(char.IsAsciiHexDigit))
        && ReasonCode is "none" or "refused" or "failed" or "unconfirmed" or "busy" or "cancelled" or "forced"
            or "pause-timeout" or "control-lost" or "clock-uncertain" or "restart"
        && (Phase == "control" ? target == "profile" && (action is "gate.start" or "gate.pause" or "gate.reset" or "gate.seal")
            && (outcome is "prepared" or "paused" or "reset" or "sealed" or "invalidated"
                || action == "gate.start" && outcome == "pending" && Path == "relay" && ActorKind == "person-client")
            : Incarnation != Guid.Empty && Guid.TryParse(target, out var id) && id != Guid.Empty && target == id.ToString()
            && GateAuditProjection.SuccessFor(action) is { } success
            && (outcome == success || outcome is "attempted" or "reserved" or "refused" or "failed" or "unconfirmed" || action == "session.stop" && outcome is "confirm" or "busy")
            && (action == "session.answer" ? Question is { } question && question != Guid.Empty : Question is null));
}

internal sealed record GateAuditTotal(string Action, string Path, string ActorKind, int Successes);
internal sealed record GateAuditReport(IReadOnlyList<GateAuditTotal> Totals, IReadOnlyList<string> IncompleteReasons, bool Invalidated);

internal static class GateAuditProjection
{
    internal static string? SuccessFor(string action) => action switch
    {
        "session.launch" => "started", "session.answer" => "delivered", "session.stop" => "ended", _ => null
    };

    internal static (List<AuditCommands.Row> Rows, bool Complete) Load(string profile, Action<string>? fault = null, bool alreadyLocked = false, Action<long, bool>? boundary = null, AuditLockTiming? lockTiming = null)
    {
        profile = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(profile));
        var rows = new List<AuditCommands.Row>();
        var error = new StringWriter();
        var code = AuditCommands.Read(["audit", "--profile", System.IO.Path.GetFileName(profile)],
            System.IO.Path.GetDirectoryName(profile)!, TextWriter.Null, error, rows.Add, fault, alreadyLocked, boundary, lockTiming);
        return (rows, code == 0 && error.GetStringBuilder().Length == 0);
    }

    internal static GateAuditReport Read(string profile, Guid run)
    {
        var coverage = false;
        var adopted = false;
        long retained = 0;
        var (rows, complete) = Load(profile, fault => coverage |= fault == "coverage-break",
            boundary: (floor, unproven) => { retained = floor; adopted = unproven; });
        return Project(rows, complete && !adopted, run, coverage: coverage || retained > 0, retainedThrough: retained);
    }

    internal static GateAuditReport Project(IReadOnlyList<AuditCommands.Row> rows, bool complete, Guid run, Guid? onlyOperation = null, bool coverage = false, long retainedThrough = 0)
    {
        var reasons = new HashSet<string>();
        if (run == Guid.Empty) { reasons.Add("missing-run-context"); }
        if (!complete) { reasons.Add("audit-incomplete"); }
        if (coverage) { reasons.Add("coverage-break"); }
        var sequences = rows.Where(r => r.Gate is not null).Select(r => r.Gate!.Sequence).Order().ToArray();
        if (sequences.Distinct().Count() != sequences.Length
            || sequences.Where(s => s > retainedThrough).Where((s, i) => s != retainedThrough + i + 1L).Any()) { reasons.Add("sequence-break"); }
        var selected = rows.Where(r => r.Gate is { } g && g.Run == run && g.Phase != "control"
            && (onlyOperation is null || g.Operation == onlyOperation)).OrderBy(r => r.Gate!.Sequence).ToArray();
        if (selected.Length == 0) { reasons.Add("missing-run-evidence"); }
        var totals = new Dictionary<(string Action, string Path, string ActorKind), int>();
        var identities = new HashSet<(string Action, string? Target, Guid Incarnation, Guid? Question)>();
        var invalidated = coverage || onlyOperation is null
            && rows.Any(r => r.Gate is { Phase: "control" } g && g.Run == run && r.Outcome == "invalidated");
        foreach (var operation in selected.GroupBy(r => r.Gate!.Operation))
        {
            var first = operation.First();
            var gate = first.Gate!;
            var launch = first.Action == "session.launch";
            var binding = launch && first.Target == first.Request;
            var key = (first.Action, gate.Path, gate.ActorKind);
            totals.TryAdd(key, 0);
            var terminals = operation.Where(r => r.Gate!.Phase == "terminal").ToArray();
            if (launch && !Guid.TryParseExact(first.Request, "D", out _)
                || operation.Any(r => r.Action != first.Action || launch && r.Request != first.Request
                || r.Target != first.Target && (!binding || r.Gate?.Phase != "terminal") || r.Gate is not { } other || other.Path != gate.Path
                || other.ActorKind != gate.ActorKind || other.Incarnation != gate.Incarnation || other.Question != gate.Question
                || other.LaunchBinding != gate.LaunchBinding)
                || rows.Any(r => r.Gate is { } other && other.Operation == gate.Operation && other.Run != run)
                || terminals.Select(r => (r.Outcome, r.Gate!.ReasonCode, r.Target)).Distinct().Count() > 1)
            { invalidated = true; continue; }
            if (gate.Phase == "terminal" && operation.All(r => r.Gate is { Phase: "terminal" }
                && (first.Action == "session.answer" && r.Gate.ReasonCode == "refused" && r.Outcome == "refused"
                    || first.Action == "session.stop" && r.Outcome is "refused" or "busy" or "failed"))) { continue; }
            if (gate.Phase != "intent" || terminals.Length == 0 || terminals[0].Outcome is "attempted" or "reserved" or "unconfirmed")
            { reasons.Add("unresolved-intent"); continue; }
            if (terminals[0].Outcome == SuccessFor(first.Action) && run != Guid.Empty)
            {
                if (!identities.Add((first.Action, terminals[0].Target, launch ? Guid.Empty : gate.Incarnation, gate.Question))) { invalidated = true; continue; }
                totals[key]++;
            }
        }
        return new(totals.Select(p => new GateAuditTotal(p.Key.Action, p.Key.Path, p.Key.ActorKind, p.Value)).ToArray(),
            reasons.Order(StringComparer.Ordinal).ToArray(), invalidated);
    }
}
