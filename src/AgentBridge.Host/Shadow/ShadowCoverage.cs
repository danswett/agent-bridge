using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentBridge.Host.Shadow;

// How much the comparison has actually covered, counted in independent events rather than
// samples: a session seen at every 15-second sample for a day is one session, and a question
// pending for ten minutes is one question. What the gate (docs/host-shadow.md, "The gate") asks of
// a machine before it leaves the daemon is enough of each.
//
// Each day's report carries its own counts; the summary adds them up. Hashed session ids and
// question kinds only, like the rest of the report.
public sealed class ShadowCoverage
{
    // When this day's file started counting events: the first sample written by a release that
    // counts them. Reports written before then have no coverage at all (and "since" says so).
    public DateTimeOffset? Since { get; set; }
    // Every session compared, by hashed id, with its agent.
    public SortedDictionary<string, string> Sessions { get; set; } = new(StringComparer.Ordinal);
    // Questions asked, by kind (choice, multi, text): one for each question either side showed,
    // however long it was pending and whether or not it was answered.
    public SortedDictionary<string, int> Questions { get; set; } = new(StringComparer.Ordinal);
    // Questions a question.* mismatch was about (one that lasted the grace), each once however
    // many kinds of mismatch it had, by a hash of its session and identity: what the question
    // rate counts, since one mismatch can run on across several questions in a session.
    public SortedDictionary<string, ShadowFailedQuestion> FailedQuestions { get; set; } = new(StringComparer.Ordinal);
    // A session's status changing (working, idle, waiting, ending) on either side, once a sample
    // however many sides changed, and the status each session is in when first compared: each
    // state the two were compared in, so a status mismatch is always about one (#40). An earlier
    // release counted the daemon's changes alone, so a disagreement begun by the host's change
    // alone counted nothing.
    public int StatusTransitions { get; set; }
    // Likewise each latest response the two were compared on: one side's changing, or the first.
    public int ResponseChanges { get; set; }
    // When states (statuses and responses) began to be counted this way, and their failures with
    // them: before it, a status mismatch is counted by its episode against the daemon's changes,
    // and a response one has nothing to count against.
    public DateTimeOffset? StatesSince { get; set; }
    // What each session showing on both sides was last compared in, by its hash: a status pair of
    // tokens and a hash of the two responses' hashes, each with when it was first counted, so a
    // restart of the comparison does not count a state again. Carried across midnight; a session
    // gone from both sides is dropped.
    public SortedDictionary<string, ShadowShown> Shown { get; set; } = new(StringComparer.Ordinal);
    // The states a status or activity.response mismatch was about (one that lasted the grace),
    // each once, by a hash of its session, kind and when it was counted: what those rates count.
    // An episode is not: one can run on across many states, and a restart of the comparison ends
    // one while the state it was about goes on. Past the cap, counted by kind in the overflow.
    public SortedDictionary<string, ShadowFailedState> FailedStates { get; set; } = new(StringComparer.Ordinal);
    public SortedDictionary<string, int> FailedStatesOverflow { get; set; } = new(StringComparer.Ordinal);
    // Sessions that had background agents or shells at some sample, and the starts and finishes
    // of that work as the daemon listed it.
    public List<string> BackgroundSessions { get; set; } = [];
    public int BackgroundStarts { get; set; }
    public int BackgroundFinishes { get; set; }
    public int Compactions { get; set; }
    public int ModelChanges { get; set; }
    // A resume list compared that differed, on either side, from the last one compared.
    public int ResumeSnapshots { get; set; }
    // A new reading of an agent's usage by the host, judged against the daemon's at the time.
    public int UsageReadings { get; set; }
    // A usage window's percentage dropping, as at the start of a new period.
    public int UsageResets { get; set; }
    // A daemon or host restart, or the comparison itself coming back after a gap (a logoff or a
    // reboot), each counted at the first sample compared after it.
    public SortedDictionary<string, int> MachineEvents { get; set; } = new(StringComparer.Ordinal);
    // The two processes' start times last seen, so a restart across one of the comparison's own
    // is still seen.
    public DateTimeOffset? DaemonStartedAt { get; set; }
    public DateTimeOffset? HostStartedAt { get; set; }

