namespace AgentBridge.Rehearsal;

internal enum InventoryDisposition { Required, HostOnly, FleetOnly, IntentionalDrop, OutsideCohort, ResolvedInventory, OutOfScope }
internal enum InventoryClientDisposition { Required, Parked, Unshipped, Optional }
internal sealed record ConformanceInventoryRow(int Row, ConformanceCapability Capability,
    InventoryDisposition Disposition = InventoryDisposition.Required, string? Decision = null);
internal sealed record ConformanceInventoryClient(string Name, InventoryClientDisposition Disposition, string? Decision = null);

internal static class ConformanceInventory
{
    public static IReadOnlyList<ConformanceInventoryRow> Rows { get; } = [];
    public static IReadOnlyList<ConformanceInventoryClient> Clients { get; } = [];

    public static IReadOnlyList<ConformanceCapability> ForClient(string client,
        IReadOnlyList<ConformanceInventoryRow>? inventory = null) => [];
}
