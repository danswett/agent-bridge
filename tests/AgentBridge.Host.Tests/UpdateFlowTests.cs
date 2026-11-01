using System.Text.Json;
using AgentBridge.Host;
using AgentBridge.Host.Core;
using AgentBridge.Local;
using AgentBridge.Protocol;
using AgentBridge.Transport;
using AgentBridge.Transport.Tests;
using Microsoft.Extensions.Time.Testing;
using Xunit;

// update.* (#51): another of the person's devices sees this machine's updates and asks for one,
// through the in-process relay over a Noise channel; the app beside the host is a channel that
// reports through host.update.report, as LocalApi lets only the app do (LocalApiTests).
public sealed class UpdateFlowTests : IAsyncLifetime
{
    const string Id = "66666666-6666-4666-8666-666666666666";
    static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);
    static readonly DateTimeOffset Now = new(2026, 11, 2, 18, 0, 0, TimeSpan.Zero);

    readonly Fixture f = new();
    readonly FakeTimeProvider time = new(Now);
    readonly List<string> log = [];
    readonly AppChannel app = new();
    string? readOnly;
    int asksChanged;
    TestRelay relay = null!;
    SessionSource source = null!;
    UpdateService updates = null!;
    HostControl control = null!;
    Device host = null!;
    string token = "";

    static readonly UpdateState Reported = new()
    {
        Current = "0.4.0",
        Installer = true,
        Available = "0.5.0",
        Notes = "What 0.5.0 changes",
        CheckedAt = Now,
        Automatic = true,
    };

    public async Task InitializeAsync()
    {
        var events = f.CopilotSession(Id, 100);
        Fixture.Append(events, Copilot.User("go", 1));
        f.Processes.Run(AgentKind.Copilot, 100);
        source = new SessionSource(f.Options(), f.Processes, TimeProvider.System);
        await source.RefreshAsync();
        var machine = new MachineInfo("host-id", "Test host", "windows", "0.4.0");
        updates = new UpdateService("0.4.0", m => { lock (log) { log.Add(m); } }, time) { ReadOnly = () => readOnly };
        updates.AsksChanged += () => Interlocked.Increment(ref asksChanged);
        control = new HostControl(new HostControlOptions
        {
            Profile = "test",
            Folder = f.Root,
            Version = "0.4.0",
            Machine = machine,
            Running = new HostSettingsView(RemoteApproval: true),
            SignIn = (_, _, _) => Task.FromException<SignInResult>(new InvalidOperationException("no sign-in here")),
            SignedIn = (_, _) => { },
            Stop = () => { },
            Restart = () => { },
            Updates = updates,
        });
        var service = new SessionService(source, machine) { ReadOnly = () => readOnly, Updates = updates };

        relay = await TestRelay.StartAsync();
        token = relay.TokenFor(1, "octo");
        host = await relay.ConnectAsync(token, "Test host", "host");
        host.Hub.JsonOptions = ProtocolJson.Options;
        host.Hub.Handler = service.HandleAsync;
        updates.Attach(host.Relay);
    }

    public async Task DisposeAsync()
    {
        await relay.DisposeAsync();
        await source.DisposeAsync();
        f.Dispose();
    }

    async Task<SecureChannel> OpenAsync(string name = "Phone", string kind = "pwa")
    {
        var device = await relay.ConnectAsync(token, name, kind);
        await device.Relay.WaitForPeerAsync(p => p.Id == host.Id, Patience);
        await host.Relay.WaitForPeerAsync(p => p.Id == device.Relay.DeviceId, Patience);
        return await device.Hub.OpenAsync(host.Id);
    }

    static JsonElement Result(JsonElement response)
    {
        Assert.True(response.GetProperty("ok").GetBoolean(), response.GetRawText());
        return response.GetProperty("result");
    }

    static string Error(JsonElement response)
    {
        Assert.False(response.GetProperty("ok").GetBoolean(), response.GetRawText());
        return response.GetProperty("error").GetString()!;
    }

    static T Read<T>(JsonElement e) => e.Deserialize<T>(ProtocolJson.Options)!;

    static async Task<ChannelEvent> NextAsync(SecureChannel ch)
    {
        using var cts = new CancellationTokenSource(Patience);
        return await ch.Events.ReadAsync(cts.Token);
    }

    Task<object?> ReportAsync(UpdateState state, params string[] taken) =>
        control.HandleAsync(app, HostOps.UpdateReport, JsonSerializer.SerializeToElement(new UpdateReport(state, taken.Length > 0 ? taken : null), ProtocolJson.Options));

    static Task<JsonElement> AskAsync(SecureChannel ch, string op, string? request = null) =>
        ch.RequestAsync(op, new { request = request ?? Guid.NewGuid().ToString() });

    [Fact]
    public async Task A_phone_through_the_relay_is_offered_updates_and_sees_what_the_app_reported()
    {
        var ch = await OpenAsync();
        var hello = Result(await ch.RequestAsync(Ops.Hello, new { }));
        Assert.Contains(UpdateProtocol.Capability, hello.GetProperty("capabilities").EnumerateArray().Select(c => c.GetString()));

        // Before the app has said anything: this host's version, and that nothing can install.
        var before = Read<UpdateState>(Result(await ch.RequestAsync(UpdateProtocol.Get, new { })));
        Assert.False(before.Installer);
        Assert.Equal("0.4.0", before.Current);
        Assert.Equal(UpdateService.NoApp, before.Unavailable);

        await ReportAsync(Reported);
        var now = Read<UpdateState>(Result(await ch.RequestAsync(UpdateProtocol.Get, new { })));
        Assert.Equal(Reported, now);
    }

    [Fact]
    public async Task A_subscriber_hears_each_change_the_app_reports_and_the_app_going_away()
    {
        var ch = await OpenAsync();
        var first = Read<UpdateState>(Result(await ch.RequestAsync(UpdateProtocol.Subscribe, new { })));
        Assert.False(first.Installer);

        await ReportAsync(Reported with { Phase = UpdatePhase.Downloading, Progress = 40 });
        var ev = await NextAsync(ch);
        Assert.Equal(UpdateProtocol.Event, ev.Name);
        Assert.Equal(40, Read<UpdateState>(ev.Data).Progress);

        // The app quitting (or restarting into the new release) leaves nothing to act on.
        app.Close();
        var gone = Read<UpdateState>((await NextAsync(ch)).Data);
        Assert.False(gone.Installer);
        Assert.Equal(UpdateService.NoApp, gone.Unavailable);
    }

    [Fact]
    public async Task A_subscriber_hears_nothing_more_once_the_machine_becomes_read_only()
    {
        var ch = await OpenAsync();
        Result(await ch.RequestAsync(UpdateProtocol.Subscribe, new { }));

        readOnly = "agent-ha-bridge is running for this user";
        await ReportAsync(Reported);

        using var quiet = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await ch.Events.ReadAsync(quiet.Token));
    }

    [Fact]
    public async Task A_phones_press_of_Install_reaches_the_app_in_the_hosts_status_until_the_app_takes_it()
    {
        await ReportAsync(Reported);
        var ch = await OpenAsync();
        var request = Guid.NewGuid().ToString();

        var result = Read<UpdateCommandResult>(Result(await AskAsync(ch, UpdateProtocol.Install, request)));

        Assert.Equal(UpdateAck.Accepted, result.Outcome);
        var ask = Assert.Single(control.Status().UpdateAsks!);
        Assert.Equal((request, UpdateAskKind.Install), (ask.Request, ask.Kind));
        Assert.StartsWith("pwa ", ask.By);
        Assert.Equal(1, Volatile.Read(ref asksChanged));
        // The same press sent again is the same ask.
        Assert.Equal(UpdateAck.Accepted, Read<UpdateCommandResult>(Result(await AskAsync(ch, UpdateProtocol.Install, request))).Outcome);
        Assert.Single(control.Status().UpdateAsks!);

        await ReportAsync(Reported with { Phase = UpdatePhase.Waiting, Request = request }, request);
        Assert.Null(control.Status().UpdateAsks);
        Assert.Equal(2, Volatile.Read(ref asksChanged));
    }

    [Fact]
    public async Task The_Windows_app_on_another_machine_may_ask_too_but_a_machine_may_not()
    {
        await ReportAsync(Reported);
        var fromApp = await OpenAsync("Desk app", "app");
        Assert.Equal(UpdateAck.Accepted, Read<UpdateCommandResult>(Result(await AskAsync(fromApp, UpdateProtocol.Check))).Outcome);

        // Another machine is where agents act: its host may read, never press.
        var fromMachine = await OpenAsync("Other host", "host");
        Assert.Equal(Reported, Read<UpdateState>(Result(await fromMachine.RequestAsync(UpdateProtocol.Get, new { }))));
        var refused = Read<UpdateCommandResult>(Result(await AskAsync(fromMachine, UpdateProtocol.Install)));

        Assert.Equal(UpdateAck.Refused, refused.Outcome);
        Assert.Contains("only a person's", refused.Reason);
        Assert.Equal([UpdateAskKind.Check], control.Status().UpdateAsks!.Select(a => a.Kind));
    }

    [Fact]
    public async Task With_no_app_running_or_an_app_that_cannot_update_a_press_is_refused_and_nothing_is_asked()
    {
        var ch = await OpenAsync();
        var none = Read<UpdateCommandResult>(Result(await AskAsync(ch, UpdateProtocol.Install)));
        Assert.Equal((UpdateAck.Refused, UpdateService.NoApp), (none.Outcome, none.Reason));

        await ReportAsync(new UpdateState { Installer = false, Unavailable = "this copy of the app was not installed by its updater, so it cannot update" });
        var dev = Read<UpdateCommandResult>(Result(await AskAsync(ch, UpdateProtocol.Install)));
        Assert.Equal(UpdateAck.Refused, dev.Outcome);
        Assert.Contains("not installed by its updater", dev.Reason);
        Assert.Null(control.Status().UpdateAsks);
    }

    [Fact]
    public async Task A_read_only_machine_offers_no_updates_and_serves_none_even_to_read()
    {
        await ReportAsync(Reported);
        readOnly = "delivery.readOnly is set in config.json";
        var ch = await OpenAsync();

        var hello = Result(await ch.RequestAsync(Ops.Hello, new { }));
        Assert.DoesNotContain(UpdateProtocol.Capability, hello.GetProperty("capabilities").EnumerateArray().Select(c => c.GetString()));
        Assert.Contains("read-only", Error(await ch.RequestAsync(UpdateProtocol.Get, new { })));
        Assert.Contains("read-only", Error(await AskAsync(ch, UpdateProtocol.Install)));
        Assert.Null(control.Status().UpdateAsks);
    }

    [Fact]
    public async Task Nothing_but_the_relay_may_ask_this_machines_pipe_is_where_agents_are()
    {
        await ReportAsync(Reported);
        var service = new SessionService(source, new MachineInfo("host-id", "Test host", "windows", "0.4.0")) { Updates = updates };

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => service.HandleAsync(new AppChannel(), UpdateProtocol.Install,
            JsonSerializer.SerializeToElement(new UpdateCommand(Guid.NewGuid().ToString()), ProtocolJson.Options)));

        Assert.Equal(UpdateService.ThroughTheRelay, refused.Message);
        Assert.Null(control.Status().UpdateAsks);
        var hello = (HelloResult)(await service.HandleAsync(new AppChannel(), Ops.Hello, default))!;
        Assert.DoesNotContain(UpdateProtocol.Capability, hello.Capabilities);
    }

    [Fact]
    public async Task A_press_needs_a_request_and_one_the_app_never_takes_lapses()
    {
        await ReportAsync(Reported);
        var ch = await OpenAsync();
        Assert.Contains("needs a request", Error(await ch.RequestAsync(UpdateProtocol.Install, new { request = "not-a-uuid" })));

        await AskAsync(ch, UpdateProtocol.Install);
        Assert.Single(control.Status().UpdateAsks!);
        time.Advance(UpdateService.AskKept + TimeSpan.FromSeconds(1));
        Assert.Null(control.Status().UpdateAsks);
    }

    // The app's end of the pipe, which a test closes.
    sealed class AppChannel : IProtocolChannel
    {
        readonly TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string PeerId => "local-app";
        public string PeerName => "app";
        public string PeerKind => "local";
        public bool IsOpen => !closed.Task.IsCompleted;
        public Task Closed => closed.Task;
        public Task<long> SendEventAsync(string name, object? data = null, CancellationToken ct = default) => Task.FromResult(0L);
        public void Close() => closed.TrySetResult();
    }
}
