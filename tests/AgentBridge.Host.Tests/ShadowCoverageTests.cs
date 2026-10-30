using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.Host.Shadow;
using Microsoft.Extensions.Time.Testing;
using Xunit;

// Coverage and the gate (docs/host-shadow.md, "Coverage" and "The gate"): events counted from the
// samples a run records, and the verdict over the reports. Every view here is invented.
public sealed class ShadowCoverageTests : IDisposable
{
    readonly string reports = Path.Combine(Path.GetTempPath(), "abhc-" + Guid.NewGuid().ToString("N")[..8]);
    readonly FakeTimeProvider time = new(new DateTimeOffset(2026, 10, 28, 8, 0, 0, TimeSpan.Zero));

    public ShadowCoverageTests() => time.SetLocalTimeZone(TimeZoneInfo.Utc);

    public void Dispose()
    {
        try { Directory.Delete(reports, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    const string A = "11111111-1111-4111-8111-111111111111";
    const string B = "22222222-2222-4222-8222-222222222222";

    static ShadowSession Session(string id, string status, string agent = "copilot", string[]? agents = null, string[]? shells = null, int compactions = 0, string? model = "m1", ShadowQuestion? question = null) => new()
    {
        Id = id,
        Agent = agent,
        Status = status,
        Agents = agents?.Length ?? 0,
        Shells = shells?.Length ?? 0,
        AgentIds = agents ?? [],
        ShellIds = shells ?? [],
        Compactions = compactions,
        Model = model,
        Question = question,
    };

    static ShadowView View(DateTimeOffset? started, IReadOnlyList<string>? resumable, Dictionary<string, ShadowUsage>? usage, params ShadowSession[] sessions) => new()
    {
        Sessions = sessions.ToDictionary(s => s.Id),
        StartedAt = started,
        Resumable = resumable,
        Usage = usage,
        SeesAgentHaBridge = true,
    };

    static ShadowQuestion Asked(string? call, DateTimeOffset? at, params ShadowField[] fields) => new() { CallId = call, AskedAt = at, Fields = fields };

    static Dictionary<string, ShadowUsage> Used(double percent, DateTimeOffset at) =>
        new() { ["copilot"] = new ShadowUsage { Reading = true, Windows = new Dictionary<string, double> { ["monthly"] = percent }, MeasuredAt = at } };

    void Sample(ShadowLedger ledger, ShadowView daemon, ShadowView host, bool resumable = false, params string[] usageFresh)
    {
        ledger.Record(new ShadowComparison(true, daemon.Sessions.Count, host.Sessions.Count, [], []) { ResumableCompared = resumable, UsageFresh = usageFresh }, daemon, host);
        time.Advance(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public void A_run_counts_each_session_question_transition_and_piece_of_work_once_however_many_samples_show_it()
    {
        var ledger = new ShadowLedger(reports, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(15), time);
        var t0 = time.GetUtcNow();
        var daemonStart = t0.AddHours(-5);
        var hostStart = t0.AddHours(-1);
        var choice = new ShadowField("choice", 2, "h");
        // A question pending over three samples, each side knowing it by its own identity.
        for (var i = 0; i < 3; i++)
        {
            Sample(ledger,
                View(daemonStart, ["one - repo"], Used(10, t0), Session(A, "waiting", question: Asked(null, t0, choice)), Session(B, "idle", agent: "claude")),
                View(hostStart, ["one - repo"], Used(10, t0), Session(A, "waiting", question: Asked("call-1", t0, choice)), Session(B, "idle", agent: "claude")),
                resumable: true, usageFresh: "copilot");
        }
        // Answered: working, with a background agent and a shell; a compaction and a new model.
        Sample(ledger,
            View(daemonStart, ["one - repo"], Used(10, t0), Session(A, "working", agents: ["c1"], shells: ["7"], compactions: 1, model: "m2"), Session(B, "idle", agent: "claude")),
            View(hostStart, ["one - repo"], Used(10, t0), Session(A, "working", agents: ["c1"], shells: ["7"], compactions: 1, model: "m2"), Session(B, "idle", agent: "claude")),
            resumable: true);
        // The shell ends; a second question, this one multi; a new resume list and a new host
        // usage reading, after the window reset; and the daemon restarted.
        var multi = new ShadowField("multi", 3, "h2");
        Sample(ledger,
            View(daemonStart.AddHours(5), ["two - repo", "one - repo"], Used(1, t0.AddMinutes(1)), Session(A, "waiting", agents: ["c1"], compactions: 1, model: "m2", question: Asked(null, t0.AddMinutes(1), multi)), Session(B, "idle", agent: "claude")),
            View(hostStart, ["two - repo", "one - repo"], Used(1, t0.AddMinutes(1)), Session(A, "waiting", agents: ["c1"], compactions: 1, model: "m2", question: Asked("call-2", t0.AddMinutes(1), multi)), Session(B, "idle", agent: "claude")),
            resumable: true, usageFresh: "copilot");

        var c = ledger.Today!.Coverage!;
        Assert.Equal(new Dictionary<string, string> { [ShadowHash.Session(A)] = "copilot", [ShadowHash.Session(B)] = "claude" }, c.Sessions);
        Assert.Equal(new Dictionary<string, int> { ["choice"] = 1, ["multi"] = 1 }, c.Questions);
        // The state each session was first compared in, then A's working and waiting again.
        Assert.Equal(4, c.StatusTransitions);
        Assert.Equal([ShadowHash.Session(A)], c.BackgroundSessions);
        Assert.Equal((2, 1), (c.BackgroundStarts, c.BackgroundFinishes));
        Assert.Equal((1, 1), (c.Compactions, c.ModelChanges));
        Assert.Equal((2, 2, 1), (c.ResumeSnapshots, c.UsageReadings, c.UsageResets));
        Assert.Equal(new Dictionary<string, int> { [ShadowCoverage.DaemonRestart] = 1 }, c.MachineEvents);

        // The comparison itself stopped for an hour (a reboot) and came back: one machine event,
        // and the host's restart across it is seen too.
        time.Advance(TimeSpan.FromHours(1));
        var after = new ShadowLedger(reports, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(15), time);
        Sample(after, View(daemonStart.AddHours(5), null, null), View(hostStart.AddHours(2), null, null));
        Assert.Equal(new Dictionary<string, int> { [ShadowCoverage.DaemonRestart] = 1, [ShadowCoverage.HostRestart] = 1, [ShadowCoverage.ObserverResumed] = 1 },
            ShadowLedger.Read(reports, "2026-10-28")!.Coverage!.MachineEvents);
    }

    // Found live (#40): the host going to waiting while the daemon stayed working was a status
    // mismatch with no event counted, since only the daemon's changes were.
    [Fact]
    public void Every_state_the_two_sides_are_compared_in_is_counted_once_whichever_side_changed_and_across_a_restart_or_midnight()
    {
        time.SetUtcNow(new DateTimeOffset(2026, 10, 28, 23, 58, 0, TimeSpan.Zero));
        var ledger = new ShadowLedger(reports, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(15), time);
        ShadowSession S(string status, string? response) => Session(A, status) with { ResponseTail = response };
        void Both(ShadowLedger l, ShadowSession d, ShadowSession h) => Sample(l, View(null, null, null, d), View(null, null, null, h));

        Both(ledger, S("working", "r1"), S("working", "r1"));
        Both(ledger, S("working", "r1"), S("working", "r1"));
        // Only the host changes: a new state compared, though the daemon's is unchanged.
        Both(ledger, S("working", "r1"), S("waiting", "r2"));
        Both(ledger, S("working", "r1"), S("waiting", "r2"));
        var c = ledger.Today!.Coverage!;
        Assert.Equal((2, 2), (c.StatusTransitions, c.ResponseChanges));

        // The comparison restarts: the same states are not counted again.
        var restarted = new ShadowLedger(reports, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(15), time);
        Both(restarted, S("working", "r1"), S("waiting", "r2"));
        Assert.Equal((2, 2), (restarted.Today!.Coverage!.StatusTransitions, restarted.Today.Coverage.ResponseChanges));

        // Past midnight neither are they, and the next change is.
        time.SetUtcNow(new DateTimeOffset(2026, 10, 29, 0, 0, 5, TimeSpan.Zero));
        Both(restarted, S("working", "r1"), S("waiting", "r2"));
        Both(restarted, S("idle", "r3"), S("waiting", "r2"));
        var next = restarted.Today!.Coverage!;
        Assert.Equal("2026-10-29", restarted.Today.Date);
        Assert.Equal((1, 1), (next.StatusTransitions, next.ResponseChanges));

        // A marker caught half written: a definite disagreement is still a state compared, and one
        // the host's waiting could match either way is waiting.
        Both(restarted, S("working", "r3") with { QuestionUnknown = true }, S("idle", "r2"));
        Assert.Equal("working/idle", next.Shown[ShadowHash.Session(A)].Status);
        Both(restarted, S("working", "r3") with { QuestionUnknown = true }, S("waiting", "r2"));
        Assert.Equal(("waiting/waiting", 3), (next.Shown[ShadowHash.Session(A)].Status, next.StatusTransitions));
    }

    // Found in review (#82): an episode is not what a rate counts. One running on across several
    // states is one episode, and a restart of the comparison during one state is two.
    [Fact]
    public void A_status_or_response_mismatch_counts_once_for_each_state_it_was_about_and_not_before_states_were_counted()
    {
        Directory.CreateDirectory(reports);
        var session = ShadowHash.Session(A);
        void Samples(ShadowLedger ledger, int n, string hostResponse, bool differ = true)
        {
            for (var i = 0; i < n; i++)
            {
                ShadowDifference[] diffs = differ ? [new(ShadowKinds.Status, session, "daemon=idle host=working"), new(ShadowKinds.Response, session, "the end of the latest response differs")] : [];
                ledger.Record(new ShadowComparison(true, 1, 1, diffs, []),
                    View(null, null, null, Session(A, "idle") with { ResponseTail = "r1" }),
                    View(null, null, null, Session(A, differ ? "working" : "idle") with { ResponseTail = differ ? hostResponse : "r1" }));
                time.Advance(TimeSpan.FromSeconds(15));
            }
        }
        Samples(new ShadowLedger(reports, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(15), time), 6, "r2");
        // Restarted during the same disagreement: another episode, the same states.
        var restarted = new ShadowLedger(reports, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(15), time);
        Samples(restarted, 6, "r2");
        // The host's response moves on, still differing: a second response state, failing too.
        Samples(restarted, 2, "r3");
        Samples(restarted, 1, "r1", differ: false);
        var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);

        var summary = ShadowSummary.Build(reports, today, 1, time.GetUtcNow());
        var lines = summary.Coverage.ToDictionary(l => l.Kind);
        Assert.Equal(2, summary.Kinds.Single(k => k.Kind == ShadowKinds.Response).Count);
        Assert.Equal((3, 2), (lines[ShadowCoverageKinds.Responses].Events, lines[ShadowCoverageKinds.Responses].Mismatches));
        // idle/working, then idle/idle.
        Assert.Equal((2, 1), (lines[ShadowCoverageKinds.Transitions].Events, lines[ShadowCoverageKinds.Transitions].Mismatches));

        // The same day as a release before states were counted wrote it, a day's cap passed: its
        // status episodes and overflow count against the daemon's changes, as they did, and its
        // response ones have nothing to count against.
        var path = Path.Combine(reports, ShadowLedger.FileName(today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)));
        var day = JsonNode.Parse(File.ReadAllText(path))!;
        var coverage = day["coverage"]!.AsObject();
        coverage.Remove("statesSince");
        coverage.Remove("failedStates");
        day["overflow"] = new JsonObject { [ShadowKinds.Response] = 3, [ShadowKinds.Status] = 2 };
        File.WriteAllText(path, day.ToJsonString());
        lines = ShadowSummary.Build(reports, today, 1, time.GetUtcNow()).Coverage.ToDictionary(l => l.Kind);
        Assert.Equal(0, lines[ShadowCoverageKinds.Responses].Mismatches);
        Assert.Equal(4, lines[ShadowCoverageKinds.Transitions].Mismatches);
    }

    // Found in review (#82): a state a mismatch was about only during its grace went uncounted, a
    // state past the cap could be counted again after a restart, and midnight reset when states
    // began to be counted.
    [Fact]
    public void A_state_changed_during_the_grace_fails_too_one_past_the_cap_fails_once_and_midnight_keeps_when_counting_began()
    {
        Directory.CreateDirectory(reports);
        time.SetUtcNow(new DateTimeOffset(2026, 10, 28, 23, 58, 0, TimeSpan.Zero));
        var session = ShadowHash.Session(A);
        var ledger = new ShadowLedger(reports, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(15), time);
        void Sample(string hostStatus, bool differ = true)
        {
            ledger.Record(new ShadowComparison(true, 1, 1, differ ? [new ShadowDifference(ShadowKinds.Status, session, $"daemon=idle host={hostStatus}")] : [], []),
                View(null, null, null, Session(A, "idle")), View(null, null, null, Session(A, hostStatus)));
            time.Advance(TimeSpan.FromSeconds(15));
        }
        Sample("working");
        Sample("working");
        Sample("waiting");
        Sample("waiting");
        Sample("waiting");
        Sample("waiting");
        var c = ledger.Today!.Coverage!;
        Assert.Equal(2, c.StatusTransitions);
        Assert.Equal(2, c.FailedStates.Count);

        // Past midnight, the cutoff is the day before's, and the state that failed then is not a
        // new failure today.
        var since = c.StatesSince;
        time.SetUtcNow(new DateTimeOffset(2026, 10, 29, 0, 0, 5, TimeSpan.Zero));
        Sample("waiting");
        Assert.Equal("2026-10-29", ledger.Today!.Date);
        Assert.Equal(since, ledger.Today.Coverage!.StatesSince);
        Assert.True(ledger.Today.Coverage.Shown[session].StatusFailed);
        Assert.Empty(ledger.Today.Coverage.FailedStates);

        // Past the cap, the state fails once, whoever asks again.
        var full = new ShadowCoverage();
        for (var i = 0; i < 5000; i++) { full.FailedStates["k" + i] = new ShadowFailedState(); }
        full.Shown[session] = new ShadowShown { Status = "idle/working", StatusAt = time.GetUtcNow() };
        var key = ShadowCoverageTracker.StateKey(full, session, ShadowKinds.Status)!;
        ShadowCoverageTracker.StateFailed(full, session, ShadowKinds.Status, key, time.GetUtcNow());
        ShadowCoverageTracker.StateFailed(full, session, ShadowKinds.Status, key, time.GetUtcNow());
        Assert.Equal(1, full.FailedStatesOverflow[ShadowKinds.Status]);
    }

    // Found in review (#82): with a long grace, a response changing every sample went through more
    // states than an episode kept, and those past it were not counted as failing.
    [Fact]
    public void Every_state_a_long_disagreement_went_through_in_its_grace_fails_those_past_what_is_kept_by_identity_included()
    {
        Directory.CreateDirectory(reports);
        var session = ShadowHash.Session(A);
        var ledger = new ShadowLedger(reports, TimeSpan.FromSeconds(1500), TimeSpan.FromSeconds(5), time);
        // A new response every sample, 257 of them, more than are kept by identity; then the last
        // goes on unchanged past the grace: the same state, counted once.
        for (var i = 0; i <= 305; i++)
        {
            ledger.Record(new ShadowComparison(true, 1, 1, [new ShadowDifference(ShadowKinds.Response, session, "the end of the latest response differs")], []),
                View(null, null, null, Session(A, "idle") with { ResponseTail = "d" }), View(null, null, null, Session(A, "idle") with { ResponseTail = "h" + Math.Min(i, 256) }));
            time.Advance(TimeSpan.FromSeconds(5));
        }
        var c = ledger.Today!.Coverage!;
        Assert.Equal(257, c.ResponseChanges);
        Assert.Equal(257, c.FailedStates.Count + c.FailedStatesOverflow.GetValueOrDefault(ShadowKinds.Response));
        Assert.True(c.Shown[session].ResponseFailed);
    }

    [Fact]
    public void The_upper_bound_is_three_over_n_with_no_mismatches_and_the_exact_clopper_pearson_bound_otherwise()
    {
        Assert.Null(ShadowStats.UpperBound(0, 0));
        Assert.Equal(0.03, ShadowStats.UpperBound(0, 100)!.Value, 10);
        // The one-sided 95% bounds for 1 and 2 in 100, and for 10 in 1000.
        Assert.Equal(0.0466, ShadowStats.UpperBound(1, 100)!.Value, 3);
        Assert.Equal(0.0616, ShadowStats.UpperBound(2, 100)!.Value, 3);
        Assert.Equal(0.0168, ShadowStats.UpperBound(10, 1000)!.Value, 3);
        Assert.Equal(1, ShadowStats.UpperBound(5, 5));
    }

    // A report as the release before coverage wrote it: sessions listed, a side down and back.
    void OldReport(string date, DateTimeOffset first, params string[] sessions) =>
        File.WriteAllText(Path.Combine(reports, ShadowLedger.FileName(date)), new JsonObject
        {
            ["schema"] = 1,
            ["date"] = date,
            ["timeZone"] = "UTC",
            ["graceSeconds"] = 60,
            ["intervalSeconds"] = 15,
            ["samples"] = 100,
            ["compared"] = 100,
            ["comparedSeconds"] = 1500,
            ["firstSample"] = first.ToString("o"),
            ["lastSample"] = first.AddMinutes(25).ToString("o"),
            ["sessions"] = new JsonArray([.. sessions.Select(s => (JsonNode)s)]),
            ["mismatches"] = new JsonArray(),
            ["transient"] = new JsonObject { ["machine.host"] = new JsonObject { ["count"] = 1, ["longestSeconds"] = 30 } },
        }.ToJsonString());

    void Gate(object config) => File.WriteAllText(Path.Combine(reports, ShadowGateConfig.FileName), JsonSerializer.Serialize(config));

    ShadowGate Verdict()
    {
        var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        var read = ShadowGateConfig.Read(reports);
        return ShadowGate.Evaluate(ShadowSummary.Build(reports, today, ShadowSummary.DaysSinceFirst(reports, today), time.GetUtcNow(), read.Config), read);
    }

    [Fact]
    public void The_gate_waits_for_enough_of_everything_fails_on_an_unexplained_mismatch_and_counts_what_reports_from_before_coverage_hold()
    {
        Directory.CreateDirectory(reports);
        // Last night, before coverage: two sessions and the host down and back.
        OldReport("2026-10-27", new DateTimeOffset(2026, 10, 27, 20, 0, 0, TimeSpan.Zero), ShadowHash.Session(A), "sffffffffffff");
        Gate(new
        {
            questions = 1,
            questionsMaxRate = 1.0,
            sessions = 3,
            statusTransitions = 1,
            background = 0,
            compactions = 0,
            resumeSnapshots = 1,
            usageReadings = 0,
            machineEvents = 1,
            hours = 24,
            comparedHours = 0.01,
        });
        var ledger = new ShadowLedger(reports, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(15), time);
        var q = Asked("call-1", time.GetUtcNow(), new ShadowField("choice", 2, "h"));
        Sample(ledger, View(null, ["x"], null, Session(A, "waiting", question: q), Session(B, "idle")), View(null, ["x"], null, Session(A, "waiting", question: q), Session(B, "idle")), resumable: true);
        Sample(ledger, View(null, ["x"], null, Session(A, "working"), Session(B, "idle")), View(null, ["x"], null, Session(A, "working"), Session(B, "idle")), resumable: true);

        var waiting = Verdict();
        Assert.Equal(ShadowGate.NotYet, waiting.Outcome);
        Assert.Equal(["12 h/24 h since the first sample"], waiting.Missing);
        // Fewer than five sessions of an agent is not covered, not a failure.
        Assert.Contains("copilot (2 sessions)", waiting.NotCovered);

        time.Advance(TimeSpan.FromHours(12));
        Assert.Equal(ShadowGate.Pass, Verdict().Outcome);

        // A mismatch in the last day fails it, until gate.json explains it.
        for (var i = 0; i < 6; i++)
        {
            ledger.Record(new ShadowComparison(true, 1, 1, [new ShadowDifference(ShadowKinds.Status, ShadowHash.Session(A), "daemon=working host=idle")], []), new ShadowView(), new ShadowView());
            time.Advance(TimeSpan.FromSeconds(15));
        }
        var failing = Verdict();
        Assert.Equal(ShadowGate.Fail, failing.Outcome);
        Assert.Contains("status x1", Assert.Single(failing.Failing));
        Gate(new { questions = 1, questionsMaxRate = 1.0, sessions = 3, statusTransitions = 1, background = 0, compactions = 0, resumeSnapshots = 1, usageReadings = 0, machineEvents = 1, comparedHours = 0.01, explained = new[] { new { kind = ShadowKinds.Status, session = ShadowHash.Session(A), reason = "investigated: the daemon's pass was late" } } });
        Assert.Equal(ShadowGate.Pass, Verdict().Outcome);
        // Explained only through a time before it ended (a report from before its cause was
        // classified, say): it still fails.
        Gate(new { questions = 1, questionsMaxRate = 1.0, sessions = 3, statusTransitions = 1, background = 0, compactions = 0, resumeSnapshots = 1, usageReadings = 0, machineEvents = 1, comparedHours = 0.01, explained = new[] { new { kind = ShadowKinds.Status, session = ShadowHash.Session(A), through = time.GetUtcNow() - TimeSpan.FromMinutes(5), reason = "an earlier one" } } });
        Assert.Equal(ShadowGate.Fail, Verdict().Outcome);
    }

    // Found in review (#77): one mismatch running on across several questions in a session was
    // one episode against each question counted, and two kinds of mismatch on one question two.
    [Fact]
    public void Each_failed_question_counts_once_and_a_failure_rate_over_the_limit_fails_however_few_questions_there_are()
    {
        Directory.CreateDirectory(reports);
        var ledger = new ShadowLedger(reports, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(15), time);
        var session = ShadowHash.Session(A);
        void Ask(string call, int samples, params ShadowDifference[] diffs)
        {
            var q = Asked(call, time.GetUtcNow(), new ShadowField("choice", 2, "h"));
            for (var i = 0; i < samples; i++)
            {
                ledger.Record(new ShadowComparison(true, 1, 1, diffs, []), View(null, null, null, Session(A, "waiting", question: q)), View(null, null, null, Session(A, "waiting", question: q)));
                time.Advance(TimeSpan.FromSeconds(15));
            }
        }
        void Work() => Sample(ledger, View(null, null, null, Session(A, "working")), View(null, null, null, Session(A, "working")));
        void Config(int questions, params object[] explained) => Gate(new { questions, sessions = 0, statusTransitions = 0, background = 0, compactions = 0, resumeSnapshots = 0, usageReadings = 0, machineEvents = 0, hours = 0, comparedHours = 0, cleanHours = 0, explained });

        for (var i = 0; i < 38; i++) { Ask("fine-" + i, 1); Work(); }
        Config(40);
        Assert.Equal(["questions 38/40"], Verdict().Missing);

        // Two questions in a row with the same two kinds of mismatch: one episode of each kind,
        // running across both.
        ShadowDifference[] wrong = [new(ShadowKinds.QuestionOptions, session, "field 1: daemon=2 host=3"), new(ShadowKinds.QuestionFieldType, session, "field 1: daemon=choice host=multi")];
        Ask("wrong-1", 6, wrong);
        Ask("wrong-2", 6, wrong);
        Work();
        Assert.Equal(2, ledger.Today!.Coverage!.FailedQuestions.Count);
        // Two in forty is 5%, over the 3% allowed, however short of a hundred the questions are.
        Config(100);
        var verdict = Verdict();
        Assert.Equal(ShadowGate.Fail, verdict.Outcome);
        Assert.Equal(["question mismatch rate 5.0% over 3.0%"], verdict.Failing);

        // Investigated and explained, both kinds: no failure, and forty questions say too little.
        Config(40, new { kind = ShadowKinds.QuestionOptions, session }, new { kind = ShadowKinds.QuestionFieldType, session });
        var explained = Verdict();
        Assert.Equal(ShadowGate.NotYet, explained.Outcome);
        Assert.Equal(["questions: upper bound 7.5% over 3.0%, more questions needed"], explained.Missing);
        // Explained only through a time before they failed: they still count against the rate.
        var before = time.GetUtcNow() - TimeSpan.FromMinutes(10);
        Config(100, new { kind = ShadowKinds.QuestionOptions, session, through = before }, new { kind = ShadowKinds.QuestionFieldType, session, through = before });
        Assert.Equal(ShadowGate.Fail, Verdict().Outcome);
        Config(40, new { kind = ShadowKinds.QuestionOptions, session, through = time.GetUtcNow() }, new { kind = ShadowKinds.QuestionFieldType, session, through = time.GetUtcNow() });
        Assert.Equal(ShadowGate.NotYet, Verdict().Outcome);
    }

    // Found in review (#91): a question failing in two ways is explained kind by kind, each through
    // when that kind last failed, not when the question last failed in any way.
    [Fact]
    public void A_question_failing_in_two_ways_is_explained_by_each_kind_through_when_that_kind_last_failed()
    {
        Directory.CreateDirectory(reports);
        var ledger = new ShadowLedger(reports, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(15), time);
        var session = ShadowHash.Session(A);
        var q = Asked("q-1", time.GetUtcNow(), new ShadowField("choice", 2, "h"));
        void Samples(int n, params ShadowDifference[] diffs)
        {
            for (var i = 0; i < n; i++)
            {
                ledger.Record(new ShadowComparison(true, 1, 1, diffs, []), View(null, null, null, Session(A, "waiting", question: q)), View(null, null, null, Session(A, "waiting", question: q)));
                time.Advance(TimeSpan.FromSeconds(15));
            }
        }
        ShadowDifference options = new(ShadowKinds.QuestionOptions, session, "field 1: daemon=2 host=3"), type = new(ShadowKinds.QuestionFieldType, session, "field 1: daemon=choice host=multi");
        Samples(6, options, type);
        var optionsEnded = time.GetUtcNow();
        Samples(6, type);
        Sample(ledger, View(null, null, null, Session(A, "working")), View(null, null, null, Session(A, "working")));
        void Config(DateTimeOffset optionsThrough) => Gate(new
        {
            questions = 1, questionsMaxRate = 0.5, sessions = 0, statusTransitions = 0, background = 0, compactions = 0, resumeSnapshots = 0, usageReadings = 0, machineEvents = 0, hours = 0, comparedHours = 0, cleanHours = 0,
            explained = new object[] { new { kind = ShadowKinds.QuestionOptions, session, through = optionsThrough }, new { kind = ShadowKinds.QuestionFieldType, session, through = time.GetUtcNow() } },
        });

        // Each kind explained through its own end: the question no longer counts as failed, and one
        // question is too few to say more.
        Config(optionsEnded);
        Assert.Equal(ShadowGate.NotYet, Verdict().Outcome);
        // The options' explanation ending before the options did: it still counts.
        Config(optionsEnded - TimeSpan.FromMinutes(2));
        Assert.Equal(ShadowGate.Fail, Verdict().Outcome);
    }

    // Found in review (#91): a report from the build before kinds had their own times, resumed the
    // same day, moved every old kind's time on with the new one.
    [Fact]
    public void A_question_failing_in_a_report_from_before_kinds_had_times_keeps_its_old_kinds_times_when_it_fails_again()
    {
        Directory.CreateDirectory(reports);
        var session = ShadowHash.Session(A);
        var q = Asked("q-1", time.GetUtcNow(), new ShadowField("choice", 2, "h"));
        void Samples(ShadowLedger ledger, int n, params ShadowDifference[] diffs)
        {
            for (var i = 0; i < n; i++)
            {
                ledger.Record(new ShadowComparison(true, 1, 1, diffs, []), View(null, null, null, Session(A, "waiting", question: q)), View(null, null, null, Session(A, "waiting", question: q)));
                time.Advance(TimeSpan.FromSeconds(15));
            }
        }
        ShadowDifference options = new(ShadowKinds.QuestionOptions, session, "field 1: daemon=2 host=3"), type = new(ShadowKinds.QuestionFieldType, session, "field 1: daemon=choice host=multi");
        Samples(new ShadowLedger(reports, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(15), time), 6, options, type);
        var optionsEnded = time.GetUtcNow();
        // As the earlier build wrote it: no kindLast.
        var file = Directory.GetFiles(reports, "shadow-*.json").Single();
        var json = JsonNode.Parse(File.ReadAllText(file))!;
        foreach (var (_, failed) in json["coverage"]!["failedQuestions"]!.AsObject()) { failed!.AsObject().Remove("kindLast"); }
        File.WriteAllText(file, json.ToJsonString());

        var resumed = new ShadowLedger(reports, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(15), time);
        Samples(resumed, 6, type);
        Sample(resumed, View(null, null, null, Session(A, "working")), View(null, null, null, Session(A, "working")));
        Gate(new
        {
            questions = 1, questionsMaxRate = 0.4, sessions = 0, statusTransitions = 0, background = 0, compactions = 0, resumeSnapshots = 0, usageReadings = 0, machineEvents = 0, hours = 0, comparedHours = 0, cleanHours = 0,
            explained = new object[] { new { kind = ShadowKinds.QuestionOptions, session, through = optionsEnded }, new { kind = ShadowKinds.QuestionFieldType, session, through = time.GetUtcNow() } },
        });
        // (The comparison's restart counts the question showing again: two events, one failure.)
        Assert.Equal(ShadowGate.NotYet, Verdict().Outcome);
    }

    // Found in review (#77): a question one side stops showing is the same question, and a question
    // failing across midnight keeps the kinds it had before it.
    [Fact]
    public void A_failed_question_keeps_one_key_while_either_side_shows_it_and_every_kind_it_had_across_midnight()
    {
        Directory.CreateDirectory(reports);
        time.SetUtcNow(new DateTimeOffset(2026, 10, 28, 23, 58, 0, TimeSpan.Zero));
        var ledger = new ShadowLedger(reports, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(15), time);
        var session = ShadowHash.Session(A);
        var asked = time.GetUtcNow();
        var field = new ShadowField("choice", 2, "h");
        void Show(bool onHost, ShadowDifference diff, int samples)
        {
            for (var i = 0; i < samples; i++)
            {
                ledger.Record(new ShadowComparison(true, 1, 1, [diff], []),
                    View(null, null, null, Session(A, "waiting", question: Asked(null, asked, field))),
                    View(null, null, null, Session(A, onHost ? "waiting" : "working", question: onHost ? Asked("call-1", asked, field) : null)));
                time.Advance(TimeSpan.FromSeconds(15));
            }
        }

        // Its options differ for a while before midnight; after it the host stops showing it.
        Show(onHost: true, new ShadowDifference(ShadowKinds.QuestionOptions, session, "field 1: daemon=2 host=3"), 6);
        Show(onHost: false, new ShadowDifference(ShadowKinds.QuestionMissingOnHost, session, "daemon fields=1"), 8);

        var today = new DateOnly(2026, 10, 29);
        ShadowCoverageLine Questions(ShadowGateConfig config) => ShadowSummary.Build(reports, today, 2, time.GetUtcNow(), config).Coverage.Single(c => c.Kind == ShadowCoverageKinds.Questions);
        Assert.Equal((1, 1, 0), (Questions(new()).Events, Questions(new()).Mismatches, Questions(new()).Explained));
        // Explaining only what it did after midnight leaves what it did before unexplained.
        var partly = new ShadowGateConfig { Explained = [new() { Kind = ShadowKinds.QuestionMissingOnHost, Session = session }] };
        Assert.Equal((1, 0), (Questions(partly).Mismatches, Questions(partly).Explained));
    }
    // Found in review (#77): a question the daemon showed first kept the daemon's key until the host
    // showed it, then took the host's; and one side moving on first charged the new question to the
    // kind of the old one the other side still showed.
    [Fact]
    public void A_question_keeps_the_key_it_was_first_known_by_and_is_counted_as_the_kind_of_the_side_that_shows_it_first()
    {
        Directory.CreateDirectory(reports);
        var ledger = new ShadowLedger(reports, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(15), time);
        var session = ShadowHash.Session(A);
        var asked = time.GetUtcNow();
        var choice = new ShadowField("choice", 2, "h");
        var multi = new ShadowField("multi", 3, "h2");
        void Show(ShadowQuestion? daemon, ShadowQuestion? host, ShadowDifference? diff, int samples)
        {
            for (var i = 0; i < samples; i++)
            {
                ledger.Record(new ShadowComparison(true, 1, 1, diff is null ? [] : [diff], []),
                    View(null, null, null, Session(A, daemon is null ? "working" : "waiting", question: daemon)),
                    View(null, null, null, Session(A, host is null ? "working" : "waiting", question: host)));
                time.Advance(TimeSpan.FromSeconds(15));
            }
        }

        // The daemon shows it for a while before the host does, then their options differ.
        Show(Asked(null, asked, choice), null, new(ShadowKinds.QuestionMissingOnHost, session, "daemon fields=1"), 6);
        Show(Asked(null, asked, choice), Asked("call-1", asked, choice), new(ShadowKinds.QuestionOptions, session, "field 1: 2 options, not the same ones"), 6);
        Assert.Single(ledger.Today!.Coverage!.FailedQuestions);

        // Five minutes later the daemon shows the next question, a multi, a sample before the host.
        var next = asked.AddMinutes(5);
        Show(Asked(null, next, multi), Asked("call-1", asked, choice), null, 1);
        Show(Asked(null, next, multi), Asked("call-2", next, multi), null, 1);
        Assert.Equal(new Dictionary<string, int> { ["choice"] = 1, ["multi"] = 1 }, ledger.Today.Coverage.Questions);
    }
    // Found in review (#77): past a day's cap a mismatch keeps no time or session, so nothing can
    // say it was explained or over.
    [Fact]
    public void Mismatches_past_a_days_cap_in_the_clean_window_fail_the_gate()
    {
        Directory.CreateDirectory(reports);
        OldReport("2026-10-28", time.GetUtcNow().AddHours(-1));
        var path = Path.Combine(reports, ShadowLedger.FileName("2026-10-28"));
        var day = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        day["overflow"] = new JsonObject { [ShadowKinds.Status] = 3 };
        File.WriteAllText(path, day.ToJsonString());
        Gate(new { questions = 0, sessions = 0, statusTransitions = 0, background = 0, compactions = 0, resumeSnapshots = 0, usageReadings = 0, machineEvents = 0, hours = 0, comparedHours = 0 });

        Assert.Equal([$"3 mismatches past a day's cap of {ShadowLedger.MaxMismatches} in the last 24 h"], Verdict().Failing);
    }

    // Found in review (#77): redeployed partway through a day, the coverage the release began
    // writing lost the sides that went down and came back earlier that day.
    [Fact]
    public void A_day_begun_before_coverage_keeps_its_earlier_machine_events_once_coverage_starts()
    {
        Directory.CreateDirectory(reports);
        OldReport("2026-10-28", time.GetUtcNow().AddMinutes(-26));
        var ledger = new ShadowLedger(reports, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(15), time);

        Sample(ledger, View(null, null, null), View(null, null, null));

        Assert.Equal(new Dictionary<string, int> { [ShadowCoverage.SideDownAndBack] = 1 }, ledger.Today!.Coverage!.MachineEvents);
        var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        Assert.Equal(1, ShadowSummary.Build(reports, today, 1, time.GetUtcNow()).Coverage.Single(c => c.Kind == ShadowCoverageKinds.Machine).Events);
    }
}