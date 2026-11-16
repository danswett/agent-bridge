namespace AgentBridge.Host;

internal sealed record PhoneClientEvidence(string Client, int Asked, int Answered, int AnsweredAway);

internal sealed record PhoneApprovalEvidenceReport(IReadOnlyList<PhoneClientEvidence> Clients, IReadOnlyList<string> Missing, bool Complete)
{
    internal bool Passed => true;
}

// Recorder stub: the red-only head. It reports every check as passing so the tests that follow fail first.
internal static class PhoneApprovalEvidence
{
    internal static PhoneApprovalEvidenceReport Project(IReadOnlyList<AuditCommands.Row> rows, bool complete, IReadOnlyCollection<string> expectedClients) =>
        new([], [], complete);
}
