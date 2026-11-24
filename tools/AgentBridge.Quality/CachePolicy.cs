using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace AgentBridge.Quality;

/// <summary>
/// ci.yml's cache rules (quality/README.md, "CI: speed, runners and what gates main": warm caches and "a reused machine
/// must give the same answer as a fresh one"; #2454, Quality B221/B222). Only a push to main saves a cache, so a pull
/// request can't plant an entry a later job trusts: every save (<c>actions/cache/save</c>, docker <c>cache-to</c>) is
/// gated on exactly that, and what saves on every event (<c>actions/cache</c>, <c>buildjet/cache</c>, a
/// <c>setup-*</c> action's <c>cache:</c>) isn't used. What ships restores from the package feeds, never a cache, and
/// <c>obj</c>/<c>bin</c> are never cached. Local composite actions are read and held to the same rules, except the two
/// named in <see cref="Exceptions"/>; a reusable workflow or an action this can't read fails closed. A job whose runner
/// may be anything but a known hosted label (<see cref="RunnerRouting"/>, fail closed) may land on a reused ab-ci
/// machine: before it restores or builds it sets NuGet's folders, the .NET tools' home and MSBuild node reuse off under
/// <c>RUNNER_TEMP</c> (emptied for each job) and runs <c>git clean -ffdx</c>, it caches only from there, and its last
/// step stops its build servers. Comments and steps with <c>if: false</c> don't count.
/// </summary>
static partial class CachePolicy
{
    const string MainPush = "github.event_name == 'push' && github.ref == 'refs/heads/main'";
    static readonly HashSet<string> Ships = ["app", "app-installer", "sign", "publish", "attest", "publish-gate"];
    static readonly string[] PerJob = ["NUGET_PACKAGES=", "NUGET_HTTP_CACHE_PATH=", "NUGET_PLUGINS_CACHE_PATH=", "DOTNET_CLI_HOME=", "MSBUILDDISABLENODEREUSE=1", "RUNNER_TEMP"];
    // The two documented exceptions (B221), by local action, and the one rule each may break:
    static readonly Dictionary<string, string> Exceptions = new(StringComparer.Ordinal)
    {
        // Its trivy archive is SHA-256 checked against the pinned hash before use, so a shipping job may restore it.
        ["./.github/actions/install-trivy"] = "restore",
        // Its debs are verified against live signed apt indexes before install, so it saves on always().
        ["./.github/actions/playwright-system-packages"] = "save",
    };

    [GeneratedRegex(@"(^|[/\\*])(obj|bin)([/\\]|$)", RegexOptions.Multiline)]
    private static partial Regex BuildOutput();

