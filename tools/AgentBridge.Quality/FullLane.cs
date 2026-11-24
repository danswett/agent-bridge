using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace AgentBridge.Quality;

/// <summary>
/// The FULL lane's workflow (full.yml, S2, #4104, quality/README.md §4.7): everything the merge gate no longer waits for. It never
/// gates a merge, so it may not run for a pull request, a merge group or a push; it runs after a green FAST run (workflow_run), on
/// a schedule and by hand. Its aggregate <c>full-gate</c> mirrors quality-gate: it needs every other job, runs whatever they did,
/// and reads their results, so a skipped job fails it instead of passing. Which jobs belong to the lane is the catalog's
/// <c>lane: full</c> suites (their where names full.yml; <see cref="Catalog.Validate"/> and <see cref="Completeness"/>).
/// </summary>
static class FullLane
{
    public const string Name = "full";
    public const string File = "full.yml";
    const string Gate = "full-gate";
    const string Kind = "full-lane";
    static readonly string[] Triggers = ["workflow_run", "schedule", "workflow_dispatch"];

    // The commit under test: the jobs run only when the trigger job says ok, and it says ok only for a run by hand or a green push run of THIS repository on
    // main, so a fork's, a pull request's or another branch's run, or a CI run that did not pass, never starts them; and each checks out that run's commit.
    const string RunByHand = "github.event_name == 'workflow_dispatch'";
    static readonly string[] GreenPushOnMain =
    [
        "github.event.workflow_run.conclusion == 'success'", "github.event.workflow_run.event == 'push'",
        "github.event.workflow_run.head_branch == 'main'", "github.event.workflow_run.head_repository.full_name == github.repository",
    ];
    const string CommitRef = "${{ github.event.workflow_run.head_sha || inputs.sha }}";
    const string OnlyWhenOk = "needs.trigger.outputs.ok == 'true'";

    public static IReadOnlyList<Problem> Check(Catalog catalog, string yaml)
    {
        var problems = new List<Problem>();
        void Add(string message) => problems.Add(new(Kind, message));

        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root
            || Child(root, "jobs") is not YamlMappingNode jobMap || jobMap.Children.Count == 0)
        {
            Add($"{File} has no jobs");
            return problems;
        }

        foreach (var trigger in TriggerNames(Child(root, "on")).Where(t => !Triggers.Contains(t, StringComparer.Ordinal)))
            Add($"{File} triggers on {trigger}, but the FULL lane runs only on {string.Join(", ", Triggers[..^1])} and {Triggers[^1]}");

