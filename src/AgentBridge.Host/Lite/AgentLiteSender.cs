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
    Task<LiteAck> SendAsync(string peerId, object request, CancellationToken ct);
}

// AP5-lite L3 (#728, #1351): sends a signed own-agent envelope to a pinned host of the same account and reports
// one typed outcome. Never resends, and never reports delivered without the receiver's acknowledgement.
public sealed class AgentLiteSender(
    ILiteMode mode, ILitePeers peers, ILiteLink link, Func<AgentCredential, bool> credentialLive, Func<IEnvelopeSigner?> signer,
    byte[] staticPublic, TimeProvider? time = null, TimeSpan? ackTimeout = null, TimeSpan? watchEvery = null) : ILiteSender
{
    public const string OfflineText = "that machine is offline; nothing was queued";
    public const string NoCredential = "credential", NoSigner = "no-sign-key", NotPeer = LiteReceiver.NotPeer, Malformed = LiteReceiver.Malformed;
    public const string TimedOut = "timeout", ModeSwitchedOff = "mode switched off", ChannelLost = "channel lost", BadAck = "unreadable acknowledgement";

    readonly TimeProvider time = time ?? TimeProvider.System;
    readonly TimeSpan ackTimeout = ackTimeout ?? TimeSpan.FromSeconds(10);
    readonly TimeSpan watchEvery = watchEvery ?? TimeSpan.FromMilliseconds(500);
    readonly string instance = Codec.Base64Url(RandomNumberGenerator.GetBytes(12));
    static readonly string Zero12 = Codec.Base64Url(new byte[12]);

    public async Task<LiteOutcome> SendAsync(AgentCredential caller, LiteSendInput input, CancellationToken ct)
    {
        if (!mode.On) { return new LiteOutcome(LiteKind.Off, "off", ""); }
        var request = Guid.NewGuid().ToString("D");
        LiteOutcome Out(LiteKind kind, string? code) => new(kind, code, request);

        if (string.IsNullOrEmpty(input.Text) || input.Text.Length > LiteReceiver.MaxText || string.IsNullOrEmpty(input.Session)) { return Out(LiteKind.Refused, Malformed); }
        var data = Encoding.UTF8.GetBytes(input.Text);
        // Checked afresh, before anything is signed: a credential revoked or expired since the pipe call signs nothing.
        if (string.IsNullOrEmpty(caller.Session) || !credentialLive(caller)) { return Out(LiteKind.Refused, NoCredential); }

        var peer = peers.All().FirstOrDefault(p => p.DeviceId == input.Machine || string.Equals(p.Name, input.Machine, StringComparison.OrdinalIgnoreCase));
        if (peer is null) { return Out(LiteKind.Refused, NotPeer); }
        if (!link.Presence.Any(p => p.Id == peer.DeviceId && p.Kind == "host")) { return Out(LiteKind.Undeliverable, OfflineText); }
        if (signer() is not { } sign) { return Out(LiteKind.Refused, NoSigner); }

        var from = new LiteSender(link.Account, link.Self, Codec.Base64Url(SHA256.HashData(staticPublic)), caller.Session, instance,
            Codec.Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(caller.Id))[..16]), "agent", "agent-bridge");
        byte[] envelope;
        try { envelope = LiteProfile.Seal(from, new LiteTarget(peer.DeviceId, input.Session, Zero12), request, data, time.GetUtcNow().ToUnixTimeSeconds(), sign); }
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
            var ack = await link.SendAsync(peer.DeviceId, new { envelope = Codec.Base64Url(envelope), text = input.Text, killEpoch = mode.KillEpoch }, flight.Token);
            if (ack.KillEpoch > mode.KillEpoch) { mode.RaiseEpoch(ack.KillEpoch); }
            if (ack.Request != request) { return Out(LiteKind.Unconfirmed, BadAck); }
            return ack.Status switch
            {
                "delivered" => Out(LiteKind.Delivered, null),
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

    // The host's own sender: the agent signing key is opened once and held, and credentials are judged by the store.
    public static AgentLiteSender ForHost(string profileFolder, ILiteMode mode, ILitePeers peers, ILiteLink link, AgentCredentials? credentials, byte[] staticPublic)
    {
        AgentSignKey? key = null;
        var gate = new object();
        IEnvelopeSigner? Signer()
        {
            lock (gate) { key ??= AgentSignKey.Open(profileFolder); }
            return key is null ? null : new KeySigner(key);
        }
        return new AgentLiteSender(mode, peers, link, c => credentials?.IsLive(c) == true, Signer, staticPublic);
    }

    sealed class KeySigner(AgentSignKey key) : IEnvelopeSigner
    {
        public byte[] Sign(ReadOnlySpan<byte> message) => key.Sign(message);
    }
}

// The link on the live relay connection, attached and detached as the host connects and disconnects.
public sealed class RelayLiteLink : ILiteLink
{
    sealed record Current(RelayClient Relay, ChannelHub Hub);
    Current? now;

    public void Attach(RelayClient? relay, ChannelHub? hub) => Volatile.Write(ref now, relay is null || hub is null ? null : new Current(relay, hub));
    public string Self => now?.Relay.DeviceId ?? "";
    public string Account => now?.Relay.AccountId ?? "";
    public IReadOnlyList<PresenceEntry> Presence => now?.Relay.Presence ?? [];

    public async Task<LiteAck> SendAsync(string peerId, object request, CancellationToken ct)
    {
        var l = Volatile.Read(ref now) ?? throw new LitePeerOfflineException("not connected to the relay");
        SecureChannel ch;
        try { ch = await l.Hub.OpenAsync(peerId, TimeSpan.FromSeconds(10)); }
        catch (IOException e) { throw new LitePeerOfflineException(e.Message); }
        try
        {
            var response = await ch.RequestAsync(LiteReceiver.Op, request, TimeSpan.FromSeconds(10), ct);
            if (!response.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True) { return new LiteAck("refused", "peerError", "", 0); }
            return response.GetProperty("result").Deserialize<LiteAck>() ?? throw new JsonException("empty acknowledgement");
        }
        finally { await l.Hub.CloseAsync(ch); }
    }
}
