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
/// or a runner named by something the workflow doesn't hold (a variable, an input, the event) is a violation.
/// </summary>
static partial class RunnerRouting
{
    // The whole guard, as quality/README.md gives it. The repository variable lets one setting flip the pool off.
    internal const string Guard ="vars.AB_CI_SELF_HOSTED == 'true' && (github.event_name == 'push' || github.event.pull_request.head.repo.full_name == github.repository)";
    static readonly string[] Privileged = ["pull_request_target", "workflow_run"];
    static readonly HashSet<string> Needed = [.. Conjuncts(Expr.Parse(Guard)).Select(c => c.ToString())];

    // The labels only: not "--deny-self-hosted-runners", which names an option, not a runner.
    [GeneratedRegex(@"(?<![\w-])(ab-ci|self-hosted)(?![\w-])", RegexOptions.IgnoreCase)]
    private static partial Regex Label();
    [GeneratedRegex(@"^\s*\$\{\{(.*)\}\}\s*$", RegexOptions.Singleline)]
    private static partial Regex WholeExpression();
    // GitHub-hosted labels. Any other label (windows, x64, a custom one) can match the pool's labels, so it needs the guard.
    [GeneratedRegex(@"^\s*(ubuntu-(latest|\d{2}\.\d{2}(-arm)?)|windows-(latest|\d{4}|11-arm)|macos-(latest|\d{2}(-large|-xlarge|-intel)?))\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex Hosted();
    // Sources the workflow doesn't hold: a matrix or a job output built from them may name any runner (R4zzB226 P1-1).
    [GeneratedRegex(@"(?<![\w.])(vars|inputs|env|secrets)\.|(?<![\w.])github\.event(?!\w)")]
    private static partial Regex Untrusted();
    static bool MaySelfHost(string label) => Label().IsMatch(label) || !Hosted().IsMatch(label);

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
        // The jobs whose own text names a label, or whose outputs read a source the workflow doesn't hold: a runner chosen
        // from their outputs can carry anything. A job whose outputs come from its own steps (ci.yml's plan) stays allowed.
        var labelled = jobs.Where(p => Label().IsMatch(Text(p.Value))
                || p.Value.Children.TryGetValue(new YamlScalarNode("outputs"), out var outputs) && Untrusted().IsMatch(Text(outputs)))
            .Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
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
            var strategy = job.Children.TryGetValue(new YamlScalarNode("strategy"), out var s) ? Text(s) : "";
            // A matrix value is a literal set when its strategy lists literals: nothing the workflow doesn't hold, no output.
            var literalMatrix = !Untrusted().IsMatch(strategy) && !labelled.Any(other => other != name && strategy.Contains($"needs.{other}.", StringComparison.Ordinal));
            var whys = routes.Select(r => Unguarded(r, value => MaySelfHost(value, strategy, labelled, name), literalMatrix)).OfType<string>().ToList();
            if (whys.Count == 0) { continue; }
            if (whys.FirstOrDefault(w => w.Length > 0) is { } why) { problems.Add($"{file} job {name} can run on a self-hosted runner without the guard ({Guard}) around it: {why}"); }
            foreach (var trigger in privileged) { problems.Add($"{file} job {name} can take a self-hosted runner in a workflow that a {trigger} can run"); }
        }
        return problems;
    }

    // null: never self-hosted. "": self-hosted only behind the whole guard. Otherwise why not.
    static string? Unguarded(YamlNode runsOn, Func<Expr, bool> maySelfHost, bool literalMatrix)
    {
        if (runsOn is YamlMappingNode mapping)
        {
            if (mapping.Children.ContainsKey(new YamlScalarNode("group"))) { return "a runner group"; }
            return mapping.Children.TryGetValue(new YamlScalarNode("labels"), out var labels) ? Unguarded(labels, maySelfHost, literalMatrix) : null;
        }
        if (runsOn is YamlSequenceNode sequence)
        {
            var each = sequence.Children.Select(item => Unguarded(item, maySelfHost, literalMatrix)).Where(w => w is not null).ToList();
            return each.Count == 0 ? null : each.FirstOrDefault(w => w!.Length > 0) ?? "";
        }
        var text = (runsOn as YamlScalarNode)?.Value ?? "";
        if (!text.Contains("${{", StringComparison.Ordinal)) { return MaySelfHost(text) ? $"the label {text.Trim()}" : null; }
        var whole = WholeExpression().Match(text);
        Expr expr;
        try { expr = whole.Success && !whole.Groups[1].Value.Contains("${{", StringComparison.Ordinal) ? Expr.Parse(whole.Groups[1].Value) : throw new FormatException(); }
        catch (FormatException) { return $"{text.Trim()} isn't one expression this check can read"; }
        // A label used as a condition is never the value GitHub returns, but it is no guard either: say so.
        if (Tested(expr).FirstOrDefault(NamesLabel) is { } tested) { return $"{tested} is tested, not guarded"; }
        var reachable = Values(expr, []).Where(v => maySelfHost(v.Value)).ToList();
        if (reachable.Count == 0) { return null; }
        var open = reachable.FirstOrDefault(v => !Needed.IsSubsetOf(v.Conditions));
        if (open.Value is not null) { return $"{open.Value} is reached without it"; }
        // Even behind the guard, only a literal label set may name the pool: a variable, input, event field, matrix or
        // output from them, or a built string could name any runner, so it fails closed (R4zzB226 P1-1).
        var dynamic = reachable.FirstOrDefault(v => v.Value is not Expr.Literal && !(literalMatrix && v.Value is Expr.Path { Text: var m } && m.StartsWith("matrix.", StringComparison.Ordinal))
            && !(v.Value is Expr.Call { Args: [Expr.Literal] } call && call.Name.Equals("fromJSON", StringComparison.OrdinalIgnoreCase)));
        if (dynamic.Value is not null) { return $"{dynamic.Value} isn't a literal label set, so even behind the guard it could name any runner"; }
        // The whole shape: GUARD (with any narrowing && conditions) && <the one self-hosted value> || <one hosted fallback>.
        // A second ||, a second self-hosted arm or no hosted fallback is a violation even when each piece is guarded.
        var shaped = expr is Expr.Or { Arms: [Expr.And pool, var fallback] } && reachable.Count == 1
            && ReferenceEquals(reachable[0].Value, pool.Arms[^1]) && fallback is not (Expr.Or or Expr.And) && !maySelfHost(fallback);
        return shaped ? "" : $"{text.Trim()} isn't {Guard} && <self-hosted label> || <hosted label>";
    }

