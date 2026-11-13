namespace AgentBridge.Host;

public sealed record GateAuditMetadata(int V, Guid Operation, Guid Run, long Sequence, string Phase, string Path,
    string ActorKind, Guid Incarnation, string ReasonCode, Guid? Question = null)
{
    internal bool Valid(string action, string? target, string outcome) =>
        V == 1 && Operation != Guid.Empty && Run != Guid.Empty && Sequence > 0
        && Phase is "intent" or "terminal" or "control" && Path is "relay" or "local" or "legacy-ha"
        && ActorKind is "person-client" or "agent" or "ha-component"
        && ReasonCode is "none" or "refused" or "failed" or "unconfirmed" or "busy" or "cancelled"
        && (Phase == "control" ? target == "profile" && (action is "gate.start" or "gate.pause" or "gate.reset" or "gate.seal")
            && (outcome is "prepared" or "paused" or "reset" or "sealed" or "invalidated")
            : Incarnation != Guid.Empty && Guid.TryParse(target, out var id) && id != Guid.Empty && target == id.ToString()
            && GateAuditProjection.SuccessFor(action) is { } success
            && (outcome == success || outcome is "attempted" or "reserved" or "refused" or "failed" or "unconfirmed")
            && (action != "session.answer" || Question is { } question && question != Guid.Empty));
}

internal sealed record GateAuditTotal(string Action, string Path, string ActorKind, int Successes);
internal sealed record GateAuditReport(IReadOnlyList<GateAuditTotal> Totals, IReadOnlyList<string> IncompleteReasons, bool Invalidated);

internal static class GateAuditProjection
{
    internal static string? SuccessFor(string action) => action switch
    {
        "session.launch" => "started", "session.answer" => "delivered", "session.stop" => "ended", _ => null
    };

    internal static (List<AuditCommands.Row> Rows, bool Complete) Load(string profile)
    {
        profile = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(profile));
        var rows = new List<AuditCommands.Row>();
        var error = new StringWriter();
        var code = AuditCommands.Read(["audit", "--profile", System.IO.Path.GetFileName(profile)],
            System.IO.Path.GetDirectoryName(profile)!, TextWriter.Null, error, rows.Add);
        return (rows, code == 0 && error.GetStringBuilder().Length == 0);
    }

    internal static GateAuditReport Read(string profile, Guid run)
    {
        var (rows, complete) = Load(profile);
        var reasons = new HashSet<string>();
        if (!complete) { reasons.Add("audit-incomplete"); }
        var sequences = rows.Where(r => r.Gate is not null).Select(r => r.Gate!.Sequence).Order().ToArray();
        if (sequences.Where((s, i) => s != i + 1L).Any()) { reasons.Add("sequence-break"); }
        var selected = rows.Where(r => r.Gate is { } g && g.Run == run && g.Phase != "control").OrderBy(r => r.Gate!.Sequence).ToArray();
        if (selected.Length == 0) { reasons.Add("missing-run-evidence"); }
        var totals = new Dictionary<(string Action, string Path, string ActorKind), int>();
        var identities = new HashSet<(string Action, string? Target, Guid Incarnation, Guid? Question)>();
        var invalidated = false;
        foreach (var operation in selected.GroupBy(r => r.Gate!.Operation))
        {
            var first = operation.First();
            var gate = first.Gate!;
            var key = (first.Action, gate.Path, gate.ActorKind);
            totals.TryAdd(key, 0);
            if (!identities.Add((first.Action, first.Target, gate.Incarnation, gate.Question))) { invalidated = true; continue; }
            var terminals = operation.Where(r => r.Gate!.Phase == "terminal").ToArray();
            if (operation.Any(r => r.Action != first.Action || r.Target != first.Target || r.Gate is not { } other || other.Path != gate.Path
                || other.ActorKind != gate.ActorKind || other.Incarnation != gate.Incarnation || other.Question != gate.Question)
                || rows.Any(r => r.Gate is { } other && other.Operation == gate.Operation && other.Run != run)
                || terminals.Select(r => (r.Outcome, r.Gate!.ReasonCode)).Distinct().Count() > 1)
            { invalidated = true; continue; }
            if (gate.Phase != "intent" || terminals.Length == 0 || terminals[0].Outcome is "attempted" or "reserved" or "unconfirmed")
            { reasons.Add("unresolved-intent"); continue; }
            if (terminals[0].Outcome == SuccessFor(first.Action)) { totals[key]++; }
        }
        return new(totals.Select(p => new GateAuditTotal(p.Key.Action, p.Key.Path, p.Key.ActorKind, p.Value)).ToArray(),
            reasons.Order(StringComparer.Ordinal).ToArray(), invalidated);
    }
}