    // A side down and back, counted from a day's transient and closed machine.* disagreements in
    // a report from before coverage, and from the part of a day before it was redeployed.
    public const string SideDownAndBack = "sideDownAndBack";
    public const string DaemonRestart = "daemonRestart";
    public const string HostRestart = "hostRestart";
    public const string ObserverResumed = "observerResumed";
}

public sealed class ShadowFailedQuestion
{
    public string Session { get; set; } = "";
    public List<string> Kinds { get; set; } = [];
    public DateTimeOffset Last { get; set; }
}

public sealed class ShadowShown
{
    public string? Status { get; set; }
    public DateTimeOffset? StatusAt { get; set; }
    public string? Response { get; set; }
    public DateTimeOffset? ResponseAt { get; set; }
}

public sealed class ShadowFailedState
{
    public string Session { get; set; } = "";
    public string Kind { get; set; } = "";
    public DateTimeOffset Last { get; set; }
}

// Turns each compared sample into events, remembering what the previous one showed. One per run;
// what has to outlast a restart of the comparison is in the day's ShadowCoverage.
internal sealed class ShadowCoverageTracker
{
    const int MaxSessions = 2000;
    // Longer than this since the last sample written, and the comparison was not running: it has
    // come back from a logoff, a reboot or being stopped.
    static readonly TimeSpan Gap = TimeSpan.FromMinutes(3);

    sealed class Asked
    {
        public HashSet<string> Daemon { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Host { get; } = new(StringComparer.Ordinal);
        public int Counted;
        // The question showing now, by its key: the first identity it was known by, which every
        // identity either side gives the same question maps to, so it keeps one key while either
        // side shows it, whichever showed it first.
        public string? Current;
        public Dictionary<string, string> Key { get; } = new(StringComparer.Ordinal);
    }

    const int MaxFailed = 5000;

    // A question.* mismatch, lasting its grace, on a session: the question showing there now failed.
    public void QuestionFailed(ShadowCoverage c, string session, string kind, DateTimeOffset now)
    {
        if (!asked.TryGetValue(session, out var a) || a.Current is null) { return; }
        var key = ShadowHash.Text(session + "\n" + a.Current);
        if (!c.FailedQuestions.TryGetValue(key, out var failed))
        {
            if (c.FailedQuestions.Count >= MaxFailed) { return; }
            c.FailedQuestions[key] = failed = new ShadowFailedQuestion { Session = session };
        }
        if (!failed.Kinds.Contains(kind)) { failed.Kinds.Add(kind); }
        failed.Last = now;
    }

    // What a report from before coverage, or the part of a day before coverage began, says about
    // sides going down and coming back.
    public static int SidesDownAndBack(ShadowDay day) =>
        day.Transient.Where(t => ShadowKinds.IsMachine(t.Key)).Sum(t => t.Value.Count) + day.Mismatches.Count(m => ShadowKinds.IsMachine(m.Kind) && !m.Open);

    ShadowView? lastDaemon;
    string? lastResume;
    bool first = true;
    readonly Dictionary<string, Asked> asked = new(StringComparer.Ordinal);
    readonly Dictionary<string, DateTimeOffset> usageCounted = new(StringComparer.Ordinal);
    readonly Dictionary<(string Agent, string Window), double> percents = [];

    // lastSample: the latest sample any earlier run wrote, to see a gap in the comparison itself.
    public void Record(ShadowCoverage c, ShadowComparison comparison, ShadowView daemon, ShadowView host, DateTimeOffset now, DateTimeOffset? lastSample)
    {
        if (!comparison.Compared) { return; }
        c.Since ??= now;
        c.StatesSince ??= now;
        if (first)
        {
            first = false;
            if (lastSample is { } last && now - last > Gap) { Machine(c, ShadowCoverage.ObserverResumed); }
        }
        if (daemon.StartedAt is { } ds)
        {
            if (c.DaemonStartedAt is { } was && was != ds) { Machine(c, ShadowCoverage.DaemonRestart); }
            c.DaemonStartedAt = ds;
        }
        if (host.StartedAt is { } hs)
        {
            if (c.HostStartedAt is { } was && was != hs) { Machine(c, ShadowCoverage.HostRestart); }
            c.HostStartedAt = hs;
        }

        var present = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in daemon.Sessions.Keys.Union(host.Sessions.Keys))
        {
            var hash = ShadowHash.Session(id);
            present.Add(hash);
            daemon.Sessions.TryGetValue(id, out var d);
            host.Sessions.TryGetValue(id, out var h);
            if (!c.Sessions.ContainsKey(hash) && c.Sessions.Count < MaxSessions) { c.Sessions[hash] = ShadowHash.Token((d ?? h)!.Agent); }
            if ((d?.Agents ?? 0) + (d?.Shells ?? 0) + (h?.Agents ?? 0) + (h?.Shells ?? 0) > 0 && !c.BackgroundSessions.Contains(hash) && c.BackgroundSessions.Count < MaxSessions)
            {
                c.BackgroundSessions.Add(hash);
            }
            // The daemon's marker caught half written says nothing either way.
            if (d?.QuestionUnknown != true) { Question(c, hash, d?.Question, h?.Question); }
            if (d is not null && h is not null) { Shown(c, hash, d, h, now); }
            if (d is not null && lastDaemon?.Sessions.GetValueOrDefault(id) is { } before) { Changes(c, before, d); }
        }
        foreach (var gone in asked.Keys.Where(k => !present.Contains(k)).ToList()) { asked.Remove(gone); }
        foreach (var gone in c.Shown.Keys.Where(k => !present.Contains(k)).ToList()) { c.Shown.Remove(gone); }

