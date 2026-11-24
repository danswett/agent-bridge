using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentBridge.AgentEnvelope;
using AgentBridge.App.Core.Grants;
using GrantListViewModel = AgentBridge.App.Core.GrantListViewModel;
using Xunit;

public sealed class GrantListTests
{
    static readonly Guid Id = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    const string PreparedDocument = """{"id":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa","generation":2,"account":"1042","profile":"default","receiver":"receiver-1","action":"grant.revoke","prompt":"PRIVATE_PROMPT π","token":"PRIVATE_TOKEN","command":"pwsh RAW_COMMAND"}""";
    static readonly string Digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(PreparedDocument)));
    static GrantSnapshot Snapshot(string caller = "agent:123/session", string outcome = "delivered") => new(JsonSerializer.SerializeToElement(new
    {
        id = Id, version = 1, generation = 2, account = "1042", issuer = new { account = "1042", device = "issuer-1", personProof = "PRIVATE_PROOF", digest = new string('a', 64) },
        audience = new { device = "receiver-1", profile = "default" }, subjects = new[] { new { account = "1042", sourceHost = "source-1", session = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
            credential = "PRIVATE_CREDENTIAL", processInstance = new { pid = 123, startedAt = DateTimeOffset.UtcNow.AddHours(-1) } } },
        actions = new[] { "launch", "stop" }, scope = new { machines = new[] { "receiver-1" }, workspaces = new[] { "approved-workspace" },
            agents = new[] { "copilot" }, models = new[] { "codex" }, profiles = new[] { "default" }, efforts = new[] { "high" },
            contexts = new[] { "default" }, permissions = new[] { "ask" } }, caps = new { hourly = 10, daily = 20, live = 3 }, expiresAt = DateTimeOffset.UtcNow.AddHours(8),
        prompt = "PRIVATE_PROMPT", token = "PRIVATE_TOKEN", command = "pwsh RAW_COMMAND"
    }), new(3, 7, 2, 1, ["completed-old"], caller, outcome, DateTimeOffset.UtcNow, ["pending-one", "pending-two"], ["live-one"]), false);
    static GrantRevokeReceipt Receipt() => new(Id, 2, "receiver-1", "1042", "default", "grant.revoke", Digest, true, ["pending-one"]);
    static Exception Failure(string kind) => kind switch { "transport" => new HttpRequestException(), "cancelled" => new OperationCanceledException(), "access" => new UnauthorizedAccessException(), "json" => new JsonException(), _ => new IOException("PRIVATE_TOKEN RAW_COMMAND") };
    static string Visible(GrantListViewModel vm) => vm.Error + " " + string.Join(" ", vm.Rows.Select(r => $"{r.Title} {r.Details} {r.State} {r.Counters} {r.InFlight} {r.Receipt}"));
    static void Safe(string text) {
        foreach (var forbidden in new[] { "PRIVATE_PROMPT", "PRIVATE_TOKEN", "PRIVATE_SECRET", "PRIVATE_PROOF", "PRIVATE_CREDENTIAL", "RAW_COMMAND", "pwsh" })
            Assert.DoesNotContain(forbidden, text);
    }
    [Theory] [InlineData("transport")] [InlineData("cancelled")] [InlineData("access")]
    public async Task Receiver_counts_remain_stale_while_offline_or_unavailable_and_never_come_from_a_callers_report(string failure)
    {
        var receiver = new FakeReceiver { Value = Snapshot(outcome: "caller reports hour=999 day=999") };
        var vm = new GrantListViewModel(receiver); await vm.RefreshAsync(); var counters = Assert.Single(vm.Rows).Counters;
        Assert.Contains("3/10", counters); Assert.Contains("7/20", counters); Assert.Contains("3/3", counters); Assert.DoesNotContain("999", counters);
        receiver.Value = receiver.Value with { Stale = true }; await vm.RefreshAsync();
        Assert.False(Assert.Single(vm.Rows).CanRevoke); Assert.Contains("stale", Assert.Single(vm.Rows).State, StringComparison.OrdinalIgnoreCase);
        receiver.Online = false; receiver.Value = receiver.Value with { Counters = receiver.Value.Counters with { Hour = 9, Day = 19 } };
        await vm.RefreshAsync(); var row = Assert.Single(vm.Rows);
        Assert.Equal(counters, row.Counters); Assert.Contains("stale", row.State, StringComparison.OrdinalIgnoreCase); Assert.False(row.CanRevoke); Assert.Equal(2, receiver.Reads);
        receiver.Online = true; receiver.ReadFailure = failure; await vm.RefreshAsync(); Assert.False(vm.Available);
        Assert.Equal(counters, row.Counters); Assert.False(row.CanRevoke); Assert.Contains("stale", row.State); Safe(Visible(vm));
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Declining_or_losing_the_person_confirmation_never_confirms_a_revoke(bool dialogUnavailable)
    {
        var receiver = new FakeReceiver(); var vm = new GrantListViewModel(receiver); await vm.RefreshAsync(); var asked = false;
        await vm.RevokeAsync(Assert.Single(vm.Rows), summary =>
        {
            asked = true; Assert.Contains(Digest, summary); Assert.Contains(Id.ToString(), summary); Assert.Contains("receiver-1", summary);
            Assert.NotEqual(PreparedDocument, summary); Safe(summary); return dialogUnavailable ? Task.FromException<bool>(new System.Runtime.InteropServices.COMException("PRIVATE_TOKEN")) : Task.FromResult(false);
        });
        Assert.True(asked); Assert.Equal(1, receiver.Prepares); Assert.Equal(0, receiver.Confirms);
        var row = Assert.Single(vm.Rows); Assert.Empty(row.Receipt); Assert.Equal(!dialogUnavailable, row.CanRevoke); Assert.False(vm.Busy); Safe(Visible(vm));
    }
    [Fact]
    public async Task Only_a_verified_tombstone_marks_revoked_and_it_does_not_hide_in_flight_or_live_work()
    {
        var receiver = new FakeReceiver { Ack = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var vm = new GrantListViewModel(receiver); await vm.RefreshAsync(); var revoke = vm.RevokeAsync(Assert.Single(vm.Rows), _ => Task.FromResult(true));
        Assert.Equal(1, receiver.Confirms); Assert.True(vm.Busy); Assert.Empty(Assert.Single(vm.Rows).Receipt);
        Assert.DoesNotContain("revoked", Assert.Single(vm.Rows).State, StringComparison.OrdinalIgnoreCase);
        receiver.Ack.SetResult(Receipt()); await revoke; var row = Assert.Single(vm.Rows);
        Assert.Contains("revoked", row.State, StringComparison.OrdinalIgnoreCase); Assert.Contains(Digest, row.Receipt);
        Assert.Contains("pending-one", row.InFlight); Assert.Contains("live-one", row.InFlight); Assert.DoesNotContain("completed-old", row.InFlight);
        Assert.Contains("3/10", row.Counters); Assert.Contains("7/20", row.Counters); Assert.False(row.CanRevoke); Assert.False(vm.Busy); Safe(Visible(vm));
        receiver.Empty = true; await vm.RefreshAsync(); Assert.Contains("stale", Assert.Single(vm.Rows).State); Assert.Contains(Digest, Assert.Single(vm.Rows).Receipt);
    }
    [Theory]
    [InlineData("agent:123/session", "prompt=PRIVATE_PROMPT token=PRIVATE_TOKEN secret=PRIVATE_SECRET command=pwsh RAW_COMMAND")]
    [InlineData("agent:123/session\nprompt=PRIVATE_PROMPT token=PRIVATE_TOKEN secret=PRIVATE_SECRET pwsh RAW_COMMAND", "delivered")]
    public async Task Malicious_caller_or_outcome_words_never_expose_prompts_tokens_secrets_or_raw_commands(string caller, string outcome)
    {
        var value = Snapshot(caller, outcome); var vm = new GrantListViewModel(new FakeReceiver { Value = value with { Counters = value.Counters with { PendingRequests = [caller], LiveSessions = [caller] } } }); await vm.RefreshAsync();
        Assert.Single(vm.Rows); Safe(Visible(vm));
        if (outcome != "delivered") Assert.Contains("Unknown", Visible(vm), StringComparison.OrdinalIgnoreCase);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Rotation_from_the_provider_or_read_result_keeps_verified_counts_and_blocks_revoke_until_a_good_refresh(bool readOnly)
    {
        var receiver = new FakeReceiver(); var vm = new GrantListViewModel(receiver); await vm.RefreshAsync(); var counters = Assert.Single(vm.Rows).Counters;
        if (readOnly) { receiver.ReadProblem = "RotationRequired"; Assert.Null(receiver.Problem); } else receiver.Problem = "RotationRequired";
        await vm.RefreshAsync(); var row = Assert.Single(vm.Rows); Assert.False(row.CanRevoke); Assert.Equal(counters, row.Counters);
        var asked = false; await vm.RevokeAsync(row, _ => { asked = true; return Task.FromResult(true); });
        Assert.False(asked); Assert.False(row.CanRevoke); Assert.Equal(0, receiver.Prepares); Assert.Equal(0, receiver.Confirms);
        Assert.Contains("rotation", Visible(vm), StringComparison.OrdinalIgnoreCase); Assert.Contains("stale", row.State, StringComparison.OrdinalIgnoreCase); Assert.Empty(row.Receipt);
        receiver.ReadProblem = null; receiver.Problem = null; await vm.RefreshAsync();
        Assert.True(Assert.Single(vm.Rows).CanRevoke); Assert.Equal(counters, Assert.Single(vm.Rows).Counters);
    }
    [Theory]
    [InlineData("RotationRequired")] [InlineData("rotation-required")]
    [InlineData("AuditIncomplete")] [InlineData("unavailable")]
    [InlineData("PRIVATE_TOKEN RAW_COMMAND")]
    public async Task Legacy_problem_strings_without_verified_first_issue_evidence_keep_the_list_and_revoke_unavailable(string problem)
    {
        var receiver = new FakeReceiver(); var vm = new GrantListViewModel(receiver);
        await vm.RefreshAsync(); var row = Assert.Single(vm.Rows); var counters = row.Counters;
        receiver.ReadProblem = problem; await vm.RefreshAsync();
        Assert.False(vm.Available); Assert.False(vm.CanIssueFirst); Assert.False(vm.Busy); Assert.False(row.CanRevoke);
        Assert.Equal(counters, row.Counters); Assert.Empty(row.Receipt); Safe(Visible(vm));
        var asked = false; await vm.RevokeAsync(row, _ => { asked = true; return Task.FromResult(true); });
        Assert.False(asked); Assert.Equal(0, receiver.Prepares); Assert.Equal(0, receiver.Confirms);
    }
    [Theory]
    [InlineData(FleetGrantReadProblem.None, false)] [InlineData(FleetGrantReadProblem.None, true)]
    [InlineData(FleetGrantReadProblem.RotationRequired, false)] [InlineData(FleetGrantReadProblem.RotationRequired, true)]
    [InlineData(FleetGrantReadProblem.Unavailable, true)] [InlineData(FleetGrantReadProblem.Other, true)]
    public async Task Only_verified_rotation_required_with_G7_readiness_admits_first_issue_without_admitting_list_or_revoke(FleetGrantReadProblem problem, bool ready)
    {
        var receiver = new FakeReceiver { ReadProblem = "RotationRequired", Code = problem, G7Ready = ready };
        var vm = new GrantListViewModel(receiver); await vm.RefreshAsync();
        Assert.False(vm.Available); Assert.Empty(vm.Rows);
        Assert.Equal(problem == FleetGrantReadProblem.RotationRequired && ready, vm.CanIssueFirst);
        if (vm.CanIssueFirst) { Assert.Contains("person confirmation", vm.FirstIssueNotice); Assert.Contains("receipt", vm.FirstIssueNotice); }
        else { Assert.Empty(vm.FirstIssueNotice); }
        Safe(Visible(vm) + vm.FirstIssueNotice);
    }
    [Theory] [InlineData(null)] [InlineData("PRIVATE_TOKEN RAW_COMMAND")]
    public async Task A_verified_rotation_refusal_never_launders_list_data_or_uses_diagnostic_words_as_authority(string? diagnostic)
    {
        var vm = new GrantListViewModel(new FakeReceiver { Code = FleetGrantReadProblem.RotationRequired, G7Ready = true, ReadProblem = diagnostic });
        await vm.RefreshAsync();
        Assert.False(vm.Available); Assert.True(vm.CanIssueFirst); Assert.Empty(vm.Rows);
        Safe(Visible(vm) + vm.FirstIssueNotice);
    }
    [Fact]
    public async Task A_legacy_reader_without_the_verified_adapter_is_unavailable_and_is_not_consulted()
    {
        var receiver = new FakeReceiver(); var vm = new GrantListViewModel(new LegacyReader(receiver));
        await vm.RefreshAsync();
        Assert.False(vm.Available); Assert.False(vm.CanIssueFirst); Assert.Empty(vm.Rows); Assert.Equal(0, receiver.Reads);
    }
    [Fact]
    public async Task Readiness_loss_other_refusals_and_transport_failure_clear_a_previous_first_issue_entry_without_enabling_revoke()
    {
        var receiver = new FakeReceiver(); var vm = new GrantListViewModel(receiver); await vm.RefreshAsync();
        var row = Assert.Single(vm.Rows); var counts = row.Counters;
        receiver.Code = FleetGrantReadProblem.RotationRequired; receiver.G7Ready = true; receiver.ReadProblem = "RotationRequired";
        await vm.RefreshAsync(); Assert.True(vm.CanIssueFirst); Assert.False(row.CanRevoke); Assert.Equal(counts, row.Counters);
        await vm.RevokeAsync(row, _ => Task.FromResult(true)); Assert.Equal(0, receiver.Prepares); Assert.Equal(0, receiver.Confirms);
        receiver.G7Ready = false; await vm.RefreshAsync(); Assert.False(vm.CanIssueFirst);
        receiver.G7Ready = true; receiver.Code = FleetGrantReadProblem.Other; await vm.RefreshAsync(); Assert.False(vm.CanIssueFirst);
        receiver.Code = FleetGrantReadProblem.RotationRequired; await vm.RefreshAsync(); Assert.True(vm.CanIssueFirst);
        receiver.Online = false; Assert.False(vm.CanIssueFirst);
        receiver.Online = true; receiver.ReadFailure = "transport"; await vm.RefreshAsync();
        Assert.False(vm.CanIssueFirst); Assert.False(vm.Available); Assert.False(row.CanRevoke); Safe(Visible(vm));
    }
    [Theory]
    [InlineData(FleetGrantReadProblem.None, false)] [InlineData(FleetGrantReadProblem.None, true)]
    [InlineData(FleetGrantReadProblem.RotationRequired, false)] [InlineData(FleetGrantReadProblem.RotationRequired, true)]
    public async Task Receiver_switches_disable_cached_gates_and_discard_in_flight_verified_reads(FleetGrantReadProblem problem, bool inFlight)
    {
        var receiver = new FakeReceiver { G7Ready = true }; var vm = new GrantListViewModel(receiver);
        await vm.RefreshAsync(); var counts = Assert.Single(vm.Rows).Counters;
        receiver.Code = problem;
        if (inFlight) { receiver.ReadAck = new(TaskCreationOptions.RunContinuationsAsynchronously); }
        var refresh = vm.RefreshAsync();
        if (!inFlight)
        {
            await refresh;
            Assert.Equal(problem == FleetGrantReadProblem.None, vm.Available);
            Assert.Equal(problem == FleetGrantReadProblem.RotationRequired, vm.CanIssueFirst);
        }
        receiver.Receiver = "receiver-2";
        if (inFlight) { receiver.ReadAck!.SetResult(new([], null)); await refresh; }
        Assert.False(vm.Available); Assert.False(vm.CanIssueFirst); Assert.Empty(vm.FirstIssueNotice);
        Assert.Equal(counts, Assert.Single(vm.Rows).Counters); Assert.Equal(2, receiver.Reads);
        if (inFlight)
        {
            receiver.Receiver = "receiver-1";
            Assert.False(vm.Available); Assert.False(vm.CanIssueFirst);
        }
    }
    [Theory] [InlineData("missing")] [InlineData("unverified")] [InlineData("unknown")]
    public async Task A_missing_or_refused_verified_result_clears_first_issue_without_retrying(string failure)
    {
        var receiver = new FakeReceiver { Code = FleetGrantReadProblem.RotationRequired, G7Ready = true, ReadProblem = "RotationRequired" };
        var vm = new GrantListViewModel(receiver); await vm.RefreshAsync(); Assert.True(vm.CanIssueFirst);
        receiver.MissingVerified = failure == "missing"; receiver.VerificationRefusal = failure == "unverified";
        if (failure == "unknown") { receiver.Code = (FleetGrantReadProblem)255; }
        await vm.RefreshAsync();
        Assert.False(vm.Available); Assert.False(vm.CanIssueFirst); Assert.Empty(vm.FirstIssueNotice); Assert.Equal(2, receiver.Reads);
        Safe(Visible(vm));
    }
    [Theory]
    [InlineData("id")] [InlineData("generation")] [InlineData("receiver")] [InlineData("account")]
    [InlineData("profile")] [InlineData("action")] [InlineData("digest")] [InlineData("tombstone")]
    public async Task A_receipt_with_any_wrong_binding_or_no_tombstone_leaves_revocation_pending(string wrong)
    {
        var good = Receipt(); var receiver = new FakeReceiver { Result = wrong switch {
            "id" => good with { Id = Guid.Empty }, "generation" => good with { Generation = 3 }, "receiver" => good with { Receiver = "other" },
            "account" => good with { Account = "other" }, "profile" => good with { Profile = "other" }, "action" => good with { Action = "grant.issue" },
            "digest" => good with { Digest = new string('0', 64) }, _ => good with { Tombstone = false } } };
        var vm = new GrantListViewModel(receiver); await vm.RefreshAsync(); await vm.RevokeAsync(Assert.Single(vm.Rows), _ => Task.FromResult(true));
        var row = Assert.Single(vm.Rows); Assert.Contains("pending", row.State, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(row.Receipt); Assert.False(row.CanRevoke); Assert.False(vm.Busy); Safe(Visible(vm));
    }
    [Theory]
    [InlineData(false, "io")] [InlineData(true, "io")]
    [InlineData(false, "transport")] [InlineData(false, "cancelled")] [InlineData(false, "access")] [InlineData(false, "json")]
    public async Task A_lost_confirmation_even_if_the_receiver_goes_offline_never_claims_revoked_or_hides_work(bool offline, string failure)
    {
        var receiver = new FakeReceiver { Lost = true, OfflineAfterSend = offline, FailureKind = failure }; var vm = new GrantListViewModel(receiver); await vm.RefreshAsync();
        await vm.RevokeAsync(Assert.Single(vm.Rows), _ => Task.FromResult(true)); var row = Assert.Single(vm.Rows);
        Assert.Equal(1, receiver.Confirms); Assert.Contains("pending", row.State, StringComparison.OrdinalIgnoreCase);
        if (offline) Assert.Contains("stale", row.State, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(row.Receipt); Assert.False(row.CanRevoke); Assert.Contains("pending-one", row.InFlight); Assert.Contains("live-one", row.InFlight);
        Assert.Contains("3/10", row.Counters); Assert.Contains("7/20", row.Counters); Assert.False(vm.Busy); Safe(Visible(vm));
    }
    sealed class FakeReceiver : IGrantListReceiver, IVerifiedGrantReader
    {
        public string Receiver { get; set; } = "receiver-1";
        public bool Online { get; set; } = true;
        public string? Problem { get; set; } public string? ReadProblem;
        public FleetGrantReadProblem Code = FleetGrantReadProblem.None; public bool G7Ready;
        public bool MissingVerified, VerificationRefusal;
        public GrantSnapshot Value = Snapshot(); public GrantRevokeReceipt Result = Receipt();
        public bool Lost, OfflineAfterSend, Empty; public int Reads, Prepares, Confirms; public string FailureKind = "io"; public string? ReadFailure;
        public TaskCompletionSource<GrantRevokeReceipt>? Ack;
        public TaskCompletionSource<GrantReadResult>? ReadAck;
        public Task<GrantReadResult> ReadAsync(CancellationToken ct = default)
        { Reads++; if (ReadFailure is { } failure) throw Failure(failure); return ReadAck?.Task ?? Task.FromResult(new GrantReadResult(ReadProblem is null && !Empty ? [Value] : [], ReadProblem ?? Problem)); }
        async Task<VerifiedGrantRead> IVerifiedGrantReader.ReadAsync(CancellationToken ct)
        {
            var data = await ReadAsync(ct);
            if (MissingVerified) { return null!; }
            if (VerificationRefusal) { throw new InvalidDataException("PRIVATE_TOKEN RAW_COMMAND"); }
            return new VerifiedGrantRead(Code, G7Ready, data);
        }
        public Task<GrantRevokePreview> PrepareRevokeAsync(Guid id, long generation, CancellationToken ct = default)
        { Assert.Equal(Id, id); Assert.Equal(2, generation); Prepares++; return Task.FromResult(new GrantRevokePreview(id, generation, Receiver, "1042", "default", "grant.revoke", PreparedDocument, Digest)); }
        public Task<GrantRevokeReceipt> ConfirmRevokeAsync(Guid id, long generation, string digest, CancellationToken ct = default)
        {
            Assert.Equal(Id, id); Assert.Equal(2, generation); Assert.Equal(Digest, digest); Confirms++;
            if (Lost) { Online = !OfflineAfterSend; throw Failure(FailureKind); }
            return Ack?.Task ?? Task.FromResult(Result);
        }
    }
    sealed class LegacyReader(FakeReceiver receiver) : IGrantListReceiver
    {
        public string Receiver => receiver.Receiver;
        public bool Online => receiver.Online;
        public string? Problem => receiver.Problem;
        public Task<GrantReadResult> ReadAsync(CancellationToken ct = default) => receiver.ReadAsync(ct);
        public Task<GrantRevokePreview> PrepareRevokeAsync(Guid id, long generation, CancellationToken ct = default) => receiver.PrepareRevokeAsync(id, generation, ct);
        public Task<GrantRevokeReceipt> ConfirmRevokeAsync(Guid id, long generation, string digest, CancellationToken ct = default) => receiver.ConfirmRevokeAsync(id, generation, digest, ct);
    }
}
