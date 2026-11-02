using System.Text;
using System.Text.Json;
using AgentBridge.Protocol;

namespace AgentBridge.Host.Core.Usage;

// Codex's two rate-limit windows, from Codex itself (#200) and from its own transcripts.
//
// Codex's app-server is asked for a reading (CodexUsageProbe) once the newest one has gone stale,
// at the pace ProbePace sets. Between those, and wherever no probe can run (codex missing, an API
// key login, a session running without the daemon), the transcripts stand: Codex learns its limits
// from the headers on its replies and records them in the token_count events it writes as it goes,
// so that is the newest figure Codex itself saw, ageing between runs. The newer of the two wins.
public sealed class CodexUsageCollector(UsageOptions options, TimeProvider? time = null, UsageProbes? probes = null, Action<string>? log = null) : IUsageCollector
{
    readonly TimeProvider clock = time ?? TimeProvider.System;
    readonly Action<string> say = log ?? (_ => { });
    readonly ProbePace pace = new(options);
    readonly object gate = new();
    AgentUsage? probed;
    DateTimeOffset? signedOutAt;
    bool sessionOpen, busy, wasOpen;
    Task? running;
    Task? answering;
    bool finished;
    DateTimeOffset? nextDue;
    string? said;

    public AgentKind Agent => AgentKind.Codex;

    public void Pace(bool sessionOpen, bool busy)
    {
        lock (gate)
        {
            (this.sessionOpen, this.busy) = (sessionOpen, busy);
            // A Codex session running here again ends the hold a sign-out put on the probes: it
            // has signed in, or it will say so itself.
            if (sessionOpen && !wasOpen && signedOutAt is not null)
            {
                signedOutAt = null;
                pace.Release();
                nextDue = null;
            }
            wasOpen = sessionOpen;
        }
    }

    public DateTimeOffset? WakeAt
    {
        get
        {
            lock (gate) { return finished ? DateTimeOffset.MinValue : running is null ? nextDue : null; }
        }
    }

    // The probe's run, while one is under way, for tests to wait on.
    internal Task Running { get { lock (gate) { return running ?? Task.CompletedTask; } } }

    // A probe still waiting for its answers: its standard input goes with the host.
    public Task StoppingAsync()
    {
        lock (gate) { return answering ?? Task.CompletedTask; }
    }

    public Task<AgentUsage?> CollectAsync(CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();
        var transcript = Read(cancellationToken);
        AgentUsage? newest;
        lock (gate)
        {
            finished = false;
            newest = Newer(transcript, probed);
        }
        if (probes is not null) { Consider(newest?.MeasuredAt, now); }
        lock (gate)
        {
            newest = Newer(transcript, probed);
            if (signedOutAt is { } at)
            {
                return Task.FromResult<AgentUsage?>(newest is not null ? newest with { Error = CodexUsageProbe.SignedOutError }
                    : UsageRead.Failure(AgentKind.Codex, CodexUsageProbe.SignedOutError, at));
            }
        }
        return Task.FromResult(newest);
    }

    // Of two readings, the one measured later: a session's transcript can be newer than the last
    // probe, and a probe newer than any transcript.
    static AgentUsage? Newer(AgentUsage? a, AgentUsage? b) => a is null ? b : b is null ? a : b.MeasuredAt > a.MeasuredAt ? b : a;

    void Consider(DateTimeOffset? readingAt, DateTimeOffset now)
    {
        lock (gate)
        {
            nextDue = null;
            if (running is not null) { return; }
            if (!pace.Due(readingAt, busy, now))
            {
                nextDue = pace.DueAt(readingAt, busy);
                return;
            }
            // Codex has been used here: a machine where it is installed and never run says nothing.
            if (!Directory.Exists(Path.Combine(options.CodexHome, "sessions"))) { return; }
            pace.Started(now);
            var run = new CodexUsageProbe(options, probes!, clock, say).Start();
            answering = run.Reported;
            running = Task.Run(async () =>
            {
                try
                {
                    var outcome = await run.Reported;
                    lock (gate) { answering = null; }
                    Apply(outcome);
                    await run.Finished;
                }
                catch (Exception e)
                {
                    say($"usage: asking Codex for its allowance failed: {e.GetType().Name}");
                    lock (gate) { pace.Failed(clock.GetUtcNow()); }
                }
                finally
                {
                    lock (gate)
                    {
                        running = null;
                        answering = null;
                        finished = true;
                    }
                }
            });
        }
    }

