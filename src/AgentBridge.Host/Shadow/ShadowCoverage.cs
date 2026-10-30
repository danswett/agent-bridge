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
    // A session's status changing (working, idle, waiting, ending) as the daemon published it.
    public int StatusTransitions { get; set; }
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

    public const string DaemonRestart = "daemonRestart";
    public const string HostRestart = "hostRestart";
    public const string ObserverResumed = "observerResumed";
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
    }

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
            if (d is not null && lastDaemon?.Sessions.GetValueOrDefault(id) is { } before) { Changes(c, before, d); }
        }
        foreach (var gone in asked.Keys.Where(k => !present.Contains(k)).ToList()) { asked.Remove(gone); }

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
        var count = Math.Max(a.Daemon.Count, a.Host.Count);
        if (count <= a.Counted) { return; }
        var kind = ShadowCompare.QuestionKind((h ?? d)!);
        c.Questions[kind] = c.Questions.GetValueOrDefault(kind) + count - a.Counted;
        a.Counted = count;

        static string Identity(ShadowQuestion q) =>
            q.CallId ?? q.AskedAt?.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) ?? q.MessageHead ?? "";
    }

    // What changed in one session between two samples, as the daemon published it.
    static void Changes(ShadowCoverage c, ShadowSession before, ShadowSession now)
    {
        if (ShadowCompare.Comparable(before.Status) != ShadowCompare.Comparable(now.Status)) { c.StatusTransitions++; }
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
    public const string Background = "background";
    public const string Compactions = "compactions";
    public const string Models = "modelChanges";
    public const string Resume = "resumeSnapshots";
    public const string Usage = "usageReadings";
    public const string Resets = "usageResets";
    public const string Machine = "machineEvents";

    public static readonly IReadOnlyList<string> All = [Sessions, Questions, Transitions, Background, Compactions, Models, Resume, Usage, Resets, Machine];

    // Which coverage kind a mismatch counts against. Usage resets have no mismatch of their own:
    // one would show as a usage.percent, which counts against the readings.
    public static string? Of(string mismatchKind) => mismatchKind switch
    {
        ShadowKinds.MissingOnHost or ShadowKinds.MissingOnDaemon or ShadowKinds.Agent => Sessions,
        ShadowKinds.Status or ShadowKinds.Response => Transitions,
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
