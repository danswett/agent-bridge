namespace AgentBridge.Host;

// Asked: answers this client tried; Answered: those the host delivered; AnsweredAway: delivered ones that
// came through the relay, which is what a phone away from home uses.
internal sealed record PhoneClientEvidence(string Client, int Asked, int Answered, int AnsweredAway);

// Unattributed (per-client reads only): operations whose rows do not all carry the same host-derived client. They are never assigned to a client,
// and any of them makes the evidence incomplete for every client.
internal sealed record PhoneApprovalEvidenceReport(IReadOnlyList<PhoneClientEvidence> Clients, IReadOnlyList<string> Missing, bool Complete, int Unattributed = 0)
{
    // A missing client, or an audit log that may be missing rows, is never evidence.
    internal bool Passed => Complete && Missing.Count == 0;
}

// Reads the gate audit log for per-client evidence that approvals are answered from each phone (#2649).
// The audit log records the answer, not the push notice that preceded it, so this shows a client can
// answer, not that its notice arrived. It only reads: no audit writer or authorization path changes.
// Today no writer emits the person:<device> relay answers it counts (the host audits answers as person:local),
// so against real logs it reports every expected client missing until that writer exists (#2723).
internal static class PhoneApprovalEvidence
{
    internal static PhoneApprovalEvidenceReport Read(string profile, IReadOnlyCollection<string> expectedClients)
    {
        var coverage = false;
        var adopted = false;
        long retained = 0;
        var (rows, complete) = GateAuditProjection.Load(profile, fault => coverage |= fault == "coverage-break",
            boundary: (floor, unproven) => { retained = floor; adopted = unproven; });
        return Project(rows, complete, expectedClients, adopted, coverage, retained);
    }

    // The same soundness checks as GateAuditProjection.Read: an adopted, trimmed or coverage-broken log, or one
    // with a repeated or missing sequence, may lack rows, so it is never complete. Each contributing run also goes
    // through the real operation-coherence validation, so an operation with conflicting terminals or a changed
    // path, target, incarnation or question is invalidated rather than counted.
    internal static PhoneApprovalEvidenceReport Project(IReadOnlyList<AuditCommands.Row> rows, bool complete, IReadOnlyCollection<string> expectedClients,
        bool adopted = false, bool coverage = false, long retainedThrough = 0)
    {
        var (answers, sound) = Sound(rows, complete, adopted, coverage, retainedThrough);
        complete = sound;
        var perClient = answers
            .GroupBy(r => r.Principal["person:".Length..])
            .ToDictionary(g => g.Key, g => g.GroupBy(r => r.Gate!.Operation).Select(op =>
            {
                var delivered = op.Any(r => r.Gate!.Phase == "terminal" && r.Outcome == "delivered");
                return (Delivered: delivered, Away: delivered && op.First().Gate!.Path == "relay");
            }).ToArray());
        var clients = perClient.Keys.Union(expectedClients, StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Select(c => perClient.TryGetValue(c, out var ops)
                ? new PhoneClientEvidence(c, ops.Length, ops.Count(o => o.Delivered), ops.Count(o => o.Away))
                : new PhoneClientEvidence(c, 0, 0, 0))
            .ToArray();
        var missing = expectedClients.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Where(c => clients.Single(x => x.Client == c).AnsweredAway == 0).ToArray();
        return new(clients, missing, complete);
    }

    // The same evidence grouped by the host-derived client label ("pwa/ios"), not by device (ha-audit-client-field.md). An operation whose rows
    // do not all carry one client is unattributed: it is counted, never guessed, and makes the evidence incomplete for every client.
    internal static PhoneApprovalEvidenceReport ProjectByClient(IReadOnlyList<AuditCommands.Row> rows, bool complete, IReadOnlyCollection<string> expectedClients,
        bool adopted = false, bool coverage = false, long retainedThrough = 0)
    {
        var (answers, sound) = Sound(rows, complete, adopted, coverage, retainedThrough);
        var ops = answers.GroupBy(r => r.Gate!.Operation).Select(op =>
        {
            var labels = op.Select(r => r.Gate!.ClientKind is { } kind && r.Gate.ClientPlatform is { } platform ? $"{kind}/{platform}" : null).Distinct().ToArray();
            var delivered = op.Any(r => r.Gate!.Phase == "terminal" && r.Outcome == "delivered");
            return (Label: labels.Length == 1 ? labels[0] : null, Delivered: delivered, Away: delivered && op.First().Gate!.Path == "relay");
        }).ToArray();
        var perClient = ops.Where(o => o.Label is not null).GroupBy(o => o.Label!).ToDictionary(g => g.Key, g => g.ToArray());
        var clients = perClient.Keys.Union(expectedClients, StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Select(c => perClient.TryGetValue(c, out var list)
                ? new PhoneClientEvidence(c, list.Length, list.Count(o => o.Delivered), list.Count(o => o.Away))
                : new PhoneClientEvidence(c, 0, 0, 0))
            .ToArray();
        var missing = expectedClients.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Where(c => clients.Single(x => x.Client == c).AnsweredAway == 0).ToArray();
        var unattributed = ops.Count(o => o.Label is null);
        return new(clients, missing, sound && unattributed == 0, unattributed);
    }

    // The relay person answers, and whether the log is sound: an adopted, trimmed or coverage-broken log, or one whose rows fail operation
    // coherence or sequence, may lack or have changed rows (shared by the per-device and per-client reads).
    static (AuditCommands.Row[] Answers, bool Sound) Sound(IReadOnlyList<AuditCommands.Row> rows, bool complete, bool adopted, bool coverage, long retainedThrough)
    {
        var answers = rows
            .Where(r => r.Action == "session.answer" && r.Gate is { ActorKind: "person-client", Phase: not "control" }
                && r.Principal.StartsWith("person:", StringComparison.Ordinal) && r.Principal != "person:local")
            .ToArray();
        var unsound = false;
        foreach (var run in answers.Select(r => r.Gate!.Run).Distinct().DefaultIfEmpty(Guid.NewGuid()))
        {
            var check = GateAuditProjection.Project(rows, complete, run, retainedThrough: retainedThrough);
            unsound |= check.Invalidated || check.IncompleteReasons.Contains("sequence-break");
        }
        return (answers, complete && !adopted && !coverage && retainedThrough == 0 && !unsound);
    }
}