        if (comparison.ResumableCompared && daemon.Resumable is { } dr && host.Resumable is { } hr)
        {
            var key = ShadowHash.Text(string.Join('\u001f', dr) + "\u001e" + string.Join('\u001f', hr));
            if (key != lastResume) { c.ResumeSnapshots++; lastResume = key; }
        }
        foreach (var client in comparison.UsageFresh)
        {
            if (host.Usage?.GetValueOrDefault(client)?.MeasuredAt is { } at && (!usageCounted.TryGetValue(client, out var was) || was != at))
            {
                c.UsageReadings++;
                usageCounted[client] = at;
            }
        }
        foreach (var (client, usage) in daemon.Usage ?? new Dictionary<string, ShadowUsage>())
        {
            if (!usage.Reading) { continue; }
            foreach (var (window, percent) in usage.Windows)
            {
                if (percents.TryGetValue((client, window), out var was) && percent < was - 1) { c.UsageResets++; }
                percents[(client, window)] = percent;
            }
        }
        lastDaemon = daemon;
    }

    static void Machine(ShadowCoverage c, string what) => c.MachineEvents[what] = c.MachineEvents.GetValueOrDefault(what) + 1;

    // One question per identity either side gave it, the larger count of the two: the daemon
    // knows a Copilot question by its hook's clock and the host by the tool call, so the same
    // question has a different identity on each side, but each side sees each question once.
    void Question(ShadowCoverage c, string session, ShadowQuestion? d, ShadowQuestion? h)
    {
        if (d is null && h is null) { asked.Remove(session); return; }
        if (!asked.TryGetValue(session, out var a)) { asked[session] = a = new Asked(); }
        if (d is not null) { a.Daemon.Add(Identity(d)); }
        if (h is not null) { a.Host.Add(Identity(h)); }
        string? dk = d is null ? null : Identity(d), hk = h is null ? null : Identity(h);
        // Linked only when they are the same question by the comparison's own rule: one side may
        // already show the next question while the other still shows the last.
        var same = d is not null && h is not null && ShadowCompare.SameQuestion(d, h);
        var key = (hk is not null ? a.Key.GetValueOrDefault(hk) : null) ?? (same || h is null ? a.Key.GetValueOrDefault(dk!) : null) ?? hk ?? dk!;
        if (hk is not null) { a.Key[hk] = key; }
        if (dk is not null && (same || h is null)) { a.Key[dk] = key; }
        a.Current = key;
        var count = Math.Max(a.Daemon.Count, a.Host.Count);
        if (count <= a.Counted) { return; }
        // Charged to the kind of the side that has shown more questions: the one now ahead.
        var kind = ShadowCompare.QuestionKind((a.Daemon.Count > a.Host.Count ? d ?? h : h ?? d)!);
        c.Questions[kind] = c.Questions.GetValueOrDefault(kind) + count - a.Counted;
        a.Counted = count;

        static string Identity(ShadowQuestion q) =>
            q.CallId ?? q.AskedAt?.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) ?? q.MessageHead ?? "";
    }

    // What a session showing on both sides is being compared in, counted when it is new: a status
    // or a response either side changed to, or the one it has when first seen. A status mismatch
    // or a response one begins only when one of these changes, so each is about an event counted.
    static void Shown(ShadowCoverage c, string session, ShadowSession d, ShadowSession h, DateTimeOffset now)
    {
        if (!c.Shown.TryGetValue(session, out var was))
        {
            if (c.Shown.Count >= MaxSessions) { return; }
            c.Shown[session] = was = new ShadowShown();
        }
        // As the comparison judged it, a marker caught half written included.
        var status = $"{ShadowHash.Token(ShadowCompare.ComparedDaemonStatus(d, h.Status))}/{ShadowHash.Token(h.Status)}";
        if (status != was.Status)
        {
            c.StatusTransitions++;
            was.Status = status;
            was.StatusAt = now;
        }
        // Compared only when both have one, so counted only then.
        if (d.ResponseTail is not null && h.ResponseTail is not null && ShadowHash.Text(d.ResponseTail + "\n" + h.ResponseTail) is var response && response != was.Response)
        {
            c.ResponseChanges++;
            was.Response = response;
            was.ResponseAt = now;
        }
    }

    readonly HashSet<string> overflowedStates = new(StringComparer.Ordinal);

    // A status or activity.response mismatch, lasting its grace, on a session: the state it is
    // being compared in now failed.
    public void StateFailed(ShadowCoverage c, string session, string kind, DateTimeOffset now)
    {
        if (!c.Shown.TryGetValue(session, out var shown)) { return; }
        var at = kind == ShadowKinds.Status ? shown.StatusAt : shown.ResponseAt;
        if (at is null) { return; }
        var key = ShadowHash.Text(session + "\n" + kind + "\n" + at.Value.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
        if (!c.FailedStates.TryGetValue(key, out var failed))
        {
            if (c.FailedStates.Count >= MaxFailed)
            {
                if (overflowedStates.Add(key)) { c.FailedStatesOverflow[kind] = c.FailedStatesOverflow.GetValueOrDefault(kind) + 1; }
                return;
            }
            c.FailedStates[key] = failed = new ShadowFailedState { Session = session, Kind = kind };
        }
        failed.Last = now;
    }

    // What changed in one session between two samples, as the daemon published it.
    static void Changes(ShadowCoverage c, ShadowSession before, ShadowSession now)
    {
        var (was, isNow) = (before.AgentIds.Concat(before.ShellIds.Select(s => "shell:" + s)).ToHashSet(StringComparer.Ordinal),
            now.AgentIds.Concat(now.ShellIds.Select(s => "shell:" + s)).ToHashSet(StringComparer.Ordinal));
        c.BackgroundStarts += isNow.Count(x => !was.Contains(x));
        c.BackgroundFinishes += was.Count(x => !isNow.Contains(x));
        if (now.Compactions > before.Compactions) { c.Compactions += now.Compactions - before.Compactions; }
        if (before.Model is not null && now.Model is not null && before.Model != now.Model) { c.ModelChanges++; }
    }
}

