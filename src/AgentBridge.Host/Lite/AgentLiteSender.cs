using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentBridge.AgentEnvelope;
using Codec = AgentBridge.Roster.Codec;
using AgentBridge.Transport;

namespace AgentBridge.Host.Lite;

// The relay raised Undeliverable, or the peer could not be reached at all: nothing was sent.
public sealed class LitePeerOfflineException(string message) : Exception(message);

// What the sender needs of the relay link: who this host is, who is present, and one request to a peer.
// The real one is RelayLiteLink; tests use a fake channel. SendAsync must honour `ct`: the 10 s limit and
// a mode switched off both arrive as a cancelled token.
public interface ILiteLink
{
    string Self { get; }
    string Account { get; }
    IReadOnlyList<PresenceEntry> Presence { get; }
    Task<LiteAck> SendAsync(string peerId, string account, object request, CancellationToken ct);
}

// AP5-lite L3 (#728, #1351): sends a signed own-agent envelope to a present host of the same account (no pin) and reports
// one typed outcome. Never resends, and never reports delivered without the receiver's acknowledgement.
public sealed class AgentLiteSender(
    ILiteMode mode, ILiteLink link, Func<AgentCredential, bool> credentialLive, Func<(IEnvelopeSigner Sign, byte[] Point)?> signer,
    byte[] staticPublic, TimeProvider? time = null, TimeSpan? ackTimeout = null, TimeSpan? watchEvery = null) : ILiteSender, ILiteReceipts
{
    public const string OfflineText = "that machine is offline; nothing was queued";
    public const string NoCredential = "credential", NoSigner = "no-sign-key", NotPeer = LiteReceiver.NotPeer, Malformed = LiteReceiver.Malformed, BadMachine = "badMachine", NoSuchMachine = "noSuchMachine";
    public const int MaxReceiptRecords = 64;
    public const string TimedOut = "timeout", ModeSwitchedOff = "mode switched off", ChannelLost = "channel lost", BadAck = "unreadable acknowledgement";

    readonly TimeProvider time = time ?? TimeProvider.System;
    readonly string machine = Wire.DeviceId(staticPublic);
    readonly TimeSpan ackTimeout = ackTimeout ?? TimeSpan.FromSeconds(10);
    readonly TimeSpan watchEvery = watchEvery ?? TimeSpan.FromMilliseconds(500);
    readonly string instance = Codec.Base64Url(RandomNumberGenerator.GetBytes(12));
    static readonly string Zero12 = Codec.Base64Url(new byte[12]);

    // What a later receipt lookup needs (#3875): the request id and target of an unconfirmed send while its envelope could still arrive; no text.
    sealed record Sent(string Request, string Machine, string Session, DateTimeOffset Until);
    readonly object recordsGate = new();
    readonly Dictionary<string, List<Sent>> records = new(StringComparer.Ordinal);

    public async Task<LiteOutcome> SendAsync(AgentCredential caller, LiteSendInput input, CancellationToken ct)
    {
        var outcome = await SendCoreAsync(caller, input, ct);
        if (outcome.Kind == LiteKind.Unconfirmed && outcome.Request.Length > 0) { Remember(caller, outcome.Request, input); }
        return outcome;
    }

    void Remember(AgentCredential caller, string request, LiteSendInput input)
    {
        lock (recordsGate)
        {
            var now = time.GetUtcNow();
            if (!records.TryGetValue(caller.Id, out var list)) { records[caller.Id] = list = []; }
            list.RemoveAll(s => s.Until <= now);
            list.Add(new Sent(request, input.Machine, input.Session, now + TimeSpan.FromSeconds(EnvelopeBody.MaxLifetimeSeconds)));
            while (list.Count > MaxReceiptRecords) { list.RemoveAt(0); }
        }
    }

    Sent? Recorded(AgentCredential caller, string request)
    {
        lock (recordsGate)
        {
            var now = time.GetUtcNow();
            return records.TryGetValue(caller.Id, out var list) ? list.FirstOrDefault(s => s.Request == request && s.Until > now) : null;
        }
    }

    async Task<LiteOutcome> SendCoreAsync(AgentCredential caller, LiteSendInput input, CancellationToken ct)
    {
        if (!mode.On) { return new LiteOutcome(LiteKind.Off, "off", ""); }
        var request = Guid.NewGuid().ToString("D");
        LiteOutcome Out(LiteKind kind, string? code) => new(kind, code, request);

        if (string.IsNullOrEmpty(input.Text) || input.Text.Length > LiteReceiver.MaxText || string.IsNullOrEmpty(input.Session)) { return Out(LiteKind.Refused, Malformed); }
        var data = Encoding.UTF8.GetBytes(input.Text);
        // Checked afresh, before anything is signed: a credential revoked or expired since the pipe call signs nothing.
        var script = caller.Kind == "script";
        if ((script ? !AgentCredentials.ValidScript(caller, machine)
            : caller.Kind != "session" || string.IsNullOrEmpty(caller.Session) || caller.Principal is not null)
            || !credentialLive(caller)) { return Out(LiteKind.Refused, NoCredential); }

        // Presence comes from the relay; names are display only and cannot retarget an absent device ID.
        // One account is read here and held through signing and the opened channel, so a sign-in meanwhile sends nothing.
        var account = link.Account;
        if (!Wire.IsDeviceId(input.Machine)) { return Out(LiteKind.Refused, MachineRefusal(input.Machine, BadMachine, "must be an exact device ID; names never route")); }
        var ids = input.Machine == link.Self ? [link.Self]
            : link.Presence.Where(p => p.Kind == "host" && p.Id == input.Machine).Select(p => p.Id).Distinct().ToList();
        if (string.IsNullOrEmpty(account)) { return Out(LiteKind.Undeliverable, OfflineText); }
        if (ids.Count == 0) { return Out(LiteKind.Refused, MachineRefusal(input.Machine, NoSuchMachine, "is unknown or offline; nothing was queued")); }
        var peerId = ids[0];
        var signed = signer();
        // Making the key can wait up to 30 s on its file lock; messaging turned off or a caller that lapsed meanwhile signs nothing.
        if (!mode.On) { return new LiteOutcome(LiteKind.Off, "off", ""); }
        if (signed is not { } key) { return Out(LiteKind.Refused, NoSigner); }
        if (!credentialLive(caller)) { return Out(LiteKind.Refused, NoCredential); }

        var from = new LiteSender(account, link.Self, Codec.Base64Url(SHA256.HashData(staticPublic)), caller.Session, script ? "" : instance,
            Codec.Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(caller.Id))[..16]), script ? "" : "agent", script ? "" : "agent-bridge",
            script ? "script" : "agent", script ? caller.Principal : null);
        byte[] envelope;
        try { envelope = LiteProfile.Seal(from, new LiteTarget(peerId, input.Session, Zero12), request, data, time.GetUtcNow().ToUnixTimeSeconds(), key.Sign); }
        catch (EnvelopeException) { return Out(LiteKind.Refused, Malformed); }

        using var flight = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var watching = new CancellationTokenSource();
        var switchedOff = false;
        // The mode can be turned off while we wait; the receiver may already have typed the text, so that is unconfirmed.
        var watcher = Task.Run(async () =>
        {
            try
            {
                using var tick = new PeriodicTimer(watchEvery);
                while (await tick.WaitForNextTickAsync(watching.Token))
                {
                    if (!mode.On) { switchedOff = true; flight.Cancel(); return; }
                }
            }
            catch (OperationCanceledException) { }
        });
        flight.CancelAfter(ackTimeout);
        try
        {
            var ack = await link.SendAsync(peerId, account, new { envelope = Codec.Base64Url(envelope), text = input.Text, killEpoch = mode.KillEpoch, signPoint = Codec.Base64Url(key.Point) }, flight.Token);
            if (ack.KillEpoch > mode.KillEpoch) { mode.RaiseEpoch(ack.KillEpoch); }
            if (ack.Request != request) { return Out(LiteKind.Unconfirmed, BadAck); }
            return ack.Status switch
            {
                "delivered" => Out(LiteKind.Delivered, null),
                "pending" => Out(LiteKind.Pending, ack.Code),
                "queued" => Out(LiteKind.Queued, ack.Code),
                "unconfirmed" => Out(LiteKind.Unconfirmed, ack.Code),
                "refused" => Out(LiteKind.Refused, ack.Code ?? "refused"),
                _ => Out(LiteKind.Unconfirmed, BadAck),
            };
        }
        catch (LitePeerOfflineException) { return Out(LiteKind.Undeliverable, OfflineText); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return Out(LiteKind.Unconfirmed, switchedOff ? ModeSwitchedOff : TimedOut); }
        catch (TimeoutException) { return Out(LiteKind.Unconfirmed, TimedOut); }
        catch (Exception e) when (e is not OperationCanceledException) { return Out(LiteKind.Unconfirmed, ChannelLost); }
        // The watcher must be finished before the sources are disposed, or its next tick would fault unobserved.
        finally { watching.Cancel(); await watcher; }
    }

    static readonly string[] ReceiptOutcomes = ["delivered", "refused", "pending", "queued", "unconfirmed", "in-flight"];

    // Asks the target what an unconfirmed send's first attempt became, with a signed probe that never types (#3875). Null: no such record (nothing is sent).
    // Only an answered receipt can say `refused`; any failure of the lookup itself is `unknown` ("lookup: ..."), so the send stays uncertain.
    public async Task<LiteReceiptReply?> ReceiptAsync(AgentCredential caller, string request, CancellationToken ct)
    {
        if (Recorded(caller, request) is not { } sent) { return null; }
        LiteReceiptReply Unknown(string? code) => new("unknown", code, request);
        if (!mode.On) { return Unknown("lookup: off"); }
        var script = caller.Kind == "script";
        if ((script ? !AgentCredentials.ValidScript(caller, machine)
            : caller.Kind != "session" || string.IsNullOrEmpty(caller.Session) || caller.Principal is not null)
            || !credentialLive(caller)) { return Unknown("lookup: " + NoCredential); }
        var account = link.Account;
        var ids = sent.Machine == link.Self ? [link.Self]
            : link.Presence.Where(p => p.Kind == "host" && p.Id == sent.Machine).Select(p => p.Id).Distinct().ToList();
        if (string.IsNullOrEmpty(account) || ids.Count == 0) { return Unknown("lookup: offline"); }
        var peerId = ids[0];
        var signed = signer();
        if (!mode.On) { return Unknown("lookup: off"); }
        if (signed is not { } key) { return Unknown("lookup: " + NoSigner); }
        if (!credentialLive(caller)) { return Unknown("lookup: " + NoCredential); }

        var from = new LiteSender(account, link.Self, Codec.Base64Url(SHA256.HashData(staticPublic)), caller.Session, script ? "" : instance,
            Codec.Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(caller.Id))[..16]), script ? "" : "agent", script ? "" : "agent-bridge",
            script ? "script" : "agent", script ? caller.Principal : null);
        byte[] envelope;
        try { envelope = LiteProfile.SealReceipt(from, new LiteTarget(peerId, sent.Session, Zero12), request, time.GetUtcNow().ToUnixTimeSeconds(), key.Sign); }
        catch (EnvelopeException) { return Unknown("lookup: " + Malformed); }

        using var flight = CancellationTokenSource.CreateLinkedTokenSource(ct);
        flight.CancelAfter(ackTimeout);
        try
        {
            var ack = await link.SendAsync(peerId, account, new { envelope = Codec.Base64Url(envelope), text = "", killEpoch = mode.KillEpoch, signPoint = Codec.Base64Url(key.Point) }, flight.Token);
            if (ack.KillEpoch > mode.KillEpoch) { mode.RaiseEpoch(ack.KillEpoch); }
            if (ack.Request != request) { return Unknown("lookup: " + BadAck); }
            switch (ack.Status)
            {
                case "unknown": return Unknown(null);
                case "refused": return Unknown("lookup: refused:" + (ack.Code ?? "refused"));
                case "receipt" when ack.Code is { Length: > 0 } code:
                    var colon = code.IndexOf(':');
                    var outcome = colon < 0 ? code : code[..colon];
                    return ReceiptOutcomes.Contains(outcome) ? new LiteReceiptReply(outcome, colon < 0 ? null : code[(colon + 1)..], request) : Unknown("lookup: " + BadAck);
                default: return Unknown("lookup: " + BadAck);
            }
        }
        catch (LitePeerOfflineException) { return Unknown("lookup: offline"); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return Unknown("lookup: " + TimedOut); }
        catch (TimeoutException) { return Unknown("lookup: " + TimedOut); }
        catch (Exception e) when (e is not OperationCanceledException) { return Unknown("lookup: " + ChannelLost); }
    }

    internal static string MachineRefusal(string? machine, string code, string reason)
    {
        var id = machine is { Length: > 64 } ? machine[..(char.IsHighSurrogate(machine[63]) ? 63 : 64)] + "..." : machine ?? "<missing>";
        return $"refused: {code}: machine ID {JsonSerializer.Serialize(id)} {reason}";
    }

    // The host's own sender; credentials are judged by the store.
    public static AgentLiteSender ForHost(string profileFolder, ILiteMode mode, ILiteLink link, AgentCredentials? credentials, byte[] staticPublic) =>
        new(mode, link, c => credentials?.IsLive(c) == true, HostSigner(profileFolder, mode), staticPublic);

    // The agent signing key is made on first use while the mode is on (no pin or setup step), then held.
    // A damaged key file is refused, never replaced.
    public static Func<(IEnvelopeSigner Sign, byte[] Point)?> HostSigner(string profileFolder, ILiteMode mode)
    {
        AgentSignKey? key = null;
        var gate = new object();
        return () =>
        {
            lock (gate)
            {
                try { key ??= AgentSignKey.OpenOrCreate(profileFolder, mode.On); }
                catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException or InvalidOperationException or CryptographicException) { return null; }
                return (new KeySigner(key), key.PublicPoint);
            }
        };
    }

    sealed class KeySigner(AgentSignKey key) : IEnvelopeSigner
    {
        public byte[] Sign(ReadOnlySpan<byte> message) => key.Sign(message);
    }
}

