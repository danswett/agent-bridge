using System.Security.Cryptography;
using AgentBridge.Host.Core.Usage;
using AgentBridge.Host.Reaping;
using AgentBridge.Protocol;
using Microsoft.Extensions.Time.Testing;
using Xunit;

public sealed class SessionReaperLiveTests : IAsyncLifetime
{
    readonly string folder = Path.Combine(Path.GetTempPath(), "ab-reaper-live-" + Guid.NewGuid().ToString("N"));
    readonly FakeTimeProvider clock = new(DateTimeOffset.Parse("2026-11-18T05:00:00Z"));
    readonly List<string> log = [];
    public Task InitializeAsync() { Directory.CreateDirectory(folder); return Task.CompletedTask; }
    public Task DisposeAsync() { Directory.Delete(folder, true); return Task.CompletedTask; }

    [Theory]
    [InlineData(StopOutcome.Ended)]
    [InlineData(StopOutcome.Confirm)]
    [InlineData(StopOutcome.Failed)]
    [InlineData(StopOutcome.Busy)]
    public async Task Approved_live_uses_one_existing_stop_and_permanent_local_intent_survives_restart(StopOutcome outcome)
    {
        var config = new SessionReaperSettings { Live = true, MachineId = "fixture-vm", IsVm = true, OwnerLaptop = false };
        var calls = 0;
        Task<ReaperSnapshot> Read(CancellationToken _) => Task.FromResult(SessionReaperPolicyTests.Snapshot(SessionReaperPolicyTests.Builder()));
        SessionReaperService Make() => new(folder, "fixture-vm", Read, () => config, log.Add, clock)
        {
            CanEnd = () => true,
            End = (_, request, _) => { Assert.Null(request.Confirm); calls++; return outcome == StopOutcome.Busy
                ? throw new IOException("fixture uncertain stop") : Task.FromResult(new StopResult(outcome)); },
        };
        await using var first = Make();
        var dry = Assert.IsType<SessionReaperReceipt>(await first.CycleAsync());
        Assert.Equal(0, calls);
        var path = Path.Combine(folder, "reaper", "receipts", dry.Id + ".json");
        config = config with { ApprovedReceipt = dry.Id + ".json", ApprovedSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) };
        config = config with { Live = false };
        Assert.Equal("dry-run", (await first.CycleAsync())!.Mode); Assert.Equal(0, calls);
        config = config with { Live = true };
        var live = Assert.IsType<SessionReaperReceipt>(await first.CycleAsync());
        Assert.Equal(1, calls);
        Assert.Equal(outcome == StopOutcome.Ended ? 1 : 0, live.Ended.Count);
        await using var restarted = Make();
        await restarted.CycleAsync();
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Changed_evidence_and_unavailable_approval_never_reach_the_stop()
    {
        var config = new SessionReaperSettings { Live = true, MachineId = "fixture-vm", IsVm = true, OwnerLaptop = false };
        var reads = 0;
        await using var service = new SessionReaperService(folder, "fixture-vm", _ =>
        {
            var row = SessionReaperPolicyTests.Builder();
            return Task.FromResult(SessionReaperPolicyTests.Snapshot(++reads >= 4 ? row with { AskingQuestion = true } : row));
        }, () => config, log.Add, clock)
        { CanEnd = () => true, End = (_, _, _) => throw new InvalidOperationException("must not stop") };
        var dry = Assert.IsType<SessionReaperReceipt>(await service.CycleAsync());
        var path = Path.Combine(folder, "reaper", "receipts", dry.Id + ".json");
        config = config with { ApprovedReceipt = dry.Id + ".json", ApprovedSha256 = new('0', 64) };
        Assert.Equal("dry-run", (await service.CycleAsync())!.Mode);
        config = config with { ApprovedSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) };
        var result = (await service.CycleAsync())!;
        Assert.Empty(result.Ended);
        Assert.Contains(result.Kept, row => row.Reason.Contains("question"));
    }

    [Theory]
    [InlineData("different")]
    [InlineData("mixed")]
    public async Task Git_proves_any_origin_branch_without_tracking_or_same_name_and_fetches_once(string mode)
    {
        File.WriteAllText(Path.Combine(folder, ".git"), "fixture");
        var runner = new FakeGit(folder) { Mode = mode };
        var git = new SessionReaperGit(runner, log.Add);
        Assert.True((await git.ReadAsync(folder, default))!.Contained);
        Assert.True((await git.ReadAsync(folder, default))!.Contained);
        Assert.Equal(1, runner.Fetches);
        runner.Dirty = true;
        Assert.Null(await git.ReadAsync(folder, default));
        Assert.Equal(1, runner.Fetches);
    }

    internal sealed class FakeGit(string folder) : ICommandRunner
    {
        public int Fetches; public bool Dirty; public string Mode = "";
        bool pruned;
        public Task<CommandResult> RunAsync(string program, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            Assert.DoesNotContain("@{upstream}", args);
            var fetching = args.Contains("fetch");
            if (fetching) { Fetches++; pruned = args.Contains("--prune"); }
            if (fetching && (Mode == "absent" || Mode is "different" or "mixed" or "foreign" or "alias" or "none"
                && !args.Any(arg => arg.EndsWith("refs/heads/*:refs/remotes/origin/*", StringComparison.Ordinal)))
                || args.Contains("merge-base") && Mode == "diverged")
                return Task.FromResult(new CommandResult(true, false, 1, ""));
            if (args.Contains("--contains"))
            {
                var branches = Mode switch
                {
                    "foreign" => "refs/remotes/elsewhere/published\t",
                    "alias" => "refs/remotes/origin/HEAD\trefs/remotes/elsewhere/published\nrefs/remotes/origin/alias\trefs/remotes/elsewhere/published",
                    "none" => "",
                    "deleted" when pruned => "",
                    "mixed" => "refs/remotes/elsewhere/published\t\nrefs/remotes/origin/HEAD\trefs/remotes/origin/published\nrefs/remotes/origin/published\t",
                    _ => "refs/remotes/origin/published\t",
                };
                return Task.FromResult(new CommandResult(true, false, 0, branches));
            }
            var text = args.Contains("--absolute-git-dir") ? folder : args.Contains("symbolic-ref") ? "fixture"
                : args.Contains("--porcelain=v1") ? Dirty ? "?? private" : "" : args.Contains("stash") && Mode == "stash" ? new('a', 40)
                : args.Contains("stash") || args.Contains("fetch") || args.Contains("merge-base") ? ""
                : args.Any(arg => arg.StartsWith("refs/remotes/", StringComparison.Ordinal)) ? new('b', 40) : new string('a', 40);
            return Task.FromResult(new CommandResult(true, false, 0, text));
        }
    }

    [Theory]
    [InlineData("foreign")]
    [InlineData("alias")]
    [InlineData("none")]
    [InlineData("deleted")]
    public async Task Other_remotes_symbolic_aliases_and_deleted_tracking_refs_are_not_origin_branch_proof(string mode)
    {
        File.WriteAllText(Path.Combine(folder, ".git"), "fixture");
        Assert.Null(await new SessionReaperGit(new FakeGit(folder) { Mode = mode }, log.Add).ReadAsync(folder, default));
        Assert.NotEmpty(log);
    }

    [Fact]
    public async Task Failed_fetch_stays_unknown_for_the_whole_cycle_instead_of_reusing_stale_refs_or_retrying()
    {
        File.WriteAllText(Path.Combine(folder, ".git"), "fixture");
        var runner = new FakeGit(folder) { Mode = "absent" };
        var git = new SessionReaperGit(runner, log.Add);
        Assert.Null(await git.ReadAsync(folder, default));
        runner.Mode = "";
        Assert.Null(await git.ReadAsync(folder, default));
        Assert.Equal(1, runner.Fetches);
        git.Reset();
        Assert.True((await git.ReadAsync(folder, default))!.Contained);
        Assert.Equal(2, runner.Fetches);
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("diverged")]
    [InlineData("stash")]
    [InlineData("operation")]
    [InlineData("index.lock")]
    [InlineData("HEAD.lock")]
    [InlineData("locked")]
    public async Task Missing_remote_unpublished_stash_and_mid_operation_proof_are_refused(string mode)
    {
        File.WriteAllText(Path.Combine(folder, ".git"), "fixture");
        if (mode is "operation" or "index.lock" or "HEAD.lock" or "locked")
            File.WriteAllText(Path.Combine(folder, mode == "operation" ? "MERGE_HEAD" : mode), "fixture");
        Assert.Null(await new SessionReaperGit(new FakeGit(folder) { Mode = mode }, log.Add).ReadAsync(folder, default));
        Assert.NotEmpty(log);
    }

    [Fact]
    public void Native_tools_are_not_inferred_from_idle_and_missing_custody_stays_unknown()
    {
        var session = new Session { Id = "fixture", Agent = AgentKind.Copilot, Title = "SG Reviewer fixture",
            Status = SessionStatus.Idle, StatusSince = clock.GetUtcNow().AddHours(-1), UpdatedAt = clock.GetUtcNow(),
            Activity = new() { Response = "VERDICT CLEAN", Background = new() { Shells = 1 } } };
        var row = SessionReaperHost.Native(session, "fixture-vm", clock.GetUtcNow());
        Assert.Equal(1, row.RunningTools);
        Assert.Null(row.Held); Assert.False(row.Private); Assert.False(row.Keep);
        Assert.Null(row.OpenOwnedPr); Assert.Null(row.WorkComplete);
        Assert.Null(row.Verdict);
        Assert.Null(SessionReaperHost.Native(session with { Activity = new() { Response = "VERDICT CLEAN\nVERDICT NOT CLEAN" } }, "fixture-vm", clock.GetUtcNow()).Verdict);
    }
}
