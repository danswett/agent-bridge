using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using AgentBridge.Host;
using AgentBridge.Host.Core;
using AgentBridge.Host.Core.Intake;
using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Local;
using AgentBridge.Protocol;
using Microsoft.Extensions.Time.Testing;
using Xunit;

public sealed class AgentVerificationTests
{
    readonly FakeTimeProvider time = new(new DateTimeOffset(2026, 11, 4, 18, 0, 0, TimeSpan.Zero));
    readonly Source source = new();
    readonly Notices hooks = new();
    readonly Launcher launcher = new();
    string? blocked;
    const string SessionId = "11111111-2222-4333-8444-555555555555";
    AgentVerificationService Service(CancellationToken stopping = default) =>
        new(source, launcher, hooks, () => blocked, stopping: stopping, time: time, poll: TimeSpan.FromMilliseconds(50));
    static AgentVerificationRequest Request(string agent = "copilot") => new(Guid.NewGuid().ToString("D"), agent, "Test", Confirmed: true);

    // The bound counts polls, not wall-clock seconds: a CI runner that stalls the whole process
    // for 20 s must not fail a test whose service is merely waiting to be scheduled. A real hang
    // still ends here, and the CI hang blame covers a wait that never returns at all.
    async Task Wait(Func<bool> done)
    {
        for (var polls = 0; !done(); polls++)
        {
            if (polls == 20_000) { throw new TimeoutException("The awaited condition never held."); }
            time.Advance(TimeSpan.FromMilliseconds(100));
            await Task.Delay(5);
        }
    }

    Session Answer(LaunchRequest request, string? response = null)
    {
        time.Advance(TimeSpan.FromSeconds(1));
        var session = new Session
        {
            Id = SessionId,
            Agent = request.Agent == "agency" ? AgentKind.Copilot : Enum.Parse<AgentKind>(request.Agent!, true),
            Status = SessionStatus.Idle, StatusSince = time.GetUtcNow(), UpdatedAt = time.GetUtcNow(),
            StartedAt = time.GetUtcNow(), ProcessId = 123,
            Activity = new() { UserMessage = request.Prompt, Response = response ?? Challenge(request) },
        };
        source.Sessions[SessionId] = session;
        source.Processes[SessionId] = new(123, time.GetUtcNow());
        return session;
    }

    static string Challenge(LaunchRequest request) => Regex.Match(request.Prompt!, "AGENT_BRIDGE_VERIFY_[0-9a-f]{32}").Value;
    HookNotice Hook(string agent = "copilot", string id = SessionId, int pid = 123) =>
        new(agent, "agent_stop", null, id, null, null, null, default, [pid], time.GetUtcNow(), time.GetUtcNow());

    [Theory]
    [InlineData("copilot")]
    [InlineData("agency")]
    [InlineData("claude")]
    [InlineData("codex")]
    public async Task Only_a_new_bound_session_with_its_own_reply_and_correlated_hook_verifies_the_selection(string agent)
    {
        await using var service = Service();
        var request = Request(agent);
        service.Start(request);
        var launch = await launcher.Called.Task;
        Assert.Equal(LaunchProtocol.Ask, launch.Permissions);
        Assert.Null(launch.Resume);
        Assert.Null(launch.Transfer);
        Assert.Null(launch.Attachments);
        Assert.Contains("Do not use tools", launch.Prompt);
        Assert.Equal(1, hooks.Subscribers);
        Answer(launch);
        hooks.Send(Hook(agent == "agency" ? "copilot" : agent));
        launcher.Result.SetResult(new(LaunchOutcome.Started) { Session = SessionId });

        await Wait(() => service.Snapshot.Single().State == VerificationState.Verified);

        var report = service.Snapshot.Single();
        Assert.Equal(request.Workspace, report.Workspace);
        Assert.Equal(agent, report.Agent);
        Assert.Equal(SessionId, report.Session);
        Assert.NotNull(report.DiscoveredAt);
        Assert.NotNull(report.ProviderObservedAt);
        Assert.NotNull(report.HookReceivedAt);
        Assert.Equal(ReadinessState.Working, report.Launch.State);
        Assert.Equal(ReadinessState.Working, report.Discovery.State);
        Assert.Equal(ReadinessState.Working, report.Provider.State);
        Assert.Equal(ReadinessState.Working, report.Hooks.State);
        Assert.Equal(report, service.Start(request));
        Assert.Equal(1, launcher.Calls);
        await Wait(() => hooks.Subscribers == 0);

        source.Processes[SessionId] = new(123, time.GetUtcNow().AddSeconds(1));
        Assert.Equal(VerificationState.NeedsAction, service.Snapshot.Single().State);
        source.Processes[SessionId] = new(123, report.DiscoveredAt);
        time.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal(VerificationState.NeedsAction, service.Snapshot.Single().State);
    }

