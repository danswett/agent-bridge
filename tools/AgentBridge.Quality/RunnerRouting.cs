using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace AgentBridge.Quality;

/// <summary>
/// The routing guard for the self-hosted <c>ab-ci</c> runners (quality/README.md, "CI: speed, runners and what gates
/// main"). agent-bridge is a public repository and those runners persist between jobs, so a job may take one only
/// for a push or a pull request whose head is this repository: never a fork, and never anything a
/// <c>pull_request_target</c> runs. A workflow that names a self-hosted label without that guard is a violation, so a
/// later edit cannot drop it unseen.
/// </summary>
static partial class RunnerRouting
{
    // The whole guard, as quality/README.md gives it. The repository variable lets one setting flip the pool off.
    internal const string Guard = "vars.AB_CI_SELF_HOSTED == 'true' && (github.event_name == 'push' || github.event.pull_request.head.repo.full_name == github.repository)";

    // The labels only: not "--deny-self-hosted-runners", which names an option, not a runner.
    [GeneratedRegex(@"(?<![\w-])(ab-ci|self-hosted)(?![\w-])", RegexOptions.IgnoreCase)]
    private static partial Regex Label();

    public static IReadOnlyList<string> Violations(string yaml, string file)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root || !Label().IsMatch(yaml)) { return []; }
        var problems = new List<string>();
        var targetsFork = Triggers(root).Contains("pull_request_target");
        if (!root.Children.TryGetValue(new YamlScalarNode("jobs"), out var jobs) || jobs is not YamlMappingNode map) { return []; }
        var jobs_ = map.Children.Where(p => p.Key is YamlScalarNode && p.Value is YamlMappingNode)
            .ToDictionary(p => ((YamlScalarNode)p.Key).Value!, p => (YamlMappingNode)p.Value, StringComparer.Ordinal);
        // The jobs whose own text names a label: a runner chosen from their outputs can carry it.
        var labelled = jobs_.Where(p => Label().IsMatch(Text(p.Value))).Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var (name, job) in jobs_)
        {
            if (!job.Children.TryGetValue(new YamlScalarNode("runs-on"), out var runsOnNode)) { continue; }
            var runsOn = Text(runsOnNode);
            // A runner chosen by an expression can take the label from where it reads: this job's own matrix (its text
            // names the label) or another job's output (that job's text does). One that reads neither stays hosted.
            var dynamic = (runsOn.Contains("matrix.", StringComparison.Ordinal) || runsOn.Contains("needs.", StringComparison.Ordinal))
                && (Label().IsMatch(Text(job.Children.TryGetValue(new YamlScalarNode("strategy"), out var strategy) ? strategy : new YamlScalarNode("")))
                    || labelled.Any(other => other != name && Text(job).Contains($"needs.{other}.", StringComparison.Ordinal)
                        && Text(job).Contains(other, StringComparison.Ordinal)));
            if (!Label().IsMatch(runsOn) && !dynamic) { continue; }
            if (!Guarded(runsOn))
            {
                problems.Add($"{file} job {name} can run on a self-hosted runner without the guard in front of the label: {Guard} && <label>");
            }
            if (targetsFork)
            {
                problems.Add($"{file} job {name} can take a self-hosted runner in a workflow that a pull_request_target can run");
            }
        }
        return problems;
    }

    // The exact guard (whitespace aside), then "&&", and no label before it: a label reachable through "||", behind a
    // weaker condition, or ahead of the guard is a way round it. Two pieces of the guard somewhere in the expression are not.
    static bool Guarded(string runsOn)
    {
        var text = Regex.Replace(runsOn, @"\s+", " ").Trim();
        var at = text.IndexOf(Guard, StringComparison.Ordinal);
        if (at < 0) { return false; }
        if (Label().Matches(text).Any(m => m.Index < at)) { return false; }
        return Regex.IsMatch(text[(at + Guard.Length)..], @"^\s*\)*\s*&&");
    }

    static HashSet<string> Triggers(YamlMappingNode root)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        // YAML 1.1 can read the key "on" as a boolean; YamlDotNet keeps it a scalar, but accept both spellings.
        foreach (var (key, value) in root.Children)
        {
            if (key is not YamlScalarNode { Value: "on" or "true" or "True" }) { continue; }
            switch (value)
            {
                case YamlScalarNode scalar when scalar.Value is { } one: found.Add(one); break;
                case YamlSequenceNode sequence: foreach (var item in sequence.Children.OfType<YamlScalarNode>()) { found.Add(item.Value ?? ""); } break;
                case YamlMappingNode mapping: foreach (var trigger in mapping.Children.Keys.OfType<YamlScalarNode>()) { found.Add(trigger.Value ?? ""); } break;
            }
        }
        return found;
    }

    static string Text(YamlNode node) => node switch
    {
        YamlScalarNode scalar => scalar.Value ?? "",
        YamlSequenceNode sequence => string.Join(' ', sequence.Children.Select(Text)),
        YamlMappingNode mapping => string.Join(' ', mapping.Children.Select(p => $"{Text(p.Key)} {Text(p.Value)}")),
        _ => "",
    };
}