// The kinds of event covered, each with the mismatch kinds that are about it.
public static class ShadowCoverageKinds
{
    public const string Sessions = "sessions";
    public const string Questions = "questions";
    public const string Transitions = "statusTransitions";
    public const string Responses = "responses";
    public const string Background = "background";
    public const string Compactions = "compactions";
    public const string Models = "modelChanges";
    public const string Resume = "resumeSnapshots";
    public const string Usage = "usageReadings";
    public const string Resets = "usageResets";
    public const string Machine = "machineEvents";

    public static readonly IReadOnlyList<string> All = [Sessions, Questions, Transitions, Responses, Background, Compactions, Models, Resume, Usage, Resets, Machine];

    // Which coverage kind a mismatch counts against. Usage resets have no mismatch of their own:
    // one would show as a usage.percent, which counts against the readings.
    public static string? Of(string mismatchKind) => mismatchKind switch
    {
        ShadowKinds.MissingOnHost or ShadowKinds.MissingOnDaemon or ShadowKinds.Agent => Sessions,
        ShadowKinds.Status => Transitions,
        ShadowKinds.Response => Responses,
        ShadowKinds.BackgroundAgents or ShadowKinds.BackgroundShells => Background,
        ShadowKinds.Compactions => Compactions,
        ShadowKinds.Model => Models,
        _ when mismatchKind.StartsWith("question.", StringComparison.Ordinal) => Questions,
        _ when mismatchKind.StartsWith("resumable.", StringComparison.Ordinal) => Resume,
        _ when mismatchKind.StartsWith("usage.", StringComparison.Ordinal) => Usage,
        _ when ShadowKinds.IsMachine(mismatchKind) => Machine,
        _ => null,
    };

