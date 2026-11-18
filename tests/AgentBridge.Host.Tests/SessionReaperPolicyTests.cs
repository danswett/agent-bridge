using AgentBridge.Host;
using AgentBridge.Host.Reaping;
using Xunit;

public sealed class SessionReaperPolicyTests
{
    static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-18T05:00:00Z");
    internal static ReaperSession Builder(string id = "fixture-1") => new(id, $"SG Builder {id}", "fixture-vm")
    {
        Role = ReaperRole.Builder, State = "idle", At = Now, IdleSince = Now.AddMinutes(-60),
        RunningTools = 0, AskingQuestion = false, Held = false, Private = false, Keep = false,
        OpenOwnedPr = false, WorkComplete = true,
        Git = new(true, true, true, false, new('a', 40), new('b', 40), 0, 0),
    };
    internal static ReaperSnapshot Snapshot(params ReaperSession[] rows) => new("fixture-vm", Now, true, true, false, rows);
    static IReadOnlyList<ReaperDecision> Plan(params ReaperSession[] rows) => SessionReaperPolicy.Plan(Snapshot(rows), Now);

    [Fact]
    public void Same_name_remote_ancestry_needs_no_tracking_upstream()
    {
        Assert.True(Assert.Single(Plan(Builder())).Eligible);
        Assert.False(Assert.Single(Plan(Builder() with { Git = Builder().Git! with { Contained = false } })).Eligible);
    }

    [Theory]
    [InlineData("Lead")]
    [InlineData("Chief")]
    [InlineData("Reader")]
    [InlineData("F-Chain")]
    [InlineData("Builder 618")]
    [InlineData("Builder KEEP")]
    [InlineData("Builder held")]
    [InlineData("Builder private")]
    [InlineData("Builder Reviewer")]
    public void Protected_or_ambiguous_names_stay(string name) =>
        Assert.False(Assert.Single(Plan(Builder() with { Name = $"SG {name}" })).Eligible);

    [Theory]
    [InlineData("tools")]
    [InlineData("question")]
    [InlineData("hold")]
    [InlineData("private")]
    [InlineData("keep")]
    [InlineData("dirty")]
    [InlineData("stash")]
    [InlineData("operation")]
    [InlineData("recent")]
    [InlineData("future")]
    public void Unsafe_or_unknown_facts_are_not_grants(string field)
    {
        var row = Builder();
        row = field switch
        {
            "tools" => row with { RunningTools = null }, "question" => row with { AskingQuestion = true },
            "hold" => row with { Held = null }, "private" => row with { Private = null }, "keep" => row with { Keep = null },
            "dirty" => row with { Git = row.Git! with { Clean = false } },
            "stash" => row with { Git = row.Git! with { StashCount = 1 } },
            "operation" => row with { Git = row.Git! with { MidOperation = true } },
            "recent" => row with { IdleSince = Now.AddMinutes(-29) },
            _ => row with { At = Now.AddSeconds(1) },
        };
        Assert.False(Assert.Single(Plan(row)).Eligible);
    }

    [Fact]
    public void Fully_pushed_builder_completion_is_derived_even_with_an_open_PR()
    {
        Assert.True(Assert.Single(Plan(Builder() with { OpenOwnedPr = true, WorkComplete = null })).Eligible);
        Assert.True(Assert.Single(Plan(Builder() with { OpenOwnedPr = null, WorkComplete = false })).Eligible);
    }

    [Fact]
    public void Unknown_host_stale_discovery_duplicates_and_explicit_protection_stay()
    {
        var snapshot = Snapshot(Builder());
        foreach (var bad in new[] { snapshot with { IsVm = null }, snapshot with { OwnerLaptop = true },
            snapshot with { Complete = false }, snapshot with { At = Now.AddSeconds(-91) } })
            Assert.False(Assert.Single(SessionReaperPolicy.Plan(bad, Now)).Eligible);
        Assert.All(Plan(Builder(), Builder()), row => Assert.False(row.Eligible));
        Assert.False(Assert.Single(SessionReaperPolicy.Plan(snapshot, Now, ["fixture-1"])).Eligible);
    }

    [Fact]
    public void Each_machine_keeps_one_local_reviewer_and_requires_an_explicit_verdict()
    {
        var a = Builder("a") with { Name = "SG Reviewer a", Role = ReaperRole.Reviewer,
            Verdict = ReaperVerdict.Clean, VerdictAt = Now };
        var b = a with { Id = "b", Name = "SG Reviewer b" };
        Assert.Single(Plan(a, b), row => row.Eligible);
        Assert.False(Assert.Single(Plan(a)).Eligible);
        Assert.DoesNotContain(Plan(a, b with { Machine = "elsewhere" }), row => row.Eligible);
        foreach (var bad in new[] { b with { Verdict = null }, b with { Verdict = ReaperVerdict.NotClean, VerdictAt = Now.AddMinutes(-59) } })
            Assert.DoesNotContain(Plan(a, bad), row => row.Eligible);
        Assert.Single(Plan(a, b with { Verdict = ReaperVerdict.NotClean, VerdictAt = Now.AddMinutes(-60) }), row => row.Eligible);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"reaper":{"live":"true"}}""")]
    [InlineData("""{"reaper":{"live":true,"live":false}}""")]
    [InlineData("""{"reaper":{"live":false},"reaper":{"live":true}}""")]
    [InlineData("""{"reaper":{"protectedIds":[null]}}""")]
    public void Absent_or_broken_reaper_config_never_enables_live(string text) =>
        Assert.False(HostSettings.Parse(text, "fixture").Settings.Reaper.Live);

    [Fact]
    public void Typed_private_host_config_is_preserved()
    {
        var (settings, problem) = HostSettings.Parse("""{"reaper":{"live":true,"machineId":"fixture-vm","isVm":true,"ownerLaptop":false,"protectedIds":["held"]}}""", "fixture");
        Assert.Null(problem);
        Assert.True(settings.Reaper.Live);
        Assert.Equal("held", Assert.Single(settings.Reaper.ProtectedIds));
    }
}
