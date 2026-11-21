using AgentBridge.Host.Lite;

namespace AgentBridge.Host.Fleet;

// Routing metadata only. The actual send still checks the peer's channel key and signed envelope.
internal sealed class LiteFleetRoster(IScopedFleetRoster roster, ILiteMode mode, ILiteLink link) : IScopedFleetRoster
{
    public const string Trust = "own-account";

    public async Task<IReadOnlyList<FleetMachine>> ReadAsync(CancellationToken ct) => (await ReadScopedAsync(ct)).Machines;

    public async Task<FleetRosterSnapshot> ReadScopedAsync(CancellationToken ct)
    {
        CheckOn();
        var account = link.Account;
        if (string.IsNullOrWhiteSpace(account)) { throw new InvalidOperationException("refused: notSameAccount: no signed-in relay account"); }
        var snapshot = await roster.ReadScopedAsync(ct);
        CheckOn();
        if (snapshot.FleetScope != account || link.Account != account)
        {
            throw new InvalidOperationException("refused: notSameAccount: the fleet connection changed account during lookup");
        }
        var hosts = link.Presence.Where(p => p.Kind == "host").Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        return snapshot with
        {
            Machines = snapshot.Machines.Select(m => m with
            {
                Online = m.Online && hosts.Contains(m.Id),
                Verified = false,
                MessagingTrust = m.Online && hosts.Contains(m.Id) ? Trust : null,
            }).ToList(),
        };
    }

    void CheckOn()
    {
        if (!mode.On) { throw new InvalidOperationException("refused: off: own-account messaging is off or killed"); }
    }
}
