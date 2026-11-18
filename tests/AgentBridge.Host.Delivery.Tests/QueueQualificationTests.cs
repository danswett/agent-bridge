using System.Text;
using System.Text.Json;
using AgentBridge.Host.Delivery;
using AgentBridge.Host.Delivery.Copilot;
using Microsoft.Extensions.Time.Testing;
using Xunit;

public sealed class QueueQualificationTests
{
    sealed class Launch
    {
        public readonly FakeTimeProvider Clock = new();
        public readonly List<string> Events = [Event("assistant.turn_start")];
        public string? Identity = "identity 42 100 200:44:101 7 " + B64(@"C:\agent\cli.exe") + " " + B64("1.2") + " " + new string('a', 64)
            + " " + B64(@"C:\agent\injector.exe") + " " + B64("2.3") + " " + new string('b', 64) + " " + B64(KeyPlan.Header);
        public Action? OnRead;
        public Task<string?> Capture() => Task.FromResult(Identity);
        public IReadOnlyList<string> Read() { var rows = Events.ToArray(); Events.Clear(); OnRead?.Invoke(); return rows; }
        public Task<QueueQualification> Host() => QueueQualification.CaptureAtLaunchAsync(Capture, Read, Clock);
    }

    static string B64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
    static string Event(string type, string? content = null, string? delivery = null, string? agentId = null) =>
        JsonSerializer.Serialize(new { type, agentId, data = new { content, delivery } });

    [Fact]
    public async Task Only_the_exact_fresh_raw_queued_challenge_on_the_bound_working_target_verifies_once()
    {
        var launch = new Launch(); var host = await launch.Host();
        var challenge = Assert.IsType<QueueQualification.Challenge>(await host.IssueAsync(TimeSpan.FromSeconds(5)));
        launch.Events.Add(Event("user.message", (string)challenge.Text, "queued"));
        Assert.Equal("Verified", (await host.ObserveAsync(challenge)).ToString());
        launch.Events.Add(Event("user.message", (string)challenge.Text, "queued"));
        Assert.Equal("Unknown", (await host.ObserveAsync(challenge)).ToString());
    }

    [Theory]
    [InlineData(1, "43")][InlineData(2, "101")][InlineData(3, "201:44:101")][InlineData(4, "8")]
    [InlineData(3, "200:45:101")][InlineData(3, "200:44:102")]
    [InlineData(5, "QzpcYWdlbnRcb3RoZXIuZXhl")][InlineData(6, "OS4w")][InlineData(7, null)]
    [InlineData(8, "QzpcYWdlbnRcb3RoZXIuZXhl")][InlineData(9, "OS4w")][InlineData(10, null)][InlineData(11, "b3RoZXI=")]
    public async Task A_different_target_reused_PID_or_changed_CLI_injector_console_or_protocol_stays_unknown(int field, string? value)
    {
        var launch = new Launch(); var host = await launch.Host();
        var challenge = Assert.IsType<QueueQualification.Challenge>(await host.IssueAsync(TimeSpan.FromSeconds(5)));
        var parts = launch.Identity!.Split(' '); parts[field] = value ?? new string('c', 64); launch.Identity = string.Join(' ', parts);
        launch.Events.Add(Event("user.message", (string)challenge.Text, "queued"));
        Assert.Equal("Unknown", (await host.ObserveAsync(challenge)).ToString());
    }

    [Fact]
    public async Task A_challenge_from_another_host_cannot_qualify_even_the_same_target()
    {
        var launch = new Launch(); var owner = await launch.Host(); var other = await launch.Host();
        var challenge = Assert.IsType<QueueQualification.Challenge>(await owner.IssueAsync(TimeSpan.FromSeconds(5)));
        launch.Events.Add(Event("user.message", (string)challenge.Text, "queued"));
        Assert.Equal("Unknown", (await other.ObserveAsync(challenge)).ToString());
        Assert.Equal("Verified", (await owner.ObserveAsync(challenge)).ToString());
    }

