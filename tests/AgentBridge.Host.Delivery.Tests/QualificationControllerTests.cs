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
        public Task<string?>? CaptureOverride;
        public Task<string?> CaptureAsync() => CaptureOverride ?? Task.FromResult(Identity);
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
            if (Mode == "duplicate-content") { Rows.Clear(); Rows.Add(Event("user.message", text)); Rows.Add("{\"type\":\"user.message\",\"data\":{\"content\":" + JsonSerializer.Serialize(text) + ",\"content\":\"other\",\"delivery\":\"queued\"}}"); }
            AfterSend?.Invoke(); return Submission;
        }
    }

    sealed class Receipts : IQualificationReceiptJournal
    {
        public readonly List<QualificationRecord> Entries = [];
        public bool Available = true;
        public Action? AfterAppend;
        public void Append(QualificationRecord record)
        {
            if (!Available) { throw new IOException("receipt append failed"); }
            Entries.Add(record); AfterAppend?.Invoke();
        }
    }

    sealed class Run
    {
        public readonly QualificationGrantTests.Run Authority = new();
        public readonly Probe Probe = new();
        public readonly Receipts Receipts = new();
        public QualificationController Controller() => new(Authority.Grants(), Probe, Receipts, Authority.Clock);
        public Task<QualificationStart> Start(QualificationController controller, Guid? id = null) =>
            controller.StartAsync(id ?? Authority.Id, Authority.Trust.Binding.Target, Authority.Challenge);
        public Run()
        {
            Authority.Trust.Binding = Authority.Trust.Binding with
            { Target = QueueTarget.Decode(Probe.Identity) ?? throw new InvalidOperationException("invalid fixture identity") };
            Authority.Issue();
        }
    }

    static string B64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
    static string Event(string type, string? content = null, string delivery = "queued") =>
        JsonSerializer.Serialize(new { type, data = new { content, delivery } });

    [Fact]
    public async Task A_single_raw_queued_challenge_yields_a_sanitized_hashed_receipt_and_only_current_bound_capability()
    {
        var run = new Run(); var controller = run.Controller();
        var result = await run.Start(controller);
        Assert.Null(result.Refusal); Assert.Equal(QueueCapability.Verified, result.Capability);
        var record = Assert.IsType<QualificationRecord>(result.Record);
        Assert.Same(record, Assert.Single(run.Receipts.Entries));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(record.Evidence))), record.ContentHash);
        var json = JsonSerializer.Serialize(record);
        var binding = run.Authority.Trust.Binding;
        foreach (var forbidden in new[] { "private-fixture", "ConsoleId", "ProcessId", "CliPath", "nodeId", "machineName",
            run.Probe.Nonce!, binding.Host.ToString(), binding.Vm.ToString(), binding.Run.ToString(), run.Authority.Id.ToString() })
        { Assert.DoesNotContain(forbidden, json, StringComparison.OrdinalIgnoreCase); }
        Assert.Equal(QueueCapability.Verified, await controller.CurrentAsync());
        Assert.Equal(QueueCapability.Unknown, (await run.Start(controller)).Capability);
        Assert.Equal(1, run.Probe.Effects);
        Assert.Null((await controller.EndAsync()).Refusal);
        Assert.Equal(QueueCapability.Unknown, await controller.CurrentAsync());
        Assert.Equal(QueueCapability.Unknown, await run.Controller().CurrentAsync());
    }

    [Theory]
    [InlineData("absent")][InlineData("nonce")][InlineData("steering")]
    [InlineData("duplicate")][InlineData("ambiguous")][InlineData("malformed")][InlineData("duplicate-property")][InlineData("duplicate-content")]
    public async Task Missing_wrong_or_ambiguous_raw_evidence_stays_unknown_and_revokes_the_run(string mode)
    {
        var run = new Run(); run.Probe.Mode = mode;
        var result = await run.Start(run.Controller());
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
        var result = await run.Start(run.Controller());
        Assert.Equal(QueueCapability.Unknown, result.Capability); Assert.NotNull(result.Refusal);
    }

    [Theory]
    [InlineData("snapshot")][InlineData("revoke")][InlineData("expiry")]
    public async Task A_positive_record_is_not_a_cache_after_identity_change_revocation_or_expiry(string change)
    {
        var run = new Run(); var controller = run.Controller();
        Assert.Equal(QueueCapability.Verified, (await run.Start(controller)).Capability);
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
        Assert.Equal(QueueCapability.Unknown, (await run.Start(run.Controller())).Capability);
        var unsupported = new Run(); unsupported.Probe.Submission = QualificationSubmission.Unsupported;
        Assert.Equal(QueueCapability.Unsupported, (await unsupported.Start(unsupported.Controller())).Capability);
    }

    [Fact]
    public async Task Missing_authority_or_sinks_and_final_effect_identity_change_cannot_send_keys()
    {
        Assert.Equal(QueueCapability.Unknown, (await new QualificationController().StartAsync(Guid.NewGuid(), null, null)).Capability);
        var run = new Run();
        Assert.Equal(QueueCapability.Unknown, (await run.Start(run.Controller(), Guid.Empty)).Capability);
        Assert.Equal(QueueCapability.Unknown, (await run.Start(new QualificationController(run.Authority.Grants(), run.Probe))).Capability);
        run.Probe.BeforeFence = () => run.Probe.Identity = null;
        Assert.Equal(QueueCapability.Unknown, (await run.Start(run.Controller())).Capability);
        Assert.Equal(0, run.Probe.Effects);
    }

    [Fact]
    public async Task A_receipt_store_failure_propagates_and_revokes_instead_of_returning_verified()
    {
        var run = new Run(); run.Receipts.Available = false; var controller = run.Controller();
        await Assert.ThrowsAsync<IOException>(() => run.Start(controller));
        Assert.Equal(QualificationGrantState.Revoked, run.Authority.Journal.Entries.Last().State);
        Assert.Equal(QueueCapability.Unknown, await controller.CurrentAsync());
    }

    [Fact]
    public async Task An_identity_read_timeout_invalidates_permanently_without_waiting_past_the_run_deadline()
    {
        var run = new Run(); var controller = run.Controller();
        Assert.Equal(QueueCapability.Verified, (await run.Start(controller)).Capability);
        run.Probe.CaptureOverride = new TaskCompletionSource<string?>().Task;
        var current = controller.CurrentAsync();
        run.Authority.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(QueueCapability.Unknown, await current.WaitAsync(TimeSpan.FromSeconds(5)));
        run.Probe.CaptureOverride = null;
        Assert.Equal(QueueCapability.Unknown, await controller.CurrentAsync());
    }

    [Fact]
    public async Task Identity_loss_during_durable_append_adds_unknown_instead_of_returning_the_stale_positive()
    {
        var run = new Run(); run.Receipts.AfterAppend = () => run.Probe.Identity = null;
        var result = await run.Start(run.Controller());
        Assert.Equal(QueueCapability.Unknown, result.Capability);
        Assert.Equal(new[] { QueueCapability.Verified, QueueCapability.Unknown }, run.Receipts.Entries.Select(x => x.Evidence.Capability));
    }

    [Fact]
    public async Task A_failed_current_identity_read_surfaces_the_error_and_permanently_revokes()
    {
        var run = new Run(); var controller = run.Controller();
        Assert.Equal(QueueCapability.Verified, (await run.Start(controller)).Capability);
        run.Probe.CaptureOverride = Task.FromException<string?>(new IOException("identity unavailable"));
        await Assert.ThrowsAsync<IOException>(() => controller.CurrentAsync());
        run.Probe.CaptureOverride = null;
        Assert.Equal(QueueCapability.Unknown, await controller.CurrentAsync());
        Assert.Equal(QualificationGrantState.Revoked, run.Authority.Journal.Entries.Last().State);
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task A_wrong_target_or_nonce_refuses_before_consumption_or_effect_and_preserves_the_approved_attempt(bool nonce)
    {
        var run = new Run(); var controller = run.Controller();
        var target = run.Authority.Trust.Binding.Target;
        var challenge = run.Authority.Challenge;
        var result = await controller.StartAsync(run.Authority.Id,
            nonce ? target : target with { StartedAt = target.StartedAt + 1 },
            nonce ? new("agent-bridge-qualify:" + new string('b', 64)) : challenge);
        Assert.NotNull(result.Refusal); Assert.Equal(QueueCapability.Unknown, result.Capability);
        Assert.Equal(0, run.Probe.Effects); Assert.Empty(run.Receipts.Entries);
        Assert.Equal(QualificationGrantState.Issued, Assert.Single(run.Authority.Journal.Entries).State);
        Assert.Equal(QueueCapability.Verified, (await run.Start(controller)).Capability);
        Assert.Equal(challenge.Text, run.Probe.Nonce);
    }

    [Fact]
    public async Task A_matching_start_argument_cannot_bless_a_different_live_target()
    {
        var run = new Run(); run.Probe.Identity = run.Probe.Identity!.Replace("identity 42 ", "identity 43 ", StringComparison.Ordinal);
        var result = await run.Start(run.Controller());
        Assert.NotNull(result.Refusal); Assert.Equal(QueueCapability.Unknown, result.Capability);
        Assert.Equal(0, run.Probe.Effects);
        Assert.Equal(QualificationGrantState.Revoked, run.Authority.Journal.Entries.Last().State);
    }
}
