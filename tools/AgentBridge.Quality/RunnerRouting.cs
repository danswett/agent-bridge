using System.Text.Json;
using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace AgentBridge.Quality;

/// <summary>
/// The routing guard for the self-hosted <c>ab-ci</c> runners (quality/README.md, "CI: speed, runners and what gates
/// main"). agent-bridge is a public repository and those runners persist between jobs, so a job may take one only
/// for a push or a pull request whose head is this repository: never a fork, and never in a workflow that
/// <c>pull_request_target</c> or <c>workflow_run</c> can run. Each <c>runs-on</c> is read as a GitHub expression: every
/// value it can resolve to that may name a self-hosted runner must be reached only through both whole conjuncts of
/// <see cref="Guard"/>, compared after normalizing spacing and parentheses. A runner group, a bare self-hosted label,
/// or a runner named by something the workflow doesn't hold (a variable, an input, the event) is a violation. It fails
/// closed (R8b, Quality B221/B222 on #3831): a value is hosted only when it resolves to a known hosted label, a matrix
/// only when every value (include: too) does, or it is the one named key, ci.yml product's product-os, whose values
/// CiRunnerResolutionTests pins to <see cref="ProductRunners"/>. The self-hosted value itself must be a literal.
/// </summary>
static partial class RunnerRouting
{
    // The whole guard, as quality/README.md gives it. The repository variable lets one setting flip the pool off.
    internal const string Guard = "vars.AB_CI_SELF_HOSTED == 'true' && (github.event_name == 'push' || github.event.pull_request.head.repo.full_name == github.repository)";
    static readonly string[] Privileged = ["pull_request_target", "workflow_run"];
    // What plan.ps1 may put in ci.yml product's matrix (CiPlan pins the binding; the test pins plan.ps1 and CiPlan).
    internal static readonly string[] ProductRunners = ["ubuntu-latest", "windows-latest", "windows-11-arm"];
    const string ProductMatrix = "${{ fromJSON(needs.plan.outputs.product-os) }}";
    static readonly HashSet<string> Needed = [.. Conjuncts(Expr.Parse(Guard)).Select(c => c.ToString())];
    // The one alternative (R8b): a run by hand on main with use_ab_ci, to try the pool before the variable flips. Only
    // people with write access can dispatch, and only main's workflow runs. Exactly "(guard) || <this>", as one conjunct.
    internal const string Dispatch = "github.event_name == 'workflow_dispatch' && github.ref == 'refs/heads/main' && inputs.use_ab_ci";
    static readonly string GuardOrDispatch = Expr.Parse($"({Guard}) || {Dispatch}").ToString();

    // The labels only: not "--deny-self-hosted-runners", which names an option, not a runner.
    [GeneratedRegex(@"(?<![\w-])(ab-ci|self-hosted)(?![\w-])", RegexOptions.IgnoreCase)]
    private static partial Regex Label();
    [GeneratedRegex(@"^\s*\$\{\{(.*)\}\}\s*$", RegexOptions.Singleline)]
    private static partial Regex WholeExpression();
    // An allowlist of GitHub-hosted forms (R8b): ubuntu-*, windows-latest, windows-20xx, windows-11-arm, macos-*.
    // Anything else, "windows-x64" included, may be a self-hosted pool's label.
    [GeneratedRegex(@"^(ubuntu-[a-z0-9.-]+|windows-(latest|20\d\d|11-arm)|macos-[a-z0-9.-]+)$")]
    private static partial Regex Hosted();
    // What a matrix built by an expression may read and still count as literals: github.event_name, nothing else.
    [GeneratedRegex(@"\b(needs|vars|inputs|steps|env|secrets|matrix|jobs|strategy|runner)\.|github\.(?!event_name\b)")]
    private static partial Regex ContextRead();

