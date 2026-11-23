namespace AgentBridge.Host;

// The existing fleet message recorder's counts for the run (docs/design/ha-gate-counters.md section 6).
// Supplied by the message adapter (slice 7b); never by a client.
internal sealed record GateMessageEvidence(int RelayDelivered, int HaFallbacks);

internal enum GateVerdictKind { Pass = 0, Fail = 1, Incomplete = 2 }

// Slice S4 (windowed) of docs/design/ha-gate-counters.md (#1401, #1396): the reducer from one host's audit
// window projection (GateAuditProjection.ReadWindow) to PASS, FAIL or INCOMPLETE. Exit codes: PASS 0, FAIL 1, INCOMPLETE 2.
// Faults win over missing evidence and thresholds. Only relay-path successes earn credit; local
// successes are display only and HA successes are fallbacks, never credit.
internal sealed record GateVerdict(GateVerdictKind Kind, IReadOnlyList<string> Reasons, IReadOnlyDictionary<string, int> Relay)
{
    const int Messages = 500;
    static readonly (string Action, string Name, int Needed)[] Units =
        [("session.launch", "launch", 50), ("session.answer", "approval", 30), ("session.stop", "stop", 20)];

    internal int ExitCode => (int)Kind;

    internal static GateVerdict Evaluate(GateAuditReport audit, GateMessageEvidence? messages)
    {
        var relay = Units.ToDictionary(u => u.Action, u => audit.Totals.Where(t => t.Action == u.Action && t.Path == "relay").Sum(t => t.Successes));
        var faults = new List<string>();
        if (audit.Invalidated) { faults.Add("invalidated-run"); }
        var ha = Math.Max(audit.LegacyHaDispatches, audit.Totals.Where(t => t.Path == "legacy-ha").Sum(t => t.Successes));
        if (ha > 0) { faults.Add($"ha-fallback: {ha} legacy-ha dispatches"); }
        if (messages is { HaFallbacks: > 0 }) { faults.Add($"ha-fallback: {messages.HaFallbacks} message recorder fallbacks"); }
        if (faults.Count > 0) { return new(GateVerdictKind.Fail, faults, relay); }

        var missing = audit.IncompleteReasons.ToList();
        if (messages is null) { missing.Add("missing-message-evidence"); }
        foreach (var (action, name, needed) in Units)
        {
            if (relay[action] < needed) { missing.Add($"{name} {relay[action]} < {needed}"); }
        }
        if (messages is not null && messages.RelayDelivered < Messages) { missing.Add($"message {messages.RelayDelivered} < {Messages}"); }
        return new(missing.Count > 0 ? GateVerdictKind.Incomplete : GateVerdictKind.Pass, missing, relay);
    }
}
