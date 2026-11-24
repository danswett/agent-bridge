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
