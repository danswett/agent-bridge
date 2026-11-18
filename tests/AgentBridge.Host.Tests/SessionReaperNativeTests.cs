using AgentBridge.Host.Core;
using AgentBridge.Host.Reaping;
using AgentBridge.Protocol;
using Xunit;

public sealed class SessionReaperNativeTests : IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), "ab-reaper-native-" + Guid.NewGuid().ToString("N"));
    readonly DateTimeOffset now = DateTimeOffset.UtcNow;
    public SessionReaperNativeTests() { Directory.CreateDirectory(folder); File.WriteAllText(Path.Combine(folder, ".git"), "fixture"); }
    public void Dispose() => Directory.Delete(folder, true);
    Session Session(string id = "a", string role = "Builder") => new()
    {
        Id = id, Title = $"SG {role} {id}", Agent = AgentKind.Copilot, Cwd = folder,
        Status = SessionStatus.Idle, StatusSince = now.AddHours(-2), UpdatedAt = now,
        Activity = new() { Response = "Finished." },
    };

    [Fact]
    public void Marker_absence_is_known_but_missing_workspace_is_not_and_any_marker_entry_holds()
    {
        var session = Session();
        var row = SessionReaperHost.Native(session, "fixture-vm", now);
        Assert.False(row.Held); Assert.False(row.Private); Assert.False(row.Keep);
        Assert.Null(SessionReaperHost.Native(session with { Cwd = Path.Combine(folder, "missing") }, "fixture-vm", now).Held);
        var marker = Path.Combine(folder, ".slipgates-hold");
        File.WriteAllText(marker, "");
        Assert.True(SessionReaperHost.Native(session, "fixture-vm", now).Held);
        File.Delete(marker); Directory.CreateDirectory(marker);
        Assert.True(SessionReaperHost.Native(session, "fixture-vm", now).Held);
    }

    [Fact]
    public async Task Native_builder_no_longer_needs_invented_completion_or_PR_fields()
    {
        var runner = new SessionReaperLiveTests.FakeGit(folder);
        var git = new SessionReaperGit(runner, _ => { });
        var row = SessionReaperHost.Native(Session(), "fixture-vm", now) with { Git = await git.ReadAsync(folder, default) };
        var snapshot = new ReaperSnapshot("fixture-vm", now, true, true, false, [row]);
        Assert.True(Assert.Single(SessionReaperPolicy.Plan(snapshot, now)).Eligible);
        Assert.Equal(1, runner.Fetches);
        Assert.False(Assert.Single(SessionReaperPolicy.Plan(snapshot, now, protectedNames: [row.Name])).Eligible);
        File.WriteAllText(Path.Combine(folder, ".slipgates-hold"), "");
        row = SessionReaperHost.Native(Session(), "fixture-vm", now) with { Git = row.Git };
        Assert.False(Assert.Single(SessionReaperPolicy.Plan(snapshot with { Sessions = [row] }, now)).Eligible);
    }

    [Fact]
    public async Task Last_own_verdict_survives_newer_chatter_and_ignores_prompt_tools_and_delegates()
    {
        var source = new HistorySource
        {
            Page = before => before is null ? Page([Part("Finished."),
                Part("VERDICT CLEAN") with { Kind = HistoryPartKind.Tool },
                Part("VERDICT CLEAN") with { Kind = HistoryPartKind.Delegate }]) with { Before = "older", Start = null }
                : Page([Part("**NOT CLEAN**", -60)]),
        };
        var verdict = await SessionReaperHost.VerdictAsync(source, "fixture", now, default);
        Assert.Equal(ReaperVerdict.NotClean, verdict.Verdict); Assert.Equal(now.AddMinutes(-60), verdict.At);
        Assert.Equal(2, source.Reads);
        ReaperSession Reviewer(string id) => SessionReaperHost.Native(Session(id, "Reviewer"), "fixture-vm", now)
            with { Verdict = verdict.Verdict, VerdictAt = verdict.At };
        var snapshot = new ReaperSnapshot("fixture-vm", now, true, true, false, [Reviewer("a"), Reviewer("b")]);
        Assert.Single(SessionReaperPolicy.Plan(snapshot, now), row => row.Eligible);
        source.Page = _ => Page([Part("VERDICT CLEAN", -120), Part("Review: NOT CLEAN", -59), Part("Done.")]);
        verdict = await SessionReaperHost.VerdictAsync(source, "fixture", now, default);
        Assert.Equal(now.AddMinutes(-59), verdict.At);
        Assert.DoesNotContain(SessionReaperPolicy.Plan(snapshot with { Sessions = [Reviewer("a"), Reviewer("b")] }, now), row => row.Eligible);
        source.Page = _ => Page([Part("**VERDICT CLEAN**")]);
        Assert.Equal(ReaperVerdict.Clean, (await SessionReaperHost.VerdictAsync(source, "fixture", now, default)).Verdict);
    }

    [Theory]
    [InlineData("partial")]
    [InlineData("truncated")]
    [InlineData("unreadable")]
    [InlineData("missing")]
    [InlineData("unavailable")]
    [InlineData("ambiguous")]
    [InlineData("future")]
    [InlineData("unknown")]
    [InlineData("cursor")]
    public async Task Uncertain_history_cannot_reuse_an_older_clean_verdict(string mode)
    {
        var response = Part("VERDICT CLEAN", -120);
        var page = Page([response, mode switch
        {
            "ambiguous" => Part("VERDICT CLEAN; NOT CLEAN"),
            "future" => response with { At = now.AddMinutes(1) },
            "unknown" => response with { Kind = HistoryPartKind.Unknown },
            _ => Part("Finished.") with { Truncated = mode == "truncated" },
        }]);
        page = mode switch
        {
            "partial" => page with { Partial = true }, "missing" => HistoryResult.Gone,
            "unavailable" => HistoryResult.NotAvailable("fixture"),
            "unreadable" => page with { Turns = [page.Turns![0] with { Unreadable = true }] },
            "cursor" => Page([Part("Finished.")]) with { Before = "repeated", Start = null },
            _ => page,
        };
        var source = new HistorySource { Page = _ => page };
        Assert.Null((await SessionReaperHost.VerdictAsync(source, "fixture", now, default)).Verdict);
        Assert.InRange(source.Reads, 1, 2);
    }

    [Fact]
    public void Final_stop_guard_rechecks_markers_questions_tools_identity_and_new_activity()
    {
        var session = Session();
        var row = SessionReaperHost.Native(session, "fixture-vm", now) with { Git = SessionReaperPolicyTests.Builder().Git };
        var expected = Assert.Single(SessionReaperPolicy.Plan(new("fixture-vm", now, true, true, false, [row]), now));
        var config = new SessionReaperSettings { Live = true };
        Assert.True(SessionReaperHost.StillSafe(expected, session, config, now));
        foreach (var changed in new[] { session with { UpdatedAt = now.AddSeconds(1) }, session with { Id = "other" },
            session with { Title = "SG Builder other" }, session with { Activity = new() { Response = "A question?" } },
            session with { Activity = new() { Background = new() { Shells = 1 } } } })
            Assert.False(SessionReaperHost.StillSafe(expected, changed, config, now));
        Assert.False(SessionReaperHost.StillSafe(expected, session, config with { ProtectedNames = [row.Name] }, now));
        File.WriteAllText(Path.Combine(folder, ".slipgates-hold"), "");
        Assert.False(SessionReaperHost.StillSafe(expected, session, config, now));
    }

    HistoryPart Part(string text, int minutes = 0) => new()
        { Id = Guid.NewGuid().ToString("N"), Kind = HistoryPartKind.Response, Text = text, At = now.AddMinutes(minutes) };
    HistoryResult Page(HistoryPart[] parts) => new()
    {
        Turns = [new() { Id = Guid.NewGuid().ToString("N"), At = now.AddHours(-2), Kind = TurnKind.User, Parts = parts,
            Prompt = new() { Text = "VERDICT CLEAN" } }], Start = new(),
    };
    sealed class HistorySource : ISessionSource
    {
        public required Func<string?, HistoryResult> Page; public int Reads;
        public bool ServesHistory => true;
        public Task<HistoryResult> HistoryAsync(string id, string? before, int limit, CancellationToken cancellationToken = default)
        { Reads++; return Task.FromResult(Page(before)); }
        public long Revision => 0;
        public event EventHandler<SessionChange>? Changed { add { } remove { } }
        public IReadOnlyList<Session> Snapshot() => [];
        public SessionList List(string? cursor = null) => throw new NotSupportedException();
        public Session? Get(string id) => null;
        public SessionSubscription Subscribe() => throw new NotSupportedException();
        public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
