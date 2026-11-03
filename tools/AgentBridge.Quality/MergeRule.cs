using System.Text.RegularExpressions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace AgentBridge.Quality;

/// <summary>
/// quality/merge-rule.yml: what a pull request into main needs before GitHub may merge it, beyond
/// green CI and resolved threads, which main's ruleset checks itself (AGENTS.md, "Pull requests").
/// </summary>
sealed class MergeRuleConfig
{
    public string Base { get; set; } = "";
    public string CopilotReviewer { get; set; } = "";
    public List<string> HoldLabels { get; set; } = [];
    public IndependentReviewConfig IndependentReview { get; set; } = new();
    public CodeQlConfig Codeql { get; set; } = new();
    public List<string> SensitivePaths { get; set; } = [];

    public static MergeRuleConfig Parse(string yaml)
    {
        // Unlike the catalog, an unknown key fails: a misspelled sensitive_paths would otherwise
        // read as an empty list, and every pull request would skip the independent review.
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();
        var config = deserializer.Deserialize<MergeRuleConfig?>(yaml) ?? throw new FormatException("quality/merge-rule.yml is empty.");
        config.HoldLabels ??= [];
        config.SensitivePaths ??= [];
        config.IndependentReview ??= new();
        config.IndependentReview.Posters ??= [];
        config.Codeql ??= new();
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(config.Base)) missing.Add("base");
        if (string.IsNullOrWhiteSpace(config.CopilotReviewer)) missing.Add("copilot_reviewer");
        if (string.IsNullOrWhiteSpace(config.IndependentReview.Context)) missing.Add("independent_review.context");
        if (config.IndependentReview.Posters.Count == 0) missing.Add("independent_review.posters");
        if (string.IsNullOrWhiteSpace(config.Codeql.Workflow)) missing.Add("codeql.workflow");
        if (string.IsNullOrWhiteSpace(config.Codeql.Check)) missing.Add("codeql.check");
        if (config.SensitivePaths.Count == 0) missing.Add("sensitive_paths");
        if (missing.Count > 0)
            throw new FormatException($"quality/merge-rule.yml has no {string.Join(", ", missing)}.");
        return config;
    }

    public static MergeRuleConfig Load(string path) => Parse(File.ReadAllText(path));
}

sealed class IndependentReviewConfig
{
    public string Context { get; set; } = "";
    public List<string> Posters { get; set; } = [];
}

sealed class CodeQlConfig
{
    public string Workflow { get; set; } = "";
    public string Check { get; set; } = "";
}

/// <summary>A commit status on the head: GitHub keeps every one posted, newest last by time.</summary>
sealed record StatusFact(string Context, string State, string? Description, string? Creator, DateTimeOffset CreatedAt);

/// <summary>A submitted review. <c>IsBot</c> is GraphQL's typename, which no user account can claim.</summary>
sealed record ReviewFact(string Author, bool IsBot, string State, string? CommitOid);

/// <summary>A workflow run's or check run's progress, in GraphQL's words (COMPLETED, SUCCESS...).</summary>
sealed record RunFact(string Status, string? Conclusion);

sealed record PullRequestFacts(
    int Number,
    string State,
    bool IsDraft,
    string BaseRef,
    string HeadSha,
    IReadOnlyList<string> Labels,
    IReadOnlyList<string> Files,
    bool FilesComplete,
    IReadOnlyList<ReviewFact> Reviews,
    IReadOnlyList<StatusFact> Statuses,
    RunFact? CodeQlRun,
    RunFact? CodeQlCheck);

/// <summary>The `merge-rule` status to post: success, pending or failure, and why.</summary>
sealed record Verdict(string State, string Description, IReadOnlyList<string> Reasons, IReadOnlyList<string> SensitiveFiles);

static class MergeRule
{
    public const string StatusContext = "merge-rule";
    // GitHub truncates nothing: it refuses a longer description.
    const int MaxDescription = 140;

