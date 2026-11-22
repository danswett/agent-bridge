using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
    }), new(3, 7, 2, 1, ["pending-one", "live-one"], caller, outcome, DateTimeOffset.UtcNow), false);
    static GrantRevokeReceipt Receipt() => new(Id, 2, "receiver-1", "1042", "default", "grant.revoke", Digest, true, ["pending-one"]);
    static string Visible(GrantListViewModel vm) => vm.Error + " " + string.Join(" ", vm.Rows.Select(r => $"{r.Title} {r.Details} {r.State} {r.Counters} {r.InFlight} {r.Receipt}"));
    static void Safe(string text) {
        foreach (var forbidden in new[] { "PRIVATE_PROMPT", "PRIVATE_TOKEN", "PRIVATE_SECRET", "PRIVATE_PROOF", "PRIVATE_CREDENTIAL", "RAW_COMMAND", "pwsh" })
            Assert.DoesNotContain(forbidden, text);
    }
    [Fact]
    public async Task Receiver_counts_remain_stale_while_offline_and_never_come_from_a_callers_report()
    {
        var receiver = new FakeReceiver { Value = Snapshot(outcome: "caller reports hour=999 day=999") };
        var vm = new GrantListViewModel(receiver); await vm.RefreshAsync(); var counters = Assert.Single(vm.Rows).Counters;
        Assert.Contains("3/10", counters); Assert.Contains("7/20", counters); Assert.Contains("3/3", counters); Assert.DoesNotContain("999", counters);
        receiver.Value = receiver.Value with { Stale = true }; await vm.RefreshAsync();
        Assert.False(Assert.Single(vm.Rows).CanRevoke); Assert.Contains("stale", Assert.Single(vm.Rows).State, StringComparison.OrdinalIgnoreCase);
        receiver.Online = false; receiver.Value = receiver.Value with { Counters = receiver.Value.Counters with { Hour = 9, Day = 19 } };
        await vm.RefreshAsync(); var row = Assert.Single(vm.Rows);
        Assert.Equal(counters, row.Counters); Assert.Contains("stale", row.State, StringComparison.OrdinalIgnoreCase); Assert.False(row.CanRevoke); Assert.Equal(2, receiver.Reads);
    }
    [Fact]
    public async Task Declining_the_person_confirmation_never_confirms_a_revoke()
    {
        var receiver = new FakeReceiver(); var vm = new GrantListViewModel(receiver); await vm.RefreshAsync(); var asked = false;
        await vm.RevokeAsync(Assert.Single(vm.Rows), summary =>
        {
            asked = true; Assert.Contains(Digest, summary); Assert.Contains(Id.ToString(), summary); Assert.Contains("receiver-1", summary);
            Assert.NotEqual(PreparedDocument, summary); Safe(summary); return Task.FromResult(false);
        });
        Assert.True(asked); Assert.Equal(1, receiver.Prepares); Assert.Equal(0, receiver.Confirms);
        var row = Assert.Single(vm.Rows); Assert.Empty(row.Receipt); Assert.True(row.CanRevoke); Assert.False(vm.Busy); Safe(Visible(vm));
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
        Assert.Contains("pending-one", row.InFlight); Assert.Contains("live-one", row.InFlight);
        Assert.Contains("3/10", row.Counters); Assert.Contains("7/20", row.Counters); Assert.False(row.CanRevoke); Assert.False(vm.Busy); Safe(Visible(vm));
    }
    [Theory]
    [InlineData("agent:123/session", "prompt=PRIVATE_PROMPT token=PRIVATE_TOKEN secret=PRIVATE_SECRET command=pwsh RAW_COMMAND")]
    [InlineData("agent:123/session\nprompt=PRIVATE_PROMPT token=PRIVATE_TOKEN secret=PRIVATE_SECRET pwsh RAW_COMMAND", "delivered")]
    public async Task Malicious_caller_or_outcome_words_never_expose_prompts_tokens_secrets_or_raw_commands(string caller, string outcome)
    {
        var vm = new GrantListViewModel(new FakeReceiver { Value = Snapshot(caller, outcome) }); await vm.RefreshAsync();
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
    [InlineData(false)] [InlineData(true)]
    public async Task A_lost_confirmation_even_if_the_receiver_goes_offline_never_claims_revoked_or_hides_work(bool offline)
    {
        var receiver = new FakeReceiver { Lost = true, OfflineAfterSend = offline }; var vm = new GrantListViewModel(receiver); await vm.RefreshAsync();
        await vm.RevokeAsync(Assert.Single(vm.Rows), _ => Task.FromResult(true)); var row = Assert.Single(vm.Rows);
        Assert.Equal(1, receiver.Confirms); Assert.Contains("pending", row.State, StringComparison.OrdinalIgnoreCase);
        if (offline) Assert.Contains("stale", row.State, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(row.Receipt); Assert.False(row.CanRevoke); Assert.Contains("pending-one", row.InFlight); Assert.Contains("live-one", row.InFlight);
        Assert.Contains("3/10", row.Counters); Assert.Contains("7/20", row.Counters); Assert.False(vm.Busy); Safe(Visible(vm));
    }
    sealed class FakeReceiver : IGrantListReceiver
    {
        public string Receiver => "receiver-1";
        public bool Online { get; set; } = true;
        public string? Problem { get; set; } public string? ReadProblem;
        public GrantSnapshot Value = Snapshot(); public GrantRevokeReceipt Result = Receipt();
        public bool Lost, OfflineAfterSend; public int Reads, Prepares, Confirms;
        public TaskCompletionSource<GrantRevokeReceipt>? Ack;
        public Task<GrantReadResult> ReadAsync(CancellationToken ct = default)
        { Reads++; return Task.FromResult(new GrantReadResult(ReadProblem is null ? [Value] : [], ReadProblem ?? Problem)); }
        public Task<GrantRevokePreview> PrepareRevokeAsync(Guid id, long generation, CancellationToken ct = default)
        { Assert.Equal(Id, id); Assert.Equal(2, generation); Prepares++; return Task.FromResult(new GrantRevokePreview(id, generation, Receiver, "1042", "default", "grant.revoke", PreparedDocument, Digest)); }
        public Task<GrantRevokeReceipt> ConfirmRevokeAsync(Guid id, long generation, string digest, CancellationToken ct = default)
        {
            Assert.Equal(Id, id); Assert.Equal(2, generation); Assert.Equal(Digest, digest); Confirms++;
            if (Lost) { Online = !OfflineAfterSend; throw new IOException("PRIVATE_TOKEN RAW_COMMAND"); }
            return Ack?.Task ?? Task.FromResult(Result);
        }
    }
}
