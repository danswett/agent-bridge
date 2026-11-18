using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentBridge.Host.Delivery;
using AgentBridge.Host.Delivery.Copilot;
using Xunit;

public sealed class QualificationControllerTests
{
    sealed class Probe : IQualificationProbe
    {
        public string? Identity = "identity 42 100 200:44:101 7 " + B64(@"C:\private-fixture\cli.exe") + " " + B64("1.2")
            + " " + new string('a', 64) + " " + B64(@"C:\private-fixture\injector.exe") + " " + B64("2.3")
            + " " + new string('b', 64) + " " + B64(KeyPlan.Header);
        public readonly List<string> Rows = [Event("assistant.turn_start")];
        public QualificationSubmission Submission = QualificationSubmission.Submitted;
        public Action? BeforeFence, AfterSend;
        public string Mode = "queued";
        public string? Nonce;
        public int Effects;
        public Task<string?> CaptureAsync() => Task.FromResult(Identity);
        public IReadOnlyList<string> Read() { var rows = Rows.ToArray(); Rows.Clear(); return rows; }
        public async Task<QualificationSubmission> SendAsync(string text, Func<Task<bool>> fence, CancellationToken cancellationToken)
        {
            BeforeFence?.Invoke();
            if (cancellationToken.IsCancellationRequested || !await fence()) { return QualificationSubmission.Unknown; }
            Nonce = text; Effects++;
            if (Mode != "absent") { Rows.Add(Event("user.message", Mode == "nonce" ? "other" : text, Mode)); }
            if (Mode == "duplicate") { Rows.Clear(); Rows.Add(Event("user.message", text)); Rows.Add(Event("user.message", text)); }
            if (Mode == "ambiguous") { Rows.Clear(); Rows.Add(Event("user.message", text)); Rows.Add(Event("user.message", text, "steering")); }
            if (Mode == "malformed") { Rows.Add("{"); }
            if (Mode == "duplicate-property") { Rows.Clear(); Rows.Add("{\"type\":\"abort\",\"type\":\"user.message\",\"data\":{\"content\":" + JsonSerializer.Serialize(text) + ",\"delivery\":\"queued\"}}"); }
            AfterSend?.Invoke(); return Submission;
        }
    }

    sealed class Receipts : IQualificationReceiptJournal
    {
        public readonly List<QualificationRecord> Entries = [];
        public bool Available = true;
        public void Append(QualificationRecord record)
        {
            if (!Available) { throw new IOException("receipt append failed"); }
            Entries.Add(record);
        }
    }

    sealed class Run
    {
        public readonly QualificationGrantTests.Run Authority = new();
        public readonly Probe Probe = new();
        public readonly Receipts Receipts = new();
        public QualificationController Controller() => new(Authority.Grants(), Probe, Receipts, Authority.Clock);
        public Run() => Authority.Issue();
    }

    static string B64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
    static string Event(string type, string? content = null, string delivery = "queued") =>
        JsonSerializer.Serialize(new { type, data = new { content, delivery } });