    // Every value an expression can resolve to, with the conjuncts that must all hold for it.
    static IEnumerable<(Expr Value, HashSet<string> Conditions)> Values(Expr expr, HashSet<string> conditions) => expr switch
    {
        Expr.Or or => or.Arms.SelectMany(arm => Values(arm, conditions)),
        Expr.And and => Values(and.Arms[^1], [.. conditions, .. and.Arms[..^1].SelectMany(Conjuncts).Select(c => c.ToString())]),
        _ => [(expr, conditions)],
    };

    // The parts of an expression that are only tested, never returned: every && arm but the last, and what ! negates.
    static IEnumerable<Expr> Tested(Expr expr) => expr switch
    {
        Expr.Or or => or.Arms.SelectMany(Tested),
        Expr.And and => and.Arms[..^1].SelectMany(a => a is Expr.Or or Expr.And or Expr.Not ? Tested(a).Append(a) : [a]).Concat(Tested(and.Arms[^1])),
        Expr.Not not => Tested(not.Operand).Append(not.Operand),
        _ => [],
    };

    static bool NamesLabel(Expr expr) => expr switch
    {
        Expr.Literal { Value: string s } => Label().IsMatch(s),
        Expr.Call call => call.Args.Any(NamesLabel),
        _ => false,
    };

    static IEnumerable<Expr> Conjuncts(Expr expr) => expr is Expr.And and ? and.Arms.SelectMany(Conjuncts) : [expr];

    // The labels a fromJSON literal holds (a string or an array of strings), or null if it holds anything else.
    static string[]? JsonLabels(string json)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            return doc.RootElement.ValueKind switch
            {
                System.Text.Json.JsonValueKind.String => [doc.RootElement.GetString()!],
                System.Text.Json.JsonValueKind.Array when doc.RootElement.EnumerateArray().All(e => e.ValueKind == System.Text.Json.JsonValueKind.String)
                    => [.. doc.RootElement.EnumerateArray().Select(e => e.GetString()!)],
                _ => null,
            };
        }
        catch (System.Text.Json.JsonException) { return null; }
    }

    static bool MaySelfHost(Expr value, string strategy, HashSet<string> labelled, string self) => value switch
    {
        Expr.Literal { Value: string s } => MaySelfHost(s),
        Expr.Literal or Expr.Compare or Expr.Not => false,
        // fromJSON of a literal is that literal's labels; of anything else, whatever that is. Any other function (format,
        // join) can build a label from pieces, so it fails closed (R4zzB226 P2-3).
        Expr.Call { Name: var n, Args: [Expr.Literal { Value: string json }] } when n.Equals("fromJSON", StringComparison.OrdinalIgnoreCase) => JsonLabels(json) is { } labels ? labels.Any(MaySelfHost) : true,
        Expr.Call { Name: var n } call when n.Equals("fromJSON", StringComparison.OrdinalIgnoreCase) => call.Args.Any(a => MaySelfHost(a, strategy, labelled, self)),
        Expr.Call => true,
        Expr.Path { Text: var p } when p.StartsWith("matrix.", StringComparison.Ordinal) =>
            Label().IsMatch(strategy) || Untrusted().IsMatch(strategy) || labelled.Any(other => other != self && strategy.Contains($"needs.{other}.", StringComparison.Ordinal)),
        Expr.Path { Text: var p } when p.StartsWith("needs.", StringComparison.Ordinal) => labelled.Any(other => p.StartsWith($"needs.{other}.", StringComparison.Ordinal)),
        // A variable, an input, the event or anything else the workflow doesn't hold may name any runner.
        _ => true,
    };

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
