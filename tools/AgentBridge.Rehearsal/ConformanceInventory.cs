namespace AgentBridge.Rehearsal;

internal enum InventoryDisposition { Required, HostOnly, FleetOnly, IntentionalDrop, OutsideCohort, ResolvedInventory, OutOfScope }
internal enum InventoryClientDisposition { Required, Parked, Unshipped, Optional }
internal sealed record ConformanceInventoryRow
{
    public int Row { get; }
    public ConformanceCapability Capability { get; }
    public InventoryDisposition Disposition { get; }
    public string? Decision { get; }

    public ConformanceInventoryRow(int row, ConformanceCapability capability,
        InventoryDisposition disposition = InventoryDisposition.Required, string? decision = null)
    {
        if (!Enum.IsDefined(disposition))
            throw new ArgumentException("Unknown inventory disposition.", nameof(disposition));
        if (disposition != InventoryDisposition.Required && !ConformanceInventory.IsApprovedDecision(decision))
            throw new ArgumentException("Every excluded row needs its recorded approved decision.", nameof(decision));
        Row = row; Capability = capability; Disposition = disposition; Decision = decision;
    }
}
internal sealed record ConformanceInventoryClient(string Name, InventoryClientDisposition Disposition, string? Decision = null);

internal static class ConformanceInventory
{
    // Inventory: slipgates-fleet/docs/ha-retirement-parity.md at d3ce9998c6640bd21c54e58330410d3f749a2987.
    // #1396's later decisions supersede its old gate wording; this catalog awards no evidence.
    const string MappingDecision = "https://github.com/danswett/agent-bridge/issues/1407#issuecomment-6791949799";
    const string AppleHostsDecision = "https://github.com/danswett/agent-bridge/issues/1396#issuecomment-6791450599";
    const string OwnerInventoryDecision = "https://github.com/danswett/agent-bridge/issues/1396#issuecomment-6790737464";
    const string AppleClientsDecision = "https://github.com/danswett/agent-bridge/issues/696#issuecomment-6673554321";
    const string OptionalHaDecision = "https://github.com/danswett/agent-bridge/issues/1396#issuecomment-6790674187";

    public static IReadOnlyList<ConformanceInventoryRow> Rows { get; } = Array.AsReadOnly<ConformanceInventoryRow>(
    [
        R(1, "reply"),
        R(2, "machine-presence"),
        R(3, "fleet-sessions-and-name-lookup"),
        R(4, "session-stop"),
        R(5, "questions-and-approvals"),
        R(6, "session-launch"),
        R(7, "launch-options-and-result"),
        R(8, "updates"),
        R(9, "usage"),
        R(10, "detailed-activity-toggle", InventoryDisposition.IntentionalDrop, MappingDecision),
        R(11, "pairing-and-transfer"),
        R(12, "credentials-and-audit"),
        R(13, "apple-host-cohort", InventoryDisposition.OutsideCohort, AppleHostsDecision),
        R(14, "other-ha-dependencies", InventoryDisposition.ResolvedInventory, OwnerInventoryDecision),
        R(15, "forget-machine"),
        R(16, "turn-and-question-notifications"),
        R(17, "multi-field-questions"),
        R(18, "agent-list-read-and-ask"),
        R(19, "agent-attribution"),
        R(20, "agent-credential-distribution", InventoryDisposition.HostOnly, MappingDecision),
        R(21, "legacy-daemon-migration", InventoryDisposition.HostOnly, MappingDecision),
        R(22, "launch-helper-and-ledger"),
        R(23, "dispatcher-launch", InventoryDisposition.FleetOnly, MappingDecision),
        R(24, "live-board-roster"),
        R(25, "pump-wake", InventoryDisposition.FleetOnly, MappingDecision),
        R(26, "messenger-lookup-and-send", InventoryDisposition.FleetOnly, MappingDecision),
        R(27, "dashboard-and-cards"),
        R(28, "notification-actions-and-withdrawal"),
        R(29, "reply-attachments"),
        R(30, "cross-machine-session-list"),
        R(31, "ha-helper-liveness", InventoryDisposition.IntentionalDrop, MappingDecision),
        R(32, "owner-built-ha-inventory", InventoryDisposition.ResolvedInventory, OwnerInventoryDecision),
        R(33, "non-fleet-home-automation", InventoryDisposition.OutOfScope, OwnerInventoryDecision),
        R(34, "agent-ha-token-migration", InventoryDisposition.HostOnly, MappingDecision)
    ]);

    public static IReadOnlyList<ConformanceInventoryClient> Clients { get; } = Array.AsReadOnly<ConformanceInventoryClient>(
    [
        new("PWA", InventoryClientDisposition.Required),
        new("Windows", InventoryClientDisposition.Required),
        new("Mac", InventoryClientDisposition.Parked, AppleClientsDecision),
        new("iOS", InventoryClientDisposition.Parked, AppleClientsDecision),
        new("Android", InventoryClientDisposition.Unshipped, MappingDecision),
        new("Linux", InventoryClientDisposition.Unshipped, MappingDecision),
        new("HA", InventoryClientDisposition.Optional, OptionalHaDecision),
        new("ThirdParty", InventoryClientDisposition.Parked, MappingDecision)
    ]);

    public static IReadOnlyList<ConformanceCapability> ForClient(string client)
    {
        if (!Clients.Any(c => c.Name == client))
            throw new ArgumentException("Unknown inventory client.", nameof(client));
        // Client scheduling is metadata, not permission to hide gaps when that client is selected.
        return Rows.Where(r => r.Disposition == InventoryDisposition.Required).Select(r => r.Capability).ToArray();
    }

    internal static bool IsApprovedDecision(string? decision)
        => decision is MappingDecision or AppleHostsDecision or OwnerInventoryDecision;

    static ConformanceInventoryRow R(int row, string name, InventoryDisposition disposition = InventoryDisposition.Required,
        string? decision = null) => new(row, new(name,
            disposition is InventoryDisposition.Required or InventoryDisposition.HostOnly or InventoryDisposition.FleetOnly),
            disposition, decision);
}
