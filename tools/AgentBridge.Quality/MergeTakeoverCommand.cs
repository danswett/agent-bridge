using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentBridge.Quality;

/// <summary>GitHub's status page, so tests can stand in for it.</summary>
interface IStatusPage
{
    /// <summary>The Actions component's status (operational, degraded_performance, partial_outage or major_outage), or null when it can't be read.</summary>
    string? ActionsStatus();
}

/// <summary>https://www.githubstatus.com/api/v2/summary.json, read with a short timeout.</summary>
sealed class GitHubStatusPage : IStatusPage
{
    public static readonly GitHubStatusPage Instance = new();
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public string? ActionsStatus()
    {
        try
        {
            using var doc = JsonDocument.Parse(Http.GetStringAsync("https://www.githubstatus.com/api/v2/summary.json").GetAwaiter().GetResult());
            return MergeTakeoverCommand.ActionsStatusIn(doc.RootElement);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return null;
        }
    }
}

/// <summary>What merge-takeover remembers between ticks (quality/README.md §4.9).</summary>
sealed class TakeoverState
{
    public bool InTakeover { get; set; }
    public DateTimeOffset? EnteredAt { get; set; }
    public DateTimeOffset? ExitedAt { get; set; }
    /// <summary>The first of the healthy ticks in a row that will end a takeover; null after an unhealthy one.</summary>
    public DateTimeOffset? FirstHealthyAt { get; set; }
    public string? EnterEvidence { get; set; }
    public long? RulesetId { get; set; }
    public string? BackupPath { get; set; }
    public bool CapNoticePosted { get; set; }
    /// <summary>Set when handing back couldn't be verified: nothing more happens until the ruleset is clean again.</summary>
    public bool NeedsOwner { get; set; }
    public List<MergedPullRequest> Merged { get; set; } = [];
    /// <summary>Receipts for #170 that couldn't be posted yet, oldest first.</summary>
    public List<string> PendingComments { get; set; } = [];
}

sealed record MergedPullRequest(int Number, string Sha, DateTimeOffset At);

/// <summary>One tick's evidence about GitHub Actions: the status page and the merge-rule workflow's own runs.</summary>
sealed record TakeoverObservation(string? ActionsStatus, TimeSpan? Stall, bool Recovered, string Runs)
{
    public string StatusWords => ActionsStatus ?? "unknown";
}

/// <summary>
/// <c>merge-takeover</c>: while GitHub Actions is down, merge-rule can't be posted and auto-merge
/// stalls, so Quality takes over merging under main's own evaluator (as on 2026-11-04, #170) and
/// hands back when Actions recovers. One run is one tick of the Quality session's loop.
/// </summary>
static class MergeTakeoverCommand
{
    internal const string Usage = """
          merge-takeover [--slug OWNER/REPO] [--state FILE] [--config FILE] [--dry-run] [--allow-non-main]
              One tick of Quality's takeover of merging while GitHub Actions is down (quality/README.md
              §4.9). Reads GitHub's status page and the merge-rule workflow's runs; enters when
              Actions isn't operational AND merge-rule has stalled (a run queued, or shown in
              progress with every job done, for 15 min), by adding a pull-request-only admin
              bypass to main's ruleset and posting a receipt on #170; then merges at most one
              armed, unheld pull request a tick that passes main's evaluator, with quality-gate
              green on its head, 0 unresolved threads and MERGEABLE (--admin --match-head-commit);
              exits when Actions is operational and merge-rule succeeds again on two ticks 4+ min
              apart, removing the bypass and checking the ruleset against quality/ruleset-main.json.
              State lives in --state (default %LOCALAPPDATA%\agent-bridge\merge-takeover.json, or
              $XDG_STATE_HOME/agent-bridge/ elsewhere). --dry-run prints and changes nothing.
              Refuses, like merge-rule, unless HEAD is origin/main's with no local changes.
              Exits 1 when the ruleset has drifted or couldn't be verified.
        """;

    const int Issue = 170;
    const string Workflow = "merge-rule.yml";
    const string GateCheck = "quality-gate";
    const string Owner = "danswett";
    static readonly TimeSpan StallAfter = TimeSpan.FromMinutes(15);
    static readonly TimeSpan RecentSuccess = TimeSpan.FromMinutes(10);
    static readonly TimeSpan QueuedWhileRecovered = TimeSpan.FromMinutes(5);
    static readonly TimeSpan HealthyApart = TimeSpan.FromMinutes(4);
    static readonly TimeSpan NoReentryFor = TimeSpan.FromMinutes(15);
    static readonly TimeSpan ReentryAnywayAfter = TimeSpan.FromMinutes(30);
    static readonly TimeSpan OwnerConfirmsAfter = TimeSpan.FromHours(12);

