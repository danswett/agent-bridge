using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace AgentBridge.Quality;

/// <summary>
/// ci.yml's cache rules (quality/README.md, "CI: speed, runners and what gates main", warm caches). Only a push to
/// main saves a cache, so a pull request can't plant an entry a later job trusts: <c>actions/cache/save</c> must be
/// gated on exactly that, and the combined <c>actions/cache</c>, which saves on every event, isn't used. What ships
/// (the app's jobs and the publish side) restores from the package feeds, never from a cache, and build output
/// (<c>obj</c>, <c>bin</c>) is never cached. A job that can land on a reused <c>ab-ci</c> machine sets NuGet's
/// folders under <c>RUNNER_TEMP</c> (emptied for each job) and turns MSBuild node reuse off before it restores or
/// builds. It also sets the .NET tools' home there too, starts from a clean workspace (<c>git clean -ffdx</c>), caches only
/// from <c>RUNNER_TEMP</c>, and stops its build servers at the end, so nothing outlives the job.
/// </summary>
static partial class CachePolicy
{
    const string MainPush = "github.event_name == 'push' && github.ref == 'refs/heads/main'";
    static readonly HashSet<string> Ships = ["app", "app-installer", "sign", "publish", "attest", "publish-gate"];
    // DOTNET_CLI_HOME holds the .NET tools' state (global tools, first-run files) a reused machine would otherwise keep.
    static readonly string[] PerJob = ["NUGET_PACKAGES=", "NUGET_HTTP_CACHE_PATH=", "NUGET_PLUGINS_CACHE_PATH=", "DOTNET_CLI_HOME=", "MSBUILDDISABLENODEREUSE=1", "RUNNER_TEMP"];

    // The same labels RunnerRouting holds to its guard.
    [GeneratedRegex(@"(?<![\w-])(ab-ci|self-hosted)(?![\w-])", RegexOptions.IgnoreCase)]
    private static partial Regex Label();
    [GeneratedRegex(@"(^|[/\\*])(obj|bin)([/\\]|$)", RegexOptions.Multiline)]
    private static partial Regex BuildOutput();

    public static IReadOnlyList<string> Violations(string yaml)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root
            || Child(root, "jobs") is not YamlMappingNode jobs) { return []; }
        var problems = new List<string>();
        foreach (var (key, value) in jobs.Children)
        {
            if (key is not YamlScalarNode { Value: { } name } || value is not YamlMappingNode job) { continue; }
            var steps = (Child(job, "steps") as YamlSequenceNode)?.Children.OfType<YamlMappingNode>().ToList() ?? [];
            var caches = steps.Where(s => Scalar(s, "uses")?.StartsWith("actions/cache", StringComparison.Ordinal) == true).ToList();
            foreach (var cache in caches)
            {
                var action = Scalar(cache, "uses")!.Split('@')[0];
                var condition = Expression(Scalar(cache, "if"));
                if (action == "actions/cache")
                {
                    problems.Add($"ci.yml job {name} uses actions/cache, which saves on every event: restore with actions/cache/restore and save with actions/cache/save on {MainPush} only");
                }
                if (action == "actions/cache/save" && !(condition == MainPush || condition.StartsWith(MainPush + " && ", StringComparison.Ordinal)) || action == "actions/cache/save" && condition.Contains("||", StringComparison.Ordinal))
                {
                    problems.Add($"ci.yml job {name} saves a cache on more than a push to main: its if must be {MainPush}, optionally && more");
                }
                if (Ships.Contains(name))
                {
                    problems.Add($"ci.yml job {name} ships, so it restores from the package feeds, never from a cache");
                }
                var path = Child(cache, "with") is YamlMappingNode with ? Scalar(with, "path") ?? "" : "";
                if (BuildOutput().IsMatch(path))
                {
                    problems.Add($"ci.yml job {name} caches build output ({path.Trim()}): obj and bin are never cached");
                }
            }
            if (!Label().IsMatch(Text(Child(job, "runs-on")))) { continue; }
            var first = steps.FindIndex(s => caches.Contains(s) || (Scalar(s, "run") ?? "").Contains("dotnet", StringComparison.Ordinal));
            var perJob = steps.FindIndex(s => Scalar(s, "run") is { } run && PerJob.All(p => run.Contains(p, StringComparison.Ordinal)));
            if (perJob < 0 || (first >= 0 && perJob > first))
            {
                problems.Add($"ci.yml job {name} can run on a reused ab-ci machine but doesn't set {string.Join(", ", PerJob[..^1])} under RUNNER_TEMP before it restores or builds");
            }
            // A clean workspace: checkout's own clean left on, then git clean -ffdx before anything restores or builds.
            var clean = steps.FindIndex(s => (Scalar(s, "run") ?? "").Contains("git clean -ffdx", StringComparison.Ordinal));
            if (clean < 0 || (first >= 0 && clean > first) || steps.Any(s => Scalar(s, "uses")?.StartsWith("actions/checkout", StringComparison.Ordinal) == true
                && Child(s, "with") is YamlMappingNode cw && Scalar(cw, "clean") is "false"))
            {
                problems.Add($"ci.yml job {name} can run on a reused ab-ci machine, so it starts from a clean workspace: checkout's clean on, then git clean -ffdx before it restores or builds");
            }
            if (caches.Any(c => !(Child(c, "with") is YamlMappingNode w && (Scalar(w, "path") ?? "").Trim().StartsWith("${{ runner.temp }}", StringComparison.Ordinal))))
            {
                problems.Add($"ci.yml job {name} can run on a reused ab-ci machine, so it caches only from ${{{{ runner.temp }}}}, never the machine's own folders");
            }
            if (!steps.Any(s => Expression(Scalar(s, "if")) == "always()" && (Scalar(s, "run") ?? "").Contains("dotnet build-server shutdown", StringComparison.Ordinal)))
            {
                problems.Add($"ci.yml job {name} can run on a reused ab-ci machine, so its last step must stop its build servers: if: always(), dotnet build-server shutdown");
            }
        }
        return problems;
    }

    // An if's expression with its optional ${{ }} removed and its spacing normalized.
    static string Expression(string? text)
    {
        var t = Regex.Replace(text ?? "", @"\s+", " ").Trim();
        if (t.StartsWith("${{", StringComparison.Ordinal) && t.EndsWith("}}", StringComparison.Ordinal)) { t = t[3..^2].Trim(); }
        return t;
    }

    static YamlNode? Child(YamlMappingNode node, string key) => node.Children.TryGetValue(new YamlScalarNode(key), out var child) ? child : null;
    static string? Scalar(YamlMappingNode node, string key) => (Child(node, key) as YamlScalarNode)?.Value;

    static string Text(YamlNode? node) => node switch
    {
        YamlScalarNode scalar => scalar.Value ?? "",
        YamlSequenceNode sequence => string.Join(' ', sequence.Children.Select(Text)),
        YamlMappingNode mapping => string.Join(' ', mapping.Children.Select(p => $"{Text(p.Key)} {Text(p.Value)}")),
        _ => "",
    };
}
