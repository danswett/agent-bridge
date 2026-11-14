using System.Text.Json;
using AgentBridge.Host;
using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Protocol;
using AgentBridge.Transport;
using Xunit;

public sealed class LaunchGateAuditTests : LaunchFlowFixture
{
    private protected override bool UsesRelay => false;
    string GateProfile => Path.Combine(f.Root, "profile");
    string GateFolder => Path.Combine(GateProfile, AgentAuditLog.FolderName);

    JsonElement[] LaunchAuditRows() => Directory.Exists(GateFolder)
        ? Directory.GetFiles(GateFolder, "launches-*.jsonl").SelectMany(File.ReadAllLines)
            .Select(line => JsonSerializer.Deserialize<JsonElement>(line)).Where(row => row.TryGetProperty("action", out _)).ToArray()
        : [];

    Func<IProtocolChannel, string, JsonElement, Task<object?>> AuditedLocalLaunch(ISessionLauncher? launcher = null) =>
        LocalApi.Handler(new SessionService(source, new MachineInfo("host-id", machineName, "windows", "0.1.0"),
            launcher: launcher ?? Launcher(Config())), null!, new FixedCallerGuard(new Caller(CallerKind.App, "fixture")),
            audit: new AgentAuditLog(GateProfile, clock, logged.Enqueue));

    static JsonElement GateLaunch(Guid request) => JsonSerializer.SerializeToElement(new
    {
        request, agent = "copilot", workspace = "App", prompt = "private gate prompt",
        run = Guid.NewGuid(), path = "relay", actorKind = "ha-component",
    });

    [Fact]
    public async Task A_fresh_launch_records_intent_before_start_and_one_confirmed_terminal()
    {
        JsonElement[] beforeStart = [];
        starter.BeforeStart = () => beforeStart = LaunchAuditRows();
        var request = Guid.NewGuid();
        var handler = AuditedLocalLaunch();
        var result = Assert.IsType<LaunchResult>(await handler(new GateAuditChannel(), LaunchProtocol.Launch, GateLaunch(request)));

        Assert.Equal(LaunchOutcome.Started, result.Outcome);
        var intent = Assert.Single(beforeStart);
        Assert.Equal("intent", intent.GetProperty("gate").GetProperty("phase").GetString());
        Assert.Equal(request.ToString(), intent.GetProperty("request").GetString());
        Assert.Equal(request.ToString(), intent.GetProperty("target").GetString());
        var rows = LaunchAuditRows();
        Assert.Equal(2, rows.Length);
        var terminal = Assert.Single(rows, row => row.GetProperty("gate").GetProperty("phase").GetString() == "terminal");
        Assert.Equal("started", terminal.GetProperty("outcome").GetString());
        Assert.Equal(result.Session, terminal.GetProperty("target").GetString());
        Assert.Equal(request.ToString(), terminal.GetProperty("request").GetString());
        var operation = intent.GetProperty("gate").GetProperty("operation").GetGuid();
        Assert.NotEqual(Guid.Empty, operation);
        Assert.Equal(operation, terminal.GetProperty("gate").GetProperty("operation").GetGuid());
        Assert.Equal(Guid.Empty, intent.GetProperty("gate").GetProperty("run").GetGuid());
        Assert.Equal(0, GateAuditProjection.Read(GateProfile, Guid.Empty).Totals.Sum(total => total.Successes));
        Assert.DoesNotContain("private gate prompt", string.Join("\n", rows.Select(row => row.GetRawText())));
    }

    [Fact]
    public async Task A_launch_without_a_confirmed_terminal_stays_unresolved()
    {
        starter.Registers = false;
        var handler = AuditedLocalLaunch(Launcher(Config(), wait: TimeSpan.FromMilliseconds(100), follow: TimeSpan.FromMilliseconds(200)));
        var result = Assert.IsType<LaunchResult>(await handler(new GateAuditChannel(), LaunchProtocol.Launch, GateLaunch(Guid.NewGuid())));

        Assert.Equal(LaunchOutcome.Unconfirmed, result.Outcome);
        Assert.Equal("intent", Assert.Single(LaunchAuditRows()).GetProperty("gate").GetProperty("phase").GetString());
        var report = GateAuditProjection.Read(GateProfile, Guid.Empty);
        Assert.Contains("unresolved-intent", report.IncompleteReasons);
        Assert.Equal(0, report.Totals.Sum(total => total.Successes));
    }

    [Fact]
    public async Task An_unwritable_intent_refuses_before_the_starter()
    {
        Directory.CreateDirectory(GateProfile);
        File.WriteAllText(GateFolder, "blocked");
        var handler = AuditedLocalLaunch();
        var result = Assert.IsType<LaunchResult>(await handler(new GateAuditChannel(), LaunchProtocol.Launch, GateLaunch(Guid.NewGuid())));

        Assert.Equal(LaunchOutcome.Refused, result.Outcome);
        Assert.Empty(starter.Specs);
        Assert.Equal("blocked", File.ReadAllText(GateFolder));
    }

    sealed class GateAuditChannel : IProtocolChannel
    {
        public string PeerId => "local-app";
        public string PeerName => "fixture";
        public string PeerKind => "app";
        public bool IsOpen => true;
        public Task Closed => Task.CompletedTask;
        public Task<long> SendEventAsync(string name, object? data = null, CancellationToken ct = default) => Task.FromResult(0L);
    }
}
