using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;

namespace AgentBridge.Host.Shadow;

// The run at a glance: which days were clean, how many mismatches of each kind there were, and how much was covered.
// An episode still going at midnight is in both days' files with the same start; it is one
// mismatch here.
public sealed record ShadowSummary(
    string From,
    string To,
    string? TimeZone,
    int GraceSeconds,
    IReadOnlyList<ShadowSummaryDay> Days,
    IReadOnlyList<ShadowSummaryKind> Kinds,
    IReadOnlyList<ShadowSummaryKind> Transient,
    IReadOnlyList<string> NotCompared,
    IReadOnlyList<ShadowSummaryReason> NotComparedWhy)
{
    // When the reports start and end, how long both sides were compared in all, and when events
    // started being counted (the first report written by a release that counts them).
    public DateTimeOffset? FirstSample { get; init; }
    public DateTimeOffset? LastSample { get; init; }
    public DateTimeOffset? Now { get; init; }
    public double ComparedHours { get; init; }
    public DateTimeOffset? CoverageSince { get; init; }
    public IReadOnlyList<ShadowCoverageLine> Coverage { get; init; } = [];
    // Sessions compared by agent; "unknown" for those seen only in reports from before coverage.
    public IReadOnlyDictionary<string, int> AgentSessions { get; init; } = new Dictionary<string, int>();
    public IReadOnlyDictionary<string, int> QuestionKinds { get; init; } = new Dictionary<string, int>();
    public IReadOnlyDictionary<string, int> MachineEvents { get; init; } = new Dictionary<string, int>();
    public IReadOnlyList<ShadowSummaryKnown> Known { get; init; } = [];
    // Each mismatch once, for the gate.
    [JsonIgnore]
    public IReadOnlyList<ShadowEpisode> Episodes { get; init; } = [];
    [JsonIgnore]
    public ShadowGateConfig Config { get; init; } = new();

    public int CleanDays => Days.Count(d => d.Verdict == "clean");
    // Clean and compared for most of the day: a day with ten minutes of comparison is clean but
    // says little.
    public const double FullDayHours = 20;
    public int FullCleanDays => Days.Count(d => d.Verdict == "clean" && d.ComparedHours >= FullDayHours);
    public int Mismatches => Kinds.Sum(k => k.Count);
    public int Compared => Days.Sum(d => d.Compared);
    public int UsageCompared => Days.Sum(d => d.UsageCompared);
    public int ResumableCompared => Days.Sum(d => d.ResumableCompared);

    // How many days back the earliest report is, today included: what the gate looks over.
    public static int DaysSinceFirst(string folder, DateOnly today)
    {
        var earliest = today;
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder, "shadow-????-??-??.json"))
            {
                if (DateOnly.TryParseExact(Path.GetFileNameWithoutExtension(file)["shadow-".Length..], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) && d < earliest) { earliest = d; }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return Math.Clamp(today.DayNumber - earliest.DayNumber + 1, 1, 92);
    }

    public static ShadowSummary Build(string folder, DateOnly today, int days, DateTimeOffset? now = null, ShadowGateConfig? config = null)
    {
        config ??= new ShadowGateConfig();
        DateTimeOffset? first = null, last = null, coverageSince = null, responsesSince = null;
        long comparedSeconds = 0;
        var all = new HashSet<string>(StringComparer.Ordinal);
        var agentOf = new Dictionary<string, string>(StringComparer.Ordinal);
        var questions = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var machine = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var background = new HashSet<string>(StringComparer.Ordinal);
        int transitions = 0, responses = 0, starts = 0, finishes = 0, compactions = 0, models = 0, resume = 0, usage = 0, resets = 0;
        var known = new Dictionary<(string Kind, string Cause), (int Samples, HashSet<string> Sessions)>();
        var failedQuestions = new Dictionary<string, ShadowFailedQuestion>(StringComparer.Ordinal);
        // Mismatches past a day's cap, by kind, from the days whose events they count against.
        var overflowCounted = new Dictionary<string, int>(StringComparer.Ordinal);
        var list = new List<ShadowSummaryDay>();
        var episodes = new Dictionary<(string, string?, DateTimeOffset), ShadowEpisode>();
        var overflow = new Dictionary<string, int>(StringComparer.Ordinal);
        var transient = new Dictionary<string, (int Count, long Longest)>(StringComparer.Ordinal);
        var why = new Dictionary<string, int>(StringComparer.Ordinal);
        string? zone = null;
        var grace = 0;
        for (var i = days - 1; i >= 0; i--)
        {
            var date = today.AddDays(-i).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var day = ShadowLedger.Read(folder, date);
            if (day is null) { list.Add(new(date, 0, 0, 0, 0, "no data", 0, 0)); continue; }
            zone ??= day.TimeZone;
            grace = Math.Max(grace, day.GraceSeconds);
            if (day.FirstSample is { } f && (first is null || f < first)) { first = f; }
            if (day.LastSample is { } l && (last is null || l > last)) { last = l; }
            comparedSeconds += day.ComparedSeconds;
            all.UnionWith(day.Sessions);
            if (day.Coverage is { } c)
            {
                if (c.Since is { } s && (coverageSince is null || s < coverageSince)) { coverageSince = s; }
                foreach (var (session, agent) in c.Sessions) { all.Add(session); agentOf[session] = agent; }
                foreach (var (kind, n) in c.Questions) { questions[kind] = questions.GetValueOrDefault(kind) + n; }
                foreach (var (kind, n) in c.MachineEvents) { machine[kind] = machine.GetValueOrDefault(kind) + n; }
                background.UnionWith(c.BackgroundSessions);
                transitions += c.StatusTransitions;
                responses += c.ResponseChanges;
                if (c.ResponsesSince is { } r && (responsesSince is null || r < responsesSince)) { responsesSince = r; }
                starts += c.BackgroundStarts;
                finishes += c.BackgroundFinishes;
                compactions += c.Compactions;
                models += c.ModelChanges;
                resume += c.ResumeSnapshots;
                usage += c.UsageReadings;
                resets += c.UsageResets;
                // A question failing across midnight is in both days: once, with every kind it had
                // on either and its latest time.
                foreach (var (key, q) in c.FailedQuestions)
                {
                    if (!failedQuestions.TryGetValue(key, out var was))
                    {
                        failedQuestions[key] = new ShadowFailedQuestion { Session = q.Session, Kinds = [.. q.Kinds], Last = q.Last };
                        continue;
                    }
                    foreach (var k in q.Kinds.Where(k => !was.Kinds.Contains(k))) { was.Kinds.Add(k); }
                    if (q.Last > was.Last) { was.Last = q.Last; }
                }
            }
            else if (ShadowCoverageTracker.SidesDownAndBack(day) is > 0 and var down)
            {
                // Before events were counted: a side going down and coming back is a machine
                // disagreement that ended.
                machine[ShadowCoverage.SideDownAndBack] = machine.GetValueOrDefault(ShadowCoverage.SideDownAndBack) + down;
            }
            foreach (var (kind, n) in day.Overflow)
            {
                if (ShadowCoverageKinds.Of(kind) is { } of && (day.Coverage is not null || ShadowCoverageKinds.FromEveryReport(of))) { overflowCounted[of] = overflowCounted.GetValueOrDefault(of) + n; }
            }
            foreach (var (kind, causes) in day.Known)
            {
                foreach (var (cause, k) in causes)
                {
                    var was = known.TryGetValue((kind, cause), out var w) ? w : (0, new HashSet<string>(StringComparer.Ordinal));
                    was.Item2.UnionWith(k.Sessions);
                    known[(kind, cause)] = (was.Item1 + k.Samples, was.Item2);
                }
            }
            foreach (var m in day.Mismatches)
            {
                var key = (m.Kind, m.Session, m.First);
                if (!episodes.TryGetValue(key, out var seen) || seen.Seconds < m.Seconds) { episodes[key] = m; }
            }
            foreach (var (kind, n) in day.Overflow) { overflow[kind] = overflow.GetValueOrDefault(kind) + n; }
            foreach (var (kind, t) in day.Transient)
            {
                var was = transient.GetValueOrDefault(kind);
                transient[kind] = (was.Count + t.Count, Math.Max(was.Longest, t.LongestSeconds));
            }
            foreach (var (reason, n) in day.NotComparedWhy) { why[reason] = why.GetValueOrDefault(reason) + n; }
            var hours = Math.Round(day.ComparedSeconds / 3600.0, 3);
            list.Add(new(date, day.Samples, day.Compared, hours, day.Mismatches.Count + day.Overflow.Values.Sum(), day.Verdict, day.UsageCompared, day.ResumableCompared) { Overflow = day.Overflow.Values.Sum() });
        }
        var kinds = episodes.Values.GroupBy(e => e.Kind)
            .Select(g => new ShadowSummaryKind(g.Key, g.Count() + overflow.GetValueOrDefault(g.Key), g.Select(e => e.Session).Where(s => s is not null).Distinct().Count(), g.Max(e => e.Seconds)))
            .Concat(overflow.Where(o => !episodes.Values.Any(e => e.Kind == o.Key)).Select(o => new ShadowSummaryKind(o.Key, o.Value, 0, 0)))
            .OrderByDescending(k => k.Count).ThenBy(k => k.Kind, StringComparer.Ordinal).ToList();
        var skew = transient.Select(t => new ShadowSummaryKind(t.Key, t.Value.Count, 0, t.Value.Longest))
            .OrderByDescending(k => k.Count).ThenBy(k => k.Kind, StringComparer.Ordinal).ToList();
        var reasons = why.Select(w => new ShadowSummaryReason(w.Key, w.Value)).OrderByDescending(r => r.Samples).ThenBy(r => r.Reason, StringComparer.Ordinal).ToList();

        var counted = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [ShadowCoverageKinds.Sessions] = all.Count,
            [ShadowCoverageKinds.Questions] = questions.Values.Sum(),
            [ShadowCoverageKinds.Transitions] = transitions,
            [ShadowCoverageKinds.Responses] = responses,
            [ShadowCoverageKinds.Background] = starts + finishes,
            [ShadowCoverageKinds.Compactions] = compactions,
            [ShadowCoverageKinds.Models] = models,
            [ShadowCoverageKinds.Resume] = resume,
            [ShadowCoverageKinds.Usage] = usage,
            [ShadowCoverageKinds.Resets] = resets,
            [ShadowCoverageKinds.Machine] = machine.Values.Sum(),
        };
        var lines = new List<ShadowCoverageLine>();
        foreach (var kind in ShadowCoverageKinds.All)
        {
            var since = ShadowCoverageKinds.FromEveryReport(kind) ? first : kind == ShadowCoverageKinds.Responses ? responsesSince : coverageSince;
            // A mismatch counts against the events counted over the same time.
            var n = counted[kind];
            int m, explained;
            if (kind == ShadowCoverageKinds.Questions)
            {
                // Each failed question once, whatever kinds of mismatch it had and however many
                // questions one mismatch ran on across: explained only if all its kinds are.
                explained = failedQuestions.Values.Count(q => q.Kinds.All(k => config.Explaining(new ShadowEpisode { Kind = k, Session = q.Session }) is not null));
                m = failedQuestions.Count - explained;
            }
            else
            {
                var against = episodes.Values.Where(e => ShadowCoverageKinds.Of(e.Kind) == kind && since is { } s && e.Last >= s).ToList();
                explained = against.Count(e => config.Explaining(e) is not null);
                // Past a day's cap there is no session to explain them by.
                m = against.Count - explained + overflowCounted.GetValueOrDefault(kind);
            }
            lines.Add(new(kind, n, m, explained, since is null ? null : ShadowStats.UpperBound(m, n), since));
        }
        var agents = agentOf.Values.GroupBy(a => a).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        if (all.Count > agentOf.Count) { agents["unknown"] = all.Count - agentOf.Count; }
        return new(list[0].Date, list[^1].Date, zone, grace, list, kinds, skew, ShadowKinds.NotCompared, reasons)
        {
            FirstSample = first,
            LastSample = last,
            Now = now,
            ComparedHours = Math.Round(comparedSeconds / 3600.0, 3),
            CoverageSince = coverageSince,
            Coverage = lines,
            AgentSessions = agents,
            QuestionKinds = questions,
            MachineEvents = machine,
            Known = [.. known.Select(k => new ShadowSummaryKnown(k.Key.Kind, k.Key.Cause, k.Value.Samples, k.Value.Sessions.Count))
                .OrderBy(k => k.Kind, StringComparer.Ordinal).ThenBy(k => k.Cause, StringComparer.Ordinal)],
            Episodes = [.. episodes.Values],
            Config = config,
        };
    }

    public string Format()
    {
        var b = new StringBuilder();
        b.AppendLine($"Shadow comparison with agent-ha-bridge, {From} to {To}{(TimeZone is null ? "" : $" ({TimeZone})")}");
        b.AppendLine();
        b.AppendLine("date        compared  mismatches  verdict");
        foreach (var d in Days)
        {
            var hours = d.Compared == 0 ? "-" : d.ComparedHours >= 0.1 ? $"{d.ComparedHours.ToString("0.0", CultureInfo.InvariantCulture)} h" : $"{Math.Max(1, (int)Math.Round(d.ComparedHours * 60))} min";
            b.AppendLine($"{d.Date}  {hours,8}  {d.Mismatches,10}  {d.Verdict}");
        }
        b.AppendLine();
        if (Kinds.Count == 0) { b.AppendLine("No mismatches."); }
        else
        {
            b.AppendLine($"{Mismatches} mismatch{(Mismatches == 1 ? "" : "es")} by kind (each lasted at least {GraceSeconds} s):");
            foreach (var k in Kinds)
            {
                // A usage kind's "session" is the agent whose allowance it is.
                var noun = k.Kind.StartsWith("usage.", StringComparison.Ordinal) ? "agent" : "session";
                var sessions = k.Sessions == 0 ? "" : $" in {k.Sessions} {noun}{(k.Sessions == 1 ? "" : "s")}";
                b.AppendLine($"  {k.Kind,-26} {k.Count,5}{sessions}, longest {Duration(k.LongestSeconds)}");
            }
        }
        if (Transient.Count > 0)
        {
            b.AppendLine();
            b.AppendLine($"Timing skew (disagreements shorter than {GraceSeconds} s, not mismatches):");
            foreach (var k in Transient) { b.AppendLine($"  {k.Kind,-26} {k.Count,5}, longest {Duration(k.LongestSeconds)}"); }
        }
        if (Known.Count > 0)
        {
            b.AppendLine();
            b.AppendLine("Known differences (the evidence matched a known cause; not mismatches):");
            foreach (var k in Known) { b.AppendLine($"  {k.Kind,-26} {k.Cause,-34} {k.Samples,6} samples in {k.Sessions} session{(k.Sessions == 1 ? "" : "s")}"); }
        }
        b.AppendLine();
        b.Append(FormatCoverage());
        b.AppendLine();
        b.AppendLine($"Usage compared in {UsageCompared} of {Compared} compared samples, the resume list in {ResumableCompared}.");
        foreach (var r in NotComparedWhy.Take(10)) { b.AppendLine($"  not compared, {r.Reason}: {r.Samples}"); }
        b.AppendLine("Not compared: " + string.Join("; ", NotCompared) + ".");
        b.AppendLine();
        b.AppendLine($"Clean days: {CleanDays} of {Days.Count}, {FullCleanDays} of them compared for at least {FullDayHours} h.");
        return b.ToString();
    }

    public string FormatCoverage()
    {
        var b = new StringBuilder();
        if (FirstSample is null) { b.AppendLine("Coverage: nothing compared yet."); return b.ToString(); }
        b.AppendLine($"Coverage since {Local(FirstSample.Value)}: {ComparedHours.ToString("0.0", CultureInfo.InvariantCulture)} h compared. "
            + (CoverageSince is { } s ? $"Events counted since {Local(s)}." : "No report counts events yet (written before coverage was counted)."));
        b.AppendLine("  kind                       events  mismatches  explained  95% upper bound");
        foreach (var c in Coverage)
        {
            var bound = c.UpperBound is { } u ? $"{(u * 100).ToString("0.0", CultureInfo.InvariantCulture)}%" : "-";
            b.AppendLine($"  {c.Kind,-24} {c.Events,9} {c.Mismatches,11} {c.Explained,10} {bound,16}");
        }
        if (AgentSessions.Count > 0) { b.AppendLine("  sessions by agent: " + string.Join(", ", AgentSessions.OrderByDescending(a => a.Value).Select(a => $"{a.Key} {a.Value}"))); }
        if (QuestionKinds.Count > 0) { b.AppendLine("  questions by kind: " + string.Join(", ", QuestionKinds.Select(q => $"{q.Key} {q.Value}"))); }
        if (MachineEvents.Count > 0) { b.AppendLine("  machine events: " + string.Join(", ", MachineEvents.Select(q => $"{q.Key} {q.Value}"))); }
        return b.ToString();
    }

    string Local(DateTimeOffset at)
    {
        var zone = TimeZone is { } id && TryZone(id) is { } z ? z : TimeZoneInfo.Local;
        return TimeZoneInfo.ConvertTime(at, zone).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

        static TimeZoneInfo? TryZone(string id)
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException) { return null; }
        }
    }

    static string Duration(long seconds) =>
        seconds < 60 ? $"{seconds} s" : seconds < 3600 ? $"{seconds / 60} min {seconds % 60} s" : $"{seconds / 3600} h {seconds % 3600 / 60} min";
}

