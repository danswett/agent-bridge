using AgentBridge.Host.Core;
using AgentBridge.Host.Core.Restore;
using AgentBridge.Protocol;
using Microsoft.Extensions.Time.Testing;
using Xunit;

// The host's record of live sessions and what it finds at start of those an earlier run saw (#780;
// docs/host-launch.md, Restoring after a reboot or a crash). A fake source, clock and boot time, so
// every judgement and every write is exact. Nothing is started by any of it.
public sealed class RestoreTests : IDisposable
{
    readonly Fixture f = new();
    readonly Live source = new();
    readonly Ends ends = new();
    readonly FixedBoot boot = new(Fixture.T0);
    readonly List<RestoreNotice> told = [];

    public void Dispose() => f.Dispose();

    FakeTimeProvider Time => f.Time;
    string Record => Path.Combine(f.Root, RestoreFiles.FileName);

    RestoreFile Written() => RestoreFiles.Read(Record).File!;

    RestoreRecorder Recorder(bool restoreOnStart = true, bool freshLogon = false, Func<AgentKind, bool>? endsSettled = null, TimeProvider? clock = null,
        Func<string?>? readOnly = null, List<string>? logs = null, ILogonMarker? marker = null)
    {
        var r = new RestoreRecorder(source, new RestoreOptions
        {
            Path = Record, Instance = "run2", Ends = ends, RestoreOnStart = restoreOnStart, Boot = boot, Logon = marker ?? new Logon(freshLogon), Time = clock ?? Time, EndsSettled = endsSettled,
            ReadOnly = readOnly ?? (() => null), Log = logs is null ? null : s => logs.Add(s),
        });
        r.NoticeReady += told.Add;
        return r;
    }

    // What an earlier run of the host wrote, booted then, stopped cleanly or not.
    void Previous(DateTimeOffset booted, bool stopped, params RestoreEntry[] sessions) => RestoreFiles.Write(Record, new RestoreFile
    {
        Boot = booted,
        Instance = "run1",
        Written = Time.GetUtcNow().AddMinutes(-5),
        Stopped = stopped ? Time.GetUtcNow().AddMinutes(-5) : null,
        Sessions = sessions,
    });

    static RestoreEntry E(string id, AgentKind agent = AgentKind.Copilot, RestoreTurn turn = RestoreTurn.Idle, bool question = false, string? cwd = "/work/repo") =>
        new() { Id = id, Agent = agent, Title = "T " + id, Cwd = cwd, Turn = turn, QuestionWaiting = question, Seen = Fixture.T0 };

    static Session S(string id, SessionStatus status = SessionStatus.Idle, string? response = null) => new()
    {
        Id = id,
        Agent = AgentKind.Copilot,
        Title = "T " + id,
        Cwd = "/work/repo",
        Status = status,
        StatusSince = Fixture.T0,
        UpdatedAt = Fixture.T0,
        Activity = new Activity { Response = response },
    };

    [Fact]
    public void A_record_is_replaced_whole_and_a_write_cut_short_by_a_crash_leaves_the_one_before_readable()
    {
        Previous(Fixture.T0, false, E("a"));
        File.WriteAllText(Record + ".0123.tmp", "{ \"v\": 1, \"boot\": ");
        Previous(Fixture.T0, false, E("b"));

        var (file, problem) = RestoreFiles.Read(Record);

        Assert.Null(problem);
        Assert.Equal(["b"], file!.Sessions.Select(s => s.Id));
        Assert.Equal([Record + ".0123.tmp"], Directory.GetFiles(f.Root, "*.tmp"));
    }

    [Fact]
    public void The_cause_is_a_reboot_across_boots_a_restart_after_a_clean_stop_and_otherwise_a_crash()
    {
        var file = new RestoreFile { Boot = Fixture.T0, Instance = "run1", Written = Fixture.T0 };

        Assert.Equal(RestoreCause.Reboot, RestoreJudge.CauseOf(file, Fixture.T0.AddHours(1)));
        // A boot time read against a clock that moved a little is the same boot.
        Assert.Equal(RestoreCause.Crash, RestoreJudge.CauseOf(file, Fixture.T0.AddSeconds(30)));
        Assert.Equal(RestoreCause.Restart, RestoreJudge.CauseOf(file with { Stopped = Fixture.T0 }, Fixture.T0));
        // A sign-out, or a shutdown with Fast Startup, keeps the boot time and ends every agent.
        Assert.Equal(RestoreCause.Reboot, RestoreJudge.CauseOf(file with { Stopped = Fixture.T0 }, Fixture.T0, freshLogon: true));
    }

