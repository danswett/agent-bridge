namespace AgentBridge.Host;

// Asked: answers this client tried; Answered: those the host delivered; AnsweredAway: delivered ones that
// came through the relay, which is what a phone away from home uses.
internal sealed record PhoneClientEvidence(string Client, int Asked, int Answered, int AnsweredAway);

internal sealed record PhoneApprovalEvidenceReport(IReadOnlyList<PhoneClientEvidence> Clients, IReadOnlyList<string> Missing, bool Complete)
{
    // A missing client, or an audit log that may be missing rows, is never evidence.
    internal bool Passed => Complete && Missing.Count == 0;
}

// Reads the gate audit log for per-client evidence that approvals are answered from each phone (#2649).
// The audit log records the answer, not the push notice that preceded it, so this shows a client can
// answer, not that its notice arrived. It only reads: no audit writer or authorization path changes.
internal static class PhoneApprovalEvidence
{
    internal static PhoneApprovalEvidenceReport Read(string profile, IReadOnlyCollection<string> expectedClients)
    {
        var (rows, complete) = GateAuditProjection.Load(profile);
        return Project(rows, complete, expectedClients);
    }

    internal static PhoneApprovalEvidenceReport Project(IReadOnlyList<AuditCommands.Row> rows, bool complete, IReadOnlyCollection<string> expectedClients)
    {
        var perClient = rows
            .Where(r => r.Action == "session.answer" && r.Gate is { ActorKind: "person-client", Phase: not "control" }
                && r.Principal.StartsWith("person:", StringComparison.Ordinal) && r.Principal != "person:local")
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
}