public sealed record ShadowSummaryDay(string Date, int Samples, int Compared, double ComparedHours, int Mismatches, string Verdict, int UsageCompared, int ResumableCompared)
{
    // Mismatches past the day's cap: counted, with no time or session to them.
    public int Overflow { get; init; }
}

public sealed record ShadowSummaryReason(string Reason, int Samples);

public sealed record ShadowSummaryKind(string Kind, int Count, int Sessions, long LongestSeconds);

public sealed record ShadowSummaryKnown(string Kind, string Cause, int Samples, int Sessions);

// One kind of event: how many were compared, the mismatches about them (unexplained, and those
// gate.json explains), and the 95% upper bound on the unexplained rate; Since is when counting it
// began.
public sealed record ShadowCoverageLine(string Kind, int Events, int Mismatches, int Explained, double? UpperBound, DateTimeOffset? Since);

// shadow summary --gate: whether the comparison has covered enough, cleanly, for the machine to
// leave the daemon (docs/host-shadow.md, "The gate").
public sealed record ShadowGate(string Outcome, IReadOnlyList<string> Missing, IReadOnlyList<string> Failing, IReadOnlyList<string> NotCovered, IReadOnlyList<string> Notes)
{
    public const string Pass = "PASS";
    public const string NotYet = "NOT YET";
    public const string Fail = "FAIL";