    [Fact]
    public async Task On_a_fresh_logon_a_session_seen_going_or_written_as_ended_is_offered_as_after_a_reboot()
    {
        Previous(Fixture.T0, true, E("a") with { Gone = Fixture.T0 }, E("b"));
        ends.Of["b"] = true;
        await using var r = Recorder(freshLogon: true);

        r.Start();

        var n = Assert.Single(told);
        Assert.Equal(RestoreCause.Reboot, n.Cause);
        Assert.Equal(["a", "b"], n.Offered.Select(o => o.Id).Order(StringComparer.Ordinal));
    }

    // Copilot on #813: the logon is marked seen only once a start has written its record. Marked
    // first, a start that went down before its first write left the old record, and the next start,
    // reading the same logon, took the sessions the sign-out ended as ending normally.
    [Fact]
    public async Task A_fresh_logon_stays_fresh_for_the_next_start_when_the_first_goes_down_before_writing_its_record()
    {
        Previous(Fixture.T0, true, E("b"));
        ends.Of["b"] = true;
        var key = new VolatileKey();
        // Goes down after reading the logon and judging, before its first write (and never stops cleanly).
        await using var first = Recorder(marker: key, readOnly: () => throw new InvalidOperationException("broken"));
        first.Start();
        Assert.Equal("run1", Written().Instance);
        Assert.False(key.Made);

        await using var second = Recorder(marker: key);
        second.Start();

        var n = Assert.Single(told);
        Assert.Equal((RestoreCause.Reboot, "b"), (n.Cause, Assert.Single(n.Offered).Id));
        Assert.Equal(["/work/repo"], second.Folders()!);
        Assert.True(key.Made);
    }

    [Fact]
    public async Task Sessions_an_earlier_run_left_offered_stay_offered_but_are_not_pushed_again()
    {
        Previous(Fixture.T0, false, E("a"));
        await using (var first = Recorder())
        {
            first.Start();
        }
        Assert.Single(told);
        told.Clear();
        source.Sessions.Add(S("b"));

        await using var second = Recorder();
        second.Start();

        Assert.Empty(told);
        Assert.Equal(["a"], second.Notice!.Offered.Select(o => o.Id));
        Assert.Single(second.Folders()!);
    }

    [Fact]
    public async Task With_restore_on_start_turned_off_sessions_left_offered_by_an_earlier_run_are_let_go()
    {
        Previous(Fixture.T0, false, E("a"));
        await using (var first = Recorder())
        {
            first.Start();
        }

        await using var second = Recorder(restoreOnStart: false);
        second.Start();

        Assert.Null(second.Notice);
        Assert.Empty(second.Folders()!);
        Assert.Empty(Written().Owed);
    }

