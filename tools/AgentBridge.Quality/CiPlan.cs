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
    // Jobs that deliver rather than check, so quality-gate doesn't wait on them.
    static readonly HashSet<string> Delivery = new(StringComparer.Ordinal) { PublishGate, "publish", "attest", "relay-image-sarif" };

    [GeneratedRegex(@"needs\.plan\.outputs\.([A-Za-z0-9_-]+)\s*==\s*'true'")]
    private static partial Regex GatedOn();
    [GeneratedRegex(@"contains\(\s*fromJSON\(\s*env\.SUITES\s*\)\s*,\s*'([^']*)'\s*\)")]
    private static partial Regex SuiteTest();
    [GeneratedRegex(@"^needs\.quality-gate\.result == 'success'$")]
    private static partial Regex GatePassed();
    // The only shape a suite-gated step's if: may take: guards that hold on main too (the runner's
    // OS, not cancelled), then a disjunction of exactly the main and whole-ring bypasses and the
    // suite tests. A substring check passed `... || contains(...)) && contains(...)`, which main
    // would skip whenever the second suite wasn't selected (#523's review).
    [GeneratedRegex(@"^(?:runner\.os == '[A-Za-z]+'|!cancelled\(\)) && ")]
    private static partial Regex Guard();
    [GeneratedRegex(@"^contains\(fromJSON\(env\.SUITES\), '[^']*'\)$")]
    private static partial Regex SuiteTerm();
    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    const string MainBypass = "github.event_name != 'pull_request'";
    const string WholeRingBypass = "env.WHOLE_RING == 'true'";
    static string GatedJobIf(string id) =>
        $"github.event_name != 'pull_request' || needs.plan.outputs.whole-ring == 'true' || needs.plan.outputs.{id} == 'true'";

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

        // Jobs gated on the plan: an if: that reads the plan's true or false for a job. Exactly one
        // shape, so main and the whole ring run it whatever the plan says, and quality-gate can tell
        // from the job's own output whether it had to run.
        var gated = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (id, job) in jobs)
        {
            var condition = Expression(Scalar(job, "if") ?? "");
            if (!GatedOn().Matches(condition).Any(m => m.Groups[1].Value != WholeRing))
                continue;
            gated.Add(id);
            if (condition != GatedJobIf(id))
                Add($"ci.yml job {id} is gated on the plan, but its if: isn't exactly: {GatedJobIf(id)}");
        }

        // The list the plan step gives a true or false to, and the plan job's outputs for each,
        // bound to the step that computes them: a typo in a binding exports an empty value, which
        // the job and quality-gate would both read as not selected.
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
        if (!Steps(plan).Any(s => Scalar(s, "id") == WholeRing))
            Add($"ci.yml plan has no step with id {WholeRing}, which decides the whole ring apart from the selector");
        var planOutputs = Child(plan, "outputs") as YamlMappingNode;
        var bindings = gated.Select(id => (Output: id, Binding: $"${{{{ steps.select.outputs.{id} }}}}"))
            .Append((WholeRing, $"${{{{ steps.{WholeRing}.outputs.{WholeRing} }}}}"));
        foreach (var (output, binding) in bindings)
        {
            var actual = planOutputs is null ? null : Scalar(planOutputs, output);
            if (actual is null)
                Add($"ci.yml plan has no output {output}");
            else if (Normalize(actual) != binding)
                Add($"ci.yml plan's output {output} is {actual.Trim()}, not {binding}");
        }

        // quality-gate: every job that needs the plan, so none can fail, or be skipped when it had
        // to run, without failing the gate.
        if (!jobs.TryGetValue(Gate, out var gate))
            Add($"ci.yml has no {Gate} job");
        else
        {
            var gateNeeds = Needs(gate);
            if (!gateNeeds.Contains(Plan))
                Add($"ci.yml {Gate} doesn't need plan, so a failed plan would skip every job and pass the gate");
            // Every check job, whether it reads the plan or not. Only the delivery jobs, which run
            // on main where a superseded publish is cancelled by design, stay out; a check placed
            // downstream of the gate (needs: quality-gate) could fail after it passed, so it's
            // reported too.
            foreach (var (id, job) in jobs.Where(j => j.Key is not (Gate or Plan) && !Delivery.Contains(j.Key)))
                if (!gateNeeds.Contains(id))
                    Add($"ci.yml job {id} isn't in {Gate}'s needs, so its result wouldn't count (only {string.Join(", ", Delivery.Order(StringComparer.Ordinal))} may stay out)");
        }

        // publish-gate stands on quality-gate rather than a second copy of its needs (#512).
        if (jobs.TryGetValue(PublishGate, out var publishGate))
        {
            if (!Needs(publishGate).Contains(Gate))
                Add($"ci.yml {PublishGate} doesn't need {Gate}, so a release could publish past a failed check");
            // One of the conditions joined by &&, with no || that could route around it.
            var publishIf = Expression(Scalar(publishGate, "if") ?? "");
            if (publishIf.Contains("||", StringComparison.Ordinal)
                || !publishIf.Split("&&").Any(t => GatePassed().IsMatch(t.Trim())))
                Add($"ci.yml {PublishGate}'s if: doesn't require needs.{Gate}.result == 'success' (as one of its && conditions, with no ||)");
        }

        // Steps that run when a suite is selected: the suite must exist, say it runs in this job,
        // and the step must run on main and in the whole ring whatever the plan selected, so its
        // job must hand it the plan's whole-ring output.
        var suites = catalog.Suites.ToDictionary(s => s.Id, StringComparer.Ordinal);
        foreach (var (id, job) in jobs)
        {
            var stepsTestingSuites = false;
            foreach (var step in Steps(job))
            {
                var condition = Scalar(step, "if") ?? "";
                var ids = SuiteTest().Matches(condition).Select(m => m.Groups[1].Value).Distinct().ToList();
                if (ids.Count == 0)
                    continue;
                stepsTestingSuites = true;
                var name = Scalar(step, "name") ?? Scalar(step, "id") ?? Scalar(step, "uses") ?? "(unnamed)";
                foreach (var suiteId in ids)
                {
                    if (!suites.TryGetValue(suiteId, out var suite))
                        Add($"ci.yml job {id} step \"{name}\" runs when suite {suiteId} is selected, but the catalog has no such suite");
                    else if (!RunsIn(suite, id))
                        Add($"ci.yml job {id} step \"{name}\" runs when suite {suiteId} is selected, but that suite's where: doesn't name ci.yml {id}");
                }
                if (!BypassesSelection(condition))
                    Add($"ci.yml job {id} step \"{name}\" must run on main and in the whole ring whatever the plan selected: " +
                        $"its if: must be [runner.os == '...' && | !cancelled() && ](github.event_name != 'pull_request' || env.WHOLE_RING == 'true' || contains(fromJSON(env.SUITES), '...') || ...)");
            }
            var wholeRingEnv = Child(job, "env") is YamlMappingNode jobEnv ? Scalar(jobEnv, "WHOLE_RING") : null;
            if (stepsTestingSuites && Normalize(wholeRingEnv ?? "") != $"${{{{ needs.plan.outputs.{WholeRing} }}}}")
                Add($"ci.yml job {id} has steps that run when a suite is selected, but its env WHOLE_RING isn't ${{{{ needs.plan.outputs.{WholeRing} }}}}");
        }

        return problems;
    }

    /// <summary>
    /// Whether a suite-gated step's if: is guards that hold on main too, then a disjunction of
    /// exactly the main bypass, the whole-ring bypass and suite tests. Nothing else may sit
    /// between them: a substring check passed `(main || contains(a)) && contains(b)`, which main
    /// would skip whenever b wasn't selected (#523's review).
    /// </summary>
    static bool BypassesSelection(string condition)
    {
        var expression = Expression(condition);
        var guarded = false;
        for (var guard = Guard().Match(expression); guard.Success; guard = Guard().Match(expression))
        {
            expression = expression[guard.Length..];
            guarded = true;
        }
        if (expression.StartsWith('(') && expression.EndsWith(')'))
            expression = expression[1..^1];
        else if (guarded)
            return false;
        var terms = expression.Split("||").Select(t => t.Trim()).ToList();
        return terms.Contains(MainBypass) && terms.Contains(WholeRingBypass) && terms.Any(t => SuiteTerm().IsMatch(t))
            && terms.All(t => t is MainBypass or WholeRingBypass || SuiteTerm().IsMatch(t));
    }

    /// <summary>A value as written in YAML, with runs of whitespace collapsed.</summary>
    static string Normalize(string value) =>
        Spaces().Replace(value, " ").Trim().Replace("( ", "(", StringComparison.Ordinal).Replace(" )", ")", StringComparison.Ordinal);

    /// <summary>An if: as an expression: normalized, and without the optional ${{ }} around it.</summary>
    static string Expression(string condition)
    {
        var text = Normalize(condition);
        return text.StartsWith("${{", StringComparison.Ordinal) && text.EndsWith("}}", StringComparison.Ordinal) ? text[3..^2].Trim() : text;
    }

    // Named explicitly: a bare "ci.yml" covers every job for the completeness check, but the plan
    // gates jobs by exact name (Program.JobsOf lists it as "*"), so it would select no job (#523).
    static bool RunsIn(Suite suite, string job) =>
        !string.IsNullOrWhiteSpace(suite.Where)
        && Completeness.ParseWhere(suite.Where).Any(w => w.File == "ci.yml" && w.Jobs.Contains(job, StringComparer.Ordinal));

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
