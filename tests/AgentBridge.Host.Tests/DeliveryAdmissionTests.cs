using System.Text.RegularExpressions;
using AgentBridge.Host.Delivery;
using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Protocol;
using Microsoft.Extensions.Time.Testing;
using Xunit;

public sealed class DeliveryAdmissionTests
{
    [Fact]
    public async Task An_answer_is_refused_before_any_delivery_work_while_the_shared_gate_is_fenced()
    {
        var time = new FakeTimeProvider();
        var gate = new DeliveryGate(time);
        using var fence = gate.TryExclude(TimeSpan.FromSeconds(30), out _);
        var service = new AnswerService(gate, null!, null!, null!, null!, null!, new AnswerOptions { CopilotHome = "." });
        var result = await service.AnswerAsync(new AnswerRequest { Id = "unvisited", Question = "unvisited", Request = Guid.NewGuid().ToString() });
        Assert.Equal(AnswerOutcome.Busy, result.Outcome);
    }

    [Fact]
    public async Task A_reply_is_refused_before_any_delivery_work_while_the_shared_gate_is_fenced()
    {
        var time = new FakeTimeProvider();
        var gate = new DeliveryGate(time);
        using var fence = gate.TryExclude(TimeSpan.FromSeconds(30), out _);
        var service = new ReplyService(gate, null!, null!, null!, null!, null!, new ReplyOptions { CopilotHome = ".", PreRead = false });
        var result = await service.ReplyAsync(new ReplyRequest { Id = "unvisited", Text = "retained draft", Request = Guid.NewGuid().ToString() });
        Assert.Equal(AnswerOutcome.Busy, result.Outcome);
    }

    [Fact]
    public async Task A_stop_is_refused_before_any_delivery_work_while_the_shared_gate_is_fenced()
    {
        var time = new FakeTimeProvider();
        var gate = new DeliveryGate(time);
        using var fence = gate.TryExclude(TimeSpan.FromSeconds(30), out _);
        var service = new StopService(gate, null!, null!, null!, null!, null!, new StopOptions { CopilotHome = "." });
        var result = await service.StopAsync(new StopRequest { Id = "unvisited", Request = Guid.NewGuid().ToString() });
        Assert.Equal(StopOutcome.Busy, result.Outcome);
    }

    static LaunchService Launcher(DeliveryGate gate) => new(gate, null!, null!, null!, null!, null!, null!,
        new LaunchServiceOptions { CopilotHome = ".", ClaudeHome = ".", CodexHome = "." });

    [Fact]
    public async Task A_launch_is_refused_before_any_delivery_work_while_the_shared_gate_is_fenced()
    {
        var time = new FakeTimeProvider();
        var gate = new DeliveryGate(time);
        using var fence = gate.TryExclude(TimeSpan.FromSeconds(30), out _);
        var result = await Launcher(gate).LaunchAsync(new LaunchRequest { Request = Guid.NewGuid().ToString() }, "person");
        Assert.Equal(LaunchOutcome.Busy, result.Outcome);
    }

    [Fact]
    public async Task A_folder_trust_answer_is_refused_before_any_delivery_work_while_the_shared_gate_is_fenced()
    {
        var time = new FakeTimeProvider();
        var gate = new DeliveryGate(time);
        using var fence = gate.TryExclude(TimeSpan.FromSeconds(30), out _);
        var result = await Launcher(gate).AnswerTrustAsync(Guid.NewGuid(), "unvisited", true, "device", "person");
        Assert.Equal(TrustAnswerOutcome.Refused, result.Outcome);
        Assert.Equal("the host is preparing a departure", result.Reason);
    }

    [Fact]
    public void Production_constructors_require_a_gate_and_Program_wires_the_same_instance_to_every_path()
    {
        foreach (var type in new[] { typeof(AnswerService), typeof(ReplyService), typeof(StopService), typeof(LaunchService) })
        {
            var constructor = Assert.Single(type.GetConstructors());
            var gate = Assert.Single(constructor.GetParameters(), p => p.ParameterType == typeof(DeliveryGate));
            Assert.False(gate.IsOptional);
        }
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(root.FullName, "AgentBridge.slnx"))) { root = root.Parent ?? throw new IOException("repository root not found"); }
        var program = File.ReadAllText(Path.Combine(root.FullName, "src", "AgentBridge.Host", "Program.cs"));
        Assert.Single(Regex.Matches(program, @"new DeliveryGate\(").Cast<Match>());
        Assert.Contains("relayBlobs, codexUnsettled, deliveryGate)", program);
        Assert.Matches(@"Launcher\(source, g5, attachments, transfers, deliveryGate(, restore)?(, agentCredentials)?(, diagnosticsJournal)?\)", program);
        // Launches only carry an agent credential when the host hands its store to the launcher and the launcher on to LaunchService (#720).
        Assert.Contains("restore, agentCredentials, diagnosticsJournal)", program);
        Assert.Contains("Log, attachments, credentials: credentials, account: new CopilotLaunchAccount(launchHttp, log: Log))", program);
        // The lease every planned departure takes is composed with remote management (#801), over the same gate.
        Assert.Contains("ManageComposition.For(settings, identity.Key, identity.Folder, deliveryGate,", program);
        Assert.Contains("new LifecycleCoordinator(delivery,", File.ReadAllText(Path.Combine(root.FullName, "src", "AgentBridge.Host", "ManageComposition.cs")));
        foreach (var name in new[] { "AnswerService", "ReplyService", "StopService" }) { Assert.Contains($"new {name}(gate,", program); }
        Assert.Contains("new LaunchService(deliveryGate,", program);
    }
}