    public static IReadOnlyList<string> Violations(string yaml, string file)
    {
        var stream = new YamlStream();
        // A file this check can't read (a key given twice, say) fails it: unread is never safe.
        try { stream.Load(new StringReader(yaml)); }
        catch (Exception e) when (e is YamlDotNet.Core.YamlException or ArgumentException) { return [$"{file} isn't YAML this check can read: {e.Message}"]; }
        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root) { return []; }
        if (!root.Children.TryGetValue(new YamlScalarNode("jobs"), out var jobsNode) || jobsNode is not YamlMappingNode map) { return []; }
        var jobs = map.Children.Where(p => p.Key is YamlScalarNode && p.Value is YamlMappingNode)
            .ToDictionary(p => ((YamlScalarNode)p.Key).Value!, p => (YamlMappingNode)p.Value, StringComparer.Ordinal);
        var privileged = Triggers(root).Intersect(Privileged).ToList();
        var problems = new List<string>();
        foreach (var (name, job) in jobs)
        {
            // Where a runner is chosen: the job's runs-on, and, for a reusable workflow's caller, each input that names the
            // pool (the callee's runs-on reads it as inputs.*, which it must guard in its own file).
            var routes = new List<YamlNode>();
            if (job.Children.TryGetValue(new YamlScalarNode("runs-on"), out var runsOn)) { routes.Add(runsOn); }
            if (job.Children.ContainsKey(new YamlScalarNode("uses")) && job.Children.TryGetValue(new YamlScalarNode("with"), out var with) && with is YamlMappingNode inputs)
            {
                routes.AddRange(inputs.Children.Values.Where(v => Label().IsMatch(Text(v))));
            }
            var whys = routes.Select(r => Unguarded(r, value => MaySelfHost(value, job, name, file))).OfType<string>().ToList();
            if (whys.Count == 0) { continue; }
            if (whys.FirstOrDefault(w => w.Length > 0) is { } why) { problems.Add($"{file} job {name} can run on a self-hosted runner without the guard ({Guard}) around it: {why}"); }
            foreach (var trigger in privileged) { problems.Add($"{file} job {name} can take a self-hosted runner in a workflow that a {trigger} can run"); }
        }
        return problems;
    }

    // null: never self-hosted. "": self-hosted only behind the whole guard. Otherwise why not.
    static string? Unguarded(YamlNode runsOn, Func<Expr, bool> maySelfHost)
    {
        if (runsOn is YamlMappingNode mapping)
        {
            if (mapping.Children.ContainsKey(new YamlScalarNode("group"))) { return "a runner group"; }
            return mapping.Children.TryGetValue(new YamlScalarNode("labels"), out var labels) ? Unguarded(labels, maySelfHost) : null;
        }
        if (runsOn is YamlSequenceNode sequence)
        {
            var each = sequence.Children.Select(item => Unguarded(item, maySelfHost)).Where(w => w is not null).ToList();
            return each.Count == 0 ? null : each.FirstOrDefault(w => w!.Length > 0) ?? "";
        }
        var text = (runsOn as YamlScalarNode)?.Value ?? "";
        if (!text.Contains("${{", StringComparison.Ordinal)) { return Hosted().IsMatch(text.Trim()) ? null : $"the label {text.Trim()}, not a known hosted one"; }
        var whole = WholeExpression().Match(text);
        Expr expr;
        try { expr = whole.Success && !whole.Groups[1].Value.Contains("${{", StringComparison.Ordinal) ? Expr.Parse(whole.Groups[1].Value) : throw new FormatException(); }
        catch (FormatException) { return $"{text.Trim()} isn't one expression this check can read"; }
        var reachable = Values(expr, []).Where(v => maySelfHost(v.Value)).ToList();
        // A label anywhere but the guarded value (tested, say) is no guard: fail.
        if (reachable.Count == 0) { return Label().IsMatch(text) ? $"{text.Trim()} names the pool, but not as the guarded value" : null; }
        var open = reachable.FirstOrDefault(v => !Needed.IsSubsetOf(v.Conditions) && !v.Conditions.Contains(GuardOrDispatch));
        if (open.Value is not null) { return $"{open.Value} is reached without it"; }
        // The whole shape: GUARD (with any narrowing && conditions) && <the one self-hosted value> || <one hosted fallback>.
        // A second ||, a second self-hosted arm or no hosted fallback is a violation even when each piece is guarded.
        var shaped = expr is Expr.Or { Arms: [Expr.And pool, var fallback] } && reachable.Count == 1
            && ReferenceEquals(reachable[0].Value, pool.Arms[^1]) && PoolLiteral(pool.Arms[^1]) && fallback is not (Expr.Or or Expr.And) && !maySelfHost(fallback);
        return shaped ? "" : $"{text.Trim()} isn't {Guard} && <self-hosted label> || <hosted label>";
    }

    // Every value an expression can resolve to, with the conjuncts that must all hold for it.
    static IEnumerable<(Expr Value, HashSet<string> Conditions)> Values(Expr expr, HashSet<string> conditions) => expr switch
    {
        Expr.Or or => or.Arms.SelectMany(arm => Values(arm, conditions)),
        Expr.And and => Values(and.Arms[^1], [.. conditions, .. and.Arms[..^1].SelectMany(Conjuncts).Select(c => c.ToString())]),
        _ => [(expr, conditions)],
    };

    static IEnumerable<Expr> Conjuncts(Expr expr) => expr is Expr.And and ? and.Arms.SelectMany(Conjuncts) : [expr];

    // The self-hosted value: a literal label, or fromJSON of a literal list naming one. Nothing computed.
    static bool PoolLiteral(Expr value) => value is Expr.Literal { Value: string s } && Label().IsMatch(s)
        || value is Expr.Call { Name: var n, Args: [Expr.Literal { Value: string json }] } && n.Equals("fromJSON", StringComparison.OrdinalIgnoreCase) && Label().IsMatch(json);

    // Whether a value may be anything but a known hosted label. Unknown, unresolved or computed: yes (fail closed).
    static bool MaySelfHost(Expr value, YamlMappingNode job, string name, string file) => value switch
    {
        Expr.Literal { Value: string s } => !Hosted().IsMatch(s),
        Expr.Literal or Expr.Compare or Expr.Not => false,
        Expr.Call { Name: var n, Args: [Expr.Literal { Value: string json }] } when n.Equals("fromJSON", StringComparison.OrdinalIgnoreCase) => !HostedJson(json),
        Expr.Path { Text: var p } when p.StartsWith("matrix.", StringComparison.Ordinal) => !HostedMatrix(job, p["matrix.".Length..], name, file),
        _ => true,
    };

    static bool HostedJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var values = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToList() : [doc.RootElement];
            return values.Count > 0 && values.All(v => v.ValueKind == JsonValueKind.String && Hosted().IsMatch(v.GetString()!));
        }
        catch (JsonException) { return false; }
    }

    // A matrix key is hosted when every value it can take is a literal hosted label: its list, each include: row, and
    // rows an expression builds from literals alone. Or it is ci.yml product's named key, pinned by the tests.
    static bool HostedMatrix(YamlMappingNode job, string key, string name, string file)
    {
        if (!job.Children.TryGetValue(new YamlScalarNode("strategy"), out var st) || st is not YamlMappingNode strategy
            || !strategy.Children.TryGetValue(new YamlScalarNode("matrix"), out var m) || m is not YamlMappingNode matrix) { return false; }
        var values = new List<string>();
        var include = matrix.Children.TryGetValue(new YamlScalarNode("include"), out var inc) ? inc : null;
        switch (matrix.Children.TryGetValue(new YamlScalarNode(key), out var own) ? own : null)
        {
            case YamlScalarNode { Value: var v } when Regex.Replace(v ?? "", @"\s+", " ").Trim() == ProductMatrix:
                return file == "ci.yml" && name == "product" && key == "os" && include is null;
            case YamlScalarNode { Value: var v } when v is null || v.Contains("${{", StringComparison.Ordinal): return false;
            case YamlScalarNode { Value: { } v }: values.Add(v); break;
            case YamlSequenceNode list when list.Children.All(i => i is YamlScalarNode { Value: { } x } && !x.Contains("${{", StringComparison.Ordinal)):
                values.AddRange(list.Children.Select(i => ((YamlScalarNode)i).Value!)); break;
            case null: break;
            default: return false;
        }
        switch (include)
        {
            case YamlSequenceNode rows:
                foreach (var row in rows.Children)
                {
                    if (row is not YamlMappingNode r) { return false; }
                    if (!r.Children.TryGetValue(new YamlScalarNode(key), out var cell)) { continue; }
                    if (cell is not YamlScalarNode { Value: { } c } || c.Contains("${{", StringComparison.Ordinal)) { return false; }
                    values.Add(c);
                }
                break;
            case YamlScalarNode { Value: { } built }:
                if (ContextRead().IsMatch(built)) { return false; }
                values.AddRange(Regex.Matches(built, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"([^\"]*)\"").Select(x => x.Groups[1].Value));
                break;
            case null: break;
            default: return false;
        }
        return values.Count > 0 && values.All(v => Hosted().IsMatch(v));
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