// The link on the live relay connection, attached and detached as the host connects and disconnects.
public sealed class RelayLiteLink(LiteReceiver? localReceiver = null, byte[]? localStatic = null, Func<string>? localAccount = null) : ILiteLink
{
    sealed record Current(RelayClient Relay, ChannelHub Hub);
    Current? now;
    readonly byte[]? localStatic = localStatic?.ToArray();
    readonly string localId = localStatic is null ? "" : Wire.DeviceId(localStatic);

    public void Attach(RelayClient? relay, ChannelHub? hub) => Volatile.Write(ref now, relay is null || hub is null ? null : new Current(relay, hub));
    public string Self => now?.Relay.DeviceId ?? localId;
    public string Account => now?.Relay.AccountId ?? localAccount?.Invoke() ?? "";
    public IReadOnlyList<PresenceEntry> Presence => now?.Relay.Presence ?? [];

    public async Task<LiteAck> SendAsync(string peerId, string account, object request, CancellationToken ct)
    {
        if (localReceiver is not null && peerId == localId)
        {
            var currentAccount = localAccount?.Invoke() ?? "";
            if (string.IsNullOrEmpty(currentAccount) || account != currentAccount || Self != localId) { throw new LitePeerOfflineException("local account changed"); }
            ct.ThrowIfCancellationRequested();
            return await localReceiver.ReceiveAsync(new LiteChannel(localId, "host", localStatic, localId, currentAccount, true),
                JsonSerializer.SerializeToElement(request), ct);
        }
        var l = Volatile.Read(ref now) ?? throw new LitePeerOfflineException("not connected to the relay");
        SecureChannel ch;
        try { ch = await l.Hub.OpenAsync(peerId, TimeSpan.FromSeconds(10)); }
        catch (IOException e) { throw new LitePeerOfflineException(e.Message); }
        try
        {
            // The opened channel is judged by the same rule the receiver uses; nothing is sent to a peer it refuses.
            var c = LiteChannel.Of(ch);
            if ((c.Account != account ? LiteReceiver.NotSameAccount : c.Untrusted) is { } why) { throw new LitePeerOfflineException($"not a present host of this account ({why})"); }
            var response = await ch.RequestAsync(LiteReceiver.Op, request, TimeSpan.FromSeconds(10), ct);
            if (!response.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True) { return new LiteAck("refused", "peerError", "", 0); }
            return response.GetProperty("result").Deserialize<LiteAck>() ?? throw new JsonException("empty acknowledgement");
        }
        finally { await l.Hub.CloseAsync(ch); }
    }
}