    [Fact]
    public async Task Sessions_that_died_with_a_crashed_host_are_offered_and_ones_running_or_ended_are_not()
    {
        Previous(Fixture.T0, false, E("running"), E("ended"), E("seen-going") with { Gone = Fixture.T0 },
            E("died", turn: RestoreTurn.Working), E("asked", AgentKind.Claude, RestoreTurn.Waiting, question: true));
        source.Sessions.Add(S("running"));
        ends.Of["ended"] = true;
        ends.Of["died"] = false;
        await using var r = Recorder();

        r.Start();

        var n = Assert.Single(told);
        Assert.Equal((RestoreCause.Crash, (RestoreWhy?)RestoreWhy.Pending, 0), (n.Cause, n.Why, n.Waiting));
        Assert.Equal(["asked", "died"], n.Offered.Select(o => o.Id).Order(StringComparer.Ordinal));
        var died = n.Offered.Single(o => o.Id == "died");
        Assert.Equal(((bool?)true, (bool?)null), (died.MidTurn, died.QuestionLost));
        Assert.True(n.Offered.Single(o => o.Id == "asked").QuestionLost);
        Assert.Equal(["asked", "died"], r.Notice!.Offered.Select(o => o.Id).Order(StringComparer.Ordinal));
        Assert.Equal(["/work/repo", "/work/repo"], r.Folders()!);
        Assert.Equal(["asked", "died"], Written().Owed.Select(w => w.Entry.Id).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task After_a_reboot_every_session_that_was_live_is_offered_even_one_its_agent_wrote_as_ended()
    {
        Previous(Fixture.T0.AddDays(-1), false, E("a"));
        ends.Of["a"] = true;
        await using var r = Recorder();

        r.Start();

        var n = Assert.Single(told);
        Assert.Equal(RestoreCause.Reboot, n.Cause);
        Assert.Equal("a", Assert.Single(n.Offered).Id);
    }

    [Fact]
    public async Task A_session_whose_agents_discovery_is_held_waits_and_is_never_read_as_gone()
    {
        Previous(Fixture.T0, false, E("a"), E("b", AgentKind.Claude));
        ends.Of["a"] = false;
        source.HeldNow = new DiscoveryHeld("the copilot processes could not be listed", [AgentKind.Copilot]);
        await using var r = Recorder();

        r.Start();

        // b has died; a cannot be told yet, so the push waits for it and its worktree is kept.
        Assert.Empty(told);
        var waiting = r.Notice!;
        Assert.Equal((1, 1), (waiting.Waiting, waiting.Offered.Count));
        Assert.Equal(["/work/repo", "/work/repo"], r.Folders()!);

        source.HeldNow = null;
        Time.Advance(RestoreRecorder.JudgeEvery);

        var n = Assert.Single(told);
        Assert.Equal((0, 2), (n.Waiting, n.Offered.Count));
    }

    [Fact]
    public async Task A_hold_that_outlasts_ten_minutes_sends_the_notice_with_what_is_known_once()
    {
        Previous(Fixture.T0, false, E("a"), E("b", AgentKind.Claude));
        source.HeldNow = new DiscoveryHeld("the copilot processes could not be listed", [AgentKind.Copilot]);
        await using var r = Recorder();
        r.Start();

        Time.Advance(RestoreRecorder.NoticeAfter);
        Time.Advance(RestoreRecorder.NoticeAfter);

        var n = Assert.Single(told);
        Assert.Equal((1, "b"), (n.Waiting, Assert.Single(n.Offered).Id));
    }

    [Fact]
    public async Task With_restore_on_start_off_what_died_is_said_and_nothing_is_kept_waiting()
    {
        Previous(Fixture.T0, false, E("a"));
        await using var r = Recorder(restoreOnStart: false);

        r.Start();

        var n = Assert.Single(told);
        Assert.Equal(((RestoreWhy?)RestoreWhy.Off, "a"), (n.Why, Assert.Single(n.Offered).Id));
        Assert.Empty(r.Folders()!);
        Assert.Empty(Written().Owed);
    }

    [Fact]
    public async Task A_session_resumed_from_launch_is_no_longer_offered_and_frees_its_worktree()
    {
        Previous(Fixture.T0, false, E("a"));
        await using var r = Recorder();
        r.Start();
        Assert.Single(r.Folders()!);

        source.Upsert(S("a"));
        Time.Advance(RestoreRecorder.Coalesce);

        Assert.Null(r.Notice);
        Assert.Empty(r.Folders()!);
        Assert.Empty(Written().Owed);
        Assert.Equal(["a"], Written().Sessions.Select(s => s.Id));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("""{ "v": 1, "boot": "not a time", "instance": "run1", "written": "2026-10-28T04:00:00.000Z" }""")]
    [InlineData("""{ "v": 1, "boot": "2026-10-28T04:00:00.000Z", "instance": "run1", "written": "2026-10-28T04:00:00.000Z", "sessions": [null] }""")]
    public async Task An_unreadable_record_is_set_aside_offers_nothing_and_keeps_every_worktree(string text)
    {
        File.WriteAllText(Record, text);
        await using var r = Recorder();

        r.Start();

        Assert.Null(r.Folders());
        Assert.Null(r.Notice);
        Assert.Empty(told);
        Assert.Equal(text, File.ReadAllText(Record + ".unreadable"));
        Assert.Equal("run2", Written().Instance);
    }

    [Fact]
    public async Task A_session_that_goes_stays_in_the_record_for_a_minute_and_a_clean_stop_says_so()
    {
        source.Sessions.AddRange([S("a", SessionStatus.Working), S("b")]);
        var r = Recorder();
        r.Start();
        var first = Written();
        Assert.Equal((Fixture.T0, "run2", (DateTimeOffset?)null), (first.Boot, first.Instance, first.Stopped));
        Assert.Equal(RestoreTurn.Working, first.Sessions.Single(s => s.Id == "a").Turn);

        source.Remove("b");
        Time.Advance(RestoreRecorder.Coalesce);
        Assert.NotNull(Written().Sessions.Single(s => s.Id == "b").Gone);
        Time.Advance(RestoreRecorder.GoneKept);
        Assert.Equal(["a"], Written().Sessions.Select(s => s.Id));

        await r.DisposeAsync();

        Assert.NotNull(Written().Stopped);
        Assert.Equal(["a"], Written().Sessions.Select(s => s.Id));
    }

    [Fact]
    public async Task A_change_of_activity_alone_is_not_written_and_a_change_of_turn_is()
    {
        source.Sessions.Add(S("a"));
        await using var r = Recorder();
        r.Start();
        var written = Written().Written;

        Time.Advance(TimeSpan.FromSeconds(1));
        source.Upsert(S("a", response: "more of the answer"));
        Time.Advance(RestoreRecorder.Coalesce);
        Assert.Equal(written, Written().Written);

        source.Upsert(S("a", SessionStatus.Working));
        Time.Advance(RestoreRecorder.Coalesce);
        Assert.Equal(RestoreTurn.Working, Assert.Single(Written().Sessions).Turn);
    }

    [Fact]
    public async Task A_session_launched_through_the_host_keeps_what_it_was_launched_with()
    {
        await using var r = Recorder();
        r.Start();

        r.Launched("a", new LaunchedWith { Agent = "agency", Workspace = "Bridge", Worktree = true, Profile = "work", Model = "gpt-6.1-sol", Permissions = "ask" });
        source.Upsert(S("a"));
        Time.Advance(RestoreRecorder.Coalesce);

        Assert.Equal(new RestoreLaunch("agency", "Bridge", true, "work", "gpt-6.1-sol", null, null, "ask"), Assert.Single(Written().Sessions).Launch);
    }

    [Fact]
    public void A_copilot_session_ended_only_when_its_main_agent_shut_down_after_the_last_thing_typed_and_claude_only_when_its_registration_says_so()
    {
        var ended = SessionEnds.For(f.Options());
        var copilot = Guid.NewGuid().ToString();
        var events = f.CopilotSession(copilot, null);
        var claude = Guid.NewGuid().ToString();

        Assert.Null(ended.Ended(AgentKind.Copilot, copilot));
        Fixture.Append(events, Copilot.User("go", 1), Copilot.Event("session.shutdown", new { }, 2, agentId: "sub"));
        Assert.False(ended.Ended(AgentKind.Copilot, copilot));
        Fixture.Append(events, Copilot.Event("session.shutdown", new { }, 3));
        Assert.True(ended.Ended(AgentKind.Copilot, copilot));
        // A sub-agent's prompt after it is not the person typing.
        Fixture.Append(events, Copilot.User("look", 4, agentId: "sub"));
        Assert.True(ended.Ended(AgentKind.Copilot, copilot));
        // Resumed after it shut down, and lost before anything was typed: not the earlier ending.
        Fixture.Append(events, Copilot.Event("session.resume", new { }, 5));
        Assert.False(ended.Ended(AgentKind.Copilot, copilot));
        Fixture.Append(events, Copilot.Event("session.shutdown", new { }, 6));
        Assert.True(ended.Ended(AgentKind.Copilot, copilot));
        Fixture.Append(events, Copilot.User("and again", 7));
        Assert.False(ended.Ended(AgentKind.Copilot, copilot));
        Fixture.Append(events, Copilot.Event("session.shutdown", new { }, 8), Copilot.Event("session.start", new { }, 9));
        Assert.False(ended.Ended(AgentKind.Copilot, copilot));

        Assert.Null(ended.Ended(AgentKind.Claude, claude));
        Directory.CreateDirectory(f.ClaudeRegistrations);
        File.WriteAllText(Path.Combine(f.ClaudeRegistrations, claude + ".json"), "{ \"SessionId\": \"" + claude + "\", \"Ended\": false }");
        Assert.False(ended.Ended(AgentKind.Claude, claude));
        File.WriteAllText(Path.Combine(f.ClaudeRegistrations, claude + ".json"), "{ \"SessionId\": \"" + claude + "\", \"Ended\": true }");
        Assert.True(ended.Ended(AgentKind.Claude, claude));
    }

    [Fact]
    public async Task A_change_after_a_session_goes_is_written_within_the_coalescing_delay_not_with_the_gone_one()
    {
        source.Sessions.AddRange([S("a"), S("b")]);
        await using var r = Recorder();
        r.Start();
        source.Remove("b");
        Time.Advance(RestoreRecorder.Coalesce);

        source.Upsert(S("c"));
        Time.Advance(RestoreRecorder.Coalesce);

        Assert.Contains(Written().Sessions, e => e.Id == "c");
    }

    [Fact]
    public async Task A_session_still_running_across_a_host_restart_keeps_what_it_was_launched_with()
    {
        var launch = new RestoreLaunch("agency", "Bridge", true, "work", null, null, null, "ask");
        Previous(Fixture.T0, true, E("a") with { Launch = launch });
        source.Sessions.Add(S("a"));
        await using var r = Recorder();

        r.Start();

        Assert.Equal(launch, Assert.Single(Written().Sessions).Launch);
    }

    [Fact]
    public async Task An_offer_is_let_go_after_seven_days_while_the_host_keeps_running()
    {
        Previous(Fixture.T0, false, E("a"));
        await using var r = Recorder();
        r.Start();
        Assert.Single(r.Folders()!);

        Time.Advance(RestoreRecorder.OwedFor);
        Time.Advance(RestoreRecorder.Coalesce);

        Assert.Empty(r.Folders()!);
        Assert.Null(r.Notice);
        Assert.Empty(Written().Owed);
    }

    [Fact]
    public async Task A_session_still_being_judged_is_never_let_go_and_gets_its_full_week_once_offered()
    {
        Previous(Fixture.T0, false, E("a"));
        source.HeldNow = new DiscoveryHeld("the copilot processes could not be listed", [AgentKind.Copilot]);
        await using var r = Recorder();
        r.Start();

        Time.Advance(RestoreRecorder.OwedFor + TimeSpan.FromDays(1));
        Assert.Single(r.Folders()!);

        source.HeldNow = null;
        Time.Advance(RestoreRecorder.JudgeEvery);
        Time.Advance(RestoreRecorder.OwedFor - TimeSpan.FromDays(1));
        Assert.Single(r.Folders()!);
        Time.Advance(TimeSpan.FromDays(1));
        Assert.Empty(r.Folders()!);
    }
    [Fact]
    public async Task A_record_set_aside_keeps_every_worktree_on_later_starts_until_it_is_removed()
    {
        File.WriteAllText(Record, "{ not json");
        await using (var first = Recorder())
        {
            first.Start();
        }

        await using var second = Recorder();
        second.Start();
        Assert.Null(second.Folders());

        File.Delete(Record + ".unreadable");
        Assert.NotNull(second.Folders());
    }

    [Fact]
    public async Task A_session_still_being_judged_when_the_host_stopped_is_judged_as_of_a_reboot_since_and_pushed()
    {
        Previous(Fixture.T0, false, E("a"));
        source.HeldNow = new DiscoveryHeld("the copilot processes could not be listed", [AgentKind.Copilot]);
        await using (var first = Recorder())
        {
            first.Start();
        }
        Assert.Empty(told);
        source.HeldNow = null;
        ends.Of["a"] = true;

        await using var second = Recorder(freshLogon: true);
        second.Start();

        var n = Assert.Single(told);
        Assert.Equal((RestoreCause.Reboot, "a"), (n.Cause, Assert.Single(n.Offered).Id));
    }

    [Fact]
    public async Task The_push_names_only_what_this_start_found_and_hello_names_every_offer()
    {
        Previous(Fixture.T0, false, E("a"));
        source.Sessions.Add(S("b"));
        await using (var first = Recorder())
        {
            first.Start();
        }
        told.Clear();
        source.Sessions.Clear();

        await using var second = Recorder();
        second.Start();

        Assert.Equal("b", Assert.Single(Assert.Single(told).Offered).Id);
        Assert.Equal(["a", "b"], second.Notice!.Offered.Select(o => o.Id).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_claude_session_waits_until_its_hooks_endings_are_taken_in_before_it_is_judged_and_keeps_registrations_from_being_pruned_till_then()
    {
        Previous(Fixture.T0, false, E("a", AgentKind.Claude));
        var settled = false;
        await using var r = Recorder(endsSettled: agent => agent != AgentKind.Claude || settled);
        r.Start();
        var judging = r.Notice!;
        Assert.Equal((1, 0), (judging.Waiting, judging.Offered.Count));
        Assert.True(r.JudgingEndings);

        ends.Of["a"] = true;
        settled = true;
        Time.Advance(RestoreRecorder.JudgeEvery);

        Assert.Null(r.Notice);
        Assert.Empty(told);
        Assert.False(r.JudgingEndings);
    }

    [Fact]
    public void Of_a_registration_in_two_roots_the_one_written_last_says_whether_the_session_ended()
    {
        var older = Path.Combine(f.Root, "older");
        var newer = Path.Combine(f.Root, "newer");
        Directory.CreateDirectory(older);
        Directory.CreateDirectory(newer);
        var id = Guid.NewGuid().ToString();
        File.WriteAllText(Path.Combine(older, id + ".json"), "{ \"SessionId\": \"" + id + "\", \"Updated\": \"2026-10-28T04:00:00.000Z\", \"Ended\": true }");
        File.WriteAllText(Path.Combine(newer, id + ".json"), "{ \"SessionId\": \"" + id + "\", \"Updated\": \"2026-10-28T05:00:00.000Z\", \"Ended\": false }");

        Assert.False(new SessionEnds(f.CopilotHome, [older, newer], []).Ended(AgentKind.Claude, id));
    }

    [Fact]
    public async Task A_session_still_being_judged_when_the_host_stopped_and_found_running_keeps_what_it_was_launched_with()
    {
        var launch = new RestoreLaunch("agency", "Bridge", true, "work", null, null, null, "ask");
        Previous(Fixture.T0, false, E("a") with { Launch = launch });
        source.HeldNow = new DiscoveryHeld("the copilot processes could not be listed", [AgentKind.Copilot]);
        await using (var first = Recorder())
        {
            first.Start();
        }
        source.HeldNow = null;
        source.Sessions.Add(S("a"));

        await using var second = Recorder();
        second.Start();

        Assert.Equal(launch, Assert.Single(Written().Sessions).Launch);
    }

    [Fact]
    public async Task A_session_found_as_a_hold_lifts_is_running_before_its_upsert_arrives()
    {
        Previous(Fixture.T0, false, E("a"));
        source.HeldNow = new DiscoveryHeld("the copilot processes could not be listed", [AgentKind.Copilot]);
        await using var r = Recorder();
        r.Start();

        // The source found it and lifted the hold; its event has not been dispatched yet.
        source.Sessions.Add(S("a"));
        source.HeldNow = null;
        Time.Advance(RestoreRecorder.JudgeEvery);
        Time.Advance(RestoreRecorder.Coalesce);

        Assert.Null(r.Notice);
        Assert.Empty(told);
        // Written as live before its event arrives, so a crash now still keeps it.
        Assert.Equal(["a"], Written().Sessions.Select(e => e.Id));
        Assert.Empty(Written().Owed);
    }

    [Fact]
    public async Task An_ending_read_while_discovery_is_held_counts_after_its_registration_is_pruned()
    {
        Previous(Fixture.T0, false, E("a", AgentKind.Codex));
        source.HeldNow = new DiscoveryHeld("the codex processes could not be listed", [AgentKind.Codex]);
        ends.Of["a"] = true;
        await using var r = Recorder();
        r.Start();

        ends.Of.Remove("a");
        source.HeldNow = null;
        Time.Advance(RestoreRecorder.JudgeEvery);

        Assert.Null(r.Notice);
        Assert.Empty(r.Folders()!);
        Assert.Empty(told);
    }

    // Quality, #813: a clock set back leaves an offer dated ahead of it. Read as dated now, it is kept
    // (and its worktree with it) and let go a week on, and no timer is ever asked for past what the
    // system's timers take, which stopped the host at every start.
    [Fact]
    public async Task An_offer_dated_sixty_days_ahead_by_a_clock_set_back_is_kept_and_does_not_stop_the_host()
    {
        var now = Time.GetUtcNow();
        RestoreFiles.Write(Record, new RestoreFile
        {
            Boot = Fixture.T0, Instance = "run1", Written = now.AddDays(60),
            Owed = [new RestoreOwed(E("a"), RestoreCause.Reboot, now.AddDays(60), now.AddDays(60), Decided: true)],
        });
        var clock = new SystemLimits(Time);
        await using var r = Recorder(clock: clock);

        r.Start();

        Assert.Equal(["/work/repo"], r.Folders()!);
        Assert.Equal(now, Assert.Single(Written().Owed).At);
        clock.Advance(RestoreRecorder.OwedFor - TimeSpan.FromMinutes(1));
        Assert.Single(r.Folders()!);
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Empty(r.Folders()!);
    }

    [Fact]
    public async Task A_clock_set_back_while_the_host_runs_neither_stops_it_nor_lets_an_offer_go()
    {
        Previous(Fixture.T0, false, E("a"));
        var clock = new SystemLimits(Time);
        var logs = new List<string>();
        await using var r = Recorder(clock: clock, logs: logs);
        r.Start();
        Assert.Single(told);

        clock.SetBack(TimeSpan.FromDays(60));
        clock.Advance(RestoreRecorder.OwedFor);

        Assert.Single(r.Folders()!);
        Assert.DoesNotContain(logs, l => l.StartsWith("restore: could not", StringComparison.Ordinal));
    }

    // Copilot on #813: how long the notice is shown, and how long a hold waits before the push, are
    // elapsed time. On the wall clock, one set back days kept the push back and the notice up for days.
    [Fact]
    public async Task A_clock_set_back_stretches_neither_the_wait_for_a_held_session_nor_the_notice()
    {
        Previous(Fixture.T0, false, E("a"), E("b", AgentKind.Claude));
        source.HeldNow = new DiscoveryHeld("the copilot processes could not be listed", [AgentKind.Copilot]);
        var clock = new SystemLimits(Time);
        await using var r = Recorder(clock: clock);
        r.Start();
        Assert.Empty(told);

        clock.SetBack(TimeSpan.FromDays(3));
        clock.Advance(RestoreRecorder.NoticeAfter + RestoreRecorder.JudgeEvery);
        Assert.Equal(1, Assert.Single(told).Waiting);

        source.HeldNow = null;
        ends.Of["a"] = false;
        clock.Advance(RestoreProtocol.ShownFor);
        Assert.Null(r.Notice);
    }

    // Copilot on #813: a record that cannot be moved aside is never written over. Swallowed, the run
    // wrote its own record in its place, and the next start read that, with no .unreadable to keep
    // the worktrees of the sessions in the one it lost.
    [Fact]
    public async Task A_record_that_cannot_be_set_aside_is_never_written_over_and_keeps_every_worktree()
    {
        File.WriteAllText(Record, "{ not json");
        // In the way of the move: a folder, with something in it, that a file cannot replace.
        Directory.CreateDirectory(Path.Combine(Record + ".unreadable", "held"));
        source.Sessions.Add(S("a"));
        var logs = new List<string>();
        await using (var r = Recorder(logs: logs))
        {
            r.Start();
            source.Upsert(S("a", SessionStatus.Working));
            Time.Advance(RestoreRecorder.Coalesce);

            Assert.Null(r.Folders());
            Assert.Null(r.Notice);
        }

        Assert.Equal("{ not json", File.ReadAllText(Record));
        Assert.Contains(logs, l => l.StartsWith("restore: could not start", StringComparison.Ordinal));
    }

    // Copilot on #813: what each session was launched with is kept for every one the last run
    // recorded. Capped at the launches kept, the rest were written again without it.
    [Fact]
    public async Task Every_live_session_keeps_what_it_was_launched_with_however_many_the_last_run_recorded()
    {
        var launch = new RestoreLaunch("agency", "Bridge", false, null, null, null, null, "ask");
        var entries = Enumerable.Range(0, 300).Select(i => E($"s{i:D3}") with { Launch = launch }).ToArray();
        Previous(Fixture.T0, false, entries);
        source.Sessions.AddRange(entries.Select(e => S(e.Id)));
        await using var r = Recorder();

        r.Start();

        var written = Written().Sessions;
        Assert.Equal(300, written.Count);
        Assert.All(written, s => Assert.Equal(launch, s.Launch));
    }

    // Copilot on #813: launches past the ones kept let go of those not seen live, and a session held
    // undecided is not live yet. Let go too, it was adopted with no settings once the hold lifted.
    [Fact]
    public async Task A_session_held_undecided_keeps_what_it_was_launched_with_however_many_launches_come_first()
    {
        var launch = new RestoreLaunch("agency", "Bridge", true, "work", null, null, null, "ask");
        Previous(Fixture.T0, false, E("a") with { Launch = launch });
        source.HeldNow = new DiscoveryHeld("the copilot processes could not be listed", [AgentKind.Copilot]);
        await using var r = Recorder();
        r.Start();

        for (var i = 0; i < 300; i++) { r.Launched($"n{i:D3}", new LaunchedWith { Agent = "copilot", Permissions = "ask" }); }
        source.Sessions.Add(S("a"));
        source.HeldNow = null;
        Time.Advance(RestoreRecorder.JudgeEvery);
        Time.Advance(RestoreRecorder.Coalesce);

        Assert.Equal(launch, Assert.Single(Written().Sessions).Launch);
    }

    // Copilot on #813: the pending write's deadline is elapsed time, as its timer is. Compared on the
    // wall clock, one set forward while a gone session's minute ran held a new session back with it.
    [Fact]
    public async Task A_clock_set_forward_while_a_gone_session_is_kept_does_not_hold_back_the_next_change()
    {
        source.Sessions.AddRange([S("a"), S("b")]);
        var clock = new SystemLimits(Time);
        await using var r = Recorder(clock: clock);
        r.Start();
        source.Remove("b");
        clock.Advance(RestoreRecorder.Coalesce);

        clock.SetForward(TimeSpan.FromMinutes(5));
        source.Upsert(S("c"));
        clock.Advance(RestoreRecorder.Coalesce);

        Assert.Contains(Written().Sessions, e => e.Id == "c");
    }

    // Whatever stops the start is said, and every worktree is kept for the run: what is owed is not
    // known, and the host goes on serving rather than stop at every start.
    [Fact]
    public async Task A_start_that_fails_is_said_and_keeps_every_worktree()
    {
        Previous(Fixture.T0, false, E("a"));
        var logs = new List<string>();
        await using var r = Recorder(readOnly: () => throw new InvalidOperationException("broken"), logs: logs);

        r.Start();

        Assert.Null(r.Folders());
        Assert.Contains(logs, l => l.StartsWith("restore: could not start (InvalidOperationException: broken)", StringComparison.Ordinal));
    }

    // The system's limits on a timer, which FakeTimeProvider does not keep: a due time or a period
    // past Timer.MaxSupportedTimeout (0xFFFFFFFE ms) throws, as the system's does. And a clock that
    // can be set back, which FakeTimeProvider's cannot.
    sealed class SystemLimits(FakeTimeProvider inner) : TimeProvider
    {
        const double MaxMilliseconds = 0xFFFFFFFE;
        TimeSpan offset;

        public void SetBack(TimeSpan by) => offset -= by;
        public void SetForward(TimeSpan by) => offset += by;
        public void Advance(TimeSpan by) => inner.Advance(by);
        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow() + offset;
        // The monotonic clock is not set back.
        public override long GetTimestamp() => inner.GetTimestamp();
        public override long TimestampFrequency => inner.TimestampFrequency;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Check(dueTime);
            Check(period);
            return new Limited(inner.CreateTimer(callback, state, dueTime, period));
        }

        static void Check(TimeSpan t)
        {
            if (t != Timeout.InfiniteTimeSpan && (t < TimeSpan.Zero || t.TotalMilliseconds > MaxMilliseconds))
            {
                throw new ArgumentOutOfRangeException(nameof(t), t, "past Timer.MaxSupportedTimeout");
            }
        }

        sealed class Limited(ITimer timer) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                Check(dueTime);
                Check(period);
                return timer.Change(dueTime, period);
            }

            public void Dispose() => timer.Dispose();
            public ValueTask DisposeAsync() => timer.DisposeAsync();
        }
    }

    sealed class Live : ISessionSource
    {
        public readonly List<Session> Sessions = [];
        public DiscoveryHeld? HeldNow;

        public DiscoveryHeld? Held => HeldNow;
        public long Revision => 0;
        public event EventHandler<SessionChange>? Changed;

        public void Upsert(Session s)
        {
            Sessions.RemoveAll(x => x.Id == s.Id);
            Sessions.Add(s);
            Changed?.Invoke(this, new SessionChange(SessionChangeKind.Upserted, s.Id, s, 0));
        }

        public void Remove(string id)
        {
            Sessions.RemoveAll(x => x.Id == id);
            Changed?.Invoke(this, new SessionChange(SessionChangeKind.Removed, id, null, 0));
        }

        public IReadOnlyList<Session> Snapshot() => [.. Sessions];
        public SessionList List(string? cursor = null) => new([.. Sessions], 0);
        public Session? Get(string id) => Sessions.FirstOrDefault(s => s.Id == id);
        public SessionSubscription Subscribe() => throw new NotSupportedException();
        public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    sealed class Ends : ISessionEnds
    {
        public readonly Dictionary<string, bool?> Of = new(StringComparer.Ordinal);
        public bool? Ended(AgentKind agent, string id) => Of.GetValueOrDefault(id);
    }

    sealed class Logon(bool fresh) : ILogonMarker
    {
        public bool Fresh() => fresh;
    }

    // The volatile key's behaviour: missing until made, and made only by Commit.
    sealed class VolatileKey : ILogonMarker
    {
        public bool Made;
        public bool Fresh() => !Made;
        public void Commit() => Made = true;
    }

    sealed class FixedBoot(DateTimeOffset at) : IBootClock
    {
        public DateTimeOffset Boot() => at;
    }
}
