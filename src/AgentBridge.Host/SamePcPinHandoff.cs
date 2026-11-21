using System.Text;
using System.Text.Json;
using AgentBridge.Protocol;
using AgentBridge.Roster;
using AgentBridge.Transport;

namespace AgentBridge.Host;

// Same-PC trust (#3450 ruling item 3): the signed-in app on this machine hands over the roster
// root its own verified roster holds. The relay only supplies the chain; the app's genesis selects
// it, its heads must be held in it, its device must be an active person device, and the one pin
// writer does the rest. LocalApi reaches this from the app on the user-only pipe, nothing else.
public sealed class SamePcPinHandoff
{
    readonly string pinPath;
    readonly Func<string?> account;
    readonly Func<CancellationToken, Task<RelayRoster?>> fetch;
    readonly HostRosterReader reader;
    readonly Func<TimeSpan> clock;
    readonly Func<byte[], bool>? isCapSignGenesis;
    internal object AccountGate { get; } = new();
    long accountVersion;

    public SamePcPinHandoff(string profileFolder, Func<string?> account, Func<CancellationToken, Task<RelayRoster?>> fetch,
        HostRosterReader reader, Func<TimeSpan> clock, Func<byte[], bool>? isCapSignGenesis = null) =>
        (pinPath, this.account, this.fetch, this.reader, this.clock, this.isCapSignGenesis) =
            (HostRosterPinWriter.PinPath(profileFolder), account, fetch, reader, clock, isCapSignGenesis);

    public async Task<SamePcPinResult> HandleAsync(JsonElement data, CancellationToken ct) => new(await StateAsync(data, ct));

    internal void AccountChanged(Action change)
    {
        lock (AccountGate) { accountVersion++; change(); }
    }

    async Task<string> StateAsync(JsonElement data, CancellationToken ct)
    {
        if (Parse(data) is not { } request) { return "invalid-request"; }
        string own;
        long version;
        lock (AccountGate)
        {
            if (account() is not { Length: > 0 } current) { return "no-account"; }
            if (request.Account != current) { return "account-mismatch"; }
            if (File.Exists(pinPath)) { return "pin-exists"; }
            (own, version) = (current, accountVersion);
        }
        RelayRoster? relay;
        try { relay = await fetch(ct); }
        catch (Exception e) when (e is not OperationCanceledException) { return "relay-unreachable"; }
        if (relay is null || relay.Entries.Count == 0) { return "no-roster"; }
        if (relay.Generation != request.Generation) { return "generation-mismatch"; }

        var verifier = new RosterVerifier(own, request.Generation, clock);
        try
        {
            if (verifier.Offer(relay.Entries.Select(t => Entry.Parse(Encoding.UTF8.GetBytes(t)))).Any(o => o.Verdict == Verdict.Refused))
            { return "chain-invalid"; }
        }
        catch (RosterFormatException) { return "chain-invalid"; }
        if (verifier.Graph.Count == 0 || !verifier.Graph[0].Hash.AsSpan().SequenceEqual(request.GenesisHash)) { return "genesis-mismatch"; }
        if (!request.HeadHashes.All(verifier.Holds)) { return "heads-missing"; }
        if (verifier.State.Device(request.Device)?.IsPerson != true) { return "not-person-device"; }

        lock (AccountGate)
        {
            if (version != accountVersion || account() != own) { return "account-mismatch"; }
            ct.ThrowIfCancellationRequested();
            var state = HostRosterPinWriter.Write(pinPath, new PersonGenesis("same-pc-app", request.GenesisHash), relay, own, clock, isCapSignGenesis);
            return state == SamePcPinProtocol.Pinned && reader.Update(relay, own).Outcome != "verified" ? "pin-readback" : state;
        }
    }

    sealed record Request(string Account, string Generation, byte[] GenesisHash, byte[][] HeadHashes, string Device);

    // Exactly the five fields, each well formed; anything else is refused rather than guessed at.
    static Request? Parse(JsonElement data)
    {
        try
        {
            if (data.ValueKind != JsonValueKind.Object || data.GetRawText().Length > 4096) { return null; }
            var names = data.EnumerateObject().Select(p => p.Name).ToArray();
            if (names.Length != 5 || !names.ToHashSet(StringComparer.Ordinal).SetEquals(["account", "generation", "genesis", "heads", "device"])) { return null; }
            var r = data.Deserialize<SamePcPinRequest>(ProtocolJson.Options);
            if (r is null || string.IsNullOrEmpty(r.Account) || r.Generation is null || !Codec.IsId(r.Generation)
                || r.Genesis is null || string.IsNullOrEmpty(r.Device) || r.Device.Length > 128
                || r.Heads is not { Length: >= 1 and <= RosterVerifier.MaxHeads } || r.Heads.Any(h => h is null)) { return null; }
            return new(r.Account, r.Generation, Codec.FromBase64Url(r.Genesis, 32, "genesis"),
                [.. r.Heads.Select(h => Codec.FromBase64Url(h, 32, "head"))], r.Device);
        }
        catch (Exception e) when (e is JsonException or FormatException or RosterFormatException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }
}
