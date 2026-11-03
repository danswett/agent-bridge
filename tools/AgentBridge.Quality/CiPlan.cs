using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace AgentBridge.Quality;

/// <summary>
/// ci.yml's wiring to the plan (quality/README.md §4.7), which only names tie together: the jobs
/// gated on the plan, the list the plan gives a true or false to, quality-gate's and publish-gate's
/// needs, and the suite ids steps look for in the plan's suites. A name that drifts in any of them
/// skips a job or a step silently, on main too, or leaves a job out of the gate (#487).
/// </summary>
static partial class CiPlan
{
    public const string Kind = "ci-plan";
    const string Plan = "plan";
    const string Gate = "quality-gate";
    const string PublishGate = "publish-gate";
    // The output the plan job sets, outside the selector, when the whole ring must run: it gates
    // nothing by its own name.
    const string WholeRing = "whole-ring";

    [GeneratedRegex(@"needs\.plan\.outputs\.([A-Za-z0-9_-]+)\s*==\s*'true'")]
    private static partial Regex GatedOn();
    [GeneratedRegex(@"contains\(\s*fromJSON\(\s*env\.SUITES\s*\)\s*,\s*'([^']*)'\s*\)")]
    private static partial Regex SuiteTest();
    // On main every gated job and step runs whatever the plan says, so a selection that misses
    // something is caught before a release publishes.
    [GeneratedRegex(@"github\.event_name\s*!=\s*'pull_request'\s*\|\|")]
    private static partial Regex MainAlways();
    [GeneratedRegex(@"needs\.quality-gate\.result\s*==\s*'success'")]
    private static partial Regex GatePassed();

    public static IReadOnlyList<Problem> Check(Catalog catalog, string yaml)
    {
        var problems = new List<Problem>();
        void Add(string message) => problems.Add(new(Kind, message));

        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root
            || Child(root, "jobs") is not YamlMappingNode jobMap)
        {
            Add("ci.yml has no jobs");
            return problems;
        }
        var jobs = jobMap.Children
            .Where(c => c.Key is YamlScalarNode { Value: not null } && c.Value is YamlMappingNode)
            .ToDictionary(c => ((YamlScalarNode)c.Key).Value!, c => (YamlMappingNode)c.Value, StringComparer.Ordinal);
        if (!jobs.TryGetValue(Plan, out var plan))
        {
            Add("ci.yml has no plan job");
            return problems;
        }