    void Apply(CodexProbeOutcome outcome)
    {
        var now = clock.GetUtcNow();
        string? line;
        lock (gate)
        {
            if (outcome.Reading is { } r && (probed is null || r.MeasuredAt > probed.MeasuredAt)) { probed = r; }
            switch (outcome.Kind)
            {
                case CodexProbeKind.Reading:
                    pace.Succeeded();
                    signedOutAt = null;
                    line = null;
                    break;
                case CodexProbeKind.SignedOut:
                    // No more probes until the longest back-off ends or a Codex session runs here.
                    signedOutAt ??= now;
                    pace.Hold(now);
                    line = $"usage: {CodexUsageProbe.SignedOutError} ({outcome.Why}); not asking it again for {options.ProbeBackoffMax.TotalHours:0} hours or until a Codex session runs here";
                    break;
                case CodexProbeKind.NotApplicable:
                    pace.Hold(now);
                    line = $"usage: Codex is not asked for its allowance ({outcome.Why}); its transcripts are read";
                    break;
                case CodexProbeKind.Skipped:
                    line = $"usage: Codex was not asked for its allowance ({outcome.Why}); its transcripts are read";
                    break;
                default:
                    var wait = pace.Failed(now);
                    line = $"usage: asking Codex for its allowance failed ({outcome.Why}); trying again in {wait.TotalMinutes:0} minutes";
                    break;
            }
            finished = true;
            if (line == said) { return; }
            said = line;
        }
        if (line is not null) { say(line); }
    }

    AgentUsage? Read(CancellationToken cancellationToken)
    {
        var root = Path.Combine(options.CodexHome, "sessions");
        if (!Directory.Exists(root)) { return null; }
        // Newest first, and only a handful: a session that ended without being told its limits
        // has no such event, so the search walks back a little, but not through years.
        List<FileInfo> files;
        try
        {
            files = [.. new DirectoryInfo(root).EnumerateFiles("*.jsonl", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                .OrderByDescending(f => f.LastWriteTimeUtc).Take(options.CodexFiles)];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        // Each candidate's latest reading, and the newest of those: with several sessions, the
        // file written last need not be the one that heard of the limits last.
        AgentUsage? newest = null;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string[] lines;
            try
            {
                using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                // A transcript is conversation and tool output, so its size says nothing about
                // whether its latest limits are worth reading: a long one is read from its
                // tail, the first (partial) line of which is dropped.
                var partial = stream.Length > options.MaxStateBytes;
                if (partial) { stream.Seek(-options.MaxStateBytes, SeekOrigin.End); }
                using var reader = new StreamReader(stream, Encoding.UTF8);
                lines = reader.ReadToEnd().Split('\n');
                if (partial && lines.Length > 0) { lines[0] = ""; }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            for (var i = lines.Length - 1; i >= 0; i--)
            {
                if (!lines[i].Contains("\"rate_limits\"", StringComparison.Ordinal)) { continue; }
                if (Parse(lines[i], file.LastWriteTimeUtc) is not { } usage) { continue; }
                if (newest is null || usage.MeasuredAt > newest.MeasuredAt) { newest = usage; }
                break;
            }
        }
        return newest;
    }

    // A window named by its length, as the transcripts and the app-server both give it.
    internal static string WindowLabel(double? minutes, string fallback) => minutes is { } m and > 0
        ? m >= 10080 ? "Weekly" : m >= 1440 ? $"{Math.Round(m / 1440)}d limit" : $"{Math.Round(m / 60)}h limit"
        : fallback;

    // Without a timestamp on the event, the file's last write is the latest it can have been.
    internal static AgentUsage? Parse(string line, DateTimeOffset written)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(line); }
        catch (JsonException) { return null; }
        using (document)
        {
            var root = document.RootElement;
            if (UsageRead.Property(root, "payload") is not { } payload || UsageRead.Property(payload, "rate_limits") is not { ValueKind: JsonValueKind.Object } limits) { return null; }
            var windows = new List<UsageWindow>();
            foreach (var (name, fallback) in new[] { ("primary", "5h limit"), ("secondary", "Weekly") })
            {
                if (UsageRead.Property(limits, name) is not { } limit || UsageRead.Number(limit, "used_percent") is not { } percent) { continue; }
                var label = WindowLabel(UsageRead.Number(limit, "window_minutes"), fallback);
                DateTimeOffset? resets = UsageRead.Number(limit, "resets_at") is { } seconds and > 0 ? DateTimeOffset.FromUnixTimeSeconds((long)seconds) : null;
                // No start: Codex says when a window resets but not when it began, and guessing it
                // from the length would quietly turn a rolling window into a fixed one.
                windows.Add(UsageRead.Window(name, label, percent, resets));
            }
            if (windows.Count == 0) { return null; }
            return new AgentUsage
            {
                Agent = AgentKind.Codex,
                Label = UsageRead.Label(AgentKind.Codex),
                Plan = UsageRead.String(limits, "plan_type"),
                Source = UsageSource.Transcript,
                Windows = windows,
                MeasuredAt = UsageRead.Instant(root, "timestamp") ?? written,
            };
        }
    }
}
