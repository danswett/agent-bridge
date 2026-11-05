using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace AgentBridge.Quality;

/// <summary>
/// ci.yml's wiring to the plan (quality/README.md §4.7), which only names tie together: the jobs
/// gated on the plan, the list the plan gives a true or false to, quality-gate's and publish-gate's
/// needs, and the suite ids steps look for in the plan's suites. A name that drifts in any of them
/// skips a job or a step silently, in the whole ring too, or leaves a job out of the gate (#487).
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
    // Jobs that deliver rather than check, so quality-gate doesn't wait on them, each with the one
    // job it follows.
    static readonly Dictionary<string, string[]> DeliveryNeeds = new(StringComparer.Ordinal)
    {
        [PublishGate] = [Gate],
        ["publish"] = [PublishGate],
        ["attest"] = ["publish"],
        ["relay-image-sarif"] = ["product"],
    };
    static readonly HashSet<string> Delivery = new(DeliveryNeeds.Keys, StringComparer.Ordinal);
    static readonly string[] ProductRunners = ["ubuntu-latest", "windows-latest", "windows-11-arm"];
    static readonly string ProductOsBinding =
        $"${{{{ steps.{WholeRing}.outputs.{WholeRing} == 'true' && '[{string.Join(",", ProductRunners.Select(r => $"\"{r}\""))}]' || steps.select.outputs.product-os }}}}";
    const string ProductMatrix = "${{ fromJSON(needs.plan.outputs.product-os) }}";

    // Any read of the plan in a job's if:, in either property syntax and with or without a
    // comparison: a bare needs.plan.outputs.x once escaped a match that wanted "== 'true'" (#523).
    [GeneratedRegex(@"needs\s*(?:\.\s*plan\b|\[\s*['""]plan['""]\s*\])", RegexOptions.IgnoreCase)]
    private static partial Regex ReadsPlan();
    // Actions' function and context names are case-insensitive, so fromJson(ENV.suites) is the
    // same test: found in any case here, then held to the canonical spelling by the shape check.
    [GeneratedRegex(@"contains\(\s*fromJSON\(\s*env\.SUITES\s*\)\s*,\s*'([^']*)'\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex SuiteTest();
    // Any read of the plan's suites at all, however it's spelled, makes a step suite-gated.
    [GeneratedRegex(@"env\s*(?:\.\s*suites\b|\[\s*['""]suites['""]\s*\])", RegexOptions.IgnoreCase)]
    private static partial Regex ReadsSuites();
    [GeneratedRegex(@"^needs\.quality-gate\.result == 'success'$")]
    private static partial Regex GatePassed();
    [GeneratedRegex(@"^needs\.quality-gate\.outputs\.app == 'success'$")]
    private static partial Regex AppBuilt();
    const string AppResult = "${{ needs.app.result }}";
    const string ConcurrencyGroup =
        "${{ github.event_name == 'push' && github.ref == 'refs/heads/main' && github.run_attempt == '1' && 'ci-main' || format('{0}-{1}', github.workflow, github.event_name == 'pull_request' && format('pr-{0}', github.event.pull_request.number) || github.run_id) }}";
    const string CancelInProgress = "${{ github.event_name == 'pull_request' }}";
    // The only shape a suite-gated step's if: may take: guards that hold in the whole ring too
    // (the runner's OS, not cancelled), then a disjunction of exactly the whole-ring bypass and the
    // suite tests. A substring check passed `... || contains(...)) && contains(...)`, which the
    // whole ring would skip whenever the second suite wasn't selected (#523's review).
    // At most one of each, and only an OS a runner has: runner.os == 'Windows' && runner.os ==
    // 'Linux', or a misspelt OS, would never run the step anywhere.
    [GeneratedRegex(@"^(?:(?<os>runner\.os == '(?<name>Windows|Linux|macOS)')|(?<live>!cancelled\(\))) && ")]
    private static partial Regex Guard();
    [GeneratedRegex(@"^\$\{\{ matrix\.([A-Za-z0-9_-]+) \}\}$")]
    private static partial Regex MatrixValue();
    [GeneratedRegex(@"\b(?:always|cancelled|failure|success)\s*\(", RegexOptions.IgnoreCase)]
    private static partial Regex StatusFunction();
    [GeneratedRegex(@"^contains\(fromJSON\(env\.SUITES\), '[^']*'\)$")]
    private static partial Regex SuiteTerm();
    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    // Main is planned like a pull request (quality/README.md §4.7): only the whole ring, which the
    // plan decides apart from the selector, runs a gated job or step whatever the selector says.
    const string WholeRingBypass = "env.WHOLE_RING == 'true'";
    static string GatedJobIf(string id) =>
        $"needs.plan.outputs.whole-ring == 'true' || needs.plan.outputs.{id} == 'true'";

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

        // Main's push runs coalesce in ci-main, never cancelled once running, and only first
        // attempts join it: a re-run there could take a waiting push run's place, so that push's
        // commit would go untested (quality/README.md §4.7).
        if (Child(root, "concurrency") is not YamlMappingNode concurrency
            || Normalize(Scalar(concurrency, "group") ?? "") != ConcurrencyGroup
            || Normalize(Scalar(concurrency, "cancel-in-progress") ?? "") != CancelInProgress)
            Add($"ci.yml's concurrency isn't group {ConcurrencyGroup} with cancel-in-progress {CancelInProgress}");

        // Jobs gated on the plan: an if: that reads the plan's true or false for a job. Exactly one
        // shape, so the whole ring runs it whatever the plan says, and quality-gate can tell from
        // the job's own output whether it had to run.
        var gated = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (id, job) in jobs)
        {
            var condition = Expression(Scalar(job, "if") ?? "");
            if (!ReadsPlan().IsMatch(condition))
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
            .Append(("suites", "${{ steps.select.outputs.suites }}"))
            .Append((WholeRing, $"${{{{ steps.{WholeRing}.outputs.{WholeRing} }}}}"));
        foreach (var (output, binding) in bindings)
        {
            var actual = planOutputs is null ? null : Scalar(planOutputs, output);
            if (actual is null)
                Add($"ci.yml plan has no output {output}");
            else if (Normalize(actual) != binding)
                Add($"ci.yml plan's output {output} is {actual.Trim()}, not {binding}");
        }

        // product's runners: in the whole ring all three, whatever the selector says,
        // or its Windows-only steps (the AOT checks, the build scripts) would have no runner.
        if (jobs.TryGetValue("product", out var product))
        {
            var productOs = planOutputs is null ? null : Scalar(planOutputs, "product-os");
            if (Normalize(productOs ?? "") != ProductOsBinding)
                Add($"ci.yml plan's output product-os isn't {ProductOsBinding}");
            var matrix = Child(product, "strategy") is YamlMappingNode strategy ? Child(strategy, "matrix") as YamlMappingNode : null;
            if (Normalize((matrix is null ? null : Scalar(matrix, "os")) ?? "") != ProductMatrix)
                Add($"ci.yml job product's strategy.matrix.os isn't {ProductMatrix}");
        }

        // A delivery job stays out of quality-gate's needs by name, so each is pinned to its place:
        // a check job renamed to one, needing the plan, would otherwise drop out of the gate.
        foreach (var (id, expected) in DeliveryNeeds)
            if (jobs.TryGetValue(id, out var delivery))
            {
                if (!Needs(delivery).SetEquals(expected))
                    Add($"ci.yml delivery job {id} must need exactly [{string.Join(", ", expected)}]");
                if (gated.Contains(id))
                    Add($"ci.yml delivery job {id} reads the plan, as only a check job does");
            }

        // quality-gate: every job that needs the plan, so none can fail, or be skipped when it had
        // to run, without failing the gate.
        if (!jobs.TryGetValue(Gate, out var gate))
            Add($"ci.yml has no {Gate} job");
        else
        {
            // Without always(), the implicit success() skips the gate when a check fails, and a
            // skipped required check counts as passed.
            if (Expression(Scalar(gate, "if") ?? "") != "always()")
                Add($"ci.yml {Gate}'s if: isn't always(), so a failed check would skip the gate, and a skipped required check passes");
            var gateNeeds = Needs(gate);
            if (!gateNeeds.Contains(Plan))
                Add($"ci.yml {Gate} doesn't need plan, so a failed plan would skip every job and pass the gate");
            // Every check job, whether it reads the plan or not. Only the delivery jobs, which run
            // on main after the gate, stay out; a check placed
            // downstream of the gate (needs: quality-gate) could fail after it passed, so it's
            // reported too.
            foreach (var (id, job) in jobs.Where(j => j.Key is not (Gate or Plan) && !Delivery.Contains(j.Key)))
                if (!gateNeeds.Contains(id))
                    Add($"ci.yml job {id} isn't in {Gate}'s needs, so its result wouldn't count (only {string.Join(", ", Delivery.Order(StringComparer.Ordinal))} may stay out)");
            // What publish-gate reads to tell the app was built and checked in this run.
            var gateOutputs = Child(gate, "outputs") as YamlMappingNode;
            if (Normalize((gateOutputs is null ? null : Scalar(gateOutputs, "app")) ?? "") != AppResult)
                Add($"ci.yml {Gate}'s output app isn't exactly {AppResult}, so {PublishGate} can't tell this run built the app");
        }

        // publish-gate stands on quality-gate rather than a second copy of its needs (#512), and
        // publish stands on publish-gate: without either link a release ships past a red check.
        if (!jobs.TryGetValue(PublishGate, out var publishGate))
            Add($"ci.yml has no {PublishGate} job, so nothing holds a release to {Gate}");
        else
        {
            // A top-level conjunct, with no || anywhere that could route around it: a split on &&
            // also matched one inside !(false && ... && false), which is always true. And a single
            // expression: ${{ }} mixed with text is a string template, and any such string is true.
            var publishIf = Expression(Scalar(publishGate, "if") ?? "");
            if (publishIf.Contains("||", StringComparison.Ordinal) || IsTemplate(publishIf)
                || !TopLevelConjuncts(publishIf).Any(t => GatePassed().IsMatch(t)))
                Add($"ci.yml {PublishGate}'s if: doesn't require needs.{Gate}.result == 'success' (as one of its top-level && conditions, with no || and no ${{{{ }}}} inside)");
            // On main the app job runs only when the plan selected it, and a run that skipped it
            // has no packages: the gate passing isn't enough to publish.
            else if (!TopLevelConjuncts(publishIf).Any(t => AppBuilt().IsMatch(t)))
                Add($"ci.yml {PublishGate}'s if: doesn't require needs.{Gate}.outputs.app == 'success' (as one of its top-level && conditions), so a run that skipped the app would publish");
        }
        if (jobs.TryGetValue("publish", out var publish))
        {
            // No status function: then the implicit success() holds publish to publish-gate's.
            var publishCondition = Expression(Scalar(publish, "if") ?? "");
            if (publishCondition.Contains("||", StringComparison.Ordinal) || IsTemplate(publishCondition)
                || StatusFunction().IsMatch(publishCondition))
                Add($"ci.yml publish's if: has a ||, ${{{{ }}}} inside or a status function, so it could run without {PublishGate} succeeding");
        }

        // Steps that run when a suite is selected: the suite must exist, say it runs in this job,
        // and the step must run in the whole ring whatever the plan selected, so its
        // job must hand it the plan's whole-ring output.
        var suites = catalog.Suites.ToDictionary(s => s.Id, StringComparer.Ordinal);
        foreach (var (id, job) in jobs)
        {
            var stepsTestingSuites = false;
            foreach (var step in Steps(job))
            {
                var condition = Scalar(step, "if") ?? "";
                // Reading the plan's suites by any route (needs.plan.outputs.suites directly too)
                // holds a step to the one shape.
                if (!ReadsSuites().IsMatch(condition) && !ReadsPlan().IsMatch(condition))
                    continue;
                var ids = SuiteTest().Matches(condition).Select(m => m.Groups[1].Value).Distinct().ToList();
                stepsTestingSuites = true;
                var name = Scalar(step, "name") ?? Scalar(step, "id") ?? Scalar(step, "uses") ?? "(unnamed)";
                foreach (var suiteId in ids)
                {
                    if (!suites.TryGetValue(suiteId, out var suite))
                        Add($"ci.yml job {id} step \"{name}\" runs when suite {suiteId} is selected, but the catalog has no such suite");
                    else if (!RunsIn(suite, id))
                        Add($"ci.yml job {id} step \"{name}\" runs when suite {suiteId} is selected, but that suite's where: doesn't name ci.yml {id}");
                }
                if (!BypassesSelection(condition, JobOses(job)))
                    Add($"ci.yml job {id} step \"{name}\" must run in the whole ring whatever the plan selected: " +
                        $"its if: must be [runner.os == 'Windows|Linux|macOS' && ][!cancelled() && ](env.WHOLE_RING == 'true' || contains(fromJSON(env.SUITES), '...') || ...)");
            }
            var jobEnvMap = Child(job, "env") as YamlMappingNode;
            if (stepsTestingSuites)
                foreach (var (key, output) in new[] { ("WHOLE_RING", WholeRing), ("SUITES", "suites") })
                {
                    var binding = $"${{{{ needs.plan.outputs.{output} }}}}";
                    if (Normalize((jobEnvMap is null ? null : Scalar(jobEnvMap, key)) ?? "") != binding)
                        Add($"ci.yml job {id} has steps that run when a suite is selected, but its env {key} isn't {binding}");
                }
        }

        // The plan's suites and whole-ring reach steps only as SUITES and WHOLE_RING: under another
        // name a step could test them without the checks above seeing it.
        var envs = new List<(string Where, YamlMappingNode Env, bool Step)>();
        if (Child(root, "env") is YamlMappingNode workflowEnv)
            envs.Add(("ci.yml env", workflowEnv, false));
        // Not the plan's own steps: its WHOLE_RING comes from its whole-ring step, not from needs.
        foreach (var (id, job) in jobs.Where(j => j.Key != Plan))
        {
            if (Child(job, "env") is YamlMappingNode jobEnv)
                envs.Add(($"ci.yml job {id} env", jobEnv, false));
            foreach (var step in Steps(job))
                if (Child(step, "env") is YamlMappingNode stepEnv)
                    envs.Add(($"ci.yml job {id} step \"{Scalar(step, "name") ?? Scalar(step, "id") ?? "(unnamed)"}\" env", stepEnv, true));
        }
        foreach (var (where, envMap, isStep) in envs)
            foreach (var (keyNode, valueNode) in envMap.Children)
            {
                var key = (keyNode as YamlScalarNode)?.Value ?? "";
                var value = (valueNode as YamlScalarNode)?.Value ?? "";
                foreach (var (output, allowed) in new[] { ("suites", "SUITES"), (WholeRing, "WHOLE_RING") })
                    if (ReadsOutput(output).IsMatch(value) && key != allowed)
                        Add($"{where} {key} reads the plan's {output}, which steps may read only as {allowed}");
                // A step's env wins over its job's, and env names are matched in any case in
                // expressions, so a step could hand itself SUITES: '[]' and skip on a ready PR; a
                // second spelling beside the job's would leave which one counts unclear.
                var upper = key.ToUpperInvariant();
                if (upper is "SUITES" or "WHOLE_RING" && (isStep || key != upper))
                    Add($"{where} sets {key}, which only a job's env may set, as {upper}, from the plan");
            }

        return problems;
    }

    static Regex ReadsOutput(string output) =>
        new($@"needs\s*(?:\.\s*plan|\[\s*['""]plan['""]\s*\])\s*(?:\.\s*outputs|\[\s*['""]outputs['""]\s*\])\s*(?:\.\s*{Regex.Escape(output)}\b|\[\s*['""]{Regex.Escape(output)}['""]\s*\])", RegexOptions.IgnoreCase);

    // ${{ }} left inside an if: after unwrapping makes it a string template, which is always true.
    static bool IsTemplate(string expression) =>
        expression.Contains("${{", StringComparison.Ordinal) || expression.Contains("}}", StringComparison.Ordinal);

    /// <summary>
    /// Whether a suite-gated step's if: is guards that hold in the whole ring too, then a
    /// disjunction of exactly the whole-ring bypass and suite tests. Nothing else may sit between
    /// them: a substring check passed `(whole || contains(a)) && contains(b)`, which the whole
    /// ring would skip whenever b wasn't selected (#523's review), and a leftover main bypass
    /// would run the step on every push to main again.
    /// </summary>
    static bool BypassesSelection(string condition, IReadOnlySet<string>? jobOses)
    {
        var expression = Expression(condition);
        var guarded = false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var guard = Guard().Match(expression); guard.Success; guard = Guard().Match(expression))
        {
            if (!seen.Add(guard.Groups["os"].Success ? "os" : "live"))
                return false;
            // An OS the job never runs on would skip the step everywhere.
            if (guard.Groups["os"].Success && jobOses is not null && !jobOses.Contains(guard.Groups["name"].Value))
                return false;
            expression = expression[guard.Length..];
            guarded = true;
        }
        if (expression.StartsWith('(') && expression.EndsWith(')'))
            expression = expression[1..^1];
        else if (guarded)
            return false;
        var terms = expression.Split("||").Select(t => t.Trim()).ToList();
        return terms.Contains(WholeRingBypass) && terms.Any(t => SuiteTerm().IsMatch(t))
            && terms.All(t => t is WholeRingBypass || SuiteTerm().IsMatch(t));
    }

    /// <summary>A value as written in YAML, with runs of whitespace collapsed.</summary>
    static string Normalize(string value) =>
        Spaces().Replace(value, " ").Trim().Replace("( ", "(", StringComparison.Ordinal).Replace(" )", ")", StringComparison.Ordinal);

    /// <summary>
    /// The OSes a job runs on (Windows, Linux, macOS), from its runs-on or the matrix values it
    /// names; product's matrix, pinned above, is all three runners. Null when it can't be told.
    /// </summary>
    static HashSet<string>? JobOses(YamlMappingNode job)
    {
        var runsOn = Scalar(job, "runs-on");
        if (runsOn is null)
            return null;
        var label = Normalize(runsOn);
        var labels = new List<string>();
        var fromMatrix = MatrixValue().Match(label);
        if (!fromMatrix.Success)
        {
            if (IsTemplate(label))
                return null;
            labels.Add(label);
        }
        else
        {
            var key = fromMatrix.Groups[1].Value;
            var matrix = Child(job, "strategy") is YamlMappingNode strategy ? Child(strategy, "matrix") as YamlMappingNode : null;
            if (matrix is null)
                return null;
            switch (Child(matrix, key))
            {
                case YamlSequenceNode values:
                    labels.AddRange(values.Children.OfType<YamlScalarNode>().Select(v => v.Value ?? ""));
                    break;
                case YamlScalarNode value when Normalize(value.Value ?? "") == ProductMatrix:
                    labels.AddRange(ProductRunners);
                    break;
                case YamlScalarNode:
                    return null;
            }
            if (Child(matrix, "include") is YamlSequenceNode include)
                labels.AddRange(include.Children.OfType<YamlMappingNode>().Select(i => Scalar(i, key)).OfType<string>());
        }
        if (labels.Count == 0)
            return null;
        var oses = new HashSet<string>(StringComparer.Ordinal);
        foreach (var l in labels)
        {
            var os = l.Contains("windows", StringComparison.OrdinalIgnoreCase) ? "Windows"
                : l.Contains("ubuntu", StringComparison.OrdinalIgnoreCase) ? "Linux"
                : l.Contains("macos", StringComparison.OrdinalIgnoreCase) ? "macOS" : null;
            if (os is null)
                return null;
            oses.Add(os);
        }
        return oses;
    }

    /// <summary>
    /// The terms joined by && outside any parentheses or quoted string, so a clause inside a
    /// negation or a call isn't taken for one the whole expression requires.
    /// </summary>
    static IEnumerable<string> TopLevelConjuncts(string expression)
    {
        var depth = 0;
        var quoted = false;
        var start = 0;
        for (var i = 0; i < expression.Length; i++)
        {
            var c = expression[i];
            if (c == '\'')
                quoted = !quoted;
            else if (quoted)
                continue;
            else if (c == '(')
                depth++;
            else if (c == ')')
                depth--;
            else if (depth == 0 && c == '&' && i + 1 < expression.Length && expression[i + 1] == '&')
            {
                yield return expression[start..i].Trim();
                start = i + 2;
                i++;
            }
        }
        yield return expression[start..].Trim();
    }

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