    // Counted from every report, the ones written before events were counted included: those
    // already list the sessions compared, and a machine side going down and coming back is a
    // closed machine.daemon or machine.host disagreement in them.
    public static bool FromEveryReport(string kind) => kind is Sessions or Machine;

    // Counted by the states compared, and their failures by state (ShadowCoverage.FailedStates).
    public static bool IsState(string kind) => kind is Transitions or Responses;
}

public static class ShadowStats
{
    // The 95% upper bound on a rate, one-sided, from m events of n: the rule of three (3/n) with
    // none, otherwise the exact Clopper-Pearson bound, the p at which seeing m or fewer has a 5%
    // chance. Null with nothing observed.
    public static double? UpperBound(int m, int n)
    {
        if (n <= 0) { return null; }
        if (m <= 0) { return Math.Min(1, 3.0 / n); }
        if (m >= n) { return 1; }
        double lo = (double)m / n, hi = 1;
        for (var i = 0; i < 200 && hi - lo > 1e-12; i++)
        {
            var mid = (lo + hi) / 2;
            if (AtMost(m, n, mid) > 0.05) { lo = mid; } else { hi = mid; }
        }
        return hi;
    }

    // P(X <= m) for X ~ Binomial(n, p), summed in logs so a large n neither underflows nor overflows.
    static double AtMost(int m, int n, double p)
    {
        if (p <= 0) { return 1; }
        if (p >= 1) { return m >= n ? 1 : 0; }
        var log = n * double.LogP1(-p);
        var ratio = Math.Log(p) - double.LogP1(-p);
        var max = log;
        var terms = new double[m + 1];
        terms[0] = log;
        for (var k = 1; k <= m; k++)
        {
            log += Math.Log(n - k + 1) - Math.Log(k) + ratio;
            terms[k] = log;
            max = Math.Max(max, log);
        }
        var sum = terms.Sum(t => Math.Exp(t - max));
        return Math.Min(1, Math.Exp(max + Math.Log(sum)));
    }
}

// What the gate asks for, from <profile>/shadow/gate.json when there is one (docs/host-shadow.md):
// anything left out takes its default. The file is the operator's, never written by the host.
public sealed class ShadowGateConfig
{
    public const string FileName = "gate.json";

    public int Questions { get; set; } = 100;
    public double QuestionsMaxRate { get; set; } = 0.03;
    public int Sessions { get; set; } = 30;
    public int StatusTransitions { get; set; } = 500;
    public int Background { get; set; } = 50;
    public int Compactions { get; set; } = 5;
    public int ResumeSnapshots { get; set; } = 100;
    public int UsageReadings { get; set; } = 50;
    public int MachineEvents { get; set; } = 1;
    // Wall-clock hours since the first sample, and hours actually compared.
    public double Hours { get; set; } = 24;
    public double ComparedHours { get; set; } = 8;
    // How far back no unexplained mismatch may be.
    public double CleanHours { get; set; } = 24;
    // An agent with fewer sessions than this is reported as not covered, not failed.
    public int AgentSessions { get; set; } = 5;
    // Mismatches investigated and documented as known differences (docs/host-shadow.md), each by
    // kind and, optionally, the one session it was seen on.
    public List<ShadowExplanation> Explained { get; set; } = [];

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public static (ShadowGateConfig Config, string? Problem) Read(string folder)
    {
        var path = Path.Combine(folder, FileName);
        if (!File.Exists(path)) { return (new(), null); }
        try { return (JsonSerializer.Deserialize<ShadowGateConfig>(File.ReadAllText(path), Json) ?? new(), null); }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return (new(), $"{FileName} could not be read ({e.GetType().Name}); the defaults are used");
        }
    }

    public ShadowExplanation? Explaining(ShadowEpisode m) =>
        Explained.FirstOrDefault(x => x.Kind == m.Kind && (x.Session is null || x.Session == m.Session));
}

public sealed class ShadowExplanation
{
    public string Kind { get; set; } = "";
    public string? Session { get; set; }
    [JsonPropertyName("reason")]
    public string? Reason { get; set; }
}
