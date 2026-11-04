using System.Text.Json;
using AgentBridge.Host;
using AgentBridge.Host.Core;
using AgentBridge.Protocol;
using AgentBridge.Transport;
using AgentBridge.Transport.Tests;
using Microsoft.Extensions.Time.Testing;
using Xunit;

[Trait("Category", "Network")]
public sealed class RemoteAuthorizationTests : IAsyncLifetime
{
    const string SessionId = "88888888-8888-4888-8888-888888888888";
    static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);
    readonly Fixture fixture = new();
    readonly FakeTimeProvider time = new(new DateTimeOffset(2026, 11, 3, 0, 0, 0, TimeSpan.Zero));
    readonly SessionDrivers drivers = new();
    readonly List<Device> devices = [];
    SessionSource source = null!;
    TestRelay relay = null!;
    Device host = null!;
    TrustedPeerStore store = null!;
    RemoteAuthorization authorization = null!;
    SessionService service = null!;

    public async Task InitializeAsync()
    {
        var events = fixture.CopilotSession(SessionId, 100, name: "Read without taking over");
        Fixture.Append(events, Copilot.User("go", 1), Copilot.TurnStart(2));
        fixture.Processes.Run(AgentKind.Copilot, 100);
        source = new SessionSource(fixture.Options(), fixture.Processes, TimeProvider.System);
        await source.RefreshAsync();
        relay = await TestRelay.StartAsync();
        host = await PeerAsync("host");
        store = new TrustedPeerStore(Path.Combine(fixture.Root, "trust"), host.Key.Public, () => host.Relay.AccountId, time);
        store.Reset();
        authorization = new RemoteAuthorization(true, store, time);
        authorization.Attach(host.Hub);
        service = new SessionService(source, new MachineInfo(host.Id, "Host", "windows", "0.1.0")) { Drivers = drivers };
        host.Hub.JsonOptions = ProtocolJson.Options;
        host.Hub.Handler = authorization.Handler(service);
    }

    async Task<Device> PeerAsync(string kind = "app")
    {
        var peer = await relay.ConnectAsync(relay.TokenFor(1, "fixture"), "Peer", kind);
        peer.Hub.JsonOptions = ProtocolJson.Options;
        devices.Add(peer);
        return peer;
    }

    void Pin(Device peer, string role = TrustedPeerStore.Service) =>
        store.Put(peer.Key.Public, role, time.GetUtcNow().AddHours(1));

    static JsonElement Result(JsonElement response)
    {
        Assert.True(response.GetProperty("ok").GetBoolean(), response.GetRawText());
        return response.GetProperty("result");
    }

    static async Task DeniedAsync(SecureChannel channel, string op)
    {
        var response = await channel.RequestAsync(op, new { }, Patience);
        Assert.False(response.GetProperty("ok").GetBoolean());
        Assert.Equal("the peer role does not allow this operation", response.GetProperty("error").GetString());
    }

    [Theory]
    [InlineData("app")]
    [InlineData("pwa")]
    [InlineData("host")]
    [InlineData("phone")]
    public async Task A_relay_admitted_key_has_no_authority_whatever_kind_it_claims(string kind)
    {
        var peer = await PeerAsync(kind);
        var accepted = 0;
        host.Hub.Accepted += _ => Interlocked.Increment(ref accepted);
        var channel = await peer.Hub.OpenAsync(host.Id, Patience);
        await channel.Closed.WaitAsync(Patience);
        Assert.Equal(0, Volatile.Read(ref accepted));
        Assert.Equal(0, host.Hub.ChannelCount);
        Assert.Equal(0, service.Subscribers);
    }

    [Fact]
    public async Task Host_initiated_channels_also_require_a_pin_before_the_handshake_completes()
    {
        var peer = await PeerAsync("host");
        await Assert.ThrowsAsync<IOException>(() => host.Hub.OpenAsync(peer.Id, Patience));
        Assert.Equal(0, host.Hub.ChannelCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_service_only_reads_even_on_a_host_initiated_channel_and_does_not_change_the_driver(bool outbound)
    {
        var peer = await PeerAsync("app");
        Pin(peer);
        SecureChannel channel;
        if (outbound)
        {
            var accepted = new TaskCompletionSource<SecureChannel>(TaskCreationOptions.RunContinuationsAsynchronously);
            peer.Hub.Accepted += ch => accepted.TrySetResult(ch);
            await host.Hub.OpenAsync(peer.Id, Patience);
            channel = await accepted.Task.WaitAsync(Patience);
        }
        else { channel = await peer.Hub.OpenAsync(host.Id, Patience); }

        var hello = Result(await channel.RequestAsync(Ops.Hello, new { }, Patience)).Deserialize<HelloResult>(ProtocolJson.Options)!;
        Assert.Equal([Capabilities.SessionsRead], hello.Capabilities);
        Result(await channel.RequestAsync(Ops.SessionsList, new { }, Patience));
        Assert.Equal(SessionId, Result(await channel.RequestAsync(Ops.SessionGet, new { id = SessionId }, Patience)).GetProperty("id").GetString());
        Result(await channel.RequestAsync(Ops.SessionsSubscribe, new { }, Patience));
        await Wait.UntilAsync(() => service.Subscribers == 1, "the read-only feed", Patience);
        foreach (var op in new[]
        {
            Ops.SessionAnswer, Ops.SessionReply, Ops.SessionStop, Ops.AttachmentStart, Ops.PushSubscribe,
            LaunchProtocol.Options, LaunchProtocol.Launch, TransferProtocol.Op, TrustProtocol.Answer,
            UsageProtocol.Get, UpdateProtocol.Install, "host.peers.reset", "future.operation",
        })
        {
            await DeniedAsync(channel, op);
        }
        Assert.Equal(0, drivers.GenerationOf(SessionId));
        Assert.Equal((null, (string?)null), drivers.Shown(SessionId));
    }

    [Fact]
    public async Task A_service_peer_that_asked_for_changes_is_not_told_when_only_update_changes_but_a_person_is()
    {
        string? updateWithheld = null;
        var watched = new SessionService(source, new MachineInfo(host.Id, "Host", "windows", "0.1.0"))
        {
            Drivers = drivers,
            Time = time,
            Updates = new UpdateService("0.1.0", time: time) { ReadOnly = () => updateWithheld },
        };
        host.Hub.Handler = authorization.Handler(watched);
        var reader = await PeerAsync();
        Pin(reader);
        var person = await PeerAsync();
        Pin(person, TrustedPeerStore.Person);
        var readerChannel = await reader.Hub.OpenAsync(host.Id, Patience);
        var personChannel = await person.Hub.OpenAsync(host.Id, Patience);
        HelloResult Hello(JsonElement response) => Result(response).Deserialize<HelloResult>(ProtocolJson.Options)!;

        Assert.Equal([Capabilities.SessionsRead], Hello(await readerChannel.RequestAsync(Ops.Hello, new { changes = true }, Patience)).Capabilities);
        Assert.Contains(UpdateProtocol.Capability, Hello(await personChannel.RequestAsync(Ops.Hello, new { changes = true }, Patience)).Capabilities);

        updateWithheld = "agent-ha-bridge is running for this user";
        time.Advance(watched.HelloCheck);
        Assert.Equal(Events.HelloChanged, (await personChannel.Events.ReadAsync().AsTask().WaitAsync(Patience)).Name);
        // Told in the same look as the person, had it been told at all.
        await Task.Delay(200);
        Assert.False(readerChannel.Events.TryRead(out _));
    }

    [Theory]
    [InlineData(TrustedPeerStore.Host)]
    [InlineData(TrustedPeerStore.Person)]
    public async Task Role_is_the_local_pin_not_the_intro_and_host_transfer_authority_is_not_person_authority(string role)
    {
        var peer = await PeerAsync(role == TrustedPeerStore.Host ? "app" : "host");
        Pin(peer, role);
        var channel = await peer.Hub.OpenAsync(host.Id, Patience);
        Result(await channel.RequestAsync(Ops.Hello, new { }, Patience));
        if (role == TrustedPeerStore.Person)
        {
            Result(await channel.RequestAsync(Ops.SessionsList, new { }, Patience));
            await DeniedAsync(channel, TransferProtocol.Op);
        }
        else
        {
            await DeniedAsync(channel, Ops.SessionsList);
            await DeniedAsync(channel, UsageProtocol.Subscribe);
            await DeniedAsync(channel, LaunchProtocol.Launch);
            var unavailable = await channel.RequestAsync(UsageProtocol.Get, new { }, Patience);
            Assert.Equal($"unknown op {UsageProtocol.Get}", unavailable.GetProperty("error").GetString());
        }
        await DeniedAsync(channel, "future.operation");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Revocation_or_expiry_ends_feeds_and_pending_calls_without_disturbing_other_pins(bool expire)
    {
        var peer = await PeerAsync();
        var other = await PeerAsync();
        Pin(peer);
        store.Put(other.Key.Public, TrustedPeerStore.Person, time.GetUtcNow().AddHours(2));
        var accepted = new TaskCompletionSource<SecureChannel>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Hub.Accepted += ch => { if (ch.PeerId == peer.Id) { accepted.TrySetResult(ch); } };
        var channel = await peer.Hub.OpenAsync(host.Id, Patience);
        var otherChannel = await other.Hub.OpenAsync(host.Id, Patience);
        Result(await channel.RequestAsync(Ops.SessionsSubscribe, new { }, Patience));
        await Wait.UntilAsync(() => service.Subscribers == 1, "the active feed", Patience);
        var held = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.Hub.Handler = (_, _, _) => { started.TrySetResult(); return held.Task; };
        var pending = (await accepted.Task).RequestAsync("held", new { }, Patience);
        await started.Task.WaitAsync(Patience);
        try
        {
            if (expire) { time.Advance(TimeSpan.FromHours(1)); }
            else { store.Revoke(peer.Key.Public); authorization.Revalidate(); }
            await Assert.ThrowsAsync<IOException>(async () => await pending);
            await channel.Closed.WaitAsync(Patience);
            await Wait.UntilAsync(() => service.Subscribers == 0, "the revoked feed to end", Patience);
            Result(await otherChannel.RequestAsync(Ops.Hello, new { }, Patience));
        }
        finally { held.TrySetResult(new { finished = true }); }
    }

    [Fact]
    public async Task Reapproval_requires_a_new_channel_instead_of_upgrading_an_old_lease()
    {
        var peer = await PeerAsync();
        Pin(peer);
        var old = await peer.Hub.OpenAsync(host.Id, Patience);
        Result(await old.RequestAsync(Ops.Hello, new { }, Patience));
        Pin(peer, TrustedPeerStore.Person);
        authorization.Revalidate();
        await old.Closed.WaitAsync(Patience);
        var current = await peer.Hub.OpenAsync(host.Id, Patience);
        Result(await current.RequestAsync(Ops.SessionsList, new { }, Patience));
    }

    [Fact]
    public async Task Push_registration_binds_the_authenticated_channel_key_not_subscription_or_request_keys()
    {
        var peer = await PeerAsync("host");
        Pin(peer, TrustedPeerStore.Person);
        var subscriptions = PushStore.Load(null, host.Relay.AccountId);
        await using var push = new PushNotifier(source, new MachineInfo(host.Id, "Host", "windows", "0.1.0"), subscriptions, trust: authorization);
        authorization.Changed += push.RevalidateTrust;
        host.Hub.Handler = authorization.Handler(new SessionService(source, new MachineInfo(host.Id, "Host", "windows", "0.1.0"), push: push));
        var channel = await peer.Hub.OpenAsync(host.Id, Patience);
        using var browser = P256KeyPair.Generate();
        Result(await channel.RequestAsync(Ops.PushSubscribe, new
        {
            endpoint = "https://web.push.apple.com/fixture",
            keys = new { p256dh = B64Url.Encode(browser.Public), auth = B64Url.Encode(new byte[16]) },
            app = "https://app.example/",
            publicKey = B64Url.Encode(browser.Public),
        }, Patience));
        Assert.Equal(store.Find(peer.Key.Public, host.Relay.AccountId), Assert.Single(subscriptions.All).Grant);
        store.Revoke(peer.Key.Public);
        authorization.Revalidate();
        Assert.Empty(subscriptions.All);
    }

    [Fact]
    public async Task Disabled_mode_preserves_legacy_unpinned_person_requests()
    {
        authorization.Dispose();
        authorization = new RemoteAuthorization(false, store, time);
        authorization.Attach(host.Hub);
        host.Hub.Handler = authorization.Handler(service);
        var peer = await PeerAsync();
        var channel = await peer.Hub.OpenAsync(host.Id, Patience);
        var response = Result(await channel.RequestAsync(Ops.SessionAnswer,
            new { id = SessionId, question = "q", request = Guid.NewGuid().ToString(), text = "yes" }, Patience));
        Assert.Equal("this host does not take answers; answer at the machine", response.GetProperty("reason").GetString());
    }

    public async Task DisposeAsync()
    {
        authorization.Dispose();
        await relay.DisposeAsync();
        await source.DisposeAsync();
        foreach (var device in devices) { device.Key.Dispose(); }
        fixture.Dispose();
    }
}
