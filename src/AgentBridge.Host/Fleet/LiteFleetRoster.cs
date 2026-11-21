using AgentBridge.Host.Lite;
using AgentBridge.Transport;

namespace AgentBridge.Host.Fleet;

// Routing metadata only. The actual send still checks the peer's channel key and signed envelope.
internal sealed record LiteFleetConnection(string Account, IReadOnlyList<PresenceEntry> Presence,
    Func<CancellationToken, Task<FleetRosterSnapshot>> ReadAsync, Func<bool> Current);

internal sealed class LiteFleetRoster(Func<LiteFleetConnection?> connection, ILiteMode mode, Func<string?> identityAccount) : IScopedFleetRoster
{
    public const string Trust = "own-account";

    public async Task<IReadOnlyList<FleetMachine>> ReadAsync(CancellationToken ct) => (await ReadScopedAsync(ct)).Machines;

    public async Task<FleetRosterSnapshot> ReadScopedAsync(CancellationToken ct)
    {
        CheckOn();
        var captured = connection() ?? throw new InvalidOperationException("refused: notSameAccount: no signed-in relay connection");
        CheckAccount(captured);
        FleetRosterSnapshot snapshot;
        try { snapshot = await captured.ReadAsync(ct); }
        finally { CheckOn(); CheckAccount(captured); }
        if (snapshot.FleetScope != captured.Account)
        {
            throw new InvalidOperationException("refused: notSameAccount: the fleet connection changed account during lookup");
        }
        var hosts = captured.Presence.Where(p => p.Kind == "host").Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        var result = snapshot with
        {
            Machines = snapshot.Machines.Select(m => m with
            {
                Online = m.Online && hosts.Contains(m.Id),
                Verified = false,
                MessagingTrust = m.Online && hosts.Contains(m.Id) ? Trust : null,
            }).ToList(),
        };
        CheckOn();
        CheckAccount(captured);
        return result;
    }

    void CheckAccount(LiteFleetConnection captured)
    {
        if (string.IsNullOrWhiteSpace(captured.Account) || identityAccount() != captured.Account || !captured.Current())
        {
            throw new InvalidOperationException("refused: notSameAccount: the host account or fleet connection changed during lookup");
        }
    }

    void CheckOn()
    {
        if (!mode.On) { throw new InvalidOperationException("refused: off: own-account messaging is off or killed"); }
    }
}
