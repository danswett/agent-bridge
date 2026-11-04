using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentBridge.Protocol;
using AgentBridge.Transport;

namespace AgentBridge.Host;

public sealed class RemoteAuthorization : IDisposable
{
    static readonly HashSet<string> Reads = [Ops.Hello, Ops.SessionsList, Ops.SessionGet, Ops.SessionsSubscribe, Ops.SessionsFocus];
    static readonly HashSet<string> HostOperations = [Ops.Hello, UsageProtocol.Get, TransferProtocol.Op];
    static readonly HashSet<string> PersonOperations =
    [
        .. Reads, Ops.SessionAnswer, Ops.SessionReply, Ops.SessionStop,
        Ops.AttachmentStart, Ops.AttachmentChunk, Ops.AttachmentDiscard,
        Ops.PushSubscribe, Ops.PushUnsubscribe, Ops.PushList,
        LaunchProtocol.Options, LaunchProtocol.Launch, TrustProtocol.Answer,
        UsageProtocol.Get, UsageProtocol.Subscribe,
        UpdateProtocol.Get, UpdateProtocol.Subscribe, UpdateProtocol.Check, UpdateProtocol.Install,
    ];
    static readonly HashSet<string> HostCapabilities =
        [UsageProtocol.Capability, UsageProtocol.SharedCapability, TransferProtocol.ServeCapability];

    readonly ConditionalWeakTable<SecureChannel, TrustedPeerLease> leases = new();
    readonly ITimer? timer;
    ChannelHub? hub;

    public bool Enabled { get; }
    public TrustedPeerStore Store { get; }
    public event Action? Changed;

    public RemoteAuthorization(bool enabled, TrustedPeerStore store, TimeProvider? time = null)
    {
        Enabled = enabled;
        Store = store;
        if (enabled)
        {
            timer = (time ?? TimeProvider.System).CreateTimer(_ => Revalidate(), null,
                TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(250));
        }
    }

    public void Attach(ChannelHub? next)
    {
        if (next is not null) { next.PeerAllowed = Allows; }
        Volatile.Write(ref hub, next);
        Revalidate();
    }

    public TrustedPeerLease? Lease(byte[]? key, string? account) =>
        key is null ? null : Store.Find(key, account);

    public bool Allows(SecureChannel channel)
    {
        if (!Enabled) { return true; }
        if (!leases.TryGetValue(channel, out var lease))
        {
            var current = Lease(channel.PeerStatic, channel.Relay.AccountId);
            if (current is null) { return false; }
            lease = leases.GetValue(channel, _ => current);
        }
        return Store.Current(lease, channel.Relay.AccountId);
    }

    public bool HasRole(string device, string role) => !Enabled || Store.FindDevice(device)?.Role == role;

    public bool HasRole(SecureChannel channel, string role) =>
        !Enabled || (Allows(channel) && leases.TryGetValue(channel, out var lease) && lease.Role == role);

    public bool PersonKey(string? publicKey) => !Enabled ||
        (publicKey is not null && Store.FindEncoded(publicKey, StoreAccount())?.Role == TrustedPeerStore.Person);

    string? StoreAccount() => Volatile.Read(ref hub)?.Relay.AccountId;

    public Func<SecureChannel, string, JsonElement, Task<object?>> Handler(SessionService sessions) => async (channel, op, data) =>
    {
        if (!Enabled) { return await sessions.HandleAsync(channel, op, data); }
        if (!Allows(channel) || !leases.TryGetValue(channel, out var lease)) { throw new InvalidOperationException("the peer is not authorized"); }
        var operations = lease.Role switch
        {
            TrustedPeerStore.Person => PersonOperations,
            TrustedPeerStore.Host => HostOperations,
            TrustedPeerStore.Service => Reads,
            _ => throw new InvalidOperationException("the peer role is not authorized"),
        };
        if (!operations.Contains(op)) { throw new InvalidOperationException("the peer role does not allow this operation"); }
        var result = await sessions.HandleAsync(channel, op, data);
        if (result is not HelloResult hello) { return result; }
        return hello with
        {
            Capabilities = [.. hello.Capabilities.Where(c => lease.Role switch
            {
                TrustedPeerStore.Service => c == Capabilities.SessionsRead,
                TrustedPeerStore.Host => HostCapabilities.Contains(c),
                _ => c != TransferProtocol.ServeCapability,
            })],
        };
    };

    public void Revalidate()
    {
        if (!Enabled) { return; }
        Volatile.Read(ref hub)?.RevalidatePeers();
        Changed?.Invoke();
    }

    public object Manage(string op, JsonElement data)
    {
        try
        {
            switch (op)
            {
                case "host.peers.status":
                    var pins = Store.Pins;
                    return new { enabled = Enabled, problem = Store.Problem, pins };
                case "host.peers.reset":
                    Store.Reset();
                    return new { reset = true };
                case "host.peers.put":
                    var key = ReadKey(data);
                    if (!data.TryGetProperty("role", out var roleValue) || roleValue.ValueKind != JsonValueKind.String
                        || roleValue.GetString() is not { } role) { throw new ArgumentException("a peer role is required"); }
                    if (!data.TryGetProperty("expiresAt", out var expiryValue) || expiryValue.ValueKind != JsonValueKind.String
                        || !expiryValue.TryGetDateTime(out var timestamp) || timestamp.Kind == DateTimeKind.Unspecified
                        || !expiryValue.TryGetDateTimeOffset(out var expiry)) { throw new ArgumentException("a valid expiry timestamp is required"); }
                    return Store.Put(key, role, expiry);
                case "host.peers.revoke":
                    Store.Revoke(ReadKey(data));
                    return new { revoked = true };
                default:
                    throw new ArgumentException("unknown trusted-peer management operation");
            }
        }
        finally { Revalidate(); }
    }

    static byte[] ReadKey(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("publicKey", out var encoded)
            || encoded.ValueKind != JsonValueKind.String || BlobFormat.FromBase64Url(encoded.GetString()) is not { } key)
        {
            throw new ArgumentException("a full public key is required");
        }
        return key;
    }

    public void Dispose() => timer?.Dispose();
}