    [Fact]
    public async Task An_unconfirmed_launch_keeps_observing_but_cannot_bind_an_arbitrary_new_session()
    {
        await using var service = Service();
        var request = Request();
        service.Start(request);
        var launch = await launcher.Called.Task;
        Answer(launch);
        hooks.Send(Hook());
        launcher.Result.SetResult(new(LaunchOutcome.Unconfirmed, "Answer folder trust in its window."));
        await Wait(() => service.Snapshot.Single().Launch.State == ReadinessState.NeedsAction);
        Assert.Null(service.Snapshot.Single().Session);
        Assert.Equal(VerificationState.Running, service.Snapshot.Single().State);

        launcher.Register(Guid.Parse(request.Request), SessionId);
        await Wait(() => service.Snapshot.Single().State == VerificationState.Verified);
    }

    [Theory]
    [InlineData("process exited")]
    [InlineData("configuration changed")]
    [InlineData("evidence expired")]
    public async Task Retrying_a_retained_run_revalidates_its_own_evidence_after_a_newer_test_replaces_it(string invalid)
    {
        await using var service = Service();
        var first = Request();
        service.Start(first);
        var launch = await launcher.Called.Task;
        Answer(launch);
        hooks.Send(Hook());
        launcher.Result.SetResult(new(LaunchOutcome.Started) { Session = SessionId });
        await Wait(() => service.Snapshot.Single().State == VerificationState.Verified && hooks.Subscribers == 0);

        var second = Request();
        service.Start(second);
        await Wait(() => service.Snapshot.Single().State == VerificationState.NeedsAction);
        Assert.Equal(second.Request, service.Snapshot.Single().Request);
        Assert.Equal(VerificationState.Verified, service.Start(first).State);
        switch (invalid)
        {
            case "process exited": source.Processes.TryRemove(SessionId, out _); break;
            case "configuration changed": blocked = "Host restart required."; break;
            case "evidence expired": time.Advance(TimeSpan.FromMinutes(11)); break;
            default: throw new ArgumentOutOfRangeException(nameof(invalid));
        }

        Assert.Equal(VerificationState.NeedsAction, service.Start(first).State);
        Assert.Equal(VerificationState.NeedsAction, service.Cancel(first.Request).State);
        Assert.Equal(2, launcher.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_new_process_waiting_for_registration_keeps_the_same_test_open_until_discovery_clears_or_times_out(bool timeout)
    {
        await using var service = Service();
        var request = Request();
        service.Start(request);
        var launch = await launcher.Called.Task;
        source.Held = new("A new CLI process has not registered its session yet.", [AgentKind.Copilot]);
        launcher.Result.SetResult(new(LaunchOutcome.Unconfirmed, "Answer folder trust in its window."));
        await Wait(() => service.Snapshot.Single().Detail.Contains("Waiting for session discovery", StringComparison.Ordinal));
        Assert.Equal(VerificationState.Running, service.Snapshot.Single().State);

        if (timeout)
        {
            time.Advance(TimeSpan.FromMinutes(9));
            await Wait(() => service.Snapshot.Single().State == VerificationState.NeedsAction);
            Assert.Contains("timed out", service.Snapshot.Single().Detail);
        }
        else
        {
            Answer(launch);
            hooks.Send(Hook());
            launcher.Register(Guid.Parse(request.Request), SessionId);
            time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(20);
            Assert.Equal(VerificationState.Running, service.Snapshot.Single().State);
            Assert.Equal(ReadinessState.Unknown, service.Snapshot.Single().Discovery.State);
            source.Held = null;
            await Wait(() => service.Snapshot.Single().State == VerificationState.Verified);
        }
        Assert.Equal(1, launcher.Calls);
        await Wait(() => hooks.Subscribers == 0);
    }

    [Theory]
    [InlineData("old")]
    [InlineData("unknown time")]
    [InlineData("future")]
    [InlineData("other session")]
    [InlineData("other process")]
    [InlineData("other agent")]
    public async Task Old_unattributed_or_unrelated_hooks_cannot_complete_a_new_test(string invalid)
    {
        await using var service = Service();
        var started = service.Start(Request());
        var launch = await launcher.Called.Task;
        Answer(launch);
        var notice = Hook();
        notice = invalid switch
        {
            "old" => notice with { ReceivedAt = started.StartedAt.AddSeconds(-1) },
            "unknown time" => notice with { ReceivedAt = null },
            "future" => notice with { ReceivedAt = time.GetUtcNow().AddMinutes(1) },
            "other session" => notice with { SessionId = Guid.NewGuid().ToString("D") },
            "other process" => notice with { Ancestors = [456] },
            "other agent" => notice with { Agent = "claude" },
            _ => throw new ArgumentOutOfRangeException(nameof(invalid)),
        };
        hooks.Send(notice);
        launcher.Result.SetResult(new(LaunchOutcome.Started) { Session = SessionId });
        await Wait(() => service.Snapshot.Single().Provider.State == ReadinessState.Working);
        Assert.Equal(VerificationState.Running, service.Snapshot.Single().State);
        Assert.Equal(ReadinessState.Unknown, service.Snapshot.Single().Hooks.State);

        hooks.Send(Hook());
        await Wait(() => service.Snapshot.Single().State == VerificationState.Verified);
    }

    [Fact]
    public async Task A_prompt_echo_or_delegate_response_is_not_a_main_provider_reply()
    {
        await using var service = Service();
        service.Start(Request());
        var launch = await launcher.Called.Task;
        var session = Answer(launch, response: launch.Prompt);
        hooks.Send(Hook());
        launcher.Result.SetResult(new(LaunchOutcome.Started) { Session = SessionId });
        await Wait(() => service.Snapshot.Single().Hooks.State == ReadinessState.Working);
        Assert.Equal(ReadinessState.Unknown, service.Snapshot.Single().Provider.State);
        source.Sessions[SessionId] = session with
        {
            Activity = session.Activity with { Response = Challenge(launch), Source = new() { Response = "delegate" } },
        };
        time.Advance(TimeSpan.FromSeconds(1));
        await Task.Delay(20);
        Assert.Equal(ReadinessState.Unknown, service.Snapshot.Single().Provider.State);

        source.Sessions[SessionId] = session with
        {
            Activity = session.Activity with
            {
                Response = "A delegate's different reply", Source = new() { Response = "delegate" },
                Own = new() { Response = Challenge(launch), At = time.GetUtcNow() },
            },
        };
        await Wait(() => service.Snapshot.Single().State == VerificationState.Verified);
    }

    [Fact]
    public async Task An_existing_session_cannot_be_relabelled_as_a_new_test()
    {
        source.Sessions[SessionId] = new()
        {
            Id = SessionId, Agent = AgentKind.Copilot, Status = SessionStatus.Idle,
            StatusSince = time.GetUtcNow(), UpdatedAt = time.GetUtcNow(), Activity = new(),
        };
        await using var service = Service();
        service.Start(Request());
        var launch = await launcher.Called.Task;
        Answer(launch);        hooks.Send(Hook());
        launcher.Result.SetResult(new(LaunchOutcome.Started) { Session = SessionId });
        await Wait(() => service.Snapshot.Single().State == VerificationState.NeedsAction);
        Assert.Contains("existing session", service.Snapshot.Single().Detail);
    }

    [Fact]
    public async Task Consent_and_current_configuration_are_required_before_any_launch_and_changes_stop_observation()
    {
        await using var service = Service();
        Assert.Throws<ArgumentException>(() => service.Start(Request() with { Confirmed = false }));
        blocked = "Host restart required after setup.";
        Assert.Throws<InvalidOperationException>(() => service.Start(Request()));
        Assert.Equal(0, launcher.Calls);
        blocked = null;
        var request = Request();
        service.Start(request);
        await launcher.Called.Task;
        Assert.Throws<InvalidOperationException>(() => service.Start(Request("claude")));
        Assert.Throws<ArgumentException>(() => service.Start(request with { Workspace = "Different" }));
        Assert.Equal(1, launcher.Calls);

        blocked = "Workspace authorization changed.";
        await Wait(() => service.Snapshot.Single().State == VerificationState.Blocked);
        await Wait(() => hooks.Subscribers == 0);
        Assert.Contains("authorization changed", service.Snapshot.Single().Detail);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_and_shutdown_stop_observation_without_claiming_to_undo_a_launch(bool shutdown)
    {
        using var stop = new CancellationTokenSource();
        await using var service = Service(stop.Token);
        var request = Request();
        service.Start(request);
        await launcher.Called.Task;
        if (shutdown) { stop.Cancel(); }
        else { Assert.Equal(VerificationState.Cancelled, service.Cancel(request.Request).State); }

        await Wait(() => service.Snapshot.Single().State == VerificationState.Cancelled && hooks.Subscribers == 0);
        Assert.Contains("may still open", service.Snapshot.Single().Detail);
        launcher.Result.SetResult(new(LaunchOutcome.Started) { Session = SessionId });
        Assert.Equal(VerificationState.Cancelled, service.Snapshot.Single().State);
        Assert.Equal(1, launcher.Calls);
    }

    [Fact]
    public async Task A_timeout_does_not_convert_missing_provider_or_hook_evidence_into_success()
    {
        await using var service = Service();
        service.Start(Request());
        await launcher.Called.Task;
        time.Advance(TimeSpan.FromMinutes(9));
        await Wait(() => service.Snapshot.Single().State == VerificationState.NeedsAction);
        Assert.Contains("timed out", service.Snapshot.Single().Detail);
        Assert.Equal(ReadinessState.Unknown, service.Snapshot.Single().Provider.State);
        Assert.Equal(ReadinessState.Unknown, service.Snapshot.Single().Hooks.State);
        await Wait(() => hooks.Subscribers == 0);
    }

    internal sealed class Launcher : ISessionLauncher
    {
        public bool Available => true;
        public TaskCompletionSource<LaunchRequest> Called { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<LaunchResult> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;
        public event Action<Guid, string>? RequestRegistered;
        public void Register(Guid request, string id) => RequestRegistered?.Invoke(request, id);
        public Task<LaunchOptions> OptionsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LaunchResult> LaunchAsync(LaunchRequest request, string peer, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LaunchResult> LaunchVerificationAsync(LaunchRequest request, string peer, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            Called.TrySetResult(request);
            return Result.Task;
        }
    }

    internal sealed class Notices : IHookNotices
    {
        readonly Channel<HookNotice> channel = Channel.CreateUnbounded<HookNotice>();
        public int Subscribers;
        public ChannelReader<HookNotice> Subscribe() { Interlocked.Increment(ref Subscribers); return channel.Reader; }
        public void Unsubscribe(ChannelReader<HookNotice> reader) => Interlocked.Decrement(ref Subscribers);
        public void Send(HookNotice notice) => Assert.True(channel.Writer.TryWrite(notice));
    }

    sealed class Source : ISessionSource
    {
        public ConcurrentDictionary<string, Session> Sessions { get; } = new();
        public ConcurrentDictionary<string, SessionProcess> Processes { get; } = new();
        public DiscoveryHeld? Held { get; set; }
        public long Revision => 0;
        public event EventHandler<SessionChange>? Changed { add { } remove { } }
        public IReadOnlyList<Session> Snapshot() => Sessions.Values.ToArray();
        public SessionList List(string? cursor = null) => new(Snapshot(), 0);
        public Session? Get(string id) => Sessions.GetValueOrDefault(id);
        public SessionProcess? ProcessOf(string id) => Processes.GetValueOrDefault(id);
        public SessionSubscription Subscribe() => throw new NotSupportedException();
        public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
