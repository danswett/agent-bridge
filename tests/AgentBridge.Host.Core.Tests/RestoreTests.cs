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

    RestoreRecorder Recorder(bool restoreOnStart = true, bool freshLogon = false, Func<AgentKind, bool>? endsSettled = null, Resumer? resumer = null,
        Func<string?>? readOnly = null, bool restoreKept = false, string? logon = null, TimeProvider? clock = null, ILogonMarker? marker = null)
    {
        var r = new RestoreRecorder(source, new RestoreOptions
        {
            Path = Record, Instance = "run2", Ends = ends, RestoreOnStart = restoreOnStart, Boot = boot, Logon = marker ?? new Marker(freshLogon, logon), Time = clock ?? Time, EndsSettled = endsSettled,
            RestoreKept = restoreKept, ReadOnly = readOnly ?? (() => null), Marks = marks, Zone = TimeZoneInfo.Utc, Log = s => { lock (logs) { logs.Add(s); } },
        });
        r.NoticeReady += n => { lock (told) { told.Add(n); } };
        if (resumer is not null) { r.Restore(resumer); }
        return r;
    }

    readonly Marks marks = new();
    readonly List<string> logs = [];

    bool Logged(string part) { lock (logs) { return logs.Any(l => l.Contains(part, StringComparison.Ordinal)); } }

    // A restore runs off the caller's thread; what it does is waited for, never slept on blindly.
    static async Task Until(Func<bool> done)
    {
        for (var i = 0; i < 500 && !done(); i++) { await Task.Delay(10); }
        Assert.True(done());
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

    // Engineering on #813: on the same boot, a Codex session that ended while the host was down has its
    // registration pruned by agent-ha-bridge's daemon within a minute, and a Claude one once it is
    // 240 minutes old. Gone, it says nothing either way: neither is offered or pushed as having died,
    // and their folders are kept as an offer's are, for as long.
    [Fact]
    public async Task A_claude_or_codex_session_whose_registration_is_gone_on_the_same_boot_is_not_offered_and_keeps_its_folder()
    {
        Previous(Fixture.T0, false, E("codex", AgentKind.Codex, cwd: "/work/codex"), E("claude", AgentKind.Claude, cwd: "/work/claude"));
        ends.Of["codex"] = null;
        ends.Of["claude"] = null;
        await using var r = Recorder();

        r.Start();

        Assert.Empty(told);
        Assert.Null(r.Notice);
        Assert.Equal(["/work/claude", "/work/codex"], r.Folders()!.Order(StringComparer.Ordinal));
        Assert.Equal(2, Written().Owed.Count(w => w.Uncertain == true));
        Assert.True(Logged("registration is gone"));

        Time.Advance(RestoreRecorder.OwedFor);
        Assert.Empty(r.Folders()!);
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
    // Copilot on #813: an owed entry's judgement missing, never read as a default (no time read as
    // MinValue, let go at once with its worktree).
    [InlineData("""{ "v": 1, "boot": "2026-10-28T04:00:00.000Z", "instance": "run1", "written": "2026-10-28T04:00:00.000Z", "owed": [{ "entry": { "id": "a", "agent": "copilot", "cwd": "/work/repo", "seen": "2026-10-28T04:00:00.000Z" }, "cause": "crash", "decided": true }] }""")]
    [InlineData("""{ "v": 1, "boot": "2026-10-28T04:00:00.000Z", "instance": "run1", "written": "2026-10-28T04:00:00.000Z", "owed": [{ "entry": { "id": "a", "agent": "copilot", "cwd": "/work/repo", "seen": "2026-10-28T04:00:00.000Z" }, "at": "2026-10-28T04:00:00.000Z", "decided": true }] }""")]
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

    // Engineering on #837: a record holding a field longer than the host ever writes (a 70,000
    // character title) is set aside as malformed, as any unreadable record: read, it made
    // machine.restore larger than a message, and hello could not be sent.
    [Fact]
    public async Task A_record_with_a_field_longer_than_the_host_writes_is_set_aside()
    {
        Previous(Fixture.T0, false, E("a") with { Title = new string('x', 70_000) });
        await using var r = Recorder();

        r.Start();

        Assert.Null(r.Folders());
        Assert.Null(r.Notice);
        Assert.Empty(told);
        Assert.True(File.Exists(Record + ".unreadable"));
    }

    // And the notice itself stays within its budget whatever its titles hold: twenty titles of 60
    // emoji each are within Limits.Title, but their JSON is three times that. Cut, every session is
    // still named.
    [Fact]
    public async Task The_restore_notice_stays_within_its_budget_with_every_session_named()
    {
        var title = string.Concat(Enumerable.Repeat("\U0001F600", 60));
        Previous(Fixture.T0, false, [.. Enumerable.Range(0, RestoreProtocol.MaxOffered).Select(i => E($"s{i:D2}") with { Title = title })]);
        await using var r = Recorder();

        r.Start();

        var n = r.Notice!;
        Assert.True(ProtocolJson.ByteCount(n) <= RestoreProtocol.MaxNoticeBytes);
        Assert.Equal(RestoreProtocol.MaxOffered, n.Offered.Count);
        Assert.Null(n.OfferedMore);
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

    // Copilot on #813: ended while discovery was held, then resumed and gone again before the hold
    // lifted. Its registration now says it has not ended, which goes before the ending kept: it is
    // offered and its folder kept, and while held the intake keeps its endings again.
    [Fact]
    public async Task An_ending_kept_while_discovery_is_held_gives_way_to_a_registration_that_says_it_has_not_ended()
    {
        Previous(Fixture.T0, false, E("a", AgentKind.Codex));
        source.HeldNow = new DiscoveryHeld("the codex processes could not be listed", [AgentKind.Codex]);
        ends.Of["a"] = true;
        await using var r = Recorder();
        r.Start();
        Assert.False(r.JudgingEndings);

        ends.Of["a"] = false;
        Time.Advance(RestoreRecorder.JudgeEvery);
        Assert.True(r.JudgingEndings);
        source.HeldNow = null;
        Time.Advance(RestoreRecorder.JudgeEvery);

        Assert.Equal("a", Assert.Single(r.Notice!.Offered).Id);
        Assert.Single(r.Folders()!);
    }

    [Fact]
    public async Task An_ending_read_while_discovery_is_held_counts_after_its_registration_is_pruned()
    {
        Previous(Fixture.T0, false, E("a", AgentKind.Codex));
        source.HeldNow = new DiscoveryHeld("the codex processes could not be listed", [AgentKind.Codex]);
        ends.Of["a"] = true;
        await using var r = Recorder();
        r.Start();

        ends.Of["a"] = null;
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
        await using var r = Recorder(clock: clock);
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
        await using (var r = Recorder())
        {
            r.Start();
            source.Upsert(S("a", SessionStatus.Working));
            Time.Advance(RestoreRecorder.Coalesce);

            Assert.Null(r.Folders());
            Assert.Null(r.Notice);
        }

        Assert.Equal("{ not json", File.ReadAllText(Record));
        Assert.True(Logged("restore: could not start"));
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

    // Copilot on #813: the launches kept are bounded by those not yet seen, never counting the live
    // ones: with as many live as are kept, a second launch let go of the first before its session was
    // seen, and it was written with no settings.
    [Fact]
    public async Task A_launch_not_yet_seen_keeps_its_settings_however_many_live_sessions_were_launched_here()
    {
        var live = Enumerable.Range(0, 256).Select(i => $"live{i:D3}").ToList();
        source.Sessions.AddRange(live.Select(id => S(id)));
        await using var r = Recorder();
        r.Start();
        foreach (var id in live) { r.Launched(id, new LaunchedWith { Agent = "copilot", Permissions = "ask" }); }

        r.Launched("x", new LaunchedWith { Agent = "agency", Workspace = "Bridge", Worktree = true, Profile = "work", Permissions = "ask" });
        r.Launched("y", new LaunchedWith { Agent = "copilot", Permissions = "ask" });
        source.Upsert(S("x"));
        Time.Advance(RestoreRecorder.Coalesce);

        Assert.Equal(new RestoreLaunch("agency", "Bridge", true, "work", null, null, null, "ask"), Written().Sessions.Single(e => e.Id == "x").Launch);
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
        await using var r = Recorder(readOnly: () => throw new InvalidOperationException("broken"));

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

    // ---- Host B: restoring (#780) ----

    [Fact]
    public async Task What_a_crash_took_is_restored_one_at_a_time_as_it_was_launched_and_one_push_says_so_once_all_are_done()
    {
        var launch = new RestoreLaunch("copilot", "App", null, "work", "gpt-6.1-sol", "high", null, LaunchProtocol.Ask);
        Previous(Fixture.T0, false, E("a", turn: RestoreTurn.Working) with { Launch = launch }, E("b", question: true));
        var resumer = new Resumer { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var r = Recorder(resumer: resumer);
        r.Start();

        await Until(() => resumer.Asked.Count == 1);
        // The push waits for the queue, so it can say what was restored.
        Assert.Empty(told);
        resumer.Gate!.SetResult();
        await Until(() => resumer.Asked.Count == 2 && told.Count == 1);

        Assert.Equal(1, resumer.Most);
        Assert.Equal([("a", launch, "host-restore"), ("b", (RestoreLaunch?)null, "host-restore")], resumer.Asked.Select(x => (x.Id, x.Launch, x.By)));
        var n = told[0];
        Assert.Equal((2, 0, RestoreCause.Crash), (n.Restored, n.Offered.Count, n.Cause));
        // Continue's words for the one that went mid-turn only, with the time it went in the machine's
        // zone (the record was last written five minutes before this start, at 04:55).
        Assert.Equal(((bool?)true, (bool?)null, (string?)"You were interrupted by a host crash at 04:55; carry on where you left off"), (marks.Of["a"]!.MidTurn, marks.Of["a"]!.QuestionLost, marks.Of["a"]!.Continue));
        Assert.Equal(((bool?)null, true, (string?)null), (marks.Of["b"]!.MidTurn, marks.Of["b"]!.QuestionLost, marks.Of["b"]!.Continue));
        // Stamped, so a restore is never started twice, and one that dies soon is not restored again.
        Time.Advance(RestoreRecorder.Coalesce);
        Assert.All(Written().Owed, w => Assert.NotNull(w.Restored));
    }

    [Fact]
    public async Task A_session_that_dies_again_within_ten_minutes_of_its_restore_is_only_offered()
    {
        Previous(Fixture.T0, false, E("a") with { Restored = Time.GetUtcNow().AddMinutes(-8) }, E("b"));
        var resumer = new Resumer();
        await using var r = Recorder(resumer: resumer);
        r.Start();

        await Until(() => told.Count == 1);
        Assert.Equal(["b"], resumer.Asked.Select(x => x.Id));
        var n = told[0];
        Assert.Equal((1, 1), (n.Restored, n.Loops));
        Assert.Equal(["a"], n.Offered.Select(o => o.Id));
    }

    [Fact]
    public async Task A_restored_session_that_stays_live_ten_minutes_is_restored_again_when_it_later_dies()
    {
        Previous(Fixture.T0, false, E("a"));
        var resumer = new Resumer();
        await using (var first = Recorder(resumer: resumer))
        {
            first.Start();
            await Until(() => told.Count == 1);
            source.Upsert(S("a"));
            Time.Advance(RestoreRecorder.Coalesce);
            Assert.NotNull(Assert.Single(Written().Sessions).Restored);
            Time.Advance(RestoreRecorder.LoopWithin);
            Assert.Null(Assert.Single(Written().Sessions).Restored);
        }
        source.Remove("a");
        lock (told) { told.Clear(); }

        await using var second = Recorder(resumer: resumer);
        second.Start();

        await Until(() => told.Count == 1);
        Assert.Equal(["a", "a"], resumer.Asked.Select(x => x.Id));
        Assert.Equal((1, 0), (told[0].Restored, told[0].Loops));
    }

    [Fact]
    public async Task Nothing_is_restored_while_the_machine_is_read_only_and_what_died_waits_offered_until_it_takes_launches()
    {
        Previous(Fixture.T0, false, E("a"));
        string? why = "agent-ha-bridge is running for this user";
        var resumer = new Resumer();
        await using var r = Recorder(resumer: resumer, readOnly: () => Volatile.Read(ref why));
        r.Start();

        var n = Assert.Single(told);
        Assert.Equal((RestoreWhy.ReadOnly, 0), (n.Why, n.Restored));
        Assert.Equal(["a"], n.Offered.Select(o => o.Id));
        Time.Advance(RestoreRecorder.JudgeEvery);
        Assert.Empty(resumer.Asked);

        Volatile.Write(ref why, null);
        Time.Advance(RestoreRecorder.JudgeEvery);
        await Until(() => resumer.Asked.Count == 1);
    }

    [Fact]
    public async Task With_restore_on_start_off_nothing_is_restored()
    {
        Previous(Fixture.T0, false, E("a"));
        var resumer = new Resumer();
        await using var r = Recorder(restoreOnStart: false, resumer: resumer);
        r.Start();

        Assert.Equal(RestoreWhy.Off, Assert.Single(told).Why);
        Time.Advance(RestoreRecorder.JudgeEvery);
        Assert.Empty(resumer.Asked);
    }

    // sec-813: off only because config.json could not be read, nothing is started, and what a reboot
    // took is kept, on this start and the next: the person may have meant it on.
    [Fact]
    public async Task A_config_that_cannot_be_read_restores_nothing_but_keeps_what_died_offered()
    {
        Previous(Fixture.T0, false, E("a"));
        var resumer = new Resumer();
        await using (var first = Recorder(restoreOnStart: false, restoreKept: true, resumer: resumer, readOnly: () => "config.json has a problem"))
        {
            first.Start();
            Assert.Equal(RestoreWhy.ReadOnly, Assert.Single(told).Why);
            Assert.Equal(["a"], Written().Owed.Select(w => w.Entry.Id));
        }

        await using var second = Recorder(restoreOnStart: false, restoreKept: true, readOnly: () => "config.json has a problem");
        second.Start();

        Assert.Equal(["a"], second.Notice!.Offered.Select(o => o.Id));
        Assert.Single(second.Folders()!);
        Assert.Empty(resumer.Asked);
    }

    [Fact]
    public async Task A_session_is_not_restored_while_its_agents_discovery_is_held_and_is_once_it_is_judged_to_have_died()
    {
        Previous(Fixture.T0, false, E("a"));
        source.HeldNow = new DiscoveryHeld("the copilot processes could not be listed", [AgentKind.Copilot]);
        var resumer = new Resumer();
        await using var r = Recorder(resumer: resumer);
        r.Start();

        Time.Advance(RestoreRecorder.JudgeEvery);
        Assert.Empty(resumer.Asked);
        Assert.Empty(told);

        source.HeldNow = null;
        Time.Advance(RestoreRecorder.JudgeEvery);
        await Until(() => resumer.Asked.Count == 1 && told.Count == 1);
    }

    [Fact]
    public async Task A_restore_the_launcher_says_is_busy_is_tried_again_and_one_it_refuses_stays_offered()
    {
        Previous(Fixture.T0, false, E("a"), E("b"));
        var resumer = new Resumer();
        resumer.Answers.Enqueue(new LaunchResult(LaunchOutcome.Busy, "Busy: Copilot in App is waiting for a person to answer whether to trust that folder"));
        resumer.Answers.Enqueue(new LaunchResult(LaunchOutcome.Refused, "Resume refused: the original directory must exist and be an approved workspace."));
        await using var r = Recorder(resumer: resumer);
        r.Start();

        await Until(() => Logged("waits (Busy"));
        Assert.Single(resumer.Asked);
        Time.Advance(RestoreRecorder.BusyRetry);
        await Until(() => told.Count == 1);

        Assert.Equal(["a", "a", "b"], resumer.Asked.Select(x => x.Id));
        Assert.Equal((1, RestoreWhy.Pending), (told[0].Restored, told[0].Why));
        Assert.Equal(["a"], told[0].Offered.Select(o => o.Id));
    }

    [Fact]
    public async Task Sessions_an_earlier_run_offered_are_left_for_a_person_to_resume_and_not_restored_by_a_later_start()
    {
        Previous(Fixture.T0, false, E("a"));
        await using (var first = Recorder(readOnly: () => "agent-ha-bridge is running for this user"))
        {
            first.Start();
        }
        var resumer = new Resumer();

        await using var second = Recorder(resumer: resumer);
        second.Start();
        Time.Advance(RestoreRecorder.JudgeEvery);

        Assert.Equal(["a"], second.Notice!.Offered.Select(o => o.Id));
        Assert.Empty(resumer.Asked);
    }

    // #800's way in: a session resumed for another reason goes through the same queue and path, marked
    // restored with Continue offered when it went mid-turn, and is refused while the machine is read-only.
    [Fact]
    public async Task A_session_restored_on_request_goes_through_the_resume_path_and_offers_continue_when_it_went_mid_turn()
    {
        var resumer = new Resumer();
        await using var r = Recorder(resumer: resumer);
        r.Start();

        var result = await r.RestoreAsync(new RestoreAsk { Session = "s", Cause = RestoreCause.Crash, Since = Time.GetUtcNow().AddMinutes(-2), MidTurn = true, By = "host-signin" });

        Assert.Equal(LaunchOutcome.Started, result.Outcome);
        Assert.Equal(("s", "host-signin"), (resumer.Asked[0].Id, resumer.Asked[0].By));
        Assert.Equal("You were interrupted by a host crash at 04:58; carry on where you left off", marks.Of["s"]!.Continue);

        await using var readOnly = Recorder(resumer: resumer, readOnly: () => "agent-ha-bridge is running for this user");
        readOnly.Start();
        var refused = await readOnly.RestoreAsync(new RestoreAsk { Session = "t", Cause = RestoreCause.Crash, By = "host-signin" });
        Assert.Equal((LaunchOutcome.Refused, "agent-ha-bridge is running for this user"), (refused.Outcome, refused.Reason));
        Assert.Single(resumer.Asked);
    }

    // sec-813: the logon marker can be gone within one logon (removed at uninstall, or never made by
    // an older host); the logon's own number, kept in the record, says whether it really is new.
    [Theory]
    [InlineData("00000000000abc12", false)]
    [InlineData("00000000000def34", true)]
    public async Task A_missing_logon_marker_reads_as_a_new_logon_only_when_the_logon_itself_changed(string logonNow, bool offered)
    {
        RestoreFiles.Write(Record, new RestoreFile
        {
            Boot = Fixture.T0, Logon = "00000000000abc12", Instance = "run1", Written = Time.GetUtcNow().AddMinutes(-5), Sessions = [E("a")],
        });
        ends.Of["a"] = true;
        await using var r = Recorder(freshLogon: true, logon: logonNow);

        r.Start();

        Assert.Equal(offered, told.Count == 1);
        if (offered) { Assert.Equal(RestoreCause.Reboot, told[0].Cause); }
    }

    [Fact]
    public async Task A_restored_mark_offers_continue_until_a_turn_starts_after_the_restore()
    {
        const string Id = "99999999-9999-4999-8999-999999999999";
        var events = f.CopilotSession(Id, 100);
        f.Processes.Run(AgentKind.Copilot, 100);
        await using var real = f.Source();
        Fixture.Append(events, Copilot.User("go", 1), Copilot.TurnStart(2));
        await real.RefreshAsync();

        real.Mark(Id, new SessionRestored { At = Fixture.T0.AddSeconds(5), Cause = RestoreCause.Reboot, MidTurn = true, Continue = "carry on" });
        await Until(() => real.Get(Id)?.Restored is not null);
        // Working still, by a transcript that stops where the reboot cut it: Continue stays, and the
        // session, resumed with no prompt, is shown idle, not in the turn that was cut.
        Assert.Equal(((bool?)true, "carry on", SessionStatus.Idle), (real.Get(Id)!.Restored!.MidTurn, real.Get(Id)!.Restored!.Continue, real.Get(Id)!.Status));

        Fixture.Append(events, Copilot.TurnEnd(3), Copilot.User("carry on", 10), Copilot.TurnStart(11));
        await real.RefreshAsync();
        var after = real.Get(Id)!;
        Assert.Equal(SessionStatus.Working, after.Status);
        Assert.Equal(((bool?)null, (string?)null, RestoreCause.Reboot), (after.Restored!.MidTurn, after.Restored.Continue, after.Restored.Cause));

        real.Mark(Id, null);
        await Until(() => real.Get(Id)!.Restored is null);
    }

    // A whole turn can start and end between two reads: Continue goes all the same, once the
    // transcript holds a prompt it did not when the mark was first shown.
    [Fact]
    public async Task Continue_goes_after_a_whole_turn_that_starts_and_ends_between_two_reads()
    {
        const string Id = "99999999-9999-4999-8999-999999999998";
        var events = f.CopilotSession(Id, 100);
        f.Processes.Run(AgentKind.Copilot, 100);
        await using var real = f.Source();
        Fixture.Append(events, Copilot.User("go", 1), Copilot.TurnStart(2));
        await real.RefreshAsync();
        real.Mark(Id, new SessionRestored { At = Fixture.T0.AddSeconds(5), Cause = RestoreCause.Reboot, MidTurn = true, Continue = "carry on" });
        await Until(() => real.Get(Id)?.Restored is not null);

        Fixture.Append(events, Copilot.TurnEnd(3), Copilot.User("carry on", 10), Copilot.TurnStart(11), Copilot.Assistant("done", 12), Copilot.TurnEnd(13));
        await real.RefreshAsync();

        var after = real.Get(Id)!;
        Assert.NotEqual(SessionStatus.Working, after.Status);
        Assert.Equal(((bool?)null, (string?)null), (after.Restored!.MidTurn, after.Restored.Continue));
    }

    // A question the interrupted run left waiting went with it: the restored session is not shown
    // asking it (questionLost says it went). One asked after the restore is shown.
    [Fact]
    public async Task A_question_left_by_the_interrupted_run_is_not_shown_as_asked_but_one_asked_after_the_restore_is()
    {
        const string Id = "99999999-9999-4999-8999-999999999997";
        var events = f.CopilotSession(Id, 100);
        f.Processes.Run(AgentKind.Copilot, 100);
        await using var real = f.Source();
        Fixture.Append(events, Copilot.User("go", 1), Copilot.TurnStart(2), Copilot.Permission("r1", "c1", Copilot.Prompts["commands"], 3));
        await real.RefreshAsync();
        Assert.Equal(SessionStatus.Waiting, real.Get(Id)!.Status);

        real.Mark(Id, new SessionRestored { At = Fixture.T0.AddSeconds(5), Cause = RestoreCause.Reboot, QuestionLost = true });
        await Until(() => real.Get(Id)?.Restored is not null);
        var lost = real.Get(Id)!;
        Assert.Equal((SessionStatus.Idle, (Question?)null), (lost.Status, lost.Question));

        Fixture.Append(events, Copilot.User("again", 10), Copilot.TurnStart(11), Copilot.Permission("r2", "c2", Copilot.Prompts["commands"], 12));
        await real.RefreshAsync();
        var asked = real.Get(Id)!;
        Assert.Equal(SessionStatus.Waiting, asked.Status);
        Assert.NotNull(asked.Question);
    }

    // Marked before it registers, and given a whole turn before the source first reads it: that
    // prompt came after the restore, by its own time, so Continue has gone.
    [Fact]
    public async Task Continue_goes_for_a_turn_that_ran_whole_before_the_restored_session_was_first_read()
    {
        const string Id = "99999999-9999-4999-8999-999999999995";
        var events = f.CopilotSession(Id, 100);
        f.Processes.Run(AgentKind.Copilot, 100);
        await using var real = f.Source();
        real.Mark(Id, new SessionRestored { At = Fixture.T0.AddSeconds(5), Cause = RestoreCause.Reboot, MidTurn = true, Continue = "carry on" });
        Fixture.Append(events, Copilot.User("go", 1), Copilot.TurnStart(2), Copilot.User("carry on", 10), Copilot.TurnStart(11), Copilot.TurnEnd(12));

        await real.RefreshAsync();

        var after = real.Get(Id)!;
        Assert.Equal(((bool?)null, (string?)null), (after.Restored!.MidTurn, after.Restored.Continue));
    }

    // Held while its window has not been seen ready: no restored mark and no Continue, but the
    // interrupted turn is not shown; shown once the launch has seen the window ready.
    [Fact]
    public async Task A_held_restore_shows_no_continue_until_its_window_is_seen_ready()
    {
        const string Id = "99999999-9999-4999-8999-999999999993";
        var events = f.CopilotSession(Id, 100);
        f.Processes.Run(AgentKind.Copilot, 100);
        await using var real = f.Source();
        Fixture.Append(events, Copilot.User("go", 1), Copilot.TurnStart(2));
        await real.RefreshAsync();
        var mark = new SessionRestored { At = Fixture.T0.AddSeconds(5), Cause = RestoreCause.Reboot, MidTurn = true, Continue = "carry on" };

        real.Hold(Id, mark);
        await Until(() => real.Get(Id)!.Status == SessionStatus.Idle);
        Assert.Null(real.Get(Id)!.Restored);

        real.Mark(Id, mark);
        await Until(() => real.Get(Id)!.Restored is not null);
        Assert.Equal("carry on", real.Get(Id)!.Restored!.Continue);
    }

    // The badge goes after a day; the restore does not: the interrupted turn is still not shown.
    [Fact]
    public async Task The_interrupted_turn_stays_hidden_after_the_restored_badge_goes()
    {
        const string Id = "99999999-9999-4999-8999-999999999994";
        var events = f.CopilotSession(Id, 100);
        f.Processes.Run(AgentKind.Copilot, 100);
        await using var real = f.Source();
        Fixture.Append(events, Copilot.User("go", 1), Copilot.TurnStart(2));
        await real.RefreshAsync();
        real.Mark(Id, new SessionRestored { At = Fixture.T0.AddSeconds(5), Cause = RestoreCause.Reboot, MidTurn = true, Continue = "carry on" });
        await Until(() => real.Get(Id)?.Restored is not null);

        real.Mark(Id, null);
        await Until(() => real.Get(Id)!.Restored is null);

        Assert.Equal(SessionStatus.Idle, real.Get(Id)!.Status);
        await real.RefreshAsync();
        Assert.Equal(SessionStatus.Idle, real.Get(Id)!.Status);
    }

    // Killed mid-turn with a question up, then resumed in a new CLI: the turn and the question died
    // with the old process, so the session is idle and asks nothing, mark or no mark.
    [Fact]
    public async Task A_copilot_session_resumed_after_its_process_was_killed_mid_turn_is_idle_and_asks_nothing()
    {
        const string Id = "99999999-9999-4999-8999-999999999996";
        var events = f.CopilotSession(Id, 100);
        f.Processes.Run(AgentKind.Copilot, 100);
        await using var real = f.Source();
        Fixture.Append(events, Copilot.User("go", 1), Copilot.TurnStart(2), Copilot.ToolStart("q1", "ask_user", new { message = "?" }, 3));
        await real.RefreshAsync();
        Assert.Equal(SessionStatus.Waiting, real.Get(Id)!.Status);

        f.Processes.Exit(AgentKind.Copilot, 100);
        f.Processes.Run(AgentKind.Copilot, 200, startedAt: Fixture.T0.AddSeconds(30));
        f.CopilotSession(Id, 200, lockWrite: Fixture.T0.AddSeconds(31));
        Fixture.Append(events, Copilot.Event("session.resume", new { resumeTime = Copilot.At(31) }, 31));
        await real.RefreshAsync();

        var resumed = real.Get(Id)!;
        Assert.Equal((200, SessionStatus.Idle, (Question?)null), (resumed.ProcessId, resumed.Status, resumed.Question));
    }

    // Copilot on #837: Claude keeps a question the interrupted run left (an AskUserQuestion with no
    // result) pending, so its status reads Waiting. A prompt after the restore and the tool call it
    // started are shown working, not idle, and the old question is still not shown as asked.
    [Fact]
    public async Task A_restored_claude_session_shows_a_new_turn_working_while_the_interrupted_question_has_no_result()
    {
        const string Id = "99999999-9999-4999-8999-999999999997";
        var transcript = f.ClaudeTranscript(Id);
        Fixture.Append(transcript, Claude.User("plan it", 1), Claude.Assistant(2, "tool_use", Claude.ToolUse("ask1", "AskUserQuestion", Claude.Ask(("Which database?", false, ["PostgreSQL", "SQLite"])))));
        f.ClaudeSessionFile(700, Id);
        f.Processes.Run(AgentKind.Claude, 700);
        await using var real = f.Source();
        real.Mark(Id, new SessionRestored { At = Fixture.T0.AddSeconds(5), Cause = RestoreCause.Reboot, QuestionLost = true });
        Fixture.Append(transcript, Claude.User("carry on", 10), Claude.Assistant(11, "tool_use", Claude.ToolUse("t1", "Bash", new System.Text.Json.Nodes.JsonObject { ["command"] = "ls" })));

        await real.RefreshAsync();

        var after = real.Get(Id)!;
        Assert.Equal((SessionStatus.Working, (Question?)null), (after.Status, after.Question));
    }

    // Copilot on #837: the interrupted run's unanswered form is set aside before the status is worked
    // out, so a permission prompt the hooks report for the new turn is shown, not hidden behind it.
    [Fact]
    public async Task A_restored_claude_session_shows_a_new_permission_prompt_while_the_interrupted_question_has_no_result()
    {
        const string Id = "99999999-9999-4999-8999-999999999998";
        var transcript = f.ClaudeTranscript(Id);
        Fixture.Append(transcript, Claude.User("plan it", 1), Claude.Assistant(2, "tool_use", Claude.ToolUse("ask1", "AskUserQuestion", Claude.Ask(("Which database?", false, ["PostgreSQL", "SQLite"])))));
        f.ClaudeRegistration(Id, 701, transcript, hookStatus: "waiting", hookAt: Fixture.T0.AddSeconds(12));
        f.Processes.Run(AgentKind.Claude, 701);
        await using var real = f.Source();
        real.Mark(Id, new SessionRestored { At = Fixture.T0.AddSeconds(5), Cause = RestoreCause.Reboot, QuestionLost = true });
        Fixture.Append(transcript, Claude.User("carry on", 10), Claude.Assistant(11, "tool_use", Claude.ToolUse("t1", "Bash", new System.Text.Json.Nodes.JsonObject { ["command"] = "deploy.sh" })));

        await real.RefreshAsync();

        var after = real.Get(Id)!;
        Assert.Equal((SessionStatus.Waiting, "t1", QuestionKind.Approval), (after.Status, after.Question?.Id, after.Question?.Kind));
    }

    // And a whole new turn is dated by its own end, not by the restore.
    [Fact]
    public async Task A_restored_claude_session_dates_a_finished_new_turn_by_its_end_while_the_interrupted_question_has_no_result()
    {
        const string Id = "99999999-9999-4999-8999-999999999989";
        var transcript = f.ClaudeTranscript(Id);
        Fixture.Append(transcript, Claude.User("plan it", 1), Claude.Assistant(2, "tool_use", Claude.ToolUse("ask1", "AskUserQuestion", Claude.Ask(("Which database?", false, ["PostgreSQL", "SQLite"])))));
        f.ClaudeSessionFile(702, Id);
        f.Processes.Run(AgentKind.Claude, 702);
        await using var real = f.Source();
        real.Mark(Id, new SessionRestored { At = Fixture.T0.AddSeconds(5), Cause = RestoreCause.Reboot, QuestionLost = true });
        Fixture.Append(transcript, Claude.User("carry on", 10), Claude.Assistant(11, null, Claude.Text("Done.")), Claude.System("turn_duration", 12));

        await real.RefreshAsync();

        var after = real.Get(Id)!;
        Assert.Equal((SessionStatus.Idle, Fixture.T0.AddSeconds(12), (Question?)null), (after.Status, after.StatusSince, after.Question));
    }

    // ---- #837 review: the restore lifecycle ----

    // The restore's stamp is written down before its launch: a session that registers and then dies
    // with the host reads as a loop at the next start, never restored again.
    [Fact]
    public async Task A_restore_is_stamped_in_the_record_before_its_launch_so_one_that_dies_with_the_host_is_not_restored_again()
    {
        Previous(Fixture.T0, false, E("a"));
        var resumer = new Resumer { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var first = Recorder(resumer: resumer);
        first.Start();
        await Until(() => resumer.Asked.Count == 1);
        Assert.NotNull(Assert.Single(Written().Owed).Restored);

        // It registers while its launch is still being followed, and the record is written...
        source.Upsert(S("a"));
        Time.Advance(RestoreRecorder.Coalesce);
        Assert.NotNull(Assert.Single(Written().Sessions).Restored);
        // ...and the machine goes down with it before the launch returns.
        source.Sessions.Clear();
        var again = new Resumer();
        await using var second = Recorder(resumer: again);
        second.Start();

        Assert.Empty(again.Asked);
        Assert.Equal((0, 1), (second.Notice!.Restored, second.Notice.Loops));
        resumer.Gate!.SetResult();
    }

    [Fact]
    public async Task A_restore_that_registers_then_fails_stays_offered_as_one_that_died_again()
    {
        Previous(Fixture.T0, false, E("a"));
        var resumer = new Resumer { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        resumer.Answers.Enqueue(new LaunchResult(LaunchOutcome.Failed, "Copilot in App closed before it started; start it at the machine to see why"));
        await using var r = Recorder(resumer: resumer);
        r.Start();
        await Until(() => resumer.Asked.Count == 1);

        source.Upsert(S("a"));
        source.Remove("a");
        resumer.Gate!.SetResult();
        await Until(() => Logged("could not be restored"));
        Time.Advance(RestoreRecorder.JudgeEvery);

        Assert.Equal(["a"], r.Notice!.Offered.Select(o => o.Id));
        Assert.Equal(1, r.Notice.Loops);
        Assert.Single(r.Folders()!);
    }

    // Restored, then gone within ten minutes while the host runs: judged as at a start. One that died
    // is offered as a loop and kept owed (its worktree too); one its agent ended normally is not.
    [Fact]
    public async Task A_restored_session_that_dies_while_the_host_runs_is_offered_as_a_loop_and_one_ended_normally_is_not()
    {
        Previous(Fixture.T0, false, E("a"), E("b"));
        var resumer = new Resumer();
        await using var r = Recorder(resumer: resumer);
        r.Start();
        await Until(() => told.Count == 1);
        source.Upsert(S("a"));
        source.Upsert(S("b"));
        Time.Advance(TimeSpan.FromMinutes(5));

        ends.Of["b"] = true;
        source.Remove("a");
        source.Remove("b");
        Time.Advance(RestoreRecorder.JudgeEvery);

        Assert.Equal(["a"], r.Notice!.Offered.Select(o => o.Id));
        // a is a loop now, not restored too; b, ended normally, was restored.
        Assert.Equal((1, 1), (r.Notice.Loops, r.Notice.Restored));
        Time.Advance(RestoreRecorder.GoneKept);
        Assert.Equal(["a"], Written().Owed.Select(w => w.Entry.Id));
        Assert.Equal(["/work/repo"], r.Folders()!);
        Assert.Equal(2, resumer.Asked.Count);
    }

    // A restart within GoneKept of that death: the session is carried as owed, not read as ended
    // under the last run's eyes. And the notice keeps the reboot it was restored from as its cause.
    [Fact]
    public async Task A_restored_session_that_died_while_the_host_ran_stays_offered_across_a_quick_restart_and_the_reboot_stays_the_cause()
    {
        Previous(Fixture.T0.AddDays(-1), false, E("a"));
        var resumer = new Resumer();
        await using (var first = Recorder(resumer: resumer))
        {
            first.Start();
            await Until(() => told.Count == 1);
            source.Upsert(S("a"));
            source.Remove("a");
            Time.Advance(RestoreRecorder.JudgeEvery);

            Assert.Equal(RestoreCause.Reboot, first.Notice!.Cause);
            Assert.Equal(["a"], first.Notice.Offered.Select(o => o.Id));
        }

        await using var second = Recorder(resumer: resumer);
        second.Start();

        Assert.Equal(["a"], second.Notice!.Offered.Select(o => o.Id));
        Assert.Single(second.Folders()!);
        Assert.Single(resumer.Asked);
    }

    // A restore that waits at folder trust for longer than ten minutes keeps its stamp until it has
    // been seen live that long: one that registers late and dies at once is not restored again.
    [Fact]
    public async Task A_restore_still_waiting_to_register_after_ten_minutes_keeps_its_stamp()
    {
        Previous(Fixture.T0, false, E("a"));
        var resumer = new Resumer();
        resumer.Answers.Enqueue(new LaunchResult(LaunchOutcome.Unconfirmed, "Copilot in App is asking whether to trust the folder; left for a person at the machine") { Session = "a" });
        await using var r = Recorder(resumer: resumer);
        r.Start();
        await Until(() => told.Count == 1);

        Time.Advance(RestoreRecorder.LoopWithin + TimeSpan.FromMinutes(5));
        source.Upsert(S("a"));
        Time.Advance(RestoreRecorder.Coalesce);

        Assert.NotNull(Assert.Single(Written().Sessions).Restored);
    }

    // The host stopping while a restore waits its turn: it starts nothing.
    [Fact]
    public async Task A_restore_waiting_its_turn_when_the_host_stops_starts_nothing()
    {
        Previous(Fixture.T0, false, E("a"));
        var resumer = new Resumer { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var r = Recorder(resumer: resumer);
        r.Start();
        await Until(() => resumer.Asked.Count == 1);

        var queued = r.RestoreAsync(new RestoreAsk { Session = "x", Cause = RestoreCause.Crash, By = "host-signin" });
        var stopping = r.DisposeAsync().AsTask();
        var refused = await queued.WaitAsync(TimeSpan.FromSeconds(5));
        resumer.Gate!.SetResult();
        await stopping;

        Assert.Equal((LaunchOutcome.Refused, "the host is stopping"), (refused.Outcome, refused.Reason));
        Assert.Equal(["a"], resumer.Asked.Select(x => x.Id));
    }

    // A restore on request already launching when the host stops is let finish, as the queue's is: the
    // session it starts registers before the last record is written, and is in it.
    [Fact]
    public async Task The_host_stopping_waits_for_a_restore_on_request_already_launching_and_writes_down_its_session()
    {
        var resumer = new Resumer { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var r = Recorder(resumer: resumer);
        r.Start();
        var asked = r.RestoreAsync(new RestoreAsk { Session = "s", Cause = RestoreCause.Crash, By = "host-signin" });
        await Until(() => resumer.Asked.Count == 1);

        var stopping = r.DisposeAsync().AsTask();
        await Task.Delay(200);
        Assert.False(stopping.IsCompleted);
        source.Upsert(S("s"));
        resumer.Gate!.SetResult();
        await stopping.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(LaunchOutcome.Started, (await asked).Outcome);
        var written = Written();
        Assert.NotNull(written.Stopped);
        Assert.Equal("s", Assert.Single(written.Sessions).Id);
    }

    // A restore on request of a session the record holds no owed entry of keeps its folder from any
    // reclaim while it waits its turn and launches, and until it registers.
    [Fact]
    public async Task A_restore_on_request_keeps_its_folder_from_a_reclaim_until_its_session_registers()
    {
        source.Sessions.Add(S("s"));
        var resumer = new Resumer { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var r = Recorder(resumer: resumer);
        r.Start();
        source.Remove("s");
        Assert.Empty(r.Folders()!);

        var asked = r.RestoreAsync(new RestoreAsk { Session = "s", Cause = RestoreCause.Crash, By = "host-signin" });
        await Until(() => resumer.Asked.Count == 1);
        Assert.Equal(["/work/repo"], r.Folders()!);
        resumer.Gate!.SetResult();
        await asked;
        Assert.Equal(["/work/repo"], r.Folders()!);

        source.Upsert(S("s"));
        Assert.Empty(r.Folders()!);
    }

    // One the record knows nothing of keeps every worktree meanwhile (its folder is not known), and a
    // stamp that never registers goes after a day on a host that writes nothing else.
    [Fact]
    public async Task A_restore_on_request_that_never_registers_keeps_every_worktree_and_lets_its_stamp_go_a_day_later()
    {
        var resumer = new Resumer();
        resumer.Answers.Enqueue(new LaunchResult(LaunchOutcome.Unconfirmed, "Copilot in App is asking whether to trust the folder; left for a person at the machine") { Session = "s" });
        await using var r = Recorder(resumer: resumer);
        r.Start();

        Assert.Equal(LaunchOutcome.Unconfirmed, (await r.RestoreAsync(new RestoreAsk { Session = "s", Cause = RestoreCause.Crash, By = "host-signin" })).Outcome);
        Time.Advance(RestoreRecorder.Coalesce);
        Assert.True(Written().Stamps.ContainsKey("s"));
        Assert.Null(r.Folders());

        Time.Advance(RestoreProtocol.ShownFor);

        Assert.False(Written().Stamps.ContainsKey("s"));
        Assert.Empty(r.Folders()!);
    }

    // Answered Unconfirmed (at folder trust), then seen ready while the launch still followed it: counted
    // and shown restored then, with Continue; a later attempt's stamp is never confirmed by an earlier one.
    [Fact]
    public async Task A_restore_answered_unconfirmed_and_seen_ready_later_is_counted_and_shown_restored_then()
    {
        Previous(Fixture.T0, false, E("a", turn: RestoreTurn.Working));
        var resumer = new Resumer();
        resumer.Answers.Enqueue(new LaunchResult(LaunchOutcome.Unconfirmed, "Copilot in App is asking whether to trust the folder; left for a person at the machine") { Session = "a" });
        await using var r = Recorder(resumer: resumer);
        r.Start();
        await Until(() => Logged("not seen ready"));
        source.Upsert(S("a"));
        Assert.False(marks.Of.ContainsKey("a"));
        Assert.Equal(0, r.Notice?.Restored ?? 0);

        r.SeenReadyLate("a");

        Assert.NotNull(marks.Of["a"]?.Continue);
        Assert.Equal(1, r.Notice?.Restored);
        Assert.Contains(r.RestoredLosses(), l => l.Id == "a");
    }

    // ---- Copilot on #837 at fcdac180 ----

    // A restore on request that had not registered when the host went is still a request at the next
    // start: its folder is not known, so every worktree is kept until it registers.
    [Fact]
    public async Task A_restore_on_request_not_yet_registered_when_the_host_went_keeps_every_worktree_at_the_next_start()
    {
        var resumer = new Resumer();
        resumer.Answers.Enqueue(new LaunchResult(LaunchOutcome.Unconfirmed, "Copilot in App is asking whether to trust the folder; left for a person at the machine") { Session = "s" });
        await using (var first = Recorder(resumer: resumer))
        {
            first.Start();
            await first.RestoreAsync(new RestoreAsk { Session = "s", Cause = RestoreCause.Crash, By = "host-signin" });
            Assert.Null(first.Folders());
        }
        Assert.True(Written().Stamps.ContainsKey("s"));

        await using var second = Recorder(resumer: new Resumer());
        second.Start();
        Assert.Null(second.Folders());

        source.Upsert(S("s"));
        Assert.Empty(second.Folders()!);
    }

    // An offer an earlier run restored, whose restore had not registered when the machine went down:
    // it went again straight after its restore, so it is a loop.
    [Fact]
    public async Task An_offer_whose_restore_had_not_registered_when_the_machine_rebooted_is_a_loop()
    {
        var now = Time.GetUtcNow();
        RestoreFiles.Write(Record, new RestoreFile
        {
            Boot = Fixture.T0.AddDays(-1), Instance = "run1", Written = now.AddMinutes(-1),
            Owed = [new RestoreOwed(E("a"), RestoreCause.Reboot, now.AddMinutes(-5), now.AddMinutes(-2), Decided: true, Restored: now.AddMinutes(-1))],
        });
        await using var r = Recorder(resumer: new Resumer());

        r.Start();

        Assert.Equal(["a"], r.Notice!.Offered.Select(o => o.Id));
        Assert.Equal(1, r.Notice.Loops);
    }

    // Seen ready after the session has lived ten minutes (its stamp taken away then) at folder trust,
    // which the launch follows for longer: still this attempt's, so counted and shown restored.
    [Fact]
    public async Task A_restore_seen_ready_after_its_stamp_was_taken_away_is_still_counted_restored()
    {
        Previous(Fixture.T0, false, E("a", turn: RestoreTurn.Working));
        var resumer = new Resumer();
        resumer.Answers.Enqueue(new LaunchResult(LaunchOutcome.Unconfirmed, "Copilot in App is asking whether to trust the folder; left for a person at the machine") { Session = "a" });
        await using var r = Recorder(resumer: resumer);
        r.Start();
        await Until(() => Logged("not seen ready"));
        source.Upsert(S("a"));
        Time.Advance(RestoreRecorder.LoopWithin + TimeSpan.FromMinutes(1));
        Assert.Null(Assert.Single(Written().Sessions).Restored);

        r.SeenReadyLate("a");

        Assert.Equal(1, r.Notice?.Restored);
        Assert.NotNull(marks.Of["a"]?.Continue);
    }

    // The only session registers before its launch answers Unconfirmed: its owed entry goes with it.
    // Seen ready later, the push is sent then, saying it was restored.
    [Fact]
    public async Task A_restore_that_registered_before_its_unconfirmed_answer_and_is_seen_ready_later_sends_the_push()
    {
        Previous(Fixture.T0, false, E("a"));
        var resumer = new Resumer { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        resumer.Answers.Enqueue(new LaunchResult(LaunchOutcome.Unconfirmed, "Copilot in App is asking whether to trust the folder; left for a person at the machine") { Session = "a" });
        await using var r = Recorder(resumer: resumer);
        r.Start();
        await Until(() => resumer.Asked.Count == 1);
        source.Upsert(S("a"));
        resumer.Gate!.SetResult();
        await Until(() => Logged("not seen ready"));
        Assert.Empty(told);

        r.SeenReadyLate("a");

        Assert.Equal(1, Assert.Single(told).Restored);
    }

    // ---- Copilot on #837 at f8f8919b ----

    // A second request for a restore still pending that starts nothing (busy at folder trust) leaves
    // the first's protection as it was: every worktree, its folder not known.
    [Fact]
    public async Task A_second_restore_request_that_starts_nothing_keeps_the_first_requests_worktree_protection()
    {
        var resumer = new Resumer();
        resumer.Answers.Enqueue(new LaunchResult(LaunchOutcome.Unconfirmed, "Copilot in App is asking whether to trust the folder; left for a person at the machine") { Session = "s" });
        resumer.Answers.Enqueue(new LaunchResult(LaunchOutcome.Busy, "a folder-trust question is waiting for a person at the machine"));
        await using var r = Recorder(resumer: resumer);
        r.Start();
        Assert.Equal(LaunchOutcome.Unconfirmed, (await r.RestoreAsync(new RestoreAsk { Session = "s", Cause = RestoreCause.Crash, By = "host-signin" })).Outcome);
        Assert.Null(r.Folders());

        Assert.Equal(LaunchOutcome.Busy, (await r.RestoreAsync(new RestoreAsk { Session = "s", Cause = RestoreCause.Crash, By = "host-signin" })).Outcome);

        Assert.Null(r.Folders());
    }

    // While its agent's discovery is held, a listed session is not known to be live: its restore's
    // stamp stays past ten minutes, and one that died in the hold is a loop once the hold lifts.
    [Fact]
    public async Task A_restore_stamp_is_kept_while_its_agents_discovery_is_held()
    {
        Previous(Fixture.T0, false, E("a"));
        var resumer = new Resumer();
        await using var r = Recorder(resumer: resumer);
        r.Start();
        await Until(() => told.Count == 1);
        source.Upsert(S("a"));
        source.HeldNow = new DiscoveryHeld("the copilot processes could not be listed", [AgentKind.Copilot]);

        Time.Advance(RestoreRecorder.LoopWithin + TimeSpan.FromMinutes(1));
        Assert.NotNull(Assert.Single(Written().Sessions).Restored);

        source.HeldNow = null;
        source.Remove("a");
        Time.Advance(RestoreRecorder.JudgeEvery);
        Assert.Equal(1, r.Notice!.Loops);
    }

    // ---- Copilot on #837 at f3f5bdb0 ----

    // The hold lifts before the removal it found reaches the recorder. A stamp due to go in that gap
    // stays while the source no longer lists the session, so the removal is still a loop.
    [Fact]
    public async Task A_restore_stamp_is_kept_while_a_removal_found_as_the_hold_lifts_has_not_arrived()
    {
        Previous(Fixture.T0, false, E("a"));
        var resumer = new Resumer();
        await using var r = Recorder(resumer: resumer);
        r.Start();
        await Until(() => told.Count == 1);
        source.Upsert(S("a"));
        source.HeldNow = new DiscoveryHeld("the copilot processes could not be listed", [AgentKind.Copilot]);
        Time.Advance(RestoreRecorder.LoopWithin + TimeSpan.FromMinutes(1));

        // The source found it gone and lifted the hold; its removal has not been dispatched yet.
        source.HeldNow = null;
        source.Sessions.RemoveAll(s => s.Id == "a");
        Time.Advance(RestoreRecorder.HeldRetry);
        Assert.NotNull(Assert.Single(Written().Sessions).Restored);

        source.Remove("a");
        Time.Advance(RestoreRecorder.JudgeEvery);
        Assert.Equal(1, r.Notice!.Loops);
    }

    // An earlier restore answered Unconfirmed is seen ready while a retry is under way, and the retry is
    // refused (the session is running now). The earlier attempt's readiness is kept: shown restored.
    [Fact]
    public async Task An_unconfirmed_restore_seen_ready_while_a_refused_retry_is_under_way_is_shown_restored()
    {
        var resumer = new Resumer();
        resumer.Answers.Enqueue(new LaunchResult(LaunchOutcome.Unconfirmed, "Copilot in App is asking whether to trust the folder; left for a person at the machine") { Session = "s" });
        await using var r = Recorder(resumer: resumer);
        r.Start();
        Assert.Equal(LaunchOutcome.Unconfirmed, (await r.RestoreAsync(new RestoreAsk { Session = "s", Cause = RestoreCause.Crash, By = "host-signin" })).Outcome);
        var first = Time.GetUtcNow();
        Time.Advance(TimeSpan.FromMinutes(1));

        resumer.Answers.Enqueue(new LaunchResult(LaunchOutcome.Refused, "that session is already running"));
        resumer.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var retry = r.RestoreAsync(new RestoreAsk { Session = "s", Cause = RestoreCause.Crash, By = "host-signin" });
        await Until(() => { lock (resumer.Asked) { return resumer.Asked.Count == 2; } });
        r.SeenReadyLate("s");
        resumer.Gate.SetResult();

        Assert.Equal(LaunchOutcome.Refused, (await retry).Outcome);
        Assert.Equal(first, Assert.IsType<SessionRestored>(marks.Of.GetValueOrDefault("s")).At);
    }

    // ---- Copilot on #837 at 48232df5 ----

    // A record that could not be set aside stops the recorder's start: no stamp can be written down,
    // so a restore on request is busy and starts nothing. Started on an in-memory stamp, a crash would
    // lose it, and the session could be restored again and again.
    [Fact]
    public async Task A_restore_on_request_is_busy_and_starts_nothing_while_the_recorder_could_not_start()
    {
        File.WriteAllText(Record, "{ not json");
        Directory.CreateDirectory(Path.Combine(Record + ".unreadable", "held"));
        var resumer = new Resumer();
        await using var r = Recorder(resumer: resumer);
        r.Start();
        Assert.True(Logged("restore: could not start"));

        var result = await r.RestoreAsync(new RestoreAsk { Session = "s", Cause = RestoreCause.Crash, By = "host-signin" });

        Assert.Equal(LaunchOutcome.Busy, result.Outcome);
        Assert.Empty(resumer.Asked);
        Assert.False(marks.Held.ContainsKey("s"));
    }

    // ---- Copilot on #837 at 8db6ae68, 93e6317f and de0401f0 ----

    // An earlier unconfirmed restore's window registers while a retry is under way, and the retry is
    // refused. Put back, the earlier stamp stays on the live entry, so a crash within ten minutes is
    // still a loop.
    [Fact]
    public async Task A_session_that_registers_while_a_refused_retry_is_under_way_keeps_the_earlier_restores_stamp()
    {
        var resumer = new Resumer();
        resumer.Answers.Enqueue(new LaunchResult(LaunchOutcome.Unconfirmed, "Copilot in App is asking whether to trust the folder; left for a person at the machine") { Session = "s" });
        await using var r = Recorder(resumer: resumer);
        r.Start();
        Assert.Equal(LaunchOutcome.Unconfirmed, (await r.RestoreAsync(new RestoreAsk { Session = "s", Cause = RestoreCause.Crash, By = "host-signin" })).Outcome);
        var first = Time.GetUtcNow();
        Time.Advance(TimeSpan.FromMinutes(1));

        resumer.Answers.Enqueue(new LaunchResult(LaunchOutcome.Refused, "that session is already running"));
        resumer.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var retry = r.RestoreAsync(new RestoreAsk { Session = "s", Cause = RestoreCause.Crash, By = "host-signin" });
        await Until(() => { lock (resumer.Asked) { return resumer.Asked.Count == 2; } });
        source.Upsert(S("s"));
        resumer.Gate.SetResult();
        Assert.Equal(LaunchOutcome.Refused, (await retry).Outcome);
        Time.Advance(RestoreRecorder.Coalesce);

        Assert.Equal(first, Assert.Single(Written().Sessions).Restored);
    }

    // How long a restored session has lived is elapsed time: a clock set forward past ten minutes just
    // after it registered does not clear its stamp.
    [Fact]
    public async Task A_clock_set_forward_does_not_clear_a_restore_stamp_before_its_session_has_lived_ten_minutes()
    {
        Previous(Fixture.T0, false, E("a"));
        var clock = new SystemLimits(Time);
        await using var r = Recorder(resumer: new Resumer(), clock: clock);
        r.Start();
        await Until(() => told.Count == 1);
        source.Upsert(S("a"));

        clock.SetForward(RestoreRecorder.LoopWithin + TimeSpan.FromMinutes(1));
        source.Upsert(S("a", SessionStatus.Working));
        clock.Advance(RestoreRecorder.Coalesce);

        Assert.NotNull(Assert.Single(Written().Sessions).Restored);
    }

    // A request holds the queue's only candidate while a judging tick finds nothing left to restore
    // (only the offer's expiry is armed then). Once the request is busy, the queue goes on.
    [Fact]
    public async Task The_start_up_queue_goes_on_after_a_request_that_held_back_its_last_candidate_is_busy()
    {
        Previous(Fixture.T0, false, E("a"));
        var resumer = new Resumer();
        resumer.Answers.Enqueue(new LaunchResult(LaunchOutcome.Busy, "a folder-trust question is waiting for a person at the machine"));
        await using var r = Recorder(resumer: resumer);
        r.Start();
        await Until(() => Logged("session a waits"));

        resumer.Answers.Enqueue(new LaunchResult(LaunchOutcome.Busy, "a folder-trust question is waiting for a person at the machine"));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        resumer.Gate = gate;
        var request = r.RestoreAsync(new RestoreAsk { Session = "a", Cause = RestoreCause.Crash, By = "host-signin" });
        await Until(() => { lock (resumer.Asked) { return resumer.Asked.Count == 2; } });
        Time.Advance(RestoreRecorder.JudgeEvery);
        resumer.Gate = null;
        gate.SetResult();
        Assert.Equal(LaunchOutcome.Busy, (await request).Outcome);

        Time.Advance(RestoreRecorder.BusyRetry);
        await Until(() => { lock (resumer.Asked) { return resumer.Asked.Count == 3; } });
    }

    // Nothing is restored while any session is still being judged (a Claude ending still in the
    // hooks' spool); once all are, the newest goes first, whatever its agent.
    [Fact]
    public async Task Nothing_is_restored_while_any_session_is_still_being_judged_and_then_the_newest_goes_first()
    {
        var settled = false;
        Previous(Fixture.T0, false, E("a"), E("b", AgentKind.Claude) with { Seen = Fixture.T0.AddMinutes(1) });
        var resumer = new Resumer();
        await using var r = Recorder(resumer: resumer, endsSettled: agent => agent != AgentKind.Claude || settled);
        r.Start();
        Assert.Empty(resumer.Asked);

        settled = true;
        Time.Advance(RestoreRecorder.JudgeEvery);
        await Until(() => { lock (resumer.Asked) { return resumer.Asked.Count == 2; } });

        Assert.Equal(["b", "a"], resumer.Asked.Select(a => a.Id));
    }

    [Fact]
    public async Task What_restored_sessions_lost_is_kept_for_the_push()
    {
        Previous(Fixture.T0, false, E("a", turn: RestoreTurn.Working), E("b"), E("c", question: true));
        var resumer = new Resumer();
        await using var r = Recorder(resumer: resumer);
        r.Start();
        await Until(() => told.Count == 1);

        var lost = r.RestoredLosses();
        Assert.Equal(["a", "c"], lost.Select(x => x.Id).Order(StringComparer.Ordinal));
        Assert.True(lost.Single(x => x.Id == "a").MidTurn);
        Assert.True(lost.Single(x => x.Id == "c").QuestionLost);
    }

    [Fact]
    public async Task A_session_an_earlier_run_restored_stays_offered_when_the_clock_is_set_back_before_its_restore()
    {
        var now = Time.GetUtcNow();
        RestoreFiles.Write(Record, new RestoreFile
        {
            Boot = Fixture.T0, Instance = "run1", Written = now.AddMinutes(-5),
            Owed = [new RestoreOwed(E("a"), RestoreCause.Reboot, now.AddMinutes(-5), now.AddMinutes(-5), Decided: true, Restored: now.AddDays(60))],
        });
        await using var r = Recorder(resumer: new Resumer(), clock: new SystemLimits(Time));

        r.Start();

        Assert.Equal(["a"], r.Notice!.Offered.Select(o => o.Id));
    }

    // ---- #837 review, second round ----

    // A restore started and not yet registered when the host went is stamped on its owed entry; found
    // running at the next start, it is adopted stamped.
    [Fact]
    public async Task A_session_found_running_whose_restore_had_not_registered_is_adopted_with_its_stamp()
    {
        var now = Time.GetUtcNow();
        RestoreFiles.Write(Record, new RestoreFile
        {
            Boot = Fixture.T0, Instance = "run1", Written = now.AddMinutes(-1),
            Owed = [new RestoreOwed(E("a"), RestoreCause.Reboot, now.AddMinutes(-5), now.AddMinutes(-2), Decided: true, Restored: now.AddMinutes(-1))],
        });
        source.Sessions.Add(S("a"));
        await using var r = Recorder();

        r.Start();

        Assert.NotNull(Assert.Single(Written().Sessions).Restored);
    }

    // A second request for a session already being restored is refused before it touches anything,
    // so the first keeps its stamp; one the record holds no entry of is written down in Stamps before
    // its launch, and adopted stamped if the host goes before it registers.
    [Fact]
    public async Task A_restore_on_request_is_written_down_before_its_launch_and_a_second_request_for_it_is_refused()
    {
        var resumer = new Resumer { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var first = Recorder(resumer: resumer);
        first.Start();

        var asked = first.RestoreAsync(new RestoreAsk { Session = "s", Cause = RestoreCause.Crash, By = "host-signin" });
        await Until(() => resumer.Asked.Count == 1);
        var again = await first.RestoreAsync(new RestoreAsk { Session = "s", Cause = RestoreCause.Crash, By = "host-signin" }).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal((LaunchOutcome.Refused, "that session is already running or being restored"), (again.Outcome, again.Reason));
        Assert.True(Written().Stamps.ContainsKey("s"));

        source.Sessions.Add(S("s"));
        await using var second = Recorder();
        second.Start();
        Assert.NotNull(Assert.Single(Written().Sessions).Restored);

        resumer.Gate!.SetResult();
        Assert.Equal(LaunchOutcome.Started, (await asked).Outcome);
        Assert.Single(resumer.Asked);
    }

    // A restore the launcher could not confirm (its window at folder trust) stays offered until its
    // session registers, and is never counted restored or shown so: its window was never seen ready.
    [Fact]
    public async Task An_unconfirmed_restore_is_offered_until_it_registers_and_never_shown_restored()
    {
        Previous(Fixture.T0, false, E("a"));
        var resumer = new Resumer();
        resumer.Answers.Enqueue(new LaunchResult(LaunchOutcome.Unconfirmed, "Copilot in App is asking whether to trust the folder; left for a person at the machine") { Session = "a" });
        await using var r = Recorder(resumer: resumer);
        r.Start();
        await Until(() => told.Count == 1);

        Assert.Equal((0, 1), (r.Notice!.Restored, r.Notice.Offered.Count));
        source.Upsert(S("a"));
        Assert.Equal((0, 0), (r.Notice?.Restored ?? 0, r.Notice?.Offered.Count ?? 0));
        Assert.False(marks.Of.ContainsKey("a"));
        Assert.True(marks.Held.ContainsKey("a"));
    }

    // Registered and gone before the launch returned: offered as a loop, not hidden as restored.
    [Fact]
    public async Task A_restore_that_registers_and_dies_before_its_launch_returns_is_offered_as_a_loop()
    {
        Previous(Fixture.T0, false, E("a", turn: RestoreTurn.Working));
        var resumer = new Resumer { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var r = Recorder(resumer: resumer);
        r.Start();
        await Until(() => resumer.Asked.Count == 1);

        source.Upsert(S("a"));
        source.Remove("a");
        resumer.Gate!.SetResult();
        await Until(() => Logged("restored, idle"));
        Time.Advance(RestoreRecorder.JudgeEvery);

        Assert.Equal(["a"], r.Notice!.Offered.Select(o => o.Id));
        // Once: a loop, not also restored, and what it lost is said with the offer alone.
        Assert.Equal((1, 0), (r.Notice.Loops, r.Notice.Restored));
        Assert.Empty(r.RestoredLosses());
    }

    // Registered, but its window never seen ready (it may be asking whether to trust its folder): not
    // counted restored, and no restored mark or Continue shown, which one tap would send into that
    // question. The boundary is held, so its interrupted turn is not shown either.
    [Fact]
    public async Task A_restore_whose_window_was_not_seen_ready_is_not_counted_and_shows_no_continue()
    {
        Previous(Fixture.T0, false, E("a", turn: RestoreTurn.Working));
        var resumer = new Resumer { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        resumer.Answers.Enqueue(new LaunchResult(LaunchOutcome.Unconfirmed, "Copilot in App started, but its window was not seen ready; check at the machine") { Session = "a" });
        await using var r = Recorder(resumer: resumer);
        r.Start();
        await Until(() => resumer.Asked.Count == 1);

        source.Upsert(S("a"));
        resumer.Gate!.SetResult();
        await Until(() => Logged("not seen ready"));
        Time.Advance(RestoreRecorder.NoticeAfter);

        Assert.Empty(told);
        Assert.Equal(0, r.Notice?.Restored ?? 0);
        Assert.False(marks.Of.ContainsKey("a"));
        Assert.NotNull(marks.Held["a"].Continue);
    }

    // A request that started nothing (busy) takes back only its own mark: the one an earlier restore
    // showed comes back, and is taken away when it was due to go, not a day after the attempt.
    [Fact]
    public async Task A_restore_on_request_that_starts_nothing_puts_back_the_earlier_restores_mark_until_its_own_time()
    {
        Previous(Fixture.T0, false, E("a", turn: RestoreTurn.Working));
        var resumer = new Resumer();
        await using var r = Recorder(resumer: resumer);
        r.Start();
        await Until(() => told.Count == 1);
        var first = marks.Of["a"]!;
        Time.Advance(TimeSpan.FromHours(20));

        resumer.Answers.Enqueue(new LaunchResult(LaunchOutcome.Busy, "Busy: Copilot in App is waiting for a person to answer whether to trust that folder"));
        var busy = await r.RestoreAsync(new RestoreAsk { Session = "a", Cause = RestoreCause.Crash, By = "host-signin" });

        Assert.Equal(LaunchOutcome.Busy, busy.Outcome);
        Assert.Same(first, marks.Of["a"]);
        Time.Advance(TimeSpan.FromHours(4));
        Assert.Null(marks.Of["a"]);
    }

    // ---- #837 review, third round ----

    // A session that went a moment ago, restored on request: pruning its old entry 60 s after it went
    // keeps the newer attempt's stamp, written in Stamps, so the session is adopted stamped.
    [Fact]
    public async Task Pruning_a_gone_session_keeps_the_stamp_of_a_restore_started_after_it_went()
    {
        var resumer = new Resumer { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var first = Recorder(resumer: resumer);
        first.Start();
        source.Upsert(S("s"));
        source.Remove("s");
        Time.Advance(TimeSpan.FromSeconds(1));

        var asked = first.RestoreAsync(new RestoreAsk { Session = "s", Cause = RestoreCause.Crash, By = "host-signin" });
        await Until(() => resumer.Asked.Count == 1);
        Time.Advance(RestoreRecorder.GoneKept);

        Assert.Empty(Written().Sessions);
        Assert.True(Written().Stamps.ContainsKey("s"));
        resumer.Gate!.SetResult();
        Assert.Equal(LaunchOutcome.Started, (await asked).Outcome);
        Time.Advance(RestoreRecorder.Coalesce);
        source.Sessions.Add(S("s"));
        await using var second = Recorder();
        second.Start();
        Assert.NotNull(Assert.Single(Written().Sessions).Restored);
    }

    // A request cancelled while it waits its turn starts nothing, and takes back its stamp and mark.
    [Fact]
    public async Task A_restore_request_cancelled_while_it_waits_its_turn_starts_nothing_and_takes_back_its_stamp()
    {
        Previous(Fixture.T0, false, E("a"));
        var resumer = new Resumer { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var r = Recorder(resumer: resumer);
        r.Start();
        await Until(() => resumer.Asked.Count == 1);

        using var cancel = new CancellationTokenSource();
        var queued = r.RestoreAsync(new RestoreAsk { Session = "x", Cause = RestoreCause.Crash, By = "host-signin" }, cancel.Token);
        Assert.True(Written().Stamps.ContainsKey("x"));
        cancel.Cancel();
        var withdrawn = await queued.WaitAsync(TimeSpan.FromSeconds(5));
        resumer.Gate!.SetResult();
        await Until(() => Logged("restored, idle"));
        Time.Advance(RestoreRecorder.Coalesce);

        Assert.Equal(LaunchOutcome.Refused, withdrawn.Outcome);
        Assert.Equal(["a"], resumer.Asked.Select(x => x.Id));
        Assert.False(Written().Stamps.ContainsKey("x"));
        Assert.False(marks.Held.ContainsKey("x") || marks.Of.ContainsKey("x"));
    }

    // The expiry of a mark since replaced, already under way when it was replaced, takes nothing away.
    [Fact]
    public async Task An_expiry_already_under_way_for_a_replaced_mark_leaves_the_new_mark()
    {
        Previous(Fixture.T0, false, E("a", turn: RestoreTurn.Working));
        var clock = new Captured(Time);
        var resumer = new Resumer { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var r = Recorder(resumer: resumer, clock: clock);
        r.Start();
        await Until(() => resumer.Asked.Count == 1);
        // The restore's mark is the last timer armed for a day before its launch (its stamp comes last).
        (TimerCallback Callback, object? State, TimeSpan Due) expiry;
        lock (clock.Timers) { expiry = clock.Timers.Last(x => x.Due == RestoreProtocol.ShownFor); }
        resumer.Gate!.SetResult();
        await Until(() => told.Count == 1);
        resumer.Gate = null;

        Assert.Equal(LaunchOutcome.Started, (await r.RestoreAsync(new RestoreAsk { Session = "a", Cause = RestoreCause.Crash, MidTurn = true, By = "host-signin" })).Outcome);
        var replaced = marks.Of["a"];
        expiry.Callback(expiry.State);

        Assert.NotNull(replaced);
        Assert.Same(replaced, marks.Of["a"]);
    }

    // A session restored, then died while the host watched (a loop of this run): restored again on
    // request, and seen ready, it is shown restored. Only a death during that attempt holds it back.
    [Fact]
    public async Task A_session_that_died_after_an_earlier_restore_is_shown_restored_when_a_later_restore_succeeds()
    {
        Previous(Fixture.T0, false, E("a"));
        await using var r = Recorder(resumer: new Resumer());
        r.Start();
        await Until(() => told.Count == 1);
        source.Upsert(S("a"));
        source.Remove("a");
        Time.Advance(TimeSpan.FromMinutes(1));

        var again = await r.RestoreAsync(new RestoreAsk { Session = "a", Cause = RestoreCause.Crash, By = "host-signin" });

        Assert.Equal(LaunchOutcome.Started, again.Outcome);
        Assert.Equal(Time.GetUtcNow(), marks.Of["a"]!.At);
    }

    // A TimeProvider that keeps each timer's callback, to run one as if it had been dispatched before
    // its timer was disposed.
    sealed class Captured(FakeTimeProvider inner) : TimeProvider
    {
        public readonly List<(TimerCallback Callback, object? State, TimeSpan Due)> Timers = [];
        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (Timers) { Timers.Add((callback, state, dueTime)); }
            return inner.CreateTimer(callback, state, dueTime, period);
        }
    }

    sealed class Resumer : ISessionResumer
    {
        public readonly List<ResumeAsk> Asked = [];
        public readonly Queue<LaunchResult> Answers = new();
        public TaskCompletionSource? Gate;
        public int Most;
        int under;

        public async Task<LaunchResult> ResumeAsync(ResumeAsk ask, CancellationToken cancellationToken = default)
        {
            LaunchResult? answer;
            lock (Asked)
            {
                Asked.Add(ask);
                Most = Math.Max(Most, ++under);
                answer = Answers.Count > 0 ? Answers.Dequeue() : null;
            }
            try
            {
                if (Gate is { } gate) { await gate.Task; }
                return answer ?? new LaunchResult(LaunchOutcome.Started) { Session = ask.Id };
            }
            finally { lock (Asked) { under--; } }
        }
    }

    sealed class Marks : IRestoreMarks
    {
        public readonly System.Collections.Concurrent.ConcurrentDictionary<string, SessionRestored?> Of = new(StringComparer.Ordinal);
        public readonly System.Collections.Concurrent.ConcurrentDictionary<string, SessionRestored?> Before = new(StringComparer.Ordinal);
        // Held, not shown (the boundary): restored and Continue wait for the window to be seen ready.
        public readonly System.Collections.Concurrent.ConcurrentDictionary<string, SessionRestored> Held = new(StringComparer.Ordinal);
        public void Mark(string id, SessionRestored? mark)
        {
            if (mark is null || !(Held.TryGetValue(id, out var held) && held == mark)) { Before[id] = Of.GetValueOrDefault(id); }
            Of[id] = mark;
        }
        public void Hold(string id, SessionRestored mark) => Held[id] = mark;
        public void Revert(string id, SessionRestored mark)
        {
            if (Held.TryGetValue(id, out var held) && held == mark) { Held.TryRemove(id, out _); }
            if (Of.GetValueOrDefault(id) == mark) { Of[id] = Before.TryRemove(id, out var was) ? was : null; }
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

    // As its agent records it; one not listed has a record that says it has not ended (a registration
    // without Ended), and null is one with no record at all (a registration pruned).
    sealed class Ends : ISessionEnds
    {
        public readonly Dictionary<string, bool?> Of = new(StringComparer.Ordinal);
        public bool? Ended(AgentKind agent, string id) => Of.TryGetValue(id, out var ended) ? ended : false;
    }

    sealed class Marker(bool fresh, string? logon = null) : ILogonMarker
    {
        public bool Fresh() => fresh;
        public string? Logon() => logon;
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