    [Fact]
    public async Task A_single_raw_queued_challenge_yields_a_sanitized_hashed_receipt_and_only_current_bound_capability()
    {
        var run = new Run(); var controller = run.Controller();
        var result = await controller.StartAsync(run.Authority.Id);
        Assert.Null(result.Refusal); Assert.Equal(QueueCapability.Verified, result.Capability);
        var record = Assert.IsType<QualificationRecord>(result.Record);
        Assert.Same(record, Assert.Single(run.Receipts.Entries));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(record.Evidence))), record.ContentHash);
        var json = JsonSerializer.Serialize(record);
        foreach (var forbidden in new[] { "private-fixture", "ConsoleId", "ProcessId", "CliPath", "nodeId", "machineName", run.Probe.Nonce! })
        { Assert.DoesNotContain(forbidden, json, StringComparison.OrdinalIgnoreCase); }
        Assert.Equal(QueueCapability.Verified, await controller.CurrentAsync());
        Assert.Equal(QueueCapability.Unknown, (await controller.StartAsync(run.Authority.Id)).Capability);
        Assert.Equal(1, run.Probe.Effects);
        Assert.Null(controller.End().Refusal);
        Assert.Equal(QueueCapability.Unknown, await controller.CurrentAsync());
    }

    [Theory]
    [InlineData("absent")][InlineData("nonce")][InlineData("steering")]
    [InlineData("duplicate")][InlineData("ambiguous")][InlineData("malformed")][InlineData("duplicate-property")]
    public async Task Missing_wrong_or_ambiguous_raw_evidence_stays_unknown_and_revokes_the_run(string mode)
    {
        var run = new Run(); run.Probe.Mode = mode;
        var result = await run.Controller().StartAsync(run.Authority.Id);
        Assert.Equal(QueueCapability.Unknown, result.Capability); Assert.NotNull(result.Refusal);
        Assert.Equal(QueueCapability.Unknown, Assert.Single(run.Receipts.Entries).Evidence.Capability);
        Assert.Equal(QualificationGrantState.Revoked, run.Authority.Journal.Entries.Last().State);
    }

    [Theory]
    [InlineData("snapshot")][InlineData("revoke")][InlineData("timeout")]
    public async Task Mid_run_identity_change_revocation_or_deadline_expiry_invalidates_even_exact_evidence(string change)
    {
        var run = new Run();
        run.Probe.AfterSend = () =>
        {
            if (change == "snapshot") { run.Probe.Identity = null; }
            else if (change == "revoke") { run.Authority.Grants().Revoke(run.Authority.Id); }
            else { run.Authority.Clock.Advance(TimeSpan.FromMinutes(1)); }
        };
        var result = await run.Controller().StartAsync(run.Authority.Id);
        Assert.Equal(QueueCapability.Unknown, result.Capability); Assert.NotNull(result.Refusal);
    }

    [Theory]
    [InlineData("snapshot")][InlineData("revoke")][InlineData("expiry")]
    public async Task A_positive_record_is_not_a_cache_after_identity_change_revocation_or_expiry(string change)
    {
        var run = new Run(); var controller = run.Controller();
        Assert.Equal(QueueCapability.Verified, (await controller.StartAsync(run.Authority.Id)).Capability);
        var identity = run.Probe.Identity;
        if (change == "snapshot") { run.Probe.Identity = null; }
        else if (change == "revoke") { run.Authority.Grants().Revoke(run.Authority.Id); }
        else { run.Authority.Clock.Advance(TimeSpan.FromMinutes(1)); }
        Assert.Equal(QueueCapability.Unknown, await controller.CurrentAsync());
        run.Probe.Identity = identity;
        Assert.Equal(QueueCapability.Unknown, await controller.CurrentAsync());
    }

    [Fact]
    public async Task Unknown_send_never_verifies_but_explicit_transport_unsupported_is_recorded_distinctly()
    {
        var run = new Run(); run.Probe.Submission = QualificationSubmission.Unknown;
        Assert.Equal(QueueCapability.Unknown, (await run.Controller().StartAsync(run.Authority.Id)).Capability);
        var unsupported = new Run(); unsupported.Probe.Submission = QualificationSubmission.Unsupported;
        Assert.Equal(QueueCapability.Unsupported, (await unsupported.Controller().StartAsync(unsupported.Authority.Id)).Capability);
    }

    [Fact]
    public async Task Missing_authority_or_sinks_and_final_effect_identity_change_cannot_send_keys()
    {
        Assert.Equal(QueueCapability.Unknown, (await new QualificationController().StartAsync(Guid.NewGuid())).Capability);
        var run = new Run();
        Assert.Equal(QueueCapability.Unknown, (await run.Controller().StartAsync(Guid.Empty)).Capability);
        Assert.Equal(QueueCapability.Unknown, (await new QualificationController(run.Authority.Grants(), run.Probe).StartAsync(run.Authority.Id)).Capability);
        run.Probe.BeforeFence = () => run.Probe.Identity = null;
        Assert.Equal(QueueCapability.Unknown, (await run.Controller().StartAsync(run.Authority.Id)).Capability);
        Assert.Equal(0, run.Probe.Effects);
    }

    [Fact]
    public async Task A_receipt_store_failure_propagates_and_revokes_instead_of_returning_verified()
    {
        var run = new Run(); run.Receipts.Available = false; var controller = run.Controller();
        await Assert.ThrowsAsync<IOException>(() => controller.StartAsync(run.Authority.Id));
        Assert.Equal(QualificationGrantState.Revoked, run.Authority.Journal.Entries.Last().State);
        Assert.Equal(QueueCapability.Unknown, await controller.CurrentAsync());
    }
}
