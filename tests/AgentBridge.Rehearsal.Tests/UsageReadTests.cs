using System.Text.Json;
using System.Threading.Channels;
using AgentBridge.Protocol;
using AgentBridge.Rehearsal;
using AgentBridge.Transport;
using Xunit;

namespace AgentBridge.Rehearsal.Tests;

public class UsageReadTests
{
    static readonly DateTimeOffset Earlier = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);
    static readonly DateTimeOffset Later = Earlier.AddMinutes(5);

    static AgentUsage Reading(AgentKind agent, string account, double percent, DateTimeOffset at, UsageReader? reader = null) =>
        new()
        {
            Agent = agent,
            Label = agent.ToString(),
            Account = account,
            Source = UsageSource.Api,
            MeasuredAt = at,
            ReadBy = reader,
            Windows = [new UsageWindow { Key = "plan", Label = "Plan", Percent = percent }],
        };

    static AgentUsage Failure(AgentKind agent, string account, DateTimeOffset at, string error) =>
        new()
        {
            Agent = agent,
            Label = agent.ToString(),
            Account = account,
            Source = UsageSource.None,
            MeasuredAt = at,
            Error = error,
        };

    [Fact]
    public async Task The_helper_reads_the_shared_report_and_prints_one_app_ranked_figure_per_allowance()
    {
        var peer = new UsageReader(new string('a', 32), "Peer");
        var shared =
            new UsageReport([
                Reading(AgentKind.Copilot, "octo", 18, Earlier),
                Reading(AgentKind.Copilot, "octo", 42, Later, peer),
                Failure(AgentKind.Copilot, "octo", Later.AddMinutes(1), "newer failure"),
                Reading(AgentKind.Codex, "codex-account", 31, Earlier, peer),
                Failure(AgentKind.Claude, "claude-account", Later, "not signed in"),
            ]);
        var localOnly = new UsageReport([Reading(AgentKind.Codex, "codex-account", 99, Later)]);
        var pipe = new FakePipe(shared, localOnly);
        using var output = new StringWriter();

        var exitCode = await UsageRead.RunAsync(Cli.Parse(["usage-read"]).Args, pipe, output, CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(new[] { Ops.Hello, UsageProtocol.Get }, pipe.Calls.Select(call => call.Operation));
        var request = Assert.IsType<UsageRequest>(Assert.Single(pipe.Calls, call => call.Operation == UsageProtocol.Get).Data);
        Assert.True(request.Shared);

        var report = JsonSerializer.Deserialize<UsageReport>(output.ToString(), ProtocolJson.Options)!;
        Assert.Equal(3, report.Agents.Count);
        var copilot = Assert.Single(report.Agents, agent => agent.Agent == AgentKind.Copilot);
        Assert.Equal(42, Assert.Single(copilot.Windows).Percent);
        Assert.Equal(peer, copilot.ReadBy);
        Assert.Equal(31, Assert.Single(Assert.Single(report.Agents, agent => agent.Agent == AgentKind.Codex).Windows).Percent);
        var claude = Assert.Single(report.Agents, agent => agent.Agent == AgentKind.Claude);
        Assert.Empty(claude.Windows);
        Assert.Equal("not signed in", claude.Error);
    }

    [Fact]
    public async Task A_pipe_error_is_reported_with_a_nonzero_exit_code()
    {
        var pipe = new FakePipe(UsageReport.Empty, UsageReport.Empty, "usage unavailable");
        using var output = new StringWriter();

        var exitCode = await UsageRead.RunAsync(Cli.Parse(["usage-read"]).Args, pipe, output, CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.Contains("usage unavailable", output.ToString(), StringComparison.Ordinal);
    }

    sealed class FakePipe(UsageReport shared, UsageReport localOnly, string? usageError = null) : IClientChannel
    {
        readonly Channel<ChannelEvent> events = Channel.CreateUnbounded<ChannelEvent>();
        public List<(string Operation, object? Data)> Calls { get; } = [];
        public string PeerId => "local-test";
        public string PeerName => "Test";
        public string PeerKind => "host";
        public bool IsOpen => true;
        public Task Closed => Task.CompletedTask;
        public Exception? CloseReason => null;
        public ChannelReader<ChannelEvent> Events => events.Reader;

        public Task<JsonElement> RequestAsync(string op, object? data = null, TimeSpan? timeout = null, CancellationToken ct = default, Action<JsonElement>? progress = null)
        {
            Calls.Add((op, data));
            if (op == Ops.Hello)
            {
                var hello = new HelloResult(1, new MachineInfo(new string('0', 32), "Test", "test", "test"), [UsageProtocol.Capability, UsageProtocol.SharedCapability]);
                return Task.FromResult(JsonSerializer.SerializeToElement(new { ok = true, result = hello }, ProtocolJson.Options));
            }
            if (op == UsageProtocol.Get && usageError is not null)
            {
                return Task.FromResult(JsonSerializer.SerializeToElement(new { ok = false, error = usageError }, ProtocolJson.Options));
            }
            if (op == UsageProtocol.Get)
            {
                var report = data is UsageRequest { Shared: true } ? shared : localOnly;
                return Task.FromResult(JsonSerializer.SerializeToElement(new { ok = true, result = report }, ProtocolJson.Options));
            }
            throw new InvalidOperationException($"Unexpected operation {op}");
        }

        public async Task<(JsonElement Response, long EventsBefore)> RequestOrderedAsync(string op, object? data = null, TimeSpan? timeout = null, CancellationToken ct = default, Action<JsonElement>? progress = null) =>
            (await RequestAsync(op, data, timeout, ct, progress), -1);

        public Task<long> SendEventAsync(string name, object? data = null, CancellationToken ct = default) => Task.FromResult(0L);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