        // Jobs gated on the plan: an if: that reads the plan's true or false for this job.
        var gated = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (id, job) in jobs)
        {
            var condition = Scalar(job, "if") ?? "";
            var outputs = GatedOn().Matches(condition).Select(m => m.Groups[1].Value).Where(o => o != WholeRing).Distinct().ToList();
            if (outputs.Count == 0)
                continue;
            gated.Add(id);
            foreach (var other in outputs.Where(o => o != id))
                Add($"ci.yml job {id} is gated on the plan's {other} output, not its own, so quality-gate can't tell whether it had to run");
            if (!outputs.Contains(id))
                continue;
            if (!MainAlways().IsMatch(condition))
                Add($"ci.yml job {id} runs only when the plan selects it, even on main: its if: must start with github.event_name != 'pull_request' ||");
        }

        // The list the plan step gives a true or false to, and the plan job's outputs for each.
        var select = Steps(plan).FirstOrDefault(s => Scalar(s, "id") == "select");
        var listed = (select is null ? null : Child(select, "env") is YamlMappingNode env ? Scalar(env, "JOBS") : null)
            ?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        if (listed is null)
            Add("ci.yml plan has no select step with env JOBS, the jobs the plan gives a true or false to");
        else
        {
            foreach (var id in gated.Where(g => !listed.Contains(g)))
                Add($"ci.yml job {id} is gated on the plan, but the plan's JOBS doesn't list it, so it would never run on a pull request");
            foreach (var id in listed.Where(l => !gated.Contains(l)).Order(StringComparer.Ordinal))
                Add($"ci.yml plan's JOBS lists {id}, but no job of that name is gated on it");
        }
        var planOutputs = Child(plan, "outputs") as YamlMappingNode;
        foreach (var id in gated.Append(WholeRing).Where(o => planOutputs is null || Child(planOutputs, o) is null))
            Add($"ci.yml plan has no output {id}");

        // quality-gate: every job that needs the plan, so none can fail, or be skipped when it had
        // to run, without failing the gate.
        if (!jobs.TryGetValue(Gate, out var gate))
            Add($"ci.yml has no {Gate} job");
        else
        {
            var gateNeeds = Needs(gate);
            if (!gateNeeds.Contains(Plan))
                Add($"ci.yml {Gate} doesn't need plan, so a failed plan would skip every job and pass the gate");
            // A job downstream of the gate (publish-gate, checked below) can't be in its needs.
            foreach (var (id, job) in jobs.Where(j => j.Key is not (Gate or PublishGate) && !Needs(j.Value).Contains(Gate) && (gated.Contains(j.Key) || Needs(j.Value).Contains(Plan))))
                if (!gateNeeds.Contains(id))
                    Add($"ci.yml job {id} needs the plan but isn't in {Gate}'s needs, so its result wouldn't count");
        }

        // publish-gate stands on quality-gate rather than a second copy of its needs (#512).
        if (jobs.TryGetValue(PublishGate, out var publishGate))
        {
            if (!Needs(publishGate).Contains(Gate))
                Add($"ci.yml {PublishGate} doesn't need {Gate}, so a release could publish past a failed check");
            if (!GatePassed().IsMatch(Scalar(publishGate, "if") ?? ""))
                Add($"ci.yml {PublishGate}'s if: doesn't require needs.{Gate}.result == 'success'");
        }

        // Steps that run when a suite is selected: the suite must exist, say it runs in this job,
        // and the step must run on main whatever the plan selected.
        var suites = catalog.Suites.ToDictionary(s => s.Id, StringComparer.Ordinal);
        foreach (var (id, job) in jobs)
            foreach (var step in Steps(job))
            {
                var condition = Scalar(step, "if") ?? "";
                var ids = SuiteTest().Matches(condition).Select(m => m.Groups[1].Value).Distinct().ToList();
                if (ids.Count == 0)
                    continue;
                var name = Scalar(step, "name") ?? Scalar(step, "id") ?? Scalar(step, "uses") ?? "(unnamed)";
                foreach (var suiteId in ids)
                {
                    if (!suites.TryGetValue(suiteId, out var suite))
                        Add($"ci.yml job {id} step \"{name}\" runs when suite {suiteId} is selected, but the catalog has no such suite");
                    else if (!RunsIn(suite, id))
                        Add($"ci.yml job {id} step \"{name}\" runs when suite {suiteId} is selected, but that suite's where: doesn't name ci.yml {id}");
                }
                if (!MainAlways().IsMatch(condition))
                    Add($"ci.yml job {id} step \"{name}\" runs only when its suites are selected, even on main: its if: must include github.event_name != 'pull_request' ||");
            }

        return problems;
    }

    static bool RunsIn(Suite suite, string job) =>
        !string.IsNullOrWhiteSpace(suite.Where)
        && Completeness.ParseWhere(suite.Where).Any(w => w.File == "ci.yml" && (w.Jobs.Count == 0 || w.Jobs.Contains(job, StringComparer.Ordinal)));

    static YamlNode? Child(YamlMappingNode map, string key) =>
        map.Children.TryGetValue(new YamlScalarNode(key), out var value) ? value : null;

    static string? Scalar(YamlMappingNode map, string key) => (Child(map, key) as YamlScalarNode)?.Value;

    static IEnumerable<YamlMappingNode> Steps(YamlMappingNode job) =>
        Child(job, "steps") is YamlSequenceNode steps ? steps.Children.OfType<YamlMappingNode>() : [];

    static HashSet<string> Needs(YamlMappingNode job) => Child(job, "needs") switch
    {
        YamlScalarNode { Value: { } one } => new(StringComparer.Ordinal) { one },
        YamlSequenceNode many => many.Children.OfType<YamlScalarNode>().Select(n => n.Value!).ToHashSet(StringComparer.Ordinal),
        _ => new(StringComparer.Ordinal),
    };
}