    public static ShadowGate Evaluate(ShadowSummary s, (ShadowGateConfig Config, string? Problem) read)
    {
        var g = read.Config;
        var missing = new List<string>();
        var failing = new List<string>();
        var notes = new List<string>();
        if (read.Problem is { } problem) { notes.Add(problem); }
        int Events(string kind) => s.Coverage.FirstOrDefault(c => c.Kind == kind)?.Events ?? 0;
        void AtLeast(string kind, int needed, string what)
        {
            if (Events(kind) < needed) { missing.Add($"{what} {Events(kind)}/{needed}"); }
        }

        var questions = s.Coverage.FirstOrDefault(c => c.Kind == ShadowCoverageKinds.Questions);
        // An observed rate over the limit fails however few questions there are: each of those
        // failures has to be fixed or explained, and gate.json's explanations take it out.
        if (questions is { Events: > 0 } && (double)questions.Mismatches / questions.Events > g.QuestionsMaxRate)
        {
            failing.Add($"question mismatch rate {Percent((double)questions.Mismatches / questions.Events)} over {Percent(g.QuestionsMaxRate)}");
        }
        else if (questions is null || questions.Events < g.Questions) { missing.Add($"questions {questions?.Events ?? 0}/{g.Questions}"); }
        else if (questions.UpperBound > g.QuestionsMaxRate)
        {
            missing.Add($"questions: upper bound {Percent(questions.UpperBound!.Value)} over {Percent(g.QuestionsMaxRate)}, more questions needed");
        }
        AtLeast(ShadowCoverageKinds.Sessions, g.Sessions, "distinct sessions");
        AtLeast(ShadowCoverageKinds.Transitions, g.StatusTransitions, "status transitions");
        AtLeast(ShadowCoverageKinds.Background, g.Background, "background starts and finishes");
        AtLeast(ShadowCoverageKinds.Compactions, g.Compactions, "compactions");
        AtLeast(ShadowCoverageKinds.Resume, g.ResumeSnapshots, "resume snapshots");
        AtLeast(ShadowCoverageKinds.Usage, g.UsageReadings, "usage readings");
        AtLeast(ShadowCoverageKinds.Machine, g.MachineEvents, "machine events");
        var now = s.Now ?? DateTimeOffset.UtcNow;
        var hours = s.FirstSample is { } first ? (now - first).TotalHours : 0;
        if (hours < g.Hours) { missing.Add($"{Hours(hours)}/{Hours(g.Hours)} since the first sample"); }
        if (s.ComparedHours < g.ComparedHours) { missing.Add($"{Hours(s.ComparedHours)}/{Hours(g.ComparedHours)} compared"); }

        // Any mismatch in the last CleanHours that nothing explains fails, whatever else. By when it
        // was last seen, not by "open": a day's file keeps an episode open that a run stopped in.
        var recent = s.Episodes.Where(e => e.Last >= now - TimeSpan.FromHours(g.CleanHours) && g.Explaining(e) is null)
            .GroupBy(e => e.Kind).OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => $"{x.Key} x{x.Count()}").ToList();
        if (recent.Count > 0) { failing.Add($"unexplained mismatches in the last {Hours(g.CleanHours)}: {string.Join(", ", recent)}"); }
        // Past a day's cap a mismatch keeps no time or session, so a day that overflowed and might
        // lie in the window fails it: nothing says those were explained or long over.
        var zone = s.TimeZone is { } id && TryZone(id) is { } z ? z : TimeZoneInfo.Local;
        var windowStart = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now - TimeSpan.FromHours(g.CleanHours), zone).DateTime);
        var overflowed = s.Days.Where(d => d.Overflow > 0 && DateOnly.ParseExact(d.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture) >= windowStart).Sum(d => d.Overflow);
        if (overflowed > 0) { failing.Add($"{overflowed} mismatches past a day's cap of {ShadowLedger.MaxMismatches} in the last {Hours(g.CleanHours)}"); }

        var notCovered = s.AgentSessions.Where(a => a.Key != "unknown" && a.Value < g.AgentSessions).Select(a => $"{a.Key} ({a.Value} session{(a.Value == 1 ? "" : "s")})").ToList();
        // Before any report counts events no session has a known agent, so none is said to be missing.
        if (s.CoverageSince is not null) { foreach (var agent in new[] { "copilot", "claude", "codex" }.Where(a => !s.AgentSessions.ContainsKey(a))) { notCovered.Add($"{agent} (no sessions)"); } }
        if (s.CoverageSince is null) { notes.Add("no report counts events yet: they are counted from the first sample a release with coverage writes"); }
        var outcome = failing.Count > 0 ? Fail : missing.Count > 0 ? NotYet : Pass;
        return new(outcome, missing, failing, notCovered, notes);
    }

    public string Format()
    {
        var b = new StringBuilder();
        foreach (var n in Notes) { b.AppendLine($"Note: {n}."); }
        if (NotCovered.Count > 0) { b.AppendLine($"Not covered (fewer than the sessions an agent needs; not a failure): {string.Join(", ", NotCovered)}."); }
        b.AppendLine(Outcome switch
        {
            Pass => Pass,
            Fail => $"{Fail} ({string.Join("; ", Failing)})",
            _ => $"{NotYet} ({string.Join("; ", Missing)})",
        });
        return b.ToString();
    }

    static string Percent(double p) => (p * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%";

    static TimeZoneInfo? TryZone(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException) { return null; }
    }

    static string Hours(double h) => h.ToString("0.#", CultureInfo.InvariantCulture) + " h";
}
