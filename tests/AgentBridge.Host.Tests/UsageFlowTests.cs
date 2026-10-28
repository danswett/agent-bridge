using System.Text.Json;
using AgentBridge.Host;
using AgentBridge.Host.Core;
using AgentBridge.Host.Core.Usage;
using AgentBridge.Protocol;
using AgentBridge.Transport;
using AgentBridge.Transport.Tests;
using Microsoft.Extensions.Time.Testing;
using Xunit;

// usage.get, usage.subscribe and hello's machine status, served through the in-process relay
// over a Noise channel to a client, with the agents' allowances from fake collectors.
public sealed class UsageFlowTests : IAsyncLifetime
{
    const string Id = "77777777-7777-4777-8777-777777777777";
    static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);
    static readonly DateTimeOffset Now = new(2026, 10, 28, 20, 0, 0, TimeSpan.Zero);

    readonly Fixture f = new();
    readonly FakeTimeProvider time = new(Now);
    double percent = 40;
    TestRelay relay = null!;
    SessionSource source = null!;
    UsageMonitor monitor = null!;
    UsageService usage = null!;
    Device host = null!;
    Device client = null!;

    public async Task InitializeAsync()
    {
        var events = f.CopilotSession(Id, 100);
        Fixture.Append(events, Copilot.User("go", 1));
        f.Processes.Run(AgentKind.Copilot, 100);
        source = new SessionSource(f.Options(), f.Processes, TimeProvider.System);
        await source.RefreshAsync();
        monitor = new UsageMonitor([new Collector(AgentKind.Copilot, () => Reading(percent))], source,
            new UsageOptions { CopilotHome = f.CopilotHome, ClaudeHome = f.ClaudeHome, CodexHome = f.CodexHome }, time);
        await monitor.PassAsync();
        usage = new UsageService(monitor);
        var service = new SessionService(source, new MachineInfo("host-id", "Test host", "windows", "0.1.0", Dev: true), usage: usage);

        relay = await TestRelay.StartAsync();
        var token = relay.TokenFor(1, "octo");
        host = await relay.ConnectAsync(token, "Test host", "host");
        host.Hub.JsonOptions = ProtocolJson.Options;
        host.Hub.Handler = service.HandleAsync;
        client = await relay.ConnectAsync(token, "Phone", "pwa");
    }

    public async Task DisposeAsync()
    {
        await relay.DisposeAsync();
        await monitor.DisposeAsync();
        await source.DisposeAsync();
        f.Dispose();
    }

    AgentUsage Reading(double used) => new()
    {
        Agent = AgentKind.Copilot,
        Label = "GitHub Copilot",
        Account = "octo",
        Plan = "enterprise",
        Source = UsageSource.Api,
        Windows = [new UsageWindow { Key = "plan", Label = "Plan", Percent = used, Used = used * 15000, Limit = 1500000, Unit = "AIC", StartsAt = Now.AddDays(-27), ResetsAt = Now.AddDays(4) }],
        MeasuredAt = time.GetUtcNow(),
    };

    async Task<SecureChannel> OpenAsync()
    {
        await client.Relay.WaitForPeerAsync(p => p.Id == host.Id, Patience);
        return await client.Hub.OpenAsync(host.Id);
    }

    static JsonElement Result(JsonElement response)
    {
        Assert.True(response.GetProperty("ok").GetBoolean(), response.GetRawText());
        return response.GetProperty("result");
    }

    static async Task<ChannelEvent> NextAsync(SecureChannel ch)
    {
        using var cts = new CancellationTokenSource(Patience);
        return await ch.Events.ReadAsync(cts.Token);
    }

    static async Task QuietAsync(SecureChannel ch)
    {
        using var quiet = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await ch.Events.ReadAsync(quiet.Token));
    }

    // The next read, a quarter of an hour on, with the figure moved or not.
    async Task ReadAgainAsync(double used)
    {
        percent = used;
        time.Advance(TimeSpan.FromMinutes(15));
        await monitor.PassAsync();
    }

    [Fact]
    public async Task Hello_offers_usage_and_says_how_many_sessions_are_live_and_that_the_build_is_dev()
    {
        var ch = await OpenAsync();
        var hello = Result(await ch.RequestAsync(Ops.Hello, new { }));

        Assert.Contains(UsageProtocol.Capability, hello.GetProperty("capabilities").EnumerateArray().Select(c => c.GetString()));
        var machine = hello.GetProperty("machine");
        Assert.Equal("0.1.0", machine.GetProperty("hostVersion").GetString());
        Assert.True(machine.GetProperty("dev").GetBoolean());
        Assert.Equal(1, machine.GetProperty("sessions").GetInt32());
    }

    [Fact]
    public async Task Usage_get_returns_the_latest_readings_in_contract_form()
    {
        var ch = await OpenAsync();
        var report = Result(await ch.RequestAsync(UsageProtocol.Get, new { }));

        var copilot = Assert.Single(report.GetProperty("agents").EnumerateArray());
        Assert.Equal(("copilot", "octo", "api"), (copilot.GetProperty("agent").GetString(), copilot.GetProperty("account").GetString(), copilot.GetProperty("source").GetString()));
        Assert.Equal("2026-10-28T20:00:00.000Z", copilot.GetProperty("measuredAt").GetString());
        var plan = copilot.GetProperty("windows")[0];
        Assert.Equal(40, plan.GetProperty("percent").GetDouble());
        Assert.Equal("2026-10-01T20:00:00.000Z", plan.GetProperty("startsAt").GetString());
        Assert.Equal("2026-11-01T20:00:00.000Z", plan.GetProperty("resetsAt").GetString());
        Assert.False(copilot.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task Subscribing_answers_with_the_report_then_sends_one_update_per_change_and_nothing_while_it_holds_still()
    {
        var ch = await OpenAsync();
        var first = Result(await ch.RequestAsync(UsageProtocol.Subscribe, new { }));
        Assert.Equal(40, first.GetProperty("agents")[0].GetProperty("windows")[0].GetProperty("percent").GetDouble());
        // Subscribing again replaces the subscription rather than adding one.
        Result(await ch.RequestAsync(UsageProtocol.Subscribe, new { }));
        Assert.Equal(1, usage.Subscribers);

        await ReadAgainAsync(41);
        var update = await NextAsync(ch);
        Assert.Equal(UsageProtocol.Update, update.Name);
        Assert.Equal(41, update.Data.GetProperty("agents")[0].GetProperty("windows")[0].GetProperty("percent").GetDouble());
        await QuietAsync(ch);

        await ReadAgainAsync(41);
        await QuietAsync(ch);
    }

    [Fact]
    public async Task A_usage_subscription_ends_with_its_channel()
    {
        var ch = await OpenAsync();
        Result(await ch.RequestAsync(UsageProtocol.Subscribe, new { }));
        Assert.Equal(1, usage.Subscribers);

        await client.DisposeAsync();

        await Wait.UntilAsync(() => usage.Subscribers == 0, "the usage subscription to end with its channel", Patience);
    }

    [Fact]
    public async Task A_host_without_usage_neither_offers_it_nor_answers_it()
    {
        var plain = new SessionService(source, new MachineInfo("host-id", "Test host", "windows", "0.1.0"));
        host.Hub.Handler = plain.HandleAsync;
        var ch = await OpenAsync();

        var hello = Result(await ch.RequestAsync(Ops.Hello, new { }));
        Assert.DoesNotContain(UsageProtocol.Capability, hello.GetProperty("capabilities").EnumerateArray().Select(c => c.GetString()));
        Assert.False(hello.GetProperty("machine").TryGetProperty("dev", out _));
        Assert.False((await ch.RequestAsync(UsageProtocol.Get, new { })).GetProperty("ok").GetBoolean());
    }

    sealed class Collector(AgentKind agent, Func<AgentUsage?> read) : IUsageCollector
    {
        public AgentKind Agent => agent;
        public Task<AgentUsage?> CollectAsync(CancellationToken cancellationToken = default) => Task.FromResult(read());
    }
}