    // Repository role 5 is admin (GitHub's rulesets API), and pull_request mode lets it merge a
    // pull request past the rules but not push to main: what Quality added by hand on 2026-11-04.
    internal static readonly JsonObject AdminBypass = new() { ["actor_id"] = 5, ["actor_type"] = "RepositoryRole", ["bypass_mode"] = "pull_request" };

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static int Run(IReadOnlyList<string> args, TextWriter output, IGitHub github, IStatusPage statusPage, DateTimeOffset now, Func<string, MergeRuleCommand.CheckoutFacts>? checkout = null)
    {
        var options = ParseOptions(args);
        var root = ChangedFiles.RepoRoot(Directory.GetCurrentDirectory());
        var configPath = options.GetValueOrDefault("config") ?? Path.Combine(root, "quality", "merge-rule.yml");
        var config = MergeRuleConfig.Load(configPath);
        if (!MergeRuleCommand.MainsEvaluator(output, "merge-takeover", config, configPath, options.ContainsKey("allow-non-main"), checkout))
            return 2;
        var expected = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "quality", "ruleset-main.json")))!.AsObject();
        var slug = options.GetValueOrDefault("slug") ?? "danswett/agent-bridge";
        var statePath = options.GetValueOrDefault("state") ?? DefaultStatePath();
        var dryRun = options.ContainsKey("dry-run");
        var state = LoadState(statePath);
        var rulesetProblem = false;
        var otherProblem = false;

        // A receipt that fails to post (GitHub's API is often shaky in the same incident) is kept
        // in the state and posted on a later tick, so #170 never misses a start or an end.
        void Post(string body)
        {
            state.PendingComments.Add(body);
            Flush();
        }
        void Flush()
        {
            while (state.PendingComments.Count > 0)
            {
                try
                {
                    Comment(github, slug, state.PendingComments[0]);
                }
                catch (Exception e) when (MergeRuleCommand.IsReadFailure(e))
                {
                    output.WriteLine($"Couldn't post a receipt on #{Issue} ({e.Message}); it's kept for the next tick.");
                    otherProblem = true;
                    break;
                }
                state.PendingComments.RemoveAt(0);
            }
            SaveState(statePath, state);
        }
        if (!dryRun && state.PendingComments.Count > 0)
            Flush();

        var observed = Observe(github, statusPage, slug, now);
        output.WriteLine($"Status page: Actions is {observed.StatusWords}.");
        output.WriteLine($"merge-rule runs: {observed.Runs}");
        output.WriteLine($"Stalled: {(observed.Stall is { } s ? $"yes, for {Minutes(s)}" : "no")}. Recovered: {(observed.Recovered ? "yes" : "no")}.");
        output.WriteLine(state.InTakeover
            ? $"State: in takeover since {FlakeReport.Pacific(state.EnteredAt!.Value)}, {state.Merged.Count} merged."
            : $"State: not in takeover{(state.ExitedAt is { } ended ? $", last ended {FlakeReport.Pacific(ended)}" : "")}.");

        // Handing back once failed verification: nothing happens until the owner has put the
        // ruleset right, since entering again would build on a ruleset no one has checked.
        if (state.NeedsOwner && !state.InTakeover)
        {
            var (_, live, problems) = ReadRuleset(github, slug, expected);
            if (problems.Count > 0 || Bypass(live).Count > 0)
            {
                output.WriteLine($"Ruleset still needs @{Owner}: {string.Join("; ", problems.Concat(Bypass(live).Count > 0 ? ["it has bypass actors"] : []))}. Doing nothing.");
                return 1;
            }
            output.WriteLine("The ruleset matches quality/ruleset-main.json with no bypass again.");
            state.NeedsOwner = false;
            if (!dryRun)
                SaveState(statePath, state);
        }

        var (decision, why) = Decide(state, observed, now);
        output.WriteLine($"Decision: {decision}: {why}.");
        if (dryRun)
            output.WriteLine("Dry run: nothing is changed, posted, merged or saved.");

        var merging = false;
        switch (decision)
        {
            case "enter":
                {
                    var (id, live, problems) = ReadRuleset(github, slug, expected);
                    if (Bypass(live).Count > 0)
                        problems.Add($"it already has {Bypass(live).Count} bypass actor(s)");
                    if (problems.Count > 0)
                    {
                        output.WriteLine($"Refusing to take over: ruleset {id?.ToString(CultureInfo.InvariantCulture) ?? "(not found)"} doesn't match quality/ruleset-main.json: {string.Join("; ", problems)}. Changing nothing; it needs @{Owner}.");
                        return 1;
                    }
                    var evidence = $"Actions is {observed.StatusWords} on GitHub's status page, and merge-rule has stalled for {Minutes(observed.Stall!.Value)} ({observed.Runs})";
                    if (dryRun)
                    {
                        output.WriteLine($"Would back up ruleset {id}, PUT it with the one admin pull-request bypass, verify it, and post the takeover receipt on #{Issue}.");
                        merging = true;
                        break;
                    }
                    var backup = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(statePath))!, $"merge-takeover-ruleset-{id}-{now.UtcDateTime:yyyyMMdd'T'HHmmss'Z'}.json");
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                    File.WriteAllText(backup, live!.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                    output.WriteLine($"Backed up ruleset {id} to {backup}.");
                    // Saved before the PUT, so a PUT that lands though gh reports a failure is
                    // still a takeover, and recovery removes its bypass.
                    var before = JsonSerializer.Serialize(state, Json);
                    state.InTakeover = true;
                    state.EnteredAt = now;
                    state.FirstHealthyAt = null;
                    state.EnterEvidence = evidence;
                    state.RulesetId = id;
                    state.BackupPath = backup;
                    state.CapNoticePosted = false;
                    state.NeedsOwner = false;
                    state.Merged = [];
                    SaveState(statePath, state);
                    try
                    {
                        github.Send("PUT", $"repos/{slug}/rulesets/{id}", PutBody(live, [AdminBypass.DeepClone()]).ToJsonString()).Dispose();
                    }
                    catch (Exception e) when (MergeRuleCommand.IsReadFailure(e))
                    {
                        output.WriteLine($"The PUT to ruleset {id} failed: {e.Message}");
                        if (ReadRuleset(github, slug, expected, id) is { Live: { } unchanged } && Bypass(unchanged).Count == 0)
                        {
                            output.WriteLine("It didn't land, so this isn't a takeover; the next tick tries again.");
                            File.WriteAllText(statePath, before);
                            return 2;
                        }
                    }
                    var (_, reread, after) = ReadRuleset(github, slug, expected, id);
                    var verified = after.Count == 0 && OnlyAdminBypass(reread);
                    output.WriteLine(verified ? $"Ruleset {id} now has the one admin pull-request bypass (verified)." : $"::error::merge-takeover: ruleset {id} did NOT verify after the PUT: {string.Join("; ", after.Append(OnlyAdminBypass(reread) ? "" : "its bypass actors aren't exactly the admin pull-request one").Where(p => p.Length > 0))}. Nothing merges until @{Owner} has looked.");
                    // Unverified, it stays a takeover, so recovery removes the bypass and verifies again.
                    state.NeedsOwner = !verified;
                    Post(EnterReceipt(now, id!.Value, evidence, verified));
                    if (!verified)
                        return 1;
                    merging = true;
                    break;
                }
            case "exit":
                {
                    if (dryRun)
                    {
                        output.WriteLine($"Would PUT ruleset {state.RulesetId} with no bypass actors, verify it against quality/ruleset-main.json, and post the takeover-ended receipt on #{Issue}.");
                        break;
                    }
                    // A read or a PUT that fails before anything changed keeps the takeover, so the
                    // next tick tries again, rather than leave the bypass for the owner to find.
                    var (id, live, readProblems) = ReadRuleset(github, slug, expected, state.RulesetId);
                    if (live is null)
                    {
                        output.WriteLine($"Couldn't read ruleset {state.RulesetId} to hand back ({string.Join("; ", readProblems)}); still in takeover, trying again next tick.");
                        SaveState(statePath, state);
                        return 2;
                    }
                    var putFailed = false;
                    try
                    {
                        github.Send("PUT", $"repos/{slug}/rulesets/{id}", PutBody(live, []).ToJsonString()).Dispose();
                    }
                    catch (Exception e) when (MergeRuleCommand.IsReadFailure(e))
                    {
                        output.WriteLine($"The PUT to ruleset {id} failed ({e.Message}); checking what landed.");
                        putFailed = true;
                    }
                    var (_, reread, after) = ReadRuleset(github, slug, expected, id);
                    if (reread is null)
                    {
                        output.WriteLine($"Couldn't read ruleset {id} back after the PUT ({string.Join("; ", after)}); still in takeover, checking again next tick.");
                        SaveState(statePath, state);
                        return 2;
                    }
                    if (Bypass(reread).Count > 0)
                    {
                        if (putFailed && OnlyAdminBypass(reread) && after.Count == 0)
                        {
                            output.WriteLine($"Ruleset {id} still has the takeover's bypass; still in takeover, trying again next tick.");
                            SaveState(statePath, state);
                            return 2;
                        }
                        after.Add($"it still has {Bypass(reread).Count} bypass actor(s)");
                    }
                    var merged = state.Merged.ToList();
                    var lasted = now - (state.EnteredAt ?? now);
                    state.InTakeover = false;
                    state.ExitedAt = now;
                    state.FirstHealthyAt = null;
                    state.NeedsOwner = after.Count > 0;
                    state.Merged = [];
                    SaveState(statePath, state);
                    if (after.Count == 0)
                        output.WriteLine($"Ruleset {id} has no bypass and matches quality/ruleset-main.json (verified).");
                    else
                    {
                        output.WriteLine($"::error::merge-takeover: ruleset {id} did NOT verify after removing the bypass: {string.Join("; ", after)}. It needs @{Owner}.");
                        rulesetProblem = true;
                    }
                    Post(ExitReceipt(now, id ?? state.RulesetId ?? 0, lasted, merged, observed, after));
                    break;
                }
            case "stay in":
                merging = !state.NeedsOwner;
                if (state.NeedsOwner)
                {
                    output.WriteLine($"::error::merge-takeover: ruleset {state.RulesetId} didn't verify when the takeover began, so nothing merges; it needs @{Owner}.");
                    rulesetProblem = true;
                }
                if (state.EnteredAt is { } since && now - since > OwnerConfirmsAfter && !state.CapNoticePosted)
                {
                    if (dryRun)
                        output.WriteLine($"Would ask @{Owner} on #{Issue} to confirm the takeover, now over {OwnerConfirmsAfter.TotalHours:0} h.");
                    else
                    {
                        state.CapNoticePosted = true;
                        Post($"**Quality: the Actions takeover has lasted over {OwnerConfirmsAfter.TotalHours:0} hours** (since {FlakeReport.Pacific(since)}). @{Owner}, please confirm Quality should keep merging under the temporary admin bypass on ruleset {state.RulesetId}. Until Actions recovers it stays in place, with the same rules; {state.Merged.Count} PR(s) merged so far.");
                    }
                }
                if (!dryRun)
                    SaveState(statePath, state);
                break;
            default:
                if (!dryRun)
                    SaveState(statePath, state);
                break;
        }

        if (merging && !MergeOne(output, github, config, slug, state, statePath, now, dryRun))
            otherProblem = true;
        return rulesetProblem ? 1 : otherProblem ? 2 : 0;
    }

    /// <summary>
    /// Enter needs both signals bad, since either alone has been wrong: the status page lags and
    /// covers every region, and a merge-rule run can queue behind a busy runner pool. Exit needs
    /// both good on two ticks 4+ minutes apart, and after an exit the takeover stays off for 15
    /// minutes unless the stall is already 30 minutes old, so a flickering recovery doesn't add
    /// and remove the bypass every pass.
    /// </summary>
    internal static (string Decision, string Why) Decide(TakeoverState state, TakeoverObservation o, DateTimeOffset now)
    {
        if (!state.InTakeover)
        {
            if (o.ActionsStatus is null)
                return ("stay out", "the status page couldn't be read, and unknown never enters");
            if (o.ActionsStatus == "operational")
                return ("stay out", o.Stall is null ? "Actions is operational and merge-rule isn't stalled" : "merge-rule is stalled, but the status page says Actions is operational");
            if (o.Stall is not { } stall)
                return ("stay out", $"Actions is {o.ActionsStatus}, but merge-rule isn't stalled");
            if (state.ExitedAt is { } exited && now - exited < NoReentryFor && stall < ReentryAnywayAfter)
                return ("hold off", $"the last takeover ended {Minutes(now - exited)} ago, under {Minutes(NoReentryFor)}, and the stall is only {Minutes(stall)} old");
            return ("enter", $"Actions is {o.ActionsStatus} and merge-rule has stalled for {Minutes(stall)}");
        }
        if (o.ActionsStatus != "operational" || !o.Recovered)
        {
            state.FirstHealthyAt = null;
            return ("stay in", o.ActionsStatus != "operational" ? $"Actions is {o.StatusWords}" : "merge-rule hasn't recovered");
        }
        if (state.FirstHealthyAt is not { } first)
        {
            state.FirstHealthyAt = now;
            return ("stay in", "Actions looks healthy; waiting for a second healthy check");
        }
        if (now - first < HealthyApart)
            return ("stay in", $"healthy since {Minutes(now - first)} ago; waiting for {Minutes(HealthyApart)}");
        return ("exit", $"Actions is operational and merge-rule has recovered, on checks {Minutes(now - first)} apart");
    }

    /// <summary>The Actions component's status in the status page's summary, or null if it isn't one of the four it uses.</summary>
    internal static string? ActionsStatusIn(JsonElement summary)
    {
        foreach (var component in summary.GetProperty("components").EnumerateArray())
            if (component.GetProperty("name").GetString() == "Actions")
            {
                var status = component.GetProperty("status").GetString();
                return status is "operational" or "degraded_performance" or "partial_outage" or "major_outage" ? status : null;
            }
        return null;
    }

    /// <summary>
    /// The merge-rule workflow's runs: stalled when the oldest queued run is 15+ min old, or a run
    /// has shown in progress for 15+ min though every job in it is done (both seen on 2026-11-04);
    /// recovered when a run succeeded in the last 10 min and nothing has queued for 5. Runs that
    /// can't be read are neither, so a takeover can't start or end on nothing.
    /// </summary>
    static TakeoverObservation Observe(IGitHub github, IStatusPage statusPage, string slug, DateTimeOffset now)
    {
        var status = statusPage.ActionsStatus();
        var runs = $"repos/{slug}/actions/workflows/{Workflow}/runs";
        try
        {
            using var queued = github.Rest($"{runs}?status=queued&per_page=100");
            var queuedAges = queued.RootElement.GetProperty("workflow_runs").EnumerateArray().Select(r => now - Started(r)).ToList();
            var oldestQueued = queuedAges.Count > 0 ? queuedAges.Max() : (TimeSpan?)null;

            using var running = github.Rest($"{runs}?status=in_progress&per_page=100");
            TimeSpan? stuck = null;
            var stuckCount = 0;
            foreach (var run in running.RootElement.GetProperty("workflow_runs").EnumerateArray())
            {
                var age = now - Started(run);
                if (age < StallAfter)
                    continue;
                using var jobs = github.Rest($"repos/{slug}/actions/runs/{run.GetProperty("id").GetInt64()}/jobs?per_page=100");
                var list = jobs.RootElement.GetProperty("jobs").EnumerateArray().ToList();
                if (list.Count > 0 && list.All(j => j.GetProperty("status").GetString() == "completed"))
                {
                    stuckCount++;
                    stuck = stuck is { } s && s > age ? s : age;
                }
            }

            using var succeeded = github.Rest($"{runs}?status=success&per_page=10");
            var lastSuccess = succeeded.RootElement.GetProperty("workflow_runs").EnumerateArray()
                .Select(r => DateTimeOffset.Parse(r.GetProperty("updated_at").GetString()!, CultureInfo.InvariantCulture))
                .DefaultIfEmpty(DateTimeOffset.MinValue).Max();

            var stall = new[] { oldestQueued is { } q && q >= StallAfter ? q : (TimeSpan?)null, stuck }.Max();
            var recovered = now - lastSuccess <= RecentSuccess && !(oldestQueued >= QueuedWhileRecovered);
            var words = $"{queuedAges.Count} queued{(oldestQueued is { } oq ? $", the oldest for {Minutes(oq)}" : "")}; "
                + $"{stuckCount} shown in progress with every job done{(stuck is { } st ? $", for {Minutes(st)}" : "")}; "
                + (lastSuccess == DateTimeOffset.MinValue ? "no recent success" : $"last success {Minutes(now - lastSuccess)} ago");
            return new(status, stall, recovered, words);
        }
        catch (Exception e) when (MergeRuleCommand.IsReadFailure(e))
        {
            return new(status, null, false, $"couldn't be read ({e.Message})");
        }
    }

    static DateTimeOffset Started(JsonElement run) =>
        DateTimeOffset.Parse(
            (run.TryGetProperty("run_started_at", out var started) && started.ValueKind == JsonValueKind.String ? started : run.GetProperty("created_at")).GetString()!,
            CultureInfo.InvariantCulture);

    /// <summary>
    /// Main's ruleset, found by quality/ruleset-main.json's name (or the id the takeover recorded),
    /// with every way it differs from that file. Null when it can't be found or read.
    /// </summary>
    static (long? Id, JsonObject? Live, List<string> Problems) ReadRuleset(IGitHub github, string slug, JsonObject expected, long? id = null)
    {
        try
        {
            if (id is null)
            {
                using var list = github.Rest($"repos/{slug}/rulesets?per_page=100");
                var name = expected["name"]!.GetValue<string>();
                id = list.RootElement.EnumerateArray().Where(r => r.GetProperty("name").GetString() == name).Select(r => (long?)r.GetProperty("id").GetInt64()).FirstOrDefault();
                if (id is null)
                    return (null, null, [$"no ruleset named '{name}'"]);
            }
            using var doc = github.Rest($"repos/{slug}/rulesets/{id}");
            var live = JsonNode.Parse(doc.RootElement.GetRawText())!.AsObject();
            return (id, live, Drift(expected, live));
        }
        catch (Exception e) when (MergeRuleCommand.IsReadFailure(e))
        {
            return (id, null, [$"couldn't read it ({e.Message})"]);
        }
    }

    /// <summary>
    /// How the live ruleset differs from quality/ruleset-main.json in its target, enforcement,
    /// conditions and rules; bypass actors are judged apart, and server-only fields (id, source,
    /// _links, timestamps) aren't compared. GitHub also fills in parameters the file leaves out:
    /// those count only when they say something (not empty, false or null), except the ones known
    /// to be GitHub's defaults, which it adds to every ruleset.
    /// </summary>
    internal static List<string> Drift(JsonObject expected, JsonObject live)
    {
        var problems = new List<string>();
        foreach (var key in new[] { "target", "enforcement", "conditions", "rules" })
            Compare(expected[key], live[key], key, problems);
        return problems;
    }

    static readonly HashSet<string> ServerDefaults = new(StringComparer.Ordinal) { "require_extra_approval_for_unattributed_changes" };

    static void Compare(JsonNode? want, JsonNode? have, string path, List<string> problems)
    {
        switch (want)
        {
            case JsonObject wantObject when have is JsonObject haveObject:
                foreach (var (key, value) in wantObject)
                    if (!haveObject.ContainsKey(key))
                        problems.Add($"{path}.{key} is missing");
                    else
                        Compare(value, haveObject[key], $"{path}.{key}", problems);
                foreach (var (key, value) in haveObject)
                    if (!wantObject.ContainsKey(key) && !ServerDefaults.Contains(key) && Says(value))
                        problems.Add($"{path}.{key} is {value!.ToJsonString()}, which the file doesn't set");
                break;
            case JsonArray wantArray when have is JsonArray haveArray:
                if (wantArray.Count != haveArray.Count)
                {
                    problems.Add($"{path} has {haveArray.Count} item(s), not {wantArray.Count}");
                    break;
                }
                // Order doesn't matter: rules match by type, status checks by context, the rest by value.
                var unmatched = haveArray.ToList();
                foreach (var item in wantArray)
                {
                    var key = Key(item);
                    var match = unmatched.FirstOrDefault(h => key is not null ? Key(h) == key : JsonNode.DeepEquals(h, item));
                    if (match is null)
                    {
                        problems.Add($"{path} has no {key ?? item?.ToJsonString()}");
                        continue;
                    }
                    unmatched.Remove(match);
                    if (key is not null)
                        Compare(item, match, $"{path}[{key}]", problems);
                }
                break;
            default:
                if (!JsonNode.DeepEquals(want, have))
                    problems.Add($"{path} is {have?.ToJsonString() ?? "missing"}, not {want?.ToJsonString()}");
                break;
        }
    }

    static string? Key(JsonNode? item) =>
        item is JsonObject o ? (o["type"] ?? o["context"])?.ToJsonString() : null;

    static bool Says(JsonNode? value) => value switch
    {
        null => false,
        JsonArray a => a.Count > 0,
        JsonObject o => o.Count > 0,
        JsonValue v when v.TryGetValue<bool>(out var b) => b,
        _ => true,
    };

    static JsonArray Bypass(JsonObject? ruleset) => ruleset?["bypass_actors"] as JsonArray ?? [];

    static bool OnlyAdminBypass(JsonObject? ruleset) =>
        Bypass(ruleset) is [JsonObject actor]
        && actor["actor_id"]?.ToJsonString() == "5"
        && actor["actor_type"]?.GetValue<string>() == "RepositoryRole"
        && actor["bypass_mode"]?.GetValue<string>() == "pull_request";

    /// <summary>The live ruleset as a PUT body: what it says, with these bypass actors and none of the server's own fields.</summary>
    internal static JsonObject PutBody(JsonObject live, JsonNode[] bypass) => new()
    {
        ["name"] = live["name"]?.DeepClone(),
        ["target"] = live["target"]?.DeepClone(),
        ["enforcement"] = live["enforcement"]?.DeepClone(),
        ["bypass_actors"] = new JsonArray(bypass),
        ["conditions"] = live["conditions"]?.DeepClone(),
        ["rules"] = live["rules"]?.DeepClone(),
    };

    const string CandidatesQuery = """
        query($owner: String!, $name: String!, $base: String!, $cursor: String) {
          repository(owner: $owner, name: $name) {
            pullRequests(states: OPEN, baseRefName: $base, first: 50, after: $cursor, orderBy: { field: CREATED_AT, direction: ASC }) {
              pageInfo { hasNextPage endCursor }
              nodes {
                number isDraft headRefOid mergeable
                autoMergeRequest { enabledAt }
                labels(first: 100) { totalCount nodes { name } }
                reviewThreads(first: 100) { totalCount nodes { isResolved } }
              }
            }
          }
        }
        """;

    /// <summary>
    /// The takeover's merge: the oldest open pull request into main with auto-merge armed (the
    /// session's intent) and no hold label, 0 unresolved threads and MERGEABLE, that passes main's
    /// evaluator with quality-gate green on the head it judged; merged at that head. One a tick,
    /// since main moves with each and the rest must be judged again. False when something failed.
    /// </summary>
    static bool MergeOne(TextWriter output, IGitHub github, MergeRuleConfig config, string slug, TakeoverState state, string statePath, DateTimeOffset now, bool dryRun)
    {
        var (owner, name) = (slug.Split('/')[0], slug.Split('/')[^1]);
        var candidates = new List<(int Number, string Head)>();
        try
        {
            string? cursor = null;
            do
            {
                var variables = new Dictionary<string, object> { ["owner"] = owner, ["name"] = name, ["base"] = config.Base };
                if (cursor is not null)
                    variables["cursor"] = cursor;
                using var doc = github.GraphQL(CandidatesQuery, variables);
                var page = doc.RootElement.GetProperty("data").GetProperty("repository").GetProperty("pullRequests");
                foreach (var pr in page.GetProperty("nodes").EnumerateArray())
                {
                    var number = pr.GetProperty("number").GetInt32();
                    var why = NotACandidate(pr, config);
                    if (why is null)
                        candidates.Add((number, pr.GetProperty("headRefOid").GetString()!));
                    else if (why.Length > 0)
                        output.WriteLine($"#{number}: not merged: {why}.");
                }
                var info = page.GetProperty("pageInfo");
                cursor = info.GetProperty("hasNextPage").GetBoolean() ? info.GetProperty("endCursor").GetString() : null;
            }
            while (cursor is not null);
        }
        catch (Exception e) when (MergeRuleCommand.IsReadFailure(e))
        {
            output.WriteLine($"Couldn't list the open pull requests, so nothing is merged this tick: {e.Message}");
            return false;
        }
        if (candidates.Count == 0)
        {
            output.WriteLine("No armed pull request is ready to merge.");
            return true;
        }

        MergeRuleCommand.Judge judge;
        try
        {
            judge = MergeRuleCommand.Judge.Start(github, config, slug);
        }
        catch (Exception e) when (MergeRuleCommand.IsReadFailure(e))
        {
            output.WriteLine($"Couldn't start main's evaluator, so nothing is merged this tick: {e.Message}");
            return false;
        }
        output.WriteLine(judge.MainLog);
        foreach (var (number, head) in candidates)
        {
            (PullRequestFacts Pr, MergeVerdict Verdict)? judged;
            try
            {
                judged = judge.Of(number);
            }
            catch (Exception e) when (MergeRuleCommand.IsReadFailure(e))
            {
                output.WriteLine($"#{number}: not merged: couldn't judge it ({e.Message}).");
                continue;
            }
            if (judged is not { } j)
                continue;
            var sha = j.Pr.HeadSha;
            string? blocker =
                !string.Equals(sha, head, StringComparison.OrdinalIgnoreCase) ? $"its head moved from {MergeRule.Short(head)} to {MergeRule.Short(sha)} while it was read"
                : j.Verdict.State != "success" ? $"main's evaluator says {j.Verdict.State}: {j.Verdict.Description}"
                : !j.Pr.Checks.Any(c => c.Name == GateCheck && c.State == "success" && c.Detail == "success") ? $"{GateCheck} isn't green on {MergeRule.Short(sha)}"
                : null;
            if (blocker is not null)
            {
                output.WriteLine($"#{number} {MergeRule.Short(sha)}: not merged: {blocker}.");
                continue;
            }
            if (dryRun)
            {
                output.WriteLine($"#{number} {MergeRule.Short(sha)}: would merge (gh pr merge {number} --squash --admin --match-head-commit {sha}).");
                return true;
            }
            try
            {
                github.MergePullRequest(slug, number, sha);
            }
            catch (InvalidOperationException e)
            {
                output.WriteLine($"#{number} {MergeRule.Short(sha)}: the merge failed: {e.Message}");
                return false;
            }
            state.Merged.Add(new(number, sha, now));
            SaveState(statePath, state);
            output.WriteLine($"#{number} {MergeRule.Short(sha)}: merged ({j.Verdict.Description}). The rest are judged again next tick, on the new main.");
            return true;
        }
        output.WriteLine($"Nothing merged this tick. API: {judge.Cost}.");
        return true;
    }

    /// <summary>Why an open pull request isn't one the takeover may merge: null if it is, "" for one not to mention (not armed, a draft).</summary>
    static string? NotACandidate(JsonElement pr, MergeRuleConfig config)
    {
        if (pr.GetProperty("isDraft").GetBoolean() || pr.GetProperty("autoMergeRequest").ValueKind != JsonValueKind.Object)
            return "";
        var labels = pr.GetProperty("labels");
        var names = labels.GetProperty("nodes").EnumerateArray().Select(l => l.GetProperty("name").GetString() ?? "").ToList();
        if (names.FirstOrDefault(l => config.HoldLabels.Contains(l, StringComparer.OrdinalIgnoreCase)) is { } hold)
            return $"held by the label '{hold}'";
        if (labels.GetProperty("totalCount").GetInt32() > names.Count)
            return "more labels than were read, so a hold could be hidden";
        var threads = pr.GetProperty("reviewThreads");
        var nodes = threads.GetProperty("nodes").EnumerateArray().ToList();
        if (threads.GetProperty("totalCount").GetInt32() > nodes.Count)
            return "more review threads than were read";
        var open = nodes.Count(t => !t.GetProperty("isResolved").GetBoolean());
        if (open > 0)
            return $"{open} unresolved thread(s)";
        var mergeable = pr.GetProperty("mergeable").GetString();
        return mergeable == "MERGEABLE" ? null : $"GitHub says it's {mergeable?.ToLowerInvariant()}";
    }

    static void Comment(IGitHub github, string slug, string body) =>
        github.Send("POST", $"repos/{slug}/issues/{Issue}/comments", new JsonObject { ["body"] = body }.ToJsonString()).Dispose();

    static string EnterReceipt(DateTimeOffset now, long id, string evidence, bool verified)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"**Quality: automatic takeover of merging during GitHub's Actions incident (started {FlakeReport.Pacific(now)}).**");
        sb.AppendLine();
        sb.AppendLine($"{evidence}. So the merge-rule App can't post `merge-rule`, and auto-merge has stalled.");
        sb.AppendLine();
        sb.AppendLine("While it lasts:");
        sb.AppendLine(verified
            ? $"- Ruleset {id} has a temporary bypass for repository admins, for pull requests only. Nothing else in it changed, and the backup is kept."
            : $"- Ruleset {id} was given a temporary bypass for repository admins, for pull requests only, but it **didn't verify** when read back. @{Owner}, it needs you; nothing merges until it's right.");
        sb.AppendLine("- Quality merges, one per pass, only PRs with auto-merge armed and no `hold` or `do-not-merge` label that pass **main's own evaluator** (the verdict `merge-rule --pr <n>` gives from a fresh `origin/main` checkout). Each must also have `quality-gate` green on its exact head, no unresolved threads, and GitHub reporting it mergeable. Every merge uses `--match-head-commit`.");
        sb.AppendLine("- When Actions recovers (the status page says operational and merge-rule runs succeed again, on two checks at least 4 minutes apart), Quality removes the bypass and checks the ruleset against `quality/ruleset-main.json` again.");
        sb.AppendLine();
        sb.Append("Sessions: nothing changes for you. Keep arming auto-merge as usual.");
        return sb.ToString();
    }

    static string ExitReceipt(DateTimeOffset now, long id, TimeSpan lasted, List<MergedPullRequest> merged, TakeoverObservation observed, List<string> problems)
    {
        var prs = merged.Count == 0 ? "no PRs" : string.Join(", ", merged.Select(m => $"#{m.Number} ({MergeRule.Short(m.Sha)})"));
        if (problems.Count > 0)
            return $"**Quality: takeover ended ({FlakeReport.Pacific(now)}), but ruleset {id} did NOT verify.** @{Owner}, the ruleset needs you: {string.Join("; ", problems)}. "
                + $"Compare it with `quality/ruleset-main.json`; Quality changes nothing more until it matches. The takeover lasted {Minutes(lasted)} and merged {prs}.";
        return $"**Quality: takeover ended ({FlakeReport.Pacific(now)}).** GitHub Actions is posting merge-rule again ({observed.Runs}). "
            + $"The temporary admin bypass on ruleset {id} is removed (verified: 0 bypass actors, and it matches `quality/ruleset-main.json`). "
            + $"The takeover lasted {Minutes(lasted)} and merged {prs}. Auto-merge is back in charge.";
    }

    static string Minutes(TimeSpan span) =>
        span.TotalMinutes < 60 ? $"{Math.Max(0, (int)span.TotalMinutes)} min" : $"{(int)span.TotalHours} h {span.Minutes} min";

    internal static string DefaultStatePath()
    {
        var dir = OperatingSystem.IsWindows()
            ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : Environment.GetEnvironmentVariable("XDG_STATE_HOME") is { Length: > 0 } xdg ? xdg
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state");
        return Path.Combine(dir, "agent-bridge", "merge-takeover.json");
    }

    static TakeoverState LoadState(string path) =>
        File.Exists(path) ? JsonSerializer.Deserialize<TakeoverState>(File.ReadAllText(path), Json) ?? new() : new();

    static void SaveState(string path, TakeoverState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        // Written whole and then moved, so a tick killed halfway can't leave half a state.
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(state, Json));
        File.Move(temp, path, overwrite: true);
    }

    static readonly HashSet<string> Flags = new(StringComparer.Ordinal) { "dry-run", "allow-non-main" };
    static readonly HashSet<string> Valued = new(StringComparer.Ordinal) { "slug", "state", "config" };

    static Dictionary<string, string> ParseOptions(IReadOnlyList<string> args)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Count; i++)
        {
            var name = args[i].StartsWith("--", StringComparison.Ordinal) ? args[i][2..] : null;
            if (name is not null && Flags.Contains(name))
                options[name] = "";
            else if (name is not null && Valued.Contains(name))
            {
                if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException($"--{name} needs a value.");
                options[name] = args[++i];
            }
            else
                throw new ArgumentException($"Unexpected argument '{args[i]}'.");
        }
        if (options.TryGetValue("slug", out var slug) && (slug.Split('/') is not [{ Length: > 0 }, { Length: > 0 }]))
            throw new ArgumentException($"--slug {slug} is not OWNER/REPO.");
        return options;
    }
}