    /// <summary>
    /// The verdict on one pull request, or null when the rule doesn't apply (closed, or into
    /// another branch). Failure is anything a person must act on; pending is what's on its way
    /// (Copilot's review, CodeQL). Every missing piece is named, failures first.
    /// </summary>
    public static Verdict? Evaluate(MergeRuleConfig config, PullRequestFacts pr)
    {
        if (pr.State != "OPEN" || pr.BaseRef != config.Base)
            return null;
        var sha = Short(pr.HeadSha);
        var failures = new List<string>();
        var pending = new List<string>();

        if (pr.IsDraft)
            failures.Add("draft: mark it ready for review");
        foreach (var label in pr.Labels.Where(l => config.HoldLabels.Contains(l, StringComparer.OrdinalIgnoreCase)))
            failures.Add($"held by the label '{label}'");

        var copilot = pr.Reviews.Any(r => r.IsBot
            && string.Equals(r.Author, config.CopilotReviewer, StringComparison.Ordinal)
            && string.Equals(r.CommitOid, pr.HeadSha, StringComparison.OrdinalIgnoreCase)
            && r.State is not ("PENDING" or "DISMISSED"));
        if (!copilot)
            pending.Add($"waiting for a Copilot review of {sha}");

        var globs = config.SensitivePaths.Select(p => new Glob(p, ignoreCase: true)).ToList();
        var sensitive = pr.Files.Where(f => globs.Any(g => g.IsMatch(f))).Distinct(StringComparer.Ordinal).ToList();
        var receipt = pr.Statuses
            .Where(s => s.Context == config.IndependentReview.Context
                && s.Creator is not null && config.IndependentReview.Posters.Contains(s.Creator, StringComparer.OrdinalIgnoreCase))
            .OrderByDescending(s => s.CreatedAt)
            .FirstOrDefault();
        // A reviewer's failure blocks any pull request, sensitive or not.
        if (receipt is { State: "failure" or "error" })
            failures.Add($"the independent review of {sha} failed: {receipt.Description}");
        else if ((sensitive.Count > 0 || !pr.FilesComplete) && receipt?.State != "success")
        {
            // A file list GitHub cut short could hide a sensitive file, so it counts as one.
            var what = !pr.FilesComplete ? "its file list is incomplete"
                : $"it touches {sensitive[0]}{(sensitive.Count > 1 ? $" (+{sensitive.Count - 1} more)" : "")}";
            failures.Add($"needs an independent review of {sha}: {what}");
        }

        // Pending, since CodeQL starts with the push; but a pull request retargeted onto main gets
        // no run until a push or a reopen (codeql.yml's types), so the description says so.
        if (pr.CodeQlRun is null)
            pending.Add($"no {config.Codeql.Workflow} run on {sha} yet (after a retarget, push or reopen)");
        else if (pr.CodeQlRun.Status != "COMPLETED")
            pending.Add($"{config.Codeql.Workflow} is running on {sha}");
        else if (pr.CodeQlRun.Conclusion != "SUCCESS")
            failures.Add($"{config.Codeql.Workflow} ended {pr.CodeQlRun.Conclusion?.ToLowerInvariant() ?? "without a result"} on {sha}");
        // Code scanning's own check: absent when no language was analysed, neutral when some were
        // not, failure on new alerts at GitHub's threshold.
        if (pr.CodeQlCheck is { } check)
        {
            if (check.Status != "COMPLETED")
                pending.Add($"code scanning is still comparing {sha}'s {config.Codeql.Check} results");
            else if (check.Conclusion is not ("SUCCESS" or "NEUTRAL" or "SKIPPED"))
                failures.Add($"code scanning's {config.Codeql.Check} check is {check.Conclusion?.ToLowerInvariant()}: new alerts?");
        }

        var reasons = failures.Concat(pending).ToList();
        var state = failures.Count > 0 ? "failure" : pending.Count > 0 ? "pending" : "success";
        var description = state == "success"
            ? $"Copilot reviewed {sha}; CodeQL passed{(sensitive.Count > 0 || !pr.FilesComplete ? "; independent review on file" : "")}"
            : Capitalize(string.Join("; ", reasons));
        return new Verdict(state, Truncate(description), reasons, sensitive);
    }

    public static string Short(string sha) => sha.Length > 7 ? sha[..7] : sha;

    static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    static string Truncate(string s) => s.Length <= MaxDescription ? s : s[..(MaxDescription - 1)] + "…";

    /// <summary>A Markdown table cell can't hold a pipe or a line break.</summary>
    public static string Cell(string s) => Regex.Replace(s.Replace("|", "\\|", StringComparison.Ordinal), @"\s+", " ");
}