    [Fact]
    public async Task An_expired_challenge_stays_unknown_even_with_an_exact_queued_event()
    {
        var launch = new Launch(); var host = await launch.Host();
        var challenge = Assert.IsType<QueueQualification.Challenge>(await host.IssueAsync(TimeSpan.FromSeconds(5)));
        launch.Clock.Advance(TimeSpan.FromSeconds(5));
        launch.Events.Add(Event("user.message", (string)challenge.Text, "queued"));
        Assert.Equal("Unknown", (await host.ObserveAsync(challenge)).ToString());
    }

    [Theory]
    [InlineData("user.message", null, null)][InlineData("user.message", "steering", null)]
    [InlineData("user.message", "idle", null)][InlineData("writing", "queued", null)]
    [InlineData("ok", "queued", null)][InlineData("provider.confirmed", "queued", null)]
    [InlineData("user.message", "queued", "helper")]
    public async Task Stripped_delivery_legacy_key_success_hooks_or_subagent_events_never_qualify(string type, string? delivery, string? agentId)
    {
        var launch = new Launch(); var host = await launch.Host();
        var challenge = Assert.IsType<QueueQualification.Challenge>(await host.IssueAsync(TimeSpan.FromSeconds(5)));
        launch.Events.Add(Event(type, (string)challenge.Text, delivery, agentId));
        Assert.Equal("Unknown", (await host.ObserveAsync(challenge)).ToString());
    }

    [Theory]
    [InlineData(null)][InlineData("identity 42")][InlineData("ok")]
    public async Task A_PID_or_incomplete_identity_cannot_issue_a_challenge(string? identity)
    {
        var launch = new Launch { Identity = identity }; var host = await launch.Host();
        Assert.Null(await host.IssueAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task A_target_that_was_not_working_cannot_issue_a_challenge()
    {
        var launch = new Launch(); launch.Events.Clear(); var host = await launch.Host();
        Assert.Null(await host.IssueAsync(TimeSpan.FromSeconds(5)));
    }

    [Theory]
    [InlineData("{")][InlineData("[]")][InlineData("null")]
    public async Task Unreadable_raw_evidence_cannot_qualify_even_beside_a_matching_event(string invalid)
    {
        var launch = new Launch(); var host = await launch.Host();
        var challenge = Assert.IsType<QueueQualification.Challenge>(await host.IssueAsync(TimeSpan.FromSeconds(5)));
        launch.Events.Add(Event("user.message", challenge.Text, "queued")); launch.Events.Add(invalid);
        Assert.Equal("Unknown", (await host.ObserveAsync(challenge)).ToString());
    }

    [Fact]
    public async Task A_queued_event_for_an_old_or_different_nonce_cannot_qualify_a_new_challenge()
    {
        var launch = new Launch(); var host = await launch.Host();
        var old = Assert.IsType<QueueQualification.Challenge>(await host.IssueAsync(TimeSpan.FromSeconds(5)));
        var fresh = Assert.IsType<QueueQualification.Challenge>(await host.IssueAsync(TimeSpan.FromSeconds(5)));
        launch.Events.Add(Event("user.message", old.Text, "queued"));
        Assert.Equal("Unknown", (await host.ObserveAsync(fresh)).ToString());
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task Identity_loss_or_expiry_during_the_raw_read_stays_unknown(bool expire)
    {
        var launch = new Launch(); var host = await launch.Host();
        var challenge = Assert.IsType<QueueQualification.Challenge>(await host.IssueAsync(TimeSpan.FromSeconds(5)));
        launch.OnRead = () => { if (expire) { launch.Clock.Advance(TimeSpan.FromSeconds(5)); } else { launch.Identity = null; } };
        launch.Events.Add(Event("user.message", challenge.Text, "queued"));
        Assert.Equal("Unknown", (await host.ObserveAsync(challenge)).ToString());
    }
}