        var jobs = jobMap.Children
            .Where(c => c.Key is YamlScalarNode { Value: not null } && c.Value is YamlMappingNode)
            .ToDictionary(c => ((YamlScalarNode)c.Key).Value!, c => (YamlMappingNode)c.Value, StringComparer.Ordinal);
        CheckGuards(jobs, Add);
        if (!jobs.TryGetValue(Gate, out var gate))
        {
            Add($"{File} has no {Gate} job");
            return problems;
        }
        var needs = Child(gate, "needs") switch
        {
            YamlScalarNode { Value: not null } one => [one.Value],
            YamlSequenceNode many => many.Children.OfType<YamlScalarNode>().Select(n => n.Value!).ToList(),
            _ => new List<string>(),
        };
        foreach (var id in jobs.Keys.Where(id => id != Gate && !needs.Contains(id, StringComparer.Ordinal)))
            Add($"{File} {Gate} doesn't need {id}");
        var condition = ((Child(gate, "if") as YamlScalarNode)?.Value ?? "").Trim();
        if (condition.StartsWith("${{", StringComparison.Ordinal) && condition.EndsWith("}}", StringComparison.Ordinal))
            condition = condition[3..^2].Trim();
        if (condition != "always()")
            Add($"{File} {Gate}'s if: isn't always(), so a failed or skipped job would skip the gate instead of failing it");
        if (!Mentions(gate, "toJSON(needs)"))
            Add($"{File} {Gate} reads no toJSON(needs), so it cannot tell a skipped job from a passed one");
        return problems;
    }

    static void CheckGuards(Dictionary<string, YamlMappingNode> jobs, Action<string> add)
    {
        if (!jobs.TryGetValue("trigger", out var trigger))
            add($"{File} has no trigger job, the one that says whether there is a green push run of this repository on main to test");
        else
        {
            var ok = Collapse(Child(trigger, "outputs") is YamlMappingNode outputs ? Scalar(outputs, "ok") : "");
            foreach (var condition in GreenPushOnMain.Prepend(RunByHand).Where(c => !ok.Contains(c, StringComparison.Ordinal)))
                add($"{File}'s trigger ok output doesn't test {condition}, so the jobs could start for a run they must not follow");
        }
        foreach (var (id, job) in jobs.Where(j => j.Key is not ("trigger" or Gate)))
        {
            if (!NeedsOf(job).Contains("trigger", StringComparer.Ordinal))
                add($"{File} job {id} doesn't need trigger");
            if (Collapse(Unwrap(Scalar(job, "if"))) != OnlyWhenOk)
                add($"{File} job {id}'s if: isn't {OnlyWhenOk}, so it could run when the trigger said no");
        }
        foreach (var (id, job) in jobs)
            foreach (var step in (Child(job, "steps") as YamlSequenceNode)?.Children.OfType<YamlMappingNode>() ?? [])
            {
                if (!Scalar(step, "uses").StartsWith("actions/checkout@", StringComparison.Ordinal))
                    continue;
                var with = Child(step, "with") as YamlMappingNode;
                var reference = with is null ? "" : Collapse(Scalar(with, "ref"));
                if (reference.Length == 0)
                    add($"{File} job {id}'s checkout has no ref, so it would test the workflow's own commit, not the one the FAST run passed on");
                else if (reference != CommitRef)
                    add($"{File} job {id} checks out {reference}, not the commit the FAST run passed on ({CommitRef})");
                if (with is null || Scalar(with, "persist-credentials") != "false")
                    add($"{File} job {id}'s checkout keeps credentials: persist-credentials must be false");
            }
    }

    static HashSet<string> NeedsOf(YamlMappingNode job) => Child(job, "needs") switch
    {
        YamlScalarNode { Value: not null } one => [one.Value],
        YamlSequenceNode many => many.Children.OfType<YamlScalarNode>().Select(n => n.Value!).ToHashSet(StringComparer.Ordinal),
        _ => [],
    };

    static string Scalar(YamlMappingNode map, string key) => (Child(map, key) as YamlScalarNode)?.Value ?? "";

    static string Unwrap(string value) => value.Trim().StartsWith("${{", StringComparison.Ordinal) && value.Trim().EndsWith("}}", StringComparison.Ordinal) ? value.Trim()[3..^2] : value;

    static string Collapse(string value) => Regex.Replace(value, @"\s+", " ").Trim();

    static IEnumerable<string> TriggerNames(YamlNode? on) => on switch
    {
        YamlScalarNode { Value: not null } one => [one.Value],
        YamlSequenceNode many => many.Children.OfType<YamlScalarNode>().Select(n => n.Value!),
        YamlMappingNode map => map.Children.Keys.OfType<YamlScalarNode>().Select(k => k.Value!),
        _ => [],
    };

    static bool Mentions(YamlNode node, string text) => node switch
    {
        YamlScalarNode scalar => scalar.Value?.Contains(text, StringComparison.Ordinal) == true,
        YamlSequenceNode sequence => sequence.Children.Any(c => Mentions(c, text)),
        YamlMappingNode map => map.Children.Any(c => Mentions(c.Value, text)),
        _ => false,
    };

    static YamlNode? Child(YamlMappingNode map, string key) =>
        map.Children.TryGetValue(new YamlScalarNode(key), out var value) ? value : null;
}