    public static IReadOnlyList<string> Violations(string yaml, Func<string, string?>? readAction = null)
    {
        readAction ??= _ => null;
        YamlMappingNode? root;
        try { var stream = new YamlStream(); stream.Load(new StringReader(yaml)); root = stream.Documents.Count > 0 ? stream.Documents[0].RootNode as YamlMappingNode : null; }
        catch (Exception e) when (e is YamlException or ArgumentException) { return [$"ci.yml isn't YAML this check can read: {e.Message}"]; }
        if (root is null || Child(root, "jobs") is not YamlMappingNode jobs || jobs.Children.Count == 0) { return ["ci.yml has no map of jobs, so nothing here can be checked"]; }
        var problems = new List<string>();
        foreach (var (key, value) in jobs.Children)
        {
            if (key is not YamlScalarNode { Value: { } name } || value is not YamlMappingNode job) { problems.Add($"ci.yml job {key} isn't a map"); continue; }
            if (Scalar(job, "uses") is { } called) { problems.Add($"ci.yml job {name} calls {called}, which this check can't follow"); continue; }
            var top = Steps(job).Where(s => !Never(s)).ToList();
            var steps = new List<(YamlMappingNode Step, string? From)>();
            foreach (var step in top) { Expand(step, null, readAction, name, steps, problems, 0); }
            foreach (var (step, from) in steps)
            {
                var action = (Scalar(step, "uses") ?? "").Split('@')[0];
                var with = Child(step, "with") as YamlMappingNode;
                var (save, restore) = (action is "actions/cache/save" or "buildjet/cache/save", action is "actions/cache/restore" or "buildjet/cache/restore");
                if (action is "actions/cache" or "buildjet/cache") { problems.Add($"ci.yml job {name} uses {action}, which saves on every event: restore, then save on {MainPush} only"); }
                if (action.StartsWith("actions/setup-", StringComparison.Ordinal) && with is not null && Scalar(with, "cache") is { Length: > 0 } c && c != "false")
                {
                    problems.Add($"ci.yml job {name}: {action} with cache: {c} saves on every event");
                }
                if (action == "docker/build-push-action" && with is not null) { save |= Scalar(with, "cache-to") is not null; restore |= Scalar(with, "cache-from") is not null; }
                if (save && !MainOnly(Expression(Scalar(step, "if"))) && Exception(from) != "save") { problems.Add($"ci.yml job {name} saves a cache on more than a push to main: its if must be {MainPush}, optionally && more"); }
                if ((save || restore) && Ships.Contains(name) && Exception(from) != "restore") { problems.Add($"ci.yml job {name} ships, so it restores from the package feeds, never from a cache"); }
                if (with is not null && Scalar(with, "path") is { } path && BuildOutput().IsMatch(path)) { problems.Add($"ci.yml job {name} caches build output ({path.Trim()}): obj and bin are never cached"); }
            }
            if (!RunnerRouting.MayRunSelfHosted(job, name, "ci.yml")) { continue; }
            var caches = steps.Where(s => (Scalar(s.Step, "uses") ?? "").Contains("cache", StringComparison.Ordinal)).Select(s => s.Step).ToList();
            var first = top.FindIndex(s => caches.Contains(s) || Run(s).Contains("dotnet", StringComparison.Ordinal) || (Scalar(s, "uses") ?? "").StartsWith("./", StringComparison.Ordinal));
            var perJob = top.FindIndex(s => PerJob.All(p => Run(s).Contains(p, StringComparison.Ordinal)));
            var clean = top.FindIndex(s => Run(s).Contains("git clean -ffdx", StringComparison.Ordinal));
            var ab = $"ci.yml job {name} can run on a reused ab-ci machine (its runner doesn't resolve to hosted labels only), so";
            if (perJob < 0 || (first >= 0 && perJob > first)) { problems.Add($"{ab} it sets {string.Join(", ", PerJob[..^1])} under RUNNER_TEMP before it restores or builds"); }
            if (clean < 0 || (first >= 0 && clean > first) || top.Any(s => (Scalar(s, "uses") ?? "").StartsWith("actions/checkout", StringComparison.Ordinal) && Child(s, "with") is YamlMappingNode w && Scalar(w, "clean") == "false"))
            {
                problems.Add($"{ab} it starts from a clean workspace: checkout's clean on, then git clean -ffdx before it restores or builds");
            }
            if (caches.Any(s => !(Child(s, "with") is YamlMappingNode w && (Scalar(w, "path") ?? "").Trim().StartsWith("${{ runner.temp }}", StringComparison.Ordinal)))) { problems.Add($"{ab} it caches only from ${{{{ runner.temp }}}}"); }
            if (top.Count == 0 || Expression(Scalar(top[^1], "if")) != "always()" || !Run(top[^1]).Contains("dotnet build-server shutdown", StringComparison.Ordinal))
            {
                problems.Add($"{ab} its last step stops its build servers: if: always(), dotnet build-server shutdown");
            }
        }
        return problems;
    }

    // A step, or a local composite action's steps in its place (they run in the job), each with the local action it came from.
    static void Expand(YamlMappingNode step, string? from, Func<string, string?> read, string job, List<(YamlMappingNode, string?)> into, List<string> problems, int depth)
    {
        var uses = Scalar(step, "uses") ?? "";
        if (!uses.StartsWith("./", StringComparison.Ordinal)) { into.Add((step, from)); return; }
        var path = uses.TrimEnd('/');
        YamlMappingNode? action = null;
        try { if (depth < 4 && read(path) is { } text) { var s = new YamlStream(); s.Load(new StringReader(text)); action = s.Documents.Count > 0 ? s.Documents[0].RootNode as YamlMappingNode : null; } }
        catch (Exception e) when (e is YamlException or ArgumentException) { action = null; }
        if (action is null || Child(action, "runs") is not YamlMappingNode runs) { problems.Add($"ci.yml job {job} uses {path}, which this check can't read"); return; }
        foreach (var inner in Steps(runs).Where(s => !Never(s))) { Expand(inner, from ?? path, read, job, into, problems, depth + 1); }
    }

    static string? Exception(string? from) => from is not null && Exceptions.TryGetValue(from, out var rule) ? rule : null;
    static bool MainOnly(string condition) => (condition == MainPush || condition.StartsWith(MainPush + " && ", StringComparison.Ordinal)) && !condition.Contains("||", StringComparison.Ordinal);
    static bool Never(YamlMappingNode step) => Expression(Scalar(step, "if")) == "false";
    static List<YamlMappingNode> Steps(YamlMappingNode node) => (Child(node, "steps") as YamlSequenceNode)?.Children.OfType<YamlMappingNode>().ToList() ?? [];
    // A step's script without its comment lines.
    static string Run(YamlMappingNode step) => string.Join('\n', (Scalar(step, "run") ?? "").Split('\n').Where(l => !l.TrimStart().StartsWith('#')));

    // An if's expression with its optional ${{ }} removed and its spacing normalized.
    static string Expression(string? text)
    {
        var t = Regex.Replace(text ?? "", @"\s+", " ").Trim();
        if (t.StartsWith("${{", StringComparison.Ordinal) && t.EndsWith("}}", StringComparison.Ordinal)) { t = t[3..^2].Trim(); }
        return t;
    }

    static YamlNode? Child(YamlMappingNode node, string key) => node.Children.TryGetValue(new YamlScalarNode(key), out var child) ? child : null;
    static string? Scalar(YamlMappingNode node, string key) => (Child(node, key) as YamlScalarNode)?.Value;
}
