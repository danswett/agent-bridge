namespace AgentBridge.Host;

internal sealed record GateAuditTotal(string Action, string Path, string ActorKind, int Successes);
internal sealed record GateAuditReport(IReadOnlyList<GateAuditTotal> Totals, IReadOnlyList<string> IncompleteReasons, bool Invalidated);

internal static class GateAuditProjection
{
    internal static GateAuditReport? Read(string profile, Guid run) => null;
}
