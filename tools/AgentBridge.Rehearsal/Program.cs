using System.Text;
using System.Text.Json;
using AgentBridge.Host.Delivery.Windows;
using AgentBridge.Local;
using AgentBridge.Transport;

namespace AgentBridge.Rehearsal;

internal static class Program
{
    public static async Task<int> Main(string[] argv)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        if (argv.Length == 0 || argv[0] is "-h" or "--help" or "help")
        {
            Console.WriteLine(Cli.Help());
            return argv.Length == 0 ? 2 : 0;
        }
        Parsed p;
        try { p = Cli.Parse(argv); }
        catch (UsageException e)
        {
            Console.Error.WriteLine(e.Message);
            return 2;
        }
        // Checked before the step runs, so a mistyped id never leaves a check unrecorded.
        if (p.Option("--step") is { } named && !Evidence.ValidStep(named))
        {
            Console.Error.WriteLine("--step is letters, digits and '-' only, up to 40 characters");
            return 2;
        }
        var evidence = new Evidence(p.Option("--evidence") ?? Environment.GetEnvironmentVariable("AGENT_BRIDGE_REHEARSAL_EVIDENCE") ?? "rehearsal-evidence");
        Outcome outcome;
        try { outcome = await RunAsync(p, evidence, CancellationToken.None); }
        catch (UsageException e)
        {
            Console.Error.WriteLine(e.Message);
            return 2;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or HttpRequestException or HostErrorException or System.Net.WebSockets.WebSocketException or TaskCanceledException)
        {
            outcome = Outcome.Error(e.Message);
        }
        if (outcome.Verdict == Verdict.Pass && (p.Name == "newest" || (p.Name == "sessions" && p.Flag("--id-only"))))
        {
            // Only the id, so `$cp = agent-bridge-rehearsal …` sets a variable.
            Console.WriteLine(outcome.Detail);
        }
        else
        {
            Console.WriteLine($"{outcome.Verdict.ToString().ToUpperInvariant()} {outcome.Detail}");
            if (outcome.Data is not null) { Console.WriteLine(JsonSerializer.Serialize(outcome.Data, Evidence.Json)); }
        }
        if (p.Option("--step") is { } step) { evidence.Record(step, p.Name, argv, outcome, DateTimeOffset.UtcNow); }
        return outcome.ExitCode;
    }

    internal static async Task<Outcome> RunAsync(Parsed p, Evidence evidence, CancellationToken ct)
    {
        var homes = Homes.For(p.Option("--home"));
        var profile = p.Option("--profile") ?? "default";
        if (!Identity.IsValidProfile(profile)) { throw new UsageException("--profile is a name, not a path"); }
        // Never Identity.Load: that creates a profile and a key, and a check must change nothing.
        var profileFolder = Path.Combine(p.Option("--home") is { } h
            ? (OperatingSystem.IsWindows() ? Path.Combine(Path.GetFullPath(h), "AppData", "Local", "AgentBridge") : Path.Combine(Path.GetFullPath(h), ".agent-bridge"))
            : Identity.DefaultRoot, profile);

        switch (p.Name)
        {
            case "baseline save":
                {
                    var baseline = BaselineCapture.Capture(homes, new ScheduledTasks(), DateTimeOffset.UtcNow);
                    BaselineCapture.Save(baseline, p.Option("--out")!);
                    return Outcome.Pass($"{baseline.Files.Count} file(s) and {baseline.Tasks.Count} task(s) hashed into {Path.GetFullPath(p.Option("--out")!)}",
                        new { files = baseline.Files.GroupBy(f => f.Category).ToDictionary(g => g.Key, g => g.Count()), tasks = baseline.Tasks.Select(t => new { t.Name, t.Enabled, t.State }) });
                }
            case "baseline compare":
                {
                    var before = BaselineCapture.Load(p.Option("--baseline")!);
                    var now = BaselineCapture.Capture(homes, new ScheduledTasks(), DateTimeOffset.UtcNow);
                    var allowed = p.All("--allow").ToHashSet(StringComparer.Ordinal);
                    if (allowed.FirstOrDefault(a => !BaselineCapture.Categories.Contains(a)) is { } bad) { throw new UsageException($"--allow takes {string.Join(", ", BaselineCapture.Categories)}, not {bad}"); }
                    return BaselineCapture.Judge(before, BaselineCapture.Compare(before, now, p.All("--ignore")), allowed);
                }
            case "g5":
                {
                    if (!OperatingSystem.IsWindows()) { return Outcome.Error("the agent-ha-bridge probe exists on Windows only"); }
                    var want = p.Option("--expect");
                    if (want is not (null or "active" or "inactive")) { throw new UsageException($"--expect is active or inactive, not {want}"); }
                    var until = p.Option("--timeout") is null ? DateTimeOffset.MinValue : DateTimeOffset.UtcNow + p.Timeout(0);
                    while (true)
                    {
                        var active = new AgentHaBridgeProbe().Active();
                        var data = new { active, tasks = new ScheduledTasks().Read() };
                        var outcome = want switch
                        {
                            "active" => active is not null ? Outcome.Pass($"active: {active}", data) : Outcome.Fail("agent-ha-bridge is not running for this user", data),
                            "inactive" => active is null ? Outcome.Pass("agent-ha-bridge is not running for this user", data) : Outcome.Fail(active, data),
                            _ => Outcome.Pass(active ?? "agent-ha-bridge is not running for this user", data),
                        };
                        if (outcome.Verdict == Verdict.Pass || DateTimeOffset.UtcNow >= until) { return outcome; }
                        await Task.Delay(TimeSpan.FromSeconds(2), ct);
                    }
                }
            case "host": return await HostChecks.HostAsync(profileFolder, p, ct);
            case "sessions": return await HostChecks.SessionsAsync(profileFolder, p, ct);
            case "mark":
                {
                    var id = p.Args[0];
                    if (Transcripts.Find(homes, id) is not { } t) { return Outcome.Error($"no Copilot, Claude or Codex transcript for {id} under {homes.Home}"); }
                    var at = DateTimeOffset.UtcNow;
                    evidence.SaveMark(id, new Mark(t.Kind, t.Path, new FileInfo(t.Path).Length, at));
                    var log = await HostChecks.LogPathAsync(profileFolder, ct);
                    if (log is not null && File.Exists(log)) { evidence.SaveMark("host-log", new Mark("log", log, new FileInfo(log).Length, at)); }
                    return Outcome.Pass($"marked {t.Kind} session {id} at byte {new FileInfo(t.Path).Length}{(log is null ? "; no host log found" : "")}");
                }
            case "newest":
                {
                    var agent = p.Args[0].ToLowerInvariant();
                    return Transcripts.Newest(homes, agent) is { } n
                        ? Outcome.Pass(n.Id)
                        : Outcome.Error($"no {agent} transcript under {homes.Home}");
                }
            case "expect answer" or "expect reply" or "expect steering" or "expect permission" or "expect shutdown":
                {
                    var id = p.Args[0];
                    if (!evidence.Marks().TryGetValue(id, out var mark)) { throw new UsageException($"mark {id} first, before the step, so only what the step produced is read"); }
                    if (!File.Exists(mark.Path)) { return Outcome.Error($"the transcript at {mark.Path} has gone"); }
                    Func<Match> check = p.Name switch
                    {
                        "expect answer" => () => Transcripts.Answer(mark.Kind, mark.Path, mark.Offset, p.Option("--text"), Fields(p)),
                        "expect reply" => () => Transcripts.Reply(mark.Kind, mark.Path, mark.Offset, p.Option("--text")!, p.Int("--attachments") ?? 0, steering: false),
                        "expect steering" => () => Transcripts.Reply(mark.Kind, mark.Path, mark.Offset, p.Option("--text")!, 0, steering: true),
                        "expect permission" => () => Transcripts.Permission(mark.Path, mark.Offset, p.Option("--decision") is "approved" or "denied" ? p.Option("--decision")! : throw new UsageException("--decision is approved or denied")),
                        _ => () => Transcripts.Shutdown(mark.Kind, mark.Path, mark.Offset),
                    };
                    if (p.Name == "expect permission" && mark.Kind != Transcripts.Copilot) { return Outcome.Manual($"{mark.Kind} permission prompts are answered at the machine"); }
                    var found = await PollAsync(check, p.Timeout(p.Name == "expect reply" || p.Name == "expect steering" ? 90 : 30), ct);
                    if (found.Verdict != Verdict.Pass || !p.Flag("--expect-once")) { return found; }
                    // Typed once: wait for a late second typing to land, then count.
                    await Task.Delay(TimeSpan.FromSeconds(p.Int("--settle") ?? 15), ct);
                    var (answers, strays) = Transcripts.AnswerCount(mark.Kind, mark.Path, mark.Offset, p.Option("--text"));
                    var counted = new { answers, strays };
                    if (answers != 1) { return Outcome.Fail($"{answers} answers recorded since the mark, expected exactly one", counted); }
                    if (strays > 0) { return Outcome.Fail($"the answer's words were also taken as {strays} prompt(s), as a second typing would be", counted); }
                    return Outcome.Pass($"{found.Detail}, exactly once", counted);
                }
            case "log":
                {
                    if (!evidence.Marks().TryGetValue("host-log", out var mark)) { throw new UsageException("mark a session first: that also marks the host log"); }
                    var text = p.Option("--contains")!;
                    return await PollAsync(() => LogLine(mark, text), p.Timeout(15), ct);
                }
            case "ha":
                {
                    var url = new Uri(p.Option("--url") ?? "http://127.0.0.1:8123");
                    if (HaCheck.Refusal(url) is { } refused) { return Outcome.Error(refused); }
                    var token = File.ReadAllText(p.Option("--token-file")!).Trim();
                    if (token.Length == 0) { return Outcome.Error("the token file is empty"); }
                    // A session's entities, named by the connector's own contract, so a check
                    // never has to work out the node id by hand.
                    static IEnumerable<string> SessionEntities(IEnumerable<string> ids) =>
                        ids.SelectMany(id => AgentBridge.HomeAssistant.HaContract.SessionSensorIds(AgentBridge.HomeAssistant.HaContract.NodeId(id)).Values);
                    var expect = new HaExpectations(p.Option("--slug"), p.Flag("--expect-online"), p.Flag("--expect-offline"), p.Int("--expect-sessions"), p.Flag("--expect-no-duplicates"),
                        [.. p.All("--expect-entity"), .. SessionEntities(p.All("--expect-session"))],
                        [.. p.All("--expect-no-entity"), .. SessionEntities(p.All("--expect-no-session"))],
                        Pairs(p.All("--expect-state"), "--expect-state"));
                    var registry = expect.NoDuplicates || expect.NoEntities.Count > 0;
                    return await Poll.UntilAsync("Home Assistant", async c =>
                    {
                        var (states, entities) = await HaCheck.ReadAsync(url, token, registry, c);
                        return HaCheck.Evaluate(states, entities, expect);
                    }, Poll.Deadline(p), TimeSpan.FromSeconds(5), ct);
                }
            case "staging": return Disk.Staging(profileFolder, p.Int("--expect-min-files"));
            case "worktrees": return Disk.Worktrees(Path.GetFullPath(p.Args[0]), p.Int("--expect-host-trees"), p.Int("--expect-max-host-trees"), p.Int("--expect-old-trees"));
            case "note":
                {
                    var step = p.Option("--step")!;
                    var detail = p.Option("--detail")!;
                    string? screen = null;
                    if (p.Option("--screenshot") is { } shot)
                    {
                        if (!File.Exists(shot)) { return Outcome.Error($"{shot} does not exist"); }
                        screen = evidence.SaveScreenshot(step, shot);
                    }
                    var data = screen is null ? null : new { screenshot = screen };
                    return p.Option("--result") switch
                    {
                        "pass" => Outcome.Pass(detail, data),
                        "fail" => Outcome.Fail(detail, data),
                        "manual" => Outcome.Manual(detail, data),
                        var other => throw new UsageException($"--result is pass, fail or manual, not {other}"),
                    };
                }
            case "report":
                {
                    var steps = p.Option("--checklist") is { } doc ? Checklist.ExpectedSteps(File.ReadAllText(doc)) : null;
                    Console.WriteLine(evidence.Report(Evidence.Pacific(), steps));
                    return Outcome.Pass($"evidence in {evidence.Folder}");
                }
            default: throw new UsageException($"unknown command {p.Name}");
        }
    }

    static IReadOnlyDictionary<string, string> Fields(Parsed p) => Pairs(p.All("--field"), "--field");

    static Dictionary<string, string> Pairs(IReadOnlyList<string> values, string option)
    {
        var pairs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var v in values)
        {
            var at = v.IndexOf('=');
            if (at <= 0) { throw new UsageException($"{option} is name=value"); }
            pairs[v[..at]] = v[(at + 1)..];
        }
        return pairs;
    }

    static Match LogLine(Mark mark, string text)
    {
        if (!File.Exists(mark.Path)) { return Match.Waiting; }
        using var stream = new FileStream(mark.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        // The log rolls at 2 MiB; a file shorter than the mark has rolled, so read it whole.
        stream.Seek(stream.Length >= mark.Offset ? mark.Offset : 0, SeekOrigin.Begin);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (reader.ReadLine() is { } line)
        {
            if (line.Contains(text, StringComparison.Ordinal)) { return Match.Yes("the host logged it", new { line }); }
        }
        return Match.Waiting;
    }

    internal static async Task<Outcome> PollAsync(Func<Match> check, TimeSpan timeout, CancellationToken ct, TimeSpan? every = null)
    {
        var until = DateTimeOffset.UtcNow + timeout;
        while (true)
        {
            var m = check();
            if (m.Found) { return Outcome.Pass(m.Detail, m.Data); }
            if (m.Final) { return m.Detail.Contains("at the machine", StringComparison.Ordinal) || m.Detail.Contains("writes no", StringComparison.Ordinal) ? Outcome.Manual(m.Detail, m.Data) : Outcome.Fail(m.Detail, m.Data); }
            if (DateTimeOffset.UtcNow >= until) { return Outcome.Fail($"nothing recorded within {timeout.TotalSeconds:0} s", m.Data); }
            await Task.Delay(every ?? TimeSpan.FromMilliseconds(500), ct);
        }
    }
}
