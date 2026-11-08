using System.Text.Json;
using AgentBridge.Host;
using AgentBridge.Host.Core;
using AgentBridge.Host.SignIn;
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

    // #972 slice 2: the pin is the roster's, by the channel's full key and account. The kind the
    // channel claims is never asked; the roster's kind and presence are. A #402 person pin (every
    // channel here has one) is not enough, and no roster, or one unreadable, pins nobody.
    [Theory]
    [InlineData("app", "phone", "key", RosterState.Ready, true)]
    [InlineData("pwa", "pwa", "webauthn", RosterState.Ready, true)]
    [InlineData("host", "phone", "key", RosterState.Ready, true)]
    [InlineData("app", "host", "key", RosterState.Ready, false)]
    [InlineData("app", "phone", "none", RosterState.Ready, false)]
    [InlineData("app", "watch", "key", RosterState.Ready, false)]
    [InlineData("app", "phone", "key", RosterState.Unreadable, false)]
    [InlineData("app", "phone", "key", RosterState.None, false)]
    [InlineData("app", null, null, RosterState.Ready, false)]
    public async Task Sign_in_is_for_a_device_the_roster_pins_as_a_person_whatever_kind_it_claims(
        string claimed, string? kind, string? presence, RosterState state, bool allowed)
    {
        var (_, channel) = await OpenAsync(claimed);
        var roster = new Roster { State = state };
        if (kind is not null) { roster.Pin(channel.PeerStatic!, host.Relay.AccountId, kind, presence!); }
        var authority = new RosterSignInAuthority(roster, () => false, _ => true);

        Assert.Equal(allowed, authority.Current(channel) is not null);
        Assert.Null(new RosterSignInAuthority(new NoPersonRoster(), () => false, _ => true).Current(channel));
        // Pinned or not: never on a read-only machine (G5), nor where trusted-peer enforcement gives
        // the channel no person role.
        Assert.Null(new RosterSignInAuthority(roster, () => true, _ => true).Current(channel));
        Assert.Null(new RosterSignInAuthority(roster, () => false, _ => false).Current(channel));
    }

    // Trust's condition 4: a removal, heard from anywhere, says it changed and ends the grant; the
    // same key added again is another grant, which the old one's flow never becomes.
    [Fact]
    public async Task A_removal_ends_the_grant_and_says_so_and_a_re_add_is_a_new_grant()
    {
        var (_, channel) = await OpenAsync();
        var roster = new Roster();
        roster.Pin(channel.PeerStatic!, host.Relay.AccountId);
        var authority = new RosterSignInAuthority(roster, () => false, _ => true);
        var changes = 0;
        authority.Changed += () => changes++;
        var grant = Assert.IsType<SignInGrant>(authority.Current(channel));
        Assert.True(authority.StillCurrent(grant));

        roster.Remove(channel.PeerStatic!);
        Assert.Equal(1, changes);
        Assert.False(authority.StillCurrent(grant));
        Assert.Null(authority.Current(channel));

        roster.Pin(channel.PeerStatic!, host.Relay.AccountId, generation: "2");
        Assert.False(authority.StillCurrent(grant));
        Assert.NotEqual(grant, authority.Current(channel));
        roster.State = RosterState.Unreadable;
        Assert.Null(authority.Current(channel));
        roster.Throws = true;
        roster.State = RosterState.Ready;
        Assert.Null(authority.Current(channel));
    }

    // The same service, built the same way, with only the roster swapped: none offers nothing and
    // refuses signin.start; one that pins this phone offers sign-in and lists it as this device.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Swapping_in_a_roster_that_pins_the_phone_is_all_it_takes_to_offer_sign_in(bool pinned)
    {
        var (_, channel) = await OpenAsync("phone");
        var fake = new Roster();
        if (pinned) { fake.Pin(channel.PeerStatic!, host.Relay.AccountId); }
        IPersonDeviceRoster roster = pinned ? fake : new NoPersonRoster();
        await using var signIn = SignInFor(roster, out var sessions);

        var hello = (HelloResult)(await sessions.HandleAsync(channel, Ops.Hello, Json(new { })))!;
        var devices = Assert.IsType<SignInDevices>(await sessions.HandleAsync(channel, AgentSignIn.Devices, Json(new { })));

        Assert.Equal(pinned, hello.Capabilities.Contains(AgentSignIn.Capability));
        Assert.Contains(AgentSignIn.DevicesCapability, hello.Capabilities);
        Assert.Equal(pinned ? new[] { new SignInDevice("iPhone", "phone", "iOS", true) } : [], devices.Devices);
        Assert.Equal(pinned ? SignInDeviceStates.Ready : SignInDeviceStates.None, devices.State);
        if (!pinned)
        {
            var start = await sessions.HandleAsync(channel, AgentSignIn.Start, Json(new { request = Guid.NewGuid(), agent = "copilot" }));
            Assert.Equal("refused", JsonSerializer.SerializeToElement(start, ProtocolJson.Options).GetProperty("outcome").GetString());
        }
    }

    // The switch off (no sign-in service), a roster that pins this phone offers nothing either.
    [Fact]
    public async Task Who_can_sign_in_is_not_offered_where_the_machine_has_sign_in_off()
    {
        var (_, channel) = await OpenAsync("phone");
        var roster = new Roster();
        roster.Pin(channel.PeerStatic!, host.Relay.AccountId);
        var sessions = new SessionService(source, new MachineInfo(host.Id, "Host", "windows", "0.1.0"))
        {
            SignInDevices = self => PersonDevices.View(roster, host.Relay.AccountId, self),
        };

        var hello = (HelloResult)(await sessions.HandleAsync(channel, Ops.Hello, Json(new { })))!;

        Assert.DoesNotContain(AgentSignIn.DevicesCapability, hello.Capabilities);
        Assert.DoesNotContain(AgentSignIn.Capability, hello.Capabilities);
        Assert.Null(await sessions.HandleAsync(channel, AgentSignIn.Devices, Json(new { })));
    }
    [Theory]
    [InlineData(TrustedPeerStore.Person, true)]
    [InlineData(TrustedPeerStore.Service, false)]
    [InlineData(TrustedPeerStore.Host, false)]
    public async Task Who_can_sign_in_is_a_person_role_read_and_carries_no_keys(string role, bool allowed)
    {
        var peer = await PeerAsync("phone");
        Pin(peer, role);
        var roster = new Roster();
        roster.Pin(peer.Key.Public, host.Relay.AccountId);
        await using var signIn = SignInFor(roster, out var sessions);
        host.Hub.Handler = authorization.Handler(sessions);
        var channel = await peer.Hub.OpenAsync(host.Id, Patience);

        if (!allowed) { await DeniedAsync(channel, AgentSignIn.Devices); return; }
        var result = Result(await channel.RequestAsync(AgentSignIn.Devices, new { }, Patience));
        Assert.Equal("""{"state":"ready","devices":[{"name":"iPhone","kind":"phone","platform":"iOS","thisDevice":true}]}""", result.GetRawText());
        var hello = Result(await channel.RequestAsync(Ops.Hello, new { }, Patience)).Deserialize<HelloResult>(ProtocolJson.Options)!;
        Assert.Contains(AgentSignIn.DevicesCapability, hello.Capabilities);
    }

    async Task<(Device Peer, SecureChannel Channel)> OpenAsync(string kind = "app")
    {
        var peer = await PeerAsync(kind);
        Pin(peer, TrustedPeerStore.Person);
        var accepted = new TaskCompletionSource<SecureChannel>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Hub.Accepted += ch => accepted.TrySetResult(ch);
        await peer.Hub.OpenAsync(host.Id, Patience);
        return (peer, await accepted.Task.WaitAsync(Patience));
    }

    // Sign-in turned on, with Copilot's adapter, as serve builds it; only the roster differs.
    AgentSignInService SignInFor(IPersonDeviceRoster roster, out SessionService sessions)
    {
        var on = HostSettings.Parse("{\"permissions\":{\"phoneSignIn\":true}}", HostSettings.FileName).Settings;
        var signIn = PhoneSignInSetup.Create(on, copilotAdapter: true, source, new RosterSignInAuthority(roster, () => false, _ => true),
            new HostSignInContextResolver(source, fixture.CopilotHome), new NoLogin(), () => false, time)!;
        sessions = new SessionService(source, new MachineInfo(host.Id, "Host", "windows", "0.1.0"))
        {
            SignIn = signIn, SignInEntrypointApproved = true,
            SignInDevices = self => PersonDevices.View(roster, host.Relay.AccountId, self),
        };
        return signIn;
    }

    static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value, ProtocolJson.Options);

    sealed class NoLogin : IPrivateSignInProcessFactory
    {
        public Task<IPrivateSignInProcess> StartAsync(SignInTarget target, Func<bool> authorize, CancellationToken ct) =>
            throw new InvalidOperationException("no sign-in runs in these tests");
    }

    sealed class Roster : IPersonDeviceRoster
    {
        readonly List<(byte[] Key, string Account, PinnedDevice Device)> entries = [];
        public RosterState State { get; set; } = RosterState.Ready;
        public bool Throws;
        public void Pin(byte[] key, string account, string kind = "phone", string presence = "key", string generation = "1") =>
            entries.Add((key, account, new(Wire.DeviceId(key), kind, "iOS", "iPhone", presence, generation, $"add-{generation}")));
        public void Remove(byte[] key)
        {
            entries.RemoveAll(e => e.Key.AsSpan().SequenceEqual(key));
            Changed?.Invoke();
        }
        public PinnedDevice? Find(byte[] key, string account) => Throws ? throw new IOException("unreadable")
            : entries.Where(e => e.Key.AsSpan().SequenceEqual(key) && e.Account == account).Select(e => e.Device).FirstOrDefault();
        public IReadOnlyList<PinnedDevice> Pinned(string account) => [.. entries.Where(e => e.Account == account).Select(e => e.Device)];
        public bool StillPinned(PinnedDevice device) => !Throws && entries.Any(e => e.Device == device);
        public event Action? Changed;
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
        await HostWait.UntilAsync(() => service.Subscribers == 1, "the read-only feed", Patience);
        foreach (var op in new[]
        {
            Ops.SessionAnswer, Ops.SessionReply, Ops.SessionStop, Ops.AttachmentStart, Ops.PushSubscribe,
            LaunchProtocol.Options, LaunchProtocol.Launch, TransferProtocol.Op, TrustProtocol.Answer,
            UsageProtocol.Get, UpdateProtocol.Install, "host.peers.reset", "future.operation",
            // The HA connector's reads are the latest exchange; a session's history is a person's (#292).
            HistoryProtocol.Op,
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

    // #780: machine.restore names the sessions a reboot took. A host peer, which may not read
    // sessions, is not told them in hello either, nor told when they change.
    [Fact]
    public async Task A_host_peer_is_not_told_the_sessions_a_reboot_took_but_a_person_and_a_reader_are()
    {
        RestoreNotice? notice = new() { At = time.GetUtcNow(), Cause = RestoreCause.Reboot, Offered = [new RestoreSession { Id = SessionId, Agent = AgentKind.Copilot, Title = "Read without taking over" }] };
        var watched = new SessionService(source, new MachineInfo(host.Id, "Host", "windows", "0.1.0")) { Drivers = drivers, Time = time, Restore = () => notice };
        host.Hub.Handler = authorization.Handler(watched);
        var other = await PeerAsync("app");
        Pin(other, TrustedPeerStore.Host);
        var person = await PeerAsync();
        Pin(person, TrustedPeerStore.Person);
        var reader = await PeerAsync();
        Pin(reader);
        var otherChannel = await other.Hub.OpenAsync(host.Id, Patience);
        var personChannel = await person.Hub.OpenAsync(host.Id, Patience);
        var readerChannel = await reader.Hub.OpenAsync(host.Id, Patience);
        HelloResult Hello(JsonElement response) => Result(response).Deserialize<HelloResult>(ProtocolJson.Options)!;

        Assert.Null(Hello(await otherChannel.RequestAsync(Ops.Hello, new { changes = true }, Patience)).Machine.Restore);
        Assert.Equal(SessionId, Assert.Single(Hello(await personChannel.RequestAsync(Ops.Hello, new { changes = true }, Patience)).Machine.Restore!.Offered).Id);
        Assert.NotNull(Hello(await readerChannel.RequestAsync(Ops.Hello, new { }, Patience)).Machine.Restore);

        notice = null;
        time.Advance(watched.HelloCheck);
        Assert.Equal(Events.HelloChanged, (await personChannel.Events.ReadAsync().AsTask().WaitAsync(Patience)).Name);
        await Task.Delay(200);
        Assert.False(otherChannel.Events.TryRead(out _));
    }
    [Theory]
    [InlineData(TrustedPeerStore.Host)]
    [InlineData(TrustedPeerStore.Person)]
    public async Task Role_is_the_local_pin_not_the_intro_and_host_transfer_authority_is_not_person_authority(string role)
    {
        var peer = await PeerAsync(role == TrustedPeerStore.Host ? "app" : "host");
        Pin(peer, role);
        var channel = await peer.Hub.OpenAsync(host.Id, Patience);
        var hello = Result(await channel.RequestAsync(Ops.Hello, new { }, Patience)).Deserialize<HelloResult>(ProtocolJson.Options)!;
        if (role == TrustedPeerStore.Person)
        {
            Result(await channel.RequestAsync(Ops.SessionsList, new { }, Patience));
            await DeniedAsync(channel, TransferProtocol.Op);
            // A person's device pages back through a session's history (#292).
            Assert.Contains(HistoryProtocol.Capability, hello.Capabilities);
            var history = Result(await channel.RequestAsync(HistoryProtocol.Op, new { id = SessionId }, Patience)).Deserialize<HistoryResult>(ProtocolJson.Options)!;
            Assert.Equal("go", history.Turns!.Single().Prompt!.Text);
        }
        else
        {
            Assert.DoesNotContain(HistoryProtocol.Capability, hello.Capabilities);
            await DeniedAsync(channel, HistoryProtocol.Op);
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
        await HostWait.UntilAsync(() => service.Subscribers == 1, "the active feed", Patience);
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
            await HostWait.UntilAsync(() => service.Subscribers == 0, "the revoked feed to end", Patience);
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

    [Fact]
    public async Task Diagnostics_reach_only_a_device_pinned_as_a_person_whatever_it_claims_or_sends()
    {
        var diagnostics = new RemoteDiagnostics(authorization.IsPinnedPerson, null, "0.1.0", false, () => null, () => (DiagnosticsReadOnly.None, null), time);
        host.Hub.Handler = authorization.Handler(new SessionService(source, new MachineInfo(host.Id, "Host", "windows", "0.1.0"))
        {
            Drivers = drivers,
            Diagnostics = diagnostics,
        });
        HelloResult Hello(JsonElement response) => Result(response).Deserialize<HelloResult>(ProtocolJson.Options)!;
        // A service or another host says it is a person's app, and is still what its pin says.
        foreach (var role in new[] { TrustedPeerStore.Service, TrustedPeerStore.Host })
        {
            var peer = await PeerAsync("pwa");
            Pin(peer, role);
            var other = await peer.Hub.OpenAsync(host.Id, Patience);
            Assert.DoesNotContain(DiagnosticsProtocol.Capability, Hello(await other.RequestAsync(Ops.Hello, new { }, Patience)).Capabilities);
            await DeniedAsync(other, DiagnosticsProtocol.Get);
        }

        var person = await PeerAsync("host");
        Pin(person, TrustedPeerStore.Person);
        var channel = await person.Hub.OpenAsync(host.Id, Patience);
        Assert.Contains(DiagnosticsProtocol.Capability, Hello(await channel.RequestAsync(Ops.Hello, new { }, Patience)).Capabilities);
        // Who asks is the channel's pinned key: a principal or origin in the request is refused, not obeyed or ignored.
        var forged = await channel.RequestAsync(DiagnosticsProtocol.Get, new { maxEntries = 5, principal = "agent", origin = "copilot" }, Patience);
        Assert.False(forged.GetProperty("ok").GetBoolean());
        Assert.False(forged.TryGetProperty("result", out _));
        // Nor is an agent's principal or signed origin nested inside the person's request.
        var nested = await channel.RequestAsync(DiagnosticsProtocol.Get,
            new { maxEntries = 5, on = new { principal = "agent", origin = new { kind = "agent", signed = "copilot" } } }, Patience);
        Assert.False(nested.GetProperty("ok").GetBoolean());
        Assert.False(nested.TryGetProperty("result", out _));
        var snapshot = Result(await channel.RequestAsync(DiagnosticsProtocol.Get, new { maxEntries = 5 }, Patience))
            .Deserialize<DiagnosticsSnapshot>(ProtocolJson.Options)!;
        Assert.Equal(DiagnosticsProtocol.Schema, snapshot.Schema);
        Assert.Empty(snapshot.Journal.Entries);
        Assert.Equal(DiagnosticsMissing.NotConfigured, snapshot.Journal.Missing);
        Assert.Equal(DiagnosticsMissing.NotStarted, snapshot.Relay.Missing);
        // The same device again at once is refused over the channel, then served after MinInterval.
        var again = await channel.RequestAsync(DiagnosticsProtocol.Get, new { }, Patience);
        Assert.Equal(RemoteDiagnostics.TooSoon, again.GetProperty("error").GetString());
        time.Advance(DiagnosticsProtocol.MinInterval);
        Assert.True((await channel.RequestAsync(DiagnosticsProtocol.Get, new { }, Patience)).GetProperty("ok").GetBoolean());

        store.Revoke(person.Key.Public);
        authorization.Revalidate();
        await channel.Closed.WaitAsync(Patience);
    }

    [Fact]
    public async Task Diagnostics_are_neither_offered_nor_served_while_pins_are_off()
    {
        authorization.Dispose();
        authorization = new RemoteAuthorization(false, store, time);
        authorization.Attach(host.Hub);
        host.Hub.Handler = authorization.Handler(new SessionService(source, new MachineInfo(host.Id, "Host", "windows", "0.1.0"))
        {
            Drivers = drivers,
            Diagnostics = new RemoteDiagnostics(authorization.IsPinnedPerson, null, "0.1.0", false, () => null, () => (DiagnosticsReadOnly.None, null), time),
        });
        var peer = await PeerAsync("app");
        Pin(peer, TrustedPeerStore.Person);
        var channel = await peer.Hub.OpenAsync(host.Id, Patience);
        var hello = Result(await channel.RequestAsync(Ops.Hello, new { }, Patience)).Deserialize<HelloResult>(ProtocolJson.Options)!;
        Assert.DoesNotContain(DiagnosticsProtocol.Capability, hello.Capabilities);
        var refused = await channel.RequestAsync(DiagnosticsProtocol.Get, new { }, Patience);
        Assert.False(refused.GetProperty("ok").GetBoolean());
        Assert.Equal(RemoteDiagnostics.Refusal, refused.GetProperty("error").GetString());
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
