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
        // waiting, working, waiting.
        Assert.Equal(2, c.StatusTransitions);
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